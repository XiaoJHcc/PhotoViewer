using System.Text.Json;
using LibHeifSharp;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using SkiaSharp;

namespace HeifFastDecodeTest;

/// <summary>
/// 色彩链端到端:真实 <see cref="FastHeifDecoder"/>(pvhevc 瓦片)与 Windows libheif 主图回落
/// (<see cref="LibHeifDecoder.DecodePresentationAsync"/>)在同一夹具上解出的线性 RGBA16F 逐色块对照,
/// 并给出与"独立矩阵/参考实现"的期望值比较。
/// </summary>
/// <remarks>
/// 夹具是 <c>Tools/generate-native-color-fixtures.py</c> 生成的色卡:上两行 6 块纯色、行 560–719 为灰阶斜坡、
/// 左下白色标记块、其余为 (64,64,64) 背景。色块的**码值**是已知真值,按文件声明的色彩描述(ICC 或 nclx)
/// 用参考实现(skcms,即 <see cref="SkiaSharp.SKColorSpace"/> 转换)换算成线性 sRGB 期望值 —— 与被测代码
/// 走的是不同实现,因此同时校验 ICC 优先、原色矩阵与传递曲线。
/// </remarks>
internal static class E2E
{
    private const int ChartWidth = 1200;
    private const int ChartHeight = 800;

    /// <summary>色卡上 6 块纯色块按码值排列(左→右、上→下)。</summary>
    private static readonly (string Name, int R, int G, int B)[] Patches =
    {
        ("red", 255, 0, 0),
        ("green", 0, 255, 0),
        ("blue", 0, 0, 255),
        ("cyan", 0, 255, 255),
        ("magenta", 255, 0, 255),
        ("yellow", 255, 255, 0),
    };

    /// <summary>灰阶斜坡上取样的列(期望灰 = round(x·255/1199))。</summary>
    private static readonly int[] RampColumns = { 100, 400, 800, 1100 };

    public static int Run(string[] args)
    {
        var files = ResolveFiles(args.Length == 0 ? new[] { "Fixtures/heif" } : args);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no input files");
            return 2;
        }

        var failures = 0;
        var report = new List<object>();
        failures += RunDarkPlaneNormalization();
        foreach (var file in files)
        {
            var fileFailures = RunFile(file, report);
            failures += fileFailures;
            Console.WriteLine();
        }

        var reportPath = Path.Combine(AppContext.BaseDirectory, "e2e-color-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report: {reportPath}");
        Console.WriteLine(failures == 0 ? "e2e: PASS" : $"e2e: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 码值归一化不得依赖白点或像素最大值:自建一块**纯暗**的 12 位交错平面(除一个满量程样本外,全部 ≤255,
    /// 即满量程 4095 的 6%),并把"句柄声明位深"故意给 8。
    /// 正确实现按平面自身声明的范围(4095)归一化 → 暗区仍是暗区、那个满量程样本才是 1.0;
    /// 若按声明位深(255)、按像素最大值或任何固定猜测,暗区会被顶到 1.0 或整体偏亮。
    /// </summary>
    private static int RunDarkPlaneNormalization()
    {
        const int width = 32, height = 8;
        const int planeBitDepth = 12;
        const int fullScale = (1 << planeBitDepth) - 1;
        Console.WriteLine($"暗平面归一化:构造 {width}x{height}、平面范围 {planeBitDepth} 位、除一个样本外全部 ≤255(不含白点)");

        using var image = new HeifImage(width, height, HeifColorspace.Rgb, HeifChroma.InterleavedRgb48LE);
        image.AddPlane(HeifChannel.Interleaved, width, height, planeBitDepth);
        var plane = image.GetPlane(HeifChannel.Interleaved);
        if (plane == null || plane.Scan0 == IntPtr.Zero)
        {
            Console.WriteLine("  ! 自建平面失败");
            return 1;
        }

        unsafe
        {
            for (var y = 0; y < height; y++)
            {
                var row = (ushort*)((byte*)plane.Scan0 + (long)y * plane.Stride);
                for (var x = 0; x < width; x++)
                {
                    var value = (ushort)((x * 8 + y) % 256);
                    if (y == 0 && x == width - 1) value = fullScale; // 唯一满量程样本:证明除数就是这个平面的满量程
                    row[x * 3] = value;
                    row[x * 3 + 1] = value;
                    row[x * 3 + 2] = value;
                }
            }
        }

        // 声明位深故意给 8:归一化必须忽略它、只按平面范围 12 位。
        using var coded = LibHeifDecoder.ConvertRgb48ToCodeValuesForTest(image, declaredBitDepth: 8);
        if (coded is null)
        {
            Console.WriteLine($"  ! 码值转换失败: {HeifLoader.LastDecodeError}");
            return 1;
        }

        if (coded.Encoding != PixelEncoding.RgbaF16 || coded.Width != width || coded.Height != height)
        {
            Console.WriteLine($"  ! 产物形态错误: {coded.Encoding} {coded.Width}x{coded.Height}");
            return 1;
        }

        var failures = 0;
        float darkest = 1f, brightestDark = 0f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var expected = (y == 0 && x == width - 1) ? 1f : ((x * 8 + y) % 256) / (float)fullScale;
                var actual = LinearSample(coded, x, y);
                if (Math.Abs(actual - expected) > 0.002f)
                {
                    if (failures++ < 5)
                        Console.WriteLine($"  ! ({x},{y}) 期望 {expected:F4} 实际 {actual:F4}(没按平面范围归一化)");
                    continue;
                }

                if (expected < 0.9f)
                {
                    darkest = Math.Min(darkest, actual);
                    brightestDark = Math.Max(brightestDark, actual);
                }
            }
        }

