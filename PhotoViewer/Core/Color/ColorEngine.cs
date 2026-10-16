using System;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Core.Color;

/// <summary>使用 Skia 的 skcms 色彩转换；浮点产物保留广色域，8 位产物供既有分析口径。</summary>
public static class ColorEngine
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    private static readonly SKColorSpace LinearSrgb = SKColorSpace.CreateSrgbLinear();
    private static readonly SKColorSpace DisplayP3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
    private static readonly SKColorSpace AdobeRgb = SKColorSpace.CreateRgb(
        new SKColorSpaceTransferFn(563f / 256f, 1, 0, 0, 0, 0, 0),
        SKColorSpaceXyz.AdobeRgb);

    /// <summary>解析无 ICC 的 JPEG 色域；EXIF Adobe RGB 或互操作标识 R03 优先，其余按 sRGB。</summary>
    /// <param name="exifColorSpace">EXIF ColorSpace 的数值。</param>
    /// <param name="interoperabilityIndex">互操作标识，例如 R03。</param>
    /// <returns>推断色域。</returns>
    public static ImageColorSpace InferJpegColorSpace(int? exifColorSpace, string? interoperabilityIndex = null)
        => exifColorSpace == 2 || interoperabilityIndex == "R03" ? ImageColorSpace.AdobeRgb : ImageColorSpace.Srgb;

    /// <summary>转换为 RGBA 半精度线性 scRGB，返回独立租约，不接管源图像。</summary>
    /// <param name="source">已解码且携带色彩标签的图像。</param>
    /// <returns>须由调用方释放的浮点图像。</returns>
    public static DecodedImage ToScRgb16(DecodedImage source)
        => Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb, LinearSrgb);

    /// <summary>转换为 BGRA8 sRGB，保留旧分析与模型输入所需的字节布局。</summary>
    /// <param name="source">已解码图像。</param>
    /// <returns>须由调用方释放的 sRGB 图像。</returns>
    public static DecodedImage ToSrgb8(DecodedImage source)
        => Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb, Srgb);

    /// <summary>为既有 CV 算法提供显示朝向的 sRGB8，不改变其亮度/网格数值口径（含 8 位 Display P3 展示产物）。</summary>
    public static DecodedImage ToAnalysisSrgb8(DecodedImage source)
    {
        var converted = ToSrgb8(source);
        var (rotationCw, mirror) = ImageOrientationMapping.ToDisplay(source.Orientation);
        if (rotationCw == 0 && !mirror) return converted;
        try { return SkiaImageCodec.ApplyOrientation(converted, rotationCw, mirror) ?? throw new InvalidOperationException("分析图方向变换失败"); }
        finally { converted.Release(); }
    }

    /// <summary>在 pinned 像素间进行色域与位深转换，失败显式抛出并归还目标缓冲。</summary>
    private static unsafe DecodedImage Convert(DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace, SKColorSpace targetSpace)
    {
        if (source.Encoding == encoding && source.ColorSpace == colorSpace)
        {
            if (!source.TryAcquire()) throw new ObjectDisposedException(nameof(source));
            return source;
        }

        using var rgbConverted = source.Encoding == PixelEncoding.Rgb24 ? SkiaImageCodec.ToBgra32(source) : null;
        var original = rgbConverted ?? source;
        using var packed = original.Stride % DecodedImage.BytesPerPixel(original.Encoding) != 0 ? PackRows(original) : null;
        var effective = packed ?? original;
        using var customSpace = source.ColorSpace == ImageColorSpace.Icc
            ? SKColorSpace.CreateIcc(source.IccProfile.ToArray()) ?? throw new InvalidOperationException("无法解析图像 ICC profile")
            : null;
        var sourceSpace = customSpace ?? source.ColorSpace switch
        {
            ImageColorSpace.Legacy or ImageColorSpace.Srgb => Srgb,
            ImageColorSpace.DisplayP3 => DisplayP3,
            ImageColorSpace.AdobeRgb => AdobeRgb,
            ImageColorSpace.LinearSrgb => LinearSrgb,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        int stride = checked(source.Width * DecodedImage.BytesPerPixel(encoding));
        var buffer = DecodedImage.RentPixels(checked(stride * source.Height));
        try
        {
            fixed (byte* sourcePixels = effective.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                var sourceInfo = new SKImageInfo(source.Width, source.Height, ColorType(effective.Encoding), SKAlphaType.Premul, sourceSpace);
                var targetInfo = new SKImageInfo(source.Width, source.Height, ColorType(encoding), SKAlphaType.Premul, targetSpace);
                using var pixmap = new SKPixmap(sourceInfo, (IntPtr)sourcePixels, effective.Stride);
                if (!pixmap.ReadPixels(targetInfo, (IntPtr)targetPixels, stride))
                    throw new InvalidOperationException("skcms 像素色彩转换失败");
            }
            return DecodedImage.CreatePooled(buffer, source.Width, source.Height, stride, encoding,
                colorSpace, source.SourceWidth, source.SourceHeight, source.Orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>像素编码 → Skia 色彩类型；不支持的编码显式抛出，不做静默猜字节序。</summary>
    /// <param name="encoding">像素编码。</param>
    /// <returns>Skia 色彩类型。</returns>
    private static SKColorType ColorType(PixelEncoding encoding) => encoding switch
    {
        PixelEncoding.RgbaF16 => SKColorType.RgbaF16,
        PixelEncoding.Bgra32 => SKColorType.Bgra8888,
        PixelEncoding.Rgba8 => SKColorType.Rgba8888,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>skcms 要求行距按像素对齐，非对齐输入按行打包，保留有效像素而不读取末行填充。</summary>
    private static DecodedImage PackRows(DecodedImage source)
    {
        int stride = checked(source.Width * DecodedImage.BytesPerPixel(source.Encoding));
        var buffer = DecodedImage.RentPixels(checked(stride * source.Height));
        for (int row = 0; row < source.Height; row++) Array.Copy(source.Pixels, row * source.Stride, buffer, row * stride, stride);
        return DecodedImage.CreatePooled(buffer, source.Width, source.Height, stride, source.Encoding, source.ColorSpace);
    }
}
