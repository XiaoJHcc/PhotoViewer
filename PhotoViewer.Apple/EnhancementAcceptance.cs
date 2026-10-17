using SkiaSharp;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Apple;

/// <summary>
/// 增强预览的共用验收工具：生成确定性的低对比度 JPEG 素材，并比较两次 GPU 帧读回的像素差异。
/// 只依赖 SkiaSharp / CoreGraphics 无关的共享类型，Mac 与 iPad 两个原生壳共用同一口径。
/// </summary>
public static class EnhancementAcceptance
{
    /// <summary>低对比度素材的宽（像素），足够大以覆盖代理与 Full 两种展示档。</summary>
    public const int FixtureWidth = 2400;
    /// <summary>低对比度素材的高（像素）。</summary>
    public const int FixtureHeight = 1600;

    /// <summary>
    /// 生成一张低对比度 JPEG（窄亮度区间的横向渐变 + 两枚平缓色块），
    /// 用于证明增强预览确实改变了上屏像素：直方图均衡在窄区间上必然产生明显拉伸。
    /// </summary>
    /// <param name="path">目标 JPEG 路径（写自有沙盒/输出目录，不碰用户照片）。</param>
    public static void CreateLowContrastJpeg(string path)
    {
        using var bitmap = new SKBitmap(FixtureWidth, FixtureHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = bitmap.Pixels;
        for (int y = 0; y < FixtureHeight; y++)
        {
            int ripple = (int)Math.Round(10 * Math.Sin(y / 180.0));
            for (int x = 0; x < FixtureWidth; x++)
            {
                int baseValue = 90 + 60 * x / FixtureWidth;
                byte red = Channel(baseValue + ripple);
                byte green = Channel(baseValue + ripple + 8);
                byte blue = Channel(baseValue + ripple + 16);
                double dx = x - 700, dy = y - 700;
                if (dx * dx + dy * dy < 400 * 400) (red, green, blue) = (Channel(baseValue + 30), Channel(baseValue + 38), Channel(baseValue + 46));
                if (x is > 1400 and < 2100 && y is > 500 and < 1300) (red, green, blue) = (Channel(baseValue + 12), Channel(baseValue + 20), Channel(baseValue + 26));
                pixels[y * FixtureWidth + x] = new SKColor(red, green, blue);
            }
        }
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92)
            ?? throw new InvalidOperationException("低对比度增强素材编码失败");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>把 0–255 附近的亮度收进字节范围。</summary>
    /// <param name="value">目标亮度。</param>
    /// <returns>钳制后的字节。</returns>
    private static byte Channel(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// 比较两帧 BGRA8 像素的可见差异：按网格抽样计算每通道平均绝对差与最大绝对差。
    /// 两帧尺寸不一致时显式抛出（调用方在切换照片/重建视口时就会暴露）。
    /// </summary>
    /// <param name="before">增强前的帧（BGRA8）。</param>
    /// <param name="after">增强后的帧（BGRA8）。</param>
    /// <returns>平均绝对差（0–255）与最大绝对差。</returns>
    public static (double Mean, int Max) MeasureDifference(DecodedImage before, DecodedImage after)
    {
        if (before.Encoding != PixelEncoding.Bgra32 || after.Encoding != PixelEncoding.Bgra32)
            throw new InvalidOperationException($"帧比较需要 BGRA8：{before.Encoding}/{after.Encoding}");
        if (before.Width != after.Width || before.Height != after.Height)
            throw new InvalidOperationException($"帧尺寸不一致：{before.Width}×{before.Height} vs {after.Width}×{after.Height}");
        int stepX = Math.Max(1, before.Width / 200);
        int stepY = Math.Max(1, before.Height / 200);
        long total = 0;
        int maximum = 0;
        long samples = 0;
        var first = before.Pixels;
        var second = after.Pixels;
        for (int y = 0; y < before.Height; y += stepY)
        {
            int firstRow = y * before.Stride, secondRow = y * after.Stride;
            for (int x = 0; x < before.Width; x += stepX)
            {
                int firstOffset = firstRow + x * 4, secondOffset = secondRow + x * 4;
                int difference = Math.Abs(first[firstOffset] - second[secondOffset]);
                difference = Math.Max(difference, Math.Abs(first[firstOffset + 1] - second[secondOffset + 1]));
                difference = Math.Max(difference, Math.Abs(first[firstOffset + 2] - second[secondOffset + 2]));
                total += difference;
                if (difference > maximum) maximum = difference;
                samples++;
            }
        }
        return (samples == 0 ? 0 : (double)total / samples, maximum);
    }
}
