using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 主图"增强预览"的确定性"信息归一化"图像处理：全局限对比直方图均衡（CLHE）+ 保色度重建（YCbCr 恒定色度）。
/// 输入原片解码位图、输出增强位图；纯算法、可复现、不改原文件、不入库。
/// 定位：把有效信息分布得更均匀、对比更清晰但低失真，为下一步模型训练 / 人眼判断提供"后期潜力"参考（真正后期由人操作）。
/// 设计约束为可学习性的 5 条规格——全局、单调、平滑(斜率有界)、少量固定参数、色彩稳定——故不用任何局部自适应 / 分段 / 逐图自适应强度。
/// 铁约束：作为模型输入的最终算法须非破坏性，本算法仅供产品目视，不代表入模制式。
/// </summary>
public static class ImageEnhancer
{
    /// <summary>裁剪上限系数：限对比裁剪把每个 bin 封顶到 ClipFactor × 平均 bin 高，封住 LUT 斜率 → 防断层、防尖峰独吞输出范围。
    /// public：数据集提取工具（Training/DatasetBuilder）据此拼确定性的增强 model_id 后缀，保证参数与后缀永不漂移。</summary>
    public const double ClipFactor = 2.0;

    /// <summary>色度缩放系数：保色度重建 ch' = Y' + s·(ch − Y) 中的 s。
    /// 1.0 = 保留**绝对**彩度（YCbCr 恒定色度）——暗部彩度不随亮度塌缩，是"饱和度不偏移"的中性定义；>1 更艳、<1 回退发灰。
    /// 替代旧的 (newY+ε)/(oldY+ε) 等比增益：加性重建无除法、深阴影不放大彩噪，故 ε 一并取消。
    /// public：同 ClipFactor，随增强 model_id 契约一并冻结记录。</summary>
    public const double SaturationScale = 1.0;
    /// <summary>
    /// 对源位图做全局限对比直方图均衡（CLHE），返回新的增强位图（调用方负责 Dispose）。
    /// 按 ch' = Y' + s·(ch − Y) 保色度重建（恒定色度、色相不变）；支持 32 位 BGRA/RGBA 与 24 位 BGR/RGB。
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
    /// 在像素缓冲上原地做全局限对比直方图均衡（CLHE）：亮度直方图 → 限对比裁剪回填 → 累积分布 LUT → 保色度重建 ch' = Y' + s·(ch − Y)。
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

        // 4) 保色度重建：ch' = Y' + s·(ch − Y)。
        //    此式对亮度权重求和恒得 Σw·ch' = Y'（彩度向量在亮度方向的投影为 0），故结果亮度精确命中 LUT[Y] →
        //    亮暗关系严格单调、色相不变（彩度向量只被标量缩放）。s = SaturationScale 保绝对彩度，暗部不再随亮度塌缩发灰。
        //    极端处（Y' 很小 / 很大）绝对彩度会越界，逐像素把 s 收到"恰好不越界"的 sMax → 物理被迫的最小去饱和，无硬钳位、无色相偏移。
        for (int i = 0; i + bpp <= byteCount; i += bpp)
        {
            int r = pixels[i + rIdx];
            int g = pixels[i + 1];
            int b = pixels[i + bIdx];
            int oldY = (r * 77 + g * 150 + b * 29) >> 8;
            int newY = lut[oldY];

            int cr = r - oldY, cg = g - oldY, cb = b - oldY;
            // 求保持三通道均落 [0,255] 的最大 s（Y' ∈ [0,255] 故各上界 ≥ 0，s 必 ≥ 0）
            double s = SaturationScale;
            s = LimitScale(newY, cr, s);
            s = LimitScale(newY, cg, s);
            s = LimitScale(newY, cb, s);

            pixels[i + rIdx] = ReconClamp(newY, cr, s);
            pixels[i + 1] = ReconClamp(newY, cg, s);
            pixels[i + bIdx] = ReconClamp(newY, cb, s);
            // alpha 通道（若有）保持不变
        }
    }

    /// <summary>
    /// 收紧色度缩放系数 s，使单通道重建值 newY + s·chroma 不越出 [0,255]。
    /// chroma>0 受上界 (255−newY)/chroma 约束、chroma&lt;0 受 newY/|chroma| 约束、chroma==0 无约束。
    /// </summary>
    private static double LimitScale(int newY, int chroma, double s)
    {
        if (chroma > 0)
        {
            double lim = (255.0 - newY) / chroma;
            if (lim < s) s = lim;
        }
        else if (chroma < 0)
        {
            double lim = (double)newY / -chroma;
            if (lim < s) s = lim;
        }
        return s;
    }

    /// <summary>
    /// 保色度重建单通道：newY + s·chroma，四舍五入后钳制到 [0,255]（钳制仅为吸收取整边界误差）。
    /// </summary>
    private static byte ReconClamp(int newY, int chroma, double s)
    {
        int v = (int)(newY + s * chroma + 0.5);
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
