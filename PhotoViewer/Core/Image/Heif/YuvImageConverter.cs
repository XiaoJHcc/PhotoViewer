using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;

namespace PhotoViewer.Core.Image.Heif;

/// <summary>HEVC 色度采样格式。</summary>
public enum HeifChromaSampling
{
    /// <summary>单色(无 Cb/Cr 平面)。</summary>
    Monochrome = 0,

    /// <summary>4:2:0(色度水平与垂直各减半)。</summary>
    Yuv420 = 1,

    /// <summary>4:2:2(色度水平减半)。</summary>
    Yuv422 = 2,

    /// <summary>4:4:4(色度与亮度同尺寸)。</summary>
    Yuv444 = 3,
}

/// <summary>
/// 一块已解码的 YUV 瓦片:三个平面指针、行跨距,以及在输出画布中的摆放位置。
/// </summary>
/// <remarks>
/// 平面为 HEVC 原生排布(自顶向下);8 位样本按字节存,10/12 位按小端 <see cref="ushort"/> 存。
/// 解码器产出常按编码对齐向上取整(例如 <c>ispe</c> 报 1616x1080 而 SPS 解出 1664x1088),
/// 所以 <see cref="Width"/>/<see cref="Height"/> 只声明有效区域,行跨距另行给出。
/// </remarks>
public readonly struct YuvTile
{
    /// <summary>亮度平面首地址。</summary>
    public IntPtr Y { get; init; }

    /// <summary>Cb 平面首地址。</summary>
    public IntPtr Cb { get; init; }

    /// <summary>Cr 平面首地址。</summary>
    public IntPtr Cr { get; init; }

    /// <summary>亮度行跨距(字节)。</summary>
    public int StrideY { get; init; }

    /// <summary>色度行跨距(字节)。</summary>
    public int StrideCb { get; init; }

    /// <summary>色度行跨距(字节)。</summary>
    public int StrideCr { get; init; }

    /// <summary>瓦片左边界在输出画布中的 x(像素)。</summary>
    public int Column { get; init; }

    /// <summary>瓦片上边界在输出画布中的 y(像素)。</summary>
    public int Row { get; init; }

    /// <summary>瓦片有效宽度(亮度像素)。</summary>
    public int Width { get; init; }

    /// <summary>瓦片有效高度(亮度像素)。</summary>
    public int Height { get; init; }
}

/// <summary>
/// 一次转换的完整输入:瓦片集合、画布尺寸、位深与色彩描述。
/// </summary>
/// <remarks>
/// 画布是"把瓦片当成一张图"的裁剪窗口:右/下边缘之外的瓦片像素不参与输出,
/// 解码器的对齐留白因此不会漏进画面。
/// </remarks>
public sealed class YuvImage
{
    /// <summary>瓦片集合(以 <see cref="YuvTile.Column"/>/<see cref="YuvTile.Row"/> 定位,顺序无关)。</summary>
    public required IReadOnlyList<YuvTile> Tiles { get; init; }

    /// <summary>输出画布宽(裁剪后的实际像素宽)。</summary>
    public required int CanvasWidth { get; init; }

    /// <summary>输出画布高。</summary>
    public required int CanvasHeight { get; init; }

    /// <summary>样本位深(8/10/12)。</summary>
    public int BitDepth { get; init; } = 10;

    /// <summary>色度采样格式。</summary>
    public HeifChromaSampling Chroma { get; init; } = HeifChromaSampling.Yuv422;

    /// <summary>nclx 矩阵系数代码(1=BT.709,5/6=BT.601,9=BT.2020 非恒定亮度,2=未指定→BT.601)。</summary>
    public int MatrixCode { get; init; } = 5;

    /// <summary>nclx 原色代码(1=BT.709,2=未指定→BT.709,9=BT.2020,12=Display P3)。</summary>
    public int PrimariesCode { get; init; } = 1;

    /// <summary>nclx 传输特性代码(1/6/14/15=BT.709 曲线族,2/13=sRGB 曲线,8=线性)。</summary>
    public int TransferCode { get; init; } = 13;

    /// <summary>全范围(true)或视频范围(false)。</summary>
    public bool FullRange { get; init; } = true;

    /// <summary>
    /// 源 ICC 配置文件字节;存在时按 HEIF 规定优先于 <c>nclx</c> 三元组解释编码值。
    /// </summary>
    /// <remarks>
    /// 携带 ICC 时必须走 <see cref="YuvImageConverter.TryConvertToIccEncodedRgbaF16"/> /
    /// <see cref="YuvImageConverter.TryConvertToIccEncodedBgra32"/>:那两条出口只做 YCbCr 反矩阵,
    /// 产出"该 ICC 空间内的编码值",再由色彩引擎( skcms )按 profile 转到目标空间。
    /// </remarks>
    public byte[]? IccProfile { get; init; }

    /// <summary>是否携带可用的 ICC 配置文件。</summary>
    public bool HasIccProfile => IccProfile is { Length: > 0 };
}

/// <summary>
/// YUV 瓦片 → 呈现缓冲的转换器。
/// </summary>
/// <remarks>
/// 三条出口:
/// <list type="bullet">
/// <item>线性光 RGBA 半浮点(scRGB / 线性 sRGB 基色),供线性交换链直接采样;</item>
/// <item>sRGB 8 位 BGRA,供缩略图与分析使用;</item>
/// <item>8 位 RGBA,按请求标记为 sRGB 或 Display P3,供 Android 等 8 位展示表面使用。</item>
/// </list>
/// 另有两条例外出口供携带 ICC 的源使用(<see cref="TryConvertToIccEncodedRgbaF16"/> /
/// <see cref="TryConvertToIccEncodedBgra32"/>):只做 YCbCr 反矩阵,产出源 ICC 空间内的编码值,
/// 不线性化、不做原色转换 —— 后续由色彩引擎按 profile 转到目标空间,避免把广色域当 sRGB。
/// 目标尺寸等于画布时逐像素直通;小于画布时用 Mitchell–Netravali(B=C=1/3)可分离重采样,
/// 核支撑按缩放倍数展宽(抗锯齿),重采样在编码值(R'G'B')上完成,最后才线性化/量化。
/// 两条路径都按输出行带并行:逐像素直通无中间层;重采样每个线程只持有"一行源 + 若干行已横向重采样"
/// 的缓冲(数 MB 级),不物化全分辨率浮点 RGB 中间图像。
///
/// 原色与传递函数的支持面(超出即 <see cref="IsSupported"/> 返回 false,由调用方回落平台 HEIF 解码):
/// <list type="bullet">
/// <item>原色:nclx 1/2(BT.709,等同 sRGB 原色)、9(BT.2020)、12(Display P3);
/// 其余(470M/470BG/601/240M/电影/428/431/EBU)不猜,显式拒绝;</item>
/// <item>传递函数:1/6/14/15(BT.709 曲线族)、2/13(sRGB)、4(伽马 2.2)、5(伽马 2.8)、8(线性);
/// 7(SMPTE 240M)、11(xvYCC)、12(BT.1361)、16(PQ)、18(HLG)等显式拒绝 —— 本转换器不做 HDR tone map,
/// 不把 PQ/HLG 当 SDR 输出;</item>
/// <item>矩阵系数:1(BT.709)、5/6/2(BT.601)、9(BT.2020 非恒定亮度);其余显式拒绝。</item>
/// </list>
/// </remarks>
public static class YuvImageConverter
{
    /// <summary>每幅转换的并行度上限；0 表示交给线程池（默认），平台可在启动时配置。</summary>
    public static int MaxDegreeOfParallelism { get; set; }

    public static TaskScheduler? ConversionTaskScheduler { get; set; }

    /// <summary>最近一次转换的临时缓冲峰值字节数(各并行线程的并发峰值)。</summary>
    public static long LastScratchPeakBytes => Interlocked.Read(ref s_scratchPeak);

    private const int LutSize = 4096;
    private const int FullResBandRows = 8;
    private const long ScratchBudgetPerWorker = 3L << 20;
    private const int RowPaddingFloats = 8;

    private static readonly float[] s_encodeLut = BuildSrgbEncodeLut();
    private static readonly float[] s_linearizeSrgbLut = BuildLinearizeLut(TransferCurve.Srgb);
    private static readonly float[] s_linearizeBt709Lut = BuildLinearizeLut(TransferCurve.Bt709);
    private static readonly float[] s_linearizeGamma22Lut = BuildLinearizeLut(TransferCurve.Gamma22);
    private static readonly float[] s_linearizeGamma28Lut = BuildLinearizeLut(TransferCurve.Gamma28);

    /// <summary>
    /// BT.709/sRGB → Display P3 的线性基色矩阵(行优先)。取 Display P3 → sRGB 标准线性基色矩阵
    /// (即 <c>s_p3ToSrgbMatrix</c> 那三行)在双精度下求逆的结果,两者互逆:正向应用后再反向应用的
    /// 舍入误差在 1e-7 量级,远小于 1/255 的量化步长。
    /// </summary>
    private static readonly float[] s_srgbToP3Matrix =
    {
        0.8224620f, 0.1775382f, 0.0000000f,
        0.0331942f, 0.9668059f, 0.0000000f,
        0.0170827f, 0.0723975f, 0.9105200f,
    };

