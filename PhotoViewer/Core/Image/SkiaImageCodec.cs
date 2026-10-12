using System;
using System.IO;
using SkiaSharp;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 基于 SkiaSharp 的解码/缩放/旋转/裁剪像素原语,Core 层唯一的像素操作出口。
/// 输出统一为 <see cref="PixelEncoding.Bgra32"/>(BGRA8888 premul,与原 Avalonia 位图内存布局一致)。
/// </summary>
public static class SkiaImageCodec
{
    private static SKImageInfo BgraInfo(int width, int height)
        => new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>
    /// 全尺寸解码图像流(JPEG/PNG 等 Skia 支持格式)。
    /// </summary>
    /// <param name="stream">图像字节流。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? Decode(Stream stream)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            using var bmp = SKBitmap.Decode(codec, BgraInfo(codec.Info.Width, codec.Info.Height));
            return FromSkBitmap(bmp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.Decode failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按目标宽度等比缩解码(等价于原 Avalonia <c>Bitmap.DecodeToWidth</c>):
    /// 先按 codec 支持的采样率解码,必要时再做一次高质量精确缩放。
    /// </summary>
    /// <param name="stream">图像字节流。</param>
    /// <param name="targetWidth">目标宽度(不放大,超过原始宽度时按原始宽度)。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? DecodeToWidth(Stream stream, int targetWidth)
    {
        if (targetWidth <= 0) return null;

        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int srcWidth = codec.Info.Width;
            if (srcWidth <= 0) return null;
            if (targetWidth >= srcWidth)
            {
                using var full = SKBitmap.Decode(codec, BgraInfo(codec.Info.Width, codec.Info.Height));
                return FromSkBitmap(full);
            }

            // codec 支持的最接近采样尺寸,再做一次精确缩放
            var scaled = codec.GetScaledDimensions((float)targetWidth / srcWidth);
            using var sampled = SKBitmap.Decode(codec, BgraInfo(scaled.Width, scaled.Height));
            if (sampled == null) return null;

            if (sampled.Width == targetWidth)
            {
                return FromSkBitmap(sampled);
            }

            int targetHeight = Math.Max(1, (int)Math.Round((double)codec.Info.Height * targetWidth / codec.Info.Width));
            using var exact = new SKBitmap(BgraInfo(targetWidth, targetHeight));
            using (var canvas = new SKCanvas(exact))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(SKImage.FromBitmap(sampled),
                    SKRect.Create(0, 0, sampled.Width, sampled.Height),
                    SKRect.Create(0, 0, targetWidth, targetHeight),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(exact);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.DecodeToWidth failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按顺时针角度(0/90/180/270)与可选水平镜像,把传感器朝向像素变换到显示朝向。
    /// 变换顺序:平移到源中心 → (水平镜像) → (顺时针旋转) → 平移到目标中心;返回新对象。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="rotationCw">顺时针旋转角度。</param>
    /// <param name="mirror">旋转前是否水平镜像。</param>
    /// <returns>变换后的新图像;参数或尺寸非法时返回 null。</returns>
    public static DecodedImage? ApplyOrientation(DecodedImage src, int rotationCw, bool mirror)
    {
        if (rotationCw is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(rotationCw));
        if (src.Encoding != PixelEncoding.Bgra32) return null;

        int sw = src.Width, sh = src.Height;
        bool swap = rotationCw is 90 or 270;
        int dw = swap ? sh : sw;
        int dh = swap ? sw : sh;

        try
        {
            using var dst = new SKBitmap(BgraInfo(dw, dh));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(dw / 2f, dh / 2f);
                if (rotationCw != 0) canvas.RotateDegrees(rotationCw);
                if (mirror) canvas.Scale(-1, 1);
                canvas.Translate(-sw / 2f, -sh / 2f);
                canvas.DrawImage(FromBytes(src), 0, 0);
            }
            return FromSkBitmap(dst);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ApplyOrientation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按源像素坐标裁剪(行级字节拷贝,无重采样)。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="width">裁剪宽。</param>
    /// <param name="height">裁剪高。</param>
    /// <returns>裁剪结果;失败返回 null。</returns>
    public static DecodedImage? Crop(DecodedImage src, int x, int y, int width, int height)
    {
        if (src.Encoding != PixelEncoding.Bgra32) return null;
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > src.Width || y + height > src.Height)
            return null;

        const int bpp = 4;
        var rowBytes = width * bpp;
        var dest = new byte[rowBytes * height];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(src.Pixels, (long)(y + row) * src.Stride + (long)x * bpp, dest, (long)row * rowBytes, rowBytes);
        }
        return new DecodedImage(dest, width, height, rowBytes, PixelEncoding.Bgra32);
    }

    /// <summary>
    /// SKBitmap → DecodedImage 行级拷贝(仅接受 Bgra8888)。
    /// </summary>
    /// <param name="bmp">源位图。</param>
    /// <returns>转换结果;尺寸非法或格式不符返回 null。</returns>
    public static DecodedImage? FromSkBitmap(SKBitmap? bmp)
    {
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;

        var bytes = new byte[(long)bmp.RowBytes * bmp.Height];
        unsafe
        {
            var srcPtr = (byte*)bmp.GetPixels();
            if (srcPtr == null) return null;
            fixed (byte* destPtr = bytes)
            {
                Buffer.MemoryCopy(srcPtr, destPtr, bytes.Length, bytes.Length);
            }
        }
        return new DecodedImage(bytes, bmp.Width, bmp.Height, bmp.RowBytes, PixelEncoding.Bgra32);
    }

    /// <summary>
    /// 拉伸缩放到精确尺寸(可改变纵横比;线性采样,与原 Avalonia DrawImage 默认采样口径一致,
    /// DINO 模型输入等像素契约场景依赖此口径)。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="width">目标宽。</param>
    /// <param name="height">目标高。</param>
    /// <returns>缩放结果;失败返回 null。</returns>
    public static DecodedImage? ScaleToExact(DecodedImage src, int width, int height)
    {
        if (src.Encoding != PixelEncoding.Bgra32 || width <= 0 || height <= 0) return null;

        try
        {
            using var dst = new SKBitmap(BgraInfo(width, height));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                canvas.DrawImage(FromBytes(src),
                    SKRect.Create(0, 0, src.Width, src.Height),
                    SKRect.Create(0, 0, width, height),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(dst);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ScaleToExact failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把图像编码为 PNG 写入流(供工具/训练侧落盘;不修改源图像)。
    /// </summary>
    /// <param name="image">源图像。</param>
    /// <param name="stream">目标流。</param>
    /// <returns>是否成功。</returns>
    public static bool SavePng(DecodedImage image, Stream stream)
    {
        try
        {
            using var skImage = FromBytes(image);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return false;
            data.SaveTo(stream);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.SavePng failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>从 DecodedImage 字节构造只读 SKImage(拷贝一次)。</summary>
    private static SKImage FromBytes(DecodedImage image)
    {
        var info = BgraInfo(image.Width, image.Height);
        var data = SKData.CreateCopy(image.Pixels);
        return SKImage.FromPixels(info, data, image.Stride);
    }
}
