using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DatasetBuilder;

/// <summary>
/// 入库完整性覆盖率报告 + GATE 判定（Plan-3-1 §1.3）。控制台摘要 + `&lt;db&gt;.coverage.md`。
/// 只做"入了什么、齐不齐"的核验；分布/阈值校准（§1.4 data_audit）与线性探针（§1.2）是消费本库的独立 Python 工具。
/// GATE 齐备度分母为**可解码组数**（仅 RAW 组无法提特征，属合法缺失，单列不计失败）。
/// </summary>
public static class CoverageReport
{
    /// <summary>生成报告并落盘，返回 GATE 是否通过。</summary>
    /// <param name="db">数据集库。</param>
    /// <param name="groups">本次扫描到的指纹组（提供结构性统计）。</param>
    /// <param name="modelId">原片 CLS model_id。</param>
    /// <param name="enhancedModelId">增强 CLS model_id；null 表示未启用增强。</param>
    /// <param name="cvSpec">CV grid 版本。</param>
    /// <param name="ingested">本次实际写入组数。</param>
    /// <param name="skipped">本次跳过（已齐备）组数。</param>
    /// <param name="failed">本次失败组数。</param>
    /// <param name="failures">失败信息样本（前若干条）。</param>
    /// <param name="elapsed">总耗时。</param>
    /// <param name="noPatch">本次跳过 patch 提取，GATE 不核验 patch 覆盖。</param>
    public static async Task<bool> GenerateAsync(
        DatasetDatabase db, IReadOnlyList<FpGroup> groups,
        string modelId, string? enhancedModelId, string cvSpec,
        int ingested, int skipped, int failed, IReadOnlyList<string> failures, TimeSpan elapsed,
        bool noPatch = false)
    {
        int totalGroups = groups.Count;
        int totalFiles = groups.Sum(g => g.Files.Count);
        int decodable = groups.Count(g => PhotoDecode.CanDecode(g.Representative.Path));
        int rawOnly = totalGroups - decodable;

        // ── DB 侧实测计数 ──
        await using var conn = db.OpenConnection();
        long dbPhotos = await ScalarAsync(conn, "SELECT COUNT(*) FROM photos;");
        long dbOrigCls = await ScalarAsync(conn, "SELECT COUNT(*) FROM photo_features WHERE model_id=$m;", ("$m", modelId));
        long dbEnhCls = enhancedModelId == null ? 0
            : await ScalarAsync(conn, "SELECT COUNT(*) FROM photo_features WHERE model_id=$m;", ("$m", enhancedModelId));
        long dbPatches = await ScalarAsync(conn, "SELECT COUNT(*) FROM photo_patches WHERE model_id=$m;", ("$m", modelId));
        long dbCv = await ScalarAsync(conn, "SELECT COUNT(*) FROM photos WHERE cv_grid_spec=$s;", ("$s", cvSpec));
        long dbRating = await ScalarAsync(conn, "SELECT COUNT(*) FROM photos WHERE rating IS NOT NULL;");
        long dbFocal = await ScalarAsync(conn, "SELECT COUNT(*) FROM photos WHERE focal_length IS NOT NULL;");
        long dbRetouched = await ScalarAsync(conn, "SELECT COUNT(*) FROM photos WHERE is_retouched=1;");

        var ratingHist = new long[6];
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT rating, COUNT(*) FROM photos WHERE rating IS NOT NULL GROUP BY rating;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                int star = r.GetInt32(0);
                if (star is >= 0 and <= 5) ratingHist[star] = r.GetInt64(1);
            }
        }

        // ── GATE ──
        bool origOk = dbOrigCls >= decodable;
        bool patchOk = noPatch || dbPatches >= decodable;
        bool cvOk = dbCv >= decodable;
        bool enhOk = enhancedModelId == null || dbEnhCls >= decodable;
        bool photosOk = dbPhotos >= totalGroups;
        bool gatePass = failed == 0 && origOk && patchOk && cvOk && enhOk && photosOk;

        // ── 每文件夹结构统计（按 event_label 或路径归组，representative 所属文件夹）──
        var byFolder = new Dictionary<string, (int files, int grps, int multiFmt)>();
        foreach (var g in groups)
        {
            var key = g.Representative.Folder.EventLabel ?? g.Representative.Folder.Path;
            byFolder.TryGetValue(key, out var acc);
            acc.files += g.Files.Count;
            acc.grps += 1;
            if (g.Formats.Contains('|')) acc.multiFmt += 1;
            byFolder[key] = acc;
        }

        // ── Markdown ──
        var sb = new StringBuilder();
        sb.AppendLine("# 数据集入库覆盖率报告");
        sb.AppendLine();
        sb.AppendLine($"- 库: `{db.DatabasePath}`");
        sb.AppendLine($"- 生成: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 耗时 {elapsed:hh\\:mm\\:ss}");
        sb.AppendLine($"- 原片 model_id: `{modelId}`");
        sb.AppendLine($"- 增强 model_id: {(enhancedModelId == null ? "（未启用）" : $"`{enhancedModelId}`")}");
        sb.AppendLine($"- CV spec: `{cvSpec}`");
        sb.AppendLine();
        sb.AppendLine("## 本次运行");
        sb.AppendLine($"- 扫描: {totalFiles} 文件 → {totalGroups} 指纹组（可解码 {decodable}，仅 RAW {rawOnly}）");
        sb.AppendLine($"- 结果: 入库 {ingested}，跳过 {skipped}，失败 {failed}");
        sb.AppendLine();
        sb.AppendLine("## 库内计数（累计）");
        sb.AppendLine($"| 项 | 计数 | 期望(可解码组) | 齐备 |");
        sb.AppendLine($"|---|---|---|---|");
        sb.AppendLine($"| photos 身份行 | {dbPhotos} | {totalGroups} | {Mark(photosOk)} |");
        sb.AppendLine($"| 原片 CLS | {dbOrigCls} | {decodable} | {Mark(origOk)} |");
        sb.AppendLine($"| 增强 CLS | {dbEnhCls} | {(enhancedModelId == null ? "—" : decodable.ToString())} | {(enhancedModelId == null ? "—" : Mark(enhOk))} |");
        sb.AppendLine($"| patch token | {dbPatches} | {(noPatch ? "—" : decodable.ToString())} | {(noPatch ? "—（--no-patch 跳过）" : Mark(patchOk))} |");
        sb.AppendLine($"| CV grid | {dbCv} | {decodable} | {Mark(cvOk)} |");
        sb.AppendLine();
        sb.AppendLine("## 标签 / EXIF 覆盖");
        sb.AppendLine($"- rating 非空: {dbRating}/{dbPhotos}（{Pct(dbRating, dbPhotos)}）");
        sb.AppendLine($"- focal_length 非空: {dbFocal}/{dbPhotos}（{Pct(dbFocal, dbPhotos)}）");
        sb.AppendLine($"- is_retouched=1: {dbRetouched}");
        sb.AppendLine($"- 星级分布: " + string.Join(" · ", Enumerable.Range(0, 6).Select(i => $"{i}★={ratingHist[i]}")));
        sb.AppendLine();
        sb.AppendLine("## 每文件夹");
        sb.AppendLine("| 文件夹 | 文件 | 指纹组 | 多格式组(合一) |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (k, v) in byFolder.OrderBy(kv => kv.Key))
            sb.AppendLine($"| {k} | {v.files} | {v.grps} | {v.multiFmt} |");
        sb.AppendLine();
        if (failures.Count > 0)
        {
            sb.AppendLine("## 失败样本");
            foreach (var f in failures.Take(20)) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }
        sb.AppendLine($"## GATE: {(gatePass ? "✅ PASS" : "❌ FAIL")}");
        if (!gatePass)
        {
            if (failed > 0) sb.AppendLine($"- 有 {failed} 组失败");
            if (!photosOk) sb.AppendLine("- photos 身份行数少于扫描组数");
            if (!origOk) sb.AppendLine("- 原片 CLS 未覆盖全部可解码组");
            if (!enhOk) sb.AppendLine("- 增强 CLS 未覆盖全部可解码组");
            if (!patchOk) sb.AppendLine("- patch token 未覆盖全部可解码组");
            if (!cvOk) sb.AppendLine("- CV grid 未覆盖全部可解码组");
        }

        var reportPath = db.DatabasePath + ".coverage.md";
        await File.WriteAllTextAsync(reportPath, sb.ToString());

        // ── 控制台摘要 ──
        Console.WriteLine();
        Console.WriteLine($"入库 {ingested} · 跳过 {skipped} · 失败 {failed} · 组 {totalGroups}(可解码 {decodable}/仅RAW {rawOnly})");
        Console.WriteLine($"库计数: photos={dbPhotos} 原片CLS={dbOrigCls} 增强CLS={dbEnhCls} patch={dbPatches} cv={dbCv}");
        Console.WriteLine($"rating 覆盖 {dbRating}/{dbPhotos} · 报告: {reportPath}");
        Console.WriteLine($"GATE: {(gatePass ? "PASS" : "FAIL")}");
        return gatePass;
    }

    private static string Mark(bool ok) => ok ? "✅" : "❌";

    private static string Pct(long n, long d) => d == 0 ? "—" : $"{100.0 * n / d:0.0}%";

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        var o = await cmd.ExecuteScalarAsync();
        return o is long l ? l : Convert.ToInt64(o);
    }
}
