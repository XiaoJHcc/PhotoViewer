using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using CoreImage;
using Foundation;
using ImageIO;
using Metal;
using ObjCRuntime;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Shared;

namespace PhotoViewer.Apple;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-perf-probe &lt;目录&gt;</c> 对目录内前 6 张图片
/// （.heic/.heif/.jpg/.jpeg/.arw，按文件名排序）分段计时：属性读取、缩略图、全图解码、
/// 两种展示编码的位图绘制、ImageIO 子采样、CIRAWFilter、Metal 上传、直方图、增强与
/// 分析用 sRGB 转换。每项跑三次，报告首次（冷）与中位数，并写纯文本报告；产品路径不调用。
/// 只依赖 Foundation/CoreGraphics/CoreImage/ImageIO/Metal 与共享 PhotoViewer 类型。
/// </summary>
public static class ApplePerfProbe
{
    /// <summary>目标长边：iPad mini 横屏物理长边 2266 × 2 超采样；Mac 用同一值便于对比。</summary>
    private const int TargetLongEdge = 2266 * 2;

    /// <summary>每项测量次数。</summary>
    private const int Runs = 3;

    /// <summary>参与探针的图片扩展名。</summary>
    private static readonly string[] SupportedExtensions = [".heic", ".heif", ".jpg", ".jpeg", ".arw"];

    /// <summary>对目录内图片执行分段计时并写报告。</summary>
    /// <param name="directory">图片目录。</param>
    /// <param name="reportPath">报告文本路径（目录不存在时创建）。</param>
    /// <param name="memoryProbe">读取当前进程 footprint 与可用字节；null 表示不记录内存。</param>
    /// <returns>探针完成的任务。</returns>
    public static Task RunAsync(string directory, string reportPath, Func<(long footprint, long available)>? memoryProbe)
        => Task.Run(() => Run(directory, reportPath, memoryProbe), CancellationToken.None);

