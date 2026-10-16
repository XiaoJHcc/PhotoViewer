using CoreGraphics;
using Foundation;
using ImageIO;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

/// <summary>ImageIO 的 JPEG/HEIF/RAW 统一主图解码器：按请求编码输出 RGBA16F 扩展线性 sRGB 或 8 位 Display P3，方向留给 GPU。</summary>
public sealed class ApplePresentationDecoder : IPresentationImageDecoder
{
    /// <summary>在线程池解码；编码器子采样限制代理尺寸，不使用容器内的小缩略图。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次必须产出的像素编码（F16 扩展线性 sRGB 或 8 位 Display P3）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() => Decode(file.PathKey, maxLongEdge, encoding, cancellationToken), cancellationToken);

    /// <summary>无内嵌预览的用户图片按短边缩解码，保持 ICC 与方向正确，不走原图全尺寸路径。</summary>
    public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() =>
        {
            using var pool = new NSAutoreleasePool();
            using var url = NSUrl.FromFilename(file.PathKey);
            using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false }) ?? throw new InvalidOperationException("ImageIO 无法读取缩略图尺寸");
            var properties = source.GetProperties(0, null);
            int width = properties?.PixelWidth ?? 0, height = properties?.PixelHeight ?? 0;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("缩略图源尺寸无效");
            int target = Math.Max(minShortEdge, (int)Math.Ceiling((double)minShortEdge * Math.Max(width, height) / Math.Min(width, height)));
            using var image = Decode(file.PathKey, target, PixelEncoding.RgbaF16, cancellationToken);
            return ColorEngine.ToAnalysisSrgb8(image);
        }, cancellationToken);

    /// <summary>读取原图色彩/尺寸/方向，并在 pinned 池化缓冲内完成一次转换。</summary>
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
        using var url = NSUrl.FromFilename(path);
        using var source = CGImageSource.FromUrl(url, new CGImageOptions { ShouldCache = false })
            ?? throw new InvalidOperationException("ImageIO 无法打开图片");
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
        if (image == null) throw new InvalidOperationException("ImageIO 无法解码图片（格式或机型不受系统支持）");
        using var inferredImage = InterpretUntaggedJpeg(image, properties, path);
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        return encoding == PixelEncoding.RgbaF16
            ? DecodeLinearF16(inferredImage ?? image, width, height, sourceWidth, sourceHeight, orientation, cancellationToken)
            : DecodeDisplayP3(inferredImage ?? image, width, height, sourceWidth, sourceHeight, orientation, cancellationToken);
    }

    /// <summary>绘制到扩展线性 sRGB 的半精度浮点缓冲；广色域原色允许超过一，由 scRGB 承载不裁剪。</summary>
    private static unsafe DecodedImage DecodeLinearF16(CGImage image, int width, int height, int sourceWidth, int sourceHeight, int orientation, CancellationToken cancellationToken)
    {
        int stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            using var linearSpace = CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
                ?? throw new InvalidOperationException("系统不支持扩展线性 sRGB");
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
            using var displayP3 = CGColorSpace.CreateWithName(CGColorSpaceNames.DisplayP3) ?? throw new InvalidOperationException("系统不支持 Display P3");
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
            ?? throw new InvalidOperationException("JPEG 推断色彩空间不可用");
        return image.WithColorSpace(space) ?? throw new InvalidOperationException("JPEG 色彩解释失败");
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
