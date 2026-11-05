using System;
using System.IO;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using SkiaSharp;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 基于 SkiaSharp 的解码/缩放/旋转/裁剪像素原语,Core 层唯一的像素操作出口。
/// 解码产物保留**文件内的编码值**:像素原样保留 JPEG/PNG 里存的值,真实源色域由 <see cref="DecodedImage.ColorSpace"/>
/// (含 ICC)声明,不在解码期先收敛到 sRGB —— 收敛到显示口径是各平台展示解码器与色彩转换的职责。
/// 所有几何变换(缩放/旋转/裁剪)保持编码、色彩标签、ICC、源尺寸与待应用方向不变,只改需要改的那一项。
/// </summary>
/// <remarks>
/// 色域表达约束:<see cref="ImageColorSpace"/> 只能用已知原色或 ICC 描述色域,而 Skia 对 JPEG/PNG 只暴露数值色彩空间
/// (实测 <c>SKColorSpace.ToProfile()</c> 恒为空,拿不到 ICC 原始字节)。因此:
/// JPEG 从容器 APP2/EXIF 自读 ICC(唯一可靠来源);非 JPEG 若原色就是 sRGB 则解码期精确换曲线后标 sRGB,
/// 能导出 ICC 时标 ICC 保留原值,否则直接解到 <see cref="PixelEncoding.RgbaF16"/> 线性 scRGB 保全广色域,
/// 绝不把广色域先钳到 sRGB 再称自己是 sRGB。
/// </remarks>
public static class SkiaImageCodec
{
    /// <summary>ICC profile 的最小长度(128 字节头)。</summary>
    private const int IccMinimumLength = 128;

    /// <summary>判定"原色就是 sRGB"的 D50 适配 RGB→XYZ 矩阵容差(实测 gAMA/cHRM 老 PNG 约 1e-4,广色域 ≥ 0.09)。</summary>
    private const float SrgbPrimariesTolerance = 1e-3f;

    /// <summary>
    /// 解码出口的源色彩描述。
    /// </summary>
    /// <param name="ColorSpace">产物色彩标签。</param>
    /// <param name="IccProfile">ICC 标签随产物携带的 profile 字节。</param>
    /// <param name="DecodeColorSpace">解码期目标色彩空间;null 表示保留文件内的编码值(不做色彩管理)。</param>
    /// <param name="Encoding">产物像素编码;广色域无法用 8 位 + ICC 表达时为 RgbaF16。</param>
    private readonly record struct SourceColor(ImageColorSpace ColorSpace, byte[]? IccProfile, SKColorSpace? DecodeColorSpace, PixelEncoding Encoding);

    /// <summary>像素信息:编码 → Skia 色彩类型,色彩空间由标签/ICC 解析(可为 null,表示不做色彩管理)。</summary>
    private static SKImageInfo InfoFor(PixelEncoding encoding, int width, int height, SKColorSpace? colorSpace)
        => new(width, height, ColorEngine.ColorType(encoding), SKAlphaType.Premul, colorSpace);

    /// <summary>
    /// 全尺寸解码图像流(JPEG/PNG 等 Skia 支持格式),保留文件内的编码值与真实源色域。
    /// 产物编码通常是 Bgra32;非 JPEG 的广色域容器无法用 ICC 表达时为 RgbaF16 线性 scRGB(见类型注释)。
    /// </summary>
    /// <param name="stream">图像字节流(需可寻址,以便读取容器色彩元数据)。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? Decode(Stream stream) => DecodeToFit(stream, int.MaxValue, int.MaxValue);

    /// <summary>
    /// 按目标宽度等比缩解码(语义同 <c>Bitmap.DecodeToWidth</c>):
    /// 先按 codec 支持的采样率解码,必要时再做一次高质量精确缩放。
    /// </summary>
    /// <param name="stream">图像字节流。</param>
    /// <param name="targetWidth">目标宽度(不放大,超过原始宽度时按原始宽度)。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? DecodeToWidth(Stream stream, int targetWidth)
    {
        if (targetWidth <= 0) return null;
        return DecodeToFit(stream, targetWidth, int.MaxValue);
    }