    /// <summary>
    /// Display P3 → BT.709/sRGB 的线性基色矩阵(行优先),由 ITU-R BT.709 与 Display P3 白点(D65)
    /// 三基色的色度坐标在双精度下推导;行和为 1,中性灰因此保持不变。
    /// </summary>
    private static readonly float[] s_p3ToSrgbMatrix =
    {
        1.2249401f, -0.2249404f, 0.0000000f,
        -0.0420569f, 1.0420569f, 0.0000000f,
        -0.0196376f, -0.0786361f, 1.0982735f,
    };

    /// <summary>BT.2020 → BT.709/sRGB 的线性基色矩阵(同法推导,行和为 1)。</summary>
    private static readonly float[] s_bt2020ToSrgbMatrix =
    {
        1.6604910f, -0.5876411f, -0.0728499f,
        -0.1245505f, 1.1328999f, -0.0083494f,
        -0.0181508f, -0.1005789f, 1.1187297f,
    };

    /// <summary>BT.2020 → Display P3 的线性基色矩阵(行和为 1)。</summary>
    private static readonly float[] s_bt2020ToP3Matrix =
    {
        1.3435783f, -0.2821797f, -0.0613986f,
        -0.0652975f, 1.0757879f, -0.0104905f,
        0.0028218f, -0.0195985f, 1.0167767f,
    };

    private static long s_scratchCurrent;
    private static long s_scratchPeak;

    // ---------------------------------------------------------------
    // 公共入口
    // ---------------------------------------------------------------

    /// <summary>
    /// 转换到线性光 RGBA 半浮点缓冲(R,G,B,A 各 2 字节小端,A=1.0)。
    /// </summary>
    /// <param name="source">解码后的瓦片与色彩描述。</param>
    /// <param name="targetWidth">目标宽;等于画布宽时不做重采样。</param>
    /// <param name="targetHeight">目标高。</param>
    /// <param name="destination">目标缓冲,按行跨距写入。</param>
    /// <param name="destinationStride">目标行跨距(字节),需 ≥ targetWidth×8。</param>
    /// <param name="unsupportedReason">不受支持时的原因;成功为 null。</param>
    /// <returns>转换成功返回 true。</returns>
    public static bool TryConvertToRgbaLinearF16(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        out string? unsupportedReason)
        => Convert(
            source,
            targetWidth,
            targetHeight,
            destination,
            destinationStride,
            OutputFormat.RgbaF16,
            ImageColorSpace.LinearSrgb,
            false,
            out unsupportedReason);

    /// <summary>
    /// 转换到 sRGB 8 位 BGRA(B,G,R,255)缓冲。
    /// </summary>
    /// <param name="source">解码后的瓦片与色彩描述。</param>
    /// <param name="targetWidth">目标宽;等于画布宽时不做重采样。</param>
    /// <param name="targetHeight">目标高。</param>
    /// <param name="destination">目标缓冲,按行跨距写入。</param>
    /// <param name="destinationStride">目标行跨距(字节),需 ≥ targetWidth×4。</param>
    /// <param name="unsupportedReason">不受支持时的原因;成功为 null。</param>
    /// <returns>转换成功返回 true。</returns>
    public static bool TryConvertToBgra32(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        out string? unsupportedReason)
        => Convert(
            source,
            targetWidth,
            targetHeight,
            destination,
            destinationStride,
            OutputFormat.Bgra32,
            ImageColorSpace.Srgb,
            false,
            out unsupportedReason);

    /// <summary>
    /// 转换到 8 位 RGBA(R,G,B,255)缓冲,色彩标记为 <paramref name="colorSpace"/>。
    /// </summary>
    /// <remarks>
    /// <paramref name="colorSpace"/> 只影响原色转换,传递函数始终是 sRGB 曲线(Display P3 也用 sRGB TRC):
    /// <list type="bullet">
    /// <item><see cref="ImageColorSpace.Srgb"/>:源为 BT.709/sRGB 原色时与原 BGRA32 路径逐像素相同(仅通道序不同);
    /// 源为 Display P3 原色时先转 sRGB 再量化。</item>
    /// <item><see cref="ImageColorSpace.DisplayP3"/>:源为 Display P3 原色时直接量化(不裁剪到 sRGB 色域);
    /// 源为 BT.709/sRGB 原色时在线性域乘以 sRGB→P3 基色矩阵后再编码量化。</item>
    /// </list>
    /// </remarks>
    /// <param name="source">解码后的瓦片与色彩描述。</param>
    /// <param name="targetWidth">目标宽;等于画布宽时不做重采样。</param>
    /// <param name="targetHeight">目标高。</param>
    /// <param name="destination">目标缓冲,按行跨距写入。</param>
    /// <param name="destinationStride">目标行跨距(字节),需 ≥ targetWidth×4。</param>
    /// <param name="colorSpace">目标 8 位色彩空间(仅接受 <see cref="ImageColorSpace.Srgb"/> 与 <see cref="ImageColorSpace.DisplayP3"/>)。</param>
    /// <param name="unsupportedReason">不受支持时的原因;成功为 null。</param>
    /// <returns>转换成功返回 true。</returns>
    public static bool TryConvertToRgba8(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        ImageColorSpace colorSpace,
        out string? unsupportedReason)
        => Convert(source, targetWidth, targetHeight, destination, destinationStride, OutputFormat.Rgba8, colorSpace, false, out unsupportedReason);

    /// <summary>
    /// 携带 ICC 的源:只做 YCbCr 反矩阵,输出 ICC 空间内的编码值(半浮点 RGBA,A=1.0)。
    /// </summary>
    /// <remarks>
    /// 不线性化、不做原色矩阵:编码值就是该 ICC profile 描述的 R'G'B',由色彩引擎(skcms)
    /// 按 profile 一次转到目标空间。这样 10/12 位源不会先被压成 8 位,也不会把 Display P3/BT.2020
    /// 等广色域误当 sRGB。目标空间的原色/传递函数因此完全不参与本函数。
    /// </remarks>
    /// <param name="source">解码后的瓦片与色彩描述(须带 <see cref="YuvImage.IccProfile"/>)。</param>
    /// <param name="targetWidth">目标宽;等于画布宽时不做重采样。</param>
    /// <param name="targetHeight">目标高。</param>
    /// <param name="destination">目标缓冲,按行跨距写入。</param>
    /// <param name="destinationStride">目标行跨距(字节),需 ≥ targetWidth×8。</param>
    /// <param name="unsupportedReason">不受支持时的原因;成功为 null。</param>
    /// <returns>转换成功返回 true。</returns>
    public static bool TryConvertToIccEncodedRgbaF16(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        out string? unsupportedReason)
        => Convert(
            source,
            targetWidth,
            targetHeight,
            destination,
            destinationStride,
            OutputFormat.RgbaF16,
            ImageColorSpace.Icc,
            true,
            out unsupportedReason);

    /// <summary>
    /// 携带 ICC 的源:只做 YCbCr 反矩阵,输出 ICC 空间内的 8 位编码值(B,G,R,255)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="TryConvertToIccEncodedRgbaF16"/> 同一口径,供 8 位目标省掉一份浮点中间图;
    /// 代价是先把码值量化到 8 位,10/12 位源请优先用半浮点出口。
    /// </remarks>
    /// <param name="source">解码后的瓦片与色彩描述(须带 <see cref="YuvImage.IccProfile"/>)。</param>
    /// <param name="targetWidth">目标宽;等于画布宽时不做重采样。</param>
    /// <param name="targetHeight">目标高。</param>
    /// <param name="destination">目标缓冲,按行跨距写入。</param>
    /// <param name="destinationStride">目标行跨距(字节),需 ≥ targetWidth×4。</param>
    /// <param name="unsupportedReason">不受支持时的原因;成功为 null。</param>
    /// <returns>转换成功返回 true。</returns>
    public static bool TryConvertToIccEncodedBgra32(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        out string? unsupportedReason)
        => Convert(
            source,
            targetWidth,
            targetHeight,
            destination,
            destinationStride,
            OutputFormat.Bgra32,
            ImageColorSpace.Icc,
            true,
            out unsupportedReason);

    /// <summary>检查色彩与格式是否可转换,不实际转换。</summary>
    /// <param name="source">输入描述。</param>
    /// <param name="unsupportedReason">不受支持时的原因;支持为 null。</param>
    /// <returns>支持返回 true。</returns>
    public static bool IsSupported(YuvImage source, out string? unsupportedReason)
        => (unsupportedReason = DescribeUnsupported(source, false)) is null;

    /// <summary>检查携带 ICC 的源能否走编码值出口(<see cref="TryConvertToIccEncodedRgbaF16"/> 等),不实际转换。</summary>
    /// <param name="source">输入描述。</param>
    /// <param name="unsupportedReason">不受支持时的原因;支持为 null。</param>
    /// <returns>支持返回 true。</returns>
    public static bool IsSupportedIccEncoded(YuvImage source, out string? unsupportedReason)
        => (unsupportedReason = DescribeUnsupported(source, true)) is null;

