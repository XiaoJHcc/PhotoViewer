using System;
using System.IO;
using SkiaSharp;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 基于 SkiaSharp 的解码/缩放/旋转/裁剪像素原语,Core 层唯一的像素操作出口。
/// 输出统一为 <see cref="PixelEncoding.Bgra32"/>(BGRA8888 premul,与原 Avalonia 位图内存布局一致)。
/// 解码产物缓冲经 <see cref="ImageBufferPool"/> 租用(P1-M3)。
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
        return DecodeToFit(stream, targetWidth, int.MaxValue);
    }

    /// <summary>
    /// 按长边上限等比缩解码(P1-M4 代理图口径):codec 子采样 + Mitchell 精修,不放大。
    /// 旋转无关:长边约束对 90° 旋转对称。
    /// </summary>
    /// <param name="stream">图像字节流。</param>
    /// <param name="targetLongEdge">目标长边像素。</param>
    /// <returns>解码结果;失败返回 null。</returns>
    public static DecodedImage? DecodeToLongEdge(Stream stream, int targetLongEdge)
    {
        if (targetLongEdge <= 0) return null;
        return DecodeToFit(stream, int.MaxValue, targetLongEdge);
    }

    /// <summary>
    /// 统一缩解码实现:宽 ≤ maxWidth 且长边 ≤ maxLongEdge 中取更严格的缩放;不放大。
    /// </summary>
    private static DecodedImage? DecodeToFit(Stream stream, int maxWidth, int maxLongEdge)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int srcW = codec.Info.Width, srcH = codec.Info.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            // 计算等比缩放因子(两个约束取更严格;不放大)
            double scale = 1.0;
            if (maxWidth != int.MaxValue && maxWidth < srcW) scale = Math.Min(scale, (double)maxWidth / srcW);
            int longEdge = Math.Max(srcW, srcH);
            if (maxLongEdge != int.MaxValue && maxLongEdge < longEdge) scale = Math.Min(scale, (double)maxLongEdge / longEdge);

            if (scale >= 1.0)
            {
                using var full = SKBitmap.Decode(codec, BgraInfo(srcW, srcH));
                return FromSkBitmap(full);
            }

            int targetW = Math.Max(1, (int)Math.Round(srcW * scale));
            int targetH = Math.Max(1, (int)Math.Round(srcH * scale));

            // codec 支持的最接近采样尺寸,再做一次 Mitchell 精确缩放
            var scaled = codec.GetScaledDimensions((float)scale);
            using var sampled = SKBitmap.Decode(codec, BgraInfo(scaled.Width, scaled.Height));
            if (sampled == null) return null;

            if (sampled.Width == targetW && sampled.Height == targetH)
            {
                return FromSkBitmap(sampled);
            }

            using var exact = new SKBitmap(BgraInfo(targetW, targetH));
            using (var canvas = new SKCanvas(exact))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(SKImage.FromBitmap(sampled),
                    SKRect.Create(0, 0, sampled.Width, sampled.Height),
                    SKRect.Create(0, 0, targetW, targetH),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(exact);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.DecodeToFit failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按长边上限等比缩放已解码图像(Mitchell 三次滤波,不放大;输入支持 Bgra32/Rgb24,输出恒 Bgra32)。
    /// Windows libheif 无子采样解码,P1-M4 代理图靠"全解码 + 本方法精缩"实现(画质红线:不许最近邻)。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <param name="targetLongEdge">目标长边像素。</param>
    /// <returns>缩放结果;失败或不需缩放返回 null 语义见调用方。</returns>
    public static DecodedImage? ScaleToLongEdge(DecodedImage src, int targetLongEdge)
    {
        if (targetLongEdge <= 0) return null;
        int longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= targetLongEdge) return null; // 不放大;调用方用原图

        double scale = (double)targetLongEdge / longEdge;
        int targetW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int targetH = Math.Max(1, (int)Math.Round(src.Height * scale));

        // Skia 无 24 位打包像素格式:Rgb24 源先转 Bgra32
        DecodedImage? converted = null;
        var effective = src;
        if (src.Encoding == PixelEncoding.Rgb24)
        {
            converted = ToBgra32(src);
            if (converted == null) return null;
            effective = converted;
        }

        try
        {
            using var dst = new SKBitmap(BgraInfo(targetW, targetH));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(FromBytes(effective),
                    SKRect.Create(0, 0, effective.Width, effective.Height),
                    SKRect.Create(0, 0, targetW, targetH),
                    sampling, new SKPaint());
            }
            return FromSkBitmap(dst);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ScaleToLongEdge failed: {ex.Message}");
            return null;
        }
        finally
        {
            converted?.Release();
        }
    }

    /// <summary>
    /// Rgb24 → Bgra32 转换(alpha 置 255);输入已是 Bgra32 时返回 null(调用方无需转换)。缓冲池租用。
    /// </summary>
    /// <param name="src">源图像,所有权不转移。</param>
    /// <returns>转换结果;编码非 Rgb24 或失败返回 null。</returns>
    public static DecodedImage? ToBgra32(DecodedImage src)
    {
        if (src.Encoding != PixelEncoding.Rgb24) return null;
        int w = src.Width, h = src.Height;
        try
        {
            var dest = DecodedImage.RentPixels(w * h * 4);
            for (int y = 0; y < h; y++)
            {
                long srcRow = (long)y * src.Stride;
                long dstRow = (long)y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    long si = srcRow + x * 3;
                    long di = dstRow + x * 4;
                    dest[di] = src.Pixels[si];         // B
                    dest[di + 1] = src.Pixels[si + 1]; // G
                    dest[di + 2] = src.Pixels[si + 2]; // R
                    dest[di + 3] = 255;
                }
            }
            return DecodedImage.CreatePooled(dest, w, h, w * 4, PixelEncoding.Bgra32);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SkiaImageCodec.ToBgra32 failed: {ex.Message}");
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
    /// 按源像素坐标裁剪(行级字节拷贝,无重采样);缓冲池租用。
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
        var dest = DecodedImage.RentPixels(rowBytes * height);
        for (int row = 0; row < height; row++)
        {
            Array.Copy(src.Pixels, (long)(y + row) * src.Stride + (long)x * bpp, dest, (long)row * rowBytes, rowBytes);
        }
        return DecodedImage.CreatePooled(dest, width, height, rowBytes, PixelEncoding.Bgra32);
    }

    /// <summary>
    /// SKBitmap → DecodedImage 行级拷贝(仅接受 Bgra8888);缓冲池租用。
    /// </summary>
    /// <param name="bmp">源位图。</param>
    /// <returns>转换结果;尺寸非法或格式不符返回 null。</returns>
    public static DecodedImage? FromSkBitmap(SKBitmap? bmp)
    {
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;

        long copyBytes = (long)bmp.RowBytes * bmp.Height;
        var bytes = DecodedImage.RentPixels((int)Math.Min(copyBytes, int.MaxValue));
        unsafe
        {
            var srcPtr = (byte*)bmp.GetPixels();
            if (srcPtr == null)
            {
                ImageBufferPool.Return(bytes);
                return null;
            }
            // 池化缓冲长度是分档对齐后的值,可能大于实际数据——只拷实际字节数
            fixed (byte* destPtr = bytes)
            {
                Buffer.MemoryCopy(srcPtr, destPtr, bytes.Length, copyBytes);
            }
        }
        return DecodedImage.CreatePooled(bytes, bmp.Width, bmp.Height, bmp.RowBytes, PixelEncoding.Bgra32);
    }

    /// <summary>
    /// 拉伸缩放到精确尺寸(可改变纵横比;线性采样,与原 Avalonia DrawImage 默认采样口径一致,
    /// DINO 模型输入等像素契约场景依赖此口径)。缓冲池租用。
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
