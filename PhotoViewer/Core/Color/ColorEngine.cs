using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Core.Color;

/// <summary>使用 Skia 的 skcms 色彩转换；浮点产物保留广色域，8 位产物供既有分析口径。</summary>
public static class ColorEngine
{
    /// <summary>半精度位模式总数（查表上界）。</summary>
    private const int HalfPatternCount = 65536;

    /// <summary>半精度 1.0 的位模式，即预乘 alpha 的不透明值。</summary>
    private const ushort HalfOne = 0x3C00;

    /// <summary>8 位不透明 alpha。</summary>
    private const byte ByteOpaque = 255;

    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    private static readonly SKColorSpace LinearSrgb = SKColorSpace.CreateSrgbLinear();
    private static readonly SKColorSpace DisplayP3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
    private static readonly SKColorSpace AdobeRgb = SKColorSpace.CreateRgb(
        new SKColorSpaceTransferFn(563f / 256f, 1, 0, 0, 0, 0, 0),
        SKColorSpaceXyz.AdobeRgb);

    /// <summary>线性 F16 → sRGB8 的逐通道传递查表，由 skcms 枚举全部半精度位模式生成，故逐字节等同 skcms。</summary>
    private static readonly Lazy<HalfToByteLut> LinearToSrgb8Lut =
        new(BuildLinearToSrgb8Lut, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>sRGB8 → 线性 F16 的逐通道传递查表，同样由 skcms 生成。</summary>
    private static readonly Lazy<ByteToHalfLut> Srgb8ToLinearLut =
        new(BuildSrgb8ToLinearLut, LazyThreadSafetyMode.ExecutionAndPublication);

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

    /// <summary>转换为线性 scRGB16，并显式指定源尺寸与待应用方向（供原生主图解码器：8 位解码路径不携带这两项）。</summary>
    /// <param name="source">已解码且携带色彩标签的图像，像素为未旋转状态。</param>
    /// <param name="sourceWidth">源（未旋转）全分辨率宽。</param>
    /// <param name="sourceHeight">源（未旋转）全分辨率高。</param>
    /// <param name="orientation">EXIF 方向 1-8，由渲染层消费，不做像素旋转。</param>
    /// <returns>须由调用方释放的浮点图像。</returns>
    public static DecodedImage ToScRgb16(DecodedImage source, int sourceWidth, int sourceHeight, int orientation)
        => Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb, LinearSrgb, sourceWidth, sourceHeight, orientation);

