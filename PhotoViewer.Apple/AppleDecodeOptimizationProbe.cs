using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using CoreImage;
using Foundation;
using ImageIO;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

/// <summary>
/// 验收专用只读对照探针：<c>--pv-decode-optimize &lt;目录&gt;</c>。
/// JPEG/HEIC 比较产品 ImageIO+CGBitmapContext 与 ImageIO+CIImage+CIContext 两条路径；
/// RAW 分开测"每次独立新实例"与"单实例 proxy→full"，核对像素、朝向、内存。
/// 不进入产品；像素/朝向/色调守卫失败即探针 FAIL。
/// </summary>
public static class AppleDecodeOptimizationProbe
{
    private const int TargetLongEdge = 4532;
    private const int Runs = 3;
    private const int MaxPerClass = 2;
    private const double HalfMeanLimit = 0.01;
    private const double P3MeanLimit = 1.0;

    private static readonly string[] JpegExtensions = [".jpg", ".jpeg"];
    private static readonly string[] HeicExtensions = [".heic", ".heif", ".hif"];
    private static readonly string[] RawExtensions = [".arw"];

    /// <summary>RAW Full 逐像素对拍允许同时保留两份输出；超出改用首尾+网格签名，避免双份大缓冲。</summary>
    private static long ComparePairBudget => OperatingSystem.IsMacOS() ? 768L * 1024 * 1024 : 320L * 1024 * 1024;

    public static Task<bool> RunAsync(string directory, string reportPath)
        => Task.Run(() => Run(directory, reportPath), CancellationToken.None);

    private static bool Run(string directory, string reportPath)
    {
        var report = new ReportWriter(reportPath);
        bool success = true;
        report.WriteLine($"PhotoViewer decode optimization probe platform={PlatformName()} " +
            $"arch={RuntimeInformation.ProcessArchitecture} cpus={Environment.ProcessorCount} T={TargetLongEdge} " +
            $"samples={Runs} compare_pair_budget={Megabytes(ComparePairBudget)}");
        report.WriteLine("baseline=e2e_*_product: ApplePresentationDecoder(useCoreImage:false), bitmap only");
        report.WriteLine("accelerated=e2e_*_accelerated: actual default ApplePresentationDecoder; CI only for HEIF F16 >=4MP; first sample keeps CI init");
        report.WriteLine("ci=e2e_*_ci: independent probe candidate (ImageIO thumbnail + CIImage.FromCGImage + own CIContext); experiment only");
        report.WriteLine("note=pixel equality is not speed evidence; gc is not forced; candidate verdict is probe-only");
        report.WriteLine("raw_cold=proxy and full each use independent new CIRawFilter; raw_reuse=one new instance per round does proxy->full");
        report.WriteLine("completed=0 success=0");
        report.Flush();
        try
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"输入目录不存在：{directory}");
            Candidate[] inputs = Discover(directory);
            if (inputs.Length == 0) throw new InvalidOperationException("目录内没有 JPEG/HEIC/ARW 素材");
            report.WriteLine($"directory={directory} scan=top+1level files={inputs.Length} " +
                $"jpeg={inputs.Count(x => x.Kind == "jpeg")} heic={inputs.Count(x => x.Kind == "heic")} raw={inputs.Count(x => x.Kind == "raw")}");
            foreach (Candidate input in inputs)
                report.WriteLine($"input kind={input.Kind} name={Path.GetFileName(input.Path)} longEdge={input.LongEdge}" +
                    (input.UntaggedAdobe ? " untaggedAdobe=1" : ""));

