using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewer.Windows.Core.Heif;

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

    /// <summary>nclx 原色代码(1=BT.709,2=未指定→BT.709,12=Display P3)。</summary>
    public int PrimariesCode { get; init; } = 1;

    /// <summary>nclx 传输特性代码(1/6/13/2 视作 sRGB 曲线)。</summary>
    public int TransferCode { get; init; } = 13;

    /// <summary>全范围(true)或视频范围(false)。</summary>
    public bool FullRange { get; init; } = true;
}

/// <summary>
/// YUV 瓦片 → 呈现缓冲的转换器。
/// </summary>
/// <remarks>
/// 两条出口:
/// <list type="bullet">
/// <item>线性光 RGBA 半浮点(scRGB / 线性 sRGB 基色),供线性交换链直接采样;</item>
/// <item>sRGB 8 位 BGRA,供缩略图与分析使用。</item>
/// </list>
/// 目标尺寸等于画布时逐像素直通;小于画布时用 Mitchell–Netravali(B=C=1/3)可分离重采样,
/// 核支撑按缩放倍数展宽(抗锯齿),重采样在 gamma 编码的 R'G'B' 上完成,最后才线性化/量化。
/// 两条路径都按输出行带并行:逐像素直通无中间层;重采样每个线程只持有"一行源 + 若干行已横向重采样"
/// 的缓冲(数 MB 级),不物化全分辨率浮点 RGB 中间图像。
/// </remarks>
public static class YuvImageConverter
{
    /// <summary>并行度上限;0 表示交给线程池(默认)。仅用于测量与调优。</summary>
    public static int MaxDegreeOfParallelism { get; set; }

    /// <summary>最近一次转换的临时缓冲峰值字节数(各并行线程的并发峰值)。</summary>
    public static long LastScratchPeakBytes => Interlocked.Read(ref s_scratchPeak);

    private const int LutSize = 4096;
    private const int FullResBandRows = 8;
    private const long ScratchBudgetPerWorker = 3L << 20;
    private const int RowPaddingFloats = 8;

    private static readonly float[] s_linearLut = BuildLut(false);
    private static readonly float[] s_encodeLut = BuildLut(true);

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
        => Convert(source, targetWidth, targetHeight, destination, destinationStride, true, out unsupportedReason);

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
        => Convert(source, targetWidth, targetHeight, destination, destinationStride, false, out unsupportedReason);

    /// <summary>检查色彩与格式是否可转换,不实际转换。</summary>
    /// <param name="source">输入描述。</param>
    /// <param name="unsupportedReason">不受支持时的原因;支持为 null。</param>
    /// <returns>支持返回 true。</returns>
    public static bool IsSupported(YuvImage source, out string? unsupportedReason)
        => (unsupportedReason = DescribeUnsupported(source)) is null;

    // ---------------------------------------------------------------
    // 准备与调度
    // ---------------------------------------------------------------

    private static bool Convert(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        int destinationStride,
        bool linearHalf,
        out string? unsupportedReason)
    {
        unsupportedReason = DescribeUnsupported(source);
        if (unsupportedReason is not null) return false;

        var bytesPerPixel = linearHalf ? 8 : 4;
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

        var plan = new Plan(source, targetWidth, targetHeight, destinationStride, linearHalf);
        Interlocked.Exchange(ref s_scratchPeak, 0);

        var options = new ParallelOptions();
        var threadLimit = MaxDegreeOfParallelism;
        if (threadLimit > 0) options.MaxDegreeOfParallelism = threadLimit;

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

    private static string? DescribeUnsupported(YuvImage source)
    {
        if (source.CanvasWidth <= 0 || source.CanvasHeight <= 0) return "画布尺寸非法";
        if (source.Tiles.Count == 0) return "没有瓦片";
        if (source.BitDepth is not (8 or 10 or 12)) return "位深 " + source.BitDepth + " 不受支持";
        if (source.Chroma is not (HeifChromaSampling.Monochrome or HeifChromaSampling.Yuv420
            or HeifChromaSampling.Yuv422 or HeifChromaSampling.Yuv444))
        {
            return "色度格式 " + (int)source.Chroma + " 不受支持";
        }
        if (source.TransferCode is not (1 or 2 or 6 or 13))
        {
            return "传输特性 " + source.TransferCode + "(PQ/HLG 等)不受支持,回落到 libheif";
        }
        if (source.PrimariesCode is not (1 or 2 or 12))
        {
            return "原色 " + source.PrimariesCode + " 不受支持,回落到 libheif";
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

    // ---------------------------------------------------------------
    // 逐像素直通(目标尺寸 == 画布)
    // ---------------------------------------------------------------

    private static unsafe void RunFullResolution(Plan plan, byte* destination, ParallelOptions options)
    {
        var bands = (plan.TargetHeight + FullResBandRows - 1) / FullResBandRows;
        var linearHalf = plan.LinearHalf;
        if (options.MaxDegreeOfParallelism == 1)
        {
            RunFullResolutionRange(plan, destination, 0, bands, linearHalf);
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
                if (linearHalf) ConvertRowToHalf(plan, y, row, scratch, single);
                else ConvertRowToBgra(plan, y, row, scratch, single);
            }
        });
    }

    private static unsafe void RunFullResolutionRange(Plan plan, byte* destination, int firstBand, int lastBand, bool linearHalf)
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
                if (linearHalf) ConvertRowToHalf(plan, y, row, scratch, single);
                else ConvertRowToBgra(plan, y, row, scratch, single);
            }
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
            if (plan.LinearHalf) PackHalf(plan, outputRow, plan.TargetWidth, row);
            else PackBgra(plan, outputRow, plan.TargetWidth, row);
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
        if (plan.P3ToSrgb)
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

