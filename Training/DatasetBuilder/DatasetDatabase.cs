using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoViewer.Core.Database;

namespace DatasetBuilder;

/// <summary>
/// 独立训练数据集库（SQLite）—— 与产品 photos.db 完全解耦的可扩展"超集"库。
///
/// 与 <see cref="PhotoDatabase"/> 的区别（有意为之，见 Plan-3-1 M1）：
/// 1. 路径由清单指定，不落 AppData，便携、"入库后不再碰原始文件"；
/// 2. photos 表在产品列之外**加训练专用列**：is_retouched / source_rel_path / event_label / subject_label / formats；
/// 3. photo_features 每指纹存**两行** —— 原片 CLS（model_id）+ 增强 CLS（model_id+clhe 后缀），支撑探针"仅原片/仅增强/多视图"对比；
/// 4. **无产品的旧-schema 删库逻辑** —— 数据集是精选产物，迁移只做加列（additive）；
/// 5. dataset_meta 表记录模型/增强参数/分辨率契约，保证可复现。
///
/// 表/列名与产品对齐，Python 侧按 photos.db 写法可直接读，额外列只是"多出来"。
/// </summary>
public sealed class DatasetDatabase
{
    private readonly string _dbPath;

    /// <summary>产品沿用列之外的训练专用列，schema 演进时在此追加（additive 迁移）。</summary>
    private static readonly (string Name, string Type)[] ExtraPhotoColumns =
    [
        ("is_retouched", "INTEGER"),
        ("source_rel_path", "TEXT"),
        ("event_label", "TEXT"),
        ("subject_label", "TEXT"),
        ("formats", "TEXT"),
    ];

    /// <summary>用给定数据集库路径构造门面（不立即建库，需调用 <see cref="Initialize"/>）。</summary>
    /// <param name="dbPath">数据集库文件绝对路径。</param>
    public DatasetDatabase(string dbPath) => _dbPath = dbPath;

    /// <summary>数据集库文件绝对路径。</summary>
    public string DatabasePath => _dbPath;

