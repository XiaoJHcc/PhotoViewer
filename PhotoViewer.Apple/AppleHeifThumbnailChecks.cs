using System.Diagnostics;
using System.Globalization;
using System.Text;
using Foundation;
using ImageIO;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

/// <summary>
/// 验收专用（测试钩子）：HEIF 列表小图内嵌来源对照。基线为解码器子采样路径 preferEmbedded:false，
/// 候选为平台 <c>DecodeAsync</c>（maxSize ≤ 512 时优先容器内嵌小图）。两个入口都必须由头工程传入真实平台解码器。
/// 120 允许内嵌近似并要求不慢于基线，400 要求产物长边达到 min(maxSize, 源长边)，560 要求与基线逐字节相同。
/// </summary>
public static class AppleHeifThumbnailChecks
{
    private const int EmbeddedMaxSize = 512;
    private const int EmbeddedMeanTolerance = 8;
    private const int TimingRuns = 3;
    private static readonly int[] Targets = [120, 400, 560];

    /// <summary>跑完整套对照；报告逐样本落盘，返回全部检查是否通过。</summary>
    /// <param name="directory">HEIF 素材目录（只读，递归不展开）。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <param name="baseline">基线入口：真实平台子采样解码，preferEmbedded 恒为 false。</param>
    /// <param name="candidate">候选入口：真实平台 <c>HeifDecoder.DecodeAsync</c>。</param>
    public static Task<bool> RunAsync(
        string directory,
        string reportPath,
        Func<IFileRef, int?, Task<DecodedImage?>> baseline,
        Func<IFileRef, int?, Task<DecodedImage?>> candidate)
        => Task.Run(() => Run(directory, reportPath, baseline, candidate), CancellationToken.None);

    private static bool Run(
        string directory,
        string reportPath,
        Func<IFileRef, int?, Task<DecodedImage?>> baseline,
        Func<IFileRef, int?, Task<DecodedImage?>> candidate)
    {
        string? reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(reportDirectory)) Directory.CreateDirectory(reportDirectory);

