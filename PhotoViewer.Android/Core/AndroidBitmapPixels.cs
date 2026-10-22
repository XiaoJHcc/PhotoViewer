using System;
using System.Runtime.InteropServices;
using Android.Graphics;
using SkiaSharp;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android <see cref="Bitmap"/> 与 RGBA8 池化缓冲之间的小工具（RAW 解码器与预览解码共用）。
/// 与 <see cref="AndroidPresentationDecoder"/> 的位图拷贝口径一致：ARGB_8888 打包值拆成 R,G,B,A。
/// </summary>
internal static class AndroidBitmapPixels
{
    /// <summary>把位图按目标色彩空间拷成 RGBA8 池化缓冲（照片不透明，A 取位图原值）。</summary>
    /// <param name="bitmap">已解码位图，所有权不转移。</param>
    /// <param name="colorSpace">像素色彩标签。</param>
    /// <param name="sourceWidth">未旋转的全分辨率源宽。</param>
    /// <param name="sourceHeight">未旋转的全分辨率源高。</param>
    /// <param name="orientation">待渲染的 EXIF 方向。</param>
    /// <returns>RGBA8 产物；失败返回 null。</returns>
    internal static DecodedImage? ToRgba8(Bitmap bitmap, ImageColorSpace colorSpace, int sourceWidth, int sourceHeight, int orientation)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (width <= 0 || height <= 0) return null;

        var buffer = DecodedImage.RentPixels(checked(width * height * 4));
        try
        {
            const int rowsPerChunk = 64;
            var pixels = new int[width * Math.Min(height, rowsPerChunk)];
            int offset = 0;
            for (int y = 0; y < height; y += rowsPerChunk)
            {
                int rows = Math.Min(rowsPerChunk, height - y);
                bitmap.GetPixels(pixels, 0, width, 0, y, width, rows);
                int count = width * rows;
                for (int i = 0; i < count; i++)
                {
                    int color = pixels[i];
                    buffer[offset++] = (byte)(color >> 16);
                    buffer[offset++] = (byte)(color >> 8);
                    buffer[offset++] = (byte)color;
                    buffer[offset++] = (byte)(color >> 24);
                }
            }
            return DecodedImage.CreatePooled(buffer, width, height, checked(width * 4), PixelEncoding.Rgba8,
                colorSpace, Math.Max(sourceWidth, width), Math.Max(sourceHeight, height), orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>RGBA8 → Bgra32（Skia 缩放通道只吃 Bgra32/Rgb24）。调用方负责释放返回对象。</summary>
    /// <param name="source">源图像，所有权不转移。</param>
    /// <returns>Bgra32 图像；编码不符或失败返回 null。</returns>
    internal static DecodedImage? ToBgra32(DecodedImage source)
    {
        if (source.Encoding != PixelEncoding.Rgba8) return null;
        int width = source.Width, height = source.Height;
        var buffer = DecodedImage.RentPixels(checked(width * height * 4));
        try
        {
            var src = source.Pixels;
            for (int y = 0; y < height; y++)
            {
                long srcRow = (long)y * source.Stride;
                long dstRow = (long)y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    long si = srcRow + x * 4;
                    long di = dstRow + x * 4;
                    buffer[di] = src[si + 2];     // B
                    buffer[di + 1] = src[si + 1]; // G
                    buffer[di + 2] = src[si];     // R
                    buffer[di + 3] = src[si + 3]; // A
                }
            }
            return DecodedImage.CreatePooled(buffer, width, height, checked(width * 4), PixelEncoding.Bgra32,
                source.ColorSpace, source.SourceWidth, source.SourceHeight, source.Orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>把 Bgra32 像素拷进 RGBA8 目标缓冲（R/B 交换，保留行距）。</summary>
    /// <param name="source">源像素。</param>
    /// <param name="sourceStride">源行距（字节）。</param>
    /// <param name="target">目标缓冲，长度 ≥ width*height*4。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    internal static void CopyBgraToRgba(byte[] source, int sourceStride, byte[] target, int width, int height)
    {
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            long row = (long)y * sourceStride;
            for (int x = 0; x < width; x++)
            {
                long i = row + x * 4;
                target[offset++] = source[i + 2];
                target[offset++] = source[i + 1];
                target[offset++] = source[i];
                target[offset++] = source[i + 3];
            }
        }
    }

    /// <summary>
    /// RGBA8 直接按长边精缩（Mitchell 三次滤波，与 <see cref="SkiaImageCodec.ScaleToLongEdge"/> 同一采样口径）。
    /// 不走 Bgra32 往返：RAW 代理常见 25MP→4800px，四次整图拷贝的代价比缩放本身还大。
    /// </summary>
    /// <param name="source">源图像（RGBA8，不透明），所有权不转移。</param>
    /// <param name="targetLongEdge">目标长边。</param>
    /// <param name="colorSpace">产物色彩标签。</param>
    /// <param name="sourceWidth">全分辨率源宽（几何基准）。</param>
    /// <param name="sourceHeight">全分辨率源高。</param>
    /// <param name="orientation">待渲染方向。</param>
    /// <returns>缩放产物；无需缩放或失败返回 null。</returns>
    internal static DecodedImage? ScaleToLongEdge(DecodedImage source, int targetLongEdge, ImageColorSpace colorSpace,
        int sourceWidth, int sourceHeight, int orientation)
    {
        if (source.Encoding != PixelEncoding.Rgba8 || targetLongEdge <= 0) return null;
        int longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= targetLongEdge) return null;
        double scale = (double)targetLongEdge / longEdge;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        byte[]? outBuffer = null;
        try
        {
            unsafe
            {
                fixed (byte* sourcePixels = source.Pixels)
                {
                    // 照片不透明：Opaque 让 Skia 走整数快速路径（Unpremul 会退化成逐像素 alpha 混合）。
                    var sourceInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var sourceBitmap = new SKBitmap();
                    if (!sourceBitmap.InstallPixels(sourceInfo, (IntPtr)sourcePixels, source.Stride)) return null;
                    using var sourceImage = SKImage.FromBitmap(sourceBitmap);

                    var targetInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var target = new SKBitmap(targetInfo);
                    using (var canvas = new SKCanvas(target))
                    {
                        canvas.Clear(SKColors.Black);
                        canvas.DrawImage(sourceImage,
                            SKRect.Create(0, 0, source.Width, source.Height),
                            SKRect.Create(0, 0, width, height),
                            new SKSamplingOptions(SKCubicResampler.Mitchell), new SKPaint());
                    }

                    outBuffer = DecodedImage.RentPixels(checked(width * height * 4));
                    Marshal.Copy(target.GetPixels(), outBuffer, 0, width * height * 4);
                }
            }
            var result = DecodedImage.CreatePooled(outBuffer, width, height, width * 4, PixelEncoding.Rgba8,
                colorSpace, sourceWidth, sourceHeight, orientation);
            outBuffer = null;   // 所有权已转移
            return result;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] RAW 代理缩放失败：{exception.Message}");
            return null;
        }
        finally
        {
            if (outBuffer != null) ImageBufferPool.Return(outBuffer);
        }
    }
}