    /// <summary>转换为 BGRA8 sRGB，保留旧分析与模型输入所需的字节布局。</summary>
    /// <param name="source">已解码图像。</param>
    /// <returns>须由调用方释放的 sRGB 图像。</returns>
    public static DecodedImage ToSrgb8(DecodedImage source)
        => Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb, Srgb);

    /// <summary>转换为 8 位 Display P3（RGBA 字节序、sRGB 传递曲线），供 8 位展示档在保色处理后回到原展示编码。</summary>
    /// <param name="source">已解码图像（通常是刚处理过的 sRGB8 副本）。</param>
    /// <returns>须由调用方释放的 8 位 Display P3 图像。</returns>
    public static DecodedImage ToDisplayP3(DecodedImage source)
        => Convert(source, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3, DisplayP3);

    /// <summary>为既有 CV 算法提供显示朝向的 sRGB8，不改变其亮度/网格数值口径（含 8 位 Display P3 展示产物）。</summary>
    public static DecodedImage ToAnalysisSrgb8(DecodedImage source)
        => RotateForAnalysis(ToSrgb8(source), source.Orientation);

    /// <summary>为既有 CV 算法提供显示朝向的 sRGB8，方向由调用方给出（供原生壳：8 位解码路径不带方向）。</summary>
    /// <param name="source">已解码的未旋转图像。</param>
    /// <param name="orientation">EXIF 方向 1-8。</param>
    /// <returns>须由调用方释放的 sRGB 图像。</returns>
    public static DecodedImage ToAnalysisSrgb8(DecodedImage source, int orientation)
        => RotateForAnalysis(ToSrgb8(source), orientation);

    /// <summary>按 EXIF 方向旋转已转换的图像；方向 1 直通。</summary>
    private static DecodedImage RotateForAnalysis(DecodedImage converted, int orientation)
    {
        var (rotationCw, mirror) = ImageOrientationMapping.ToDisplay(orientation);
        if (rotationCw == 0 && !mirror) return converted;
        try { return SkiaImageCodec.ApplyOrientation(converted, rotationCw, mirror) ?? throw new InvalidOperationException("分析图方向变换失败"); }
        finally { converted.Release(); }
    }

    /// <summary>在 pinned 像素间进行色域与位深转换，失败显式抛出并归还目标缓冲。</summary>
    /// <param name="source">源图像。</param>
    /// <param name="encoding">目标像素编码。</param>
    /// <param name="colorSpace">目标色彩标签。</param>
    /// <param name="targetSpace">目标 skcms 色彩空间。</param>
    /// <param name="sourceWidth">源全分辨率宽;null 表示沿用源图像元数据。</param>
    /// <param name="sourceHeight">源全分辨率高;null 表示沿用源图像元数据。</param>
    /// <param name="orientation">EXIF 方向;null 表示沿用源图像元数据。</param>
    private static unsafe DecodedImage Convert(DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace, SKColorSpace targetSpace,
        int? sourceWidth = null, int? sourceHeight = null, int? orientation = null)
    {
        // 元数据不可变:要求覆盖元数据时不能走"原格式直通"快路径。
        if (sourceWidth == null && sourceHeight == null && orientation == null &&
            source.Encoding == encoding && source.ColorSpace == colorSpace)
        {
            if (!source.TryAcquire()) throw new ObjectDisposedException(nameof(source));
            return source;
        }

        // 同原色、仅传递函数不同(线性 sRGB ↔ sRGB)的常用配对走逐通道查表；其余仍由 skcms 处理。
        if (TryConvertSamePrimaries(source, encoding, colorSpace, sourceWidth, sourceHeight, orientation) is { } converted)
            return converted;

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
                colorSpace,
                sourceWidth ?? source.SourceWidth,
                sourceHeight ?? source.SourceHeight,
                orientation ?? source.Orientation);
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

    /// <summary>同原色的线性 sRGB ↔ sRGB 配对走查表快通道；不适用或 alpha 非不透明时返回 null 交回 skcms。</summary>
    /// <param name="source">源图像。</param>
    /// <param name="encoding">目标像素编码。</param>
    /// <param name="colorSpace">目标色彩标签。</param>
    /// <param name="sourceWidth">源全分辨率宽覆盖值。</param>
    /// <param name="sourceHeight">源全分辨率高覆盖值。</param>
    /// <param name="orientation">EXIF 方向覆盖值。</param>
    /// <returns>转换结果；空表示应回退到 skcms。</returns>
    private static unsafe DecodedImage? TryConvertSamePrimaries(DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace,
        int? sourceWidth, int? sourceHeight, int? orientation)
    {
        if (colorSpace == ImageColorSpace.Srgb && encoding is PixelEncoding.Bgra32 or PixelEncoding.Rgba8 &&
            source.Encoding == PixelEncoding.RgbaF16 && source.ColorSpace == ImageColorSpace.LinearSrgb)
            return ConvertLinearToSrgb8(source, encoding, sourceWidth, sourceHeight, orientation);

        if (colorSpace == ImageColorSpace.LinearSrgb && encoding == PixelEncoding.RgbaF16 &&
            source.Encoding is PixelEncoding.Bgra32 or PixelEncoding.Rgba8 or PixelEncoding.Rgb24 &&
            source.ColorSpace is ImageColorSpace.Srgb or ImageColorSpace.Legacy)
            return ConvertSrgb8ToLinear(source, sourceWidth, sourceHeight, orientation);

        return null;
    }

    /// <summary>线性 F16 → sRGB8：同原色下只剩传递函数编码，逐像素三通道查表即可；alpha 非不透明时退回 skcms。</summary>
    private static unsafe DecodedImage? ConvertLinearToSrgb8(DecodedImage source, PixelEncoding encoding,
        int? sourceWidth, int? sourceHeight, int? orientation)
    {
        var lut = LinearToSrgb8Lut.Value;
        byte[] lutR = lut.R, lutG = lut.G, lutB = lut.B;
        bool rgbaOrder = encoding == PixelEncoding.Rgba8;
        int width = source.Width;
        int height = source.Height;
        int sourceStride = source.Stride;
        int targetStride = checked(width * 4);
        var buffer = DecodedImage.RentPixels(checked(targetStride * height));
        int opaqueBreach = 0;
        try
        {
            fixed (byte* sourcePixels = source.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                IntPtr sourceBase = (IntPtr)sourcePixels, targetBase = (IntPtr)targetPixels;
                ForEachRowBand(height, (start, end) =>
                {
                    bool breach = false;
                    byte* source0 = (byte*)sourceBase;
                    byte* target0 = (byte*)targetBase;
                    for (int row = start; row < end; row++)
                    {
                        ushort* sourceRow = (ushort*)(source0 + (long)row * sourceStride);
                        byte* targetRow = target0 + (long)row * targetStride;
                        if (rgbaOrder)
                        {
                            for (int x = 0; x < width; x++, sourceRow += 4, targetRow += 4)
                            {
                                if (sourceRow[3] != HalfOne) breach = true;
                                targetRow[0] = lutR[sourceRow[0]];
                                targetRow[1] = lutG[sourceRow[1]];
                                targetRow[2] = lutB[sourceRow[2]];
                                targetRow[3] = ByteOpaque;
                            }
                        }
                        else
                        {
                            for (int x = 0; x < width; x++, sourceRow += 4, targetRow += 4)
                            {
                                if (sourceRow[3] != HalfOne) breach = true;
                                targetRow[0] = lutB[sourceRow[2]];
                                targetRow[1] = lutG[sourceRow[1]];
                                targetRow[2] = lutR[sourceRow[0]];
                                targetRow[3] = ByteOpaque;
                            }
                        }
                    }
                    if (breach) Interlocked.Exchange(ref opaqueBreach, 1);
                });
            }
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
        if (Volatile.Read(ref opaqueBreach) != 0)
        {
            ImageBufferPool.Return(buffer);
            return null;
        }
        return DecodedImage.CreatePooled(buffer, width, height, targetStride, encoding, ImageColorSpace.Srgb,
            sourceWidth ?? source.SourceWidth, sourceHeight ?? source.SourceHeight, orientation ?? source.Orientation);
    }

    /// <summary>sRGB8 → 线性 F16：同原色，逐通道查表得到半精度位模式；alpha 非不透明时退回 skcms。</summary>
    private static unsafe DecodedImage? ConvertSrgb8ToLinear(DecodedImage source,
        int? sourceWidth, int? sourceHeight, int? orientation)
    {
        var lut = Srgb8ToLinearLut.Value;
        ushort[] lutR = lut.R, lutG = lut.G, lutB = lut.B;
        var sourceEncoding = source.Encoding;
        int width = source.Width;
        int height = source.Height;
        int sourceStride = source.Stride;
        int targetStride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(targetStride * height));
        int opaqueBreach = 0;
        try
        {
            fixed (byte* sourcePixels = source.Pixels)
            fixed (byte* targetPixels = buffer)
            {
                IntPtr sourceBase = (IntPtr)sourcePixels, targetBase = (IntPtr)targetPixels;
                ForEachRowBand(height, (start, end) =>
                {
                    bool breach = false;
                    byte* source0 = (byte*)sourceBase;
                    byte* target0 = (byte*)targetBase;
                    for (int row = start; row < end; row++)
                    {
                        byte* sourceRow = source0 + (long)row * sourceStride;
                        ushort* targetRow = (ushort*)(target0 + (long)row * targetStride);
                        switch (sourceEncoding)
                        {
                            case PixelEncoding.Bgra32:
                                for (int x = 0; x < width; x++, sourceRow += 4, targetRow += 4)
                                {
                                    if (sourceRow[3] != ByteOpaque) breach = true;
                                    targetRow[0] = lutR[sourceRow[2]];
                                    targetRow[1] = lutG[sourceRow[1]];
                                    targetRow[2] = lutB[sourceRow[0]];
                                    targetRow[3] = HalfOne;
                                }
                                break;
                            case PixelEncoding.Rgba8:
                                for (int x = 0; x < width; x++, sourceRow += 4, targetRow += 4)
                                {
                                    if (sourceRow[3] != ByteOpaque) breach = true;
                                    targetRow[0] = lutR[sourceRow[0]];
                                    targetRow[1] = lutG[sourceRow[1]];
                                    targetRow[2] = lutB[sourceRow[2]];
                                    targetRow[3] = HalfOne;
                                }
                                break;
                            default:
                                for (int x = 0; x < width; x++, sourceRow += 3, targetRow += 4)
                                {
                                    targetRow[0] = lutR[sourceRow[0]];
                                    targetRow[1] = lutG[sourceRow[1]];
                                    targetRow[2] = lutB[sourceRow[2]];
                                    targetRow[3] = HalfOne;
                                }
                                break;
                        }
                    }
                    if (breach) Interlocked.Exchange(ref opaqueBreach, 1);
                });
            }
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
        if (Volatile.Read(ref opaqueBreach) != 0)
        {
            ImageBufferPool.Return(buffer);
            return null;
        }
        return DecodedImage.CreatePooled(buffer, width, height, targetStride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb,
            sourceWidth ?? source.SourceWidth, sourceHeight ?? source.SourceHeight, orientation ?? source.Orientation);
    }

    /// <summary>按行分带并行；小图退化为串行，避免调度开销压过像素量。</summary>
    private static void ForEachRowBand(int height, Action<int, int> rows)
    {
        int bands = Math.Min(Environment.ProcessorCount * 4, height / 32);
        if (bands <= 1)
        {
            rows(0, height);
            return;
        }
        Parallel.For(0, bands, band =>
        {
            int perBand = (height + bands - 1) / bands;
            int start = band * perBand;
            int end = Math.Min(height, start + perBand);
            if (start < end) rows(start, end);
        });
    }

    /// <summary>用 skcms 本身跑一行像素；查表的内容因此与通用转换逐字节一致（含 NaN/Inf/负值）。</summary>
    private static unsafe void SkcmsRow(SKImageInfo sourceInfo, byte[] sourceRow, SKImageInfo targetInfo, byte[] targetRow)
    {
        int sourceStride = checked(sourceInfo.Width * sourceInfo.BytesPerPixel);
        int targetStride = checked(targetInfo.Width * targetInfo.BytesPerPixel);
        fixed (byte* sourcePixels = sourceRow)
        fixed (byte* targetPixels = targetRow)
        {
            using var pixmap = new SKPixmap(sourceInfo, (IntPtr)sourcePixels, sourceStride);
            if (!pixmap.ReadPixels(targetInfo, (IntPtr)targetPixels, targetStride))
                throw new InvalidOperationException("skcms 查表构建失败");
        }
    }

    /// <summary>构建线性 F16 → sRGB8 查表：枚举全部半精度位模式，取 skcms 的 8 位结果。</summary>
    private static HalfToByteLut BuildLinearToSrgb8Lut()
    {
        var sourceRow = new byte[HalfPatternCount * 8];
        var targetRow = new byte[HalfPatternCount * 4];
        var halves = MemoryMarshal.Cast<byte, ushort>(sourceRow.AsSpan());
        for (int pattern = 0; pattern < HalfPatternCount; pattern++)
        {
            ushort half = (ushort)pattern;
            int at = pattern * 4;
            halves[at] = half;
            halves[at + 1] = half;
            halves[at + 2] = half;
            halves[at + 3] = HalfOne;
        }
        SkcmsRow(
            new SKImageInfo(HalfPatternCount, 1, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb), sourceRow,
            new SKImageInfo(HalfPatternCount, 1, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb), targetRow);

        var channelR = new byte[HalfPatternCount];
        var channelG = new byte[HalfPatternCount];
        var channelB = new byte[HalfPatternCount];
        bool unified = true;
        for (int pattern = 0; pattern < HalfPatternCount; pattern++)
        {
            byte b = targetRow[pattern * 4];
            byte g = targetRow[pattern * 4 + 1];
            byte r = targetRow[pattern * 4 + 2];
            channelB[pattern] = b;
            channelG[pattern] = g;
            channelR[pattern] = r;
            if (b != g || g != r) unified = false;
        }
        // 三通道数学一致时共用一张表；否则按各自通道单独枚举，保持通道无关性。
        if (unified) return new HalfToByteLut(channelR, channelR, channelR);
        return new HalfToByteLut(
            BuildLinearToSrgb8Channel(sourceChannel: 0),
            BuildLinearToSrgb8Channel(sourceChannel: 1),
            BuildLinearToSrgb8Channel(sourceChannel: 2));
    }

    /// <summary>只让指定源通道枚举全部半精度位模式，其余通道为 0，取该颜色分量的 8 位结果。</summary>
    private static byte[] BuildLinearToSrgb8Channel(int sourceChannel)
    {
        var sourceRow = new byte[HalfPatternCount * 8];
        var targetRow = new byte[HalfPatternCount * 4];
        var halves = MemoryMarshal.Cast<byte, ushort>(sourceRow.AsSpan());
        for (int pattern = 0; pattern < HalfPatternCount; pattern++)
        {
            int at = pattern * 4;
            halves[at + sourceChannel] = (ushort)pattern;
            halves[at + 3] = HalfOne;
        }
        SkcmsRow(
            new SKImageInfo(HalfPatternCount, 1, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb), sourceRow,
            new SKImageInfo(HalfPatternCount, 1, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb), targetRow);
        // 目标为 Bgra8888：色分量 R/G/B 依次落在字节 2/1/0。
        int targetByte = sourceChannel == 0 ? 2 : sourceChannel == 2 ? 0 : 1;
        var table = new byte[HalfPatternCount];
        for (int pattern = 0; pattern < HalfPatternCount; pattern++) table[pattern] = targetRow[pattern * 4 + targetByte];
        return table;
    }

    /// <summary>构建 sRGB8 → 线性 F16 查表：枚举 256 个编码值，取 skcms 的半精度位模式。</summary>
    private static ByteToHalfLut BuildSrgb8ToLinearLut()
    {
        var sourceRow = new byte[256 * 4];
        var targetRow = new byte[256 * 8];
        for (int value = 0; value < 256; value++)
        {
            sourceRow[value * 4] = (byte)value;
            sourceRow[value * 4 + 1] = (byte)value;
            sourceRow[value * 4 + 2] = (byte)value;
            sourceRow[value * 4 + 3] = ByteOpaque;
        }
        SkcmsRow(
            new SKImageInfo(256, 1, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb), sourceRow,
            new SKImageInfo(256, 1, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb), targetRow);
        var targetHalves = MemoryMarshal.Cast<byte, ushort>(targetRow.AsSpan());

        var channelR = new ushort[256];
        var channelG = new ushort[256];
        var channelB = new ushort[256];
        bool unified = true;
        for (int value = 0; value < 256; value++)
        {
            ushort r = targetHalves[value * 4];
            ushort g = targetHalves[value * 4 + 1];
            ushort b = targetHalves[value * 4 + 2];
            channelR[value] = r;
            channelG[value] = g;
            channelB[value] = b;
            if (r != g || g != b) unified = false;
        }
        if (unified) return new ByteToHalfLut(channelR, channelR, channelR);
        return new ByteToHalfLut(
            BuildSrgb8ToLinearChannel(component: 0),
            BuildSrgb8ToLinearChannel(component: 1),
            BuildSrgb8ToLinearChannel(component: 2));
    }

    /// <summary>只让指定颜色分量取值，其余分量（含 alpha）由调用方保持不透明，取该分量的半精度位模式。</summary>
    private static ushort[] BuildSrgb8ToLinearChannel(int component)
    {
        var sourceRow = new byte[256 * 4];
        var targetRow = new byte[256 * 8];
        // 源为 Bgra8888：色分量 R/G/B 依次落在字节 2/1/0。
        int sourceByte = component == 0 ? 2 : component == 2 ? 0 : 1;
        for (int value = 0; value < 256; value++)
        {
            sourceRow[value * 4 + sourceByte] = (byte)value;
            sourceRow[value * 4 + 3] = ByteOpaque;
        }
        SkcmsRow(
            new SKImageInfo(256, 1, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb), sourceRow,
            new SKImageInfo(256, 1, SKColorType.RgbaF16, SKAlphaType.Premul, LinearSrgb), targetRow);
        var targetHalves = MemoryMarshal.Cast<byte, ushort>(targetRow.AsSpan());
        var table = new ushort[256];
        for (int value = 0; value < 256; value++) table[value] = targetHalves[value * 4 + component];
        return table;
    }

    /// <summary>半精度位模式 → 8 位 sRGB 的各通道查表。</summary>
    private sealed class HalfToByteLut(byte[] r, byte[] g, byte[] b)
    {
        public readonly byte[] R = r;
        public readonly byte[] G = g;
        public readonly byte[] B = b;
    }

    /// <summary>8 位 sRGB → 半精度位模式的各通道查表。</summary>
    private sealed class ByteToHalfLut(ushort[] r, ushort[] g, ushort[] b)
    {
        public readonly ushort[] R = r;
        public readonly ushort[] G = g;
        public readonly ushort[] B = b;
    }
}
