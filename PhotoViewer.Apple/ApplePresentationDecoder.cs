using CoreGraphics;
using CoreImage;
using Foundation;
using ImageIO;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

/// <summary>
/// Apple 平台统一主图解码器：RAW 走 Core Image 的 RAW 引擎（全分辨率 + 缩放渲染），JPEG/HEIF 走 ImageIO；
/// 两者都按请求编码输出 RGBA16F 扩展线性 sRGB 或 8 位 Display P3，方向留给 GPU。
/// </summary>
public sealed class ApplePresentationDecoder : IPresentationImageDecoder
{
    /// <summary>
    /// RAW 渲染共用的 Core Image 上下文（CIContext 线程安全）：工作色彩空间为扩展线性 sRGB，
    /// 不缓存中间结果，走 GPU 渲染器。
    /// </summary>
    private static readonly Lazy<CIContext> RawRenderContext = new(CreateRawRenderContext);

    /// <summary>RAW 渲染的工作色彩空间；由上下文持有，必须与上下文同寿命，不能按次释放。</summary>
    private static CGColorSpace? _rawWorkingSpace;

    /// <summary>在线程池解码；代理尺寸按平台能力缩放（RAW 由 Core Image 缩放渲染，其余走编码器子采样），不使用容器内的小缩略图。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次必须产出的像素编码（F16 扩展线性 sRGB 或 8 位 Display P3）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() => Decode(RequireLocalPath(file), maxLongEdge, encoding, cancellationToken), cancellationToken);

