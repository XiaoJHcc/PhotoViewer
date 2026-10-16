using System;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 把一张图像渲染成 RGB 直方图图像（256 级 / 通道，叠加式填充曲线）。纯算法、不改原文件、不入库。
/// 分析栏的"直方图"瓦片用它：从当前主图（原片或增强图）现算 → 喂给 DiagnosticTile 展示，随增强切换实时重算。
/// </summary>
public static class HistogramRenderer
{
    /// <summary>
    /// 计算 source 的 R/G/B 三通道直方图并渲染为 outW×outH 的 BGRA 图像（透明底 + 三通道叠加填充）。
    /// </summary>
    /// <param name="source">主图图像（原片或增强图，本方法只读不释放）。</param>
    /// <param name="outW">输出图像宽（直方图 256 级横向铺满）。</param>
    /// <param name="outH">输出图像高（纵向为归一化频数）。</param>
    /// <returns>渲染好的直方图图像（调用方负责 Dispose）。</returns>
    public static DecodedImage Render(DecodedImage source, int outW, int outH)
    {
        var hist = ComputeHistogram(source, out long _);
        return RenderImage(hist, outW, outH);
    }

    /// <summary>
    /// 统计 source 的 R/G/B 256 级直方图（含像素总数）。
    /// </summary>
    private static int[][] ComputeHistogram(DecodedImage source, out long pixelCount)
    {
        int w = source.Width, h = source.Height;
        int bpp = DecodedImage.BytesPerPixel(source.Encoding);

        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];

        if (source.Encoding == PixelEncoding.RgbaF16)
        {
            // 线性半精度表面（macOS 原生主图）按线性→sRGB 编码后量化，与 8 位 sRGB 口径可比。
            var half = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(source.Pixels.AsSpan());
            int strideHalf = source.Stride / 2;
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * strideHalf;
                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x * 4;
                    rHist[EncodeSrgb(half[i])]++;
                    gHist[EncodeSrgb(half[i + 1])]++;
                    bHist[EncodeSrgb(half[i + 2])]++;
                }
            }
            pixelCount = (long)w * h;
            return new[] { rHist, gHist, bHist };
        }

        // Bgra32: R 在字节 2;Rgb24: R 在字节 0
        bool isBgr = source.Encoding == PixelEncoding.Bgra32;
        int rIdx = isBgr ? 2 : 0;
        int bIdx = isBgr ? 0 : 2;

        var pixels = source.Pixels;
        int stride = source.Stride;

        for (int y = 0; y < h; y++)
        {
            long rowStart = (long)y * stride;
            long rowEnd = rowStart + (long)w * bpp;
            for (long i = rowStart; i + bpp <= rowEnd; i += bpp)
            {
                rHist[pixels[i + rIdx]]++;
                gHist[pixels[i + 1]]++;
                bHist[pixels[i + bIdx]]++;
            }
        }

        pixelCount = (long)w * h;
        return new[] { rHist, gHist, bHist };
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
