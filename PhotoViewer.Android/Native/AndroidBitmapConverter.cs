using System;
using System.Collections.Generic;
using Android.Graphics;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 共享 <see cref="DecodedImage"/> → Android <see cref="Bitmap"/> 的缩略图缓存（A3）：
/// 胶片栏卡片只拿 Thumb 档（Bgra32/Rgb24），按解码产物引用缓存，缓存被淘汰换新引用时自动重转。
/// 位图出口统一是**显式 sRGB**：广色域（Display P3/Adobe RGB）、线性与 ICC 源先经
/// <see cref="ColorEngine"/> 规范化，不让 P3 码值以「默认 sRGB」被 <see cref="Bitmap.SetPixels"/> 解读。
/// </summary>
internal static class AndroidBitmapConverter
{
    private const int MaximumEntries = 400;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (DecodedImage Source, Bitmap Bitmap)> Cache = new(StringComparer.Ordinal);

    /// <summary>取得缩略图位图；同一解码产物引用只转换一次。</summary>
    /// <param name="key">缓存键（文件 StorageId）。</param>
    /// <param name="image">共享缩略图（调用方持有租约，像素已是显示朝向）。</param>
    /// <returns>位图；编码不支持时 null。</returns>
    internal static Bitmap? FromDecodedImage(string key, DecodedImage image)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var entry) && ReferenceEquals(entry.Source, image)) return entry.Bitmap;
            // 旧位图可能仍被正在绘制的卡片引用，交给 GC，不做跨卡释放。
            Cache.Remove(key);
            var bitmap = Convert(image);
            if (bitmap == null) return null;
            if (Cache.Count >= MaximumEntries) ClearLocked();
            Cache[key] = (image, bitmap);
            return bitmap;
        }
    }

    /// <summary>不进入缓存的一次性转换（导航图等短期位图）；调用方负责释放。</summary>
    /// <param name="image">共享图像（像素已是显示朝向）。</param>
    /// <returns>位图；编码不支持或色彩转换失败时 null。</returns>
    internal static Bitmap? ConvertDetached(DecodedImage image) => Convert(image);

    /// <summary>清空缓存并释放位图。</summary>
    internal static void Clear()
    {
        lock (Sync) ClearLocked();
    }

    private static void ClearLocked() => Cache.Clear();

    /// <summary>把共享像素规范到 sRGB8 后转成 ARGB_8888 位图。</summary>
    /// <param name="image">共享图像。</param>
    /// <returns>位图；不支持时 null。</returns>
    private static Bitmap? Convert(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        try
        {
            if (IsSrgb8(image)) return ToBitmap(image);
            using var srgb = ColorEngine.ToSrgb8(image);
            return ToBitmap(srgb);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 位图转换失败：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>源已按 sRGB 解释：<see cref="ImageColorSpace.Legacy"/> 与 sRGB 同义（未标记的旧产物）。</summary>
    /// <param name="image">共享图像。</param>
    /// <returns>无需色彩转换时为 true。</returns>
    private static bool IsSrgb8(DecodedImage image)
        => image.Encoding is PixelEncoding.Bgra32 or PixelEncoding.Rgba8 or PixelEncoding.Rgb24
            && image.ColorSpace is ImageColorSpace.Srgb or ImageColorSpace.Legacy;

    /// <summary>
    /// 把 sRGB 8 位像素写成 ARGB_8888 位图。共享像素是预乘 alpha，而
    /// <see cref="Bitmap.SetPixels"/> 取非预乘颜色，半透明像素逐通道还原后再上交。
    /// </summary>
    /// <param name="image">sRGB 8 位共享图像。</param>
    /// <returns>位图；编码不支持时 null。</returns>
    private static Bitmap? ToBitmap(DecodedImage image)
    {
        int width = image.Width, height = image.Height;
        var pixels = image.Pixels;
        int bytesPerPixel = DecodedImage.BytesPerPixel(image.Encoding);
        if (pixels.LongLength < (long)(height - 1) * image.Stride + (long)width * bytesPerPixel) return null;
        var colors = new int[checked(width * height)];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            long row = (long)y * image.Stride;
            switch (image.Encoding)
            {
                case PixelEncoding.Bgra32:
                    for (int x = 0; x < width; x++)
                    {
                        long index = row + x * 4L;
                        colors[offset++] = PackArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
                    }
                    break;
                case PixelEncoding.Rgba8:
                    for (int x = 0; x < width; x++)
                    {
                        long index = row + x * 4L;
                        colors[offset++] = PackArgb(pixels[index + 3], pixels[index], pixels[index + 1], pixels[index + 2]);
                    }
                    break;
                case PixelEncoding.Rgb24:
                    for (int x = 0; x < width; x++)
                    {
                        long index = row + x * 3L;
                        colors[offset++] = PackArgb(255, pixels[index], pixels[index + 1], pixels[index + 2]);
                    }
                    break;
                default:
                    return null;
            }
        }
        var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        bitmap.SetPixels(colors, 0, width, 0, 0, width, height);
        return bitmap;
    }

    /// <summary>预乘通道 → 非预乘 ARGB 打包值；不透明与全透明直接短路。</summary>
    /// <param name="alpha">预乘前的 alpha。</param>
    /// <param name="r">预乘的红通道。</param>
    /// <param name="g">预乘的绿通道。</param>
    /// <param name="b">预乘的蓝通道。</param>
    /// <returns>非预乘 ARGB 颜色。</returns>
    private static int PackArgb(int alpha, int r, int g, int b)
    {
        if (alpha == 255) return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        if (alpha == 0) return 0;
        return (alpha << 24) | (Unpremultiply(r, alpha) << 16) | (Unpremultiply(g, alpha) << 8) | Unpremultiply(b, alpha);
    }

    /// <summary>按 alpha 还原预乘通道（四舍五入并夹到 255）。</summary>
    /// <param name="channel">预乘通道值。</param>
    /// <param name="alpha">alpha。</param>
    /// <returns>非预乘通道值。</returns>
    private static int Unpremultiply(int channel, int alpha) => Math.Min(255, (channel * 255 + alpha / 2) / alpha);
}