        var darkOk = brightestDark < 0.07f;
        Console.WriteLine(
            $"  {(darkOk ? "ok" : "! ")} 暗区(≤255/{fullScale})实际范围 {darkest:F4}–{brightestDark:F4},满量程样本={LinearSample(coded, width - 1, 0):F4}");
        if (!darkOk) failures++;
        return failures;
    }

    /// <summary>取 RGBA 半浮点码值图的 R 通道(纯灰样本三通道相同)。</summary>
    private static float LinearSample(DecodedImage image, int x, int y)
    {
        var offset = (y * image.Stride) + (x * 8);
        return (float)BitConverter.UInt16BitsToHalf((ushort)(image.Pixels[offset] | (image.Pixels[offset + 1] << 8)));
    }

    private static int RunFile(string file, List<object> report)
    {
        var name = Path.GetFileName(file);
        var bytes = File.ReadAllBytes(file);
        var container = HeifContainer.TryParse(bytes, out var parseReason);
        if (container is null)
        {
            Console.WriteLine($"! {name}: 容器解析失败 {parseReason}");
            return 1;
        }

        if (container.Width != ChartWidth || container.Height != ChartHeight)
        {
            Console.WriteLine($"{name}: 尺寸 {container.Width}x{container.Height} 不是色卡夹具,跳过(期望 {ChartWidth}x{ChartHeight})");
            return 0;
        }

        var icc = container.Color.IccProfile;
        var sourceSpace = SourceSpaceOf(container.Color, icc);
        Console.WriteLine($"{name}: color=[{container.Color}] 期望来源={(icc is { Length: > 0 } ? "ICC" : "nclx")}");

        // 快速路径(未旋转产物,方向为容器声明;夹具方向为 1)。
        var fast = FastHeifDecoder.TryDecode(bytes, null, PixelEncoding.RgbaF16, CancellationToken.None, out var fastReason);
        if (fast is null)
        {
            Console.WriteLine($"  ! 快速路径失败: {fastReason}");
            return 1;
        }

        // Windows libheif 主图回落(显示朝向;夹具方向为 1,尺寸可直接对齐)。
        var fallback = LibHeifDecoder.DecodePresentationAsync(new LocalFileRef(file), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (fallback is null)
        {
            fast.Release();
            Console.WriteLine($"  ! libheif 回落失败: {HeifLoader.LastDecodeError}");
            return 1;
        }

        // 强制 48 位通道(对 8 位夹具传 10,libheif 会给 10 位码值):核对字节序与码值归一化。
        var wide = LibHeifDecoder.DecodePresentationForTest(bytes, null, 10);

        try
        {
            if (fast.Width != fallback.Width || fast.Height != fallback.Height)
            {
                Console.WriteLine($"  ! 尺寸不一致: fast {fast.Width}x{fast.Height} vs fallback {fallback.Width}x{fallback.Height}");
                return 1;
            }

            var failures = 0;
            var blocks = new List<object>();
            var iccMatters = 0f;
            foreach (var block in Blocks())
            {
                var mine = BlockMean(fast, block);
                var other = BlockMean(fallback, block);
                var expected = ExpectedLinear(sourceSpace, block.R, block.G, block.B);

                // 该夹具若被当成 sRGB 解释,期望值会偏多少:用来证明下面的断言确实能抓到"丢 ICC/丢原色"。
                if (sourceSpace is not null)
                {
                    using var srgb = SKColorSpace.CreateSrgb();
                    var assumed = ExpectedLinear(srgb, block.R, block.G, block.B);
                    iccMatters = Math.Max(iccMatters, MaxDiff(expected, assumed));
                }

                var agreement = MaxDiff(mine, other);
                var accuracy = MaxDiff(mine, expected);
                var accuracyFallback = MaxDiff(other, expected);
                var tolerance = block.Tolerance;

                var ok = agreement <= tolerance && accuracy <= tolerance && accuracyFallback <= tolerance;
                if (!ok) failures++;

                Console.WriteLine(
                    $"  {(ok ? "ok" : "! ")} {block.Name,-16} fast=({mine.R:F4},{mine.G:F4},{mine.B:F4}) "
                    + $"fallback=({other.R:F4},{other.G:F4},{other.B:F4}) 期望=({expected.R:F4},{expected.G:F4},{expected.B:F4}) "
                    + $"Δfast/fallback={agreement:F4} Δfast/期望={accuracy:F4} Δfallback/期望={accuracyFallback:F4} (容差 {tolerance:F3})");

                blocks.Add(new
                {
                    block = block.Name,
                    fast = Round(mine),
                    fallback = Round(other),
                    expected = Round(expected),
                    agreement = Math.Round(agreement, 4),
                    accuracy = Math.Round(accuracy, 4),
                    fallbackAccuracy = Math.Round(accuracyFallback, 4),
                    tolerance,
                });
            }

            if (wide is not null)
            {
                // 48 位通道必须与 8 位通道给出同一颜色:验证 16 位字节序(LE)、码值刻度与半浮点码值转换。
                var wideFailures = 0;
                foreach (var block in Blocks())
                {
                    var narrow = BlockMean(fallback, block);
                    var wideValue = BlockMean(wide, block);
                    var diff = MaxDiff(narrow, wideValue);
                    if (diff > block.Tolerance)
                    {
                        Console.WriteLine($"  ! 48 位通道 {block.Name}: 与 8 位通道相差 {diff:F4}");
                        wideFailures++;
                    }
                }

                failures += wideFailures;
                Console.WriteLine($"  {(wideFailures == 0 ? "ok" : "! ")} 48 位通道(强制 10 位码值)与 8 位通道逐色块一致:{Blocks().Count} 块");
            }
            else
            {
                Console.WriteLine("  ! 48 位通道解码失败");
                failures++;
            }

            report.Add(new
            {
                file = name,
                color = container.Color.ToString(),
                iccBytes = icc?.Length ?? 0,
                colorSpaceMatters = Math.Round(iccMatters, 4),
                blocks,
                pass = failures == 0,
            });

            Console.WriteLine($"  色彩描述若被忽略(当成 sRGB)期望值最多偏 {iccMatters:F4}:"
                + $"{(iccMatters > 0.02f ? "本用例能抓到丢 ICC/丢原色" : "本夹具色域内差异小,靠 convert 层用例覆盖")}");

            return failures;
        }
        finally
        {
            fast.Release();
            fallback.Release();
            wide?.Release();
        }
    }

    /// <summary>色卡上需要断言的平坦区域(样本取块内缩进,避开边缘与文字)。</summary>
    private static List<Block> Blocks()
    {
        var blocks = new List<Block>();
        for (var index = 0; index < Patches.Length; index++)
        {
            var patch = Patches[index];
            var x0 = index % 3 * 400;
            var y0 = index / 3 * 280;
            blocks.Add(new Block(
                patch.Name,
                x0 + 60, y0 + 40, x0 + 340, y0 + 240,
                patch.R, patch.G, patch.B,
                Tolerance: 0.03f));
        }

        // 背景灰 (64,64,64):左下白块右侧的空白区域。
        blocks.Add(new Block("bg gray", 300, 730, 1100, 790, 64, 64, 64, Tolerance: 0.02f));
        // 左下白色方向标记块:避开 "TOP / LEFT" 文字。
        blocks.Add(new Block("white marker", 120, 770, 175, 795, 255, 255, 255, Tolerance: 0.02f));
        // 灰阶斜坡:中性灰跨原色不变,单独校验传递曲线。取单列(斜坡在这一列内就有 ~4 个码值的变化,
        // 横向求平均会把期望值拉偏),纵向取 100 行平摊编码噪声。
        foreach (var column in RampColumns)
        {
            var gray = (int)Math.Round(column * 255.0 / (ChartWidth - 1));
            blocks.Add(new Block($"ramp x={column}", column, 600, column + 1, 700, gray, gray, gray, Tolerance: 0.02f));
        }

        return blocks;
    }

    /// <summary>块内逐通道平均(线性 F16)。</summary>
    private static (float R, float G, float B) BlockMean(DecodedImage image, Block block)
    {
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = block.Y0; y < block.Y1; y++)
        {
            for (var x = block.X0; x < block.X1; x++)
            {
                var offset = (y * image.Stride) + (x * 8);
                r += (float)BitConverter.UInt16BitsToHalf((ushort)(image.Pixels[offset] | (image.Pixels[offset + 1] << 8)));
                g += (float)BitConverter.UInt16BitsToHalf((ushort)(image.Pixels[offset + 2] | (image.Pixels[offset + 3] << 8)));
                b += (float)BitConverter.UInt16BitsToHalf((ushort)(image.Pixels[offset + 4] | (image.Pixels[offset + 5] << 8)));
                count++;
            }
        }

        return ((float)(r / count), (float)(g / count), (float)(b / count));
    }

    /// <summary>码值 → 线性 sRGB,走参考实现(skcms):ICC 用 profile,其余按 nclx 的原色/传递函数。</summary>
    private static (float R, float G, float B) ExpectedLinear(SKColorSpace? space, int r, int g, int b)
    {
        var bgra = new[] { (byte)b, (byte)g, (byte)r, (byte)255 };
        var linear = Reference.ToLinearF16WithSpace(bgra, 1, 1, space);
        var rHalf = (Half)BitConverter.UInt16BitsToHalf((ushort)(linear[0] | (linear[1] << 8)));
        var gHalf = (Half)BitConverter.UInt16BitsToHalf((ushort)(linear[2] | (linear[3] << 8)));
        var bHalf = (Half)BitConverter.UInt16BitsToHalf((ushort)(linear[4] | (linear[5] << 8)));
        return ((float)rHalf, (float)gHalf, (float)bHalf);
    }

    /// <summary>按容器色彩描述构造参考 sRGB/skcms 源空间:ICC 优先,其次 nclx 原色与传递函数。</summary>
    private static SKColorSpace? SourceSpaceOf(HeifColorInfo color, byte[]? icc)
    {
        if (icc is { Length: > 0 }) return SKColorSpace.CreateIcc(icc);
        if (!color.HasNclx) return SKColorSpace.CreateSrgb();

        var xyz = color.Primaries switch
        {
            9 => SKColorSpaceXyz.Rec2020,
            12 => SKColorSpaceXyz.DisplayP3,
            _ => SKColorSpaceXyz.Srgb,
        };
        var transfer = color.Transfer switch
        {
            1 or 6 or 14 or 15 => SKColorSpaceTransferFn.Rec2020,
            4 => new SKColorSpaceTransferFn { G = 2.2f, A = 1f },
            5 => new SKColorSpaceTransferFn { G = 2.8f, A = 1f },
            _ => SKColorSpaceTransferFn.Srgb,
        };
        return SKColorSpace.CreateRgb(transfer, xyz);
    }

    private static float MaxDiff((float R, float G, float B) a, (float R, float G, float B) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static object Round((float R, float G, float B) value)
        => new { r = Math.Round(value.R, 4), g = Math.Round(value.G, 4), b = Math.Round(value.B, 4) };

    private static List<string> ResolveFiles(string[] paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                foreach (var candidate in Directory.EnumerateFiles(full))
                {
                    var extension = Path.GetExtension(candidate);
                    if (extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(candidate);
                    }
                }
            }
            else if (File.Exists(full))
            {
                result.Add(full);
            }
            else
            {
                Console.Error.WriteLine("missing path: " + path);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private sealed record Block(
        string Name,
        int X0,
        int Y0,
        int X1,
        int Y1,
        int R,
        int G,
        int B,
        float Tolerance);
}
