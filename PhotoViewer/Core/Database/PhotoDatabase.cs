using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Core.Database;

/// <summary>
/// 照片缓存数据库静态门面。复用 <see cref="SettingsPathHelper"/> 的基准目录。
/// 主键为 <see cref="PhotoFingerprint"/> 计算出的哈希，跨 RAW/JPG/HEIF 同次曝光共享同一指纹。
///
/// Schema 由三张表组成(Plan-2-3):
/// 1. <c>photos</c> — 身份字段 + CV grid 单列(覆盖式,版本 bump 即作废)
/// 2. <c>photo_features</c> — DINO CLS 纵表,(fingerprint, model_id) 主键,多模型并存
/// 3. <c>photo_patches</c> — DINO patch token 纵表,同上主键结构
///
/// 启动时若检测到旧 schema(`feature_vector` 或 `heatmap` 列残留),直接删库重建 — 软件尚未对外发布,
/// 测试设备上的提取数据可丢弃。
/// </summary>
public static class PhotoDatabase
{
    private static readonly object _initLock = new();
    private static string? _dbPath;
    private static bool _initialized;

    /// <summary>初始化数据库:确保文件存在并 schema 建好;检测到旧 schema 则删库重建。可重复调用,幂等。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _dbPath = ResolvePath();

            if (ShouldResetDueToLegacySchema())
            {
                DeleteDbFiles();
            }

