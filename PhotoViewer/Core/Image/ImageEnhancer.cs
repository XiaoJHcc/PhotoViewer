using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 主图"增强预览"的确定性图像处理（v0：全局亮度直方图均衡）。
/// 输入原片解码位图、输出增强位图；纯算法、可复现、不改原文件、不入库。
/// 这是 plan-3-1 §1.2 产品化落地的算法沙盒：先粗暴拉平直方图肉眼评估，后续逐步迭代到去雾 / CLAHE。
/// 铁约束：作为模型输入的最终算法须非破坏性，本 v0 仅供产品目视，不代表入模制式。
/// </summary>
public static class ImageEnhancer
{
    /// <summary>
    /// 对源位图做全局亮度直方图均衡，返回新的增强位图（调用方负责 Dispose）。
    /// 仅按 newY/oldY 等比缩放各通道、保留色相；支持 32 位 BGRA/RGBA 与 24 位 BGR/RGB。
    /// </summary>
    /// <param name="source">原片解码位图（由 BitmapLoader 缓存所有，本方法只读不释放）。</param>
    /// <returns>增强后的可写位图。</returns>
    public static WriteableBitmap Enhance(Bitmap source)
    {
        var size = source.PixelSize;
        int w = size.Width, h = size.Height;
        var format = source.Format ?? PixelFormats.Bgra8888;
        int bpp = (format == PixelFormats.Rgb24 || format == PixelFormats.Bgr24) ? 3 : 4;

        // R/B 通道在像素内的字节下标（G 恒为 +1）：BGRA/BGR → B,_,R；RGBA/RGB → R,_,B。
        // 与 BitmapLoader.ConvertToDesiredFormat 的跨平台字节序约定一致。
        bool isBgr = format == PixelFormats.Bgra8888 || format == PixelFormats.Bgr24;
        int rIdx = isBgr ? 2 : 0;
        int bIdx = isBgr ? 0 : 2;

        int stride = w * bpp;
        int byteCount = stride * h;
        var pixels = new byte[byteCount];

        // 拷贝源像素到自有缓冲：此后与源位图解耦，即便源被 LRU 淘汰释放也不影响后续运算。
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), byteCount, stride);
        }
        finally
        {
            handle.Free();
        }

        EqualizeLuminance(pixels, byteCount, bpp, rIdx, bIdx);

        var result = new WriteableBitmap(size, source.Dpi, format, source.AlphaFormat);
        using (var fb = result.Lock())
        {
            // 多数情况下目标行无 padding（RowBytes==stride）可整段拷；否则逐行拷贝。
            if (fb.RowBytes == stride)
            {
                Marshal.Copy(pixels, 0, fb.Address, byteCount);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
            }
        }
        return result;
    }

    /// <summary>
    /// 在像素缓冲上原地做全局亮度直方图均衡：统计亮度直方图 → 累积分布映射 LUT → 按 newY/oldY 等比缩放各通道。
    /// </summary>
    /// <param name="pixels">紧凑排列的像素缓冲（stride = 宽 × bpp）。</param>
    /// <param name="byteCount">缓冲有效字节数。</param>
    /// <param name="bpp">每像素字节数（3 或 4）。</param>
    /// <param name="rIdx">R 通道在像素内的字节下标。</param>
    /// <param name="bIdx">B 通道在像素内的字节下标。</param>
    private static void EqualizeLuminance(byte[] pixels, int byteCount, int bpp, int rIdx, int bIdx)
    {
        // 1) 亮度直方图（BT.601：0.299R + 0.587G + 0.114B，定点近似）
        var hist = new int[256];
        for (int i = 0; i + bpp <= byteCount; i += bpp)
        {
            int y = (pixels[i + rIdx] * 77 + pixels[i + 1] * 150 + pixels[i + bIdx] * 29) >> 8;
            hist[y]++;
        }

        // 2) 累积分布 → 256 级映射 LUT
        long total = 0;
        for (int k = 0; k < 256; k++) total += hist[k];
        if (total == 0) return;

        var lut = new int[256];
        long cdf = 0, cdfMin = 0;
        bool minFound = false;
        for (int k = 0; k < 256; k++)
        {
            cdf += hist[k];
            if (!minFound && hist[k] > 0) { cdfMin = cdf; minFound = true; }
            long denom = total - cdfMin;
            int mapped = denom <= 0 ? k : (int)Math.Round((double)(cdf - cdfMin) / denom * 255.0);
            lut[k] = mapped < 0 ? 0 : mapped > 255 ? 255 : mapped;
        }

        // 3) 应用：按 newY/oldY 等比缩放，保留色相；oldY==0 直接置灰
        for (int i = 0; i + bpp <= byteCount; i += bpp)
        {
            int r = pixels[i + rIdx];
            int g = pixels[i + 1];
            int b = pixels[i + bIdx];
            int oldY = (r * 77 + g * 150 + b * 29) >> 8;
            int newY = lut[oldY];
            if (oldY <= 0)
            {
                pixels[i + rIdx] = (byte)newY;
                pixels[i + 1] = (byte)newY;
                pixels[i + bIdx] = (byte)newY;
            }
            else
            {
                pixels[i + rIdx] = ScaleClamp(r, newY, oldY);
                pixels[i + 1] = ScaleClamp(g, newY, oldY);
                pixels[i + bIdx] = ScaleClamp(b, newY, oldY);
            }
            // alpha 通道（若有）保持不变
        }
    }

    /// <summary>
    /// 将通道值按 newY/oldY 等比缩放并钳制到 [0,255]。
    /// </summary>
    private static byte ScaleClamp(int channel, int newY, int oldY)
    {
        int v = (int)((long)channel * newY / oldY);
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
