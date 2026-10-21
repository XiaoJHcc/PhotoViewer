using System.Runtime.InteropServices;
using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>合成数据自检:不依赖 ffmpeg/libheif,验证采样、范围、重采样的基本正确性。</summary>
internal static class Smoke
{
    public static int Run()
    {
        var failures = 0;
        failures += Case("mono 10bit", HeifChromaSampling.Monochrome, 10, true, 1, 1);
        failures += Case("422 10bit tail 12px", HeifChromaSampling.Yuv422, 10, true, 2, 2, 12, 12);
        failures += Case("420 8bit tail 12px", HeifChromaSampling.Yuv420, 8, true, 2, 1, 12, 12);
        failures += Case("P3 10bit neutral", HeifChromaSampling.Yuv422, 10, true, 1, 1, 16, 16, true);
        failures += Case("422 10bit full", HeifChromaSampling.Yuv422, 10, true, 2, 2);
        failures += Case("420 10bit full", HeifChromaSampling.Yuv420, 10, true, 2, 2);
        failures += Case("444 10bit limited", HeifChromaSampling.Yuv444, 10, false, 1, 1);
        failures += Case("420 8bit full", HeifChromaSampling.Yuv420, 8, true, 1, 3);
        failures += Case("mono 10bit", HeifChromaSampling.Monochrome, 10, true, 1, 1);
        Console.WriteLine(failures == 0 ? "smoke: PASS" : $"smoke: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Case(
        string label,
        HeifChromaSampling chroma,
        int bitDepth,
        bool fullRange,
        int columns,
        int rows,
        int tileWidth = 16,
        int tileHeight = 16,
        bool displayP3 = false)
    {
        var failures = 0;
        var width = columns * tileWidth;
        var height = rows * tileHeight;
        var bytesPerSample = bitDepth > 8 ? 2 : 1;
        var maximum = (1 << bitDepth) - 1;

        var chromaWidth = chroma switch
        {
            HeifChromaSampling.Monochrome => 0,
            HeifChromaSampling.Yuv420 => (tileWidth + 1) / 2,
            HeifChromaSampling.Yuv422 => (tileWidth + 1) / 2,
            _ => tileWidth,
        };
        var chromaHeight = chroma == HeifChromaSampling.Yuv420 ? (tileHeight + 1) / 2 : tileHeight;

        var handles = new List<GCHandle>();
        var tiles = new List<YuvTile>();
        try
        {
            // 每块瓦片填同一套值,便于按位置预测。
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var yPlane = new byte[tileWidth * tileHeight * bytesPerSample];
                    FillPlane(yPlane, tileWidth, tileHeight, bitDepth, (x, y) => (maximum * (x + y)) / (2 * (tileWidth + tileHeight - 2)));
                    var cbPlane = new byte[Math.Max(1, chromaWidth * chromaHeight) * bytesPerSample];
                    var crPlane = new byte[Math.Max(1, chromaWidth * chromaHeight) * bytesPerSample];
                    if (chroma != HeifChromaSampling.Monochrome)
                    {
                        // 恒定色度:便于手算期望 RGB。
                        // P3 用例用中性色度:线性矩阵对中性灰是恒等,便于独立校验矩阵与往返转换。
                        var neutral = displayP3;
                        FillPlane(cbPlane, chromaWidth, chromaHeight, bitDepth, (_, _) => neutral ? 1 << (bitDepth - 1) : (int)(0.40 * maximum));
                        FillPlane(crPlane, chromaWidth, chromaHeight, bitDepth, (_, _) => neutral ? 1 << (bitDepth - 1) : (int)(0.55 * maximum));
                    }

                    var yHandle = GCHandle.Alloc(yPlane, GCHandleType.Pinned);
                    handles.Add(yHandle);
                    var cbHandle = GCHandle.Alloc(cbPlane, GCHandleType.Pinned);
                    handles.Add(cbHandle);
                    var crHandle = GCHandle.Alloc(crPlane, GCHandleType.Pinned);
                    handles.Add(crHandle);

                    tiles.Add(new YuvTile
                    {
                        Y = yHandle.AddrOfPinnedObject(),
                        Cb = chroma == HeifChromaSampling.Monochrome ? IntPtr.Zero : cbHandle.AddrOfPinnedObject(),
                        Cr = chroma == HeifChromaSampling.Monochrome ? IntPtr.Zero : crHandle.AddrOfPinnedObject(),
                        StrideY = tileWidth * bytesPerSample,
                        StrideCb = chromaWidth * bytesPerSample,
                        StrideCr = chromaWidth * bytesPerSample,
                        Column = column * tileWidth,
                        Row = row * tileHeight,
                        Width = tileWidth,
                        Height = tileHeight,
                    });
                }
            }

            var source = new YuvImage
            {
                Tiles = tiles,
                CanvasWidth = width,
                CanvasHeight = height,
                BitDepth = bitDepth,
                Chroma = chroma,
                MatrixCode = 5,
                PrimariesCode = displayP3 ? 12 : 1,
                TransferCode = 13,
                FullRange = fullRange,
            };

            YuvImageConverter.MaxDegreeOfParallelism = Environment.GetEnvironmentVariable("PV_SEQ") == "1" ? 1 : 0;
            var bgra = new byte[width * height * 4];
            if (!YuvImageConverter.TryConvertToBgra32(source, width, height, bgra, width * 4, out var reason))
            {
                Console.WriteLine($"  ! {label}: rejected: {reason}");
                return 1;
            }

            var f16 = new byte[width * height * 8];
            if (!YuvImageConverter.TryConvertToRgbaLinearF16(source, width, height, f16, width * 8, out var f16Reason))
            {
                Console.WriteLine($"  ! {label}: F16 rejected: {f16Reason}");
                return 1;
            }

            // 通道间一致性:与逐像素的独立实现(浮点标量公式)对照。
            var expected = new float[3];
            var maxError = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var lumaSample = (maximum * ((x % tileWidth) + (y % tileHeight))) / (2 * (tileWidth + tileHeight - 2));
                    var monochrome = chroma == HeifChromaSampling.Monochrome || displayP3;
                    var cbSample = monochrome ? 1 << (bitDepth - 1) : (int)(0.40 * maximum);
                    var crSample = monochrome ? 1 << (bitDepth - 1) : (int)(0.55 * maximum);
                    ReferenceFormula(bitDepth, fullRange, lumaSample, cbSample, crSample, expected);
                    var index = ((y * width) + x) * 4;
                    var quantized = new[]
                    {
                        Quantize(expected[0]),
                        Quantize(expected[1]),
                        Quantize(expected[2]),
                    };
                    var actual = new[] { bgra[index + 2], bgra[index + 1], bgra[index] };
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var error = Math.Abs(actual[channel] - quantized[channel]);
                        if (error > maxError) maxError = error;
                    }
                }
            }

            if (displayP3)
            {
                // 中性灰经 P3→sRGB 线性矩阵后仍应是同一灰度(矩阵行和为 1),容差放宽到 2 级。
                if (maxError > 2)
                {
                    Console.WriteLine($"  ! {label}: max channel error {maxError} (expected <=2)");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"  ok {label}: {width}x{height} max channel error {maxError} (P3 matrix neutral)");
                }
                return failures;
            }

            if (maxError > 1)
            {
                Console.WriteLine($"  ! {label}: max channel error {maxError} (expected <=1)");
                failures++;
            }
            else
            {
                Console.WriteLine($"  ok {label}: {width}x{height} max channel error {maxError}");
            }

            // 半浮点:中心像素的线性值应与 sRGB 反变换一致
            var centerIndex = (((height / 2) * width) + (width / 2)) * 8;
            var half = (Half)BitConverter.UInt16BitsToHalf((ushort)(f16[centerIndex + 0] | (f16[centerIndex + 1] << 8)));
            ReferenceFormula(
                bitDepth,
                fullRange,
                (maximum * (((width / 2) % tileWidth) + ((height / 2) % tileHeight))) / (2 * (tileWidth + tileHeight - 2)),
                chroma == HeifChromaSampling.Monochrome || displayP3 ? 1 << (bitDepth - 1) : (int)(0.40 * maximum),
                chroma == HeifChromaSampling.Monochrome || displayP3 ? 1 << (bitDepth - 1) : (int)(0.55 * maximum),
                expected);
            var expectedLinear = SrgbToLinear(expected[0]);
            if (Math.Abs((float)half - expectedLinear) > 0.002f)
            {
                Console.WriteLine($"  ! {label}: F16 R {half} != expected {expectedLinear:F4}");
                failures++;
            }

            if (Math.Abs((float)(Half)1.0f - 1.0f) > 1e-6f) failures++;
        }
        finally
        {
            foreach (var handle in handles) handle.Free();
        }

        return failures;
    }

    private static void FillPlane(byte[] plane, int width, int height, int bitDepth, Func<int, int, int> valueAt)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = valueAt(x, y);
                var i = (y * width) + x;
                if (bitDepth > 8)
                {
                    plane[i * 2] = (byte)(value & 0xFF);
                    plane[(i * 2) + 1] = (byte)(value >> 8);
                }
                else
                {
                    plane[i] = (byte)value;
                }
            }
        }
    }

    private static void ReferenceFormula(
        int bitDepth,
        bool fullRange,
        int lumaSample,
        int cbSample,
        int crSample,
        float[] rgb)
    {
        var scale = (float)(1 << (bitDepth - 8));
        var maximum = (float)((1 << bitDepth) - 1);
        float y;
        float cb;
        float cr;
        if (fullRange)
        {
            y = lumaSample / maximum;
            cb = (cbSample - (1 << (bitDepth - 1))) / maximum;
            cr = (crSample - (1 << (bitDepth - 1))) / maximum;
        }
        else
        {
            y = (lumaSample - (16f * scale)) / (219f * scale);
            cb = (cbSample - (128f * scale)) / (224f * scale);
            cr = (crSample - (128f * scale)) / (224f * scale);
        }

        const float kr = 0.299f;
        const float kb = 0.114f;
        const float kg = 1f - kr - kb;
        rgb[0] = y + (2f * (1f - kr) * cr);
        rgb[1] = y - ((2f * kb * (1f - kb) / kg) * cb) - ((2f * kr * (1f - kr) / kg) * cr);
        rgb[2] = y + (2f * (1f - kb) * cb);
    }

    private static int Quantize(float gamma)
    {
        var value = gamma * 255f;
        if (!(value > 0f)) return 0;
        if (value >= 255f) return 255;
        return (int)(value + 0.5f);
    }

    private static float SrgbToLinear(float value)
        => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
}
