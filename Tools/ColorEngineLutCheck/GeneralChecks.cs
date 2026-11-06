using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace ColorEngineLutCheck;

/// <summary>
/// 通用转换（跨色域 / 跨位深 / 预乘 alpha / 元数据）与串行 skcms 参考逐字节对照，
/// 覆盖 1 Mpx 分带阈值的两侧、奇数尺寸与尾带、对齐与非对齐行距、并发只读源。
/// </summary>
internal static class GeneralChecks
{
    /// <summary>与 ColorEngine 分带阈值一致（宽 × 高 ≥ 该值才按行分带）。</summary>
    private const long BandThresholdPixels = 1024L * 1024L;

    private static readonly Random Dice = new(20240611);

    /// <summary>NaN / +Inf / -Inf 的半精度位模式，检查分带路径对这些值仍逐字节等同串行 skcms。</summary>
    private static readonly ushort[] SpecialHalves = [0x7E00, 0x7C00, 0xFC00];

    public static void Run()
    {
        Console.WriteLine("[5] general conversions vs serial skcms (byte-exact, no tolerance)");
        Geometry();
        EncodingMatrix();
        CrossGamut();
        PremultipliedAlpha();
        Metadata();
        Concurrency();
        Console.WriteLine();
        Timing();
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 阈值 / 尺寸 / 行距

    private static void Geometry()
    {
        Console.WriteLine($"  -- both sides of the {BandThresholdPixels} px banding threshold, odd sizes, stride alignment");
        CaseOwned("1024x1023 below threshold, tight stride: Rgba8 P3 -> Rgba8 sRGB",
            Random8(1024, 1023, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("1024x1023 below threshold, unaligned stride: Rgba8 P3 -> Bgra32 sRGB",
            Random8(1024, 1023, 3, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        CaseOwned("1024x1024 at threshold (banded), padding 12: Rgba8 P3 -> Rgba8 sRGB",
            Random8(1024, 1024, 12, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("1025x1025 banded, odd, padding 12: Rgba8 P3 -> Bgra32 sRGB",
            Random8(1025, 1025, 12, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        CaseOwned("1025x1025 banded, odd, padding 3 (packed): Rgba8 P3 -> Rgba8 sRGB",
            Random8(1025, 1025, 3, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("1031x1019 banded, ragged tail band: RgbaF16 P3 -> Bgra32 sRGB",
            RandomF16(1031, 1019, 0, extended: true, special: true), PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        CaseOwned("1025x1025 banded, F16 unaligned stride (packed): RgbaF16 P3 -> RgbaF16 linear",
            RandomF16(1025, 1025, 4, extended: true), PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }

    // ---------------------------------------------------------------- 编码组合

    private static void EncodingMatrix()
    {
        Console.WriteLine("  -- encoding matrix at 1025x1025 (banded): Rgba8 / Bgra32 / RgbaF16 sources to 3 targets");
        using var rgba8 = Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        using var bgra32 = Random8(1025, 1025, 0, PixelEncoding.Bgra32, ImageColorSpace.DisplayP3);
        using var f16 = RandomF16(1025, 1025, 0, extended: true, special: true);
        foreach (var source in new[] { rgba8, bgra32, f16 })
        {
            Case($"{source.Encoding} P3 -> Rgba8 sRGB", source, PixelEncoding.Rgba8, ImageColorSpace.Srgb);
            Case($"{source.Encoding} P3 -> Bgra32 sRGB", source, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
            Case($"{source.Encoding} P3 -> RgbaF16 linear", source, PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
        }
    }

    // ---------------------------------------------------------------- 跨色域

    private static void CrossGamut()
    {
        Console.WriteLine("  -- cross-gamut pairs at 1025x1025 (banded), including real ICC profiles from Fixtures/jpeg");
        CaseOwned("DisplayP3 -> sRGB (Rgba8)", Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("sRGB -> DisplayP3 (Rgba8)", Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Srgb), PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        CaseOwned("DisplayP3 -> AdobeRgb (Rgba8)", Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3), PixelEncoding.Rgba8, ImageColorSpace.AdobeRgb);
        CaseOwned("AdobeRgb -> DisplayP3 (Rgba8)", Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.AdobeRgb), PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        CaseOwned("AdobeRgb -> sRGB (Rgba8)", Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.AdobeRgb), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("scRGB linear F16 (extended) -> DisplayP3 Rgba8",
            RandomF16(1025, 1025, 0, extended: true, colorSpace: ImageColorSpace.LinearSrgb), PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);

        var iccP3 = LoadIcc("color-p3.jpg");
        var iccAdobe = LoadIcc("color-adobe.jpg");
        if (iccP3 == null || iccAdobe == null)
        {
            Console.WriteLine("    [SKIP] ICC cases: 缺少 Fixtures/jpeg 的 color-p3.jpg / color-adobe.jpg 内嵌 profile");
            return;
        }
        CaseOwned("ICC (real Display P3 profile) -> sRGB (Rgba8)",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Icc, icc: iccP3), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("sRGB -> ICC target (real Display P3 profile), Rgba8",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Srgb), PixelEncoding.Rgba8, ImageColorSpace.Icc, iccP3);
        CaseOwned("ICC -> ICC (same profile), Rgba8 -> Bgra32",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Icc, icc: iccP3), PixelEncoding.Bgra32, ImageColorSpace.Icc, iccP3);
        CaseOwned("ICC (real Adobe profile) -> DisplayP3 (Rgba8)",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Icc, icc: iccAdobe), PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
    }

    // ---------------------------------------------------------------- 预乘 alpha

    private static void PremultipliedAlpha()
    {
        Console.WriteLine("  -- premultiplied semi-transparent / fully transparent sources (premul components kept <= alpha)");
        CaseOwned("Rgba8 P3 semitransparent premul -> Rgba8 sRGB, banded",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3, premul: true), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("Bgra32 P3 semitransparent premul -> Bgra32 sRGB, below threshold",
            Random8(1024, 1023, 0, PixelEncoding.Bgra32, ImageColorSpace.DisplayP3, premul: true), PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        CaseOwned("Rgba8 fully transparent -> Bgra32 sRGB",
            Transparent8(1025, 1025, ImageColorSpace.DisplayP3), PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        CaseOwned("RgbaF16 P3 semitransparent premul -> Rgba8 sRGB, banded",
            RandomF16(1025, 1025, 0, premul: true), PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        CaseOwned("RgbaF16 fully transparent -> RgbaF16 linear",
            TransparentF16(1031, 1019), PixelEncoding.RgbaF16, ImageColorSpace.LinearSrgb);
    }

    // ---------------------------------------------------------------- 元数据

    private static void Metadata()
    {
        Console.WriteLine("  -- source size / orientation / ICC carried through the conversion");
        CaseOwned("Rgba8 P3 6000x4000 o6 -> Rgba8 sRGB keeps source geometry",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3, sourceWidth: 6000, sourceHeight: 4000, orientation: 6),
            PixelEncoding.Rgba8, ImageColorSpace.Srgb);

        var iccP3 = LoadIcc("color-p3.jpg");
        if (iccP3 == null)
        {
            Console.WriteLine("    [SKIP] ICC metadata case: 缺少 Fixtures/jpeg/color-p3.jpg 内嵌 profile");
            return;
        }
        CaseOwned("Rgba8 ICC 4000x3000 o8 -> Rgba8 ICC target keeps profile + geometry",
            Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.Icc, icc: iccP3, sourceWidth: 4000, sourceHeight: 3000, orientation: 8),
            PixelEncoding.Rgba8, ImageColorSpace.Icc, iccP3);
    }

    // ---------------------------------------------------------------- 并发只读源

    private static void Concurrency()
    {
        Console.WriteLine("  -- concurrent conversions of one read-only source (same result as serial, source not mutated)");
        using var source = Random8(1025, 1025, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
        using var reference = ReferenceConversion.Convert(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        var sourceBefore = (byte[])source.Pixels.Clone();

        int workers = Math.Max(2, Environment.ProcessorCount);
        int mismatches = 0;
        using var gate = new Barrier(workers);
        var tasks = new Task[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                gate.SignalAndWait();
                for (int i = 0; i < 3; i++)
                {
                    using var result = ColorEngine.ToDisplayEncoding(source, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
                    var (differing, _) = Program.PixelDiff(result, reference);
                    if (differing != 0) Interlocked.Add(ref mismatches, differing);
                }
            });
        }
        Task.WaitAll(tasks);

        if (mismatches != 0)
        {
            Program.Failures++;
            Console.WriteLine($"    MISMATCH pixels: {workers} concurrent conversions: {mismatches} byte(s) differ from the serial reference");
        }
        else if (!source.Pixels.AsSpan().SequenceEqual(sourceBefore))
        {
            Program.Failures++;
            Console.WriteLine("    MISMATCH source: 并发转换改动了只读源的像素");
        }
        else
        {
            Console.WriteLine($"    ok  {workers} concurrent conversions x3 -> Bgra32 sRGB, source untouched");
        }
    }

    // ---------------------------------------------------------------- 大图计时

    private static void Timing()
    {
        const int width = 6000, height = 4000;
        Console.WriteLine($"[6] Display P3 -> sRGB at {width}x{height} ({(long)width * height / 1e6:F1} Mpx, above the "
            + $"{BandThresholdPixels / 1e6:F3} Mpx threshold → general path uses ForEachRowBand, serial reference one pixmap)");
        Console.WriteLine($"    {Environment.ProcessorCount} cores, bands = min(cores*4, height/32). "
            + "Timing = CPU colour conversion only (skcms), includes pooled RentPixels rental + SKPixmap setup on both sides; "
            + "median of 5 runs after one warm-up each; no decode, no upload.");

        {
            using var source = Random8(width, height, 0, PixelEncoding.Rgba8, ImageColorSpace.DisplayP3);
            TimePair("Rgba8 DisplayP3 -> Rgba8 sRGB", source, PixelEncoding.Rgba8, ImageColorSpace.Srgb);
        }
        {
            var path = Path.Combine(FixtureRoot(), "jpeg-large", "color-p3-large.jpg");
            if (File.Exists(path))
            {
                using var decoded = Decode(path);
                Console.WriteLine($"    input: {Path.GetFileName(path)} {decoded.Width}x{decoded.Height} {decoded.Encoding} "
                    + $"{decoded.ColorSpace} icc={decoded.IccProfile.Length}B (real cross-gamut pixels)");
                TimePair("real ICC P3 (6000x4000) -> Bgra32 sRGB", decoded, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
            }
            else
            {
                Console.WriteLine($"    [SKIP] real 6000x4000 P3 input: 缺少 {path}");
            }
        }
        {
            using var source = RandomF16(width, height, 0, extended: true, special: true);
            TimePair("RgbaF16 DisplayP3 (extended) -> Bgra32 sRGB", source, PixelEncoding.Bgra32, ImageColorSpace.Srgb);
        }
    }

    private static void TimePair(string label, DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace,
        ReadOnlyMemory<byte> icc = default)
    {
        Func<DecodedImage> general = () => ColorEngine.ToDisplayEncoding(source, encoding, colorSpace, icc);
        Func<DecodedImage> serial = () => ReferenceConversion.Convert(source, encoding, colorSpace, icc, pooled: true);
        using (general()) { }
        using (serial()) { }

        var (generalMedian, generalMin, generalMax) = Samples(general);
        var (serialMedian, serialMin, serialMax) = Samples(serial);

        using var fastImage = general();
        using var referenceImage = serial();
        Program.Compare(label, fastImage, referenceImage);
        double targetMb = (double)source.Width * source.Height * DecodedImage.BytesPerPixel(encoding) / 1e6;
        Console.WriteLine($"    general median {generalMedian,7:F1} ms (min {generalMin:F1} / max {generalMax:F1})"
            + $" | serial median {serialMedian,7:F1} ms (min {serialMin:F1} / max {serialMax:F1})"
            + $" | x{serialMedian / generalMedian:F2} | target {targetMb:F1} MB pooled on both sides");
    }

    /// <summary>预热后取 5 次中位值，避免用单次或极值代表性能。</summary>
    private static (double median, double min, double max) Samples(Func<DecodedImage> convert, int runs = 5)
    {
        var samples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            using var result = convert();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return (samples[runs / 2], samples[0], samples[^1]);
    }

    // ---------------------------------------------------------------- 用例驱动

    /// <summary>一次转换的严格比对：公开通用入口 vs 串行 skcms 参考。源图像由调用方持有。</summary>
    private static void Case(string label, DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace,
        ReadOnlyMemory<byte> icc = default)
    {
        using var general = ColorEngine.ToDisplayEncoding(source, encoding, colorSpace, icc);
        using var serial = ReferenceConversion.Convert(source, encoding, colorSpace, icc);
        Program.Compare(label, general, serial);
    }

    /// <summary>同上，源图像归本次用例所有。</summary>
    private static void CaseOwned(string label, DecodedImage source, PixelEncoding encoding, ImageColorSpace colorSpace,
        ReadOnlyMemory<byte> icc = default)
    {
        using (source) Case(label, source, encoding, colorSpace, icc);
    }

    // ---------------------------------------------------------------- 源图构造

    /// <summary>8 位源（Rgba8 / Bgra32）：可选预乘（颜色分量 ≤ alpha）与行距填充。</summary>
    private static DecodedImage Random8(int width, int height, int stridePadding, PixelEncoding encoding,
        ImageColorSpace colorSpace, bool premul = false, byte[]? icc = null,
        int sourceWidth = 0, int sourceHeight = 0, int orientation = 1)
    {
        if (encoding is not (PixelEncoding.Rgba8 or PixelEncoding.Bgra32))
            throw new ArgumentOutOfRangeException(nameof(encoding));
        bool rgbaOrder = encoding == PixelEncoding.Rgba8;
        int stride = checked(width * 4 + stridePadding);
        var pixels = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int at = row + x * 4;
                byte a = premul ? (byte)Dice.Next(256) : byte.MaxValue;
                byte c0 = Component(a, premul), c1 = Component(a, premul), c2 = Component(a, premul);
                // 两个方向都填交错通道，覆盖 B/R 交换。
                if (rgbaOrder)
                {
                    pixels[at] = c0;
                    pixels[at + 1] = c1;
                    pixels[at + 2] = c2;
                }
                else
                {
                    pixels[at] = c2;
                    pixels[at + 1] = c1;
                    pixels[at + 2] = c0;
                }
                pixels[at + 3] = a;
            }
        }
        return new DecodedImage(pixels, width, height, stride, encoding, colorSpace, sourceWidth, sourceHeight, orientation, icc);
    }

    private static byte Component(byte alpha, bool premul) => premul ? (byte)Dice.Next(alpha + 1) : (byte)Dice.Next(256);

    /// <summary>全透明 8 位源：alpha 与颜色分量都为 0（合法预乘）。</summary>
    private static DecodedImage Transparent8(int width, int height, ImageColorSpace colorSpace)
    {
        int stride = width * 4;
        return new DecodedImage(new byte[checked(stride * height)], width, height, stride, PixelEncoding.Rgba8, colorSpace);
    }

    /// <summary>半精度源：可选扩展色域（负值与 &gt;1）、NaN/Inf 位模式、预乘（分量 ≤ alpha）与行距填充。</summary>
    private static DecodedImage RandomF16(int width, int height, int stridePadding, bool extended = false, bool special = false,
        bool premul = false, ImageColorSpace colorSpace = ImageColorSpace.DisplayP3, byte[]? icc = null)
    {
        int stride = checked(width * 8 + stridePadding);
        if ((stride & 1) != 0) throw new ArgumentException("F16 行距必须是偶数字节", nameof(stridePadding));
        var pixels = new byte[checked(stride * height)];
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            var row = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan(y * stride, width * 8));
            for (int x = 0; x < width; x++, index++)
            {
                int at = x * 4;
                double alpha = premul ? Dice.NextDouble() : 1.0;
                row[at] = Half(Value(alpha, premul, extended));
                row[at + 1] = Half(Value(alpha, premul, extended));
                row[at + 2] = Half(Value(alpha, premul, extended));
                row[at + 3] = Half((float)alpha);
                if (special && !premul && index % 4093 == 0)
                {
                    row[at] = SpecialHalves[index % 3];
                    row[at + 1] = SpecialHalves[(index + 1) % 3];
                }
            }
        }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.RgbaF16, colorSpace, 0, 0, 1, icc);
    }

    private static float Value(double alpha, bool premul, bool extended)
        => premul ? (float)(Dice.NextDouble() * alpha)
            : extended ? (float)(Dice.NextDouble() * 1.3 - 0.15)
            : (float)Dice.NextDouble();

    private static DecodedImage TransparentF16(int width, int height)
    {
        int stride = width * 8;
        return new DecodedImage(new byte[checked(stride * height)], width, height, stride, PixelEncoding.RgbaF16, ImageColorSpace.DisplayP3);
    }

    private static ushort Half(float value) => BitConverter.HalfToUInt16Bits((Half)value);

    // ---------------------------------------------------------------- 素材与路径

    /// <summary>Fixtures/jpeg 某夹具的内嵌 ICC 原始字节；缺失或无法解析时返回 null（调用方按 SKIP 处理）。</summary>
    private static byte[]? LoadIcc(string fixture)
    {
        var path = Path.Combine(FixtureRoot(), "jpeg", fixture);
        if (!File.Exists(path)) return null;
        var metadata = JpegColorMetadata.Read(File.ReadAllBytes(path));
        if (!metadata.HasIccProfile || metadata.IccProfile is not { } profile) return null;
        using var space = SkiaSharp.SKColorSpace.CreateIcc(profile);
        return space == null ? null : profile;
    }

    private static string FixtureRoot() => Path.Combine(RepoRoot(), "Fixtures");

    private static DecodedImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return SkiaImageCodec.Decode(stream) ?? throw new InvalidOperationException($"decode failed: {path}");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
