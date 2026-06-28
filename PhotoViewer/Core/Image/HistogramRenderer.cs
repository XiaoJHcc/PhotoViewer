using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 把一张位图渲染成 RGB 直方图位图（256 级 / 通道，叠加式填充曲线）。纯算法、不改原文件、不入库。
/// 分析栏的"直方图"瓦片用它：从当前主图（原片或增强图）现算 → 喂给 DiagnosticTile 展示，随增强切换实时重算。
/// </summary>
public static class HistogramRenderer
{
    /// <summary>
    /// 计算 source 的 R/G/B 三通道直方图并渲染为 outW×outH 的 BGRA 位图（透明底 + 三通道叠加填充）。
    /// </summary>
    /// <param name="source">主图位图（原片或增强图，本方法只读不释放）。</param>
    /// <param name="outW">输出位图宽（直方图 256 级横向铺满）。</param>
    /// <param name="outH">输出位图高（纵向为归一化频数）。</param>
    /// <returns>渲染好的直方图位图（调用方负责 Dispose）。</returns>
    public static WriteableBitmap Render(Bitmap source, int outW, int outH)
    {
        var hist = ComputeHistogram(source, out long _);
        return RenderBitmap(hist, outW, outH);
    }

    /// <summary>
    /// 统计 source 的 R/G/B 256 级直方图（含像素总数）。字节序沿用 BitmapLoader 约定。
    /// </summary>
    private static int[][] ComputeHistogram(Bitmap source, out long pixelCount)
    {
        var size = source.PixelSize;
        int w = size.Width, h = size.Height;
        var format = source.Format ?? PixelFormats.Bgra8888;
        int bpp = (format == PixelFormats.Rgb24 || format == PixelFormats.Bgr24) ? 3 : 4;
        bool isBgr = format == PixelFormats.Bgra8888 || format == PixelFormats.Bgr24;
        int rIdx = isBgr ? 2 : 0;
        int bIdx = isBgr ? 0 : 2;

        int stride = w * bpp;
        int byteCount = stride * h;
        var pixels = new byte[byteCount];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), byteCount, stride);
        }
        finally
        {
            handle.Free();
        }

        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];
        for (int i = 0; i + bpp <= byteCount; i += bpp)
        {
            rHist[pixels[i + rIdx]]++;
            gHist[pixels[i + 1]]++;
            bHist[pixels[i + bIdx]]++;
        }

        pixelCount = (long)w * h;
        return new[] { rHist, gHist, bHist };
    }

    /// <summary>
    /// 把三通道直方图渲染成 BGRA 位图：透明底 + 每通道按全局峰值归一化的填充曲线，叠加处取较亮值（近似屏幕混合）。
    /// </summary>
    private static WriteableBitmap RenderBitmap(int[][] hist, int outW, int outH)
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

        var wb = new WriteableBitmap(new PixelSize(outW, outH), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            if (fb.RowBytes == stride)
                Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
            else
                for (int y = 0; y < outH; y++)
                    Marshal.Copy(buffer, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        return wb;
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