        using var report = new StreamWriter(reportPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        report.WriteLine($"PhotoViewer HEIF embedded-thumbnail checks platform={PlatformName()} machine={Environment.MachineName} " +
            $"targets={string.Join(",", Targets)} embedded_max={EmbeddedMaxSize} mean_tolerance={EmbeddedMeanTolerance} runs={TimingRuns}");
        report.WriteLine("baseline=DecodeSubsampledWithImageIO(preferEmbedded:false); candidate=HeifDecoder.DecodeAsync; " +
            "120=embedded approximation allowed, 400=min(maxSize, source long edge) required, 560=byte-identical");
        report.WriteLine("completed=0 success=0");

        string[] files;
        try
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"输入目录不存在：{directory}");
            files = Directory.GetFiles(directory)
                .Where(IsHeif)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            report.WriteLine($"FAIL directory {exception.GetType().Name}: {exception.Message}");
            report.WriteLine("completed=1 success=0");
            report.WriteLine("SUMMARY FAIL");
            Console.WriteLine($"[HeifThumbChecks] FAIL {reportPath}");
            return false;
        }

        report.WriteLine($"directory={directory} heif_files={files.Length} all_files={Directory.GetFiles(directory).Length}");
        if (files.Length == 0)
        {
            report.WriteLine("FAIL no-heif-fixtures 目录内没有 HEIC/HEIF/HIF 素材");
            report.WriteLine("completed=1 success=0");
            report.WriteLine("SUMMARY FAIL");
            Console.WriteLine($"[HeifThumbChecks] FAIL {reportPath}");
            return false;
        }

        bool success = true;
        bool embeddedEffect = false;
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            var file = new LocalFileRef(path);
            var source = ReadSource(path);
            report.WriteLine($"== heif {name} source={source.Width}x{source.Height} orientation={source.Orientation}");
            foreach (int target in Targets)
            {
                try
                {
                    success &= CheckTarget(report, name, file, source, target, baseline, candidate, ref embeddedEffect);
                }
                catch (Exception exception)
                {
                    success = false;
                    report.WriteLine($"FAIL {name} T={target} {exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        if (!embeddedEffect)
        {
            success = false;
            report.WriteLine("FAIL no-embedded-effect 所有样本在 120 与基线逐字节一致且无提速，内嵌来源未生效");
        }

        report.WriteLine($"completed=1 success={(success ? 1 : 0)}");
        report.WriteLine(success ? "SUMMARY PASS" : "SUMMARY FAIL");
        Console.WriteLine($"[HeifThumbChecks] {(success ? "PASS" : "FAIL")} {reportPath}");
        return success;
    }

    private static bool CheckTarget(
        StreamWriter report,
        string name,
        IFileRef file,
        SourceInfo source,
        int target,
        Func<IFileRef, int?, Task<DecodedImage?>> baseline,
        Func<IFileRef, int?, Task<DecodedImage?>> candidate,
        ref bool embeddedEffect)
    {
        using var reference = baseline(file, target).GetAwaiter().GetResult();
        using var product = candidate(file, target).GetAwaiter().GetResult();

        if (reference == null)
        {
            // 基线自身无产物时不判定退化；内嵌路径能单独成功属于改善。
            string candidateText = product == null ? "null" : $"{product.Width}x{product.Height}";
            report.WriteLine($"{name} T={target} baseline=null candidate={candidateText} PASS(基线无产物)");
            return true;
        }

        if (product == null)
        {
            report.WriteLine($"{name} T={target} baseline={reference.Width}x{reference.Height} candidate=null FAIL(候选退化为空)");
            return false;
        }

        // 内嵌小图按自身比例缩放，长边可差 1 像素（如 267x400 vs 266x400）；超出即视为几何异常。
        int widthDelta = Math.Abs(product.Width - reference.Width);
        int heightDelta = Math.Abs(product.Height - reference.Height);
        bool sameSize = widthDelta == 0 && heightDelta == 0;
        bool geometry = (sameSize || (widthDelta <= 1 && heightDelta <= 1))
            && product.Orientation == reference.Orientation
            && product.Encoding == reference.Encoding;
        int sourceLongEdge = source.Width > 0 && source.Height > 0
            ? Math.Max(source.Width, source.Height)
            : Math.Max(reference.Width, reference.Height);
        int required = Math.Min(target, sourceLongEdge);
        bool reachesTarget = Math.Max(product.Width, product.Height) >= required;
        using var resized = geometry && !sameSize ? SkiaImageCodec.ScaleToExact(product, reference.Width, reference.Height) : null;
        var difference = geometry ? Compare(reference, resized ?? product) : (Identical: false, Mean: double.NaN, Max: 0, Samples: 0L);

        var reasons = new List<string>();
        if (!geometry) reasons.Add("geometry");
        if (!reachesTarget) reasons.Add($"long_edge<{required}");
        if (target > EmbeddedMaxSize)
        {
            if (!sameSize) reasons.Add("not_same_size");
            else if (!difference.Identical) reasons.Add("not_identical");
        }
        else if (!difference.Identical && !(difference.Mean <= EmbeddedMeanTolerance))
        {
            reasons.Add($"mean>{EmbeddedMeanTolerance}");
        }

        double baselineMs = 0, productMs = 0;
        if (target == 120)
        {
            baselineMs = MedianDecode(baseline, file, target);
            productMs = MedianDecode(candidate, file, target);
            if (productMs > baselineMs * 1.15 + 8) reasons.Add("slower");
            embeddedEffect |= !difference.Identical || productMs * 2 <= baselineMs;
        }

        bool ok = reasons.Count == 0;
        string meanText = difference.Samples > 0 ? difference.Mean.ToString("0.00", CultureInfo.InvariantCulture) : "n/a";
        string maxText = difference.Samples > 0 ? difference.Max.ToString(CultureInfo.InvariantCulture) : "n/a";
        string sizeText = sameSize ? string.Empty : $" size_delta={widthDelta}/{heightDelta}";
        string timing = target == 120
            ? $" time_baseline={baselineMs:0.0}ms time_candidate={productMs:0.0}ms speedup={(productMs > 0 ? baselineMs / productMs : 0):0.0}x"
            : string.Empty;
        report.WriteLine($"{name} T={target} baseline={reference.Width}x{reference.Height} " +
            $"candidate={product.Width}x{product.Height} orientation={reference.Orientation}/{product.Orientation} " +
            $"required={required} identical={(difference.Identical ? 1 : 0)} mean={meanText} max={maxText}{sizeText}" +
            timing + (ok ? " PASS" : $" FAIL({string.Join(",", reasons)})"));
        return ok;
    }

    private static (bool Identical, double Mean, int Max, long Samples) Compare(DecodedImage reference, DecodedImage product)
    {
        if (reference.Width != product.Width || reference.Height != product.Height || reference.Encoding != product.Encoding)
            return (false, double.NaN, -1, 0);

        int bytesPerPixel = DecodedImage.BytesPerPixel(reference.Encoding);
        long sum = 0;
        long samples = 0;
        int max = 0;
        bool identical = true;
        for (int y = 0; y < reference.Height; y++)
        {
            int referenceRow = y * reference.Stride;
            int productRow = y * product.Stride;
            for (int x = 0; x < reference.Width * bytesPerPixel; x++)
            {
                if (bytesPerPixel == 4 && (x & 3) == 3) continue; // 跳过 alpha，与此前 Swift 探针同口径
                int delta = Math.Abs(reference.Pixels[referenceRow + x] - product.Pixels[productRow + x]);
                sum += delta;
                samples++;
                if (delta > max) max = delta;
                if (delta != 0) identical = false;
            }
        }

        return (identical, samples > 0 ? (double)sum / samples : double.NaN, max, samples);
    }

    private static double MedianDecode(Func<IFileRef, int?, Task<DecodedImage?>> decode, IFileRef file, int target)
    {
        var times = new double[TimingRuns];
        for (int run = 0; run < TimingRuns; run++)
        {
            var watch = Stopwatch.StartNew();
            using var image = decode(file, target).GetAwaiter().GetResult();
            times[run] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[times.Length / 2];
    }

    private static SourceInfo ReadSource(string path)
    {
        try
        {
            using var pool = new NSAutoreleasePool();
            using var url = NSUrl.FromFilename(path);
            using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false });
            var properties = source?.GetProperties(0, null);
            if (properties == null) return default;
            return new SourceInfo(
                properties.PixelWidth ?? 0,
                properties.PixelHeight ?? 0,
                properties.Orientation is { } orientation ? (int)orientation : 1);
        }
        catch
        {
            return default;
        }
    }

    private static bool IsHeif(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private static string PlatformName()
        => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsIOS() ? "iOS" : Environment.OSVersion.ToString();

    private readonly record struct SourceInfo(int Width, int Height, int Orientation);
}
