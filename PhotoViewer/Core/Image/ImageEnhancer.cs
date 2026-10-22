using System;
using System.Threading.Tasks;

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

    /// <summary>直方图分区数：每区独立统计后合并，合并结果与单线程逐像素统计完全一致（整数加法）。</summary>
    private const int HistogramPartitions = 8;

    /// <summary>
    /// 对源图像做全局限对比直方图均衡（CLHE），返回新的增强图像（调用方负责 Dispose）。
    /// 按 ch' = Y' + s·(ch − Y) 保色度重建（恒定色度、色相不变）。
    /// 源图像不被修改；像素先按行拷贝到自有租约缓冲，再就地处理。
    /// </summary>
    /// <param name="source">原片解码图像（由 BitmapLoader 缓存所有，本方法只读不释放）。</param>
    /// <returns>增强后的图像。</returns>
    public static DecodedImage Enhance(DecodedImage source)
    {
        ValidateEncoding(source);
        int bpp = DecodedImage.BytesPerPixel(source.Encoding);
        int stride = source.Width * bpp;
        var pixels = DecodedImage.RentPixels(checked(stride * source.Height));
        try
        {
            // 按行拷贝（考虑源 stride 的对齐填充）：此后与源图像解耦，即便源被 LRU 淘汰释放也不影响后续运算。
            for (int y = 0; y < source.Height; y++)
                Array.Copy(source.Pixels, (long)y * source.Stride, pixels, (long)y * stride, stride);
            EqualizeLuminance(pixels, source.Width, source.Height, stride, source.Encoding);
            return DecodedImage.CreatePooled(pixels, source.Width, source.Height, stride, source.Encoding,
                source.ColorSpace, source.SourceWidth, source.SourceHeight, source.Orientation);
        }
        catch
        {
            ImageBufferPool.Return(pixels);
            throw;
        }
    }

    /// <summary>
    /// 在调用方独占的 8 位图像上**就地**做同一 CLHE（输出与 <see cref="Enhance"/> 逐字节一致，只是省掉一次整图拷贝）。
    /// 只在调用方已持有独立缓冲时使用（例如展示会话刚转换出来的 sRGB8 副本），共享缓存里的原图绝不可走这条路径。
    /// </summary>
    /// <param name="image">调用方独占且可写的 8 位图像（允许非紧凑 stride）。</param>
    public static void EnhanceInPlace(DecodedImage image)
    {
        ValidateEncoding(image);
        EqualizeLuminance(image.Pixels, image.Width, image.Height, image.Stride, image.Encoding);
    }

    /// <summary>算法定义在 8 位单字节通道上：F16 线性表面必须先经调用方转 sRGB8，误传时显式抛出而非按字节误算。</summary>
    /// <param name="source">待处理的图像。</param>
    private static void ValidateEncoding(DecodedImage source)
    {
        if (source.Encoding is not (PixelEncoding.Bgra32 or PixelEncoding.Rgb24 or PixelEncoding.Rgba8))
            throw new ArgumentOutOfRangeException(nameof(source), $"增强预览只接受 8 位编码，实际 {source.Encoding}");
    }

    /// <summary>
    /// 在像素缓冲上原地做全局限对比直方图均衡（CLHE）：亮度直方图 → 限对比裁剪回填 → 累积分布 LUT → 保色度重建 ch' = Y' + s·(ch − Y)。
    /// 裁剪封住 LUT 斜率：极窄高尖峰不被强行摊平（防断层）、大面积同色尖峰不独吞输出范围（防主体挤压 / 亮度漂移）。
    /// 两个逐像素阶段都按行并行；直方图分区统计后整数合并，结果与串行完全一致。
    /// </summary>
    /// <param name="pixels">像素缓冲（允许行间有对齐填充）。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="encoding">8 位像素编码（决定通道顺序与每像素字节数）。</param>
    private static void EqualizeLuminance(byte[] pixels, int width, int height, int stride, PixelEncoding encoding)
    {
        int bpp = DecodedImage.BytesPerPixel(encoding);
        bool isBgr = encoding == PixelEncoding.Bgra32;
        int rIdx = isBgr ? 2 : 0;
        int bIdx = isBgr ? 0 : 2;

        // 1) 亮度直方图（BT.601：0.299R + 0.587G + 0.114B，定点近似）；分区统计后合并。
        var hist = new int[256];
        int partitions = Math.Min(HistogramPartitions, Math.Max(1, height));
        Parallel.For(0, partitions, () => new int[256],
            (partition, _, local) =>
            {
                int start = (int)((long)height * partition / partitions);
                int end = (int)((long)height * (partition + 1) / partitions);
                for (int y = start; y < end; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x * bpp;
                        int luminance = (pixels[i + rIdx] * 77 + pixels[i + 1] * 150 + pixels[i + bIdx] * 29) >> 8;
                        local[luminance]++;
                    }
                }
                return local;
            },
            local => { lock (hist) for (int k = 0; k < 256; k++) hist[k] += local[k]; });

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

        // 4) 保色度重建：ch' = Y' + s·(ch − Y)。逐行并行，逐像素逻辑见 <see cref="ReconstructRow"/>。
        Parallel.For(0, height, y => ReconstructRow(pixels, width, stride, bpp, rIdx, bIdx, lut, y));
    }

    /// <summary>保色度重建单行（并行任务按行调用，逐行无共享写）。</summary>
    private static void ReconstructRow(byte[] pixels, int width, int stride, int bpp, int rIdx, int bIdx, int[] lut, int y)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            int i = row + x * bpp;
            int r = pixels[i + rIdx];
            int g = pixels[i + 1];
            int b = pixels[i + bIdx];
            int oldY = (r * 77 + g * 150 + b * 29) >> 8;
            int newY = lut[oldY];
            int cr = r - oldY, cg = g - oldY, cb = b - oldY;
            double s = SaturationScale;
            s = LimitScale(newY, cr, s);
            s = LimitScale(newY, cg, s);
            s = LimitScale(newY, cb, s);
            pixels[i + rIdx] = ReconClamp(newY, cr, s);
            pixels[i + 1] = ReconClamp(newY, cg, s);
            pixels[i + bIdx] = ReconClamp(newY, cb, s);
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
