using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoViewer.Core.Image;
using Windows.Graphics.Imaging;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 把共享层解码产物转成 WinUI 可显示的位图。
/// 主图不走这里 —— 主图由 <see cref="WindowsPhotoView"/> 直接上传 GPU 纹理(线性 RGBA16F);
/// 这里只服务胶片条的 8 位缩略图,对应 macOS 壳的 MacSymbols.CreateThumbnail。
/// </summary>
internal static class WindowsImageAdapter
{
    /// <summary>
    /// 把 8 位解码产物转成 XAML 位图源。
    /// </summary>
    /// <param name="image">解码产物(缩略图,8 位)</param>
    /// <returns>位图源;输入为 null 时返回 null</returns>
    public static WriteableBitmap? ToSource(DecodedImage? image)
    {
        if (image == null)
        {
            return null;
        }

        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
        {
            WriteBgra8(image, buffer);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// 用已就绪的 BGRA8 像素(如 Skia 缩放的导航图)创建 XAML 位图源。
    /// </summary>
    /// <param name="width">像素宽</param>
    /// <param name="height">像素高</param>
    /// <param name="bgra">紧排的 BGRA8 像素(长度 ≥ 宽 × 高 × 4)</param>
    /// <returns>位图源</returns>
    public static WriteableBitmap ToSource(int width, int height, ReadOnlySpan<byte> bgra)
    {
        var bitmap = new WriteableBitmap(width, height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
        {
            buffer.Write(bgra);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// 按 Windows 位图格式(BGRA8,预乘)写出像素。
    /// </summary>
    /// <param name="image">解码产物</param>
    /// <param name="destination">目标流</param>
    private static void WriteBgra8(DecodedImage image, Stream destination)
    {
        var rowBytes = image.Width * 4;
        switch (image.Encoding)
        {
            case PixelEncoding.Bgra32:
            {
                if (image.Stride == rowBytes)
                {
                    destination.Write(image.Pixels, 0, rowBytes * image.Height);
                    return;
                }

                for (var y = 0; y < image.Height; y++)
                {
                    destination.Write(image.Pixels, y * image.Stride, rowBytes);
                }

                return;
            }

            case PixelEncoding.Rgb24:
            {
                var row = new byte[rowBytes];
                for (var y = 0; y < image.Height; y++)
                {
                    var source = y * image.Stride;
                    for (var x = 0; x < image.Width; x++)
                    {
                        var target = x * 4;
                        row[target] = image.Pixels[source + (x * 3)];
                        row[target + 1] = image.Pixels[source + (x * 3) + 1];
                        row[target + 2] = image.Pixels[source + (x * 3) + 2];
                        row[target + 3] = 0xFF;
                    }

                    destination.Write(row, 0, rowBytes);
                }

                return;
            }

            default:
                throw new ArgumentException($"胶片段只支持 8 位像素,收到 {image.Encoding}。", nameof(image));
        }
    }
}
