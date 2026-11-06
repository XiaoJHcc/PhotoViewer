using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Android.Native;

internal static partial class AndroidNativeChecks
{
    private static async Task CheckBandedColorTransformAsync(DecodedImage source)
    {
        var result = await Task.Run(() =>
        {
            var serialTimes = new double[3];
            var bandedTimes = new double[3];
            bool equal = true;
            for (int run = 0; run < 4; run++)
            {
                var started = Stopwatch.GetTimestamp();
                using var serial = SerialP3ToSrgbForChecks(source);
                var serialMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                using var banded = ColorEngine.ToDisplayEncoding(source, PixelEncoding.Rgba8, ImageColorSpace.Srgb);
                var bandedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                equal &= serial.Width == banded.Width && serial.Height == banded.Height
                    && serial.Encoding == banded.Encoding && serial.ColorSpace == banded.ColorSpace
                    && serial.SourceWidth == banded.SourceWidth && serial.SourceHeight == banded.SourceHeight
                    && serial.Orientation == banded.Orientation && serial.IccProfile.Span.SequenceEqual(banded.IccProfile.Span);
                for (int row = 0; equal && row < serial.Height; row++)
                    equal = serial.Pixels.AsSpan(row * serial.Stride, serial.Width * 4)
                        .SequenceEqual(banded.Pixels.AsSpan(row * banded.Stride, banded.Width * 4));
                if (run > 0)
                {
                    serialTimes[run - 1] = serialMs;
                    bandedTimes[run - 1] = bandedMs;
                }
            }
            Array.Sort(serialTimes);
            Array.Sort(bandedTimes);
            return (equal, serialMs: serialTimes[1], bandedMs: bandedTimes[1]);
        });
        Check("color 大图分带转换与串行逐字节一致", result.equal,
            $"{source.Width}x{source.Height} n=3 median serial={result.serialMs:F2}ms banded={result.bandedMs:F2}ms cores={Environment.ProcessorCount}");
    }

    private static unsafe DecodedImage SerialP3ToSrgbForChecks(DecodedImage source)
    {
        if (source.Encoding != PixelEncoding.Rgba8 || source.ColorSpace != ImageColorSpace.DisplayP3)
            throw new ArgumentException("串行参考要求 RGBA8 Display P3 源", nameof(source));
        using var sourceSpace = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var targetSpace = SKColorSpace.CreateSrgb();
        int stride = checked(source.Width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * source.Height));
        try
        {
            fixed (byte* sourcePixels = source.Pixels)
            fixed (byte* targetPixels = pixels)
            {
                var sourceInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul, sourceSpace);
                var targetInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul, targetSpace);
                using var pixmap = new SKPixmap(sourceInfo, (IntPtr)sourcePixels, source.Stride);
                if (!pixmap.ReadPixels(targetInfo, (IntPtr)targetPixels, stride))
                    throw new InvalidOperationException("串行色彩参考转换失败");
            }
            return DecodedImage.CreatePooled(pixels, source.Width, source.Height, stride, PixelEncoding.Rgba8,
                ImageColorSpace.Srgb, source.SourceWidth, source.SourceHeight, source.Orientation);
        }
        catch
        {
            ImageBufferPool.Return(pixels);
            throw;
        }
    }
}
