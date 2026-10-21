using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using PhotoViewer.Windows.Core.Heif;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 验收专用(非产品行为,测试钩子):<c>--pv-heif-thumb-probe=&lt;目录&gt;...</c> 逐文件对比
/// "容器缩略图项"与"主图缩放"两条路径在同一请求下的产物,写纯文本报告并退出。
/// </summary>
/// <remarks>
/// 胶片栏走 <see cref="LibHeifDecoder"/> 时,小尺寸请求改由容器 <c>thmb</c> 项承接(约 5 ms,
/// 主图是 33MP 六瓦片并行)。本探针回答两个问题:
/// <list type="bullet">
/// <item>几何:两条路径的输出尺寸必须完全相同,方向必须一致(旋转/镜像不一致会表现为整幅大差异);</item>
/// <item>内容:相机缩略图与主图缩放不可能逐像素相同,但同一次拍摄应当只差压缩与重采样;
/// 用 ±2 像素对齐搜索后的平均绝对误差给出分布,尺寸/方向不一致或对齐后仍然差异巨大都算失败。</item>
/// </list>
/// 另测 560 短边(DINO 特征输入)请求:该档在两套解码器下都必须走主图,产物必须逐字节相同,
/// 以此证明阈值没有误伤模型输入语义。
/// </remarks>
internal static class WindowsHeifThumbProbe
{
    /// <summary>胶片栏列表档短边(<c>ImageFile.ListThumbnailShortSide</c> 经 ThumbnailService 传下去的值)。</summary>
    private const int FilmstripRequest = 120;

    /// <summary>大缩略图档请求:目标短边 267 > 320×212 项的短边,必须落到 1616×1080 项。</summary>
    private const int LargeRequest = 400;

    /// <summary>DINO 特征输入短边(必须保持主图语义)。</summary>
    private const int DinoRequest = 560;

    /// <summary>对齐搜索半径(像素):缩略图项 ispe 与主图画布的比例差会造成亚像素位移。</summary>
    private const int AlignmentRadius = 2;

    /// <summary>对齐后平均绝对误差的失败阈值(0–255 通道口径)。</summary>
    private const double MeanAbsDiffFailThreshold = 6.0;

    /// <summary>
    /// 解析 <c>--pv-heif-thumb-probe=&lt;目录&gt;</c>;可出现多次。未指定返回空列表。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>素材路径(目录或文件,原样)。</returns>
    internal static string[] ParsePaths(IEnumerable<string> args)
    {
        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--pv-heif-thumb-probe=", StringComparison.Ordinal)) continue;
            var value = arg["--pv-heif-thumb-probe=".Length..];
            if (!string.IsNullOrWhiteSpace(value)) paths.Add(value);
        }