    /// <summary>gamma 编码三分量 → 线性光 sRGB(必要时做 P3→sRGB 基色转换)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishRgb(Plan plan, float gr, float gg, float gb, out float r, out float g, out float b)
    {
        var lr = Linearize(gr);
        var lg = Linearize(gg);
        var lb = Linearize(gb);
        if (!plan.P3ToSrgb)
        {
            r = lr;
            g = lg;
            b = lb;
            return;
        }
        r = (plan.P3ToSrgbMatrix[0] * lr) + (plan.P3ToSrgbMatrix[1] * lg) + (plan.P3ToSrgbMatrix[2] * lb);
        g = (plan.P3ToSrgbMatrix[3] * lr) + (plan.P3ToSrgbMatrix[4] * lg) + (plan.P3ToSrgbMatrix[5] * lb);
        b = (plan.P3ToSrgbMatrix[6] * lr) + (plan.P3ToSrgbMatrix[7] * lg) + (plan.P3ToSrgbMatrix[8] * lb);
    }

    /// <summary>sRGB 电光转换:编码值 → 线性光(4096 项表 + 线性插值)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Linearize(float value)
    {
        if (!(value > 0f)) return 0f;
        if (value >= 1f) return 1f;
        // 表项 i 对应 i / LutSize(见 BuildLut),位置按同一比例换算。
        var position = value * LutSize;
        var index = (int)position;
        if (index >= LutSize) index = LutSize - 1;
        var low = s_linearLut[index];
        return low + ((s_linearLut[index + 1] - low) * (position - index));
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

    private static float[] BuildLut(bool encode)
    {
        var table = new float[LutSize + 1];
        for (var i = 0; i <= LutSize; i++)
        {
            var value = i / (float)LutSize;
            table[i] = encode
                ? (value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f)
                : (value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f));
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
        public Plan(YuvImage source, int targetWidth, int targetHeight, int destinationStride, bool linearHalf)
        {
            Tiles = new YuvTile[source.Tiles.Count];
            for (var i = 0; i < Tiles.Length; i++) Tiles[i] = source.Tiles[i];
            CanvasWidth = source.CanvasWidth;
            CanvasHeight = source.CanvasHeight;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
            DestinationStride = destinationStride;
            LinearHalf = linearHalf;
            Chroma = source.Chroma;
            WideSamples = source.BitDepth > 8;
            IsPassthrough = targetWidth == CanvasWidth && targetHeight == CanvasHeight;
            P3ToSrgb = source.PrimariesCode == 12;

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

        public bool LinearHalf { get; }

        public bool IsPassthrough { get; }

        public HeifChromaSampling Chroma { get; }

        public bool WideSamples { get; }

        public int Columns { get; }

        public int Rows { get; }

        /// <summary>画布格位 → 瓦片下标;缺瓦片的格位为 -1。</summary>
        public int[] TileIndex { get; }

        public int TileWidth { get; }

        public int TileHeight { get; }

        public bool P3ToSrgb { get; }

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

        /// <summary>Display P3 → sRGB 的线性基色变换矩阵(行优先)。</summary>
        public float[] P3ToSrgbMatrix { get; } = new[]
        {
            1.2249401f, -0.2249404f, 0.0000000f,
            -0.0420569f, 1.0420569f, 0.0000000f,
            -0.0196376f, -0.0786361f, 1.0982735f,
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
