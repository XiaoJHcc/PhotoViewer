using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DatasetBuilder;

/// <summary>
/// 入库前的轻量分布探查（--scan-only）：只做扫描 + 指纹聚合 + 读 EXIF，
/// 输出文件/指纹组计数、格式构成、焦段分布、星级分布、长焦段星级交叉、拍摄时间跨度与粗切段数。
/// 不解码位图、不提特征、不建库 —— 用于挑样本 / 核验批次（Plan-3-1 §1.1 / §1.3）。
/// </summary>
public static class ScanReport
{
    /// <summary>焦段分桶（按 35mm 等效焦距，mm）。</summary>
    private static readonly (string Label, double Min, double Max)[] FocalBuckets =
    {
        ("超广角 <20", 0, 20),
        ("广角 20-35", 20, 35),
        ("标准 35-70", 35, 70),
        ("中长 70-135", 70, 135),
        ("长焦 135-300", 135, 300),
        ("超长焦 300-600", 300, 600),
        ("极超长 ≥600", 600, double.MaxValue),
    };

    /// <summary>打印分布报告到控制台。</summary>
    /// <param name="groups">指纹组列表（扫描聚合结果）。</param>
    public static void Print(IReadOnlyList<FpGroup> groups)
    {
        int fileCount = groups.Sum(g => g.Files.Count);
        Console.WriteLine();
        Console.WriteLine("════════ 扫描分布报告（--scan-only · 未提特征）════════");
        Console.WriteLine($"文件 {fileCount} · 指纹组 {groups.Count}");
        if (groups.Count == 0) return;

        // 格式构成（按文件）
        Console.WriteLine("\n— 格式（按文件）—");
        foreach (var g in groups.SelectMany(x => x.Files)
                     .GroupBy(f => Path.GetExtension(f.Path).TrimStart('.').ToLowerInvariant())
                     .OrderByDescending(x => x.Count()))
            Console.WriteLine($"  {g.Key,-6} {g.Count()}");

        // 焦段分布（按指纹组，代表文件 35mm 等效焦距）
        Console.WriteLine("\n— 焦段（35mm 等效 · 按指纹组）—");
        var focals = groups.Select(g => EquivFocal(g.Representative.Exif)).ToList();
        foreach (var (label, min, max) in FocalBuckets)
        {
            int n = focals.Count(f => f is double v && v >= min && v < max);
            if (n > 0) Console.WriteLine($"  {label,-14} {Bar(n, groups.Count)} {n}");
        }
        int noFocal = focals.Count(f => f == null);
        if (noFocal > 0) Console.WriteLine($"  {"无焦距",-14} {Bar(noFocal, groups.Count)} {noFocal}");

        // 星级分布（按指纹组）
        Console.WriteLine("\n— 星级（按指纹组）—");
        for (int star = 0; star <= 5; star++)
        {
            int n = groups.Count(g => g.Rating == star);
            Console.WriteLine($"  {star}★ {Bar(n, groups.Count)} {n}");
        }

        // 长焦段星级交叉：超长焦低对比片有没有被评过星、能否配对（探针 A/B 的关键前提）
        Console.WriteLine("\n— 长焦段(≥135mm 等效)星级分布 —");
        var tele = groups.Where(g => EquivFocal(g.Representative.Exif) is double v && v >= 135).ToList();
        Console.WriteLine($"  长焦组 {tele.Count} / 全部 {groups.Count}");
        for (int star = 0; star <= 5; star++)
        {
            int n = tele.Count(g => g.Rating == star);
            if (n > 0) Console.WriteLine($"    {star}★ {n}");
        }

        // 拍摄时间跨度 + 粗切段（>30 分钟间隔切一段）
        Console.WriteLine("\n— 拍摄时间 —");
        var times = groups.Select(g => g.Representative.Exif.CaptureTime)
            .Where(t => t != null).Select(t => t!.Value).OrderBy(t => t).ToList();
        if (times.Count == 0)
        {
            Console.WriteLine("  无 DateTimeOriginal（指纹已回落文件写入时间）");
        }
        else
        {
            Console.WriteLine($"  跨度 {times[0]:yyyy-MM-dd HH:mm} → {times[^1]:yyyy-MM-dd HH:mm}"
                + $"（{(times[^1] - times[0]).TotalHours:0.0} 小时 · 有时间 {times.Count}/{groups.Count}）");
            int segs = 1;
            for (int i = 1; i < times.Count; i++)
                if ((times[i] - times[i - 1]).TotalMinutes > 30) segs++;
            Console.WriteLine($"  按 >30 分钟间隔粗切 ≈ {segs} 段");
        }
        Console.WriteLine("═══════════════════════════════════════════════════");
    }

    /// <summary>35mm 等效焦距：优先等效值，回落实焦，皆无则 null。</summary>
    private static double? EquivFocal(PhotoExif e) =>
        e.EquivFocalLength is > 0 ? e.EquivFocalLength : (e.FocalLength is > 0 ? e.FocalLength : null);

    /// <summary>定宽 ASCII 条形（占位到 30 格便于对齐）。</summary>
    private static string Bar(int n, int total)
    {
        if (total == 0) return new string(' ', 30);
        int len = (int)Math.Round(n / (double)total * 30);
        return new string('#', len).PadRight(30);
    }
}