    /// <summary>确保库文件与 schema 存在；已存在的旧库只做加列迁移，绝不删库。可重复调用。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var conn = OpenConnection();
        EnsureSchema(conn);
        MigrateAddMissingColumns(conn);
    }

    /// <summary>打开一个新连接（WAL + 外键）。调用方负责释放；诊断查询（CoverageReport）也走此入口。</summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>写入/更新 dataset_meta 键值（可复现契约：模型 id、增强参数、分辨率策略等）。</summary>
    /// <param name="meta">键值对集合。</param>
    public async Task WriteMetaAsync(IReadOnlyDictionary<string, string> meta)
    {
        await using var conn = OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        foreach (var (k, v) in meta)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO dataset_meta (key, value, updated_at) VALUES ($k, $v, $t)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;";
            cmd.Parameters.AddWithValue("$k", k);
            cmd.Parameters.AddWithValue("$v", v);
            cmd.Parameters.AddWithValue("$t", FormatTime(DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>
    /// 评估某指纹四路数据齐备情况：原片 CLS / 增强 CLS / patch / cv grid。
    /// 增强路仅在 <paramref name="enhancedModelId"/> 非空时评估，否则该位恒为 false（不视为缺失）。
    /// <paramref name="includePatch"/> 为 false 时 patch 位恒 false（梯级实验只提双路 CLS）。
    /// </summary>
    public async Task<DatasetMissing> EvaluateMissingAsync(
        string fingerprint, string modelId, string? enhancedModelId, string cvSpec, bool includePatch = true)
    {
        await using var conn = OpenConnection();

        bool needCv = true, needOrigCls = true, needEnhCls = enhancedModelId != null, needPatch = includePatch;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT cv_grid_spec FROM photos WHERE fingerprint = $fp LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            if (await cmd.ExecuteScalarAsync() is string s && s == cvSpec) needCv = false;
        }
        needOrigCls = !await FeatureExistsAsync(conn, fingerprint, modelId);
        needPatch = includePatch && !await PatchExistsAsync(conn, fingerprint, modelId);
        if (enhancedModelId != null)
            needEnhCls = !await FeatureExistsAsync(conn, fingerprint, enhancedModelId);

        return new DatasetMissing(needOrigCls, needEnhCls, needPatch, needCv);
    }

    /// <summary>
    /// 单事务写入一个指纹的全部产物：photos（身份 + EXIF/rating + 来源列）+ photo_features（原片行 + 可选增强行）
    /// + photo_patches（原片）+ cv grid。任一 blob 为 null 视为本次不更新该项（支持按需补齐）。
    /// </summary>
    /// <param name="input">指纹输入（身份字段）。</param>
    /// <param name="fingerprint">指纹。</param>
    /// <param name="modelId">原片 CLS 的 model_id。</param>
    /// <param name="enhancedModelId">增强 CLS 的 model_id；null 表示不写增强行。</param>
    /// <param name="origClsBlob">原片 CLS blob。</param>
    /// <param name="enhClsBlob">增强 CLS blob。</param>
    /// <param name="patchBlob">原片 patch token blob。</param>
    /// <param name="cvGridBlob">CV grid blob。</param>
    /// <param name="cvSpec">CV grid 版本。</param>
    /// <param name="cvImageWidth">CV 解码原图宽。</param>
    /// <param name="cvImageHeight">CV 解码原图高。</param>
    /// <param name="exif">EXIF/rating 快照（任一字段 null 走 COALESCE 保留旧值）。</param>
    /// <param name="source">来源标签快照（相对路径/事件/题材/格式集合/精修标记）。</param>
    public async Task WriteIndexedAsync(
        PhotoFingerprintInput input, string fingerprint, string modelId, string? enhancedModelId,
        byte[]? origClsBlob, byte[]? enhClsBlob, byte[]? patchBlob,
        byte[]? cvGridBlob, string? cvSpec, int cvImageWidth, int cvImageHeight,
        ExifSnapshot? exif, PhotoSourceInfo source)
    {
        await using var conn = OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        var nowIso = FormatTime(DateTime.UtcNow);

        // 1) 身份 + 来源列（来源标签来自清单，每次覆盖刷新；is_retouched 走 COALESCE 不误抹）
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photos (fingerprint, filename_noext, capture_time, capture_subsec,
                                    source_rel_path, event_label, subject_label, formats, is_retouched, updated_at)
                VALUES ($fp, $fn, $ct, $cs, $srp, $el, $sl, $fmt, $ir, $t)
                ON CONFLICT(fingerprint) DO UPDATE SET
                    filename_noext  = excluded.filename_noext,
                    capture_time    = excluded.capture_time,
                    capture_subsec  = excluded.capture_subsec,
                    source_rel_path = excluded.source_rel_path,
                    event_label     = excluded.event_label,
                    subject_label   = excluded.subject_label,
                    formats         = excluded.formats,
                    is_retouched    = COALESCE(excluded.is_retouched, photos.is_retouched),
                    updated_at      = excluded.updated_at;";
            BindIdentity(cmd, fingerprint, input);
            cmd.Parameters.AddWithValue("$srp", (object?)source.SourceRelPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$el", (object?)source.EventLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sl", (object?)source.SubjectLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fmt", (object?)source.Formats ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ir", (object?)source.IsRetouched ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2) CV grid
        if (cvGridBlob != null && cvSpec != null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE photos SET cv_grid=$cv, cv_grid_spec=$cs, cv_computed_at=$t,
                                  cv_image_width=$cw, cv_image_height=$ch, updated_at=$t
                WHERE fingerprint=$fp;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$cv", cvGridBlob);
            cmd.Parameters.AddWithValue("$cs", cvSpec);
            cmd.Parameters.AddWithValue("$cw", cvImageWidth);
            cmd.Parameters.AddWithValue("$ch", cvImageHeight);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3) EXIF + rating（COALESCE 保留旧值）
        if (exif.HasValue)
        {
            var snap = exif.Value;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE photos SET
                    focal_length  = COALESCE($fl, focal_length),
                    aperture      = COALESCE($ap, aperture),
                    shutter_speed = COALESCE($ss, shutter_speed),
                    crop_factor   = COALESCE($cf, crop_factor),
                    rating        = COALESCE($rt, rating),
                    updated_at    = $t
                WHERE fingerprint = $fp;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$fl", (object?)snap.FocalLength ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ap", (object?)snap.Aperture ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ss", (object?)snap.ShutterSpeed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cf", (object?)snap.CropFactor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rt", (object?)snap.Rating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        // 4) 原片 + 增强 CLS（两行，model_id 区分）
        if (origClsBlob != null)
            await UpsertFeatureAsync(conn, tx, fingerprint, modelId, origClsBlob, nowIso);
        if (enhClsBlob != null && enhancedModelId != null)
            await UpsertFeatureAsync(conn, tx, fingerprint, enhancedModelId, enhClsBlob, nowIso);

        // 5) patch（仅原片一行）
        if (patchBlob != null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO photo_patches (fingerprint, model_id, patch_tokens, computed_at)
                VALUES ($fp, $m, $v, $t)
                ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                    patch_tokens = excluded.patch_tokens, computed_at = excluded.computed_at;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", patchBlob);
            cmd.Parameters.AddWithValue("$t", nowIso);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    // ─── 内部辅助 ───

    private static async Task UpsertFeatureAsync(
        SqliteConnection conn, SqliteTransaction tx, string fingerprint, string modelId, byte[] blob, string nowIso)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO photo_features (fingerprint, model_id, cls_vector, computed_at)
            VALUES ($fp, $m, $v, $t)
            ON CONFLICT(fingerprint, model_id) DO UPDATE SET
                cls_vector = excluded.cls_vector, computed_at = excluded.computed_at;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$m", modelId);
        cmd.Parameters.AddWithValue("$v", blob);
        cmd.Parameters.AddWithValue("$t", nowIso);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> FeatureExistsAsync(SqliteConnection conn, string fingerprint, string modelId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM photo_features WHERE fingerprint=$fp AND model_id=$m LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$m", modelId);
        return await cmd.ExecuteScalarAsync() != null;
    }

    private static async Task<bool> PatchExistsAsync(SqliteConnection conn, string fingerprint, string modelId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM photo_patches WHERE fingerprint=$fp AND model_id=$m LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$m", modelId);
        return await cmd.ExecuteScalarAsync() != null;
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
        var utc = t.Value.Kind == DateTimeKind.Utc ? t.Value
            : t.Value.Kind == DateTimeKind.Local ? t.Value.ToUniversalTime()
            : DateTime.SpecifyKind(t.Value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS photos (
                fingerprint     TEXT PRIMARY KEY,
                filename_noext  TEXT NOT NULL,
                capture_time    TEXT,
                capture_subsec  TEXT,
                rating          INTEGER,
                focal_length    REAL,
                aperture        REAL,
                shutter_speed   REAL,
                crop_factor     REAL,
                cv_grid         BLOB,
                cv_grid_spec    TEXT,
                cv_computed_at  TEXT,
                cv_image_width  INTEGER,
                cv_image_height INTEGER,
                is_retouched    INTEGER,
                source_rel_path TEXT,
                event_label     TEXT,
                subject_label   TEXT,
                formats         TEXT,
                updated_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_photos_capture_time ON photos(capture_time);
            CREATE INDEX IF NOT EXISTS idx_photos_event        ON photos(event_label);

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

            CREATE TABLE IF NOT EXISTS dataset_meta (
                key        TEXT PRIMARY KEY,
                value      TEXT,
                updated_at TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>对已存在的旧数据集库做加列迁移（additive）；产品的删库重建逻辑此处一律不做。</summary>
    private static void MigrateAddMissingColumns(SqliteConnection conn)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(photos);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(1)) cols.Add(reader.GetString(1));
        }
        foreach (var (name, type) in ExtraPhotoColumns)
        {
            if (cols.Contains(name)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE photos ADD COLUMN {name} {type};";
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>四路数据齐备评估结果。任一字段为 true 表示该项缺失、需要计算。</summary>
public readonly record struct DatasetMissing(bool NeedOrigCls, bool NeedEnhCls, bool NeedPatch, bool NeedCv)
{
    /// <summary>任一缺失即视为"需要处理该指纹"。</summary>
    public bool AnyMissing => NeedOrigCls || NeedEnhCls || NeedPatch || NeedCv;

    /// <summary>是否需要跑 DINO（原片 CLS / 增强 CLS / patch 任一缺）。</summary>
    public bool NeedDino => NeedOrigCls || NeedEnhCls || NeedPatch;
}

/// <summary>指纹组的来源标签快照（来自清单 + 聚合结果），写进 photos 训练专用列。</summary>
public readonly record struct PhotoSourceInfo(
    string? SourceRelPath, string? EventLabel, string? SubjectLabel, string? Formats, int? IsRetouched);