            using var probe = CreateProbe();
            foreach (Candidate input in inputs)
            {
                try
                {
                    bool ok = input.Kind == "raw"
                        ? MeasureRaw(report, probe, input.Path)
                        : MeasureStill(report, probe, input.Path, input.Kind);
                    success &= ok;
                }
                catch (Exception exception)
                {
                    success = false;
                    report.WriteLine($"ERROR {Path.GetFileName(input.Path)} {exception.GetType().Name}: {exception.Message}");
                }

                report.Flush();
            }
        }
        catch (Exception exception)
        {
            success = false;
            report.WriteLine($"ERROR probe {exception.GetType().Name}: {exception.Message}");
        }

        report.WriteLine($"completed=1 success={(success ? 1 : 0)}");
        report.WriteLine(success ? "SUMMARY PASS" : "SUMMARY FAIL");
        report.Flush();
        Console.WriteLine($"[DecodeOptimize] {(success ? "PASS" : "FAIL")} {reportPath}");
        return success;
    }

    private static ProbeContext CreateProbe()
    {
        using var pool = new NSAutoreleasePool();
        return new ProbeContext();
    }

    private static bool MeasureStill(ReportWriter report, ProbeContext probe, string path, string kind)
    {
        using var pool = new NSAutoreleasePool();
        using var url = NSUrl.FromFilename(path);
        int sourceWidth, sourceHeight, orientation;
        string jpeg;
        using (var source = OpenSource(url))
        {
            var properties = source.GetProperties(0, null);
            sourceWidth = properties?.PixelWidth ?? 0;
            sourceHeight = properties?.PixelHeight ?? 0;
            orientation = properties?.Orientation is { } direction ? (int)direction : 1;
            jpeg = DescribeJpeg(path, properties);
        }

        if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("源尺寸无效");
        report.WriteLine($"== {kind} {Path.GetFileName(path)} source={sourceWidth}x{sourceHeight} " +
            $"orientation={orientation} T={TargetLongEdge} jpeg={jpeg}");
        bool ok = true;
        foreach (var plan in Plans(probe))
        {
            ok &= MeasureStillEncoding(report, probe, url, path, plan, orientation, sourceWidth, sourceHeight);
            report.Flush();
        }

        ok &= MeasureDecodedConversions(report, probe, url, path);
        return ok;
    }

    private static bool MeasureStillEncoding(ReportWriter report, ProbeContext probe, NSUrl url, string path,
        EncodingPlan plan, int orientation, int sourceWidth, int sourceHeight)
    {
        // baseline 显式关闭 CI；accelerated 是真实默认产品路径（HEIF F16 >=4MP 才走 CI）。
        var baseline = new ApplePresentationDecoder(useCoreImage: false);
        var accelerated = new ApplePresentationDecoder();
        var file = new LocalFileRef(path);
        var productTimes = new double[Runs];
        var acceleratedTimes = new double[Runs];
        DecodedImage? product = null;
        DecodedImage? acceleratedImage = null;
        int productWidth = 0, productHeight = 0, productOrientation = 1;
        int acceleratedWidth = 0, acceleratedHeight = 0, acceleratedOrientation = 1;
        int candidateWidth = 0, candidateHeight = 0, candidateStride = 0;
        byte[]? candidate = null;
        var decodeTimes = new double[Runs];
        var convertTimes = new double[Runs];
        var totalTimes = new double[Runs];

        void MeasureProduct(int run)
        {
            using var pool = new NSAutoreleasePool();
            product?.Dispose();
            product = null;
            var watch = Stopwatch.StartNew();
            product = baseline.DecodeAsync(file, TargetLongEdge, plan.Encoding, CancellationToken.None)
                .GetAwaiter().GetResult() ?? throw new InvalidOperationException("baseline 解码返回空");
            productTimes[run] = watch.Elapsed.TotalMilliseconds;
            productWidth = product.Width;
            productHeight = product.Height;
            productOrientation = product.Orientation;
        }

        void MeasureAccelerated(int run)
        {
            using var pool = new NSAutoreleasePool();
            acceleratedImage?.Dispose();
            acceleratedImage = null;
            var watch = Stopwatch.StartNew();
            acceleratedImage = accelerated.DecodeAsync(file, TargetLongEdge, plan.Encoding, CancellationToken.None)
                .GetAwaiter().GetResult() ?? throw new InvalidOperationException("默认产品解码返回空");
            acceleratedTimes[run] = watch.Elapsed.TotalMilliseconds;
            acceleratedWidth = acceleratedImage.Width;
            acceleratedHeight = acceleratedImage.Height;
            acceleratedOrientation = acceleratedImage.Orientation;
        }

        void MeasureCandidate(int run)
        {
            using var roundPool = new NSAutoreleasePool();
            var total = Stopwatch.StartNew();
            using var source = OpenSource(url);
            var decodeWatch = Stopwatch.StartNew();
            var properties = source.GetProperties(0, null);
            using var image = CreateThumbnail(source);
            // 与产品同一套无 ICC JPEG 色彩推断；候选不能默默按 sRGB 解释。
            using var inferred = InterpretUntaggedJpeg(image, properties, path);
            var effective = inferred ?? image;
            decodeTimes[run] = decodeWatch.Elapsed.TotalMilliseconds;
            int width = checked((int)image.Width);
            int height = checked((int)image.Height);
            if (run == 0)
            {
                candidateWidth = width;
                candidateHeight = height;
                candidateStride = checked(width * plan.BytesPerPixel);
                candidate = ImageBufferPool.Rent(checked(candidateStride * height));
            }
            else if (width != candidateWidth || height != candidateHeight)
            {
                throw new InvalidOperationException($"每轮缩解码尺寸不一致：{width}x{height} vs {candidateWidth}x{candidateHeight}");
            }

            var convertWatch = Stopwatch.StartNew();
            RenderCi(probe, effective, candidate!, width, height, plan);
            convertTimes[run] = convertWatch.Elapsed.TotalMilliseconds;
            totalTimes[run] = total.Elapsed.TotalMilliseconds;
        }

        try
        {
            // 三条路径轮流排在首/中/末，抵消"先 baseline 后 candidate"的顺序偏差；首次 CI 初始化留在 accelerated 首样本。
            for (int run = 0; run < Runs; run++)
            {
                string[] order = run switch
                {
                    0 => ["baseline", "accelerated", "candidate"],
                    1 => ["accelerated", "candidate", "baseline"],
                    _ => ["candidate", "baseline", "accelerated"],
                };
                foreach (string step in order)
                {
                    if (step == "baseline") MeasureProduct(run);
                    else if (step == "accelerated") MeasureAccelerated(run);
                    else MeasureCandidate(run);
                }
            }

            if (product == null || acceleratedImage == null) throw new InvalidOperationException("产品解码未产出图像");
            bool acceleratedPath = OperatingSystem.IsMacOS() && IsHeic(path) && plan.Half && (long)productWidth * productHeight >= 4_000_000;
            report.WriteLine(Timeline($"e2e_{plan.Label}_product", productTimes,
                $"encoding={plan.Encoding} out={productWidth}x{productHeight} orientation={productOrientation} decoder=bitmap(useCoreImage=false)"));
            report.WriteLine(Timeline($"e2e_{plan.Label}_accelerated", acceleratedTimes,
                $"encoding={plan.Encoding} out={acceleratedWidth}x{acceleratedHeight} orientation={acceleratedOrientation} " +
                $"decoder=default accelerated={(acceleratedPath ? "active" : "inactive")} first=includes-CI-init"));
            report.WriteLine(Timeline($"e2e_{plan.Label}_ci", totalTimes,
                $"encoding={plan.Encoding} out={candidateWidth}x{candidateHeight} orientation={orientation} " +
                $"decode={Median(decodeTimes):0.0}ms convert={Median(convertTimes):0.0}ms independent=1"));

            bool ok = true;
            bool candidateGeometry = productWidth == candidateWidth && productHeight == candidateHeight;
            if (!candidateGeometry)
            {
                ok = false;
                report.WriteLine($"FAIL e2e_{plan.Label}_geometry product={productWidth}x{productHeight} ci={candidateWidth}x{candidateHeight}");
            }

            if (productOrientation != orientation)
            {
                ok = false;
                report.WriteLine($"FAIL e2e_{plan.Label}_orientation product={productOrientation} source={orientation}");
            }

            if (product.SourceWidth != sourceWidth || product.SourceHeight != sourceHeight)
            {
                ok = false;
                report.WriteLine($"FAIL e2e_{plan.Label}_source product={product.SourceWidth}x{product.SourceHeight} imageio={sourceWidth}x{sourceHeight}");
            }

            bool acceleratedGeometry = acceleratedWidth == productWidth && acceleratedHeight == productHeight &&
                acceleratedOrientation == productOrientation &&
                acceleratedImage.SourceWidth == product.SourceWidth && acceleratedImage.SourceHeight == product.SourceHeight &&
                acceleratedImage.Encoding == product.Encoding && acceleratedImage.ColorSpace == product.ColorSpace;
            if (!acceleratedGeometry)
            {
                ok = false;
                report.WriteLine($"FAIL e2e_{plan.Label}_accelerated_geometry accelerated={acceleratedWidth}x{acceleratedHeight}/" +
                    $"{acceleratedImage.SourceWidth}x{acceleratedImage.SourceHeight}/{acceleratedImage.Encoding}/{acceleratedImage.ColorSpace} " +
                    $"product={productWidth}x{productHeight}/{product.SourceWidth}x{product.SourceHeight}/{product.Encoding}/{product.ColorSpace}");
            }

            bool quality = false;
            if (candidateGeometry)
            {
                var difference = Compare(candidate!, candidateStride, product.Pixels, product.Stride,
                    candidateWidth, candidateHeight, plan.Half);
                var (rows, flipMean) = DetectRows(candidate!, candidateStride, product.Pixels, product.Stride,
                    candidateWidth, candidateHeight, plan.Half, difference.Mean);
                report.WriteLine(DifferenceLine($"compare e2e_{plan.Label}", difference) + $" rows={rows} flip_mean={flipMean:0.000000}");
                double limit = plan.Half ? HalfMeanLimit : P3MeanLimit;
                quality = rows != "flipped" && difference.Samples > 0 && difference.Mean <= limit;
                report.WriteLine($"verdict e2e_{plan.Label} candidate={(quality ? "within-tolerance" : "outside-tolerance")} " +
                    $"mean_limit={limit} note=probe-only");
            }

            bool acceleratedOk = false;
            if (acceleratedGeometry)
            {
                var difference = Compare(acceleratedImage.Pixels, acceleratedImage.Stride, product.Pixels, product.Stride,
                    productWidth, productHeight, plan.Half);
                var (rows, flipMean) = DetectRows(acceleratedImage.Pixels, acceleratedImage.Stride, product.Pixels, product.Stride,
                    productWidth, productHeight, plan.Half, difference.Mean);
                double maxLimit = plan.Half ? 0.003 : 2;
                report.WriteLine(DifferenceLine($"compare e2e_{plan.Label}_accelerated", difference) +
                    $" rows={rows} flip_mean={flipMean:0.000000} max_limit={maxLimit} accelerated={(acceleratedPath ? "active" : "inactive")}");
                acceleratedOk = rows != "flipped" && difference.Samples > 0 && difference.Max <= maxLimit;
                report.WriteLine($"verdict e2e_{plan.Label}_accelerated accelerated={(acceleratedOk ? "within-tolerance" : "outside-tolerance")} " +
                    $"max_limit={maxLimit} note=probe-only");
            }

            return ok && quality && acceleratedOk;
        }
        finally
        {
            product?.Dispose();
            acceleratedImage?.Dispose();
            if (candidate != null) ImageBufferPool.Return(candidate);
        }
    }

    /// <summary>同一张已解码 CGImage 的两种转换；源解码在计时之外，不能当作新图解码速度。</summary>
    private static bool MeasureDecodedConversions(ReportWriter report, ProbeContext probe, NSUrl url, string path)
    {
        using var source = OpenSource(url);
        var properties = source.GetProperties(0, null);
        using var decoded = CreateThumbnail(source);
        using var inferred = InterpretUntaggedJpeg(decoded, properties, path);
        var image = inferred ?? decoded;
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        report.WriteLine($"decoded out={width}x{height} jpeg={DescribeJpeg(path, properties)} scope=convert-only(decode excluded)");
        bool ok = true;
        foreach (var plan in Plans(probe))
        {
            int stride = checked(width * plan.BytesPerPixel);
            byte[] bitmap = ImageBufferPool.Rent(checked(stride * height));
            byte[] candidate = ImageBufferPool.Rent(checked(stride * height));
            try
            {
                var bitmapTimes = new double[Runs];
                var candidateTimes = new double[Runs];
                for (int run = 0; run < Runs; run++)
                {
                    var watch = Stopwatch.StartNew();
                    DrawBitmap(image, bitmap, width, height, plan.Encoding);
                    bitmapTimes[run] = watch.Elapsed.TotalMilliseconds;
                }

                for (int run = 0; run < Runs; run++)
                {
                    var watch = Stopwatch.StartNew();
                    RenderCi(probe, image, candidate, width, height, plan);
                    candidateTimes[run] = watch.Elapsed.TotalMilliseconds;
                }

                report.WriteLine(Timeline($"convert_{plan.Label}_bitmap", bitmapTimes, $"encoding={plan.Encoding} out={width}x{height}"));
                report.WriteLine(Timeline($"convert_{plan.Label}_ci", candidateTimes, $"encoding={plan.Encoding} out={width}x{height}"));
                var difference = Compare(candidate, stride, bitmap, stride, width, height, plan.Half);
                var (rows, flipMean) = DetectRows(candidate, stride, bitmap, stride, width, height, plan.Half, difference.Mean);
                report.WriteLine(DifferenceLine($"compare convert_{plan.Label}", difference) + $" rows={rows} flip_mean={flipMean:0.000000}");
                double limit = plan.Half ? HalfMeanLimit : P3MeanLimit;
                bool quality = rows != "flipped" && difference.Samples > 0 && difference.Mean <= limit;
                report.WriteLine($"verdict convert_{plan.Label} candidate={(quality ? "within-tolerance" : "outside-tolerance")} " +
                    $"mean_limit={limit} note=probe-only");
                ok &= quality;
            }
            finally
            {
                ImageBufferPool.Return(candidate);
                ImageBufferPool.Return(bitmap);
            }
        }

        return ok;
    }

    /// <summary>RAW：独立新实例（产品口径）对比单实例 proxy→full（每轮都新建实例，避免跨轮加热）。</summary>
    private static bool MeasureRaw(ReportWriter report, ProbeContext probe, string path)
    {
        using var pool = new NSAutoreleasePool();
        using var url = NSUrl.FromFilename(path);
        int nativeWidth, nativeHeight, orientation;
        using (var metadata = CreateRawFilter(url))
        {
            var size = metadata.NativeSize;
            nativeWidth = (int)Math.Round(size.Width);
            nativeHeight = (int)Math.Round(size.Height);
            orientation = (int)metadata.Orientation;
        }

        if (nativeWidth <= 0 || nativeHeight <= 0) throw new InvalidOperationException("RAW 原生尺寸无效");
        int nativeLongEdge = Math.Max(nativeWidth, nativeHeight);
        report.WriteLine($"== raw {Path.GetFileName(path)} native={nativeWidth}x{nativeHeight} orientation={orientation} " +
            $"T={TargetLongEdge} scale={(float)TargetLongEdge / nativeLongEdge:0.0000}");
        bool ok = true;
        foreach (var plan in Plans(probe))
        {
            bool keepFullPixels = KeepFullPixels(nativeWidth, nativeHeight, plan.Half);
            string fullMode = keepFullPixels ? "pixel" : "grid64";
            var cold = RunRawScenario(probe, url, plan, nativeLongEdge, keepFullPixels, reuseInstance: false);
            try
            {
                report.WriteLine(Timeline($"raw_cold_{plan.Label}_create_proxy", cold.Timing.CreateProxy, "independent instance"));
                report.WriteLine(Timeline($"raw_cold_{plan.Label}_create_full", cold.Timing.CreateFull, "independent instance"));
                report.WriteLine(Timeline($"raw_cold_{plan.Label}_proxy", cold.Timing.Proxy, $"out={cold.Proxy.Width}x{cold.Proxy.Height} compare=grid64"));
                report.WriteLine(Timeline($"raw_cold_{plan.Label}_full", cold.Timing.Full, $"out={cold.Full.Width}x{cold.Full.Height} compare={fullMode}"));
                report.WriteLine(Timeline($"raw_cold_{plan.Label}_total", cold.Timing.Total, "proxy+full, separate instances"));
                ReportRawMemory(report, "cold", plan.Label, cold.Timing);

                var reuse = RunRawScenario(probe, url, plan, nativeLongEdge, keepFullPixels, reuseInstance: true);
                try
                {
                    report.WriteLine(Timeline($"raw_reuse_{plan.Label}_create", reuse.Timing.CreateProxy, "new instance per round"));
                    report.WriteLine(Timeline($"raw_reuse_{plan.Label}_proxy", reuse.Timing.Proxy, $"out={reuse.Proxy.Width}x{reuse.Proxy.Height} compare=grid64"));
                    report.WriteLine(Timeline($"raw_reuse_{plan.Label}_full", reuse.Timing.Full, $"out={reuse.Full.Width}x{reuse.Full.Height} compare={fullMode}"));
                    report.WriteLine(Timeline($"raw_reuse_{plan.Label}_total", reuse.Timing.Total, "one instance, scale changed proxy->full"));
                    ReportRawMemory(report, "reuse", plan.Label, reuse.Timing);

                    if (cold.Proxy.Width != reuse.Proxy.Width || cold.Proxy.Height != reuse.Proxy.Height ||
                        cold.Full.Width != reuse.Full.Width || cold.Full.Height != reuse.Full.Height)
                    {
                        ok = false;
                        report.WriteLine($"FAIL raw_{plan.Label}_geometry cold={cold.Proxy.Width}x{cold.Proxy.Height}/{cold.Full.Width}x{cold.Full.Height} " +
                            $"reuse={reuse.Proxy.Width}x{reuse.Proxy.Height}/{reuse.Full.Width}x{reuse.Full.Height}");
                    }

                    if (cold.Full.Width != nativeWidth || cold.Full.Height != nativeHeight)
                    {
                        ok = false;
                        report.WriteLine($"FAIL raw_full_{plan.Label}_native out={cold.Full.Width}x{cold.Full.Height} native={nativeWidth}x{nativeHeight}");
                    }

                    if (Math.Max(cold.Proxy.Width, cold.Proxy.Height) < Math.Min(TargetLongEdge, nativeLongEdge))
                    {
                        ok = false;
                        report.WriteLine($"FAIL raw_proxy_{plan.Label}_longEdge out={cold.Proxy.Width}x{cold.Proxy.Height} T={TargetLongEdge}");
                    }

                    ok &= CompareRawOutput(report, "raw_proxy", plan.Label, cold.Proxy, reuse.Proxy, plan.Half, "grid64");
                    ok &= CompareRawOutput(report, "raw_full", plan.Label, cold.Full, reuse.Full, plan.Half, fullMode);
                }
                finally
                {
                    reuse.Proxy.Dispose();
                    reuse.Full.Dispose();
                }
            }
            finally
            {
                cold.Proxy.Dispose();
                cold.Full.Dispose();
            }

            report.WriteLine($"raw_memory final_{plan.Label} after_dispose_all={Megabytes(Footprint())} pool_idle={Megabytes(ImageBufferPool.IdleBytes)}");
            report.Flush();
        }

        return ok;
    }

    private static bool CompareRawOutput(ReportWriter report, string prefix, string label, RawOutput cold, RawOutput reuse,
        bool half, string mode)
    {
        Difference difference;
        double flipMean;
        if (cold.Pixels != null && reuse.Pixels != null)
        {
            difference = Compare(reuse.Pixels, reuse.Stride, cold.Pixels, cold.Stride, cold.Width, cold.Height, half);
            var (_, detected) = DetectRows(reuse.Pixels, reuse.Stride, cold.Pixels, cold.Stride,
                cold.Width, cold.Height, half, difference.Mean);
            flipMean = detected;
        }
        else
        {
            difference = CompareSignatures(cold, reuse, out flipMean);
        }

        string rows = IsFlipped(difference.Mean, flipMean, half) ? "flipped" : "direct";
        report.WriteLine(DifferenceLine($"compare {prefix}_{label}", difference) + $" rows={rows} flip_mean={flipMean:0.000000} mode={mode}");
        double limit = half ? HalfMeanLimit : P3MeanLimit;
        bool ok = rows != "flipped" && difference.Samples > 0 && difference.Mean <= limit;
        report.WriteLine($"verdict {prefix}_{label} reuse={(ok ? "same-pixels" : "changed-pixels")} mean_limit={limit} note=probe-only");
        return ok;
    }

    private static void ReportRawMemory(ReportWriter report, string scenario, string label, RawTiming timing)
        => report.WriteLine($"raw_memory {scenario}_{label} before={Megabytes(timing.BeforeFootprint)} retained={Megabytes(timing.RetainedFootprint)} " +
            $"released_after_filters={Megabytes(timing.ReleasedFootprint)} pool_before={Megabytes(timing.BeforePool)} " +
            $"pool_retained={Megabytes(timing.RetainedPool)} pool_released={Megabytes(timing.ReleasedPool)}");

    private static (RawOutput Proxy, RawOutput Full, RawTiming Timing) RunRawScenario(
        ProbeContext probe, NSUrl url, EncodingPlan plan, int nativeLongEdge, bool keepFullPixels, bool reuseInstance)
    {
        var timing = new RawTiming();
        timing.BeforeFootprint = Footprint();
        timing.BeforePool = ImageBufferPool.IdleBytes;
        RawOutput? proxy = null;
        RawOutput? full = null;

        void RenderProxy(CIRawFilter filter, int run)
        {
            var watch = Stopwatch.StartNew();
            using var output = CreateRawOutput(filter, TargetLongEdge, nativeLongEdge);
            var extent = output.Extent;
            byte[] buffer = RenderRaw(probe, output, extent, plan, out int width, out int height, out int stride);
            timing.Proxy[run] = watch.Elapsed.TotalMilliseconds;
            try
            {
                if (run == 0) proxy = CaptureSignature(buffer, width, height, stride, plan.Half);
            }
            finally
            {
                ImageBufferPool.Return(buffer);
            }
        }

        void RenderFull(CIRawFilter filter, int run)
        {
            var watch = Stopwatch.StartNew();
            using var output = filter.OutputImage ?? throw new InvalidOperationException("RAW 输出图为空");
            var extent = output.Extent;
            byte[] buffer = RenderRaw(probe, output, extent, plan, out int width, out int height, out int stride);
            timing.Full[run] = watch.Elapsed.TotalMilliseconds;
            if (run == 0)
            {
                // 实例与输出都还活着时采 footprint，才反映常驻成本。
                timing.RetainedFootprint = Footprint();
                timing.RetainedPool = ImageBufferPool.IdleBytes;
                if (keepFullPixels)
                {
                    full = RawOutput.FromPixels(buffer, width, height, stride, plan.Half);
                }
                else
                {
                    full = CaptureSignature(buffer, width, height, stride, plan.Half);
                    ImageBufferPool.Return(buffer);
                }
            }
            else
            {
                ImageBufferPool.Return(buffer);
            }
        }

        try
        {
            for (int run = 0; run < Runs; run++)
            {
                using var pool = new NSAutoreleasePool();
                var total = Stopwatch.StartNew();
                if (reuseInstance)
                {
                    var create = Stopwatch.StartNew();
                    using var filter = CreateRawFilter(url);
                    filter.Orientation = CGImagePropertyOrientation.Up;
                    timing.CreateProxy[run] = timing.CreateFull[run] = create.Elapsed.TotalMilliseconds;
                    RenderProxy(filter, run);
                    filter.ScaleFactor = 1f;
                    RenderFull(filter, run);
                }
                else
                {
                    // 产品口径：proxy 与 full 各自独立新实例，绝不跨档复用。
                    var createProxy = Stopwatch.StartNew();
                    using (var filter = CreateRawFilter(url))
                    {
                        filter.Orientation = CGImagePropertyOrientation.Up;
                        timing.CreateProxy[run] = createProxy.Elapsed.TotalMilliseconds;
                        RenderProxy(filter, run);
                    }

                    var createFull = Stopwatch.StartNew();
                    using (var filter = CreateRawFilter(url))
                    {
                        filter.Orientation = CGImagePropertyOrientation.Up;
                        filter.ScaleFactor = 1f;
                        timing.CreateFull[run] = createFull.Elapsed.TotalMilliseconds;
                        RenderFull(filter, run);
                    }
                }

                timing.Total[run] = total.Elapsed.TotalMilliseconds;
            }

            timing.ReleasedFootprint = Footprint();
            timing.ReleasedPool = ImageBufferPool.IdleBytes;
        }
        catch
        {
            proxy?.Dispose();
            full?.Dispose();
            throw;
        }

        return (proxy!, full!, timing);
    }

    private static CIRawFilter CreateRawFilter(NSUrl url)
    {
        var filter = CIRawFilter.Create(url) ?? throw new InvalidOperationException("系统不支持该 RAW 格式或相机机型");
        filter.DraftModeEnabled = false;
        return filter;
    }

    private static CIImage CreateRawOutput(CIRawFilter filter, int limit, int nativeLongEdge)
    {
        if (limit <= 0 || limit >= nativeLongEdge)
        {
            filter.ScaleFactor = 1f;
            return filter.OutputImage ?? throw new InvalidOperationException("RAW 输出图为空");
        }

        float scale = (float)limit / nativeLongEdge;
        for (int attempt = 0; ; attempt++)
        {
            filter.ScaleFactor = scale;
            var image = filter.OutputImage ?? throw new InvalidOperationException("RAW 输出图为空");
            var extent = image.Extent;
            int longEdge = (int)Math.Round(Math.Max(extent.Width, extent.Height));
            if (longEdge <= 0)
            {
                image.Dispose();
                throw new InvalidOperationException("RAW 缩放输出尺寸无效");
            }

            if (longEdge >= limit || scale >= 1f || attempt >= 3) return image;
            image.Dispose();
            scale = Math.Min(1f, scale * ((limit + 0.5f) / longEdge));
        }
    }

    private static unsafe byte[] RenderRaw(ProbeContext probe, CIImage image, CGRect extent, EncodingPlan plan,
        out int width, out int height, out int stride)
    {
        width = (int)Math.Round(extent.Width);
        height = (int)Math.Round(extent.Height);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("RAW 输出尺寸无效");
        stride = checked(width * plan.BytesPerPixel);
        var buffer = ImageBufferPool.Rent(checked(stride * height));
        try
        {
            fixed (byte* pixels = buffer)
                probe.Context.RenderToBitmap(image, (IntPtr)pixels, (IntPtr)stride, extent, plan.Format, plan.Space);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }

        return buffer;
    }

    private static unsafe void RenderCi(ProbeContext probe, CGImage image, byte[] buffer, int width, int height, EncodingPlan plan)
    {
        int stride = checked(width * plan.BytesPerPixel);
        if (buffer.LongLength < (long)stride * height) throw new InvalidOperationException("像素缓冲不足");
        CGColorSpace? owned = null;
        var space = image.ColorSpace;
        if (space == null)
        {
            owned = CGColorSpace.CreateWithName(CGColorSpaceNames.Srgb) ?? throw new InvalidOperationException("sRGB 色彩空间不可用");
            space = owned;
        }

        try
        {
            using var ci = CIImage.FromCGImage(image, space);
            var extent = ci.Extent;
            if (extent.X != 0 || extent.Y != 0 ||
                (int)Math.Round(extent.Width) != width || (int)Math.Round(extent.Height) != height)
                throw new InvalidOperationException($"CIImage 几何不符：{extent} 期望 0,0,{width},{height}");
            fixed (byte* pixels = buffer)
                probe.Context.RenderToBitmap(ci, (IntPtr)pixels, (IntPtr)stride, extent, plan.Format, plan.Space);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>与产品 ImageIO 路径同参数的 CGImage 绘制（每次新建色彩空间与位图上下文）。</summary>
    private static unsafe void DrawBitmap(CGImage image, byte[] buffer, int width, int height, PixelEncoding encoding)
    {
        int stride = checked(width * (encoding == PixelEncoding.RgbaF16 ? 8 : 4));
        if (buffer.LongLength < (long)stride * height) throw new InvalidOperationException("像素缓冲不足");
        fixed (byte* pixels = buffer)
        {
            if (encoding == PixelEncoding.RgbaF16)
            {
                using var space = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
                    ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
                using var context = new CGBitmapContext((IntPtr)pixels, width, height, 16, stride, space,
                    CGBitmapFlags.FloatComponents | CGBitmapFlags.ByteOrder16Little | CGBitmapFlags.PremultipliedLast);
                context.SetBlendMode(CGBlendMode.Copy);
                context.DrawImage(new CGRect(0, 0, width, height), image);
            }
            else
            {
                using var space = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
                    ?? throw new InvalidOperationException("系统不支持 Display P3");
                using var context = new CGBitmapContext((IntPtr)pixels, width, height, 8, stride, space, CGImageAlphaInfo.PremultipliedLast);
                context.SetBlendMode(CGBlendMode.Copy);
                context.DrawImage(new CGRect(0, 0, width, height), image);
            }
        }
    }

    private static CGImageSource OpenSource(NSUrl url)
        => CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
            ?? throw new InvalidOperationException("ImageIO 无法打开图片");

    private static CGImage CreateThumbnail(CGImageSource source)
        => source.CreateThumbnail(0, new CGImageThumbnailOptions
        {
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = false,
            MaxPixelSize = TargetLongEdge,
            ShouldCacheImmediately = true,
        }) ?? throw new InvalidOperationException("ImageIO 缩解码失败");

    /// <summary>镜像产品逻辑：无内嵌 ICC 的 RGB JPEG 按 EXIF/互操作标签解释，候选不得默认 sRGB。</summary>
    private static CGImage? InterpretUntaggedJpeg(CGImage image, CoreGraphics.CGImageProperties? properties, string path)
    {
        if (!IsJpeg(path)) return null;
        if (HasEmbeddedJpegProfile(path) || image.ColorSpace?.Model != CGColorSpaceModel.RGB) return null;
        var inferred = InferJpegColorSpace(properties);
        using var space = CGColorSpace.CreateWithName(inferred == ImageColorSpace.AdobeRgb
            ? CGColorSpaceNames.AdobeRgb1998
            : CGColorSpaceNames.Srgb) ?? throw new InvalidOperationException("无法确定 JPEG 照片的色彩空间");
        return image.WithColorSpace(space) ?? throw new InvalidOperationException("无法正确解释 JPEG 照片的色彩");
    }

    private static string DescribeJpeg(string path, CoreGraphics.CGImageProperties? properties)
    {
        if (!IsJpeg(path)) return "n/a";
        if (HasEmbeddedJpegProfile(path)) return "embedded-icc";
        return InferJpegColorSpace(properties) == ImageColorSpace.AdobeRgb ? "inferred-adobe" : "inferred-srgb";
    }

    private static ImageColorSpace InferJpegColorSpace(CoreGraphics.CGImageProperties? properties)
    {
        using var exifKey = new NSString("{Exif}");
        using var colorKey = new NSString("ColorSpace");
        using var interopKey = new NSString("{ExifInteroperability}");
        using var indexKey = new NSString("InteroperabilityIndex");
        var exif = properties?.Dictionary[exifKey] as NSDictionary;
        var interop = properties?.Dictionary[interopKey] as NSDictionary;
        return ColorEngine.InferJpegColorSpace((exif?[colorKey] as NSNumber)?.Int32Value, interop?[indexKey]?.ToString());
    }

    private static bool HasEmbeddedJpegProfile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) throw new InvalidDataException("JPEG 文件头无效");
        Span<byte> signature = stackalloc byte[12];
        while (stream.Position < stream.Length)
        {
            if (stream.ReadByte() != 0xff) throw new InvalidDataException("JPEG 标记无效");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker is 0xda or 0xd9) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            int high = stream.ReadByte(), low = stream.ReadByte();
            if (high < 0 || low < 0) throw new InvalidDataException("JPEG 段长度缺失");
            int length = (high << 8 | low) - 2;
            long next = stream.Position + length;
            if (length < 0 || next > stream.Length) throw new InvalidDataException("JPEG 段超出文件");
            if (marker == 0xe2 && length >= signature.Length)
            {
                stream.ReadExactly(signature);
                if (signature.SequenceEqual("ICC_PROFILE\0"u8)) return true;
            }

            stream.Position = next;
        }

        throw new InvalidDataException("JPEG 扫描头部不完整");
    }

    private static Candidate[] Discover(string directory)
    {
        var paths = new List<string>();
        paths.AddRange(EnumerateImages(directory));
        foreach (string subdirectory in Directory.EnumerateDirectories(directory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            try
            {
                paths.AddRange(EnumerateImages(subdirectory));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[DecodeOptimize] 跳过不可读子目录 {subdirectory}: {exception.Message}");
            }
        }

        var items = paths.Distinct(StringComparer.Ordinal).Select(ProbeCandidate).ToList();
        return [.. Select(items, "jpeg", preferUntaggedAdobe: true), .. Select(items, "heic", false), .. Select(items, "raw", false)];
    }

    private static IEnumerable<string> EnumerateImages(string directory)
        => Directory.EnumerateFiles(directory)
            .Where(path => IsJpeg(path) || IsHeic(path) || IsRaw(path))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);

    private static Candidate ProbeCandidate(string path)
    {
        string kind = IsJpeg(path) ? "jpeg" : IsHeic(path) ? "heic" : "raw";
        int longEdge = 0;
        bool untaggedAdobe = false;
        try
        {
            using var pool = new NSAutoreleasePool();
            using var url = NSUrl.FromFilename(path);
            using var source = OpenSource(url);
            var properties = source.GetProperties(0, null);
            longEdge = Math.Max(properties?.PixelWidth ?? 0, properties?.PixelHeight ?? 0);
            untaggedAdobe = kind == "jpeg" && !HasEmbeddedJpegProfile(path)
                && InferJpegColorSpace(properties) == ImageColorSpace.AdobeRgb;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DecodeOptimize] 读取属性失败 {Path.GetFileName(path)}: {exception.Message}");
        }

        return new Candidate(path, kind, longEdge, untaggedAdobe);
    }

    /// <summary>JPEG 固定覆盖最大图与真实无 ICC Adobe 图；HEIC/RAW 按长边取最大两张。</summary>
    private static IEnumerable<Candidate> Select(List<Candidate> items, string kind, bool preferUntaggedAdobe)
    {
        var ordered = items.Where(x => x.Kind == kind)
            .OrderByDescending(x => x.LongEdge)
            .ThenBy(x => Path.GetFileName(x.Path), StringComparer.Ordinal)
            .ToList();
        var chosen = new List<Candidate>();
        if (ordered.Count > 0) chosen.Add(ordered[0]);
        if (preferUntaggedAdobe)
        {
            var untagged = ordered.FirstOrDefault(x => x.UntaggedAdobe && !chosen.Contains(x));
            if (untagged != null) chosen.Add(untagged);
        }

        foreach (var item in ordered)
        {
            if (chosen.Count >= MaxPerClass) break;
            if (!chosen.Contains(item)) chosen.Add(item);
        }

        return chosen.Take(MaxPerClass);
    }

    private static bool IsJpeg(string path) => HasExtension(path, JpegExtensions);
    private static bool IsHeic(string path) => HasExtension(path, HeicExtensions);
    private static bool IsRaw(string path) => HasExtension(path, RawExtensions);

    private static bool HasExtension(string path, string[] extensions)
        => Array.Exists(extensions, extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool KeepFullPixels(int width, int height, bool half)
        => 2L * width * height * (half ? 8 : 4) <= ComparePairBudget;

    private static EncodingPlan[] Plans(ProbeContext probe) =>
    [
        new(PixelEncoding.RgbaF16, "f16", 8, probe.RgbahFormat, probe.LinearSrgb),
        new(PixelEncoding.Rgba8, "p3", 4, probe.Rgba8Format, probe.DisplayP3),
    ];

    private static RawOutput CaptureSignature(byte[] pixels, int width, int height, int stride, bool half)
    {
        int[] columns = SampleCoordinates(width, 64);
        int[] rows = SampleCoordinates(height, 64);
        var signature = new float[columns.Length * rows.Length * 3];
        int index = 0;
        if (half)
        {
            var halves = MemoryMarshal.Cast<byte, Half>(pixels);
            foreach (int y in rows)
                foreach (int x in columns)
                {
                    int offset = y * (stride / 2) + x * 4;
                    for (int channel = 0; channel < 3; channel++) signature[index++] = (float)halves[offset + channel];
                }
        }
        else
        {
            foreach (int y in rows)
                foreach (int x in columns)
                {
                    int offset = y * stride + x * 4;
                    for (int channel = 0; channel < 3; channel++) signature[index++] = pixels[offset + channel];
                }
        }

        return RawOutput.FromSignature(signature, columns.Length, rows.Length, width, height, stride, half);
    }

    private static int[] SampleCoordinates(int length, int count)
    {
        int size = Math.Min(length, count);
        var coordinates = new int[size];
        for (int index = 0; index < size; index++)
            coordinates[index] = size == 1 ? 0 : (int)Math.Round(index * (length - 1.0) / (size - 1));
        return coordinates;
    }

    private static Difference CompareSignatures(RawOutput reference, RawOutput candidate, out double flipMean)
    {
        float[] r = reference.Signature ?? throw new InvalidOperationException("参考输出缺少签名");
        float[] c = candidate.Signature ?? throw new InvalidOperationException("候选输出缺少签名");
        if (r.Length != c.Length) throw new InvalidOperationException("抽样签名长度不一致");
        int rows = reference.SignatureRows, columns = reference.SignatureColumns;
        double sum = 0, max = 0, flip = 0;
        long samples = 0, extended = 0;
        for (int y = 0; y < rows; y++)
        {
            int flipY = rows - 1 - y;
            for (int x = 0; x < columns; x++)
            {
                int index = (y * columns + x) * 3;
                int flipIndex = (flipY * columns + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    double rv = r[index + channel], cv = c[index + channel];
                    double difference = Math.Abs(cv - rv);
                    sum += difference;
                    if (difference > max) max = difference;
                    if (reference.Half && (rv < 0 || rv > 1)) extended++;
                    flip += Math.Abs(c[flipIndex + channel] - rv);
                    samples++;
                }
            }
        }

        flipMean = samples == 0 ? 0 : flip / samples;
        return new Difference(samples == 0 ? 0 : sum / samples, max, samples, extended);
    }

    /// <summary>F16 线性分量比较保留扩展色域（小于 0 或大于 1）的原始值；8 位直接比较码值。</summary>
    private static Difference Compare(byte[] candidate, int candidateStride, byte[] reference, int referenceStride,
        int width, int height, bool half)
    {
        double sum = 0, max = 0;
        long samples = 0, extended = 0;
        if (half)
        {
            var c = MemoryMarshal.Cast<byte, Half>(candidate);
            var r = MemoryMarshal.Cast<byte, Half>(reference);
            for (int y = 0; y < height; y++)
            {
                int ci = y * (candidateStride / 2), ri = y * (referenceStride / 2);
                for (int x = 0; x < width; x++, ci += 4, ri += 4)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double cv = (float)c[ci + channel], rv = (float)r[ri + channel];
                        double difference = Math.Abs(cv - rv);
                        sum += difference;
                        if (difference > max) max = difference;
                        if (rv < 0 || rv > 1) extended++;
                        samples++;
                    }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int ci = y * candidateStride, ri = y * referenceStride;
                for (int x = 0; x < width; x++, ci += 4, ri += 4)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double difference = Math.Abs(candidate[ci + channel] - reference[ri + channel]);
                        sum += difference;
                        if (difference > max) max = difference;
                        samples++;
                    }
            }
        }

        return new Difference(samples == 0 ? 0 : sum / samples, max, samples, extended);
    }

    /// <summary>按上/中/下行抽样判断候选是否整体上下翻转（CI 与位图上下轴约定不同会在这里暴露）。</summary>
    private static (string Rows, double FlipMean) DetectRows(byte[] candidate, int candidateStride, byte[] reference,
        int referenceStride, int width, int height, bool half, double directMean)
    {
        int rows = Math.Min(7, height);
        int stepX = Math.Max(1, width / 96);
        double flipped = 0;
        long samples = 0;
        ReadOnlySpan<Half> candidateHalves = default;
        ReadOnlySpan<Half> referenceHalves = default;
        if (half)
        {
            candidateHalves = MemoryMarshal.Cast<byte, Half>(candidate);
            referenceHalves = MemoryMarshal.Cast<byte, Half>(reference);
        }

        for (int index = 0; index < rows; index++)
        {
            int y = rows == 1 ? 0 : (int)Math.Round(index * (height - 1.0) / (rows - 1));
            int flippedY = height - 1 - y;
            for (int x = 0; x < width; x += stepX)
                for (int channel = 0; channel < 3; channel++)
                {
                    double cv = half
                        ? (float)candidateHalves[y * (candidateStride / 2) + x * 4 + channel]
                        : candidate[y * candidateStride + x * 4 + channel];
                    double rv = half
                        ? (float)referenceHalves[flippedY * (referenceStride / 2) + x * 4 + channel]
                        : reference[flippedY * referenceStride + x * 4 + channel];
                    flipped += Math.Abs(cv - rv);
                    samples++;
                }
        }

        double flipMean = samples == 0 ? 0 : flipped / samples;
        return (IsFlipped(directMean, flipMean, half) ? "flipped" : "direct", flipMean);
    }

    private static bool IsFlipped(double directMean, double flipMean, bool half)
        => directMean > (half ? HalfMeanLimit : P3MeanLimit) && flipMean < directMean * 0.25;

    private static string DifferenceLine(string name, Difference difference)
        => $"{name} mean={difference.Mean.ToString("0.000000", CultureInfo.InvariantCulture)} " +
           $"max={difference.Max.ToString("0.000000", CultureInfo.InvariantCulture)} " +
           $"samples={difference.Samples} extended={difference.Extended}";

    private static string Timeline(string name, double[] times, string extra)
        => $"{name} first={times[0]:0.0}ms median={Median(times):0.0}ms " +
           $"all={string.Join("/", times.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)))} {extra}".TrimEnd();

    private static double Median(double[] times)
    {
        var sorted = (double[])times.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static long Footprint() => AppleMemoryFootprint.GetPhysFootprintBytes();

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    private static string PlatformName()
        => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsIOS() ? "iOS" : RuntimeInformation.OSDescription;

    private sealed class ProbeContext : IDisposable
    {
        public ProbeContext()
        {
            LinearSrgb = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
                ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
            DisplayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
                ?? throw new InvalidOperationException("系统不支持 Display P3");
            Context = new CIContext(new CIContextOptions { WorkingColorSpace = LinearSrgb, CacheIntermediates = false });
            RgbahFormat = CIFormat.Rgbah.GetConstant() ?? throw new InvalidOperationException("RGBAh 原生格式不可用");
            Rgba8Format = CIFormat.Rgba8.GetConstant() ?? throw new InvalidOperationException("RGBA8 原生格式不可用");
        }

        public CGColorSpace LinearSrgb { get; }
        public CGColorSpace DisplayP3 { get; }
        public CIContext Context { get; }
        public int RgbahFormat { get; }
        public int Rgba8Format { get; }

        public void Dispose()
        {
            Context.Dispose();
            DisplayP3.Dispose();
            LinearSrgb.Dispose();
        }
    }

    private readonly record struct EncodingPlan(PixelEncoding Encoding, string Label, int BytesPerPixel, int Format, CGColorSpace Space)
    {
        public bool Half => Encoding == PixelEncoding.RgbaF16;
    }

    private readonly record struct Difference(double Mean, double Max, long Samples, long Extended);

    private sealed record Candidate(string Path, string Kind, int LongEdge, bool UntaggedAdobe);

    private sealed class RawTiming
    {
        public readonly double[] CreateProxy = new double[Runs];
        public readonly double[] CreateFull = new double[Runs];
        public readonly double[] Proxy = new double[Runs];
        public readonly double[] Full = new double[Runs];
        public readonly double[] Total = new double[Runs];
        public long BeforeFootprint;
        public long BeforePool;
        public long RetainedFootprint;
        public long RetainedPool;
        public long ReleasedFootprint;
        public long ReleasedPool;
    }

    private sealed class RawOutput : IDisposable
    {
        public int Width;
        public int Height;
        public int Stride;
        public byte[]? Pixels;
        public float[]? Signature;
        public bool Half;

        public static RawOutput FromPixels(byte[] pixels, int width, int height, int stride, bool half)
            => new() { Pixels = pixels, Width = width, Height = height, Stride = stride, Half = half };

        public static RawOutput FromSignature(float[] signature, int columns, int rows, int width, int height, int stride, bool half)
            => new()
            {
                Signature = signature,
                SignatureColumns = columns,
                SignatureRows = rows,
                Width = width,
                Height = height,
                Stride = stride,
                Half = half,
            };

        public int SignatureColumns { get; private init; }
        public int SignatureRows { get; private init; }

        public void Dispose()
        {
            if (Pixels == null) return;
            ImageBufferPool.Return(Pixels);
            Pixels = null;
        }
    }

    private sealed class ReportWriter(string path)
    {
        private readonly StringBuilder _content = new();

        public void WriteLine(string line)
        {
            _content.Append(line).Append('\n');
            if (line.StartsWith("==", StringComparison.Ordinal) || line.StartsWith("SUMMARY", StringComparison.Ordinal)
                || line.StartsWith("FAIL", StringComparison.Ordinal) || line.StartsWith("ERROR", StringComparison.Ordinal)
                || line.StartsWith("verdict", StringComparison.Ordinal))
                Console.WriteLine($"[DecodeOptimize] {line}");
        }

        public void Flush()
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, _content.ToString());
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[DecodeOptimize] 报告写入失败：{exception.Message}");
            }
        }
    }
}
