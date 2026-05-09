using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Core.Database;

/// <summary>
/// 照片缓存数据库静态门面。复用 <see cref="SettingsPathHelper"/> 的基准目录。
/// 主键为 <see cref="PhotoFingerprint"/> 计算出的哈希，跨 RAW/JPG/HEIF 同次曝光共享同一指纹。
/// </summary>
public static class PhotoDatabase
{
    private static readonly object _initLock = new();
    private static string? _dbPath;
    private static bool _initialized;

    /// <summary>初始化数据库：确保文件存在并 schema 建好。可重复调用，幂等。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _dbPath = ResolvePath();
            using var conn = OpenConnection();
            EnsureSchema(conn);
            _initialized = true;
        }
    }

    /// <summary>数据库文件的绝对路径（初始化后可用）。主要用于诊断输出。</summary>
    public static string DatabasePath
    {
        get
        {
            Initialize();
            return _dbPath!;
        }
    }

    /// <summary>打开一个新连接。调用方负责释放。外部仅推荐用于罕见的自定义查询。</summary>
    public static SqliteConnection OpenConnection()
    {
        if (_dbPath == null)
        {
            _dbPath = ResolvePath();
        }
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>
    /// 根据指纹读取缓存记录；不存在时返回 null。
    /// </summary>
    public static async Task<PhotoCacheRecord?> GetAsync(string fingerprint)
    {
        Initialize();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT fingerprint, filename_noext, capture_time, capture_subsec,
                   rating, feature_vector, feature_model, feature_computed_at,
                   heatmap, updated_at
            FROM photos WHERE fingerprint = $fp LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return ReadRecord(reader);
    }

    /// <summary>
    /// 插入或更新一条缓存记录。仅更新提供的字段（其余保留原值）。
    /// </summary>
    public static async Task UpsertIdentityAsync(PhotoFingerprintInput input, string fingerprint)
    {
        Initialize();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec, updated_at)
            VALUES ($fp, $fn, $ct, $cs, $ts)
            ON CONFLICT(fingerprint) DO UPDATE SET
                filename_noext = excluded.filename_noext,
                capture_time   = excluded.capture_time,
                capture_subsec = excluded.capture_subsec,
                updated_at     = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$fn", input.FilenameNoExt ?? "");
        cmd.Parameters.AddWithValue("$ct", FormatTime(input.CaptureTime));
        cmd.Parameters.AddWithValue("$cs", (object?)input.CaptureSubSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", FormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 写入（或覆盖）特征向量及其模型标识。首次写入时同步把身份字段（filename_noext / capture_time / capture_subsec）
    /// 一并填好，满足 <c>filename_noext NOT NULL</c> 约束；已有行时仅更新特征相关列。
    /// </summary>
    public static async Task WriteFeatureVectorAsync(
        PhotoFingerprintInput input,
        string fingerprint,
        byte[] vector,
        string modelId)
    {
        Initialize();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec,
                                feature_vector, feature_model, feature_computed_at, updated_at)
            VALUES ($fp, $fn, $ct, $cs, $v, $m, $t, $t)
            ON CONFLICT(fingerprint) DO UPDATE SET
                feature_vector      = excluded.feature_vector,
                feature_model       = excluded.feature_model,
                feature_computed_at = excluded.feature_computed_at,
                updated_at          = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$fn", input.FilenameNoExt ?? "");
        cmd.Parameters.AddWithValue("$ct", FormatTime(input.CaptureTime));
        cmd.Parameters.AddWithValue("$cs", (object?)input.CaptureSubSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v", vector);
        cmd.Parameters.AddWithValue("$m", modelId);
        cmd.Parameters.AddWithValue("$t", FormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>返回总行数，诊断用。</summary>
    public static async Task<long> CountAsync()
    {
        Initialize();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM photos;";
        var n = await cmd.ExecuteScalarAsync();
        return n is long l ? l : Convert.ToInt64(n, CultureInfo.InvariantCulture);
    }

    private static string ResolvePath()
    {
        var settingsPath = SettingsPathHelper.GetDefaultPath("PhotoViewer");
        var dir = Path.GetDirectoryName(settingsPath)!;
        System.IO.Directory.CreateDirectory(dir);
        return Path.Combine(dir, "photos.db");
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS photos (
                fingerprint         TEXT PRIMARY KEY,
                filename_noext      TEXT NOT NULL,
                capture_time        TEXT,
                capture_subsec      TEXT,
                rating              INTEGER,
                feature_vector      BLOB,
                feature_model       TEXT,
                feature_computed_at TEXT,
                heatmap             BLOB,
                updated_at          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_photos_capture_time ON photos(capture_time);
            CREATE INDEX IF NOT EXISTS idx_photos_filename     ON photos(filename_noext);";
        cmd.ExecuteNonQuery();
    }

    private static string FormatTime(DateTime? t)
    {
        if (!t.HasValue) return "";
        var utc = t.Value.Kind == DateTimeKind.Utc
            ? t.Value
            : t.Value.Kind == DateTimeKind.Local ? t.Value.ToUniversalTime() : DateTime.SpecifyKind(t.Value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static PhotoCacheRecord ReadRecord(SqliteDataReader r)
    {
        return new PhotoCacheRecord
        {
            Fingerprint        = r.GetString(0),
            FilenameNoExt      = r.GetString(1),
            CaptureTime        = r.IsDBNull(2) ? null : r.GetString(2),
            CaptureSubSec      = r.IsDBNull(3) ? null : r.GetString(3),
            Rating             = r.IsDBNull(4) ? null : r.GetInt32(4),
            FeatureVector      = r.IsDBNull(5) ? null : (byte[])r.GetValue(5),
            FeatureModel       = r.IsDBNull(6) ? null : r.GetString(6),
            FeatureComputedAt  = r.IsDBNull(7) ? null : r.GetString(7),
            Heatmap            = r.IsDBNull(8) ? null : (byte[])r.GetValue(8),
            UpdatedAt          = r.GetString(9),
        };
    }
}

/// <summary>photos 表中的一条记录。数据库直出的 POCO，不做语义转换。</summary>
public sealed class PhotoCacheRecord
{
    public string Fingerprint { get; init; } = "";
    public string FilenameNoExt { get; init; } = "";
    public string? CaptureTime { get; init; }
    public string? CaptureSubSec { get; init; }
    public int? Rating { get; init; }
    public byte[]? FeatureVector { get; init; }
    public string? FeatureModel { get; init; }
    public string? FeatureComputedAt { get; init; }
    public byte[]? Heatmap { get; init; }
    public string UpdatedAt { get; init; } = "";
}
