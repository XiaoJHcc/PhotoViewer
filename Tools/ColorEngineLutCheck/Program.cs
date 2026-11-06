using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace ColorEngineLutCheck;

/// <summary>
/// 逐字节校验 ColorEngine 的查表快通道与通用（跨色域/位深/alpha）转换：以串行 skcms 为参考，
/// 比对全部半精度位模式、随机 8 位组合、真实照片与分带阈值两侧的通用用例，并给出耗时。
/// </summary>
internal static class Program
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>累计失败项；非零时进程以 1 退出。</summary>
    internal static int Failures;

    private static int Main(string[] args)
    {
        bool runLut = !args.Contains("general");
        bool runGeneral = !args.Contains("lut");

        Console.WriteLine("ColorEngine colour conversion byte-exactness check");
        Console.WriteLine(runLut && runGeneral
            ? "modes: LUT fast path + general conversion (no arguments = everything)"
            : $"modes: {(runLut ? "LUT fast path" : "general conversion")} only (pass 'general' or 'lut' to select)");
        Console.WriteLine();

        if (runLut)
        {
            LutBuildCost();
            WarmUp();
            ExhaustiveHalfPatterns();
            RandomEightBitCombinations();
            var sample = Path.Combine(TestCaseRoot(), "DSC08943.JPG");
            if (File.Exists(sample)) RealPhoto(sample);
            else Console.WriteLine($"[SKIP] real-photo LUT check: 缺少本地样本 {sample}（自备一张 JPG 放到 TestCase/CV/ 即可启用）");
            LargeImageTiming();
        }
        else
        {
            Console.WriteLine("[1]-[4] LUT fast-path checks skipped ('general' mode)");
            Console.WriteLine();
        }

        if (runGeneral) GeneralChecks.Run();
        else
        {
            Console.WriteLine("[5]-[6] general conversion checks skipped ('lut' mode)");
            Console.WriteLine();
        }

        Console.WriteLine(Failures == 0 ? "RESULT: all comparisons byte-identical" : $"RESULT: {Failures} FAILURE(S)");
        return Failures == 0 ? 0 : 1;
    }

    /// <summary>先跑一次小图，让查表在计时前构建完成。</summary>
    private static void WarmUp()
    {
        using var image = BuildLinearF16(4, 4, 0, (x, y) => ((ushort)0x3800, (ushort)0x3900, (ushort)0x3A00), 0x3C00);
        using var srgb8 = ColorEngine.ToSrgb8(image);
        using var linear = ColorEngine.ToScRgb16(srgb8);
        _ = linear.Width;
    }

    /// <summary>查表是进程内一次性的（懒构建），单独给出构建耗时，避免混进转换计时。</summary>
    private static void LutBuildCost()
    {
        var watch = Stopwatch.StartNew();
        BuildPrivate("BuildLinearToSrgb8Lut");
        double linearMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        BuildPrivate("BuildSrgb8ToLinearLut");
        double srgb8Ms = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        BuildPrivate("BuildLinearToSrgb8Lut");
        double linearAgainMs = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"LUT build (once per process, lazily): half->sRGB8 {linearMs:F2} ms (incl. first skcms init), " +
            $"sRGB8->half {srgb8Ms:F2} ms, half->sRGB8 again {linearAgainMs:F2} ms");
        Console.WriteLine();
    }

    private static void BuildPrivate(string name)
    {
        var method = typeof(ColorEngine).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException($"ColorEngine.{name}");
        method.Invoke(null, null);
    }

    // ---------------------------------------------------------------- 全部半精度位模式

    private static void ExhaustiveHalfPatterns()
    {
        Console.WriteLine("[1] exhaustive half bit patterns (all 65536, incl. negative/NaN/Inf/subnormal)");

        // 位模式连续递增且跨行分布，宽度取奇数以覆盖行尾，行距带填充以覆盖非紧凑布局。
        CheckF16ToSrgb8("R=G=B=pattern, odd width, padded stride", 257, 256, 8,
            (x, y) => { ushort p = Pattern(y * 257 + x); return (p, p, p); }, 0x3C00);
        CheckF16ToSrgb8("R only", 256, 256, 0,
            (x, y) => (Pattern(y * 256 + x), (ushort)0, (ushort)0), 0x3C00);
        CheckF16ToSrgb8("G only", 256, 256, 0,
            (x, y) => ((ushort)0, Pattern(y * 256 + x), (ushort)0), 0x3C00);
        CheckF16ToSrgb8("B only", 256, 256, 0,
            (x, y) => ((ushort)0, (ushort)0, Pattern(y * 256 + x)), 0x3C00);
        CheckF16ToSrgb8("random patterns per channel", 1024, 256, 0,
            (x, y) => ((ushort)Random24(x, y, 0), (ushort)Random24(x, y, 1), (ushort)Random24(x, y, 2)), 0x3C00);
        CheckF16ToSrgb8("non-opaque alpha falls back to skcms", 257, 64, 0,
            (x, y) => { ushort p = Pattern(y * 257 + x); return (p, p, p); }, 0x3800);
        Console.WriteLine();
    }

    private static void CheckF16ToSrgb8(string name, int width, int height, int stridePadding,
        Func<int, int, (ushort r, ushort g, ushort b)> pixel, ushort alpha)
    {
        using var source = BuildLinearF16(width, height, stridePadding, pixel, alpha);
        using var fast = ColorEngine.ToSrgb8(source);
        using var reference = ReferenceConversion.Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        Compare($"{name} -> Bgra32 sRGB", fast, reference);

        // Rgba8/Srgb 这一配对没有公开入口，经私有 Convert 走同一条快通道核对。
        using var fastRgba = PrivateConvert(source, PixelEncoding.Rgba8, ImageColorSpace.Srgb, Srgb);
        using var referenceRgba = ReferenceConversion.Convert(source, PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        Compare($"{name} -> Rgba8 sRGB", fastRgba, referenceRgba);
    }

    // ---------------------------------------------------------------- 8 位随机组合

    private static void RandomEightBitCombinations()
    {
        Console.WriteLine("[2] 8-bit random combinations (4M pixels per case)");
        var random = new Random(20240607);

        foreach (var encoding in new[] { PixelEncoding.Bgra32, PixelEncoding.Rgba8, PixelEncoding.Rgb24 })
        {
            int width = 2048, height = 2048;
            int bpp = DecodedImage.BytesPerPixel(encoding);
            var pixels = new byte[width * height * bpp];
            random.NextBytes(pixels);
            if (encoding != PixelEncoding.Rgb24)
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = byte.MaxValue;
            using var source = new DecodedImage(pixels, width, height, width * bpp, encoding, ImageColorSpace.Srgb);
            using var fast = ColorEngine.ToScRgb16(source);
            using var reference = ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
            Compare($"{encoding} sRGB random -> RgbaF16 linear", fast, reference);
        }

        // 行距填充 + 奇数宽度，覆盖非紧凑布局与行尾。
        {
            int width = 1023, height = 777, stride = width * 4 + 12;
            var pixels = new byte[stride * height];
            random.NextBytes(pixels);
            for (int row = 0; row < height; row++)
                for (int x = 0; x < width; x++) pixels[row * stride + x * 4 + 3] = byte.MaxValue;
            using var source = new DecodedImage(pixels, width, height, stride, PixelEncoding.Bgra32, ImageColorSpace.Legacy);
            using var fast = ColorEngine.ToScRgb16(source);
            using var reference = ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
            Compare("Bgra32 Legacy odd width + padded stride -> RgbaF16 linear", fast, reference);
        }

        // 非不透明 alpha：快通道必须退回 skcms，结果仍须逐字节一致。
        {
            int width = 512, height = 512;
            var pixels = new byte[width * height * 4];
            random.NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = (byte)random.Next(0, 256);
            using var source = new DecodedImage(pixels, width, height, width * 4, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
            using var fast = ColorEngine.ToScRgb16(source);
            using var reference = ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
            Compare("Bgra32 non-opaque alpha -> RgbaF16 linear (skcms fallback)", fast, reference);
        }

        // 元数据覆盖：快通道必须写回调用方给出的源尺寸与方向。
        {
            var pixels = new byte[64 * 32 * 4];
            random.NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = byte.MaxValue;
            using var source = new DecodedImage(pixels, 64, 32, 64 * 4, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
            using var fast = ColorEngine.ToScRgb16(source, 6000, 4000, 6);
            if (fast.SourceWidth != 6000 || fast.SourceHeight != 4000 || fast.Orientation != 6)
            {
                Failures++;
                Console.WriteLine($"    MISMATCH metadata: {fast.SourceWidth}x{fast.SourceHeight} o{fast.Orientation} (expected 6000x4000 o6)");
            }
            using var reference = ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb,
                sourceWidth: 6000, sourceHeight: 4000, orientation: 6);
            Compare("Bgra32 with metadata override -> RgbaF16 linear", fast, reference);
        }
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 真实照片

    private static void RealPhoto(string path)
    {
        Console.WriteLine($"[3] real photo: {Path.GetFileName(path)}");
        using var decoded = Decode(path);
        using var labelled = new DecodedImage(decoded.Pixels, decoded.Width, decoded.Height, decoded.Stride,
            decoded.Encoding, ImageColorSpace.Srgb);
        RoundTrip(label: $"decoded {decoded.Width}x{decoded.Height} {decoded.Encoding}", labelled);
        Console.WriteLine();
    }

    /// <summary>照片走一遍增强往返：sRGB8 → 线性 F16 → sRGB8，两端都与 skcms 逐字节比对。</summary>
    private static void RoundTrip(string label, DecodedImage source)
    {
        using var fastLinear = ColorEngine.ToScRgb16(source);
        using var referenceLinear = ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
        Compare($"{label}: sRGB8 -> RgbaF16 linear", fastLinear, referenceLinear);

        using var fastSrgb = ColorEngine.ToSrgb8(fastLinear);
        using var referenceSrgb = ReferenceConversion.Convert(fastLinear, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        Compare($"{label}: linear -> Bgra32 sRGB", fastSrgb, referenceSrgb);

        using var analysis = ColorEngine.ToAnalysisSrgb8(fastLinear);
        _ = analysis.Width;
    }

    // ---------------------------------------------------------------- 大图计时

    private static void LargeImageTiming()
    {
        const int width = 7008, height = 4672;
        Console.WriteLine($"[4] timing LUT fast path vs skcms on {width}x{height}");
        var random = new Random(31415);

        {
            var pixels = new byte[width * height * 8];
            var halves = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    float g = x / (float)width;
                    halves[at] = HalfBits(g * 1.1f - 0.05f + Noise(random));
                    halves[at + 1] = HalfBits(1f - g + Noise(random));
                    halves[at + 2] = HalfBits(g * 0.5f + Noise(random));
                    halves[at + 3] = 0x3C00;
                }
            }
            using var source = new DecodedImage(pixels, width, height, width * 8, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb,
                width, height, 1);
            Time("RgbaF16 linear -> Bgra32 sRGB (ToSrgb8)",
                () => ColorEngine.ToSrgb8(source), () => ReferenceConversion.Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb));
            Time("RgbaF16 linear -> analysis sRGB8 (ToAnalysisSrgb8)",
                () => ColorEngine.ToAnalysisSrgb8(source),
                () => RotateForAnalysis(ReferenceConversion.Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb), 1));
        }

        {
            var pixels = new byte[width * height * 4];
            random.NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = byte.MaxValue;
            using var source = new DecodedImage(pixels, width, height, width * 4, PixelEncoding.Bgra32, ImageColorSpace.Srgb,
                width, height, 1);
            Time("Bgra32 sRGB -> RgbaF16 linear (ToScRgb16)",
                () => ColorEngine.ToScRgb16(source), () => ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb));
        }

        {
            int stride = width * 3;
            var pixels = new byte[stride * height];
            random.NextBytes(pixels);
            using var source = new DecodedImage(pixels, width, height, stride, PixelEncoding.Rgb24, ImageColorSpace.Srgb,
                width, height, 1);
            Time("Rgb24 sRGB -> RgbaF16 linear (ToScRgb16)",
                () => ColorEngine.ToScRgb16(source), () => ReferenceConversion.Convert(source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb));
        }
        Console.WriteLine();
    }

    private static void Time(string label, Func<DecodedImage> fast, Func<DecodedImage> reference)
    {
        double fastMs = Measure(fast);
        double referenceMs = Measure(reference);

        using var fastImage = fast();
        using var referenceImage = reference();
        Compare(label, fastImage, referenceImage);
        Console.WriteLine($"    new {fastMs,7:F1} ms | old {referenceMs,7:F1} ms | x{referenceMs / fastMs:F2}");
    }

    private static double Measure(Func<DecodedImage> convert)
    {
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var watch = Stopwatch.StartNew();
            using var result = convert();
            watch.Stop();
            if (i > 0) best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static DecodedImage RotateForAnalysis(DecodedImage image, int orientation)
    {
        var (rotationCw, mirror) = ImageOrientationMapping.ToDisplay(orientation);
        if (rotationCw == 0 && !mirror) return image;
        var rotated = SkiaImageCodec.ApplyOrientation(image, rotationCw, mirror)!;
        image.Release();
        return rotated;
    }

    // ---------------------------------------------------------------- 比对与工具

    /// <summary>元数据 + 有效行字节（不含行尾填充）的严格比对，无任何容差。</summary>
    internal static void Compare(string label, DecodedImage fast, DecodedImage reference)
    {
        int bad = 0;
        if (fast.Width != reference.Width || fast.Height != reference.Height || fast.Stride != reference.Stride ||
            fast.Encoding != reference.Encoding || fast.ColorSpace != reference.ColorSpace ||
            fast.SourceWidth != reference.SourceWidth || fast.SourceHeight != reference.SourceHeight ||
            fast.Orientation != reference.Orientation)
        {
            bad++;
            Console.WriteLine($"    MISMATCH metadata: {label}: " +
                $"{fast.Width}x{fast.Height}/{fast.Stride}/{fast.Encoding}/{fast.ColorSpace}/{fast.SourceWidth}x{fast.SourceHeight}/o{fast.Orientation} vs " +
                $"{reference.Width}x{reference.Height}/{reference.Stride}/{reference.Encoding}/{reference.ColorSpace}/{reference.SourceWidth}x{reference.SourceHeight}/o{reference.Orientation}");
        }
        if (!fast.IccProfile.Span.SequenceEqual(reference.IccProfile.Span))
        {
            bad++;
            Console.WriteLine($"    MISMATCH metadata: {label}: ICC {fast.IccProfile.Length}B vs {reference.IccProfile.Length}B");
        }

        var (differing, firstBad) = PixelDiff(fast, reference);
        if (differing != 0)
        {
            bad++;
            Console.WriteLine($"    MISMATCH pixels: {label}: {differing} byte(s) differ, first at byte {firstBad}");
        }
        if (bad == 0)
            Console.WriteLine($"    ok  {label}");
        else
            Failures += bad;
    }

    /// <summary>逐行比对有效像素字节；返回差异字节数与首个差异的整图字节偏移。</summary>
    internal static (int differing, long firstBad) PixelDiff(DecodedImage a, DecodedImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Encoding != b.Encoding) return (1, 0);
        int rowBytes = a.Width * DecodedImage.BytesPerPixel(a.Encoding);
        var leftAll = a.Pixels;
        var rightAll = b.Pixels;
        long firstBad = -1;
        int differing = 0;
        for (int row = 0; row < a.Height; row++)
        {
            var left = new ReadOnlySpan<byte>(leftAll, row * a.Stride, rowBytes);
            var right = new ReadOnlySpan<byte>(rightAll, row * b.Stride, rowBytes);
            if (left.SequenceEqual(right)) continue;
            for (int i = 0; i < rowBytes; i++)
            {
                if (left[i] == right[i]) continue;
                differing++;
                if (firstBad < 0) firstBad = (long)row * rowBytes + i;
            }
        }
        return (differing, firstBad);
    }

    private static DecodedImage BuildLinearF16(int width, int height, int stridePadding,
        Func<int, int, (ushort r, ushort g, ushort b)> pixel, ushort alpha)
    {
        int stride = width * 8 + stridePadding;
        var pixels = new byte[stride * height];
        var halves = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                int at = (y * width + x) * 4;
                halves[at] = r;
                halves[at + 1] = g;
                halves[at + 2] = b;
                halves[at + 3] = alpha;
            }
        }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb,
            width, height, 1);
    }

    private static ushort Pattern(int index) => (ushort)(index & 0xFFFF);

    private static int Random24(int x, int y, int channel)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(channel * 83492791);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (int)(h & 0xFFFF);
        }
    }

    private static float Noise(Random random) => (float)(random.NextDouble() - 0.5) * 0.02f;

    private static ushort HalfBits(float value) => BitConverter.HalfToUInt16Bits((Half)value);

    /// <summary>Rgba8/Srgb 目标没有公开入口，经私有 Convert 走同一条快通道。</summary>
    private static DecodedImage PrivateConvert(DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace, SKColorSpace targetSpace)
    {
        var method = typeof(ColorEngine).GetMethod("Convert", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ColorEngine.Convert");
        return (DecodedImage?)method.Invoke(null, new object?[] { source, encoding, colorSpace, targetSpace, null, null, null })
            ?? throw new InvalidOperationException("Convert returned null");
    }

    private static DecodedImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return SkiaImageCodec.Decode(stream) ?? throw new InvalidOperationException($"decode failed: {path}");
    }

    private static string TestCaseRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? Directory.GetCurrentDirectory(), "TestCase", "CV");
    }
}
