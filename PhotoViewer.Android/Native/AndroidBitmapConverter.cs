using System;
using System.Collections.Generic;
using Android.Graphics;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 共享 <see cref="DecodedImage"/> → Android <see cref="Bitmap"/> 的缩略图缓存（A3）：
/// 胶片栏卡片只拿 Thumb 档（Bgra32/Rgb24），按解码产物引用缓存，缓存被淘汰换新引用时自动重转。
/// </summary>
internal static class AndroidBitmapConverter
{
    private const int MaximumEntries = 400;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (DecodedImage Source, Bitmap Bitmap)> Cache = new(StringComparer.Ordinal);

    /// <summary>取得缩略图位图；同一解码产物引用只转换一次。</summary>
    /// <param name="key">缓存键（文件 PathKey）。</param>
    /// <param name="image">共享缩略图（调用方持有租约）。</param>
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
    internal static Bitmap? ConvertDetached(DecodedImage image) => Convert(image);

    /// <summary>清空缓存并释放位图。</summary>
    internal static void Clear()
    {
        lock (Sync) ClearLocked();
    }

    private static void ClearLocked() => Cache.Clear();

    /// <summary>把 Bgra32/Rgb24/Rgba8 共享像素转成 ARGB_8888 位图。</summary>
    /// <param name="image">共享图像。</param>
    /// <returns>位图；不支持时 null。</returns>
    private static Bitmap? Convert(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        var pixels = image.Pixels;
        if (pixels.Length < image.Height * image.Stride) return null;
        var colors = new int[image.Width * image.Height];
        int offset = 0;
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Stride;
            for (int x = 0; x < image.Width; x++)
            {
                switch (image.Encoding)
                {
                    case PixelEncoding.Bgra32:
                    {
                        int index = row + x * 4;
                        colors[offset++] = (pixels[index + 3] << 24) | (pixels[index + 2] << 16)
                            | (pixels[index + 1] << 8) | pixels[index];
                        break;
                    }
                    case PixelEncoding.Rgba8:
                    {
                        int index = row + x * 4;
                        colors[offset++] = (pixels[index + 3] << 24) | (pixels[index] << 16)
                            | (pixels[index + 1] << 8) | pixels[index + 2];
                        break;
                    }
                    case PixelEncoding.Rgb24:
                    {
                        int index = row + x * 3;
                        colors[offset++] = unchecked((int)0xFF000000) | (pixels[index] << 16)
                            | (pixels[index + 1] << 8) | pixels[index + 2];
                        break;
                    }
                    default:
                        return null;
                }
            }
        }
        var bitmap = Bitmap.CreateBitmap(image.Width, image.Height, Bitmap.Config.Argb8888!);
        bitmap.SetPixels(colors, 0, image.Width, 0, 0, image.Width, image.Height);
        return bitmap;
    }
}