    /// <summary>读取目录、逐图计时并持续落盘（便于外部轮询报告文件）。</summary>
    private static void Run(string directory, string reportPath, Func<(long footprint, long available)>? memoryProbe)
    {
        var report = new StringBuilder();
        var (footprint, available) = ProbeMemory(memoryProbe);
        report.Append("PhotoViewer perf probe platform=").Append(PlatformName())
            .Append(" machine=").Append(MachineIdentifier())
            .Append(" cpus=").Append(Environment.ProcessorCount)
            .Append(" T=").Append(TargetLongEdge)
            .Append(" footprint=").Append(Megabytes(footprint))
            .Append(" available=").Append(ReadableAvailable(available))
            .AppendLine();

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Where(path => Array.Exists(SupportedExtensions, extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(6)
                .ToArray();
        }
        catch (Exception exception)
        {
            report.AppendLine($"directory ERROR {exception.GetType().Name}: {exception.Message}");
            WriteReport(reportPath, report);
            return;
        }

        report.AppendLine($"directory={directory} files={files.Length}");
        WriteReport(reportPath, report);

        Console.WriteLine($"[PerfProbe] start {directory} files={files.Length}");
        bool firstHeic = true;
        foreach (string file in files)
        {
            string extension = Path.GetExtension(file);
            bool isHeic = extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
            bool probeConcurrency = isHeic && firstHeic;
            if (probeConcurrency) firstHeic = false;
            try
            {
                MeasureImage(report, file, memoryProbe, concurrency: probeConcurrency);
            }
            catch (Exception exception)
            {
                report.AppendLine($"== {Path.GetFileName(file)} ERROR {exception.GetType().Name}: {exception.Message}");
            }

            WriteReport(reportPath, report);
            Console.WriteLine($"[PerfProbe] measured {Path.GetFileName(file)}");
        }
    }

    /// <summary>单张图片的全部分段测量；无论成败都释放图像与缓冲，再记录一次内存。</summary>
    private static void MeasureImage(StringBuilder report, string path, Func<(long footprint, long available)>? memoryProbe,
        bool concurrency = false)
    {
        using var pool = new NSAutoreleasePool();
        using var url = NSUrl.FromFilename(path);
        var sources = new List<CGImageSource>();
        CGImage? thumbnail = null;
        CGImage? full = null;
        DecodedImage? f16Proxy = null;
        DecodedImage? p3Proxy = null;
        DecodedImage? fullP3 = null;
        DecodedImage? fullF16 = null;
        CIRawFilter? rawFilter = null;
        CIContext? ciContext = null;
        byte[]? rawBuffer8 = null;
        byte[]? rawBuffer16 = null;
        int sourceWidth = 0, sourceHeight = 0, orientation = 1;
        try
        {
            Metric props = Measure(_ =>
            {
                var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
                    ?? throw new InvalidOperationException("ImageIO 无法打开图片");
                sources.Add(source);
                var properties = source.GetProperties(0, null);
                if (properties != null)
                {
                    sourceWidth = properties.PixelWidth ?? 0;
                    sourceHeight = properties.PixelHeight ?? 0;
                    orientation = properties.Orientation is { } direction ? (int)direction : 1;
                }

                return (sourceWidth, sourceHeight,
                    $"depth={properties?.Depth?.ToString() ?? "-"} model={properties?.ColorModel?.ToString() ?? "-"} " +
                    $"profile={properties?.ProfileName ?? "-"} orientation={orientation}");
            });

            report.AppendLine($"== {Path.GetFileName(path)} {props.Width}x{props.Height} {props.Extra}");
            report.AppendLine(props.Format("props"));
            if (props.Error != null || sources.Count == 0)
            {
                return;
            }

            var source = sources[^1];

            Metric thumbAlways = Measure(_ =>
            {
                var image = source.CreateThumbnail(0, new CGImageThumbnailOptions
                {
                    CreateThumbnailFromImageAlways = true,
                    CreateThumbnailWithTransform = false,
                    MaxPixelSize = TargetLongEdge,
                    ShouldCacheImmediately = true,
                }) ?? throw new InvalidOperationException("缩略图解码失败");
                var previous = thumbnail;
                thumbnail = image;
                previous?.Dispose();
                return ((int)image.Width, (int)image.Height,
                    $"bpc={image.BitsPerComponent} cs={image.ColorSpace?.Name ?? "-"}");
            });
            report.AppendLine(thumbAlways.Format("thumbAlways"));

            Metric thumbIfAbsent = Measure(_ =>
            {
                using var fresh = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
                    ?? throw new InvalidOperationException("ImageIO 无法打开图片");
                var image = fresh.CreateThumbnail(0, new CGImageThumbnailOptions
                {
                    CreateThumbnailFromImageIfAbsent = true,
                    CreateThumbnailWithTransform = false,
                    MaxPixelSize = TargetLongEdge,
                    ShouldCacheImmediately = true,
                }) ?? throw new InvalidOperationException("内嵌预览不可用");
                using (image)
                {
                    return ((int)image.Width, (int)image.Height,
                        $"bpc={image.BitsPerComponent} cs={image.ColorSpace?.Name ?? "-"}");
                }
            });
            report.AppendLine(thumbIfAbsent.Format("thumbIfAbsent"));

            if (thumbnail != null)
            {
                int width = (int)thumbnail.Width, height = (int)thumbnail.Height;
                f16Proxy = DrawMeasured(report, "drawF16", thumbnail, width, height, PixelEncoding.RgbaF16,
                    sourceWidth, sourceHeight, orientation);
                p3Proxy = DrawMeasured(report, "drawP3_8", thumbnail, width, height, PixelEncoding.Rgba8,
                    sourceWidth, sourceHeight, orientation);
            }

            Metric fullCreate = Measure(_ =>
            {
                // 每次用全新源：复用已解码的源会让 CreateImage 命中缓存，测不出全图解码成本。
                using var fresh = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
                    ?? throw new InvalidOperationException("ImageIO 无法打开图片");
                var image = fresh.CreateImage(0, new CGImageOptions { ShouldCacheImmediately = true })
                    ?? throw new InvalidOperationException("全图解码失败");
                var previous = full;
                full = image;
                previous?.Dispose();
                return ((int)image.Width, (int)image.Height,
                    $"bpc={image.BitsPerComponent} cs={image.ColorSpace?.Name ?? "-"}");
            });
            report.AppendLine(fullCreate.Format("fullCreate"));

            if (full != null)
            {
                int width = (int)full.Width, height = (int)full.Height;
                fullP3 = DrawMeasured(report, "fullDrawP3_8", full, width, height, PixelEncoding.Rgba8,
                    sourceWidth, sourceHeight, orientation);
                fullF16 = DrawMeasured(report, "fullDrawF16", full, width, height, PixelEncoding.RgbaF16,
                    sourceWidth, sourceHeight, orientation);
            }

            if (props.Width > TargetLongEdge || props.Height > TargetLongEdge)
            {
                Metric subsample = Measure(_ =>
                {
                    // 全新源：复用已解码的源会把子采样变成缓存命中，看不出编解码器级降采样是否省时。
                    using var fresh = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
                        ?? throw new InvalidOperationException("ImageIO 无法打开图片");
                    using var subsampleKey = new NSString("kCGImageSourceSubsampleFactor");
                    using var cacheKey = new NSString("kCGImageSourceShouldCacheImmediately");
                    using var factor = NSNumber.FromInt32(2);
                    using var cache = NSNumber.FromBoolean(true);
                    using var options = new NSMutableDictionary();
                    options.Add(subsampleKey, factor);
                    options.Add(cacheKey, cache);
                    nint handle = CGImageSourceCreateImageAtIndex(fresh.GetHandle(), 0, options.Handle);
                    if (handle == IntPtr.Zero) throw new InvalidOperationException("子采样解码失败");
                    var image = Runtime.GetINativeObject<CGImage>(handle, true)
                        ?? throw new InvalidOperationException("子采样图像包装失败");
                    using (image)
                    {
                        return ((int)image.Width, (int)image.Height, "subsample=2 decode=immediate");
                    }
                });
                report.AppendLine(subsample.Format("subsample2"));
            }
            else
            {
                report.AppendLine("subsample2 skipped (source <= T)");
            }

            if (Path.GetExtension(path).Equals(".arw", StringComparison.OrdinalIgnoreCase))
            {
                MeasureRaw(report, url, ref rawFilter, ref ciContext, ref rawBuffer8, ref rawBuffer16);
                MeasureRawProduct(report, path);
                try
                {
                    MeasureRawPipeline(report, path);
                }
                catch (Exception exception)
                {
                    report.AppendLine($"rawPipe ERROR {exception.GetType().Name}: {exception.Message}");
                }
                try
                {
                    MeasureDecodeConcurrency(report, path, lanes: new[] { 1, 2 });
                }
                catch (Exception exception)
                {
                    report.AppendLine($"decodeConcurrency ERROR {exception.GetType().Name}: {exception.Message}");
                }
            }

            if (concurrency)
            {
                try
                {
                    MeasureDecodeConcurrency(report, path);
                }
                catch (Exception exception)
                {
                    report.AppendLine($"decodeConcurrency ERROR {exception.GetType().Name}: {exception.Message}");
                }
            }

            MeasureMetal(report, f16Proxy);
            MeasureHistogram(report, "histogramF16", f16Proxy);
            MeasureHistogram(report, "histogramP3", fullP3);

            if (f16Proxy != null) MeasureEnhance(report, "enhanceF16", f16Proxy, ColorEngine.ToScRgb16);
            if (fullP3 != null) MeasureEnhance(report, "enhanceP3", fullP3, ColorEngine.ToDisplayP3);

            if (fullP3 != null)
            {
                Metric analysis = Measure(_ =>
                {
                    using var srgb = ColorEngine.ToAnalysisSrgb8(fullP3);
                    return (srgb.Width, srgb.Height, "Bgra32");
                });
                report.AppendLine(analysis.Format("analysisSrgb"));
            }
        }
        finally
        {
            thumbnail?.Dispose();
            full?.Dispose();
            f16Proxy?.Dispose();
            p3Proxy?.Dispose();
            fullP3?.Dispose();
            fullF16?.Dispose();
            rawFilter?.Dispose();
            ciContext?.Dispose();
            if (rawBuffer8 != null) ImageBufferPool.Return(rawBuffer8);
            if (rawBuffer16 != null) ImageBufferPool.Return(rawBuffer16);
            foreach (var source in sources) source.Dispose();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var (footprint, available) = ProbeMemory(memoryProbe);
            report.AppendLine($"memory footprint={Megabytes(footprint)} available={ReadableAvailable(available)}");
        }
    }

    /// <summary>把缩略图/全图绘制到展示编码缓冲，计时一次并保留最后的池化图像。</summary>
    private static DecodedImage? DrawMeasured(StringBuilder report, string name, CGImage image, int width, int height,
        PixelEncoding encoding, int sourceWidth, int sourceHeight, int orientation)
    {
        int stride = checked(width * DecodedImage.BytesPerPixel(encoding));
        byte[]? buffer = null;
        try
        {
            buffer = DecodedImage.RentPixels(checked(stride * height));
            var target = buffer;
            Metric metric = Measure(_ =>
            {
                if (encoding == PixelEncoding.RgbaF16) DrawLinearF16(image, target, width, height);
                else DrawDisplayP3(image, target, width, height);
                return (width, height, encoding == PixelEncoding.RgbaF16 ? "linearSrgb16f" : "displayP3_8");
            });
            report.AppendLine(metric.Format(name));
            if (metric.Error != null) return null;
            var decoded = encoding == PixelEncoding.RgbaF16
                ? DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16,
                    ImageColorSpace.LinearSrgb, sourceWidth, sourceHeight, orientation)
                : DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.Rgba8,
                    ImageColorSpace.DisplayP3, sourceWidth, sourceHeight, orientation);
            buffer = null;
            return decoded;
        }
        finally
        {
            if (buffer != null) ImageBufferPool.Return(buffer);
        }
    }

    /// <summary>复刻 <c>ApplePresentationDecoder.DecodeLinearF16</c> 的 CGBitmapContext 参数。</summary>
    private static unsafe void DrawLinearF16(CGImage image, byte[] buffer, int width, int height)
    {
        int stride = checked(width * 8);
        using var linearSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
            ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
        fixed (byte* pixels = buffer)
        {
            using var context = new CGBitmapContext((IntPtr)pixels, width, height, 16, stride, linearSpace,
                CGBitmapFlags.FloatComponents | CGBitmapFlags.ByteOrder16Little | CGBitmapFlags.PremultipliedLast);
            context.SetBlendMode(CGBlendMode.Copy);
            context.DrawImage(new CGRect(0, 0, width, height), image);
        }
    }

    /// <summary>复刻 <c>ApplePresentationDecoder.DecodeDisplayP3</c> 的 CGBitmapContext 参数。</summary>
    private static unsafe void DrawDisplayP3(CGImage image, byte[] buffer, int width, int height)
    {
        int stride = checked(width * 4);
        using var displayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
            ?? throw new InvalidOperationException("系统不支持 Display P3");
        fixed (byte* pixels = buffer)
        {
            using var context = new CGBitmapContext((IntPtr)pixels, width, height, 8, stride, displayP3,
                CGImageAlphaInfo.PremultipliedLast);
            context.SetBlendMode(CGBlendMode.Copy);
            context.DrawImage(new CGRect(0, 0, width, height), image);
        }
    }

    /// <summary>RAW 专用：相机列表、CIRawFilter 创建、全尺寸/超采样 8 位 P3 与超采样 F16 渲染。</summary>
    private static void MeasureRaw(StringBuilder report, NSUrl url, ref CIRawFilter? filter, ref CIContext? context,
        ref byte[]? buffer8, ref byte[]? buffer16)
    {
        try
        {
            var cameras = CIRawFilter.SupportedCameraModels;
            int cameraCount = cameras.Length;
            bool sony = Array.Exists(cameras, model => model.Contains("ILCE-7CM2", StringComparison.OrdinalIgnoreCase));
            report.AppendLine($"rawCameraModels supported={cameraCount} ilce7cm2={sony}");

            using var linearSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
                ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
            using var displayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
                ?? throw new InvalidOperationException("系统不支持 Display P3");
            var rawContext = new CIContext(new CIContextOptions
            {
                WorkingColorSpace = linearSpace,
                CacheIntermediates = false,
            });
            context = rawContext;

            CIRawFilter? rawFilter = null;
            Metric create = Measure(_ =>
            {
                var created = CIRawFilter.Create(url) ?? throw new InvalidOperationException("CIRawFilter 不可用");
                created.DraftModeEnabled = false;
                var previous = rawFilter;
                rawFilter = created;
                previous?.Dispose();
                var native = created.NativeSize;
                return ((int)Math.Round(native.Width), (int)Math.Round(native.Height), "draft=false");
            });
            report.AppendLine(create.Format("rawFilterCreate"));
            filter = rawFilter;
            if (create.Error != null || rawFilter == null) return;
            var engine = rawFilter;

            int nativeWidth = Math.Max(1, create.Width), nativeHeight = Math.Max(1, create.Height);
            float scale = (float)TargetLongEdge / Math.Max(nativeWidth, nativeHeight);
            int scaledWidth = Math.Max(1, (int)Math.Round(nativeWidth * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(nativeHeight * scale));
            var pixels8 = DecodedImage.RentPixels(checked(nativeWidth * 4 * nativeHeight));
            buffer8 = pixels8;
            var pixels16 = DecodedImage.RentPixels(checked(scaledWidth * 8 * scaledHeight));
            buffer16 = pixels16;
            Array.Clear(pixels8);
            Array.Clear(pixels16);

            long firstSum8 = 0, firstSum16 = 0;
            Metric rawFull = Measure(run =>
            {
                engine.ScaleFactor = 1f;
                var output = engine.OutputImage ?? throw new InvalidOperationException("RAW 输出为空");
                var extent = output.Extent;
                int width = (int)Math.Round(extent.Width), height = (int)Math.Round(extent.Height);
                RequireCapacity(pixels8, (long)width * 4 * height);
                unsafe
                {
                    fixed (byte* pixels = pixels8)
                        rawContext.RenderToBitmap(output, (nint)pixels, (nint)(width * 4), extent, NativeFormat(CIFormat.Rgba8), displayP3);
                }
                long sum = Checksum(pixels8);
                if (run == 0) firstSum8 = sum;
                return (width, height, $"scale=1.000 native={nativeWidth}x{nativeHeight} sum0={firstSum8} sum={sum}");
            });
            report.AppendLine(rawFull.Format("rawFull"));

            Metric rawScaled = Measure(run =>
            {
                engine.ScaleFactor = scale;
                var output = engine.OutputImage ?? throw new InvalidOperationException("RAW 输出为空");
                var extent = output.Extent;
                int width = (int)Math.Round(extent.Width), height = (int)Math.Round(extent.Height);
                RequireCapacity(pixels8, (long)width * 4 * height);
                unsafe
                {
                    fixed (byte* pixels = pixels8)
                        rawContext.RenderToBitmap(output, (nint)pixels, (nint)(width * 4), extent, NativeFormat(CIFormat.Rgba8), displayP3);
                }
                long sum = Checksum(pixels8);
                if (run == 0) firstSum8 = sum;
                return (width, height, $"scale={scale:0.0000} sum0={firstSum8} sum={sum}");
            });
            report.AppendLine(rawScaled.Format("rawScaled"));

            // CoreImage 的 half 位图输出在本机（macOS 26）静默不写像素：先用合成图探测，
            // 不可用时改走 CGImage 再画进同一 RGBA16F 表面，保证该项仍有真实数值。
            bool halfBitmap = HalfBitmapWrites(rawContext, linearSpace);
            Metric rawScaledF16 = Measure(run =>
            {
                engine.ScaleFactor = scale;
                var output = engine.OutputImage ?? throw new InvalidOperationException("RAW 输出为空");
                var extent = output.Extent;
                int width = (int)Math.Round(extent.Width), height = (int)Math.Round(extent.Height);
                RequireCapacity(pixels16, (long)width * 8 * height);
                string mode;
                if (halfBitmap)
                {
                    unsafe
                    {
                        fixed (byte* pixels = pixels16)
                            rawContext.RenderToBitmap(output, (nint)pixels, (nint)(width * 8), extent, NativeFormat(CIFormat.Rgbah), linearSpace);
                    }
                    mode = "bitmap=RGBAh";
                }
                else
                {
                    using var rendered = rawContext.CreateCGImage(output, extent)
                        ?? throw new InvalidOperationException("RAW 缩放的 CGImage 渲染失败");
                    DrawLinearF16(rendered, pixels16, width, height);
                    mode = "bitmap=CGImage→RGBA16F";
                }
                long sum = Checksum(pixels16);
                if (run == 0) firstSum16 = sum;
                return (width, height, $"scale={scale:0.0000} linearSrgb16f {mode} sum0={firstSum16} sum={sum}");
            });
            report.AppendLine(rawScaledF16.Format("rawScaledF16"));
        }
        catch (Exception exception)
        {
            report.AppendLine($"raw ERROR {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// 探测本机 CoreImage 能否把像素写进 half 位图：用合成纯色小图渲染 RGBAh，检查是否真有字节落盘。
    /// 曾经把这里的空结果误判成系统限制，实际是传了托管枚举值而非原生像素格式；现在两条路径都取原生常量。
    /// </summary>
    private static bool HalfBitmapWrites(CIContext context, CGColorSpace colorSpace)
    {
        const int Size = 32;
        var buffer = new byte[Size * Size * 8];
        var region = new CGRect(0, 0, Size, Size);
        using var image = CIImage.RedImage;
        unsafe
        {
            fixed (byte* pixels = buffer)
                context.RenderToBitmap(image, (nint)pixels, (nint)(Size * 8), region, NativeFormat(CIFormat.Rgbah), colorSpace);
        }

        long sum = 0;
        foreach (byte value in buffer) sum += value;
        return sum != 0;
    }

    /// <summary>
    /// RAW 产品路径分解：创建/元数据/方向/缩放/取输出/渲染各段耗时，以及复用实例、NSData 创建、
    /// 是否设方向、CIContext 选项、输出格式与 ScaleFactor 曲线；并对候选路径与产品路径做逐像素对拍。
    /// 不启用 DraftMode，不降低输出精度。
    /// </summary>
    private static void MeasureRawPipeline(StringBuilder report, string path)
    {
        using var url = NSUrl.FromFilename(path);
        using var linearSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
            ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
        using var displayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
            ?? throw new InvalidOperationException("系统不支持 Display P3");
        int rgba8 = NativeFormat(CIFormat.Rgba8);

        CIRawFilter CreateFilter()
        {
            var filter = CIRawFilter.Create(url) ?? throw new InvalidOperationException("CIRawFilter 不可用");
            filter.DraftModeEnabled = false;
            return filter;
        }

        using var probeFilter = CreateFilter();
        CGSize native = probeFilter.NativeSize;
        int nativeLongEdge = (int)Math.Round(Math.Max(native.Width, native.Height));
        float scale = (float)TargetLongEdge / Math.Max(1, nativeLongEdge);
        int proxyLongEdge = Math.Max(1, (int)Math.Round(nativeLongEdge * scale));
        int proxyWidth = Math.Max(1, (int)Math.Round(native.Width * scale));
        int proxyHeight = Math.Max(1, (int)Math.Round(native.Height * scale));
        report.AppendLine($"== rawPipe {Path.GetFileName(path)} native={native.Width:0}x{native.Height:0} " +
            $"orientation={(int)probeFilter.Orientation} T={TargetLongEdge} scale={scale:0.0000} proxy={proxyWidth}x{proxyHeight}");

        var buffer = DecodedImage.RentPixels(checked(nativeLongEdge * 4 * nativeLongEdge));
        try
        {
            // —— 单段分解：同一实例上逐段计时（顺序执行，后续段能看到前段的副作用） ——
            double Create(int runs)
            {
                CIRawFilter? previous = null;
                try
                {
                    var metric = Measure(_ =>
                    {
                        var created = CreateFilter();
                        previous?.Dispose();
                        previous = created;
                        var size = created.NativeSize;
                        return ((int)Math.Round(size.Width), (int)Math.Round(size.Height), "new instance");
                    }, runs);
                    return metric.Median;
                }
                finally { previous?.Dispose(); }
            }

            report.AppendLine($"rawPipe_create median={Create(1):0.0}ms cold");
            report.AppendLine($"rawPipe_createWarm median={Create(3):0.0}ms");

            var filter = CreateFilter();
            try
            {
                var metadata = Measure(_ =>
                {
                    var size = filter.NativeSize;
                    var orientation = filter.Orientation;
                    return ((int)Math.Round(size.Width), (int)Math.Round(size.Height), $"orientation={(int)orientation}");
                });
                report.AppendLine(metadata.Format("rawPipe_metadata"));

                var setOrientation = Measure(_ =>
                {
                    filter.Orientation = CGImagePropertyOrientation.Up;
                    return (proxyWidth, proxyHeight, "Orientation=Up");
                });
                report.AppendLine(setOrientation.Format("rawPipe_setOrientation"));

                var setScale = Measure(_ =>
                {
                    filter.ScaleFactor = scale;
                    return (proxyWidth, proxyHeight, $"ScaleFactor={scale:0.0000}");
                });
                report.AppendLine(setScale.Format("rawPipe_setScale"));

                CIImage? output = null;
                var takeOutput = Measure(_ =>
                {
                    var next = filter.OutputImage ?? throw new InvalidOperationException("输出图为空");
                    output?.Dispose();
                    output = next;
                    var extent = next.Extent;
                    return ((int)Math.Round(extent.Width), (int)Math.Round(extent.Height), "OutputImage");
                });
                report.AppendLine(takeOutput.Format("rawPipe_takeOutput"));
                if (output == null)
                {
                    report.AppendLine("rawPipe ERROR no output image");
                    return;
                }

                // 渲染段：每次重新取输出图（惰性求值在取图时不会真正解马赛克，真正开销落在渲染）。
                double RenderInto(Func<CIImage> obtain, CIContext context, CGColorSpace space, int format, int width, int height, int bytesPerPixel)
                {
                    var metric = Measure(_ =>
                    {
                        using var image = obtain();
                        var extent = image.Extent;
                        int w = (int)Math.Round(extent.Width), h = (int)Math.Round(extent.Height);
                        RequireCapacity(buffer, (long)w * bytesPerPixel * h);
                        unsafe
                        {
                            fixed (byte* pixels = buffer)
                                context.RenderToBitmap(image, (nint)pixels, (nint)(w * bytesPerPixel), extent, format, space);
                        }
                        return (w, h, $"render bpp={bytesPerPixel}");
                    });
                    return metric.Error == null ? metric.Median : -1;
                }

                using var productContext = new CIContext(new CIContextOptions
                {
                    WorkingColorSpace = linearSpace,
                    CacheIntermediates = false,
                });

                report.AppendLine($"rawPipe_render median={RenderInto(() => filter.OutputImage ?? throw new InvalidOperationException("输出图为空"), productContext, displayP3, rgba8, proxyWidth, proxyHeight, 4):0.0}ms " +
                    $"out={proxyWidth}x{proxyHeight}");

                // —— 整段冷/热对比 ——
                // cold = 每次新建 CIRawFilter（产品口径，每张图一次）；warm = 复用同一实例再渲染三次取中位数。
                // 早期版本让三个 run 复用同一实例，中位数落在"第二次渲染"上，会把 cold 成本低估一半以上。
                double Cold(CIContext context, bool setOrientation, bool half, int runs)
                {
                    var metric = Measure(_ =>
                    {
                        using var fresh = CreateFilter();
                        if (setOrientation) fresh.Orientation = CGImagePropertyOrientation.Up;
                        fresh.ScaleFactor = scale;
                        using var image = fresh.OutputImage ?? throw new InvalidOperationException("输出图为空");
                        var extent = image.Extent;
                        int w = (int)Math.Round(extent.Width), h = (int)Math.Round(extent.Height);
                        int bpp = half ? 8 : 4;
                        RequireCapacity(buffer, (long)w * bpp * h);
                        unsafe
                        {
                            fixed (byte* pixels = buffer)
                                context.RenderToBitmap(image, (nint)pixels, (nint)(w * bpp), extent,
                                    half ? NativeFormat(CIFormat.Rgbah) : rgba8, half ? linearSpace : displayP3);
                        }
                        return (w, h, half ? "RGBAh" : "RGBA8");
                    }, runs);
                    return metric.Error == null ? metric.Median : -1;
                }

                double Warm(CIContext context, bool setOrientation)
                {
                    using var reused = CreateFilter();
                    if (setOrientation) reused.Orientation = CGImagePropertyOrientation.Up;
                    reused.ScaleFactor = scale;
                    // 预热：首次渲染把 RAW 解出来，之后的渲染走实例内部缓存
                    using (var warmup = reused.OutputImage)
                    {
                        if (warmup != null)
                        {
                            var extent = warmup.Extent;
                            int w = (int)Math.Round(extent.Width), h = (int)Math.Round(extent.Height);
                            RequireCapacity(buffer, (long)w * 4 * h);
                            unsafe
                            {
                                fixed (byte* pixels = buffer)
                                    productContext.RenderToBitmap(warmup, (nint)pixels, (nint)(w * 4), extent, rgba8, displayP3);
                            }
                        }
                    }
                    var metric = Measure(_ =>
                    {
                        using var image = reused.OutputImage ?? throw new InvalidOperationException("输出图为空");
                        var extent = image.Extent;
                        int w = (int)Math.Round(extent.Width), h = (int)Math.Round(extent.Height);
                        RequireCapacity(buffer, (long)w * 4 * h);
                        unsafe
                        {
                            fixed (byte* pixels = buffer)
                                productContext.RenderToBitmap(image, (nint)pixels, (nint)(w * 4), extent, rgba8, displayP3);
                        }
                        return (w, h, "reuse instance");
                    });
                    return metric.Error == null ? metric.Median : -1;
                }

                report.AppendLine($"rawPipe_cold_product median={Cold(productContext, true, false, 2):0.0}ms out={proxyWidth}x{proxyHeight}");
                report.AppendLine($"rawPipe_cold_noOrientation median={Cold(productContext, false, false, 2):0.0}ms");
                report.AppendLine($"rawPipe_cold_rgbah median={Cold(productContext, true, true, 2):0.0}ms");
                report.AppendLine($"rawPipe_warm_product median={Warm(productContext, true):0.0}ms");

                // NSData 创建
                report.AppendLine("rawPipe_fromData skipped (CIRawFilter.Create(NSData) 未由 .NET 绑定暴露)");

                // 文件读入内存的成本：CIRawFilter 的 Create(NSData) 未由 .NET 绑定暴露，
                // 这里用整文件读取耗时说明 I/O 是否可能成为 Create 的主要成分。
                {
                    var readSw = Stopwatch.StartNew();
                    using (var stream = File.OpenRead(path))
                    {
                        var sink = new byte[4 * 1024 * 1024];
                        while (stream.Read(sink, 0, sink.Length) > 0) { }
                    }
                    readSw.Stop();
                    report.AppendLine($"rawPipe_fileRead median={readSw.Elapsed.TotalMilliseconds:0.0}ms " +
                        $"size={new FileInfo(path).Length / 1048576}MB");
                }

                // CIContext 选项（全部按 cold 口径：每次新建实例，否则量到的是实例内部缓存）
                foreach (var (name, options) in new (string, CIContextOptions)[]
                {
                    ("default", new CIContextOptions { WorkingColorSpace = linearSpace }),
                    ("cacheIntermediates", new CIContextOptions { WorkingColorSpace = linearSpace, CacheIntermediates = true }),
                    ("priorityLow", new CIContextOptions { WorkingColorSpace = linearSpace, PriorityRequestLow = true }),
                    ("hqDownsampleOff", new CIContextOptions { WorkingColorSpace = linearSpace, HighQualityDownsample = false }),
                })
                {
                    using var context = new CIContext(options);
                    report.AppendLine($"rawPipe_ctx_{name} median={Cold(context, true, false, 2):0.0}ms");
                }

                // ScaleFactor 曲线（cold 口径；越低越快说明缩放减少了实际处理像素，
                // 若近似不变则说明 Core Image 总是先按原生尺寸解马赛克再缩）
                foreach (float s in new[] { 1f, 0.65f, scale, 0.5f, 0.25f })
                {
                    var curve = Measure(_ =>
                    {
                        using var fresh = CreateFilter();
                        fresh.Orientation = CGImagePropertyOrientation.Up;
                        fresh.ScaleFactor = s;
                        using var image = fresh.OutputImage ?? throw new InvalidOperationException("输出图为空");
                        var extent = image.Extent;
                        int w = (int)Math.Round(extent.Width), h = (int)Math.Round(extent.Height);
                        RequireCapacity(buffer, (long)w * 4 * h);
                        unsafe
                        {
                            fixed (byte* pixels = buffer)
                                productContext.RenderToBitmap(image, (nint)pixels, (nint)(w * 4), extent, rgba8, displayP3);
                        }
                        return (w, h, $"scale={s:0.0000}");
                    }, 2);
                    report.AppendLine(curve.Format($"rawPipe_scale_{s:0.0000}"));
                }
            }
            finally
            {
                filter.Dispose();
            }
        }
        finally
        {
            ImageBufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// 代理解码的并发特性：同一张图按 1/2/3 路并发各解一次，报告每路耗时与总墙钟。
    /// 用来判断"两路并行是否各自变慢"（硬件 HEVC 解码器串行 vs CPU/GPU 绘制争用）。
    /// </summary>
    private static void MeasureDecodeConcurrency(StringBuilder report, string path, int[]? lanes = null)
    {
        var decoder = new ApplePresentationDecoder();
        var file = new LocalFileRef(path);

        void DecodeOnce()
        {
            var image = decoder.DecodeAsync(file, TargetLongEdge, PixelEncoding.RgbaF16, CancellationToken.None)
                .GetAwaiter().GetResult() ?? throw new InvalidOperationException("代理解码返回空");
            image.Release();
        }

        // 预热（编解码器/文件缓存），避免把首次冷启动算进并发对比
        DecodeOnce();

        foreach (int laneCount in lanes ?? new[] { 1, 2, 3 })
        {
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var start = Stopwatch.StartNew();
                var tasks = new Task<(double ms, long bytes)>[laneCount];
                for (int lane = 0; lane < laneCount; lane++)
                {
                    tasks[lane] = Task.Run(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        var image = decoder.DecodeAsync(file, TargetLongEdge, PixelEncoding.RgbaF16, CancellationToken.None)
                            .GetAwaiter().GetResult() ?? throw new InvalidOperationException("并发解码返回空");
                        sw.Stop();
                        long bytes = image.ByteCount;
                        image.Release();
                        return (sw.Elapsed.TotalMilliseconds, bytes);
                    });
                }

                Task.WaitAll(tasks);
                start.Stop();
                string perLane = string.Join("/", tasks.Select(t => $"{t.Result.ms:0}"));
                report.AppendLine($"decodeConcurrency lanes={laneCount} rep={repeat} " +
                    $"wall={start.Elapsed.TotalMilliseconds:0.0}ms perLane={perLane}ms " +
                    $"perLaneMean={tasks.Average(t => t.Result.ms):0.0}ms out={tasks[0].Result.bytes / 1048576}MB");
            }
        }
    }

    /// <summary>
    /// 产品解码器的 RAW 路径实测：全分辨率与代理档各解码一次，报告输出尺寸、
    /// 源尺寸（应等于传感器原生尺寸）与方向码——用来确认产品链路真的绕开了 ImageIO 的内嵌预览。
    /// </summary>
    private static void MeasureRawProduct(StringBuilder report, string path)
    {
        foreach (var (name, maxLongEdge) in new (string, int?)[] { ("rawProductFull8", null), ("rawProductProxy8", TargetLongEdge) })
        {
            DecodedImage? decoded = null;
            Metric metric = Measure(_ =>
            {
                decoded?.Dispose();
                decoded = new ApplePresentationDecoder()
                    .DecodeAsync(new LocalFileRef(path), maxLongEdge, PixelEncoding.Rgba8, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? throw new InvalidOperationException("产品解码器返回空");
                return (decoded.Width, decoded.Height,
                    $"source={decoded.SourceWidth}x{decoded.SourceHeight} orientation={decoded.Orientation}");
            });
            report.AppendLine(metric.Format(name));
            decoded?.Dispose();
        }
    }

    /// <summary>
    /// 验收专用：RAW 的 CIRawFilter 路径与 ImageIO 路径对拍（Full 8 位 P3、Full F16、代理长边），
    /// 并核对改写 EXIF 方向后的产物方向码与像素几何。源目录只读，改写只作用于自有工作副本。
    /// 报告写纯文本并返回是否全部通过；产品路径不调用。
    /// </summary>
    /// <param name="sourceDirectory">只读素材目录（内含 ARW）。</param>
    /// <param name="workDirectory">自有工作副本目录（会写入 ARW 副本）。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <returns>全部检查是否通过。</returns>
    public static Task<bool> RunRawAcceptanceAsync(string sourceDirectory, string workDirectory, string reportPath)
        => Task.Run(() => RunRawAcceptance(sourceDirectory, workDirectory, reportPath), CancellationToken.None);

    private static bool RunRawAcceptance(string sourceDirectory, string workDirectory, string reportPath)
    {
        var report = new StringBuilder();
        bool allPassed = true;
        void Check(string name, bool ok, string detail)
        {
            allPassed &= ok;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
            Console.WriteLine($"[RawAcceptance] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }

        report.AppendLine($"RAW acceptance platform={PlatformName()} machine={MachineIdentifier()} T={TargetLongEdge}");
        report.AppendLine("new = ApplePresentationDecoder（CIRawFilter），old = ImageIO 全图/缩略图渲染（与解码器同一组 CGBitmapContext 参数）");
        Directory.CreateDirectory(workDirectory);

        string[] originals;
        try
        {
            originals = Directory.EnumerateFiles(sourceDirectory)
                .Where(path => Path.GetExtension(path).Equals(".arw", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(2)
                .ToArray();
        }
        catch (Exception exception)
        {
            Check("素材目录", false, $"{exception.GetType().Name}: {exception.Message}");
            WriteReport(reportPath, report);
            return false;
        }

        Check("素材数量", originals.Length >= 1, $"{sourceDirectory} 找到 {originals.Length} 个 ARW");
        if (originals.Length == 0)
        {
            WriteReport(reportPath, report);
            return false;
        }

        var copies = new List<string>();
        foreach (string original in originals)
        {
            string copy = Path.Combine(workDirectory, Path.GetFileName(original));
            File.Copy(original, copy, true);
            copies.Add(copy);
        }

        // 基准几何取第一张未改动副本：后续方向变体必须与它同宽同高。
        var (baseWidth, baseHeight) = (0, 0);
        foreach (string copy in copies)
        {
            int fullWidth = 0, fullHeight = 0, sourceWidth = 0, sourceHeight = 0, orientation = 1;
            try
            {
                CompareRawPaths(report, Check, copy, ref fullWidth, ref fullHeight, ref sourceWidth, ref sourceHeight, ref orientation);
            }
            catch (Exception exception)
            {
                Check($"{Path.GetFileName(copy)} 对拍", false, $"{exception.GetType().Name}: {exception.Message}");
            }
            if (baseWidth == 0) (baseWidth, baseHeight) = (fullWidth, fullHeight);
        }

        // 方向核对：只在自有副本上改 TIFF 方向标签。Sony ARW 的 ImageIO/Core Image 都从第二个 IFD
        // （缩略图 IFD）读方向，IFD0 单独改不会生效，故两处一起写，保证两条路径读到同一方向。
        string? portraitVariant = null;
        foreach (int wanted in new[] { 6, 8 })
        {
            string variant = Path.Combine(workDirectory,
                $"{Path.GetFileNameWithoutExtension(copies[0])}-orient{wanted}.ARW");
            File.Copy(copies[0], variant, true);
            if (!TryPatchTiffOrientation(variant, wanted, out string patchDetail))
            {
                Check($"orientation {wanted} 改写", false, patchDetail);
                continue;
            }

            if (wanted == 6) portraitVariant = variant;
            using var decoded = DecodeProduct(variant, null, PixelEncoding.Rgba8, out string decodeError);
            if (decoded == null)
            {
                Check($"orientation {wanted} 解码", false, decodeError);
                continue;
            }

            using var url = NSUrl.FromFilename(variant);
            using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false });
            var properties = source?.GetProperties(0, null);
            int imageIoOrientation = properties?.Orientation is { } direction ? (int)direction : 1;

            Check($"orientation {wanted} 产物方向", decoded.Orientation == wanted,
                $"new={decoded.Orientation} imageio={imageIoOrientation} ({patchDetail})");
            Check($"orientation {wanted} 像素未旋转", decoded.Width == baseWidth && decoded.Height == baseHeight,
                $"new={decoded.Width}x{decoded.Height} base={baseWidth}x{baseHeight}");
            Check($"orientation {wanted} 源尺寸", decoded.SourceWidth == baseWidth && decoded.SourceHeight == baseHeight,
                $"source={decoded.SourceWidth}x{decoded.SourceHeight}");
            Check($"orientation {wanted} 两路方向一致", imageIoOrientation == decoded.Orientation,
                $"new={decoded.Orientation} imageio={imageIoOrientation}");
        }

        // 缩略图兜底：RAW 同样走新路径，产物必须是显示朝向的 sRGB8（分析/CV 口径）。
        if (portraitVariant != null)
        {
            using var thumbnail = new ApplePresentationDecoder()
                .DecodeThumbnailAsync(new LocalFileRef(portraitVariant), 200, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("缩略图兜底 sRGB8", thumbnail is { Encoding: PixelEncoding.Bgra32 },
                $"encoding={thumbnail?.Encoding} size={thumbnail?.Width}x{thumbnail?.Height}");
            Check("缩略图兜底显示朝向", thumbnail != null && thumbnail.Height > thumbnail.Width,
                $"size={thumbnail?.Width}x{thumbnail?.Height}（方向 6 应高于宽）");
            Check("缩略图兜底有像素", thumbnail != null && MeanByte(thumbnail.Pixels) > 8,
                $"mean={(thumbnail == null ? 0 : MeanByte(thumbnail.Pixels)):0.0}");
        }

        report.AppendLine(allPassed ? "SUMMARY PASS" : "SUMMARY FAIL");
        WriteReport(reportPath, report);
        return allPassed;
    }

    /// <summary>抽样平均字节值，用来确认产物确实被写过像素。</summary>
    private static double MeanByte(byte[] pixels)
    {
        if (pixels.Length == 0) return 0;
        long sum = 0;
        long count = 0;
        for (int index = 0; index < pixels.Length; index += 97) { sum += pixels[index]; count++; }
        return count == 0 ? 0 : sum / (double)count;
    }

    /// <summary>同一文件的四条对拍：Full 8 位 P3、Full F16、代理 8 位 P3 与几何。</summary>
    private static void CompareRawPaths(StringBuilder report, Action<string, bool, string> check, string path,
        ref int fullWidth, ref int fullHeight, ref int sourceWidth, ref int sourceHeight, ref int orientation)
    {
        string name = Path.GetFileName(path);
        using var newFull8 = DecodeProduct(path, null, PixelEncoding.Rgba8, out string error8);
        if (newFull8 == null)
        {
            check($"{name} Full 8 位解码", false, error8);
            return;
        }

        fullWidth = newFull8.Width;
        fullHeight = newFull8.Height;
        sourceWidth = newFull8.SourceWidth;
        sourceHeight = newFull8.SourceHeight;
        orientation = newFull8.Orientation;

        var oldFull8 = DecodeImageIoPixels(path, null, PixelEncoding.Rgba8,
            out int oldWidth, out int oldHeight, out int oldSourceWidth, out int oldSourceHeight, out int oldOrientation);

        check($"{name} Full 8 位几何",
            oldWidth == newFull8.Width && oldHeight == newFull8.Height &&
            oldSourceWidth == newFull8.SourceWidth && oldSourceHeight == newFull8.SourceHeight &&
            oldOrientation == newFull8.Orientation,
            $"new={newFull8.Width}x{newFull8.Height}/{newFull8.SourceWidth}x{newFull8.SourceHeight}/o{newFull8.Orientation} " +
            $"old={oldWidth}x{oldHeight}/{oldSourceWidth}x{oldSourceHeight}/o{oldOrientation}");

        if (oldWidth == newFull8.Width && oldHeight == newFull8.Height)
        {
            var (mean, max, over2) = Compare8(newFull8.Pixels, newFull8.Stride, oldFull8, oldWidth * 4, newFull8.Width, newFull8.Height, 4);
            report.AppendLine($"  full8 {name} mean={mean:0.0000} max={max} over2={over2 * 100:0.000}%");
            check($"{name} Full 8 位像素差", mean <= 1.0 && over2 <= 0.01,
                $"mean={mean:0.0000} max={max} over2={over2 * 100:0.000}%");
        }

        using (var newFull16 = DecodeProduct(path, null, PixelEncoding.RgbaF16, out string error16))
        {
            if (newFull16 == null)
            {
                check($"{name} Full F16 解码", false, error16);
            }
            else
            {
                var oldFull16 = DecodeImageIoPixels(path, null, PixelEncoding.RgbaF16,
                    out int oldW, out int oldH, out _, out _, out _);
                if (oldW != newFull16.Width || oldH != newFull16.Height)
                {
                    check($"{name} Full F16 几何", false, $"new={newFull16.Width}x{newFull16.Height} old={oldW}x{oldH}");
                }
                else
                {
                    var (mean, max, over2) = CompareF16(newFull16.Pixels, newFull16.Stride, oldFull16, oldW * 8, oldW, oldH);
                    report.AppendLine($"  full16 {name} mean={mean:0.0000} max={max} over2={over2 * 100:0.000}%");
                    check($"{name} Full F16 像素差（转 sRGB8）", mean <= 1.0 && over2 <= 0.01,
                        $"mean={mean:0.0000} max={max} over2={over2 * 100:0.000}%");
                }
            }
        }

        using var newProxy = DecodeProduct(path, TargetLongEdge, PixelEncoding.Rgba8, out string errorProxy);
        if (newProxy == null)
        {
            check($"{name} 代理解码", false, errorProxy);
            return;
        }

        var oldProxy = DecodeImageIoPixels(path, TargetLongEdge, PixelEncoding.Rgba8,
            out int proxyW, out int proxyH, out _, out _, out _);
        var (proxyMean, proxyMax, proxyOver2, proxyBlockMean) = CompareProxy8(
            newProxy.Pixels, newProxy.Width, newProxy.Height, oldProxy, proxyW, proxyH);
        report.AppendLine($"  proxy {name} new={newProxy.Width}x{newProxy.Height} old={proxyW}x{proxyH} " +
                          $"mean={proxyMean:0.0000} max={proxyMax} over2={proxyOver2 * 100:0.000}% " +
                          $"block8={proxyBlockMean:0.0000}");
        check($"{name} 代理长边", Math.Max(newProxy.Width, newProxy.Height) >= Math.Min(TargetLongEdge, Math.Max(newFull8.Width, newFull8.Height)),
            $"new={newProxy.Width}x{newProxy.Height} 目标={TargetLongEdge}");
        // 代理档两条路径的重采样核不同（ImageIO 全量渲染再缩 vs Core Image 缩放渲染），尺寸也可能差一像素，
        // 逐像素差集中在高对比边缘；块平均差才反映色调与几何是否一致，故以 8×8 块平均 ≤ 1 作为守卫。
        check($"{name} 代理色调/几何", proxyBlockMean <= 1.0,
            $"block8={proxyBlockMean:0.0000} 逐像素 mean={proxyMean:0.0000} max={proxyMax} over2={proxyOver2 * 100:0.000}%");
    }

    /// <summary>
    /// 代理对拍：长边相同但高度可能差一像素，先把行号归一映射到同一几何再比。
    /// 返回逐像素的平均/最大/超 2 占比，以及 8×8 块平均绝对差（抵消重采样核差异）。
    /// </summary>
    private static (double Mean, int Max, double Over2, double BlockMean) CompareProxy8(
        byte[] newPixels, int newWidth, int newHeight, byte[] oldPixels, int oldWidth, int oldHeight)
    {
        int width = Math.Min(newWidth, oldWidth);
        int height = Math.Min(newHeight, oldHeight);
        long sum = 0, over2 = 0, count = 0;
        int max = 0;
        for (int y = 0; y < height; y++)
        {
            int oldRow = MappedRow(y, newHeight, oldHeight);
            int n = y * newWidth * 4, o = oldRow * oldWidth * 4;
            for (int x = 0; x < width; x++, n += 4, o += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int difference = Math.Abs(newPixels[n + c] - oldPixels[o + c]);
                    sum += difference;
                    count++;
                    if (difference > max) max = difference;
                    if (difference > 2) over2++;
                }
            }
        }

        const int Block = 8;
        int blocksX = width / Block, blocksY = height / Block;
        double blockSum = 0;
        long blockCount = 0;
        Span<long> newBlock = stackalloc long[3];
        Span<long> oldBlock = stackalloc long[3];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                newBlock.Clear();
                oldBlock.Clear();
                for (int dy = 0; dy < Block; dy++)
                {
                    int y = by * Block + dy;
                    int oldRow = MappedRow(y, newHeight, oldHeight);
                    for (int dx = 0; dx < Block; dx++)
                    {
                        int x = bx * Block + dx;
                        for (int c = 0; c < 3; c++)
                        {
                            newBlock[c] += newPixels[(y * newWidth + x) * 4 + c];
                            oldBlock[c] += oldPixels[(oldRow * oldWidth + x) * 4 + c];
                        }
                    }
                }

                for (int c = 0; c < 3; c++)
                {
                    blockSum += Math.Abs(newBlock[c] - oldBlock[c]) / (double)(Block * Block);
                    blockCount++;
                }
            }
        }

        return (count == 0 ? 0 : sum / (double)count, max, count == 0 ? 0 : over2 / (double)count,
            blockCount == 0 ? 0 : blockSum / blockCount);
    }

    /// <summary>两图高度不同时把行号按比例映射，避免一像素差累积成整幅错位。</summary>
    private static int MappedRow(int row, int newHeight, int oldHeight)
        => newHeight == oldHeight ? row : (int)Math.Round(row * (oldHeight - 1.0) / (newHeight - 1.0));

    /// <summary>走产品解码器；失败时把异常原因交给调用方，不中断整轮对拍。</summary>
    private static DecodedImage? DecodeProduct(string path, int? maxLongEdge, PixelEncoding encoding, out string error)
    {
        try
        {
            error = "";
            return new ApplePresentationDecoder()
                .DecodeAsync(new LocalFileRef(path), maxLongEdge, encoding, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return null;
        }
    }

    /// <summary>ImageIO 路径（复制解码器的绘制参数），作为对拍基准。</summary>
    private static unsafe byte[] DecodeImageIoPixels(string path, int? maxLongEdge, PixelEncoding encoding,
        out int width, out int height, out int sourceWidth, out int sourceHeight, out int orientation)
    {
        using var pool = new NSAutoreleasePool();
        using var url = NSUrl.FromFilename(path);
        using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
            ?? throw new InvalidOperationException("ImageIO 无法打开图片");
        var properties = source.GetProperties(0, null);
        sourceWidth = properties?.PixelWidth ?? 0;
        sourceHeight = properties?.PixelHeight ?? 0;
        orientation = properties?.Orientation is { } direction ? (int)direction : 1;
        using var image = maxLongEdge.HasValue
            ? source.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = false,
                MaxPixelSize = maxLongEdge.Value,
                ShouldCacheImmediately = true,
            })
            : source.CreateImage(0, new CGImageOptions { ShouldCacheImmediately = true });
        if (image == null) throw new InvalidOperationException("ImageIO 无法解码图片");
        width = (int)image.Width;
        height = (int)image.Height;
        var buffer = new byte[width * DecodedImage.BytesPerPixel(encoding) * height];
        if (encoding == PixelEncoding.RgbaF16) DrawLinearF16(image, buffer, width, height);
        else DrawDisplayP3(image, buffer, width, height);
        return buffer;
    }

    /// <summary>8 位逐像素比较（只看 RGB，跳过 alpha）；返回平均绝对差、最大差与差 &gt; 2 的占比。</summary>
    private static (double Mean, int Max, double Over2) Compare8(byte[] newPixels, int newStride, byte[] oldPixels, int oldStride,
        int width, int height, int bytesPerPixel)
    {
        long sum = 0, over2 = 0, count = 0;
        int max = 0;
        for (int y = 0; y < height; y++)
        {
            int n = y * newStride, o = y * oldStride;
            for (int x = 0; x < width; x++, n += bytesPerPixel, o += bytesPerPixel)
            {
                for (int c = 0; c < 3; c++)
                {
                    int difference = Math.Abs(newPixels[n + c] - oldPixels[o + c]);
                    sum += difference;
                    count++;
                    if (difference > max) max = difference;
                    if (difference > 2) over2++;
                }
            }
        }

        return count == 0 ? (0, 0, 0) : (sum / (double)count, max, over2 / (double)count);
    }

    /// <summary>F16 逐像素比较：两侧都按线性→sRGB 编码量化到 8 位后再比（与 8 位口径一致）。</summary>
    private static (double Mean, int Max, double Over2) CompareF16(byte[] newPixels, int newStride, byte[] oldPixels, int oldStride,
        int width, int height)
    {
        var newHalves = MemoryMarshal.Cast<byte, Half>(newPixels);
        var oldHalves = MemoryMarshal.Cast<byte, Half>(oldPixels);
        long sum = 0, over2 = 0, count = 0;
        int max = 0;
        for (int y = 0; y < height; y++)
        {
            int n = y * (newStride / 2), o = y * (oldStride / 2);
            for (int x = 0; x < width; x++, n += 4, o += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int difference = Math.Abs(ToSrgb8((float)newHalves[n + c]) - ToSrgb8((float)oldHalves[o + c]));
                    sum += difference;
                    count++;
                    if (difference > max) max = difference;
                    if (difference > 2) over2++;
                }
            }
        }

        return count == 0 ? (0, 0, 0) : (sum / (double)count, max, over2 / (double)count);
    }

    /// <summary>取 Core Image 的原生像素格式值：`CIFormat` 是普通枚举，枚举值不是原生常量。</summary>
    private static int NativeFormat(CIFormat format)
        => format.GetConstant() ?? throw new InvalidOperationException($"{format} 的原生常量不可用");

    /// <summary>线性分量 → sRGB 编码的 8 位值（与展示口径一致）。</summary>
    private static int ToSrgb8(float linear)
    {
        double encoded = linear <= 0.0031308f ? linear * 12.92 : 1.055 * Math.Pow(Math.Max(linear, 0), 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(encoded, 0, 1) * 255);
    }

    /// <summary>
    /// 改写 TIFF 方向标签（0x0112）：Sony ARW 的首个 IFD 与缩略图 IFD 都写，两条解码路径才会读到同一方向。
    /// </summary>
    private static bool TryPatchTiffOrientation(string path, int orientation, out string detail)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool little = bytes[0] == (byte)'I' && bytes[1] == (byte)'I';
            if (!little) { detail = "不是小端 TIFF"; return false; }
            int ifdOffset = BitConverter.ToInt32(bytes, 4);
            int patched = 0, index = 0;
            while (ifdOffset > 0 && ifdOffset + 2 <= bytes.Length && index < 2)
            {
                int entries = BitConverter.ToUInt16(bytes, ifdOffset);
                for (int entry = 0; entry < entries; entry++)
                {
                    int offset = ifdOffset + 2 + entry * 12;
                    if (offset + 12 > bytes.Length) break;
                    if (BitConverter.ToUInt16(bytes, offset) == 0x0112 &&
                        BitConverter.ToUInt16(bytes, offset + 2) == 3 &&
                        BitConverter.ToUInt32(bytes, offset + 4) == 1)
                    {
                        bytes[offset + 8] = (byte)(orientation & 0xff);
                        bytes[offset + 9] = (byte)(orientation >> 8);
                        patched++;
                    }
                }

                int next = ifdOffset + 2 + entries * 12;
                ifdOffset = next + 4 <= bytes.Length ? BitConverter.ToInt32(bytes, next) : 0;
                index++;
            }

            if (patched == 0) { detail = "未找到方向标签"; return false; }
            File.WriteAllBytes(path, bytes);
            detail = $"已写 {patched} 处方向标签";
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    /// <summary>Metal 上传：渲染器同步 SetTexture，以及设备级上传 + mipmap + 等待 GPU 完成。</summary>
    private static void MeasureMetal(StringBuilder report, DecodedImage? image)
    {
        if (image == null)
        {
            report.AppendLine("metalSetTexture skipped (no F16 proxy)");
            report.AppendLine("metalUpload skipped (no F16 proxy)");
            return;
        }

        try
        {
            using var renderer = new MetalPhotoRenderer(new PhotoViewport());
            Metric setTexture = Measure(_ =>
            {
                renderer.SetTexture(image);
                return (image.Width, image.Height, "RGBA16Float sync");
            });
            report.AppendLine(setTexture.Format("metalSetTexture"));
        }
        catch (Exception exception)
        {
            report.AppendLine($"metalSetTexture ERROR {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            var device = MTLDevice.SystemDefault ?? throw new InvalidOperationException("Metal 设备不可用");
            using var queue = device.CreateCommandQueue() ?? throw new InvalidOperationException("Metal 命令队列不可用");
            Metric upload = Measure(_ =>
            {
                using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(
                    MTLPixelFormat.RGBA16Float, (nuint)image.Width, (nuint)image.Height, true);
                descriptor.StorageMode = MTLStorageMode.Shared;
                descriptor.Usage = MTLTextureUsage.ShaderRead;
                using var texture = device.CreateTexture(descriptor) ?? throw new InvalidOperationException("纹理分配失败");
                unsafe
                {
                    fixed (byte* pixels = image.Pixels)
                        texture.ReplaceRegion(MTLRegion.Create2D(0, 0, (nuint)image.Width, (nuint)image.Height), 0,
                            (IntPtr)pixels, (nuint)image.Stride);
                }

                using var command = queue.CommandBuffer() ?? throw new InvalidOperationException("Metal 命令缓冲不可用");
                using var blit = command.BlitCommandEncoder ?? throw new InvalidOperationException("Metal mipmap 编码器不可用");
                blit.GenerateMipmapsForTexture(texture);
                blit.EndEncoding();
                command.Commit();
                command.WaitUntilCompleted();
                if (command.Error != null) throw new InvalidOperationException(command.Error.LocalizedDescription);
                return (image.Width, image.Height, "upload+mipmap+wait");
            });
            report.AppendLine(upload.Format("metalUpload"));
        }
        catch (Exception exception)
        {
            report.AppendLine($"metalUpload ERROR {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>直方图渲染计时（512×512，与产品分析栏同尺寸）。</summary>
    private static void MeasureHistogram(StringBuilder report, string name, DecodedImage? image)
    {
        if (image == null)
        {
            report.AppendLine($"{name} skipped (no image)");
            return;
        }

        Metric metric = Measure(_ =>
        {
            using var histogram = HistogramRenderer.Render(image, 512, 512);
            return (histogram.Width, histogram.Height, "512x512");
        });
        report.AppendLine(metric.Format(name));
    }

    /// <summary>按 <c>PhotoLoadSession.EnhanceForDisplay</c> 的三步分别计时（转 sRGB8 / CLHE / 回展示编码）。</summary>
    private static void MeasureEnhance(StringBuilder report, string prefix, DecodedImage source,
        Func<DecodedImage, DecodedImage> back)
    {
        var toSrgb = new double[Runs];
        var clhe = new double[Runs];
        var backTimes = new double[Runs];
        int width = 0, height = 0;
        try
        {
            for (int run = 0; run < Runs; run++)
            {
                var stopwatch = Stopwatch.StartNew();
                var srgb = ColorEngine.ToSrgb8(source);
                toSrgb[run] = stopwatch.Elapsed.TotalMilliseconds;
                try
                {
                    stopwatch.Restart();
                    ImageEnhancer.EnhanceInPlace(srgb);
                    clhe[run] = stopwatch.Elapsed.TotalMilliseconds;

                    stopwatch.Restart();
                    var enhanced = back(srgb);
                    backTimes[run] = stopwatch.Elapsed.TotalMilliseconds;
                    width = enhanced.Width;
                    height = enhanced.Height;
                    enhanced.Dispose();
                }
                finally
                {
                    srgb.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"{prefix}_toSrgb8 ERROR {exception.GetType().Name}: {exception.Message}");
            return;
        }

        report.AppendLine(Line($"{prefix}_toSrgb8", toSrgb, width, height, "Bgra32"));
        report.AppendLine(Line($"{prefix}_clhe", clhe, width, height, "inPlace"));
        report.AppendLine(Line($"{prefix}_back", backTimes, width, height, "restoreDisplayEncoding"));
    }

    /// <summary>三次计时取首次与中位数，统一成报告行。</summary>
    private static Metric Measure(Func<int, (int width, int height, string extra)> body, int runs = Runs)
    {
        var metric = new Metric();
        var times = new double[runs];
        try
        {
            for (int run = 0; run < runs; run++)
            {
                var stopwatch = Stopwatch.StartNew();
                var (width, height, extra) = body(run);
                stopwatch.Stop();
                times[run] = stopwatch.Elapsed.TotalMilliseconds;
                metric.Width = width;
                metric.Height = height;
                metric.Extra = extra;
            }

            metric.First = times[0];
            metric.Median = Median(times);
        }
        catch (Exception exception)
        {
            metric.Error = $"{exception.GetType().Name}: {exception.Message}";
        }

        return metric;
    }

    /// <summary>报告行：首次（冷）与中位数，附带输出尺寸与额外信息。</summary>
    private static string Line(string name, double[] times, int width, int height, string extra)
        => $"{name} first={times[0]:0.0}ms median={Median(times):0.0}ms " +
           $"out={(width > 0 ? $"{width}x{height}" : "-")} extra={extra}";

    /// <summary>取中位数（不改原数组）。</summary>
    private static double Median(double[] times)
    {
        var sorted = (double[])times.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>按固定步长抽样求和，用来确认渲染确实写入了像素（0 表示缓冲未被写过）。</summary>
    private static long Checksum(byte[] buffer)
    {
        long sum = 0;
        for (int index = 0; index < buffer.Length; index += 4099) sum += buffer[index];
        return sum;
    }

    /// <summary>池化缓冲容量检查，不足时明确失败而不是越界写入。</summary>
    private static void RequireCapacity(byte[] buffer, long needed)
    {
        if (needed > buffer.Length)
            throw new InvalidOperationException($"缓冲不足：需要 {needed} 字节，实际 {buffer.Length}");
    }

    /// <summary>读取内存指标；委托缺失或失败时返回 0 / -1。</summary>
    private static (long footprint, long available) ProbeMemory(Func<(long footprint, long available)>? memoryProbe)
    {
        if (memoryProbe == null) return (0, -1);
        try { return memoryProbe(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PerfProbe] memory probe failed: {exception.Message}");
            return (0, -1);
        }
    }

    /// <summary>平台名（报告头）。</summary>
    private static string PlatformName()
        => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsIOS() ? "iOS" : RuntimeInformation.OSDescription;

    /// <summary>机型标识：iOS 用 hw.machine，macOS 用 hw.model；失败时 "-"。</summary>
    private static string MachineIdentifier()
        => Sysctl(OperatingSystem.IsMacOS() ? "hw.model" : "hw.machine") ?? "-";

    /// <summary>sysctl 字符串读取；P/Invoke 显式指针化（与 iOS 内存预算同一写法）。</summary>
    private static unsafe string? Sysctl(string name)
    {
        try
        {
            nuint length = 0;
            sysctlbyname(name, IntPtr.Zero, (IntPtr)(&length), IntPtr.Zero, 0);
            if (length == 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                sysctlbyname(name, buffer, (IntPtr)(&length), IntPtr.Zero, 0);
                return Marshal.PtrToStringAnsi(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>字节数按 MB 保留一位小数。</summary>
    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0") + "MB";

    /// <summary>可用内存：负值表示调用方无法提供（Mac 传 -1）。</summary>
    private static string ReadableAvailable(long bytes) => bytes < 0 ? "-1MB" : Megabytes(bytes);

    /// <summary>整份报告落盘；失败只记录到控制台，不打断测量。</summary>
    private static void WriteReport(string reportPath, StringBuilder report)
    {
        try
        {
            string? directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, report.ToString());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PerfProbe] 报告写入失败: {exception.Message}");
        }
    }

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, IntPtr oldp, IntPtr oldlenp, IntPtr newp, nuint newlen);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern nint CGImageSourceCreateImageAtIndex(nint source, nint index, nint options);

    /// <summary>一次测量的首次（冷）与中位数结果。</summary>
    private sealed class Metric
    {
        public double First;
        public double Median;
        public int Width;
        public int Height;
        public string Extra = "";
        public string? Error;

        /// <summary>格式化成报告行；失败时写出异常类型与消息。</summary>
        public string Format(string name) => Error != null
            ? $"{name} ERROR {Error}"
            : $"{name} first={First:0.0}ms median={Median:0.0}ms " +
              $"out={(Width > 0 ? $"{Width}x{Height}" : "-")} extra={Extra}";
    }
}
