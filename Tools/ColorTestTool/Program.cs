using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

int checks = 0;
double maxError = 0;
foreach (var space in new[] { ImageColorSpace.Srgb, ImageColorSpace.DisplayP3, ImageColorSpace.AdobeRgb })
{
    foreach (var color in new[] { new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, new byte[] { 128, 128, 128 },
        new byte[] { 0, 255, 0 }, new byte[] { 0, 0, 255 }, new byte[] { 255, 0, 0 }, new byte[] { 64, 192, 128 } })
    {
        using var source = new DecodedImage([color[0], color[1], color[2], 255], 1, 1, 4, PixelEncoding.Bgra32, space);
        using var converted = ColorEngine.ToScRgb16(source);
        var values = MemoryMarshal.Cast<byte, Half>(converted.Pixels.AsSpan(0, 8));
        double red = Decode(color[2] / 255.0, space), green = Decode(color[1] / 255.0, space), blue = Decode(color[0] / 255.0, space);
        double[] expected = space switch
        {
            ImageColorSpace.DisplayP3 => [1.224745 * red - .224904 * green, -.042058 * red + 1.042081 * green, -.019642 * red - .078655 * green + 1.098537 * blue],
            ImageColorSpace.AdobeRgb => [1.398283 * red - .398283 * green, green, -.042938 * green + 1.042938 * blue],
            _ => [red, green, blue],
        };
        for (int channel = 0; channel < 3; channel++)
        {
            double error = Math.Abs((double)values[channel] - expected[channel]);
            maxError = Math.Max(maxError, error);
            if (error > .0025) throw new InvalidOperationException($"{space} 转换误差 {error}");
        }
        if ((float)values[3] != 1) throw new InvalidOperationException("alpha 错误");
        if (space == ImageColorSpace.DisplayP3 && color[1] == 255 && color[0] == 0 && color[2] == 0 && (float)values[0] >= 0)
            throw new InvalidOperationException("广色域负分量被裁剪");
        checks++;
    }
}
using (var padded = new DecodedImage([0, 0, 255, 255, 0, 255, 0, 255, 99, 99, 255, 0, 0, 255, 128, 128, 128, 255], 2, 2, 10, PixelEncoding.Bgra32, ImageColorSpace.Srgb))
using (var floating = ColorEngine.ToScRgb16(padded))
using (var roundTrip = ColorEngine.ToSrgb8(floating))
{
    for (int row = 0; row < 2; row++)
        for (int column = 0; column < 8; column++)
            if (Math.Abs(roundTrip.Pixels[row * roundTrip.Stride + column] - padded.Pixels[row * 10 + column]) > 1) throw new InvalidOperationException("行距/8 位往返错误");
    checks++;
}
if (ColorEngine.InferJpegColorSpace(2) != ImageColorSpace.AdobeRgb || ColorEngine.InferJpegColorSpace(null, "R03") != ImageColorSpace.AdobeRgb || ColorEngine.InferJpegColorSpace(null) != ImageColorSpace.Srgb)
    throw new InvalidOperationException("JPEG 推断规则错误");
checks++;
Console.WriteLine($"PASS: {checks} color/stride/profile checks; max linear component error={maxError:F6}; P3 negative and >1 components preserved.");
return 0;

/// <summary>独立参考传递函数，用于验证 skcms 输出而非重复调用转换实现。</summary>
static double Decode(double value, ImageColorSpace colorSpace)
    => colorSpace == ImageColorSpace.AdobeRgb ? Math.Pow(value, 563.0 / 256) : value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