    /// <summary>无内嵌预览的用户图片按短边缩解码，保持 ICC 与方向正确，不走原图全尺寸路径。</summary>
    public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() =>
        {
            using var pool = new NSAutoreleasePool();
            var path = RequireLocalPath(file);
            using var url = NSUrl.FromFilename(path);
            using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false }) ?? throw new UserFacingException("系统无法读取缩略图尺寸");
            var properties = source.GetProperties(0, null);
            int width = properties?.PixelWidth ?? 0, height = properties?.PixelHeight ?? 0;
            if (width <= 0 || height <= 0) throw new UserFacingException("缩略图源尺寸无效");
            int target = Math.Max(minShortEdge, (int)Math.Ceiling((double)minShortEdge * Math.Max(width, height) / Math.Min(width, height)));
            using var image = Decode(path, target, PixelEncoding.RgbaF16, cancellationToken);
            return ColorEngine.ToAnalysisSrgb8(image);
        }, cancellationToken);

    /// <summary>ImageIO / Core Image 只接受真实文件路径；SAF 等非本地来源明确报不支持。</summary>
    private static string RequireLocalPath(IFileRef file)
        => file.TryGetLocalPath()
            ?? throw new NotSupportedException($"Apple 展示解码需要本地文件路径：{file.Name}");

    /// <summary>按格式选路：RAW 走 Core Image 的 RAW 引擎，其余走 ImageIO；RAW 失败时回退 ImageIO 并记录原因。</summary>
    /// <param name="path">图片路径。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">目标像素编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物。</returns>
    private static unsafe DecodedImage Decode(string path, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        if (encoding is not (PixelEncoding.RgbaF16 or PixelEncoding.Rgba8))
            throw new NotSupportedException($"Apple 展示解码器不支持编码 {encoding}");
        using var pool = new NSAutoreleasePool();
        if (RawLoader.IsRawFile(path))
        {
            try
            {
                return DecodeRaw(path, maxLongEdge, encoding, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消不是解码失败，不降级到另一条路径继续渲染。
                throw;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[PresentationDecoder] RAW 经 CIRawFilter 解码失败，回退 ImageIO：{exception.GetType().Name}: {exception.Message}");
            }
        }

        return DecodeImageIo(path, maxLongEdge, encoding, cancellationToken);
    }

    /// <summary>ImageIO 路径：JPEG/HEIF 常态，也是 RAW 在 Core Image 不可用时的兜底（iOS 上可能只得到内嵌预览）。</summary>
    private static unsafe DecodedImage DecodeImageIo(string path, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        using var url = NSUrl.FromFilename(path);
        using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
            ?? throw new UserFacingException("系统无法打开图片");
        var properties = source.GetProperties(0, null);
        int sourceWidth = properties?.PixelWidth ?? 0;
        int sourceHeight = properties?.PixelHeight ?? 0;
        int orientation = properties?.Orientation is { } direction ? (int)direction : 1;
        cancellationToken.ThrowIfCancellationRequested();

        using var image = maxLongEdge.HasValue
            ? source.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = false,
                MaxPixelSize = maxLongEdge.Value,
                ShouldCacheImmediately = true,
            })
            : source.CreateImage(0, new CGImageOptions { ShouldCacheImmediately = true });
        if (image == null) throw new UserFacingException("系统无法解码该图片");
        using var inferredImage = InterpretUntaggedJpeg(image, properties, path);
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        return encoding == PixelEncoding.RgbaF16
            ? DecodeLinearF16(inferredImage ?? image, width, height, sourceWidth, sourceHeight, orientation, cancellationToken)
            : DecodeDisplayP3(inferredImage ?? image, width, height, sourceWidth, sourceHeight, orientation, cancellationToken);
    }

    /// <summary>
    /// RAW 路径：用 Core Image 的 RAW 引擎全分辨率解码，按代理上限缩放渲染后直接写进池化缓冲。
    /// 几何取传感器的原生尺寸（未旋转），EXIF 方向只作为产物的方向码交给 GPU，与 ImageIO 路径
    /// 「CreateThumbnailWithTransform=false + 属性方向」的契约一致；不启用草稿模式（画质红线）。
    /// </summary>
    private static unsafe DecodedImage DecodeRaw(string path, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        using var url = NSUrl.FromFilename(path);
        using var filter = CIRawFilter.Create(url)
            ?? throw new UserFacingException("系统不支持该 RAW 的格式或相机机型");
        filter.DraftModeEnabled = false;
        int orientation = (int)filter.Orientation;
        CGSize native = filter.NativeSize;
        int sourceWidth = (int)Math.Round(native.Width);
        int sourceHeight = (int)Math.Round(native.Height);
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new UserFacingException("RAW 解码给出的原生尺寸无效");

        // 方向交给 GPU：先把像素摆正再渲染会让产物与源尺寸、方向码三者互相矛盾。
        filter.Orientation = CGImagePropertyOrientation.Up;
        cancellationToken.ThrowIfCancellationRequested();

        using var output = CreateRawOutput(filter, maxLongEdge, Math.Max(sourceWidth, sourceHeight), cancellationToken);
        CGRect extent = output.Extent;
        int width = (int)Math.Round(extent.Width);
        int height = (int)Math.Round(extent.Height);
        if (width <= 0 || height <= 0) throw new UserFacingException("RAW 解码的输出尺寸无效");
        return RenderRaw(output, extent, width, height, encoding, sourceWidth, sourceHeight, orientation, cancellationToken);
    }

    /// <summary>
    /// 设置缩放并取输出图：代理按长边上限缩渲染，全分辨率档用 1.0。
    /// Core Image 按自身取整给出像素尺寸，若长边比目标少一像素则微调缩放重取，保证代理不小于目标（宁大勿小）。
    /// </summary>
    private static CIImage CreateRawOutput(CIRawFilter filter, int? maxLongEdge, int nativeLongEdge, CancellationToken cancellationToken)
    {
        if (maxLongEdge is not { } limit || limit <= 0 || limit >= nativeLongEdge)
        {
            filter.ScaleFactor = 1f;
            return filter.OutputImage ?? throw new UserFacingException("RAW 解码未给出输出图");
        }

        float scale = (float)limit / nativeLongEdge;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filter.ScaleFactor = scale;
            CIImage image = filter.OutputImage ?? throw new UserFacingException("RAW 解码未给出输出图");
            CGRect extent = image.Extent;
            int longEdge = (int)Math.Round(Math.Max(extent.Width, extent.Height));
            if (longEdge <= 0)
            {
                image.Dispose();
                throw new UserFacingException("RAW 解码的缩放输出尺寸无效");
            }

            if (longEdge >= limit || scale >= 1f || attempt >= 3) return image;
            image.Dispose();
            // 多给半像素余量，抵消 Core Image 的取整误差。
            scale = Math.Min(1f, scale * ((limit + 0.5f) / longEdge));
        }
    }

    /// <summary>
    /// 把 RAW 输出直接渲染进池化缓冲：F16 用原生 RGBAh 格式 + 扩展线性 sRGB，8 位用原生 RGBA8 格式 + Display P3。
    /// 两者的色彩与预乘口径与 ImageIO 路径的绘制函数相同（RAW 不透明）。
    /// </summary>
    private static unsafe DecodedImage RenderRaw(CIImage output, CGRect extent, int width, int height,
        PixelEncoding encoding, int sourceWidth, int sourceHeight, int orientation, CancellationToken cancellationToken)
    {
        bool linear = encoding == PixelEncoding.RgbaF16;
        int stride = checked(width * (linear ? 8 : 4));
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        CGColorSpace? ownedSpace = null;
        try
        {
            CIContext context = RawRenderContext.Value;
            CGColorSpace space;
            int format;
            if (linear)
            {
                space = RawWorkingSpace;
                // CIFormat 在绑定里是普通枚举（Rgbah=1、Rgba8=6），枚举值不是原生常量；
                // 直接传会被 Core Image 当成别的格式渲染（RGBAh 时静默不写像素），必须取 GetConstant() 的原生值。
                format = CIFormat.Rgbah.GetConstant() ?? throw new UserFacingException("系统不支持该照片所需的像素格式");
            }
            else
            {
                space = ownedSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3)
                    ?? throw new UserFacingException("系统不支持 Display P3 色彩空间");
                format = CIFormat.Rgba8.GetConstant() ?? throw new UserFacingException("系统不支持该照片所需的像素格式");
            }

            fixed (byte* pixels = buffer)
                context.RenderToBitmap(output, (IntPtr)pixels, stride, extent, format, space);
            cancellationToken.ThrowIfCancellationRequested();
            return DecodedImage.CreatePooled(buffer, width, height, stride, encoding,
                linear ? ImageColorSpace.LinearSrgb : ImageColorSpace.DisplayP3, sourceWidth, sourceHeight, orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
        finally
        {
            ownedSpace?.Dispose();
        }
    }

    /// <summary>创建 RAW 渲染上下文；扩展线性 sRGB 空间随上下文常驻。</summary>
    private static CIContext CreateRawRenderContext()
    {
        var space = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
            ?? throw new UserFacingException("系统不支持扩展线性 sRGB 色彩空间");
        _rawWorkingSpace = space;
        return new CIContext(new CIContextOptions
        {
            WorkingColorSpace = space,
            CacheIntermediates = false,
        });
    }

    /// <summary>RAW 渲染的工作色彩空间（扩展线性 sRGB）；上下文创建失败时显式抛出。</summary>
    private static CGColorSpace RawWorkingSpace => _rawWorkingSpace
        ?? throw new UserFacingException("RAW 渲染所需的色彩空间尚未就绪");

    /// <summary>绘制到扩展线性 sRGB 的半精度浮点缓冲；广色域原色允许超过一，由 scRGB 承载不裁剪。</summary>
    private static unsafe DecodedImage DecodeLinearF16(CGImage image, int width, int height, int sourceWidth, int sourceHeight, int orientation, CancellationToken cancellationToken)
    {
        int stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            using var linearSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
                ?? throw new UserFacingException("系统不支持扩展线性 sRGB 色彩空间");
            fixed (byte* pixels = buffer)
            {
                using var context = new CGBitmapContext((IntPtr)pixels, width, height, 16, stride,
                    linearSpace, CGBitmapFlags.FloatComponents | CGBitmapFlags.ByteOrder16Little | CGBitmapFlags.PremultipliedLast);
                context.SetBlendMode(CGBlendMode.Copy);
                context.DrawImage(new CGRect(0, 0, width, height), image);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16,
                ImageColorSpace.LinearSrgb, sourceWidth, sourceHeight, orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// 绘制到 8 位 Display P3 缓冲（sRGB 传递函数、RGBA 字节序、预乘 alpha），供 iPad Full 档与压力降级使用。
    /// P3 立方体容不下的超范围原色（如 Adobe RGB 红原色）在此被钳到 P3 原色，这是 8 位 P3 编码的既有边界。
    /// </summary>
    private static unsafe DecodedImage DecodeDisplayP3(CGImage image, int width, int height, int sourceWidth, int sourceHeight, int orientation, CancellationToken cancellationToken)
    {
        int stride = checked(width * 4);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            using var displayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3) ?? throw new UserFacingException("系统不支持 Display P3 色彩空间");
            fixed (byte* pixels = buffer)
            {
                // PremultipliedLast + 默认字节序 = 内存中的 R,G,B,A 顺序。
                using var context = new CGBitmapContext((IntPtr)pixels, width, height, 8, stride,
                    displayP3, CGImageAlphaInfo.PremultipliedLast);
                context.SetBlendMode(CGBlendMode.Copy);
                context.DrawImage(new CGRect(0, 0, width, height), image);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.Rgba8,
                ImageColorSpace.DisplayP3, sourceWidth, sourceHeight, orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>JPEG 无内嵌 ICC 时按共享 EXIF/互操作规则解释像素，不覆盖已声明的配置文件。</summary>
    private static CGImage? InterpretUntaggedJpeg(CGImage image, CoreGraphics.CGImageProperties? properties, string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return null;
        if (HasEmbeddedJpegProfile(path) || image.ColorSpace?.Model != CGColorSpaceModel.RGB) return null;
        using var exifKey = new NSString("{Exif}");
        using var colorKey = new NSString("ColorSpace");
        using var interopKey = new NSString("{ExifInteroperability}");
        using var indexKey = new NSString("InteroperabilityIndex");
        var exif = properties?.Dictionary[exifKey] as NSDictionary;
        var interop = properties?.Dictionary[interopKey] as NSDictionary;
        int? color = (exif?[colorKey] as NSNumber)?.Int32Value;
        string? index = interop?[indexKey]?.ToString();
        var inferred = ColorEngine.InferJpegColorSpace(color, index);
        using var space = CGColorSpace.CreateWithName(inferred == ImageColorSpace.AdobeRgb ? CGColorSpaceNames.AdobeRgb1998 : CGColorSpaceNames.Srgb)
            ?? throw new UserFacingException("无法确定 JPEG 照片的色彩空间");
        return image.WithColorSpace(space) ?? throw new UserFacingException("无法正确解释 JPEG 照片的色彩");
    }

    /// <summary>扫描 JPEG 头部的 ICC APP2 段；ImageIO 的 ProfileName 可能是系统补的默认值，不能代表内嵌 ICC。</summary>
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
}