    // ---------------------------------------------------------------
    // 准备与调度
    // ---------------------------------------------------------------

    /// <summary>输出格式。</summary>
    private enum OutputFormat
    {
        /// <summary>线性光 RGBA 半浮点;编码值出口时为 ICC 空间内的编码值。</summary>
        RgbaF16,

        /// <summary>sRGB 8 位 BGRA;编码值出口时为 ICC 空间内的 8 位编码值。</summary>
        Bgra32,

        /// <summary>8 位 RGBA,目标色彩空间由调用方指定。</summary>
        Rgba8,
    }

    /// <summary>传递函数族:决定编码值 ↔ 线性光的换算曲线。</summary>
    private enum TransferCurve
    {
        /// <summary>sRGB(BT.709 的近似同族,本站点用于 nclx 2/13)。</summary>
        Srgb,

        /// <summary>BT.709/601/2020 OETF 的逆曲线(nclx 1/6/14/15)。</summary>
        Bt709,

        /// <summary>线性光(传输特性 8),编码值即线性值。</summary>
        Linear,

        /// <summary>BT.470 System M 纯幂曲线 2.2(nclx 4)。</summary>
        Gamma22,

        /// <summary>BT.470 System BG 纯幂曲线 2.8(nclx 5)。</summary>
        Gamma28,
    }

    private static bool Convert(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        OutputFormat format,
        ImageColorSpace rgba8ColorSpace,
        bool codeValues,
        out string? unsupportedReason)
    {
        unsupportedReason = DescribeUnsupported(source, codeValues);
        if (unsupportedReason is not null) return false;

        if (format == OutputFormat.Rgba8 && rgba8ColorSpace is not (ImageColorSpace.Srgb or ImageColorSpace.DisplayP3))
        {
            unsupportedReason = "8 位 RGBA 只支持 sRGB 或 Display P3 目标色彩空间,收到 " + rgba8ColorSpace;
            return false;
        }

        var bytesPerPixel = format == OutputFormat.RgbaF16 ? 8 : 4;
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            unsupportedReason = "目标尺寸非法";
            return false;
        }
        if (destinationStride < (long)targetWidth * bytesPerPixel)
        {
            unsupportedReason = "目标行跨距小于目标宽 × 每像素字节数";
            return false;
        }
        var required = ((long)(targetHeight - 1) * destinationStride) + ((long)targetWidth * bytesPerPixel);
        if (destination.LongLength < required)
        {
            unsupportedReason = "目标缓冲不足";
            return false;
        }

        var plan = new Plan(source, targetWidth, targetHeight, destinationStride, format, rgba8ColorSpace, codeValues);
        Interlocked.Exchange(ref s_scratchPeak, 0);