        return paths.ToArray();
    }

    /// <summary>跑完整对比并写报告(后台线程,不使用窗口)。</summary>
    /// <param name="paths">素材路径(目录或单个文件)。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <returns>对比完成的任务;全部检查通过时任务结果为空字符串,否则为失败摘要。</returns>
    internal static Task<string> RunAsync(string[] paths, string reportPath)
        => Task.Run(() => Run(paths, reportPath), System.Threading.CancellationToken.None);

    private static string Run(string[] paths, string reportPath)
    {
        var report = new StringBuilder();
        var files = Expand(paths);
        var failures = new List<string>();

        Report(report, "PhotoViewer HEIF thumbnail-item probe platform=Windows"
            + " machine=" + Environment.MachineName
            + " cpus=" + Environment.ProcessorCount
            + " os=" + RuntimeInformation.OSDescription
            + " filmstripRequest=" + FilmstripRequest
            + " dinoRequest=" + DinoRequest);
        Report(report, $"files={files.Count}");
        WriteReport(reportPath, report);

        var thumbnailDecoder = new LibHeifDecoder();
        var primaryDecoder = new LibHeifDecoder(preferContainerThumbnail: false);

        var filmstripDiffs = new List<double>();
        var filmstripShifts = new List<string>();
        var filmstripTimes = new List<double>();
        var primaryTimes = new List<double>();
        var thumbTimes = new List<double>();
        var largest = 0.0;
        var largestFile = "-";
        var thumbItemSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var largeDiffs = new List<double>();
        var largeItemSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var orientationMismatches = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var data = File.ReadAllBytes(file);
                var container = HeifContainer.TryParse(data, out var parseReason);
                if (container is null)
                {
                    Report(report, $"{name,-22} SKIP container: {parseReason}");
                    continue;
                }

                var fileRef = new LocalFileRef(file);

                // ---- 胶片栏档:容器缩略图项 vs 主图缩放 ----
                var thumbWatch = Stopwatch.StartNew();
                var thumbnail = thumbnailDecoder.DecodeAsync(fileRef, FilmstripRequest).GetAwaiter().GetResult();
                thumbWatch.Stop();
                var primaryWatch = Stopwatch.StartNew();
                var primary = primaryDecoder.DecodeAsync(fileRef, FilmstripRequest).GetAwaiter().GetResult();
                primaryWatch.Stop();
                if (thumbnail is null || primary is null)
                {
                    failures.Add(name + ": decode null (thumbnail=" + (thumbnail is null) + " primary=" + (primary is null) + ")");
                    thumbnail?.Release();
                    primary?.Release();
                    continue;
                }

                thumbTimes.Add(thumbWatch.Elapsed.TotalMilliseconds);
                primaryTimes.Add(primaryWatch.Elapsed.TotalMilliseconds);

                var item = container.FindThumbnail(Math.Min(primary.Width, primary.Height));
                if (item is not null)
                {
                    thumbItemSizes[$"{item.Width}x{item.Height}"] =
                        thumbItemSizes.GetValueOrDefault($"{item.Width}x{item.Height}") + 1;
                    if (item.ExifOrientation != container.ExifOrientation) orientationMismatches++;
                }

                var geometryOk = thumbnail.Width == primary.Width && thumbnail.Height == primary.Height;
                if (!geometryOk)
                {
                    failures.Add(name + ": size " + thumbnail.Width + "x" + thumbnail.Height
                        + " != " + primary.Width + "x" + primary.Height);
                }

                var (diff, dx, dy) = AlignedMeanAbsDiff(thumbnail, primary, AlignmentRadius);
                var center = MeanAbsDiff(thumbnail, primary, 0, 0);
                if (diff > largest)
                {
                    largest = diff;
                    largestFile = name;
                }

                filmstripDiffs.Add(diff);
                filmstripShifts.Add($"{dx},{dy}");
                if (!geometryOk || diff > MeanAbsDiffFailThreshold)
                {
                    failures.Add(name + ": geometry=" + geometryOk + " mad=" + Num(diff)
                        + " centerMad=" + Num(center) + " shift=" + dx + "," + dy);
                }

                // ---- DINO 档:两条解码器都必须走主图,产物逐字节相同 ----
                var dinoThumb = thumbnailDecoder.DecodeAsync(fileRef, DinoRequest).GetAwaiter().GetResult();
                var dinoPrimary = primaryDecoder.DecodeAsync(fileRef, DinoRequest).GetAwaiter().GetResult();
                var dinoIdentical = dinoThumb is not null && dinoPrimary is not null
                    && dinoThumb.Width == dinoPrimary.Width && dinoThumb.Height == dinoPrimary.Height
                    && dinoThumb.Pixels.AsSpan().SequenceEqual(dinoPrimary.Pixels);
                if (!dinoIdentical)
                {
                    failures.Add(name + ": DINO request path differs between decoders");
                }

                dinoThumb?.Release();
                dinoPrimary?.Release();

                // ---- 大缩略图档(400):目标短边 267 超出 320×212 项,必须落到 1616×1080 项,
                //      顺带验证"编码 1664×1088 超出 ispe 1616×1080 时按左上角裁到 ispe"这条路径。----
                var largeThumb = thumbnailDecoder.DecodeAsync(fileRef, LargeRequest).GetAwaiter().GetResult();
                var largePrimary = primaryDecoder.DecodeAsync(fileRef, LargeRequest).GetAwaiter().GetResult();
                var largeItem = largePrimary is null
                    ? null
                    : container.FindThumbnail(Math.Min(largePrimary.Width, largePrimary.Height));
                if (largeItem is not null)
                {
                    var key = largeItem.Width + "x" + largeItem.Height;
                    largeItemSizes[key] = largeItemSizes.GetValueOrDefault(key) + 1;
                }

                var largeGeometryOk = largeThumb is not null && largePrimary is not null
                    && largeThumb.Width == largePrimary.Width && largeThumb.Height == largePrimary.Height;
                var largeDiff = double.MaxValue;
                if (!largeGeometryOk)
                {
                    failures.Add(name + ": large request geometry mismatch");
                }
                else
                {
                    largeDiff = AlignedMeanAbsDiff(largeThumb!, largePrimary!, AlignmentRadius).Diff;
                    largeDiffs.Add(largeDiff);
                    if (largeDiff > MeanAbsDiffFailThreshold)
                    {
                        failures.Add(name + ": large mad=" + Num(largeDiff));
                    }
                }

                largeThumb?.Release();
                largePrimary?.Release();

                Report(report, $"{name,-22} {container.Width}x{container.Height} rot={container.ExifOrientation}"
                    + " item=" + (item is null ? "none" : item.Width + "x" + item.Height + "/rot" + item.ExifOrientation)
                    + " out=" + thumbnail.Width + "x" + thumbnail.Height
                    + " mad=" + Num(diff) + " centerMad=" + Num(center) + " shift=" + dx + "," + dy
                    + " thumbMs=" + Num(thumbWatch.Elapsed.TotalMilliseconds)
                    + " primaryMs=" + Num(primaryWatch.Elapsed.TotalMilliseconds)
                    + " dinoSame=" + (dinoIdentical ? "yes" : "no")
                    + " largeItem=" + (largeItem is null ? "none" : largeItem.Width + "x" + largeItem.Height)
                    + " largeMad=" + Num(largeDiff));

                thumbnail.Release();
                primary.Release();
                if ((filmstripDiffs.Count % 20) == 0) WriteReport(reportPath, report);
            }
            catch (Exception exception)
            {
                failures.Add(name + ": " + exception.GetType().Name + " " + exception.Message);
            }
        }

        WriteReport(reportPath, report);
        return Summarize(
            reportPath,
            report,
            files.Count,
            filmstripDiffs,
            largeDiffs,
            filmstripShifts,
            thumbTimes,
            primaryTimes,
            largest,
            largestFile,
            thumbItemSizes,
            largeItemSizes,
            orientationMismatches,
            failures);
    }

    private static string Summarize(
        string reportPath,
        StringBuilder report,
        int fileCount,
        List<double> diffs,
        List<double> largeDiffs,
        List<string> shifts,
        List<double> thumbTimes,
        List<double> primaryTimes,
        double largest,
        string largestFile,
        Dictionary<string, int> thumbItemSizes,
        Dictionary<string, int> largeItemSizes,
        int orientationMismatches,
        List<string> failures)
    {
        var sorted = diffs.OrderBy(value => value).ToArray();
        Report(report, "---- summary ----");
        Report(report, $"files={fileCount} compared={sorted.Length} failed={failures.Count}");
        Report(report, "mad p50=" + Num(Pct(sorted, 0.50)) + " p95=" + Num(Pct(sorted, 0.95))
            + " max=" + Num(largest) + $" ({largestFile})"
            + " threshold=" + Num(MeanAbsDiffFailThreshold));
        Report(report, "thumbItemDist=" + string.Join(" ", thumbItemSizes
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key + ":" + pair.Value)));
        var largeSorted = largeDiffs.OrderBy(value => value).ToArray();
        Report(report, "request=" + LargeRequest + " compared=" + largeSorted.Length
            + " mad p50=" + Num(Pct(largeSorted, 0.50))
            + " p95=" + Num(Pct(largeSorted, 0.95))
            + " max=" + Num(largeSorted.Length > 0 ? largeSorted[^1] : -1)
            + " itemDist=" + string.Join(" ", largeItemSizes
                .OrderByDescending(pair => pair.Value)
                .Select(pair => pair.Key + ":" + pair.Value)));
        Report(report, "itemVsPrimaryOrientationMismatch=" + orientationMismatches);
        if (shifts.Count > 0)
        {
            Report(report, "shiftDist=" + string.Join(" ", shifts
                .GroupBy(shift => shift)
                .OrderByDescending(group => group.Count())
                .Take(6)
                .Select(group => group.Key + ":" + group.Count())));
        }

        Report(report, "timing thumbnailItem p50=" + Num(Pct(thumbTimes, 0.50))
            + " p95=" + Num(Pct(thumbTimes, 0.95))
            + " max=" + Num(Pct(thumbTimes, 1.0))
            + " | primary p50=" + Num(Pct(primaryTimes, 0.50))
            + " p95=" + Num(Pct(primaryTimes, 0.95)));

        foreach (var failure in failures.Take(40)) Report(report, "FAIL " + failure);
        Report(report, failures.Count == 0 ? "RESULT PASS" : "RESULT FAIL");
        WriteReport(reportPath, report);
        return failures.Count == 0 ? string.Empty : string.Join("; ", failures.Take(10));
    }

    /// <summary>在 ±<paramref name="radius"/> 像素内搜最佳对齐,返回(最小平均绝对差, dx, dy)。</summary>
    private static (double Diff, int Dx, int Dy) AlignedMeanAbsDiff(
        DecodedImage a, DecodedImage b, int radius)
    {
        var best = double.MaxValue;
        var bestDx = 0;
        var bestDy = 0;
        // 先把"无位移"定为基准,只有确实更优的位移才被采纳(避免噪声把结果换成伪对齐)。
        best = MeanAbsDiff(a, b, 0, 0);
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var diff = MeanAbsDiff(a, b, dx, dy);
                if (diff < best)
                {
                    best = diff;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        return (best, bestDx, bestDy);
    }

    /// <summary>两图在 (dx,dy) 位移下的平均绝对差(三通道,BGRA);重叠区不足时返回最大值。</summary>
    private static double MeanAbsDiff(DecodedImage a, DecodedImage b, int dx, int dy)
    {
        var width = Math.Min(a.Width - Math.Abs(dx), b.Width);
        var height = Math.Min(a.Height - Math.Abs(dy), b.Height);
        if (width <= 0 || height <= 0) return double.MaxValue;

        var ax = Math.Max(0, -dx);
        var ay = Math.Max(0, -dy);
        var bx = Math.Max(0, dx);
        var by = Math.Max(0, dy);
        width = Math.Min(width, Math.Min(a.Width - ax, b.Width - bx));
        height = Math.Min(height, Math.Min(a.Height - ay, b.Height - by));
        if (width <= 0 || height <= 0) return double.MaxValue;

        long total = 0;
        var aPixels = a.Pixels;
        var bPixels = b.Pixels;
        for (var row = 0; row < height; row++)
        {
            var aOffset = ((ay + row) * a.Stride) + (ax * 4);
            var bOffset = ((by + row) * b.Stride) + (bx * 4);
            for (var column = 0; column < width; column++)
            {
                var aIndex = aOffset + (column * 4);
                var bIndex = bOffset + (column * 4);
                total += Math.Abs(aPixels[aIndex] - bPixels[bIndex]);
                total += Math.Abs(aPixels[aIndex + 1] - bPixels[bIndex + 1]);
                total += Math.Abs(aPixels[aIndex + 2] - bPixels[bIndex + 2]);
            }
        }

        return (double)total / (width * height * 3);
    }

    private static List<string> Expand(string[] paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                files.AddRange(Directory.EnumerateFiles(full).Where(IsHeif));
            }
            else if (File.Exists(full))
            {
                files.Add(full);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsHeif(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    private static double Pct(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0) return -1;
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
    }

    private static string Num(double value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static void Report(StringBuilder report, string line) => report.AppendLine(line);

    private static void WriteReport(string path, StringBuilder report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report.ToString());
        }
        catch (Exception exception)
        {
            Console.WriteLine("[HeifThumbProbe] report failed: " + exception.Message);
        }
    }
}