            using var conn = OpenConnectionInternal();
            EnsureSchema(conn);
            _initialized = true;
        }
    }

    /// <summary>数据库文件的绝对路径(初始化后可用)。主要用于诊断输出。</summary>
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
        Initialize();
        return OpenConnectionInternal();
    }

    /// <summary>
    /// 开发用:清空整个特征数据库(关闭连接 → 删 photos.db / -wal / -shm → 重新建空库)。
    /// AI 设置页的"清除特征数据库"按钮接通此入口。调用后 <see cref="DinoFeatureCache"/> 应同步清进程缓存。
    /// </summary>
    public static Task DeleteDatabaseAsync()
    {
        return Task.Run(() =>
        {
            lock (_initLock)
            {
                _initialized = false;
                _dbPath ??= ResolvePath();

                // SQLite 连接池里可能仍持有文件句柄(macOS / Windows 都会因此拒绝删除)
                SqliteConnection.ClearAllPools();
                DeleteDbFiles();

                using var conn = OpenConnectionInternal();
                EnsureSchema(conn);
                _initialized = true;
            }
        });
    }

    /// <summary>
    /// 根据指纹读取身份记录;不存在时返回 null。
    /// 不再随手把 DINO CLS / patch / CV grid 都拉回来,各自走专用读取方法。
    /// </summary>
    public static async Task<PhotoCacheRecord?> GetAsync(string fingerprint)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT fingerprint, filename_noext, capture_time, capture_subsec,
                   rating, cv_grid, cv_grid_spec, cv_computed_at, updated_at
            FROM photos WHERE fingerprint = $fp LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return ReadRecord(reader);
    }

    /// <summary>
    /// 仅 Upsert 身份字段(filename_noext / capture_time / capture_subsec)。
    /// 给"还没跑特征但想先占位"的极少数路径用,主调用方仍是 <see cref="WriteFeatureAsync"/> 等。
    /// </summary>
    public static async Task UpsertIdentityAsync(PhotoFingerprintInput input, string fingerprint)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec, updated_at)
            VALUES ($fp, $fn, $ct, $cs, $ts)
            ON CONFLICT(fingerprint) DO UPDATE SET
                filename_noext = excluded.filename_noext,
                capture_time   = excluded.capture_time,
                capture_subsec = excluded.capture_subsec,
                updated_at     = excluded.updated_at;";
        BindIdentity(cmd, fingerprint, input);
        cmd.Parameters.AddWithValue("$ts", FormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 写入 DINO CLS 向量(纵表)。会先 INSERT OR IGNORE 一行 photos 以满足外键。
    /// </summary>
    public static async Task WriteFeatureAsync(
        PhotoFingerprintInput input, string fingerprint, string modelId, byte[] clsBlob)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        await EnsurePhotoRowAsync(conn, tx, fingerprint, input);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photo_features (fingerprint, model_id, cls_vector, computed_at)
                VALUES ($fp, $m, $v, $t)
                ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                    cls_vector  = excluded.cls_vector,
                    computed_at = excluded.computed_at;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", clsBlob);
            cmd.Parameters.AddWithValue("$t", FormatTime(DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>读取 DINO CLS 向量(纵表),不命中返回 null。</summary>
    public static async Task<byte[]?> ReadFeatureAsync(string fingerprint, string modelId)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT cls_vector FROM photo_features
            WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$m", modelId);
        var result = await cmd.ExecuteScalarAsync();
        return result is byte[] b ? b : null;
    }

    /// <summary>写入 DINO patch token blob(纵表)。要求 photos 主行已存在(由 indexer 路径保证)。</summary>
    public static async Task WritePatchesAsync(
        PhotoFingerprintInput input, string fingerprint, string modelId, byte[] patchBlob)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        await EnsurePhotoRowAsync(conn, tx, fingerprint, input);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photo_patches (fingerprint, model_id, patch_tokens, computed_at)
                VALUES ($fp, $m, $v, $t)
                ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                    patch_tokens = excluded.patch_tokens,
                    computed_at  = excluded.computed_at;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", patchBlob);
            cmd.Parameters.AddWithValue("$t", FormatTime(DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>读取 DINO patch token blob,不命中返回 null。</summary>
    public static async Task<byte[]?> ReadPatchesAsync(string fingerprint, string modelId)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT patch_tokens FROM photo_patches
            WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$m", modelId);
        var result = await cmd.ExecuteScalarAsync();
        return result is byte[] b ? b : null;
    }

    /// <summary>写入 CV grid blob 与版本字符串(覆盖式)。同样会先确保 photos 主行存在。</summary>
    public static async Task WriteCvGridAsync(
        PhotoFingerprintInput input, string fingerprint, byte[] gridBlob, string spec)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec,
                                cv_grid, cv_grid_spec, cv_computed_at, updated_at)
            VALUES ($fp, $fn, $ct, $cs, $cv, $cs2, $t, $t)
            ON CONFLICT(fingerprint) DO UPDATE SET
                cv_grid        = excluded.cv_grid,
                cv_grid_spec   = excluded.cv_grid_spec,
                cv_computed_at = excluded.cv_computed_at,
                updated_at     = excluded.updated_at;";
        BindIdentity(cmd, fingerprint, input);
        cmd.Parameters.AddWithValue("$cv", gridBlob);
        cmd.Parameters.AddWithValue("$cs2", spec);
        cmd.Parameters.AddWithValue("$t", FormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>读取 CV grid blob 与 spec;blob 缺失或 spec 不一致由调用方判定 cache miss。</summary>
    public static async Task<(byte[] Blob, string Spec)?> ReadCvGridAsync(string fingerprint)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT cv_grid, cv_grid_spec FROM photos
            WHERE fingerprint = $fp LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;
        return ((byte[])reader.GetValue(0), reader.GetString(1));
    }

    /// <summary>
    /// indexer 一轮扫描的主写入入口:单事务同时落 photos / photo_features / photo_patches。
    /// 任一 blob 为 null 视为本次不更新该项(允许"只补 CV"或"只补 patch"的按需补齐路径)。
    /// </summary>
    public static async Task WriteIndexedAsync(
        PhotoFingerprintInput input,
        string fingerprint,
        string modelId,
        byte[]? clsBlob,
        byte[]? patchBlob,
        byte[]? cvGridBlob,
        string? cvSpec)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        var nowIso = FormatTime(DateTime.UtcNow);
        await EnsurePhotoRowAsync(conn, tx, fingerprint, input, nowIso);

        if (cvGridBlob != null && cvSpec != null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE photos
                SET cv_grid        = $cv,
                    cv_grid_spec   = $cs,
                    cv_computed_at = $t,
                    updated_at     = $t
                WHERE fingerprint = $fp;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$cv", cvGridBlob);
            cmd.Parameters.AddWithValue("$cs", cvSpec);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        if (clsBlob != null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photo_features (fingerprint, model_id, cls_vector, computed_at)
                VALUES ($fp, $m, $v, $t)
                ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                    cls_vector  = excluded.cls_vector,
                    computed_at = excluded.computed_at;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", clsBlob);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        if (patchBlob != null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photo_patches (fingerprint, model_id, patch_tokens, computed_at)
                VALUES ($fp, $m, $v, $t)
                ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                    patch_tokens = excluded.patch_tokens,
                    computed_at  = excluded.computed_at;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", patchBlob);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>
    /// 检查某指纹三路数据齐备程度,supply indexer 与相似聚类面板使用。
    /// 返回值各位表示该项缺失(true=缺失需要计算)。
    /// </summary>
    public static async Task<MissingParts> EvaluateMissingPartsAsync(string fingerprint, string modelId, string cvSpec)
    {
        Initialize();
        await using var conn = OpenConnectionInternal();

        bool needCv = true;
        bool needCls = true;
        bool needPatch = true;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT cv_grid_spec
                FROM photos WHERE fingerprint = $fp LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            var spec = await cmd.ExecuteScalarAsync();
            if (spec is string s && s == cvSpec) needCv = false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 1 FROM photo_features
                WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            if (await cmd.ExecuteScalarAsync() != null) needCls = false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 1 FROM photo_patches
                WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            if (await cmd.ExecuteScalarAsync() != null) needPatch = false;
        }

        return new MissingParts(needCls, needPatch, needCv);
    }

    /// <summary>返回 photos 主表总行数,诊断用。</summary>
    public static async Task<long> CountAsync()
    {
        Initialize();
        await using var conn = OpenConnectionInternal();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM photos;";
        var n = await cmd.ExecuteScalarAsync();
        return n is long l ? l : Convert.ToInt64(n, CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenConnectionInternal()
    {
        if (_dbPath == null) _dbPath = ResolvePath();
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private static string ResolvePath()
    {
        var settingsPath = SettingsPathHelper.GetDefaultPath("PhotoViewer");
        var dir = Path.GetDirectoryName(settingsPath)!;
        System.IO.Directory.CreateDirectory(dir);
        return Path.Combine(dir, "photos.db");
    }

    /// <summary>
    /// 检测旧 schema:photos 表若仍持有 <c>feature_vector</c> 或 <c>heatmap</c> 列,视为 Plan-2-3 前的遗留库,
    /// 直接删除让 EnsureSchema 重建。开发期用,不需要迁移用户数据。
    /// </summary>
    private static bool ShouldResetDueToLegacySchema()
    {
        if (!File.Exists(_dbPath)) return false;

        try
        {
            using var conn = OpenConnectionInternal();
            var cols = GetColumnNames(conn, "photos");
            if (cols.Count == 0) return false;
            return cols.Contains("feature_vector") || cols.Contains("heatmap");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhotoDatabase] legacy schema probe failed, will recreate: {ex.Message}");
            return true;
        }
    }

    private static HashSet<string> GetColumnNames(SqliteConnection conn, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1)) result.Add(reader.GetString(1));
        }
        return result;
    }

    private static void DeleteDbFiles()
    {
        if (_dbPath == null) return;
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhotoDatabase] failed to delete {path}: {ex.Message}");
            }
        }
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS photos (
                fingerprint    TEXT PRIMARY KEY,
                filename_noext TEXT NOT NULL,
                capture_time   TEXT,
                capture_subsec TEXT,
                rating         INTEGER,
                cv_grid        BLOB,
                cv_grid_spec   TEXT,
                cv_computed_at TEXT,
                updated_at     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_photos_capture_time ON photos(capture_time);
            CREATE INDEX IF NOT EXISTS idx_photos_filename     ON photos(filename_noext);

            CREATE TABLE IF NOT EXISTS photo_features (
                fingerprint TEXT NOT NULL,
                model_id    TEXT NOT NULL,
                cls_vector  BLOB NOT NULL,
                computed_at TEXT NOT NULL,
                PRIMARY KEY (fingerprint, model_id),
                FOREIGN KEY (fingerprint) REFERENCES photos(fingerprint) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_features_model ON photo_features(model_id);

            CREATE TABLE IF NOT EXISTS photo_patches (
                fingerprint  TEXT NOT NULL,
                model_id     TEXT NOT NULL,
                patch_tokens BLOB NOT NULL,
                computed_at  TEXT NOT NULL,
                PRIMARY KEY (fingerprint, model_id),
                FOREIGN KEY (fingerprint) REFERENCES photos(fingerprint) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_patches_model ON photo_patches(model_id);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>事务内确保 photos 主行存在:不存在则插入(填身份字段 + 时间戳),存在则不动。</summary>
    private static async Task EnsurePhotoRowAsync(
        SqliteConnection conn, SqliteTransaction tx, string fingerprint, PhotoFingerprintInput input, string? nowIso = null)
    {
        nowIso ??= FormatTime(DateTime.UtcNow);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec, updated_at)
            VALUES ($fp, $fn, $ct, $cs, $t)
            ON CONFLICT(fingerprint) DO UPDATE SET
                filename_noext = excluded.filename_noext,
                capture_time   = excluded.capture_time,
                capture_subsec = excluded.capture_subsec,
                updated_at     = excluded.updated_at;";
        BindIdentity(cmd, fingerprint, input);
        cmd.Parameters.AddWithValue("$t", nowIso);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void BindIdentity(SqliteCommand cmd, string fingerprint, PhotoFingerprintInput input)
    {
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$fn", input.FilenameNoExt ?? "");
        cmd.Parameters.AddWithValue("$ct", FormatTime(input.CaptureTime));
        cmd.Parameters.AddWithValue("$cs", (object?)input.CaptureSubSec ?? DBNull.Value);
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
            Fingerprint   = r.GetString(0),
            FilenameNoExt = r.GetString(1),
            CaptureTime   = r.IsDBNull(2) ? null : r.GetString(2),
            CaptureSubSec = r.IsDBNull(3) ? null : r.GetString(3),
            Rating        = r.IsDBNull(4) ? null : r.GetInt32(4),
            CvGrid        = r.IsDBNull(5) ? null : (byte[])r.GetValue(5),
            CvGridSpec    = r.IsDBNull(6) ? null : r.GetString(6),
            CvComputedAt  = r.IsDBNull(7) ? null : r.GetString(7),
            UpdatedAt     = r.GetString(8),
        };
    }
}

/// <summary>photos 表的身份 + CV 列直出 POCO。DINO CLS / patch 不在此结构里,各自走纵表读取方法。</summary>
public sealed class PhotoCacheRecord
{
    public string Fingerprint { get; init; } = "";
    public string FilenameNoExt { get; init; } = "";
    public string? CaptureTime { get; init; }
    public string? CaptureSubSec { get; init; }
    public int? Rating { get; init; }
    public byte[]? CvGrid { get; init; }
    public string? CvGridSpec { get; init; }
    public string? CvComputedAt { get; init; }
    public string UpdatedAt { get; init; } = "";
}

/// <summary>三路数据齐备评估结果。任一字段为 true 表示该项缺失,需要 indexer 补齐。</summary>
public readonly record struct MissingParts(bool NeedCls, bool NeedPatches, bool NeedCv)
{
    /// <summary>任一缺失即视为"需要处理该指纹"。</summary>
    public bool AnyMissing => NeedCls || NeedPatches || NeedCv;
}
