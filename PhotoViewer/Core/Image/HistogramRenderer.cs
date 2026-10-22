using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 把一张图像渲染成 RGB 直方图图像（256 级 / 通道，叠加式填充曲线）。纯算法、不改原文件、不入库。
/// 分析栏的"直方图"瓦片用它：从当前主图（原片或增强图）现算 → 喂给 DiagnosticTile 展示，随增强切换实时重算。
/// 统计阶段按行分区并行，每分区独立计数后整数合并（结果与串行完全一致）；F16 档走 Half 位模式查表，
/// 免去逐像素的幂/取整运算，输出与逐值公式逐字节相同。
/// </summary>
public static class HistogramRenderer
{
    /// <summary>每分区处理多少行检查一次取消，兼顾响应速度与检查开销。</summary>
    private const int CancelCheckRows = 64;

    /// <summary>Half 全部 65536 个位模式 → sRGB 8 位级；首次使用时构建，之后只读共享。</summary>
    private static readonly Lazy<byte[]> SrgbLut = new(CreateSrgbLut, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 计算 source 的 R/G/B 三通道直方图并渲染为 outW×outH 的 BGRA 图像（透明底 + 三通道叠加填充）。
    /// </summary>
    /// <param name="source">主图图像（原片或增强图，本方法只读不释放）。</param>
    /// <param name="outW">输出图像宽（直方图 256 级横向铺满）。</param>
    /// <param name="outH">输出图像高（纵向为归一化频数）。</param>
    /// <param name="cancellationToken">取消令牌；分区循环定期检查，取消时抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>渲染好的直方图图像（调用方负责 Dispose）。</returns>
    public static DecodedImage Render(DecodedImage source, int outW, int outH, CancellationToken cancellationToken = default)
    {
        var hist = ComputeHistogram(source, cancellationToken, out long _);
        return RenderImage(hist, outW, outH);
    }

    /// <summary>
    /// 统计 source 的 R/G/B 256 级直方图（含像素总数）。行区间按处理器数均分，
    /// 每分区独立 int[256]×3 计数，最后整数合并——合并结果与串行单遍一致。
    /// </summary>
    private static int[][] ComputeHistogram(DecodedImage source, CancellationToken cancellationToken, out long pixelCount)
    {
        int w = source.Width, h = source.Height;
        int bpp = DecodedImage.BytesPerPixel(source.Encoding);
        int partitions = Math.Max(1, Environment.ProcessorCount);
        var rParts = new int[partitions][];
        var gParts = new int[partitions][];
        var bParts = new int[partitions][];
        var options = new ParallelOptions { CancellationToken = cancellationToken };

        if (source.Encoding == PixelEncoding.RgbaF16)
        {
            // 线性半精度表面（原生主图 F16 档）按线性→sRGB 编码后量化，与 8 位 sRGB 口径可比。
            // 查表值与逐像素 EncodeSrgb(Half) 完全相同：下标即 Half 的 16 位位模式。
            var lut = SrgbLut.Value;
            var pixelBytes = source.Pixels;
            int strideHalf = source.Stride / 2;
            Parallel.For(0, partitions, options, p =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var half = MemoryMarshal.Cast<byte, ushort>(pixelBytes.AsSpan());
                (int start, int end) = RowRange(h, partitions, p);
                var r = new int[256];
                var g = new int[256];
                var b = new int[256];
                int rows = 0;
                for (int y = start; y < end; y++)
                {
                    if ((++rows & (CancelCheckRows - 1)) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int rowStart = y * strideHalf;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowStart + x * 4;
                        r[lut[half[i]]]++;
                        g[lut[half[i + 1]]]++;
                        b[lut[half[i + 2]]]++;
                    }
                }
                rParts[p] = r;
                gParts[p] = g;
                bParts[p] = b;
            });
            pixelCount = (long)w * h;
            return MergePartitions(rParts, gParts, bParts);
        }

        // 8 位展示编码直接按存储通道计数：Bgra32 = B,G,R,A；Rgb24/Rgba8(Display P3，sRGB 传递函数) 均为 R,G,B(,A)。
        // 8 位 P3 已是显示参考的编码值，与 F16 档"线性→sRGB 编码后量化"同一口径，故不再做色域换算。
        int rIdx = source.Encoding switch
        {
            PixelEncoding.Bgra32 => 2,
            PixelEncoding.Rgb24 or PixelEncoding.Rgba8 => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(source), $"不支持的直方图输入编码 {source.Encoding}"),
        };
        int bIdx = rIdx == 2 ? 0 : 2;

        var pixels = source.Pixels;
        int stride = source.Stride;