    /// <summary>
    /// 按长边上限等比缩解码(代理图口径):codec 子采样 + Mitchell 精修,不放大。
    /// 旋转无关:长边约束对 90° 旋转对称。
    /// </summary>
    /// <param name="stream">图像字节流。</param>
    /// <param name="targetLongEdge">目标长边像素。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? DecodeToLongEdge(Stream stream, int targetLongEdge)
    {
        if (targetLongEdge <= 0) return null;
        return DecodeToFit(stream, int.MaxValue, targetLongEdge);
    }

    /// <summary>
    /// 统一缩解码实现:宽 ≤ maxWidth 且长边 ≤ maxLongEdge 中取更严格的缩放;不放大。
    /// </summary>
    private static DecodedImage? DecodeToFit(Stream stream, int maxWidth, int maxLongEdge)
    {
        try
        {
            // 容器色彩元数据必须在建 codec 之前扫(读完即恢复流位置),之后 codec 独占该流。
            var metadata = JpegColorMetadata.Read(stream);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            var color = ResolveSourceColor(codec, metadata);
            int srcW = codec.Info.Width, srcH = codec.Info.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            // 计算等比缩放因子(两个约束取更严格;不放大)
            double scale = 1.0;
            if (maxWidth != int.MaxValue && maxWidth < srcW) scale = Math.Min(scale, (double)maxWidth / srcW);
            int longEdge = Math.Max(srcW, srcH);
            if (maxLongEdge != int.MaxValue && maxLongEdge < longEdge) scale = Math.Min(scale, (double)maxLongEdge / longEdge);

            if (scale >= 1.0)
            {
                using var full = SKBitmap.Decode(codec, InfoFor(color.Encoding, srcW, srcH, color.DecodeColorSpace));
                return FromSkBitmap(full, colorSpace: color.ColorSpace, iccProfile: color.IccProfile);
            }

            int targetW = Math.Max(1, (int)Math.Round(srcW * scale));
            int targetH = Math.Max(1, (int)Math.Round(srcH * scale));

            // codec 支持的最接近采样尺寸,再做一次 Mitchell 精确缩放
            var scaled = codec.GetScaledDimensions((float)scale);
            using var sampled = SKBitmap.Decode(codec,
                InfoFor(color.Encoding, scaled.Width, scaled.Height, color.DecodeColorSpace));
            if (sampled == null) return null;

            if (sampled.Width == targetW && sampled.Height == targetH)
            {
                return FromSkBitmap(sampled, srcW, srcH, color.ColorSpace, color.IccProfile);
            }

            // F16 广色域(非 JPEG 路径)精缩同样走数值重采样,避免色彩管理步骤把扩展色域钳到 [0,1]。
            if (color.Encoding == PixelEncoding.RgbaF16)
            {
                // 带上真实源尺寸再缩放,产物直接继承全分辨率几何(与 8 位路径一致)。
                using var sampledImage = FromSkBitmap(sampled, srcW, srcH, color.ColorSpace, color.IccProfile);
                return sampledImage == null ? null : ScaleF16Numeric(sampledImage, targetW, targetH);
            }

            using var exact = new SKBitmap(InfoFor(color.Encoding, targetW, targetH, color.DecodeColorSpace));
            using (var canvas = new SKCanvas(exact))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(SKImage.FromBitmap(sampled),
                    SKRect.Create(0, 0, sampled.Width, sampled.Height),
                    SKRect.Create(0, 0, targetW, targetH),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(exact, srcW, srcH, color.ColorSpace, color.IccProfile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.DecodeToFit failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析解码产物的真实源色域:JPEG 以容器内的 ICC / EXIF 声明为准(有效 ICC 优先);
    /// 其余格式用 codec 报告的色彩空间 —— 同原色换曲线、能导出 ICC 就保留,否则保全广色域到 F16,
    /// 不把广色域钳成 sRGB 后再自称 sRGB。
    /// </summary>
    /// <param name="codec">已建立的编解码器。</param>
    /// <param name="metadata">JPEG 容器色彩元数据(非 JPEG 为空)。</param>
    /// <returns>源色彩描述。</returns>
    private static SourceColor ResolveSourceColor(SKCodec codec, JpegColorMetadata metadata)
    {
        if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg)
        {
            var space = metadata.ResolveColorSpace();
            return new SourceColor(space, space == ImageColorSpace.Icc ? metadata.IccProfile : null, null, PixelEncoding.Bgra32);
        }

        var codecSpace = codec.Info.ColorSpace;
        // 未标记或数值上就是 sRGB:像素原样保留即 sRGB,无需色彩管理。
        if (codecSpace == null || codecSpace.IsSrgb)
            return new SourceColor(ImageColorSpace.Srgb, null, null, PixelEncoding.Bgra32);
        // 原色就是 sRGB、只是曲线不同(老工具只写 gAMA/cHRM 的 PNG):解码期精确换到 sRGB 曲线,没有色域损失。
        if (HasSamePrimariesAsSrgb(codecSpace))
            return new SourceColor(ImageColorSpace.Srgb, null, SkColorSpaces.Srgb, PixelEncoding.Bgra32);
        // 广色域且 Skia 能导出 ICC:保留原值,如实标注 ICC。
        if (TryExportIcc(codecSpace) is { } icc)
            return new SourceColor(ImageColorSpace.Icc, icc, null, PixelEncoding.Bgra32);
        // 广色域又无法用 ICC 标签表达:直接解到 F16 线性 scRGB,色域(负值 / >1)一个不丢。
        return new SourceColor(ImageColorSpace.LinearSrgb, null, SkColorSpaces.LinearSrgb, PixelEncoding.RgbaF16);
    }

    /// <summary>色彩空间原色是否就是 sRGB(比较 D50 适配后的 RGB→XYZ 矩阵;取不到数值矩阵时按"不是"处理)。</summary>
    /// <param name="space">待判定的色彩空间。</param>
    /// <returns>原色与 sRGB 一致时为 true。</returns>
    private static bool HasSamePrimariesAsSrgb(SKColorSpace space)
    {
        try
        {
            if (!space.IsNumericalTransferFunction) return false;
            var xyz = space.ToColorSpaceXyz().Values;
            var srgb = SKColorSpaceXyz.Srgb.Values;
            if (xyz == null || srgb == null || xyz.Length < 9 || srgb.Length < 9) return false;
            for (int i = 0; i < 9; i++)
                if (Math.Abs(xyz[i] - srgb[i]) > SrgbPrimariesTolerance) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试把 Skia 色彩空间导出为 ICC profile 字节。当前 SkiaSharp 对数值色彩空间恒返回空 profile
    /// (<c>SKColorSpace.ToProfile()</c> 的 Size 为 0,连 <c>CreateIcc</c> 建出来的也如此),故绝大多数文件走不到这里;
    /// 保留该分支以便 Skia 将来能导出、或遇到由 LUT 描述而保留 ICC 原字节的色彩空间时直接受益。
    /// </summary>
    /// <param name="space">待导出的色彩空间。</param>
    /// <returns>ICC profile 字节;无法导出或内容非法时返回 null。</returns>
    private static byte[]? TryExportIcc(SKColorSpace space)
    {
        try
        {
            using var profile = space.ToProfile();
            if (profile == null || profile.Buffer == IntPtr.Zero) return null;
            if (profile.Size < IccMinimumLength || profile.Size > int.MaxValue) return null;
            var bytes = new byte[profile.Size];
            Marshal.Copy(profile.Buffer, bytes, 0, bytes.Length);
            return bytes[36] == 'a' && bytes[37] == 'c' && bytes[38] == 's' && bytes[39] == 'p' ? bytes : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec: ICC 导出失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按长边上限等比缩放已解码图像(Mitchell 三次滤波,不放大)。
    /// 输入支持 Bgra32/Rgb24,也支持 Rgba8/RgbaF16(输出与输入同编码);Rgb24 先转 Bgra32。
    /// Windows libheif 无子采样解码,代理图靠"全解码 + 本方法精缩"实现(画质红线:不许最近邻)。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="targetLongEdge">目标长边像素。</param>
    /// <returns>缩放结果;不需要缩放(源已在目标内)返回 null。</returns>
    public static DecodedImage? ScaleToLongEdge(DecodedImage src, int targetLongEdge)
    {
        if (targetLongEdge <= 0) return null;
        int longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= targetLongEdge) return null; // 不放大;调用方用原图

        double scale = (double)targetLongEdge / longEdge;
        int targetW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int targetH = Math.Max(1, (int)Math.Round(src.Height * scale));

        // Skia 无 24 位打包像素格式:Rgb24 源先转 Bgra32
        DecodedImage? converted = null;
        var effective = src;
        if (src.Encoding == PixelEncoding.Rgb24)
        {
            converted = ToBgra32(src);
            if (converted == null) return null;
            effective = converted;
        }

        try
        {
            // F16 线性表面走数值重采样:带色彩管理的画布会把扩展色域(负值 / >1)钳到 [0,1]。
            if (effective.Encoding == PixelEncoding.RgbaF16)
            {
                return ScaleF16Numeric(effective, targetW, targetH);
            }

            using var space = SkColorSpaces.Resolve(effective.ColorSpace, effective.IccProfile);
            using var dst = new SKBitmap(InfoFor(effective.Encoding, targetW, targetH, space.Space));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(FromBytes(effective),
                    SKRect.Create(0, 0, effective.Width, effective.Height),
                    SKRect.Create(0, 0, targetW, targetH),
                    sampling, new SKPaint { BlendMode = SKBlendMode.Src });
            }
            return FromSkBitmap(dst, src.SourceWidth, src.SourceHeight, src.ColorSpace, IccOf(src), src.Orientation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ScaleToLongEdge failed: {ex.Message}");
            return null;
        }
        finally
        {
            converted?.Release();
        }
    }

    /// <summary>
    /// F16 线性表面的数值重采样(纯托管、不经过 Skia 画布):扩展色域(负值 / &gt;1)逐位保留。
    /// </summary>
    /// <remarks>
    /// 为什么要自己算:Skia 会把 F16→F16 的画布绘制钳到 [0,1](实测 P3 红 1.2246/−0.042/−0.0196 缩放后变 (1,0,0);
    /// 把源与目标的色彩空间都置空、alpha 按不透明处理也一样),而线性广色域表面的负值与超一正是要保住的信息。
    /// 重采样质量按缩放比自适应、两种都不是最近邻:
    /// 缩小 ≥2× 走**面积平均**(重下采样的理想前置滤波,无振铃);
    /// 轻微缩放走 **Mitchell**(B=C=1/3, support 2,与 Skia 的 Mitchell 同族口径)。两遍都按预计算权重可分离执行。
    /// </remarks>
    /// <param name="source">F16 源图像(所有权不转移)。</param>
    /// <param name="width">目标宽。</param>
    /// <param name="height">目标高。</param>
    /// <returns>缩放结果;失败返回 null。</returns>
    private static unsafe DecodedImage? ScaleF16Numeric(DecodedImage source, int width, int height)
    {
        if (source.Encoding != PixelEncoding.RgbaF16 || width <= 0 || height <= 0) return null;
        int sw = source.Width, sh = source.Height;
        if (sw <= 0 || sh <= 0) return null;

        var horizontal = BuildWeights(sw, width);
        var vertical = BuildWeights(sh, height);

        // 水平一遍:sw×sh → width×sh(浮点中间,避免半精度在多次加权里累积误差)
        var intermediate = new float[checked(width * sh * 4)];
        fixed (byte* sourcePixels = source.Pixels)
        {
            var rowBase = (ushort*)sourcePixels;
            for (var y = 0; y < sh; y++)
            {
                var row = rowBase + (long)y * (source.Stride / 2);
                var targetRow = (long)y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var start = horizontal.Start[x];
                    var count = horizontal.Taps[x];
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var t = 0; t < count; t++)
                    {
                        var weight = horizontal.Values[start + t];
                        var at = horizontal.Index[start + t] * 4;
                        r += (float)BitConverter.UInt16BitsToHalf(row[at]) * weight;
                        g += (float)BitConverter.UInt16BitsToHalf(row[at + 1]) * weight;
                        b += (float)BitConverter.UInt16BitsToHalf(row[at + 2]) * weight;
                        a += (float)BitConverter.UInt16BitsToHalf(row[at + 3]) * weight;
                    }
                    var at2 = targetRow + (long)x * 4;
                    intermediate[at2] = r;
                    intermediate[at2 + 1] = g;
                    intermediate[at2 + 2] = b;
                    intermediate[at2 + 3] = a;
                }
            }
        }

        // 垂直一遍:width×sh → width×height,并直接写半精度
        var stride = checked(width * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            fixed (float* mid = intermediate)
            fixed (byte* targetPixels = buffer)
            {
                for (var y = 0; y < height; y++)
                {
                    var start = vertical.Start[y];
                    var count = vertical.Taps[y];
                    var targetRow = (ushort*)(targetPixels + (long)y * stride);
                    for (var x = 0; x < width; x++)
                    {
                        float r = 0, g = 0, b = 0, a = 0;
                        for (var t = 0; t < count; t++)
                        {
                            var weight = vertical.Values[start + t];
                            var at = ((long)vertical.Index[start + t] * width + x) * 4;
                            r += mid[at] * weight;
                            g += mid[at + 1] * weight;
                            b += mid[at + 2] * weight;
                            a += mid[at + 3] * weight;
                        }
                        var at2 = x * 4;
                        targetRow[at2] = BitConverter.HalfToUInt16Bits((Half)r);
                        targetRow[at2 + 1] = BitConverter.HalfToUInt16Bits((Half)g);
                        targetRow[at2 + 2] = BitConverter.HalfToUInt16Bits((Half)b);
                        targetRow[at2 + 3] = BitConverter.HalfToUInt16Bits((Half)a);
                    }
                }
            }

            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.RgbaF16,
                source.ColorSpace, source.SourceWidth, source.SourceHeight, source.Orientation, IccOf(source));
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>一维重采样权重(每个输出位置一段连续 Tap:源下标 + 已归一化权重)。</summary>
    private sealed class ResampleWeights(int[] start, int[] taps, int[] index, float[] values)
    {
        /// <summary>第 i 个输出位置在 <see cref="Values"/> 中的起点。</summary>
        public int[] Start { get; } = start;

        /// <summary>第 i 个输出位置的 Tap 数。</summary>
        public int[] Taps { get; } = taps;

        /// <summary>Tap 对应的源下标。</summary>
        public int[] Index { get; } = index;

        /// <summary>Tap 权重(同一输出位置的权重和为 1)。</summary>
        public float[] Values { get; } = values;
    }

    /// <summary>
    /// 生成一维权重:缩小 ≥2× 用面积平均(重下采样理想前置滤波),否则用 Mitchell(B=C=1/3,support 2)。
    /// </summary>
    /// <param name="sourceLength">源长度。</param>
    /// <param name="targetLength">目标长度。</param>
    /// <returns>权重表。</returns>
    private static ResampleWeights BuildWeights(int sourceLength, int targetLength)
    {
        var start = new int[targetLength];
        var taps = new int[targetLength];
        var indices = new List<int>(targetLength * 4);
        var values = new List<float>(targetLength * 4);
        var ratio = (double)sourceLength / targetLength;
        var area = ratio >= 2;

        for (var i = 0; i < targetLength; i++)
        {
            start[i] = values.Count;
            var begin = i * ratio;
            var end = (i + 1) * ratio;
            var total = 0d;
            if (area)
            {
                var first = Math.Max(0, (int)Math.Floor(begin));
                var last = Math.Min(sourceLength - 1, (int)Math.Ceiling(end) - 1);
                for (var s = first; s <= last; s++)
                {
                    var overlap = Math.Min(end, s + 1) - Math.Max(begin, s);
                    if (overlap <= 0) continue;
                    indices.Add(s);
                    values.Add((float)overlap);
                    total += overlap;
                }
            }
            else
            {
                var center = (i + 0.5) * ratio - 0.5;
                var first = Math.Max(0, (int)Math.Ceiling(center - 2));
                var last = Math.Min(sourceLength - 1, (int)Math.Floor(center + 2));
                for (var s = first; s <= last; s++)
                {
                    var weight = Mitchell(Math.Abs(s - center));
                    if (weight <= 0) continue;
                    indices.Add(s);
                    values.Add((float)weight);
                    total += weight;
                }
            }

            taps[i] = values.Count - start[i];
            if (total <= 0)
            {
                // 退化宽度(理论上不会发生):退化为最近邻一个像素,保持权重归一。
                indices.Add(Math.Clamp((int)Math.Round(begin), 0, sourceLength - 1));
                values.Add(1f);
                taps[i] = 1;
                continue;
            }

            for (var k = start[i]; k < values.Count; k++) values[k] = (float)(values[k] / total);
        }

        return new ResampleWeights(start, taps, [.. indices], [.. values]);
    }

    /// <summary>Mitchell–Netravali 三次核(B = C = 1/3,与 Skia <c>SkCubicResampler.Mitchell</c> 同参数)。</summary>
    /// <param name="x">到采样中心的距离(≥0)。</param>
    /// <returns>核权重。</returns>
    private static double Mitchell(double x)
    {
        const double b = 1d / 3, c = 1d / 3;
        var x2 = x * x;
        var x3 = x2 * x;
        if (x < 1)
            return ((12 - 9 * b - 6 * c) * x3 + (-18 + 12 * b + 6 * c) * x2 + (6 - 2 * b)) / 6;
        if (x < 2)
            return ((-b - 6 * c) * x3 + (6 * b + 30 * c) * x2 + (-12 * b - 48 * c) * x + (8 * b + 24 * c)) / 6;
        return 0;
    }

    /// <summary>
    /// F16 方向变换:90° 整数倍旋转 + 水平镜像都是整数像素置换,不需要插值。
    /// 逐像素搬运,既不重采样也不经过画布,因此扩展色域(负值 / &gt;1)与位模式都原样保留。
    /// </summary>
    /// <param name="src">F16 源图像(所有权不转移)。</param>
    /// <param name="rotationCw">顺时针旋转角度。</param>
    /// <param name="mirror">旋转前是否水平镜像。</param>
    /// <returns>变换后的新图像;失败返回 null。</returns>
    private static unsafe DecodedImage? OrientF16Numeric(DecodedImage src, int rotationCw, bool mirror)
    {
        int sw = src.Width, sh = src.Height;
        bool swap = rotationCw is 90 or 270;
        int dw = swap ? sh : sw;
        int dh = swap ? sw : sh;
        var stride = checked(dw * 8);
        var buffer = DecodedImage.RentPixels(checked(stride * dh));
        try
        {
            fixed (byte* source = src.Pixels)
            fixed (byte* target = buffer)
            {
                for (var y = 0; y < dh; y++)
                {
                    var row = target + (long)y * stride;
                    for (var x = 0; x < dw; x++)
                    {
                        // 与画布版同一变换(先水平镜像、再顺时针旋转)的整数逆映射。
                        var (sx, sy) = (rotationCw, mirror) switch
                        {
                            (0, false) => (x, y),
                            (0, true) => (sw - 1 - x, y),
                            (90, false) => (y, sh - 1 - x),
                            (90, true) => (sw - 1 - y, sh - 1 - x),
                            (180, false) => (sw - 1 - x, sh - 1 - y),
                            (180, true) => (x, sh - 1 - y),
                            (270, false) => (sw - 1 - y, x),
                            _ => (y, x),
                        };
                        *(ulong*)(row + (long)x * 8) = *(ulong*)(source + (long)sy * src.Stride + (long)sx * 8);
                    }
                }
            }

            return DecodedImage.CreatePooled(buffer, dw, dh, stride, PixelEncoding.RgbaF16, src.ColorSpace,
                swap ? src.SourceHeight : src.SourceWidth,
                swap ? src.SourceWidth : src.SourceHeight,
                1,
                IccOf(src));
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Rgb24 → Bgra32 转换(alpha 置 255);输入已是 Bgra32 时返回 null(调用方无需转换)。
    /// 无重采样,行的字节序改变、色彩语义不变:色彩标签与 ICC 原样带到产物。缓冲池租用。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <returns>转换结果;编码非 Rgb24 或失败返回 null。</returns>
    public static DecodedImage? ToBgra32(DecodedImage src)
    {
        if (src.Encoding != PixelEncoding.Rgb24) return null;
        int w = src.Width, h = src.Height;
        try
        {
            var dest = DecodedImage.RentPixels(w * h * 4);
            for (int y = 0; y < h; y++)
            {
                long srcRow = (long)y * src.Stride;
                long dstRow = (long)y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    long si = srcRow + x * 3;
                    long di = dstRow + x * 4;
                    dest[di] = src.Pixels[si + 2];     // B
                    dest[di + 1] = src.Pixels[si + 1]; // G
                    dest[di + 2] = src.Pixels[si];     // R
                    dest[di + 3] = 255;
                }
            }
            return DecodedImage.CreatePooled(dest, w, h, w * 4, PixelEncoding.Bgra32, src.ColorSpace,
                src.SourceWidth, src.SourceHeight, src.Orientation, IccOf(src));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ToBgra32 failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按顺时针角度(0/90/180/270)与可选水平镜像,把传感器朝向像素变换到显示朝向。
    /// 变换顺序:平移到源中心 → (水平镜像) → (顺时针旋转) → 平移到目标中心;返回新对象。
    /// 支持 Bgra32/Rgba8/RgbaF16(Skia 无 24 位打包格式,Rgb24 源需先转 Bgra32)。
    /// 产物已是显示朝向,故方向契约归 1,源几何在 90°/270° 时随之互换;色彩标签与 ICC 原样保留。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="rotationCw">顺时针旋转角度。</param>
    /// <param name="mirror">旋转前是否水平镜像。</param>
    /// <returns>变换后的新图像;编码或尺寸不受支持时返回 null。</returns>
    public static DecodedImage? ApplyOrientation(DecodedImage src, int rotationCw, bool mirror)
    {
        if (rotationCw is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(rotationCw));
        if (src.Encoding is PixelEncoding.Rgb24) return null;

        int sw = src.Width, sh = src.Height;
        bool swap = rotationCw is 90 or 270;
        int dw = swap ? sh : sw;
        int dh = swap ? sw : sh;

        try
        {
            // F16 走逐像素置换:画布旋转会重采样,且色彩管理步骤会把扩展色域钳到 [0,1]。
            if (src.Encoding == PixelEncoding.RgbaF16) return OrientF16Numeric(src, rotationCw, mirror);

            using var space = SkColorSpaces.Resolve(src.ColorSpace, src.IccProfile);
            using var dst = new SKBitmap(InfoFor(src.Encoding, dw, dh, space.Space));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(dw / 2f, dh / 2f);
                if (rotationCw != 0) canvas.RotateDegrees(rotationCw);
                if (mirror) canvas.Scale(-1, 1);
                canvas.Translate(-sw / 2f, -sh / 2f);
                canvas.DrawImage(FromBytes(src), 0, 0);
            }
            return FromSkBitmap(dst,
                swap ? src.SourceHeight : src.SourceWidth,
                swap ? src.SourceWidth : src.SourceHeight,
                src.ColorSpace, IccOf(src), orientation: 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ApplyOrientation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按源像素坐标裁剪(行级字节拷贝,无重采样);缓冲池租用,保留源的像素编码、色彩标签与待应用方向。
    /// 产物几何就是裁剪块本身,故源尺寸按裁剪块计。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="width">裁剪宽。</param>
    /// <param name="height">裁剪高。</param>
    /// <returns>裁剪结果;失败返回 null。</returns>
    public static DecodedImage? Crop(DecodedImage src, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > src.Width || y + height > src.Height)
            return null;

        int bpp = DecodedImage.BytesPerPixel(src.Encoding);
        var rowBytes = checked(width * bpp);
        var dest = DecodedImage.RentPixels(checked(rowBytes * height));
        for (int row = 0; row < height; row++)
        {
            Array.Copy(src.Pixels, (long)(y + row) * src.Stride + (long)x * bpp, dest, (long)row * rowBytes, rowBytes);
        }
        return DecodedImage.CreatePooled(dest, width, height, rowBytes, src.Encoding, src.ColorSpace,
            orientation: src.Orientation, iccProfile: IccOf(src));
    }

    /// <summary>
    /// SKBitmap → DecodedImage 行级拷贝(Bgra8888 / Rgba8888 / RgbaF16);缓冲池租用。
    /// 编码由位图的色彩类型决定,色彩标签与 ICC 由调用方给出。
    /// </summary>
    /// <param name="bmp">源位图。</param>
    /// <param name="sourceWidth">可选的原始像素宽；零表示使用当前位图宽度。</param>
    /// <param name="sourceHeight">可选的原始像素高；零表示使用当前位图高度。</param>
    /// <param name="colorSpace">产物的色彩标签。</param>
    /// <param name="iccProfile">产物携带的 ICC profile。</param>
    /// <param name="orientation">产物仍待应用的方向。</param>
    /// <returns>转换结果;尺寸非法或格式不符返回 null。</returns>
    public static DecodedImage? FromSkBitmap(SKBitmap? bmp, int sourceWidth = 0, int sourceHeight = 0,
        ImageColorSpace colorSpace = ImageColorSpace.Legacy, byte[]? iccProfile = null, int orientation = 1)
    {
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;
        if (EncodingOf(bmp.ColorType) is not { } encoding) return null;

        long copyBytes = (long)bmp.RowBytes * bmp.Height;
        var bytes = DecodedImage.RentPixels((int)Math.Min(copyBytes, int.MaxValue));
        unsafe
        {
            var srcPtr = (byte*)bmp.GetPixels();
            if (srcPtr == null)
            {
                ImageBufferPool.Return(bytes);
                return null;
            }
            // 池化缓冲长度是分档对齐后的值,可能大于实际数据——只拷实际字节数
            fixed (byte* destPtr = bytes)
            {
                Buffer.MemoryCopy(srcPtr, destPtr, bytes.Length, copyBytes);
            }
        }
        return DecodedImage.CreatePooled(bytes, bmp.Width, bmp.Height, bmp.RowBytes, encoding,
            colorSpace, sourceWidth, sourceHeight, orientation, iccProfile);
    }

    /// <summary>源的 ICC 字节副本;无 ICC 时返回 null(产物按"无 profile"记账,不额外分配空数组)。</summary>
    private static byte[]? IccOf(DecodedImage image) => image.IccProfile.IsEmpty ? null : image.IccProfile.ToArray();

    /// <summary>Skia 色彩类型 → 像素编码;本层不产生的类型返回 null(不猜字节序)。</summary>
    private static PixelEncoding? EncodingOf(SKColorType colorType) => colorType switch
    {
        SKColorType.Bgra8888 => PixelEncoding.Bgra32,
        SKColorType.Rgba8888 => PixelEncoding.Rgba8,
        SKColorType.RgbaF16 => PixelEncoding.RgbaF16,
        _ => null,
    };

    /// <summary>
    /// 拉伸缩放到精确尺寸(可改变纵横比;线性采样,与旧壳默认采样口径一致,
    /// DINO 模型输入等像素契约场景依赖此口径)。缓冲池租用。
    /// 只重采样、不做色彩转换:编码、色彩标签、ICC、源尺寸与方向原样保留。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="width">目标宽。</param>
    /// <param name="height">目标高。</param>
    /// <returns>缩放结果;失败返回 null。</returns>
    public static DecodedImage? ScaleToExact(DecodedImage src, int width, int height)
    {
        if (src.Encoding != PixelEncoding.Bgra32 || width <= 0 || height <= 0) return null;

        try
        {
            using var space = SkColorSpaces.Resolve(src.ColorSpace, src.IccProfile);
            using var dst = new SKBitmap(InfoFor(PixelEncoding.Bgra32, width, height, space.Space));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                canvas.DrawImage(FromBytes(src),
                    SKRect.Create(0, 0, src.Width, src.Height),
                    SKRect.Create(0, 0, width, height),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(dst, src.SourceWidth, src.SourceHeight, src.ColorSpace, IccOf(src), src.Orientation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ScaleToExact failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把图像编码为 PNG 写入流(供工具/训练侧落盘;不修改源图像)。
    /// </summary>
    /// <param name="image">源图像。</param>
    /// <param name="stream">目标流。</param>
    /// <returns>是否成功。</returns>
    public static bool SavePng(DecodedImage image, Stream stream)
    {
        try
        {
            using var skImage = FromBytes(image);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return false;
            data.SaveTo(stream);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.SavePng failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>从 DecodedImage 字节构造只读 SKImage(拷贝一次),携带源的色彩空间,绘制时不发生色彩转换。</summary>
    private static SKImage FromBytes(DecodedImage image)
    {
        using var space = SkColorSpaces.Resolve(image.ColorSpace, image.IccProfile);
        var info = InfoFor(image.Encoding, image.Width, image.Height, space.Space);
        var data = SKData.CreateCopy(image.Pixels);
        return SKImage.FromPixels(info, data, image.Stride);
    }
}