        var options = new ParallelOptions();
        if (BatchAnalysisContext.IsActive)
        {
            // 批量分析 scope：共用全局 CPU 调度器，并发上限不超过该调度器自身并发度。
            var batchScheduler = BatchAnalysisContext.CpuScheduler;
            options.TaskScheduler = batchScheduler;
            options.MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, batchScheduler.MaximumConcurrencyLevel));
            options.CancellationToken = BatchAnalysisContext.CancellationToken();
        }
        else
        {
            var threadLimit = MaxDegreeOfParallelism;
            if (threadLimit > 0) options.MaxDegreeOfParallelism = threadLimit;
            var scheduler = ConversionTaskScheduler;
            if (scheduler != null) options.TaskScheduler = scheduler;
        }

        unsafe
        {
            fixed (byte* destinationPointer = destination)
            {
                if (plan.IsPassthrough) RunFullResolution(plan, destinationPointer, options);
                else RunResample(plan, destinationPointer, options);
            }
        }

        return true;
    }

    /// <summary>
    /// 检查输入是否在支持面内。
    /// </summary>
    /// <param name="source">输入描述。</param>
    /// <param name="codeValues">是否走 ICC 编码值出口:该出口不做线性化与原色转换,原色/传递函数代码不参与,故不校验。</param>
    /// <returns>不受支持时的原因;支持为 null。</returns>
    private static string? DescribeUnsupported(YuvImage source, bool codeValues)
    {
        if (source.CanvasWidth <= 0 || source.CanvasHeight <= 0) return "画布尺寸非法";
        if (source.Tiles.Count == 0) return "没有瓦片";
        if (source.BitDepth is not (8 or 10 or 12)) return "位深 " + source.BitDepth + " 不受支持";
        if (source.Chroma is not (HeifChromaSampling.Monochrome or HeifChromaSampling.Yuv420
            or HeifChromaSampling.Yuv422 or HeifChromaSampling.Yuv444))
        {
            return "色度格式 " + (int)source.Chroma + " 不受支持";
        }
        // 编码值出口只做 YCbCr 反矩阵:原色与传递函数由 ICC profile 描述,这里不参与也不校验。
        if (!codeValues)
        {
            if (CurveOf(source.TransferCode) is null)
            {
                return source.TransferCode is 16 or 18
                    ? "传输特性 " + source.TransferCode + "(PQ/HLG)是 HDR,不做 tone map,回落到平台 HEIF 解码"
                    : "传输特性 " + source.TransferCode + " 不受支持,回落到平台 HEIF 解码";
            }
            if (!IsSupportedPrimaries(source.PrimariesCode))
            {
                return "原色 " + source.PrimariesCode + " 不受支持(仅支持 BT.709/未指定/BT.2020/Display P3),回落到平台 HEIF 解码";
            }
        }
        if (source.MatrixCode is not (1 or 2 or 5 or 6 or 9))
        {
            return "矩阵系数 " + source.MatrixCode + " 不受支持";
        }
        for (var i = 0; i < source.Tiles.Count; i++)
        {
            var tile = source.Tiles[i];
            if (tile.Y == IntPtr.Zero) return "瓦片亮度平面为空";
            if (source.Chroma != HeifChromaSampling.Monochrome && (tile.Cb == IntPtr.Zero || tile.Cr == IntPtr.Zero))
            {
                return "瓦片色度平面为空";
            }
            if (tile.Width <= 0 || tile.Height <= 0) return "瓦片尺寸非法";
            if (tile.StrideY < tile.Width * (source.BitDepth > 8 ? 2 : 1)) return "瓦片亮度跨距过小";
            if (source.Chroma != HeifChromaSampling.Monochrome)
            {
                // 向量路径一次读 4 个色度样本(10/12 位为 8 字节),跨距必须容纳。
                var chromaMinimum = 4 * (source.BitDepth > 8 ? 2 : 1);
                if (tile.StrideCb < chromaMinimum) return "瓦片色度跨距过小";
                if (tile.StrideCr < chromaMinimum) return "瓦片色度跨距过小";
            }
        }
        return null;
    }

    /// <summary>nclx 传输特性代码 → 本站点实现的曲线族;不支持的返回 null。</summary>
    /// <param name="code">nclx 传输特性代码。</param>
    /// <returns>曲线族;不支持时 null。</returns>
    private static TransferCurve? CurveOf(int code) => code switch
    {
        // 2 = 未指定、13 = sRGB:按 sRGB 曲线解释(桌面惯例)。
        2 or 13 => TransferCurve.Srgb,
        // 1 = BT.709、6 = BT.601、14/15 = BT.2020 10/12 位:三者是同一 OETF 曲线族(α=1.099, β≈0.018)。
        1 or 6 or 14 or 15 => TransferCurve.Bt709,
        // 4 = BT.470 System M(gamma 2.2)、5 = BT.470 System BG(gamma 2.8)。
        4 => TransferCurve.Gamma22,
        5 => TransferCurve.Gamma28,
        8 => TransferCurve.Linear,
        _ => null,
    };

    /// <summary>nclx 原色代码是否在支持面内(1/2 = BT.709 即 sRGB 原色、9 = BT.2020、12 = Display P3)。</summary>
    /// <param name="code">nclx 原色代码。</param>
    /// <returns>支持返回 true。</returns>
    private static bool IsSupportedPrimaries(int code) => code is 1 or 2 or 9 or 12;

    // ---------------------------------------------------------------
    // 逐像素直通(目标尺寸 == 画布)
    // ---------------------------------------------------------------

    private static unsafe void RunFullResolution(Plan plan, byte* destination, ParallelOptions options)
    {
        var bands = (plan.TargetHeight + FullResBandRows - 1) / FullResBandRows;
        if (options.MaxDegreeOfParallelism == 1 && options.TaskScheduler == TaskScheduler.Default)
        {
            RunFullResolutionRange(plan, destination, 0, bands);
            return;
        }
        Parallel.For(0, bands, options, band =>
        {
            var startY = band * FullResBandRows;
            var endY = Math.Min(plan.TargetHeight, startY + FullResBandRows);
            var scratch = stackalloc float[24];
            var single = stackalloc float[3];
            for (var y = startY; y < endY; y++)
            {
                var row = destination + ((long)y * plan.DestinationStride);
                ConvertRow(plan, y, row, scratch, single);
            }
        });
    }

    private static unsafe void RunFullResolutionRange(Plan plan, byte* destination, int firstBand, int lastBand)
    {
        var scratch = stackalloc float[24];
        var single = stackalloc float[3];
        for (var band = firstBand; band < lastBand; band++)
        {
            var startY = band * FullResBandRows;
            var endY = Math.Min(plan.TargetHeight, startY + FullResBandRows);
            for (var y = startY; y < endY; y++)
            {
                var row = destination + ((long)y * plan.DestinationStride);
                ConvertRow(plan, y, row, scratch, single);
            }
        }
    }

    /// <summary>按输出格式把一整行画布像素写进目标缓冲。</summary>
    private static unsafe void ConvertRow(Plan plan, int y, byte* destination, float* scratch, float* single)
    {
        switch (plan.Format)
        {
            case OutputFormat.RgbaF16:
                ConvertRowToHalf(plan, y, destination, scratch, single);
                break;
            case OutputFormat.Rgba8:
                ConvertRowToRgba8(plan, y, destination, scratch, single);
                break;
            default:
                ConvertRowToBgra(plan, y, destination, scratch, single);
                break;
        }
    }

    private static unsafe void ConvertRowToHalf(Plan plan, int y, byte* destination, float* scratch, float* single)
    {
        var tileRowBase = plan.TileRowBase(y);
        var localY = plan.LocalY(y);
        var chromaWeight = plan.ChromaWeight(localY);
        for (var column = 0; column < plan.Columns; column++)
        {
            var slot = tileRowBase + column;
            var tileColumn = plan.TileIndex[slot];
            if (tileColumn < 0) continue;
            var tile = plan.Tiles[tileColumn];
            var startX = column * plan.TileWidth;
            var endX = Math.Min(plan.CanvasWidth, startX + plan.TileWidth);
            var output = destination + ((long)startX * 8);
            var localEnd = endX - startX;
            var rows = plan.ResolveRows(tile, localY);

            var localX = 0;
            for (; localX + 8 <= localEnd; localX += 8)
            {
                Load8(plan, rows, localX, chromaWeight, scratch);
                PackHalf8(plan, scratch, output + ((long)localX * 8));
            }
            for (; localX < localEnd; localX++)
            {
                Load1(plan, rows, localX, chromaWeight, single);
                PackHalf1(plan, single, output + ((long)localX * 8));
            }
        }
    }

    private static unsafe void ConvertRowToBgra(Plan plan, int y, byte* destination, float* scratch, float* single)
    {
        var tileRowBase = plan.TileRowBase(y);
        var localY = plan.LocalY(y);
        var chromaWeight = plan.ChromaWeight(localY);
        for (var column = 0; column < plan.Columns; column++)
        {
            var slot = tileRowBase + column;
            var tileColumn = plan.TileIndex[slot];
            if (tileColumn < 0) continue;
            var tile = plan.Tiles[tileColumn];
            var startX = column * plan.TileWidth;
            var endX = Math.Min(plan.CanvasWidth, startX + plan.TileWidth);
            var output = destination + ((long)startX * 4);
            var localEnd = endX - startX;
            var rows = plan.ResolveRows(tile, localY);

            var localX = 0;
            for (; localX + 8 <= localEnd; localX += 8)
            {
                Load8(plan, rows, localX, chromaWeight, scratch);
                PackBgra8(plan, scratch, output + ((long)localX * 4));
            }
            for (; localX < localEnd; localX++)
            {
                Load1(plan, rows, localX, chromaWeight, single);
                PackBgra1(plan, single, output + ((long)localX * 4));
            }
        }
    }

    private static unsafe void ConvertRowToRgba8(Plan plan, int y, byte* destination, float* scratch, float* single)
    {
        var tileRowBase = plan.TileRowBase(y);
        var localY = plan.LocalY(y);
        var chromaWeight = plan.ChromaWeight(localY);
        for (var column = 0; column < plan.Columns; column++)
        {
            var slot = tileRowBase + column;
            var tileColumn = plan.TileIndex[slot];
            if (tileColumn < 0) continue;
            var tile = plan.Tiles[tileColumn];
            var startX = column * plan.TileWidth;
            var endX = Math.Min(plan.CanvasWidth, startX + plan.TileWidth);
            var output = destination + ((long)startX * 4);
            var localEnd = endX - startX;
            var rows = plan.ResolveRows(tile, localY);

            var localX = 0;
            for (; localX + 8 <= localEnd; localX += 8)
            {
                Load8(plan, rows, localX, chromaWeight, scratch);
                PackRgba8(plan, scratch, output + ((long)localX * 4));
            }
            for (; localX < localEnd; localX++)
            {
                Load1(plan, rows, localX, chromaWeight, single);
                PackRgba1(plan, single, output + ((long)localX * 4));
            }
        }
    }

    // ---------------------------------------------------------------
    // 重采样(目标尺寸 < 画布)
    // ---------------------------------------------------------------

    private static unsafe void RunResample(Plan plan, byte* destination, ParallelOptions options)
    {
        var horizontal = ResampleTaps.Build(plan.CanvasWidth, plan.TargetWidth);
        var vertical = ResampleTaps.Build(plan.CanvasHeight, plan.TargetHeight);

        var sourceRowFloats = (3L * plan.CanvasWidth) + RowPaddingFloats;
        var horizontalRowFloats = (3L * plan.TargetWidth) + RowPaddingFloats;
        var outputRowFloats = (3L * plan.TargetWidth) + RowPaddingFloats;

        var rowBytes = horizontalRowFloats * sizeof(float);
        var cacheRows = Math.Max(8L, ScratchBudgetPerWorker / rowBytes);
        var usable = Math.Max(1.0, cacheRows - vertical.MaxTaps - 2);
        var ratio = plan.CanvasHeight / (double)plan.TargetHeight;
        // 每个输出行要跨 ratio 个源行,带宽按"源行"预算反推,再按抽头表实测最大跨距。
        var bandRows = (int)Math.Clamp((usable / ratio) - 2.0, 1.0, 256.0);
        var bandCount = (plan.TargetHeight + bandRows - 1) / bandRows;
        var maxSpan = 1;
        for (var band = 0; band < bandCount; band++)
        {
            var startY = band * bandRows;
            var endY = Math.Min(plan.TargetHeight, startY + bandRows);
            if (startY >= endY) continue;
            var first = vertical.Starts[startY];
            var last = vertical.Starts[endY - 1] + vertical.Counts[endY - 1] - 1;
            if (last - first + 1 > maxSpan) maxSpan = last - first + 1;
        }

        Parallel.For(
            0,
            bandCount,
            options,
            () => ResampleWorker.Create(sourceRowFloats, horizontalRowFloats, maxSpan, outputRowFloats),
            (band, _, worker) =>
            {
                ProcessBand(
                    plan,
                    band,
                    bandRows,
                    horizontal,
                    vertical,
                    destination,
                    worker);
                return worker;
            },
            static worker => worker.Dispose());
    }

    private static unsafe void ProcessBand(
        Plan plan,
        int band,
        int bandRows,
        ResampleTaps horizontal,
        ResampleTaps vertical,
        byte* destination,
        ResampleWorker worker)
    {
        var startY = band * bandRows;
        var endY = Math.Min(plan.TargetHeight, startY + bandRows);
        if (startY >= endY) return;

        var firstSourceRow = vertical.Starts[startY];
        var lastSourceRow = vertical.Starts[endY - 1] + vertical.Counts[endY - 1] - 1;
        var horizontalStride = worker.HorizontalRowStride;

        for (var sourceY = firstSourceRow; sourceY <= lastSourceRow; sourceY++)
        {
            ConvertRowToGammaFloat(plan, sourceY, worker.SourceRow);
            ConvolveHorizontal(
                worker.SourceRow,
                worker.HorizontalRows + ((sourceY - firstSourceRow) * horizontalStride),
                plan.TargetWidth,
                horizontal);
        }

        var outputRow = worker.OutputRow;
        for (var outputY = startY; outputY < endY; outputY++)
        {
            var count = vertical.Counts[outputY];
            var weightBase = outputY * vertical.MaxTaps;
            var tapBase = vertical.Starts[outputY];
            var first = worker.HorizontalRows + ((tapBase - firstSourceRow) * horizontalStride);

            for (int x = 0, i = 0; x < plan.TargetWidth; x++, i += 3)
            {
                var accumulator = Vector128<float>.Zero;
                for (var t = 0; t < count; t++)
                {
                    var source = first + ((long)t * horizontalStride) + i;
                    accumulator += Vector128.LoadUnsafe(ref *(float*)source) * Vector128.Create(vertical.Weights[weightBase + t]);
                }
                Vector128.StoreUnsafe(accumulator, ref *(float*)(outputRow + i));
            }

            var row = destination + ((long)outputY * plan.DestinationStride);
            switch (plan.Format)
            {
                case OutputFormat.RgbaF16:
                    PackHalf(plan, outputRow, plan.TargetWidth, row);
                    break;
                case OutputFormat.Rgba8:
                    PackRgba(plan, outputRow, plan.TargetWidth, row);
                    break;
                default:
                    PackBgra(plan, outputRow, plan.TargetWidth, row);
                    break;
            }
        }
    }

    /// <summary>把画布源行转换成 gamma 编码 R'G'B'(3 float/像素,行优先),供重采样使用。</summary>
    private static unsafe void ConvertRowToGammaFloat(Plan plan, int y, float* destination)
    {
        var tileRowBase = plan.TileRowBase(y);
        var localY = plan.LocalY(y);
        var chromaWeight = plan.ChromaWeight(localY);
        var scratch = stackalloc float[24];
        var single = stackalloc float[3];
        for (var column = 0; column < plan.Columns; column++)
        {
            var slot = tileRowBase + column;
            var tileColumn = plan.TileIndex[slot];
            if (tileColumn < 0) continue;
            var tile = plan.Tiles[tileColumn];
            var startX = column * plan.TileWidth;
            var endX = Math.Min(plan.CanvasWidth, startX + plan.TileWidth);
            var rows = plan.ResolveRows(tile, localY);
            var output = destination + ((long)startX * 3);
            var localEnd = endX - startX;

            var localX = 0;
            for (; localX + 8 <= localEnd; localX += 8)
            {
                Load8(plan, rows, localX, chromaWeight, scratch);
                var store = output + ((long)localX * 3);
                for (var i = 0; i < 8; i++)
                {
                    store[(i * 3) + 0] = scratch[i];
                    store[(i * 3) + 1] = scratch[8 + i];
                    store[(i * 3) + 2] = scratch[16 + i];
                }
            }
            for (; localX < localEnd; localX++)
            {
                Load1(plan, rows, localX, chromaWeight, single);
                var store = output + ((long)localX * 3);
                store[0] = single[0];
                store[1] = single[1];
                store[2] = single[2];
            }
        }
    }

    private static unsafe void ConvolveHorizontal(
        float* source,
        float* destination,
        int targetWidth,
        ResampleTaps taps)
    {
        for (var x = 0; x < targetWidth; x++)
        {
            var count = taps.Counts[x];
            var weightBase = x * taps.MaxTaps;
            var pixelBase = taps.Starts[x];
            var accumulator = Vector128<float>.Zero;
            for (var t = 0; t < count; t++)
            {
                var pixel = source + ((long)(pixelBase + t) * 3);
                accumulator += Vector128.LoadUnsafe(ref *(float*)pixel) * Vector128.Create(taps.Weights[weightBase + t]);
            }
            Vector128.StoreUnsafe(accumulator, ref *(float*)(destination + ((long)x * 3)));
        }
    }

    // ---------------------------------------------------------------
    // 采样与色彩换算
    // ---------------------------------------------------------------

    /// <summary>取 8 个连续像素的 Y/Cb/Cr,按平面写入 <paramref name="destination"/>(各 8 个 float)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Load8(
        Plan plan,
        in TileRows rows,
        int localX,
        float chromaWeight,
        float* destination)
    {
        Vector128<float> y0, y1, cb0, cb1, cr0, cr1;
        var chroma = plan.Chroma;

        if (chroma == HeifChromaSampling.Monochrome)
        {
            cb0 = cb1 = cr0 = cr1 = plan.ChromaMidVector;
        }
        else if (chroma == HeifChromaSampling.Yuv444)
        {
            Load8Samples(plan.WideSamples, rows.Cb0, localX, out cb0, out cb1);
            Load8Samples(plan.WideSamples, rows.Cr0, localX, out cr0, out cr1);
        }
        else
        {
            var chromaX = localX >> 1;
            var cb = Load4Samples(plan.WideSamples, rows.Cb0, chromaX);
            var cr = Load4Samples(plan.WideSamples, rows.Cr0, chromaX);
            if (chromaWeight > 0f)
            {
                var nextCb = Load4Samples(plan.WideSamples, rows.Cb1, chromaX);
                var nextCr = Load4Samples(plan.WideSamples, rows.Cr1, chromaX);
                cb += (nextCb - cb) * Vector128.Create(chromaWeight);
                cr += (nextCr - cr) * Vector128.Create(chromaWeight);
            }
            cb0 = Vector128.Shuffle(cb, Vector128.Create(0, 0, 1, 1));
            cb1 = Vector128.Shuffle(cb, Vector128.Create(2, 2, 3, 3));
            cr0 = Vector128.Shuffle(cr, Vector128.Create(0, 0, 1, 1));
            cr1 = Vector128.Shuffle(cr, Vector128.Create(2, 2, 3, 3));
        }

        Load8Samples(plan.WideSamples, rows.Y, localX, out y0, out y1);

        Store4(plan, y0, cb0, cr0, destination, 0);
        Store4(plan, y1, cb1, cr1, destination, 4);
    }

    /// <summary>标量版本,用于不足 8 像素的尾部。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Load1(
        Plan plan,
        in TileRows rows,
        int localX,
        float chromaWeight,
        float* destination)
    {
        var wide = plan.WideSamples;
        var luma = wide ? ((ushort*)rows.Y)[localX] : rows.Y[localX];

        float cb;
        float cr;
        if (plan.Chroma == HeifChromaSampling.Monochrome)
        {
            cb = plan.ChromaMid;
            cr = plan.ChromaMid;
        }
        else
        {
            var chromaX = plan.Chroma == HeifChromaSampling.Yuv444 ? localX : localX >> 1;
            cb = wide ? ((ushort*)rows.Cb0)[chromaX] : rows.Cb0[chromaX];
            cr = wide ? ((ushort*)rows.Cr0)[chromaX] : rows.Cr0[chromaX];
            if (chromaWeight > 0f)
            {
                var nextCb = wide ? ((ushort*)rows.Cb1)[chromaX] : rows.Cb1[chromaX];
                var nextCr = wide ? ((ushort*)rows.Cr1)[chromaX] : rows.Cr1[chromaX];
                cb += (nextCb - cb) * chromaWeight;
                cr += (nextCr - cr) * chromaWeight;
            }
        }

        Color(plan, luma, cb, cr, out var r, out var g, out var b);
        destination[0] = r;
        destination[1] = g;
        destination[2] = b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Load8Samples(bool wide, byte* row, int index, out Vector128<float> low, out Vector128<float> high)
    {
        if (wide)
        {
            var raw = Vector128.LoadUnsafe(ref *(ushort*)(row + ((long)index * 2)));
            low = Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.WidenLower(raw)));
            high = Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.WidenUpper(raw)));
        }
        else
        {
            var raw = Vector128.Create(Vector64.LoadUnsafe(ref *(row + index)), Vector64<byte>.Zero);
            var pairs = Vector128.WidenLower(raw);
            low = Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.WidenLower(pairs)));
            high = Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.WidenUpper(pairs)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector128<float> Load4Samples(bool wide, byte* row, int index)
    {
        if (wide)
        {
            var raw = Vector128.Create(
                Vector64.LoadUnsafe(ref *(ushort*)(row + ((long)index * 2))),
                Vector64<ushort>.Zero);
            return Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.WidenLower(raw)));
        }
        var scalar = row + index;
        return Vector128.ConvertToSingle(Vector128.Create((int)scalar[0], scalar[1], scalar[2], scalar[3]));
    }

    /// <summary>
    /// 4 像素的 R'G'B' 写入平面布局的临时行:R 在前 8 个 float、G 次之、B 最后,
    /// <paramref name="channelOffset"/> 决定落在每组的哪 4 个槽位(0–3 或 4–7)。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store4(
        Plan plan,
        Vector128<float> y,
        Vector128<float> cb,
        Vector128<float> cr,
        float* destination,
        int channelOffset)
    {
        var luma = (y * plan.LumaScaleVector) + plan.LumaOffsetVector;
        var blue = (cb * plan.ChromaScaleVector) + plan.ChromaOffsetVector;
        var red = (cr * plan.ChromaScaleVector) + plan.ChromaOffsetVector;
        Vector128.StoreUnsafe(luma + (red * plan.RedFromCr), ref *(float*)(destination + channelOffset));
        Vector128.StoreUnsafe(
            luma - (blue * plan.GreenFromCb) - (red * plan.GreenFromCr),
            ref *(float*)(destination + 8 + channelOffset));
        Vector128.StoreUnsafe(luma + (blue * plan.BlueFromCb), ref *(float*)(destination + 16 + channelOffset));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Color(Plan plan, float y, float cb, float cr, out float r, out float g, out float b)
    {
        var luma = (y * plan.LumaScale) + plan.LumaOffset;
        var blue = (cb * plan.ChromaScale) + plan.ChromaOffset;
        var red = (cr * plan.ChromaScale) + plan.ChromaOffset;
        r = luma + (red * plan.RedFromCrScalar);
        g = luma - (blue * plan.GreenFromCbScalar) - (red * plan.GreenFromCrScalar);
        b = luma + (blue * plan.BlueFromCbScalar);
    }

    // ---------------------------------------------------------------
    // 输出打包
    // ---------------------------------------------------------------

    private static unsafe void PackHalf8(Plan plan, float* scratch, byte* destination)
    {
        for (var i = 0; i < 8; i++)
        {
            FinishRgb(plan, scratch[i], scratch[8 + i], scratch[16 + i], out var r, out var g, out var b);
            WriteRgbaHalf(destination + (i * 8), r, g, b);
        }
    }

    private static unsafe void PackHalf1(Plan plan, float* rgb, byte* destination)
    {
        FinishRgb(plan, rgb[0], rgb[1], rgb[2], out var r, out var g, out var b);
        WriteRgbaHalf(destination, r, g, b);
    }

    private static unsafe void PackHalf(Plan plan, float* interleaved, int width, byte* destination)
    {
        for (var x = 0; x < width; x++)
        {
            var source = interleaved + ((long)x * 3);
            FinishRgb(plan, source[0], source[1], source[2], out var r, out var g, out var b);
            WriteRgbaHalf(destination + ((long)x * 8), r, g, b);
        }
    }

    /// <summary>写入 RGBA 四个半浮点(R/G 与 B/1.0 各一次 4 字节存储;alpha 为常量 0x3C00)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteRgbaHalf(byte* target, float r, float g, float b)
    {
        Unsafe.WriteUnaligned(ref *target, ToHalfBits(r) | (ToHalfBits(g) << 16));
        Unsafe.WriteUnaligned(ref *(target + 4), ToHalfBits(b) | 0x3C000000u);
    }

    /// <summary>
    /// float32 → IEEE 半浮点(就近舍入到偶数)。
    /// </summary>
    /// <remarks>
    /// 输入已由 <see cref="Linearize"/> 限制在 [0,1]:正规数走"加半个 ulp 再截断"的整数快路,
    /// 小于最小正规数 2^-14 的线性值改用次正规公式 <c>round(v·2^24)</c>,两级都保持就近舍入。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ToHalfBits(float value)
    {
        if (value < 6.1035156e-5f) return (uint)((value * 16777216f) + 0.5f);
        var bits = BitConverter.SingleToUInt32Bits(value);
        return ((bits + 0xFFFu + ((bits >> 13) & 1u)) >> 13) - 0x1C000u;
    }

    private static unsafe void PackBgra8(Plan plan, float* scratch, byte* destination)
    {
        for (var i = 0; i < 8; i++)
        {
            PackBgraPixel(plan, scratch[i], scratch[8 + i], scratch[16 + i], destination + (i * 4));
        }
    }

    private static unsafe void PackBgra1(Plan plan, float* rgb, byte* destination)
        => PackBgraPixel(plan, rgb[0], rgb[1], rgb[2], destination);

    private static unsafe void PackBgra(Plan plan, float* interleaved, int width, byte* destination)
    {
        for (var x = 0; x < width; x++)
        {
            var source = interleaved + ((long)x * 3);
            PackBgraPixel(plan, source[0], source[1], source[2], destination + ((long)x * 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PackBgraPixel(Plan plan, float gr, float gg, float gb, byte* target)
    {
        if (plan.LinearToSrgbRequired)
        {
            FinishRgb(plan, gr, gg, gb, out var r, out var g, out var b);
            target[0] = QuantizeLinear(b);
            target[1] = QuantizeLinear(g);
            target[2] = QuantizeLinear(r);
        }
        else
        {
            target[0] = QuantizeGamma(gb);
            target[1] = QuantizeGamma(gg);
            target[2] = QuantizeGamma(gr);
        }
        target[3] = 255;
    }

    private static unsafe void PackRgba8(Plan plan, float* scratch, byte* destination)
    {
        for (var i = 0; i < 8; i++)
        {
            PackRgbaPixel(plan, scratch[i], scratch[8 + i], scratch[16 + i], destination + (i * 4));
        }
    }

    private static unsafe void PackRgba1(Plan plan, float* rgb, byte* destination)
        => PackRgbaPixel(plan, rgb[0], rgb[1], rgb[2], destination);

    private static unsafe void PackRgba(Plan plan, float* interleaved, int width, byte* destination)
    {
        for (var x = 0; x < width; x++)
        {
            var source = interleaved + ((long)x * 3);
            PackRgbaPixel(plan, source[0], source[1], source[2], destination + ((long)x * 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PackRgbaPixel(Plan plan, float gr, float gg, float gb, byte* target)
    {
        if (plan.Rgba8ToP3)
        {
            FinishRgbToP3(plan, gr, gg, gb, out var pr, out var pg, out var pb);
            target[0] = QuantizeLinear(pr);
            target[1] = QuantizeLinear(pg);
            target[2] = QuantizeLinear(pb);
        }
        else if (plan.LinearToSrgbRequired)
        {
            FinishRgb(plan, gr, gg, gb, out var sr, out var sg, out var sb);
            target[0] = QuantizeLinear(sr);
            target[1] = QuantizeLinear(sg);
            target[2] = QuantizeLinear(sb);
        }
        else
        {
            target[0] = QuantizeGamma(gr);
            target[1] = QuantizeGamma(gg);
            target[2] = QuantizeGamma(gb);
        }
        target[3] = 255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteHalf(byte* target, float value)
        => Unsafe.WriteUnaligned(ref *target, BitConverter.HalfToUInt16Bits((Half)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte QuantizeGamma(float gamma)
    {
        var value = gamma * 255f;
        if (!(value > 0f)) return 0;
        if (value >= 255f) return 255;
        return (byte)(value + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte QuantizeLinear(float linear)
    {
        if (!(linear > 0f)) return 0;
        if (linear >= 1f) return 255;
        return (byte)((Encode(linear) * 255f) + 0.5f);
    }

    /// <summary>
    /// 编码值三分量 → 线性光 sRGB(按计划的原色矩阵与传递曲线;编码值出口原样返回)。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishRgb(Plan plan, float gr, float gg, float gb, out float r, out float g, out float b)
    {
        if (plan.CodeValues)
        {
            // ICC 空间内的编码值:不换曲线、不换原色,只做 [0,1] 夹紧(越界码值本身无定义)。
            r = gr < 0f ? 0f : gr > 1f ? 1f : gr;
            g = gg < 0f ? 0f : gg > 1f ? 1f : gg;
            b = gb < 0f ? 0f : gb > 1f ? 1f : gb;
            return;
        }
        var lr = plan.Linearize(gr);
        var lg = plan.Linearize(gg);
        var lb = plan.Linearize(gb);
        var matrix = plan.ToSrgb;
        if (matrix is null)
        {
            r = lr;
            g = lg;
            b = lb;
            return;
        }
        r = (matrix[0] * lr) + (matrix[1] * lg) + (matrix[2] * lb);
        g = (matrix[3] * lr) + (matrix[4] * lg) + (matrix[5] * lb);
        b = (matrix[6] * lr) + (matrix[7] * lg) + (matrix[8] * lb);
    }

    /// <summary>编码值三分量 → 线性光 sRGB → 线性光 Display P3(按源原色矩阵)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishRgbToP3(Plan plan, float gr, float gg, float gb, out float r, out float g, out float b)
    {
        var lr = plan.Linearize(gr);
        var lg = plan.Linearize(gg);
        var lb = plan.Linearize(gb);
        var matrix = plan.ToP3;
        if (matrix is null)
        {
            r = lr;
            g = lg;
            b = lb;
            return;
        }
        r = (matrix[0] * lr) + (matrix[1] * lg) + (matrix[2] * lb);
        g = (matrix[3] * lr) + (matrix[4] * lg) + (matrix[5] * lb);
        b = (matrix[6] * lr) + (matrix[7] * lg) + (matrix[8] * lb);
    }

    /// <summary>sRGB 光电转换:线性光 → 编码值(4096 项表 + 线性插值)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Encode(float value)
    {
        if (!(value > 0f)) return 0f;
        if (value >= 1f) return 1f;
        var position = value * LutSize;
        var index = (int)position;
        if (index >= LutSize) index = LutSize - 1;
        var low = s_encodeLut[index];
        return low + ((s_encodeLut[index + 1] - low) * (position - index));
    }

    /// <summary>构建 sRGB 光电转换表:线性光 → 编码值,表项按 value = i / LutSize 采样。</summary>
    /// <returns>4096 + 1 项编码表。</returns>
    private static float[] BuildSrgbEncodeLut()
    {
        var table = new float[LutSize + 1];
        for (var i = 0; i <= LutSize; i++)
        {
            var value = i / (float)LutSize;
            table[i] = value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;
        }
        return table;
    }

    /// <summary>
    /// 构建编码值 → 线性光的表:sRGB EOTF、BT.709 OETF 的逆曲线,或纯幂曲线(伽马 2.2/2.8);
    /// 表项按 value = i / LutSize 采样。
    /// </summary>
    /// <param name="curve">曲线族(线性由 <see cref="Plan.LinearLut"/> 为 null 表达,不走本表)。</param>
    /// <returns>4096 + 1 项反编码表。</returns>
    private static float[] BuildLinearizeLut(TransferCurve curve)
    {
        var table = new float[LutSize + 1];
        for (var i = 0; i <= LutSize; i++)
        {
            var value = i / (float)LutSize;
            table[i] = curve switch
            {
                TransferCurve.Bt709 =>
                    // BT.709 OETF 的逆:V < 0.081 时 L = V / 4.5,否则 L = ((V + 0.099) / 1.099)^(1/0.45)。
                    value <= 0.081f ? value / 4.5f : MathF.Pow((value + 0.099f) / 1.099f, 1f / 0.45f),
                // nclx 4 = BT.470 System M(gamma 2.2)、5 = BT.470 System BG(gamma 2.8):纯幂曲线,没有膝部。
                TransferCurve.Gamma22 => MathF.Pow(value, 2.2f),
                TransferCurve.Gamma28 => MathF.Pow(value, 2.8f),
                _ => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f),
            };
        }
        return table;
    }

    // ---------------------------------------------------------------
    // 转换计划
    // ---------------------------------------------------------------

    /// <summary>一行瓦片的三个平面入口(已按局部坐标定位)。</summary>
    private unsafe readonly ref struct TileRows
    {
        public TileRows(byte* y, byte* cb0, byte* cb1, byte* cr0, byte* cr1)
        {
            Y = y;
            Cb0 = cb0;
            Cb1 = cb1;
            Cr0 = cr0;
            Cr1 = cr1;
        }

        public readonly byte* Y;

        public readonly byte* Cb0;

        public readonly byte* Cb1;

        public readonly byte* Cr0;

        public readonly byte* Cr1;
    }

    private sealed class Plan
    {
        public Plan(
            YuvImage source,
            int targetWidth,
            int targetHeight,
            int destinationStride,
            OutputFormat format,
            ImageColorSpace rgba8ColorSpace,
            bool codeValues)
        {
            Tiles = new YuvTile[source.Tiles.Count];
            for (var i = 0; i < Tiles.Length; i++) Tiles[i] = source.Tiles[i];
            CanvasWidth = source.CanvasWidth;
            CanvasHeight = source.CanvasHeight;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
            DestinationStride = destinationStride;
            Format = format;
            Chroma = source.Chroma;
            WideSamples = source.BitDepth > 8;
            IsPassthrough = targetWidth == CanvasWidth && targetHeight == CanvasHeight;
            CodeValues = codeValues;

            // 编码值出口不做任何色彩换算:原色矩阵与传递曲线都不参与。
            var curve = codeValues ? TransferCurve.Linear : CurveOf(source.TransferCode) ?? TransferCurve.Srgb;
            Curve = curve;
            LinearLut = curve switch
            {
                TransferCurve.Srgb => s_linearizeSrgbLut,
                TransferCurve.Bt709 => s_linearizeBt709Lut,
                TransferCurve.Gamma22 => s_linearizeGamma22Lut,
                TransferCurve.Gamma28 => s_linearizeGamma28Lut,
                _ => null,
            };

            var primaries = codeValues ? 1 : source.PrimariesCode;
            var (toSrgb, toP3) = Matrices(primaries);
            ToSrgb = toSrgb;
            ToP3 = toP3;

            // 8 位出口:能直接量化码值时走原快路径,否则线性化 → 原色矩阵 → sRGB 编码。
            LinearToSrgbRequired = !codeValues && (curve != TransferCurve.Srgb || ToSrgb is not null);
            Rgba8ToP3 = !codeValues && format == OutputFormat.Rgba8 && rgba8ColorSpace == ImageColorSpace.DisplayP3;

            TileWidth = Tiles[0].Width;
            TileHeight = Tiles[0].Height;
            Columns = (CanvasWidth + TileWidth - 1) / TileWidth;
            Rows = (CanvasHeight + TileHeight - 1) / TileHeight;

            // 按画布位置建索引:瓦片左上角决定格位,缺失格位用 -1 标记(校验在 DescribeUnsupported)。
            TileIndex = new int[Columns * Rows];
            Array.Fill(TileIndex, -1);
            for (var i = 0; i < Tiles.Length; i++)
            {
                var column = Tiles[i].Column / TileWidth;
                var row = Tiles[i].Row / TileHeight;
                if (column < 0 || column >= Columns || row < 0 || row >= Rows) continue;
                var slot = (row * Columns) + column;
                if (TileIndex[slot] < 0) TileIndex[slot] = i;
            }

            var depth = source.BitDepth;
            var scale = (float)(1 << (depth - 8));
            var maximum = (float)((1 << depth) - 1);
            if (source.FullRange)
            {
                LumaScale = 1f / maximum;
                LumaOffset = 0f;
                ChromaScale = 1f / maximum;
                ChromaOffset = -(1 << (depth - 1)) / maximum;
            }
            else
            {
                LumaScale = 1f / (219f * scale);
                LumaOffset = -16f / 219f;
                ChromaScale = 1f / (224f * scale);
                ChromaOffset = -128f / 224f;
            }

            ChromaMid = 128f * scale;

            var (kr, kb) = source.MatrixCode switch
            {
                1 => (0.2126f, 0.0722f),
                9 => (0.2627f, 0.0593f),
                _ => (0.299f, 0.114f),
            };
            var kg = 1f - kr - kb;
            RedFromCrScalar = 2f * (1f - kr);
            BlueFromCbScalar = 2f * (1f - kb);
            GreenFromCbScalar = 2f * kb * (1f - kb) / kg;
            GreenFromCrScalar = 2f * kr * (1f - kr) / kg;

            LumaScaleVector = Vector128.Create(LumaScale);
            LumaOffsetVector = Vector128.Create(LumaOffset);
            ChromaScaleVector = Vector128.Create(ChromaScale);
            ChromaOffsetVector = Vector128.Create(ChromaOffset);
            ChromaMidVector = Vector128.Create(ChromaMid);
            RedFromCr = Vector128.Create(RedFromCrScalar);
            BlueFromCb = Vector128.Create(BlueFromCbScalar);
            GreenFromCb = Vector128.Create(GreenFromCbScalar);
            GreenFromCr = Vector128.Create(GreenFromCrScalar);

        }

        public YuvTile[] Tiles { get; }

        public int CanvasWidth { get; }

        public int CanvasHeight { get; }

        public int TargetWidth { get; }

        public int TargetHeight { get; }

        public int DestinationStride { get; }

        public OutputFormat Format { get; }

        /// <summary>是否走 ICC 编码值出口(不线性化、不做原色转换)。</summary>
        public bool CodeValues { get; }

        /// <summary>8 位 RGBA 目标是否为 Display P3(否则按 sRGB 输出)。</summary>
        public bool Rgba8ToP3 { get; }

        /// <summary>8 位出口是否必须经"线性化 → 原色矩阵 → sRGB 编码"才能写出正确码值。</summary>
        public bool LinearToSrgbRequired { get; }

        /// <summary>源的传递曲线族。</summary>
        public TransferCurve Curve { get; }

        /// <summary>编码值 → 线性光的查表;线性传递特性时为 null(码值即线性值)。</summary>
        public float[]? LinearLut { get; }

        public bool IsPassthrough { get; }

        public HeifChromaSampling Chroma { get; }

        public bool WideSamples { get; }

        public int Columns { get; }

        public int Rows { get; }

        /// <summary>画布格位 → 瓦片下标;缺瓦片的格位为 -1。</summary>
        public int[] TileIndex { get; }

        public int TileWidth { get; }

        public int TileHeight { get; }

        /// <summary>线性源原色 → 线性 sRGB 的矩阵;null 表示源原色已是 sRGB。</summary>
        public float[]? ToSrgb { get; }

        /// <summary>线性源原色 → 线性 Display P3 的矩阵;null 表示源原色已是 Display P3。</summary>
        public float[]? ToP3 { get; }

        public float ChromaMid { get; }

        public float LumaScale { get; }

        public float LumaOffset { get; }

        public float ChromaScale { get; }

        public float ChromaOffset { get; }

        public float RedFromCrScalar { get; }

        public float BlueFromCbScalar { get; }

        public float GreenFromCbScalar { get; }

        public float GreenFromCrScalar { get; }

        public Vector128<float> LumaScaleVector { get; }

        public Vector128<float> LumaOffsetVector { get; }

        public Vector128<float> ChromaScaleVector { get; }

        public Vector128<float> ChromaOffsetVector { get; }

        public Vector128<float> ChromaMidVector { get; }

        public Vector128<float> RedFromCr { get; }

        public Vector128<float> BlueFromCb { get; }

        public Vector128<float> GreenFromCb { get; }

        public Vector128<float> GreenFromCr { get; }

        /// <summary>编码值 → 线性光(按 <see cref="Curve"/>;线性传递特性原样返回,超出 [0,1] 夹紧)。</summary>
        /// <param name="value">源编码值。</param>
        /// <returns>线性光值。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Linearize(float value)
        {
            if (!(value > 0f)) return 0f;
            if (value >= 1f) return 1f;
            var table = LinearLut;
            if (table is null) return value;
            // 表项 i 对应 i / LutSize(见 BuildLinearizeLut),位置按同一比例换算。
            var position = value * LutSize;
            var index = (int)position;
            if (index >= LutSize) index = LutSize - 1;
            var low = table[index];
            return low + ((table[index + 1] - low) * (position - index));
        }

        /// <summary>按 nclx 原色代码取"源 → 线性 sRGB"与"源 → 线性 Display P3"的矩阵(null 表示已是该原色)。</summary>
        /// <param name="primariesCode">nclx 原色代码(1/2 = BT.709、9 = BT.2020、12 = Display P3)。</param>
        /// <returns>两个行优先的 3×3 矩阵。</returns>
        private static (float[]? ToSrgb, float[]? ToP3) Matrices(int primariesCode) => primariesCode switch
        {
            // BT.709 与 sRGB 原色相同;2 = 未指定按 BT.709 处理。
            9 => (s_bt2020ToSrgbMatrix, s_bt2020ToP3Matrix),
            12 => (s_p3ToSrgbMatrix, null),
            _ => (null, s_srgbToP3Matrix),
        };

        public int TileRowBase(int y) => Math.Min(Rows - 1, y / TileHeight) * Columns;

        public int LocalY(int y) => y - (Math.Min(Rows - 1, y / TileHeight) * TileHeight);

        /// <summary>4:2:0 下该亮度行到下一个色度行的插值权重;其余格式为 0(不需插值)。</summary>
        public float ChromaWeight(int localY)
        {
            if (Chroma != HeifChromaSampling.Yuv420) return 0f;
            var position = (localY - 0.5f) * 0.5f;
            if (position < 0f) return 0f;
            var fraction = position - MathF.Floor(position);
            return fraction;
        }

        public unsafe TileRows ResolveRows(YuvTile tile, int localY)
        {
            var chromaRow0 = localY;
            var chromaRow1 = localY;
            if (Chroma == HeifChromaSampling.Yuv420)
            {
                // 4:2:0 的色度行数按本瓦片高度取,超界在瓦片内夹紧(瓦片缝合处允许如此)。
                var tileChromaRows = (tile.Height + 1) / 2;
                var position = (localY - 0.5f) * 0.5f;
                var row = (int)MathF.Floor(position);
                if (row < 0) row = 0;
                if (row >= tileChromaRows) row = tileChromaRows - 1;
                chromaRow0 = row;
                chromaRow1 = row + 1 >= tileChromaRows ? tileChromaRows - 1 : row + 1;
            }

            var y = (byte*)tile.Y + ((long)localY * tile.StrideY);
            var cb0 = (byte*)tile.Cb + ((long)chromaRow0 * tile.StrideCb);
            var cb1 = (byte*)tile.Cb + ((long)chromaRow1 * tile.StrideCb);
            var cr0 = (byte*)tile.Cr + ((long)chromaRow0 * tile.StrideCr);
            var cr1 = (byte*)tile.Cr + ((long)chromaRow1 * tile.StrideCr);
            return new TileRows(y, cb0, cb1, cr0, cr1);
        }
    }

    private sealed class ResampleTaps
    {
        private ResampleTaps(int[] starts, int[] counts, float[] weights, int maxTaps)
        {
            Starts = starts;
            Counts = counts;
            Weights = weights;
            MaxTaps = maxTaps;
        }

        public int[] Starts { get; }

        public int[] Counts { get; }

        public float[] Weights { get; }

        public int MaxTaps { get; }

        /// <summary>
        /// 构造 Mitchell–Netravali(B=C=1/3)一维抽头。
        /// </summary>
        /// <remarks>
        /// 缩放倍数 &gt; 1 时核支撑按倍数展宽(抗锯齿),抽头中心取 <c>(i+0.5)*ratio-0.5</c>;
        /// 越界抽头按边缘复制折算(与 Skia 的 clamp 铺贴一致),权重最后归一化。
        /// </remarks>
        public static ResampleTaps Build(int sourceLength, int targetLength)
        {
            var ratio = sourceLength / (double)targetLength;
            var filterScale = Math.Max(1.0, ratio);
            var support = 2.0 * filterScale;

            var starts = new int[targetLength];
            var counts = new int[targetLength];
            var weightList = new List<float>(targetLength * 8);
            var maxTaps = 1;

            for (var i = 0; i < targetLength; i++)
            {
                var center = ((i + 0.5) * ratio) - 0.5;
                var left = (int)Math.Ceiling(center - support);
                var right = (int)Math.Floor(center + support);
                var weightStart = weightList.Count;
                var taps = 0;
                var start = -1;
                var total = 0.0;

                for (var j = left; j <= right; j++)
                {
                    var weight = Mitchell((j - center) / filterScale);
                    var index = j < 0 ? 0 : (j >= sourceLength ? sourceLength - 1 : j);
                    if (taps == 0)
                    {
                        start = index;
                        weightList.Add(0f);
                        taps = 1;
                    }
                    else if (index == start + taps - 1)
                    {
                        // 边缘复制:同一目标像素的权重合并
                    }
                    else
                    {
                        weightList.Add(0f);
                        taps++;
                    }
                    weightList[weightStart + taps - 1] += (float)weight;
                    total += weight;
                }

                if (taps == 0 || start < 0 || total == 0.0)
                {
                    weightList.Add(1f);
                    start = Math.Clamp(i, 0, sourceLength - 1);
                    taps = 1;
                    total = 1.0;
                }
                else if (total != 1.0)
                {
                    var inverse = (float)(1.0 / total);
                    for (var t = 0; t < taps; t++) weightList[weightStart + t] *= inverse;
                }

                starts[i] = start;
                counts[i] = taps;
                if (taps > maxTaps) maxTaps = taps;
            }

            // 补齐到统一抽头数,便于按 i*maxTaps 索引。
            var weights = new float[targetLength * maxTaps];
            var cursor = 0;
            for (var i = 0; i < targetLength; i++)
            {
                var taps = counts[i];
                for (var t = 0; t < taps; t++) weights[(i * maxTaps) + t] = weightList[cursor + t];
                cursor += taps;
            }

            return new ResampleTaps(starts, counts, weights, maxTaps);
        }

        /// <summary>Mitchell–Netravali 核,B=C=1/3,支撑 2。</summary>
        private static double Mitchell(double x)
        {
            const double b = 1.0 / 3.0;
            const double c = 1.0 / 3.0;
            x = Math.Abs(x);
            if (x < 1.0)
            {
                var p = ((12.0 - (9.0 * b) - (6.0 * c)) * x) + (-18.0 + (12.0 * b) + (6.0 * c));
                return ((p * x * x) + (6.0 - (2.0 * b))) / 6.0;
            }
            if (x < 2.0)
            {
                var p = (-b - (6.0 * c)) * x;
                p = (p + ((6.0 * b) + (30.0 * c))) * x;
                p = (p + ((-12.0 * b) - (48.0 * c))) * x;
                return (p + ((8.0 * b) + (24.0 * c))) / 6.0;
            }
            return 0.0;
        }
    }

    private sealed class ResampleWorker : IDisposable
    {
        private unsafe ResampleWorker(
            float* buffer,
            long bytes,
            float* sourceRow,
            float* horizontalRows,
            float* outputRow,
            float* single,
            int horizontalRowStride)
        {
            Buffer = buffer;
            Bytes = bytes;
            SourceRow = sourceRow;
            HorizontalRows = horizontalRows;
            OutputRow = outputRow;
            Single = single;
            HorizontalRowStride = horizontalRowStride;
        }

        public unsafe float* SourceRow { get; }

        public unsafe float* HorizontalRows { get; }

        public unsafe float* OutputRow { get; }

        public unsafe float* Single { get; }

        public int HorizontalRowStride { get; }

        private unsafe float* Buffer { get; }

        private long Bytes { get; }

        public static unsafe ResampleWorker Create(
            long sourceRowFloats,
            long horizontalRowFloats,
            int maxSpan,
            long outputRowFloats)
        {
            var totalFloats = sourceRowFloats + (maxSpan * horizontalRowFloats) + outputRowFloats + 4;
            var bytes = totalFloats * sizeof(float);
            var buffer = (float*)NativeMemory.AlignedAlloc((nuint)bytes, 64);
            var cursor = buffer;
            var sourceRow = cursor;
            cursor += sourceRowFloats;
            var horizontalRows = cursor;
            cursor += maxSpan * horizontalRowFloats;
            var outputRow = cursor;
            cursor += outputRowFloats;
            var single = cursor;

            AddScratch(bytes);
            return new ResampleWorker(
                buffer,
                bytes,
                sourceRow,
                horizontalRows,
                outputRow,
                single,
                (int)horizontalRowFloats);
        }

        public unsafe void Dispose()
        {
            if (Buffer is null) return;
            AddScratch(-Bytes);
            NativeMemory.AlignedFree(Buffer);
        }
    }

    private static void AddScratch(long bytes)
    {
        var current = Interlocked.Add(ref s_scratchCurrent, bytes);
        long peak;
        while (current > (peak = Interlocked.Read(ref s_scratchPeak)))
        {
            if (Interlocked.CompareExchange(ref s_scratchPeak, current, peak) == peak) break;
        }
    }
}
