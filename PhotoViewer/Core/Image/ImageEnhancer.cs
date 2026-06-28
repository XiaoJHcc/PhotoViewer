using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 主图"增强预览"的确定性"信息归一化"图像处理：全局限对比直方图均衡（CLHE）+ 暗部增益护栏。
/// 输入原片解码位图、输出增强位图；纯算法、可复现、不改原文件、不入库。
/// 定位：把有效信息分布得更均匀、对比更清晰但低失真，为下一步模型训练 / 人眼判断提供"后期潜力"参考（真正后期由人操作）。
/// 设计约束为可学习性的 5 条规格——全局、单调、平滑(斜率有界)、少量固定参数、色彩稳定——故不用任何局部自适应 / 分段 / 逐图自适应强度。
/// 铁约束：作为模型输入的最终算法须非破坏性，本算法仅供产品目视，不代表入模制式。
/// </summary>
public static class ImageEnhancer
{
    /// <summary>裁剪上限系数：限对比裁剪把每个 bin 封顶到 ClipFactor × 平均 bin 高，封住 LUT 斜率 → 防断层、防尖峰独吞输出范围。</summary>
    private const double ClipFactor = 2.0;

    /// <summary>暗部增益护栏 ε：增益取 (newY+ε)/(oldY+ε)，让深阴影增益平滑有界，替代纯 newY/oldY 在近黑处 ×十几倍放大色噪。</summary>
    private const int ChromaGainEpsilon = 6;
    /// <summary>
    /// 对源位图做全局限对比直方图均衡（CLHE），返回新的增强位图（调用方负责 Dispose）。
    /// 按 (newY+ε)/(oldY+ε) 等比缩放各通道、保留色相；支持 32 位 BGRA/RGBA 与 24 位 BGR/RGB。
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
    /// 在像素缓冲上原地做全局限对比直方图均衡（CLHE）：亮度直方图 → 限对比裁剪回填 → 累积分布 LUT → 按 (newY+ε)/(oldY+ε) 等比缩放各通道。
    /// 裁剪封住 LUT 斜率：极窄高尖峰不被强行摊平（防断层）、大面积同色尖峰不独吞输出范围（防主体挤压 / 亮度漂移）。
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

        long total = 0;
        for (int k = 0; k < 256; k++) total += hist[k];
        if (total == 0) return;

        // 2) 限对比裁剪：每个 bin 封顶到 ClipFactor × 平均 bin 高，削下的质量均匀回填到全部 256 bin（单遍近似）。
        //    封顶给 LUT 斜率设上界 → 消断层；回填给空区段垫平底 → 无像素处曲线更接近线性，进一步压制断层。
        long clip = (long)(ClipFactor * total / 256.0);
        if (clip < 1) clip = 1;
        long excess = 0;
        for (int k = 0; k < 256; k++)
            if (hist[k] > clip) { excess += hist[k] - clip; hist[k] = (int)clip; }
        int refill = (int)(excess / 256);
        if (refill > 0)
            for (int k = 0; k < 256; k++) hist[k] += refill;
        // 回填后总数变化，重算用于归一化
        long clippedTotal = total - excess + (long)refill * 256;

        // 3) 累积分布 → 256 级映射 LUT（cdfMin 归一化，最暗有效级映到 0）
        var lut = new int[256];
        long cdf = 0, cdfMin = 0;
        bool minFound = false;
        for (int k = 0; k < 256; k++)
        {
            cdf += hist[k];
            if (!minFound && hist[k] > 0) { cdfMin = cdf; minFound = true; }
            long denom = clippedTotal - cdfMin;
            int mapped = denom <= 0 ? k : (int)Math.Round((double)(cdf - cdfMin) / denom * 255.0);
            lut[k] = mapped < 0 ? 0 : mapped > 255 ? 255 : mapped;
        }

        // 4) 应用：按 (newY+ε)/(oldY+ε) 等比缩放保色相；ε 让深阴影增益平滑有界（防彩噪）。oldY==0 纯黑无色可乘，走无彩提亮。
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
                // gain = (newY+ε)/(oldY+ε)：oldY 大时 ≈ newY/oldY 忠实；oldY 小时增益被 ε 软封顶。
                double gain = (newY + ChromaGainEpsilon) / (double)(oldY + ChromaGainEpsilon);
                pixels[i + rIdx] = ScaleClamp(r, gain);
                pixels[i + 1] = ScaleClamp(g, gain);
                pixels[i + bIdx] = ScaleClamp(b, gain);
            }
            // alpha 通道（若有）保持不变
        }
    }

    /// <summary>
    /// 将通道值乘以增益并钳制到 [0,255]。
    /// </summary>
    private static byte ScaleClamp(int channel, double gain)
    {
        int v = (int)(channel * gain + 0.5);
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
