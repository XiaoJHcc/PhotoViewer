using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace ColorEngineLutCheck;

/// <summary>
/// 串行 skcms 参考：整幅一个 <see cref="SKPixmap"/>、一次 <c>ReadPixels</c>，不并行、不分带、不加容差。
/// 色域映射与 <c>SkColorSpaces</c> 同口径（Legacy 按 sRGB，ICC 由 profile 字节新建）。
/// </summary>
internal static class ReferenceConversion
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    private static readonly SKColorSpace LinearSrgb = SKColorSpace.CreateSrgbLinear();
    private static readonly SKColorSpace DisplayP3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
    private static readonly SKColorSpace AdobeRgb = SKColorSpace.CreateRgb(
        new SKColorSpaceTransferFn(563f / 256f, 1, 0, 0, 0, 0, 0), SKColorSpaceXyz.AdobeRgb);

    /// <summary>色彩标签 + ICC 字节 → Skia 色彩空间；ICC 为新建对象，须由调用方按 <paramref name="owned"/> 释放。</summary>
    public static SKColorSpace Resolve(ImageColorSpace colorSpace, ReadOnlyMemory<byte> iccProfile, out bool owned)
    {
        owned = colorSpace == ImageColorSpace.Icc;
        return colorSpace switch
        {
            ImageColorSpace.Legacy or ImageColorSpace.Srgb => Srgb,
            ImageColorSpace.LinearSrgb => LinearSrgb,
            ImageColorSpace.DisplayP3 => DisplayP3,
            ImageColorSpace.AdobeRgb => AdobeRgb,
            ImageColorSpace.Icc => SKColorSpace.CreateIcc(iccProfile.Span)
                ?? throw new InvalidOperationException("无法解析 ICC profile"),
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace)),
        };
    }

    /// <summary>
    /// 串行 skcms 转换：整幅一个 pixmap，目标缓冲串行分配（<paramref name="pooled"/> 时与 ColorEngine 同走
    /// <see cref="ImageBufferPool"/>，供公平计时）。
    /// </summary>
    public static unsafe DecodedImage Convert(DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace,
        ReadOnlyMemory<byte> targetIcc = default, int? sourceWidth = null, int? sourceHeight = null, int? orientation = null,
        bool pooled = false)
    {
        DecodedImage? rgbConverted = null;
        DecodedImage? packed = null;
        SKColorSpace? sourceSpace = null;
        SKColorSpace? targetSpace = null;
        bool ownedSource = false, ownedTarget = false;
        byte[]? buffer = null;
        try
        {
            var original = source;
            if (source.Encoding == PixelEncoding.Rgb24)
            {
                rgbConverted = SkiaImageCodec.ToBgra32(source) ?? throw new InvalidOperationException("ToBgra32 failed");
                original = rgbConverted;
            }
            if (original.Stride % DecodedImage.BytesPerPixel(original.Encoding) != 0)
            {
                packed = PackRows(original);
                original = packed;
            }
            sourceSpace = Resolve(source.ColorSpace, source.IccProfile, out ownedSource);
            targetSpace = Resolve(colorSpace, targetIcc, out ownedTarget);

            int width = original.Width, height = original.Height;
            int stride = checked(width * DecodedImage.BytesPerPixel(encoding));
            buffer = pooled ? DecodedImage.RentPixels(checked(stride * height)) : new byte[checked(stride * height)];
            fixed (byte* sourcePixels = original.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                var sourceInfo = new SKImageInfo(width, height, ColorType(original.Encoding), SKAlphaType.Premul, sourceSpace);
                var targetInfo = new SKImageInfo(width, height, ColorType(encoding), SKAlphaType.Premul, targetSpace);
                using var pixmap = new SKPixmap(sourceInfo, (IntPtr)sourcePixels, original.Stride);
                if (!pixmap.ReadPixels(targetInfo, (IntPtr)targetPixels, stride))
                    throw new InvalidOperationException("skcms pixel transform failed");
            }

            var icc = colorSpace == ImageColorSpace.Icc ? targetIcc.ToArray() : null;
            int sw = sourceWidth ?? source.SourceWidth, sh = sourceHeight ?? source.SourceHeight;
            int orient = orientation ?? source.Orientation;
            var result = pooled
                ? DecodedImage.CreatePooled(buffer, width, height, stride, encoding, colorSpace, sw, sh, orient, icc)
                : new DecodedImage(buffer, width, height, stride, encoding, colorSpace, sw, sh, orient, icc);
            buffer = null;
            return result;
        }
        catch
        {
            if (buffer != null && pooled) ImageBufferPool.Return(buffer);
            throw;
        }
        finally
        {
            if (ownedSource) sourceSpace?.Dispose();
            if (ownedTarget) targetSpace?.Dispose();
            packed?.Release();
            rgbConverted?.Release();
        }
    }

    /// <summary>像素编码 → Skia 色彩类型；不支持的编码显式抛出。</summary>
    public static SKColorType ColorType(PixelEncoding encoding) => encoding switch
    {
        PixelEncoding.RgbaF16 => SKColorType.RgbaF16,
        PixelEncoding.Bgra32 => SKColorType.Bgra8888,
        PixelEncoding.Rgba8 => SKColorType.Rgba8888,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>skcms 要求行距按像素对齐，非对齐输入按行打包（与 ColorEngine 同口径）。</summary>
    private static DecodedImage PackRows(DecodedImage source)
    {
        int stride = checked(source.Width * DecodedImage.BytesPerPixel(source.Encoding));
        var buffer = new byte[checked(stride * source.Height)];
        for (int row = 0; row < source.Height; row++)
            Buffer.BlockCopy(source.Pixels, row * source.Stride, buffer, row * stride, stride);
        return new DecodedImage(buffer, source.Width, source.Height, stride, source.Encoding, source.ColorSpace,
            source.SourceWidth, source.SourceHeight, source.Orientation, source.IccProfile.ToArray());
    }
}
