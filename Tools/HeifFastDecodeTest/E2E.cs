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
        failures += RunFallbackExits(files, report);
        failures += RunForcedBitDepthExits(files);
        failures += RunRejectedColorDeclarations(files);
        failures += RunRotatedContainerGeometry(files);
        failures += RunLeaseAndCancellation(files);
        failures += RunOrientationContract(files);
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

    /// <summary>
    /// 共享 libheif 回落的 8 位出口(缩略图 sRGB8 与 Android 展示 RGBA8):
    /// 同一份实现解出的 BGRA32-sRGB、RGBA8-sRGB、RGBA8-DisplayP3 三种产物,逐色块对照独立 skcms 参考。
    /// </summary>
    /// <remarks>
    /// 三个出口共用同一段标签判定与码值转换,任一处误把广色域码值当 sRGB(或反之)都会在这里暴露:
    /// sRGB 出口按参考 sRGB 8 位值判定,P3 出口按参考 Display P3 8 位值判定。
    /// </remarks>
    /// <param name="files">夹具文件。</param>
    /// <param name="report">报告条目。</param>
    /// <returns>失败项数。</returns>
    private static int RunFallbackExits(List<string> files, List<object> report)
    {
        var failures = 0;
        using var srgbSpace = SKColorSpace.CreateSrgb();
        using var p3Space = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;

            var sourceSpace = SourceSpaceOf(container.Color, container.Color.IccProfile);
            Console.WriteLine($"{name}: 共享回落 8 位出口(source={(container.Color.HasIccProfile ? "ICC" : "nclx")})");

            using var bgra = LibHeifColorDecoder.Decode(bytes, null, LibHeifOutput.Bgra32Srgb, CancellationToken.None, out var bgraReason);
            using var rgbaSrgb = LibHeifColorDecoder.Decode(bytes, null, LibHeifOutput.Rgba8Srgb, CancellationToken.None, out var srgbReason);
            using var rgbaP3 = LibHeifColorDecoder.Decode(bytes, null, LibHeifOutput.Rgba8DisplayP3, CancellationToken.None, out var p3Reason);
            if (bgra is null || rgbaSrgb is null || rgbaP3 is null)
            {
                Console.WriteLine($"  ! 解码失败 bgra={bgraReason} srgb={srgbReason} p3={p3Reason}");
                failures++;
                continue;
            }

            var shapesOk = bgra.Encoding == PixelEncoding.Bgra32 && bgra.ColorSpace == ImageColorSpace.Srgb
                && rgbaSrgb.Encoding == PixelEncoding.Rgba8 && rgbaSrgb.ColorSpace == ImageColorSpace.Srgb
                && rgbaP3.Encoding == PixelEncoding.Rgba8 && rgbaP3.ColorSpace == ImageColorSpace.DisplayP3
                && bgra.Orientation == 1 && rgbaSrgb.Orientation == 1 && rgbaP3.Orientation == 1
                && bgra.Width == ChartWidth && bgra.Height == ChartHeight
                && bgra.SourceWidth == ChartWidth && bgra.SourceHeight == ChartHeight;
            Console.WriteLine($"  {(shapesOk ? "ok" : "! ")} 出口形态 {bgra.Encoding}/{bgra.ColorSpace}、"
                + $"{rgbaSrgb.Encoding}/{rgbaSrgb.ColorSpace}、{rgbaP3.Encoding}/{rgbaP3.ColorSpace}，方向 1，"
                + $"源 {bgra.SourceWidth}x{bgra.SourceHeight}");
            if (!shapesOk) failures++;

            var worst = new[] { 0, 0, 0 };
            var channelsAgree = 0;
            var blocks = new List<object>();
            foreach (var block in Blocks())
            {
                var expectSrgb = Expected8(sourceSpace, block.R, block.G, block.B, srgbSpace);
                var expectP3 = Expected8(sourceSpace, block.R, block.G, block.B, p3Space);
                var actualBgra = BlockMean8(bgra, block);
                var actualSrgb = BlockMean8(rgbaSrgb, block);
                var actualP3 = BlockMean8(rgbaP3, block);

                var dBgra = MaxDiff(actualBgra, expectSrgb);
                var dSrgb = MaxDiff(actualSrgb, expectSrgb);
                var dP3 = MaxDiff(actualP3, expectP3);
                // 同一色彩的两个 8 位出口只差通道序:BGRA32 与 RGBA8 必须给出同一颜色。
                var dBgraVsRgba = MaxDiff(actualBgra, actualSrgb);
                if (dBgraVsRgba > 1) channelsAgree++;

                worst[0] = Math.Max(worst[0], (int)Math.Ceiling(dBgra));
                worst[1] = Math.Max(worst[1], (int)Math.Ceiling(dSrgb));
                worst[2] = Math.Max(worst[2], (int)Math.Ceiling(dP3));
                var ok = dBgra <= ExitTolerance && dSrgb <= ExitTolerance && dP3 <= ExitTolerance && dBgraVsRgba <= 1;
                if (!ok) failures++;
                Console.WriteLine($"  {(ok ? "ok" : "! ")} {block.Name,-16} bgra-sRGB=({actualBgra.R:F0},{actualBgra.G:F0},{actualBgra.B:F0}) "
                    + $"rgba-sRGB=({actualSrgb.R:F0},{actualSrgb.G:F0},{actualSrgb.B:F0}) "
                    + $"rgba-P3=({actualP3.R:F0},{actualP3.G:F0},{actualP3.B:F0}) "
                    + $"期望 sRGB=({expectSrgb.R},{expectSrgb.G},{expectSrgb.B}) P3=({expectP3.R},{expectP3.G},{expectP3.B}) "
                    + $"Δ={dBgra:F1}/{dSrgb:F1}/{dP3:F1}/{dBgraVsRgba:F1}");

                blocks.Add(new
                {
                    block = block.Name,
                    bgraSrgb = Round8(actualBgra),
                    rgbaSrgb = Round8(actualSrgb),
                    rgbaP3 = Round8(actualP3),
                    expectedSrgb = Round8(expectSrgb),
                    expectedP3 = Round8(expectP3),
                    delta = new[]
                    {
                        Math.Round(dBgra, 2), Math.Round(dSrgb, 2), Math.Round(dP3, 2), Math.Round(dBgraVsRgba, 2),
                    },
                });
            }

            var pass = worst[0] <= ExitTolerance && worst[1] <= ExitTolerance && worst[2] <= ExitTolerance && channelsAgree == 0;
            Console.WriteLine($"  {(pass ? "ok" : "! ")} 8 位出口最大偏差 bgra/sRGB={worst[0]} rgba/sRGB={worst[1]} rgba/P3={worst[2]} "
                + $"(容差 {ExitTolerance}),BGRA32↔RGBA8 通道序不一致 {channelsAgree} 块");
            report.Add(new
            {
                file = name,
                exits = "bgra-srgb/rgba8-srgb/rgba8-p3",
                worst,
                blocks,
                pass,
            });
        }

        return failures;
    }

    /// <summary>
    /// 容器把传输特性声明成 PQ/HLG 时必须拒绝:色彩支持面判定给出拒绝、实测解码返回 null 并写清原因,
    /// 且快速路径同样解不动 —— 两条路径都不许把 HDR 码值当 SDR 输出。
    /// </summary>
    /// <param name="files">夹具文件(取带 nclx 的那个)。</param>
    /// <returns>失败项数。</returns>
    private static int RunRejectedColorDeclarations(List<string> files)
    {
        string? target = null;
        byte[]? source = null;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;
            if (!container.Color.HasNclx || container.Color.HasIccProfile) continue;
            target = file;
            source = bytes;
            break;
        }

        if (target is null || source is null)
        {
            Console.WriteLine("HDR 声明拒绝:没有带 nclx 的色卡夹具,跳过");
            return 0;
        }

        var failures = 0;
        foreach (var (transfer, label) in new[] { ((ushort)16, "PQ"), ((ushort)18, "HLG") })
        {
            var patched = PatchNclxTransfer(source, transfer);
            var container = HeifContainer.TryParse(patched, out var parseReason);
            var patchedOk = container is not null && container.Color.Transfer == transfer;
            var verdict = LibHeifColorDecoder.InspectColor(patched, out var verdictReason);
            HeifLoader.SetLastDecodeError(null);
            using var decoded = LibHeifColorDecoder.Decode(patched, null, LibHeifOutput.Rgba8Srgb, CancellationToken.None, out var decodeReason);
            var fast = FastHeifDecoder.TryDecode(patched, null, PixelEncoding.Rgba8, CancellationToken.None, out var fastReason);
            fast?.Release();

            var lastError = HeifLoader.LastDecodeError ?? string.Empty;
            var ok = patchedOk
                && verdict == LibHeifColorVerdict.Rejected
                && decoded is null
                && decodeReason is not null
                && lastError.Contains("HDR", StringComparison.Ordinal)
                && fast is null;
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "ok" : "! ")} {Path.GetFileName(target)} 声明 {label}({transfer}):"
                + $" 容器={(patchedOk ? "已改" : "未改成:" + parseReason)} 判定={verdict}({verdictReason}) "
                + $"回落={(decoded is null ? "拒绝" : "返回产物")} 原因={decodeReason} 最后错误={lastError} "
                + $"快速路径={(fast is null ? "拒绝" : "返回产物")}({fastReason})");
        }

        return failures;
    }

    /// <summary>
    /// 容器 <c>irot</c> 非 0 时:回落产物必须是 libheif 真实变换后的显示朝向
    /// (方向 1、画布宽高互换、源几何同步互换、代理缩放保留全幅几何),不按 EXIF 猜方向;
    /// 另用色卡左下白块核对变换确实作用在像素上(色卡 1200x800,白块原在左下)。
    /// </summary>
    /// <param name="files">夹具文件(取第一个色卡)。</param>
    /// <returns>失败项数。</returns>
    private static int RunRotatedContainerGeometry(List<string> files)
    {
        string? target = null;
        byte[]? source = null;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;
            target = file;
            source = bytes;
            break;
        }

        if (target is null || source is null)
        {
            Console.WriteLine("irot 几何:没有色卡夹具,跳过");
            return 0;
        }

        var failures = 0;
        using var original = LibHeifColorDecoder.Decode(source, null, LibHeifOutput.Bgra32Srgb, CancellationToken.None, out var originalReason);
        if (original is null)
        {
            Console.WriteLine($"  ! 未旋转基线解码失败: {originalReason}");
            return 1;
        }

        var patched = PatchIrot(source, 1);            // irot = 1 → 逆时针 90°
        var patchedContainer = HeifContainer.TryParse(patched, out var parseReason);
        var containerOk = patchedContainer is not null && patchedContainer.ExifOrientation != 1;
        using var rotated = LibHeifColorDecoder.Decode(patched, null, LibHeifOutput.Bgra32Srgb, CancellationToken.None, out var rotatedReason);
        using var proxy = LibHeifColorDecoder.Decode(patched, 600, LibHeifOutput.Bgra32Srgb, CancellationToken.None, out var proxyReason);
        if (rotated is null || proxy is null)
        {
            Console.WriteLine($"  ! 旋转夹具解码失败 rotated={rotatedReason} proxy={proxyReason}");
            return 1 + (containerOk ? 0 : 1);
        }

        var geometryOk = containerOk
            && rotated.Orientation == 1
            && rotated.Width == original.Height && rotated.Height == original.Width
            && rotated.SourceWidth == original.Height && rotated.SourceHeight == original.Width
            && proxy.Orientation == 1
            && proxy.SourceWidth == original.Height && proxy.SourceHeight == original.Width
            && Math.Max(proxy.Width, proxy.Height) <= 601;
        if (!geometryOk) failures++;
        Console.WriteLine($"  {(geometryOk ? "ok" : "! ")} 源 {original.Width}x{original.Height} → 回落 {rotated.Width}x{rotated.Height}"
            + $"(方向 {rotated.Orientation},源几何 {rotated.SourceWidth}x{rotated.SourceHeight}),"
            + $"代理 {proxy.Width}x{proxy.Height}(源几何 {proxy.SourceWidth}x{proxy.SourceHeight}),"
            + $"容器方向={(patchedContainer is null ? parseReason : patchedContainer.ExifOrientation.ToString())}");

        // 色卡四角(1200x800):左上红、右上蓝、左下白块、右下背景灰。
        // 逆时针 90° 后四角整体轮转:新左上=旧右上(蓝)、新右上=旧右下(背景灰)、
        // 新左下=旧左上(红)、新右下=旧左下(白块)。四条同时成立才说明变换真的作用在像素上。
        var topLeft = PixelAt(rotated, 40, 60);
        var topRight = PixelAt(rotated, rotated.Width - 40, 60);
        var bottomLeft = PixelAt(rotated, 40, rotated.Height - 100);
        var bottomRight = PixelAt(rotated, rotated.Width - 40, rotated.Height - 100);
        var directionOk = IsNear(bottomRight, 255, 255, 255)
            && IsNear(topLeft, 0, 0, 255)
            && IsNear(bottomLeft, 255, 0, 0)
            && IsNear(topRight, 64, 64, 64, 12);
        if (!directionOk) failures++;
        Console.WriteLine($"  {(directionOk ? "ok" : "! ")} 四角轮转:左上={Describe(topLeft)}(期望蓝) "
            + $"右上={Describe(topRight)}(期望背景灰) 左下={Describe(bottomLeft)}(期望红) "
            + $"右下={Describe(bottomRight)}(期望白块)");

        return failures;
    }

    /// <summary>8 位出口对照容差(两条路径都走 skcms,取值差异只来自量化与行对齐)。</summary>
    private const int ExitTolerance = 3;

    /// <summary>
    /// 强制高位深解码(48 位平面)后仍必须落回正确编码:8 位出口拿到的是 BGRA/RGBA 而不是半浮点中间图,
    /// 且逐色块与同一夹具的 8 位路径一致。零拷贝快通道只能复用**同为 8 位**的中间图 —— 高位深源的
    /// 中间图是 RGBA16F(标签也可能是 sRGB),误复用会把 F16 当 BGRA32 交出去。
    /// </summary>
    private static int RunForcedBitDepthExits(List<string> files)
    {
        var failures = 0;
        Console.WriteLine("强制 10 位解码的 8 位出口:");
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;

            foreach (var output in new[] { LibHeifOutput.Bgra32Srgb, LibHeifOutput.Rgba8Srgb, LibHeifOutput.Rgba8DisplayP3 })
            {
                using var narrow = LibHeifColorDecoder.Decode(bytes, null, output, CancellationToken.None, out var narrowReason);
                using var wide = LibHeifColorDecoder.Decode(bytes, null, output, forcedBitDepth: 10, CancellationToken.None, out var wideReason);
                var expectedEncoding = output == LibHeifOutput.Bgra32Srgb ? PixelEncoding.Bgra32 : PixelEncoding.Rgba8;
                if (narrow is null || wide is null)
                {
                    Console.WriteLine($"  ! {name} {output}: 解码失败 narrow={narrowReason} wide={wideReason}");
                    failures++;
                    continue;
                }

                var shapeOk = wide.Encoding == expectedEncoding && narrow.Encoding == expectedEncoding
                    && wide.ColorSpace == narrow.ColorSpace && wide.Width == narrow.Width && wide.Height == narrow.Height
                    && wide.Orientation == 1 && wide.SourceWidth == narrow.SourceWidth;
                var worst = 0;
                foreach (var block in Blocks())
                {
                    worst = Math.Max(worst, (int)Math.Ceiling(MaxDiff(BlockMean8(wide, block), BlockMean8(narrow, block))));
                }
                var ok = shapeOk && worst <= ExitTolerance;
                if (!ok) failures++;
                Console.WriteLine($"  {(ok ? "ok" : "! ")} {name} {output}: 编码 {wide.Encoding}/{wide.ColorSpace} "
                    + $"{(shapeOk ? "" : "（形态不符）")}与 8 位路径逐色块最大偏差={worst}（容差 {ExitTolerance}）");
            }
        }
        return failures;
    }

    /// <summary>
    /// 租约与取消回归:预取消的令牌必须抛 <see cref="OperationCanceledException"/>(不返回 null、不写解码错误);
    /// 代理与全幅两条成功路径的产物释放后,缓冲必须真的回到 <see cref="ImageBufferPool"/>
    /// (解码中途创建的全幅图与缩放产物都要有主,漏掉一份就会在这里暴露为池增量不足)。
    /// </summary>
    private static int RunLeaseAndCancellation(List<string> files)
    {
        byte[]? source = null;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;
            source = bytes;
            break;
        }

        if (source is null)
        {
            Console.WriteLine("租约与取消:没有色卡夹具,跳过");
            return 0;
        }

        var failures = 0;
        Console.WriteLine("租约与取消:");

        // 1) 预取消:必须是取消异常,不能静默返回 null 或把它记成解码错误。
        HeifLoader.SetLastDecodeError("sentinel");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var thrown = false;
            try
            {
                LibHeifColorDecoder.Decode(source, 600, LibHeifOutput.Bgra32Srgb, cancelled.Token, out _)?.Release();
            }
            catch (OperationCanceledException)
            {
                thrown = true;
            }
            var lastError = HeifLoader.LastDecodeError;
            var ok = thrown && lastError == "sentinel";
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "ok" : "! ")} 预取消令牌:{(thrown ? "抛取消" : "未抛取消")}"
                + $" LastDecodeError={(lastError == "sentinel" ? "未被改写" : lastError)}");
        }

        // 2) 成功路径的租约回收:清空池后按产物字节核对回到池里的增量。
        failures += CheckLeaseReturn(source, 600, "代理");
        failures += CheckLeaseReturn(source, null, "全幅");
        return failures;
    }

    /// <summary>解一张并释放,核对缓冲真的回到池(增量 ≥ 产物字节)。</summary>
    private static int CheckLeaseReturn(byte[] data, int? maxLongEdge, string label)
    {
        ImageBufferPool.ReleaseIdle(long.MaxValue);
        var before = ImageBufferPool.IdleBytes;
        var productBytes = 0;
        using (var image = LibHeifColorDecoder.Decode(data, maxLongEdge, LibHeifOutput.Bgra32Srgb, CancellationToken.None, out var reason))
        {
            if (image is null)
            {
                Console.WriteLine($"  ! {label}: 解码失败（{reason}）");
                return 1;
            }
            productBytes = checked(image.Stride * image.Height);
            if (image.Pixels.Length < productBytes)
            {
                Console.WriteLine($"  ! {label}: 产物缓冲不足 {image.Pixels.Length} < {productBytes}");
                return 1;
            }
        }

        var grew = ImageBufferPool.IdleBytes - before;
        var ok = grew >= productBytes;
        Console.WriteLine($"  {(ok ? "ok" : "! ")} {label}释放后回到池 {grew} 字节（产物 {productBytes} 字节）");
        return ok ? 0 : 1;
    }


    /// <summary>
    /// IHeifDecoder 入口的方向契约:产物一律显示朝向(方向 1),不再让 <see cref="ThumbnailService"/> 二次旋转。
    /// 容器 <c>irot</c> = 1/2/3(逆时针 90/180/270)各验一次代理与全幅几何 + 色卡四角;另经真实
    /// <see cref="ThumbnailService.GetThumbnailAsync"/> 核 180 一例(该角度没有横竖启发式保护,双转会直接暴露)。
    /// </summary>
    private static int RunOrientationContract(List<string> files)
    {
        byte[]? source = null;
        string? target = null;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var container = HeifContainer.TryParse(bytes, out _);
            if (container is null || container.Width != ChartWidth || container.Height != ChartHeight) continue;
            source = bytes;
            target = file;
            break;
        }

        if (source is null || target is null)
        {
            Console.WriteLine("方向契约:没有色卡夹具,跳过");
            return 0;
        }

        var failures = 0;
        HeifLoader.Initialize(new LibHeifDecoder());
        var decoder = new LibHeifDecoder();
        var preRotatedOk = HeifLoader.OutputPreRotated && decoder.OutputPreRotated;
        if (!preRotatedOk) failures++;
        Console.WriteLine($"  {(preRotatedOk ? "ok" : "! ")} HeifLoader.OutputPreRotated={HeifLoader.OutputPreRotated}"
            + $"(解码器 {decoder.OutputPreRotated})");
        Console.WriteLine($"方向契约({Path.GetFileName(target)}):");

        var folder = Path.Combine(Path.GetTempPath(), "pv-heif-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string? path180 = null;
            for (byte angle = 1; angle <= 3; angle++)
            {
                var path = Path.Combine(folder, $"irot-{angle}.heic");
                File.WriteAllBytes(path, PatchIrot(source, angle));
                if (angle == 2) path180 = path;

                using var proxy = decoder.DecodeAsync(new LocalFileRef(path), 600).GetAwaiter().GetResult();
                using var full = decoder.DecodeAsync(new LocalFileRef(path), null).GetAwaiter().GetResult();
                if (proxy is null || full is null)
                {
                    Console.WriteLine($"  ! irot={angle}: 解码失败（{HeifLoader.LastDecodeError}）");
                    failures++;
                    continue;
                }

                var quarter = angle != 2;
                var geometryOk = proxy.Orientation == 1 && full.Orientation == 1
                    && (quarter
                        ? full.Width == ChartHeight && full.Height == ChartWidth
                        : full.Width == ChartWidth && full.Height == ChartHeight)
                    && proxy.SourceWidth == full.Width && proxy.SourceHeight == full.Height
                    && Math.Max(proxy.Width, proxy.Height) <= 601;
                var corners = ExpectedCorners(angle);
                var cornerOk = CornersMatch(proxy, corners);
                if (!geometryOk || !cornerOk) failures++;
                Console.WriteLine($"  {(geometryOk && cornerOk ? "ok" : "! ")} irot={angle}"
                    + $"({(quarter ? "90/270" : "180")}): 代理 {proxy.Width}x{proxy.Height} 全幅 {full.Width}x{full.Height}"
                    + $"(方向 {proxy.Orientation},源 {proxy.SourceWidth}x{proxy.SourceHeight})"
                    + $"{(cornerOk ? " 四角正确" : " 四角不符:" + DescribeCorners(proxy))}");
            }

            if (path180 is null)
            {
                Console.WriteLine("  ! 缺少 180 度夹具");
                failures++;
            }
            else
            {
                // 真实 ThumbnailService:该来源声明已预旋转,不得再按容器方向转 180。
                using var thumbnail = ThumbnailService.GetThumbnailAsync(new LocalFileRef(path180), 260)
                    .GetAwaiter().GetResult();
                if (thumbnail is null)
                {
                    Console.WriteLine("  ! ThumbnailService 180: 返回 null");
                    failures++;
                }
                else
                {
                    var corners = ExpectedCorners(2);
                    var ok = CornersMatch(thumbnail, corners);
                    if (!ok) failures++;
                    Console.WriteLine($"  {(ok ? "ok" : "! ")} ThumbnailService(irot=2) {thumbnail.Width}x{thumbnail.Height} "
                        + $"{thumbnail.Encoding}/{thumbnail.ColorSpace}:{(ok ? " 白块在右上(未二次旋转)" : " 四角不符:" + DescribeCorners(thumbnail))}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 临时目录留残留不影响结论 */ }
        }

        return failures;
    }

    /// <summary>色卡四角(左上红、右上蓝、左下白块、右下背景灰)逆时针 angle×90° 后各角应有的颜色。</summary>
    private static ((int R, int G, int B) TL, (int R, int G, int B) TR, (int R, int G, int B) BL, (int R, int G, int B) BR)
        ExpectedCorners(int angle) => angle switch
        {
            1 => ((0, 0, 255), (64, 64, 64), (255, 0, 0), (255, 255, 255)),
            2 => ((64, 64, 64), (255, 255, 255), (0, 0, 255), (255, 0, 0)),
            3 => ((255, 255, 255), (255, 0, 0), (64, 64, 64), (0, 0, 255)),
            _ => ((255, 0, 0), (0, 0, 255), (255, 255, 255), (64, 64, 64)),
        };

    /// <summary>按四角内容核对显示朝向(归一化取样点,避开色卡上的文字,缩略图尺寸同样适用)。</summary>
    private static bool CornersMatch(DecodedImage image,
        ((int R, int G, int B) TL, (int R, int G, int B) TR, (int R, int G, int B) BL, (int R, int G, int B) BR) corners)
    {
        if (image.Width < 150 || image.Height < 100) return false;
        return IsNear(CornerPixel(image, 0.06, 0.06), corners.TL, 12)
            && IsNear(CornerPixel(image, 0.94, 0.06), corners.TR, 12)
            && IsNear(CornerPixel(image, 0.06, 0.95), corners.BL, 12)
            && IsNear(CornerPixel(image, 0.94, 0.95), corners.BR, 12);
    }

    /// <summary>四角实测值的报告文本。</summary>
    private static string DescribeCorners(DecodedImage image)
        => $"左上={Describe(CornerPixel(image, 0.06, 0.06))} "
            + $"右上={Describe(CornerPixel(image, 0.94, 0.06))} "
            + $"左下={Describe(CornerPixel(image, 0.06, 0.95))} "
            + $"右下={Describe(CornerPixel(image, 0.94, 0.95))}";

    /// <summary>按归一化坐标取像素。</summary>
    private static (byte R, byte G, byte B) CornerPixel(DecodedImage image, double u, double v)
    {
        var x = Math.Clamp((int)Math.Round(u * (image.Width - 1)), 0, image.Width - 1);
        var y = Math.Clamp((int)Math.Round(v * (image.Height - 1)), 0, image.Height - 1);
        return PixelAt(image, x, y);
    }

    /// <summary>像素是否接近给定 RGB(逐通道容差)。</summary>
    private static bool IsNear((byte R, byte G, byte B) color, (int R, int G, int B) expected, int tolerance)
        => Math.Abs(color.R - expected.R) <= tolerance
            && Math.Abs(color.G - expected.G) <= tolerance
            && Math.Abs(color.B - expected.B) <= tolerance;


    /// <summary>块的 8 位平均色(Bgra32 或 Rgba8)。</summary>
    /// <param name="image">8 位图像。</param>
    /// <param name="block">色块。</param>
    /// <returns>三通道平均。</returns>
    private static (float R, float G, float B) BlockMean8(DecodedImage image, Block block)
    {
        if (image.Encoding is not (PixelEncoding.Bgra32 or PixelEncoding.Rgba8))
            throw new NotSupportedException($"8 位出口只按 BGRA/RGBA 读像素,实际 {image.Encoding}");
        var bytes = DecodedImage.BytesPerPixel(image.Encoding);
        var rgba = image.Encoding == PixelEncoding.Rgba8;
        var pixels = image.Pixels;
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = block.Y0; y < block.Y1; y++)
        {
            var row = (long)y * image.Stride;
            for (var x = block.X0; x < block.X1; x++)
            {
                var at = row + (long)x * bytes;
                var color = rgba
                    ? (pixels[at], pixels[at + 1], pixels[at + 2])
                    : (pixels[at + 2], pixels[at + 1], pixels[at]);
                r += color.Item1;
                g += color.Item2;
                b += color.Item3;
                count++;
            }
        }

        return ((float)(r / count), (float)(g / count), (float)(b / count));
    }

    /// <summary>码值 → 目标色彩空间的 8 位值,走独立 skcms 参考(1×1 转换)。</summary>
    /// <param name="space">源色彩空间。</param>
    /// <param name="r">源码值 R。</param>
    /// <param name="g">源码值 G。</param>
    /// <param name="b">源码值 B。</param>
    /// <param name="target">目标色彩空间。</param>
    /// <returns>目标空间的 8 位值。</returns>
    private static (int R, int G, int B) Expected8(SKColorSpace? space, int r, int g, int b, SKColorSpace target)
    {
        var bgra = new[] { (byte)b, (byte)g, (byte)r, (byte)255 };
        var converted = Reference.ToBgra8WithSpace(bgra, 1, 1, space, target);
        return (converted[2], converted[1], converted[0]);
    }

    /// <summary>8 位图像的指定像素(Bgra32/Rgba8;Bgra32 返回 RGB 序)。</summary>
    /// <param name="image">8 位图像。</param>
    /// <param name="x">横坐标。</param>
    /// <param name="y">纵坐标。</param>
    /// <returns>RGB 字节。</returns>
    private static (byte R, byte G, byte B) PixelAt(DecodedImage image, int x, int y)
    {
        var at = ((long)y * image.Stride) + ((long)x * DecodedImage.BytesPerPixel(image.Encoding));
        var pixels = image.Pixels;
        return image.Encoding == PixelEncoding.Rgba8
            ? (pixels[at], pixels[at + 1], pixels[at + 2])
            : (pixels[at + 2], pixels[at + 1], pixels[at]);
    }

    /// <summary>像素的可读描述。</summary>
    /// <param name="color">RGB 字节。</param>
    /// <returns>紧凑文本。</returns>
    private static string Describe((byte R, byte G, byte B) color) => $"({color.R},{color.G},{color.B})";

    /// <summary>像素是否接近给定 RGB(逐通道容差)。</summary>
    /// <param name="color">实测像素。</param>
    /// <param name="r">期望红。</param>
    /// <param name="g">期望绿。</param>
    /// <param name="b">期望蓝。</param>
    /// <param name="tolerance">每通道容差。</param>
    /// <returns>在容差内为 true。</returns>
    private static bool IsNear((byte R, byte G, byte B) color, int r, int g, int b, int tolerance = 4)
        => Math.Abs(color.R - r) <= tolerance && Math.Abs(color.G - g) <= tolerance && Math.Abs(color.B - b) <= tolerance;

    /// <summary>8 位三元组四舍五入(报告用)。</summary>
    /// <param name="value">三通道值。</param>
    /// <returns>紧凑对象。</returns>
    private static (int R, int G, int B) Round8((float R, float G, float B) value)
        => ((int)Math.Round(value.R), (int)Math.Round(value.G), (int)Math.Round(value.B));

    /// <summary>把每个 <c>nclx</c> 的传输特性改成给定代码(其余字节不动),用于构造 HDR 声明。</summary>
    /// <param name="data">原始文件字节。</param>
    /// <param name="transfer">目标传输特性代码。</param>
    /// <returns>改过的副本。</returns>
    private static byte[] PatchNclxTransfer(byte[] data, ushort transfer)
    {
        var patched = (byte[])data.Clone();
        for (var i = 0; i + 8 <= patched.Length; i++)
        {
            if (patched[i] != (byte)'n' || patched[i + 1] != (byte)'c'
                || patched[i + 2] != (byte)'l' || patched[i + 3] != (byte)'x') continue;
            // 'nclx' 之后依次是 primaries(2)/transfer(2)/matrix(2)/full_range(1)。
            patched[i + 6] = (byte)(transfer >> 8);
            patched[i + 7] = (byte)transfer;
        }
        return patched;
    }

    /// <summary>把每个 <c>irot</c> 的角度改成给定值(单位 90°,其余字节不动)。</summary>
    /// <param name="data">原始文件字节。</param>
    /// <param name="angle">角度码(0-3)。</param>
    /// <returns>改过的副本。</returns>
    private static byte[] PatchIrot(byte[] data, byte angle)
    {
        var patched = (byte[])data.Clone();
        for (var i = 0; i + 5 <= patched.Length; i++)
        {
            if (patched[i] != (byte)'i' || patched[i + 1] != (byte)'r'
                || patched[i + 2] != (byte)'o' || patched[i + 3] != (byte)'t') continue;
            patched[i + 4] = angle;
        }
        return patched;
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
