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
    /// 生成一张带 EXIF 方向码的"四角异色"测试 JPEG：像素恒为横向 1200×800（红/蓝/白/灰四角），
    /// 由 EXIF 方向码决定显示朝向。用于证明增强预览不会丢失/改写方向（方向丢失时画面会转 90°）。
    /// 只写自有沙盒/输出目录。
    /// </summary>
    /// <param name="path">目标 JPEG 路径。</param>
    /// <param name="orientation">EXIF 方向码 1–8。</param>
    /// <param name="width">容器朝向像素宽（横向基准）。</param>
    /// <param name="height">容器朝向像素高。</param>
    public static void CreateOrientationJpeg(string path, int orientation, int width = 1200, int height = 800)
    {
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation));
        if (width < 4 || height < 4) throw new ArgumentOutOfRangeException(nameof(width));
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = bitmap.Pixels;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool right = x >= width / 2, bottom = y >= height / 2;
                SKColor color = (right, bottom) switch
                {
                    (false, false) => new SKColor(220, 24, 24),   // 左上红
                    (true, false) => new SKColor(24, 32, 220),    // 右上蓝
                    (false, true) => new SKColor(238, 238, 238),  // 左下白
                    _ => new SKColor(96, 96, 96),                 // 右下灰
                };
                pixels[y * width + x] = color;
            }
        }
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 94)
            ?? throw new InvalidOperationException("方向测试素材编码失败");
        using var stream = new MemoryStream();
        data.SaveTo(stream);
        byte[] jpeg = stream.ToArray();
        File.WriteAllBytes(path, WithExifOrientation(jpeg, orientation));
    }

    /// <summary>
    /// 在 JPEG 的 SOI 之后插入一段最小 EXIF APP1（IFD0 只有 Orientation 一项），保留其余字节不变。
    /// 不依赖第三方写库；方向码由解码器的标准 EXIF 读取路径消费。
    /// </summary>
    /// <param name="jpeg">原始 JPEG 字节。</param>
    /// <param name="orientation">EXIF 方向码 1–8。</param>
    /// <returns>插入 EXIF 后的 JPEG 字节。</returns>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("JPEG 头无效");
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,             // "II"、42、IFD0 偏移 8
            0x01, 0x00,                                                   // IFD0 条目数 1
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,               // Orientation、SHORT、1 个
            (byte)orientation, 0x00, 0x00, 0x00,                          // 值
            0x00, 0x00, 0x00, 0x00,                                       // 下一个 IFD 偏移 0
        ];
        int payload = 6 + tiff.Length;
        int segmentLength = payload + 2;
        using var output = new MemoryStream(jpeg.Length + segmentLength + 2);
        output.Write(jpeg, 0, 2);
        output.WriteByte(0xFF);
        output.WriteByte(0xE1);
        output.WriteByte((byte)(segmentLength >> 8));
        output.WriteByte((byte)segmentLength);
        output.Write("Exif\0\0"u8);
        output.Write(tiff, 0, tiff.Length);
        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    /// <summary>
    /// 四角（归一化 5%/95%）的颜色分类：R/G/B/白/灰。增强只改亮度与对比、不改色相与朝向，
    /// 因此同一张照片增强前后这四类的排列必须完全一致；方向丢失或被转置时排列会变。
    /// </summary>
    /// <param name="image">BGRA8 图像（例如导航图小帧）。</param>
    /// <returns>左上、右上、左下、右下的分类。</returns>
    public static string[] CornerSignatures(DecodedImage image)
    {
        if (image.Encoding != PixelEncoding.Bgra32) throw new InvalidOperationException($"角点分类需要 BGRA8：{image.Encoding}");
        (double U, double V)[] corners = [(.05, .05), (.95, .05), (.05, .95), (.95, .95)];
        var signatures = new string[corners.Length];
        for (int index = 0; index < corners.Length; index++)
        {
            int x = Math.Clamp((int)(image.Width * corners[index].U), 0, image.Width - 1);
            int y = Math.Clamp((int)(image.Height * corners[index].V), 0, image.Height - 1);
            int offset = y * image.Stride + x * 4;
            int blue = image.Pixels[offset], green = image.Pixels[offset + 1], red = image.Pixels[offset + 2];
            int maximum = Math.Max(red, Math.Max(green, blue)), minimum = Math.Min(red, Math.Min(green, blue));
            signatures[index] = maximum - minimum < 24 ? (maximum > 150 ? "white" : "gray")
                : red == maximum ? "red"
                : blue == maximum ? "blue"
                : "green";
        }
        return signatures;
    }

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