        Parallel.For(0, partitions, options, p =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int start, int end) = RowRange(h, partitions, p);
            var r = new int[256];
            var g = new int[256];
            var b = new int[256];
            int rows = 0;
            for (int y = start; y < end; y++)
            {
                if ((++rows & (CancelCheckRows - 1)) == 0) cancellationToken.ThrowIfCancellationRequested();
                long rowStart = (long)y * stride;
                long rowEnd = rowStart + (long)w * bpp;
                for (long i = rowStart; i + bpp <= rowEnd; i += bpp)
                {
                    r[pixels[i + rIdx]]++;
                    g[pixels[i + 1]]++;
                    b[pixels[i + bIdx]]++;
                }
            }
            rParts[p] = r;
            gParts[p] = g;
            bParts[p] = b;
        });

        pixelCount = (long)w * h;
        return MergePartitions(rParts, gParts, bParts);
    }

    /// <summary>均分行区间：第 p 个分区负责 [start, end) 行。</summary>
    private static (int Start, int End) RowRange(int height, int partitions, int partition)
        => ((int)((long)height * partition / partitions), (int)((long)height * (partition + 1) / partitions));

    /// <summary>把各分区的独立计数按通道整数相加。</summary>
    private static int[][] MergePartitions(int[][] rParts, int[][] gParts, int[][] bParts)
    {
        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];
        for (int p = 0; p < rParts.Length; p++)
        {
            var r = rParts[p];
            var g = gParts[p];
            var b = bParts[p];
            for (int i = 0; i < 256; i++)
            {
                rHist[i] += r[i];
                gHist[i] += g[i];
                bHist[i] += b[i];
            }
        }
        return new[] { rHist, gHist, bHist };
    }

    /// <summary>构建 Half 位模式 → sRGB 级查表；逐项调用与像素路径相同的编码公式。</summary>
    private static byte[] CreateSrgbLut()
    {
        var lut = new byte[1 << 16];
        for (int bits = 0; bits < lut.Length; bits++)
            lut[bits] = (byte)EncodeSrgb(BitConverter.UInt16BitsToHalf((ushort)bits));
        return lut;
    }

    /// <summary>把线性 scRGB 分量编码成 sRGB 传递函数下的 0..255 级(负值与超一按边界钳位)。</summary>
    private static int EncodeSrgb(Half linear)
    {
        float value = (float)linear;
        if (float.IsNaN(value) || value <= 0f) return 0;
        if (value >= 1f) return 255;
        float encoded = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        return Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    /// <summary>
    /// 把三通道直方图渲染成 BGRA 图像：透明底 + 每通道按全局峰值归一化的填充曲线，叠加处取较亮值（近似屏幕混合）。
    /// </summary>
    private static DecodedImage RenderImage(int[][] hist, int outW, int outH)
    {
        outW = Math.Max(16, outW);
        outH = Math.Max(16, outH);

        // 全局峰值用于纵向归一化（忽略纯黑/纯白溢出柱的极端值影响：取所有通道所有 bin 的最大）
        int peak = 1;
        foreach (var ch in hist)
            for (int b = 0; b < 256; b++)
                if (ch[b] > peak) peak = ch[b];

        int stride = outW * 4;
        // 透明底（new byte[] 已全零 = (0,0,0,0)）：DiagnosticTile 的 #222 背景透出来，曲线只在覆盖处置不透明。
        var buffer = new byte[stride * outH];

        // 每通道一条填充曲线，柔和原色：主分量钉死 255，其余两分量抬高做成粉彩色软化观感。
        // 通道字节序：缓冲为 BGRA，所以 B=ch0,G=ch1,R=ch2。
        DrawChannel(buffer, stride, outW, outH, hist[0], peak, channelB: 64, channelG: 64, channelR: 255); // R 珊瑚
        DrawChannel(buffer, stride, outW, outH, hist[1], peak, channelB: 64, channelG: 255, channelR: 64); // G 薄荷
        DrawChannel(buffer, stride, outW, outH, hist[2], peak, channelB: 255, channelG: 64, channelR: 32); // B 淡蓝

        return new DecodedImage(buffer, outW, outH, stride, PixelEncoding.Bgra32);
    }

    /// <summary>
    /// 在缓冲上为单个通道画归一化填充曲线（从底部到该 bin 高度），叠加处对每个颜色分量取较大值。
    /// </summary>
    private static void DrawChannel(byte[] buffer, int stride, int outW, int outH, int[] chHist, int peak,
        byte channelB, byte channelG, byte channelR)
    {
        for (int x = 0; x < outW; x++)
        {
            // 把 x 映射回 0..255 的 bin（横向铺满）
            int bin = (int)((long)x * 256 / outW);
            if (bin > 255) bin = 255;
            double norm = chHist[bin] / (double)peak;
            if (norm > 1) norm = 1;
            int barH = (int)(norm * (outH - 1));

            for (int y = outH - 1; y >= outH - barH; y--)
            {
                int idx = y * stride + x * 4;
                // 叠加取 max（近似屏幕混合，让重叠区偏白）
                if (channelB > buffer[idx]) buffer[idx] = channelB;
                if (channelG > buffer[idx + 1]) buffer[idx + 1] = channelG;
                if (channelR > buffer[idx + 2]) buffer[idx + 2] = channelR;
                buffer[idx + 3] = 0xff; // 覆盖处置不透明（纯原色 alpha=255，premul 与直存等价）
            }
        }
    }
}
