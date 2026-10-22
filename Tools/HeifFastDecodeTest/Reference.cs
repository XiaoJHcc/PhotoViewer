using System.Runtime.InteropServices;
using LibHeifSharp;
using SkiaSharp;

namespace HeifFastDecodeTest;

/// <summary>
/// 生产参考管线(不引用 app 工程,按现有实现复刻):
/// libheif 解码 → RGB24 → BGRA32;Skia Mitchell 降采样;Skia 色彩管理转线性 RGBA 半浮点。
/// </summary>
internal static class Reference
{
    public sealed class RgbImage
    {
        public required int Width { get; init; }

        public required int Height { get; init; }

        public required byte[] Rgb24 { get; init; }

        public required int Stride { get; init; }
    }

    /// <summary>libheif 解码为交错 RGB24。</summary>
    public static RgbImage? DecodeRgb24(byte[] fileData, bool ignoreTransformations)
    {
        try
        {
            using var context = new HeifContext(fileData);
            using var handle = context.GetPrimaryImageHandle();
            if (handle is null) return null;

            var options = new HeifDecodingOptions { IgnoreTransformations = ignoreTransformations };
            using var image = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24, options);
            if (image is null) return null;

            var plane = image.GetPlane(HeifChannel.Interleaved);
            if (plane is null) return null;
            var width = (int)image.Width;
            var height = (int)image.Height;
            var stride = (int)plane.Stride;
            var source = new byte[stride * height];
            Marshal.Copy(plane.Scan0, source, 0, source.Length);
            return new RgbImage { Width = width, Height = height, Rgb24 = source, Stride = stride };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  ! libheif decode failed: " + ex.Message);
            return null;
        }
    }

    public static byte[] RgbToBgra(RgbImage image)
    {
        var output = new byte[image.Width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        {
            var sourceRow = y * image.Stride;
            var targetRow = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                var source = sourceRow + (x * 3);
                var target = targetRow + (x * 4);
                output[target] = image.Rgb24[source + 2];
                output[target + 1] = image.Rgb24[source + 1];
                output[target + 2] = image.Rgb24[source];
                output[target + 3] = 255;
            }
        }
        return output;
    }

    /// <summary>Skia Mitchell 可分离降采样(生产口径:SKCubicResampler.Mitchell)。</summary>
    public static byte[] DownscaleBgra(byte[] source, int width, int height, int targetWidth, int targetHeight)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var sourceBitmap = new SKBitmap(info);
        Marshal.Copy(source, 0, sourceBitmap.GetPixels(), source.Length);
        using var image = SKImage.FromBitmap(sourceBitmap);

        var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(targetInfo);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(
            image,
            new SKRect(0, 0, width, height),
            new SKRect(0, 0, targetWidth, targetHeight),
            new SKSamplingOptions(SKCubicResampler.Mitchell),
            paint);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var target = new SKBitmap(targetInfo);
        snapshot.ReadPixels(targetInfo, target.GetPixels(), targetInfo.RowBytes, 0, 0);
        var output = new byte[targetWidth * targetHeight * 4];
        Marshal.Copy(target.GetPixels(), output, 0, output.Length);
        return output;
    }

    /// <summary>BGRA32(sRGB)→ 线性 RGBA 半浮点,走 Skia 色彩管理。</summary>
    public static byte[] ToLinearF16(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
        var targetInfo = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul, SKColorSpace.CreateSrgbLinear());
        var source = Marshal.AllocHGlobal(bgra.Length);
        var destination = Marshal.AllocHGlobal(width * height * 8);
        Marshal.Copy(bgra, 0, source, bgra.Length);
        try
        {
            using var sourcePixmap = new SKPixmap(info, source);
            using var destinationPixmap = new SKPixmap(targetInfo, destination);
            if (!sourcePixmap.ReadPixels(destinationPixmap, 0, 0)) throw new InvalidOperationException("ReadPixels failed");
            var output = new byte[width * height * 8];
            Marshal.Copy(destination, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(destination);
        }
    }

    /// <summary>线性 RGBA 半浮点 → sRGB 8 位 BGRA(用于把两条 F16 结果拉到同一可见空间比较)。</summary>
    public static byte[] F16ToSrgb8(byte[] f16, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul, SKColorSpace.CreateSrgbLinear());
        var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
        var source = Marshal.AllocHGlobal(f16.Length);
        var destination = Marshal.AllocHGlobal(width * height * 4);
        Marshal.Copy(f16, 0, source, f16.Length);
        try
        {
            using var pixmap = new SKPixmap(info, source);
            using var destinationPixmap = new SKPixmap(targetInfo, destination);
            if (!pixmap.ReadPixels(destinationPixmap, 0, 0)) throw new InvalidOperationException("ReadPixels failed");
            var output = new byte[width * height * 4];
            Marshal.Copy(destination, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(destination);
        }
    }

    public static void SavePng(string path, byte[] bgra, int width, int height, int cropX, int cropY, int cropWidth, int cropHeight)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = Marshal.AllocHGlobal(bgra.Length);
        Marshal.Copy(bgra, 0, pixels, bgra.Length);
        try
        {
            using var pixmap = new SKPixmap(info, pixels);
            using var image = SKImage.FromPixels(pixmap);
            using var crop = image.Subset(new SKRectI(cropX, cropY, cropX + cropWidth, cropY + cropHeight));
            using var encoded = crop!.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            encoded.SaveTo(stream);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }
}
