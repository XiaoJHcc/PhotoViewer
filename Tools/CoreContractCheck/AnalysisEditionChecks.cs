using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using SkiaSharp;

namespace CoreContractCheck;

/// <summary>
/// 分析栏即时计算与 CV 诊断口径：用注入的展示解码器 + 真实 presenter 对照
/// <see cref="ColorEngine.ToAnalysisSrgb8"/> + 真实 CV 运算，核对色彩/方向/尺寸、只读库基线、
/// 按侧现算与即时缓存语义。全部在隔离临时目录与隔离数据库内运行，收尾清缓存并还原注入。
/// </summary>
internal static class AnalysisEditionChecks
{
#if PHOTOVIEWER_DINO
    private static string ModelId => DinoModelResources.ModelId;
#else
    private const string ModelId = "analysis-check-no-dino";
#endif

    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("分析栏即时计算 / CV 诊断口径");
        UiDispatcher.Initialize(new InlineDispatcher());
        var presentation = PresentationInjection.Capture();
        var resources = ResourceInjection.Capture();
        var counting = new CountingResourceProvider();
        resources.Install(counting);
        BitmapLoader.ClearCache();
        AnalysisResultCache.InvalidateAll();
        string directory = Path.Combine(root, "analysis");
        Directory.CreateDirectory(directory);
        try
        {
            string databasePath = PhotoDatabase.DatabasePath;
            if (!Path.GetFullPath(databasePath).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
            {
                c.Fail("分析栏数据库用例", "数据库不在临时根目录内，拒绝写入：" + databasePath);
                return;
            }

            await RunColorChecksAsync(c);
            await RunCvDiagnosticChecksAsync(c, directory);
            await RunPresenterChecksAsync(c, directory, counting);
        }
        catch (Exception exception)
        {
            c.Fail("分析栏用例执行", "未处理的异常：" + exception);
        }
        finally
        {
            BitmapLoader.ClearCache();
            AnalysisResultCache.InvalidateAll();
            SqliteConnection.ClearAllPools();
            resources.Restore();
            presentation.Restore();
        }
    }

    // ---------------------------------------------------------------
    // 1) 分析口径：ToAnalysisSrgb8 的 F16 / P3 / 方向 / 尺寸
    // ---------------------------------------------------------------

    private static Task RunColorChecksAsync(Checks c)
    {
        using (var linear = CreateF16(2, 1, ImageColorSpace.LinearSrgb, orientation: 6, (x, _) =>
                   x == 0 ? (0.5f, 0.25f, 1f) : (0f, 0f, 0f)))
        using (var analysis = ColorEngine.ToAnalysisSrgb8(linear))
        {
            c.True("分析口径：方向 6 交换显示宽高", analysis.Width == 1 && analysis.Height == 2,
                $"实际 {analysis.Width}×{analysis.Height}");
            c.True("分析口径：产物为 sRGB8 且方向已归 1",
                analysis.Encoding == PixelEncoding.Bgra32 && analysis.ColorSpace == ImageColorSpace.Srgb
                && analysis.Orientation == 1,
                $"编码 {analysis.Encoding} / 色彩 {analysis.ColorSpace} / 方向 {analysis.Orientation}");
            var top = ReadBgra(analysis, 0, 0);
            c.True("分析口径：线性 0.5/0.25/1.0 → sRGB8 约 188/137/255",
                Near(top.R, 188, 2) && Near(top.G, 137, 2) && Near(top.B, 255, 1),
                $"实际 R{top.R} G{top.G} B{top.B}");
            var bottom = ReadBgra(analysis, 0, 1);
            c.True("分析口径：方向变换后像素位置正确", bottom.R <= 1 && bottom.G <= 1 && bottom.B <= 1,
                $"实际 R{bottom.R} G{bottom.G} B{bottom.B}");
        }

        using (var p3 = CreateF16(1, 1, ImageColorSpace.DisplayP3, orientation: 1, (_, _) => (0.6f, 0.2f, 0.2f)))
        using (var analysis = ColorEngine.ToAnalysisSrgb8(p3))
        {
            var pixel = ReadBgra(analysis, 0, 0);
            // DisplayP3 = P3 原色 + sRGB 传递函数，像素值是编码值：0.6/0.2/0.2 经基色矩阵换到 sRGB8 约 166/40/46，
            // 若被当成 sRGB 直通则是 153/51/51，两者可区分。
            c.True("分析口径：F16 Display P3 按真实基色换到 sRGB8（0.6/0.2/0.2 → 约 166/40/46）",
                Near(pixel.R, 166, 3) && Near(pixel.G, 40, 3) && Near(pixel.B, 46, 3),
                $"实际 R{pixel.R} G{pixel.G} B{pixel.B}");
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // 2) CV 诊断页：展示解码产物 → ToAnalysisSrgb8 → CV，与独立参考逐字节一致
    // ---------------------------------------------------------------

    private static async Task RunCvDiagnosticChecksAsync(Checks c, string directory)
    {
        string path = Path.Combine(directory, "cv-diagnostic.png");
        WriteStubPng(path);
        var file = new ImageFile(new LocalFileRef(path));
        await file.LoadExifDataAsync();
        await file.LoadBasicPropertiesAsync();

        var decoder = new AnalysisPresentationDecoder
        {
            FullWidth = 240,
            FullHeight = 160,
            SourceWidth = 240,
            SourceHeight = 160,
            Orientation = 6,
            ColorSpace = ImageColorSpace.DisplayP3,
            ThumbnailWidth = 120,
            ThumbnailHeight = 180,
            Pattern = static (x, y, w, _) => (0.2f + 0.6f * x / w, 0.15f + 0.7f * y / 240f, ((x ^ y) & 1) == 0 ? 0.2f : 0.8f),
        };
        BitmapLoader.InitializePresentationDecoder(decoder, new FixedEncodingPolicy(PixelEncoding.RgbaF16));
        AnalysisResultCache.InvalidateAll();

        using var presenter = new CvDiagnosticPresenter();
        presenter.SetSource(file);
        bool ready = await WaitAsync(() => presenter.SharpnessTile.Source != null
            || presenter.Status.StartsWith("失败", StringComparison.Ordinal), 30000);
        c.True("CV 诊断：P3/F16 展示图完成现算出图", ready && presenter.SharpnessTile.Source != null, presenter.Status);
        if (presenter.SharpnessTile.Source is { } tile)
        {
            using var display = decoder.CreateFullImage();
            using var analysis = ColorEngine.ToAnalysisSrgb8(display);
            var grid = await CvGridExtractor.ExtractAsync(analysis);
            using var expected = HeatmapBitmapBuilder.BuildViridis(
                CvHeatmap.BuildSharpness(grid), CvDiagnosticPresenter.CvGridPixels, CvDiagnosticPresenter.CvGridPixels);
            c.True("CV 诊断：锐度图与 ToAnalysisSrgb8 真实运算逐字节一致", SamePixels(tile, expected),
                $"{tile.Width}×{tile.Height} vs {expected.Width}×{expected.Height}");

            float diagonal = MathF.Sqrt((float)analysis.Width * analysis.Width + (float)analysis.Height * analysis.Height);
            var field = CvHeatmap.BuildShakeField(grid, diagonal);
            c.True("CV 诊断：抖动矢量场与参考一致", SameField(presenter.ShakeTile.ShakeField, field),
                "方向/宽度/掩膜/一致性/对比度存在差异");

            double expectedRatio = (double)analysis.Width / analysis.Height;
            c.True("CV 诊断：宽高比取展示图显示朝向尺寸（方向 6 → 0.667）",
                Math.Abs(presenter.AspectRatio - expectedRatio) < 1e-6,
                $"实际 {presenter.AspectRatio:0.####}，期望 {expectedRatio:0.####}");

        }

        string? fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, CancellationToken.None);
        c.True("CV 诊断：现算只进内存，不写特征库",
            fingerprint != null && await CountCvRowsAsync(fingerprint) == 0, "检测到 cv_grid 写入");
        presenter.Dispose();
    }

    // ---------------------------------------------------------------
    // 3) 分析栏：只读基线 → 按侧现算 → 即时缓存
    // ---------------------------------------------------------------

    private static async Task RunPresenterChecksAsync(Checks c, string directory, CountingResourceProvider counting)
    {
        string fixture = Path.GetFullPath(Path.Combine("Fixtures", "jpeg", "color-srgb.jpg"));
        var seeded = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // --- 3.1 增强 + 仅 CV：现算只覆盖 CV，库内 DINO 基线继续显示 ---
        {
            string path = CopyFixture(directory, fixture, "presenter-enhanced-cv.jpg");
            var file = new ImageFile(new LocalFileRef(path));
            await file.LoadExifDataAsync();
            await file.LoadBasicPropertiesAsync();
            using (var baselineImage = CreatePattern(400, 300, invert: true))
            {
                var baselineGrid = await CvGridExtractor.ExtractAsync(baselineImage);
                string fingerprint = await SeedAsync(file, CreatePatchBlob(7), baselineGrid, 400, 300);
                seeded[fingerprint] = baselineGrid.Encode();
            }

            AnalysisResultCache.InvalidateAll();
            using var display = CreatePattern(256, 192, invert: false);
            var host = new FakeHost
            {
                CurrentFile = file,
                DisplayImage = display,
                EnhancedDisplay = true,
                ImmediateCvCompute = true,
            };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            string key = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, true, false, true);
            var entry = await WaitForImmediateAsync(key);
            c.True("增强 + 仅 CV：即时现算写入缓存", entry is { SharpnessBmp: not null }, "缓存缺失或缺少锐度图");
            if (entry?.SharpnessBmp is { } cached)
            {
                bool applied = await WaitAsync(() => ReferenceEquals(presenter.SharpnessTile.Source, cached));
                c.True("增强 + 仅 CV：现算结果上屏", applied, "瓦片未使用即时缓存项");
                using var expected = await ExpectedHeatmapAsync(display);
                c.True("增强 + 仅 CV：CV 结果来自当前展示图（非库内基线）", SamePixels(cached, expected),
                    "与展示图参考运算不一致");
                c.True("增强 + 仅 CV：抖动矢量场非空", presenter.ShakeTile.ShakeField is not null);
            }
#if PHOTOVIEWER_DINO
            c.True("增强 + 仅 CV：库内 DINO 基线在禁算侧继续显示", presenter.PcaTile.Source != null,
                "PcaTile 未保留只读库基线");
#endif
            c.True("增强 + 仅 CV：未把结果写回特征库",
                await CountCvRowsAsync(await FingerprintOfAsync(path)) == 1, "库内基线行被覆盖或新增");
        }

        // --- 3.2 未增强：只补缺失侧，成功侧刷新复用 ---
        {
            string path = CopyFixture(directory, fixture, "presenter-missing-side.jpg");
            var file = new ImageFile(new LocalFileRef(path));
            await file.LoadExifDataAsync();
            await file.LoadBasicPropertiesAsync();
            string fingerprint = await SeedAsync(file, CreatePatchBlob(11), null, 0, 0);

            AnalysisResultCache.InvalidateAll();
            using var display = CreatePattern(288, 192, invert: true);
            var host = new FakeHost
            {
                CurrentFile = file,
                DisplayImage = display,
                EnhancedDisplay = false,
                ImmediateCvCompute = true,
            };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            string key = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, false, false, true);
            var entry = await WaitForImmediateAsync(key, 20000);
            c.True("未增强回退：只补齐开启且缺失的 CV 侧", entry is { SharpnessBmp: not null }, "未生成 CV 结果");
            if (entry?.SharpnessBmp is { } first)
            {
                await WaitAsync(() => ReferenceEquals(presenter.SharpnessTile.Source, first));
                using var expected = await ExpectedHeatmapAsync(display);
                c.True("未增强回退：CV 结果来自当前展示图", SamePixels(first, expected), "与参考运算不一致");
#if PHOTOVIEWER_DINO
                c.True("未增强回退：库内 DINO 基线保留", presenter.PcaTile.Source != null);
#endif
                presenter.Refresh();
                await Task.Delay(400);
                var again = AnalysisResultCache.TryGetImmediate(key);
                c.True("未增强回退：刷新复用成功侧（不再重算）",
                    again?.SharpnessBmp is not null && ReferenceEquals(again.SharpnessBmp, first),
                    "成功侧被重新计算");
            }
            c.True("未增强回退：只读库不被即时计算写回", await CountCvRowsAsync(fingerprint) == 0);
        }

#if PHOTOVIEWER_DINO
        // --- 3.3 双侧开启：成功侧入缓存、失败侧不缓存且可重试 ---
        {
            string path = CopyFixture(directory, fixture, "presenter-partial-failure.jpg");
            var file = new ImageFile(new LocalFileRef(path));
            await file.LoadExifDataAsync();
            await file.LoadBasicPropertiesAsync();
            using (var baselineImage = CreatePattern(360, 240, invert: true))
            {
                var baselineGrid = await CvGridExtractor.ExtractAsync(baselineImage);
                string fingerprint = await SeedAsync(file, CreatePatchBlob(13), baselineGrid, 360, 240);
                seeded[fingerprint] = baselineGrid.Encode();
            }

            AnalysisResultCache.InvalidateAll();
            using var display = CreatePattern(256, 160, invert: false);
            var host = new FakeHost { CurrentFile = file };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            await WaitAsync(() => presenter.PcaTile.Source != null && presenter.SharpnessTile.Source != null);

            host.DisplayImage = display;
            host.EnhancedDisplay = true;
            host.ImmediateCompute = true;
            host.ImmediateCvCompute = true;
            counting.Reset();
            presenter.Refresh();
            string key = AnalysisResultCache.ImmediateKey(file.File.StorageId.Value, true, true, true);
            var entry = await WaitForImmediateAsync(key, 20000);
            c.True("部分失败：成功侧（CV）进入即时缓存", entry is { SharpnessBmp: not null }, "缺少 CV 结果");
            c.True("部分失败：失败侧（DINO）不写入缓存，保留刷新重试机会", entry is { Patches: null },
                "失败侧被当成已完成写入缓存");
            c.True("部分失败：DINO 失败来自缺模型资源", counting.ModelRequests == 1,
                $"资源请求 {counting.ModelRequests} 次");
            if (entry?.SharpnessBmp is { } successSide)
            {
                using var expected = await ExpectedHeatmapAsync(display);
                c.True("部分失败：成功侧来自当前展示图", SamePixels(successSide, expected), "与参考运算不一致");
                counting.Reset();
                presenter.Refresh();
                bool retried = await WaitAsync(() => counting.ModelRequests >= 1, 20000);
                c.True("部分失败：刷新重试失败侧", retried, $"资源请求仍为 {counting.ModelRequests} 次");
                await Task.Delay(200);
                var again = AnalysisResultCache.TryGetImmediate(key);
                c.True("部分失败：刷新复用成功侧（位图实例不变）",
                    again?.SharpnessBmp is not null && ReferenceEquals(again.SharpnessBmp, successSide),
                    "成功侧被重新计算");
            }
        }
#endif

        // --- 3.4 DINO-only 宽高比取当前显示方向尺寸，不依赖 CV ---
        {
            string path = CopyFixture(directory, fixture, "presenter-dino-ratio.jpg");
            var file = new ImageFile(new LocalFileRef(path));
            await file.LoadExifDataAsync();
            await file.LoadBasicPropertiesAsync();
            using (var baselineImage = CreatePattern(400, 300, invert: true))
            {
                var baselineGrid = await CvGridExtractor.ExtractAsync(baselineImage);
                string fingerprint = await SeedAsync(file, CreatePatchBlob(17), baselineGrid, 400, 300);
                seeded[fingerprint] = baselineGrid.Encode();
            }

            AnalysisResultCache.InvalidateAll();
            using var display = CreatePattern(240, 160, invert: false, orientation: 6);
            var host = new FakeHost
            {
                CurrentFile = file,
                DisplayImage = display,
                EnhancedDisplay = false,
                ImmediateCvCompute = false,
                ImmediateCompute = true,
            };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            await WaitAsync(() => presenter.PcaTile.Source != null || presenter.SharpnessTile.Source != null);
            await Task.Delay(200);
            const double expectedRatio = 160d / 240d;
            c.True("DINO-only：宽高比取当前展示图的显示朝向尺寸（0.667）",
                Math.Abs(presenter.AspectRatio - expectedRatio) < 1e-6,
                $"实际 {presenter.AspectRatio:0.####}，期望 {expectedRatio:0.####}");
            c.True("DINO-only：库内 CV 基线在禁算侧仍显示", presenter.SharpnessTile.Source != null,
                "禁算侧被清空");
#if PHOTOVIEWER_DINO
            c.True("DINO-only：库内 patch 基线可见", presenter.PcaTile.Source != null, "PcaTile 为空");
#endif
        }

        // --- 3.5 切图无串图：两张照片各自的只读基线互不染色 ---
        {
            string pathA = CopyFixture(directory, fixture, "presenter-switch-a.jpg");
            string pathB = CopyFixture(directory, fixture, "presenter-switch-b.jpg");
            var fileA = new ImageFile(new LocalFileRef(pathA));
            var fileB = new ImageFile(new LocalFileRef(pathB));
            await fileA.LoadExifDataAsync();
            await fileA.LoadBasicPropertiesAsync();
            await fileB.LoadExifDataAsync();
            await fileB.LoadBasicPropertiesAsync();
            using var imageA = CreatePattern(320, 200, invert: false);
            using var imageB = CreatePattern(200, 320, invert: true);
            var gridA = await CvGridExtractor.ExtractAsync(imageA);
            var gridB = await CvGridExtractor.ExtractAsync(imageB);
            string fpA = await SeedAsync(fileA, CreatePatchBlob(19), gridA, 320, 200);
            string fpB = await SeedAsync(fileB, CreatePatchBlob(23), gridB, 200, 320);
            seeded[fpA] = gridA.Encode();
            seeded[fpB] = gridB.Encode();

            AnalysisResultCache.InvalidateAll();
            using var expectedA = HeatmapBitmapBuilder.BuildViridis(CvHeatmap.BuildSharpness(gridA), 32, 32);
            using var expectedB = HeatmapBitmapBuilder.BuildViridis(CvHeatmap.BuildSharpness(gridB), 32, 32);
            var host = new FakeHost { CurrentFile = fileA };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            c.True("切图：A 的只读基线出图",
                await WaitAsync(() => presenter.SharpnessTile.Source is { } a && SamePixels(a, expectedA)));
            host.CurrentFile = fileB;
            presenter.Refresh();
            c.True("切图：B 的基线不串 A 的结果",
                await WaitAsync(() => presenter.SharpnessTile.Source is { } b && SamePixels(b, expectedB), 20000));
            host.CurrentFile = fileA;
            presenter.Refresh();
            c.True("切图：回到 A 仍显示 A 的基线",
                await WaitAsync(() => presenter.SharpnessTile.Source is { } a && SamePixels(a, expectedA), 20000));

            // 快速连切：B 的现算在途时切回 A，旧代际结果不得覆盖 A。
            host.EnhancedDisplay = true;
            host.ImmediateCvCompute = true;
            host.CurrentFile = fileB;
            host.DisplayImage = imageB;
            presenter.Refresh();
            host.CurrentFile = fileA;
            host.DisplayImage = imageA;
            presenter.Refresh();
            string keyA = AnalysisResultCache.ImmediateKey(fileA.File.StorageId.Value, true, false, true);
            var entryA = await WaitForImmediateAsync(keyA, 20000);
            c.True("切图：快速连切后 A 的现算结果上屏",
                entryA?.SharpnessBmp is { } computedA
                && await WaitAsync(() => ReferenceEquals(presenter.SharpnessTile.Source, computedA)),
                "最终瓦片不是 A 的现算结果");
            using var expectedComputedA = await ExpectedHeatmapAsync(imageA);
            c.True("切图：快速连切无串图", presenter.SharpnessTile.Source is { } shown && SamePixels(shown, expectedComputedA),
                "最终瓦片与 A 的参考运算不一致");
            host.ImmediateCvCompute = false;
            host.EnhancedDisplay = false;
        }

        // --- 3.6 请求捕获开关：旧代际在途结果不得上屏 ---
        {
            string path = CopyFixture(directory, fixture, "presenter-generation.jpg");
            var file = new ImageFile(new LocalFileRef(path));
            await file.LoadExifDataAsync();
            await file.LoadBasicPropertiesAsync();
            AnalysisResultCache.InvalidateAll();
            using var large = CreatePattern(1600, 1200, invert: false);
            var host = new FakeHost
            {
                CurrentFile = file,
                DisplayImage = large,
                EnhancedDisplay = true,
                ImmediateCvCompute = true,
            };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            host.ImmediateCvCompute = false;
            host.ImmediateCompute = true;
            presenter.Refresh();
            await Task.Delay(1500);
            c.True("开关切换：旧代际 CV 结果不上屏",
                presenter.SharpnessTile.Source == null && presenter.SharpnessTile.Placeholder == "未提取",
                $"锐度瓦片仍有内容：{presenter.SharpnessTile.Placeholder}");
            string fingerprint = await FingerprintOfAsync(path);
            c.True("开关切换：未把旧代际结果写回特征库", await CountCvRowsAsync(fingerprint) == 0);
        }

        foreach (var (fingerprint, expectedBytes) in seeded)
        {
            byte[]? stored = await ReadCvBlobAsync(fingerprint);
            c.True($"只读库保留：{fingerprint[..8]} cv_grid 字节未变",
                stored != null && stored.AsSpan().SequenceEqual(expectedBytes), "cv_grid 被改写或丢失");
        }
    }

    // ---------------------------------------------------------------
    // 基础设施
    // ---------------------------------------------------------------

    private static async Task<AnalysisResultCache.Entry?> WaitForImmediateAsync(string key, int timeoutMs = 15000)
    {
        AnalysisResultCache.Entry? entry = null;
        await WaitAsync(() => (entry = AnalysisResultCache.TryGetImmediate(key)) != null, timeoutMs);
        return entry;
    }

    private static async Task<DecodedImage> ExpectedHeatmapAsync(DecodedImage display)
    {
        using var analysis = ColorEngine.ToAnalysisSrgb8(display);
        var grid = await CvGridExtractor.ExtractAsync(analysis);
        return HeatmapBitmapBuilder.BuildViridis(CvHeatmap.BuildSharpness(grid), 32, 32);
    }

    private static async Task<string> SeedAsync(
        ImageFile file, byte[]? patchBlob, CvGridResult? cv, int cvWidth, int cvHeight)
    {
        var exif = await file.LoadExifDataAsync();
        if (!file.ModifiedDate.HasValue) await file.LoadBasicPropertiesAsync();
        var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
        if (!input.CaptureTime.HasValue) throw new InvalidOperationException("夹具缺少可计算指纹的时间戳");
        string fingerprint = PhotoFingerprint.Compute(input);
        await PhotoDatabase.WriteIndexedAsync(input, fingerprint, ModelId, null, patchBlob,
            cv?.Encode(), cv == null ? null : CvGridResult.CurrentVersion, cvWidth, cvHeight);
        return fingerprint;
    }

    private static async Task<string> FingerprintOfAsync(string path)
    {
        var file = new ImageFile(new LocalFileRef(path));
        var exif = await file.LoadExifDataAsync();
        if (!file.ModifiedDate.HasValue) await file.LoadBasicPropertiesAsync();
        var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
        if (!input.CaptureTime.HasValue) throw new InvalidOperationException("夹具缺少可计算指纹的时间戳");
        return PhotoFingerprint.Compute(input);
    }

#if PHOTOVIEWER_DINO
    private static byte[] CreatePatchBlob(int seed)
    {
        int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
        var blob = new byte[total * sizeof(float)];
        for (int i = 0; i < total; i++)
        {
            float value = MathF.Sin((i + seed * 31) * 0.013f) * 0.5f + MathF.Cos((i % 97 + seed) * 0.07f) * 0.25f;
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)), value);
        }
        return blob;
    }
#else
    private static byte[]? CreatePatchBlob(int seed) => null;
#endif

    private static async Task<int> CountCvRowsAsync(string fingerprint)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM photos WHERE fingerprint = $fp AND cv_grid IS NOT NULL;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<byte[]?> ReadCvBlobAsync(string fingerprint)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cv_grid FROM photos WHERE fingerprint = $fp;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        object? value = await command.ExecuteScalarAsync();
        return value is byte[] blob ? blob : null;
    }

    private static string CopyFixture(string directory, string fixture, string name)
    {
        string target = Path.Combine(directory, name);
        File.Copy(fixture, target, overwrite: true);
        return target;
    }

    private static void WriteStubPng(string path)
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
    }

    private static DecodedImage CreatePattern(int width, int height, bool invert, int orientation = 1)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value = ((x / 6 + y / 6) % 2 == 0 ? 48 : 208) + (x * 37 + y * 11) % 48;
                if (invert) value = 255 - value;
                byte v = (byte)Math.Clamp(value, 0, 255);
                int offset = y * stride + x * 4;
                pixels[offset] = v;
                pixels[offset + 1] = (byte)Math.Clamp(v / 2 + 40, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(255 - v, 0, 255);
                pixels[offset + 3] = 255;
            }
        }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.Bgra32,
            ImageColorSpace.Srgb, orientation: orientation);
    }

    private static DecodedImage CreateF16(
        int width, int height, ImageColorSpace colorSpace, int orientation,
        Func<int, int, (float R, float G, float B)> pixel)
    {
        int stride = width * 8;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                int offset = y * stride + x * 8;
                WriteHalf(pixels, offset, r);
                WriteHalf(pixels, offset + 2, g);
                WriteHalf(pixels, offset + 4, b);
                WriteHalf(pixels, offset + 6, 1f);
            }
        }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.RgbaF16,
            colorSpace, orientation: orientation);
    }

    private static void WriteHalf(byte[] buffer, int offset, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static (byte B, byte G, byte R) ReadBgra(DecodedImage image, int x, int y)
    {
        int offset = y * image.Stride + x * 4;
        return (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);
    }

    private static bool Near(byte actual, int expected, int tolerance) => Math.Abs(actual - expected) <= tolerance;

    private static bool SamePixels(DecodedImage a, DecodedImage b)
        => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private static bool SameField(ShakeField? actual, ShakeField expected)
    {
        if (actual == null) return false;
        return SameFloats(actual.Direction, expected.Direction)
            && SameFloats(actual.Width, expected.Width)
            && actual.Mask.AsSpan().SequenceEqual(expected.Mask)
            && SameFloats(actual.LocalConsistency, expected.LocalConsistency)
            && SameFloats(actual.Contrast, expected.Contrast)
            && BitConverter.SingleToInt32Bits(actual.Diagonal) == BitConverter.SingleToInt32Bits(expected.Diagonal);
    }

    private static bool SameFloats(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
        return true;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private sealed class FakeHost : IAnalysisHost
    {
        public bool AnalysisVisible { get; set; } = true;
        public bool ImmediateCompute { get; set; }
        public bool ImmediateCvCompute { get; set; }
        public ImageFile? CurrentFile { get; set; }
        public DecodedImage? DisplayImage { get; set; }
        public bool EnhancedDisplay { get; set; }
        public bool ThumbnailsBusy => false;
        public event Action? HostChanged;

        public void Raise() => HostChanged?.Invoke();
    }

    /// <summary>按源几何产出合成展示图：Full 为可选 F16/P3 且保留待应用方向，缩略图为已旋转 sRGB8。</summary>
    private sealed class AnalysisPresentationDecoder : IPresentationImageDecoder
    {
        public int FullWidth { get; init; } = 240;
        public int FullHeight { get; init; } = 160;
        public int SourceWidth { get; init; } = 240;
        public int SourceHeight { get; init; } = 160;
        public int Orientation { get; init; } = 1;
        public ImageColorSpace ColorSpace { get; init; } = ImageColorSpace.LinearSrgb;
        public int ThumbnailWidth { get; init; } = 90;
        public int ThumbnailHeight { get; init; } = 120;
        public Func<int, int, int, int, (float R, float G, float B)> Pattern { get; init; } =
            static (x, y, w, h) => (x / (float)w, y / (float)h, 0.5f);

        public DecodedImage CreateFullImage()
        {
            int stride = FullWidth * 8;
            var pixels = new byte[stride * FullHeight];
            for (int y = 0; y < FullHeight; y++)
            {
                for (int x = 0; x < FullWidth; x++)
                {
                    var (r, g, b) = Pattern(x, y, FullWidth, FullHeight);
                    int offset = y * stride + x * 8;
                    WriteHalf(pixels, offset, r);
                    WriteHalf(pixels, offset + 2, g);
                    WriteHalf(pixels, offset + 4, b);
                    WriteHalf(pixels, offset + 6, 1f);
                }
            }
            return new DecodedImage(pixels, FullWidth, FullHeight, stride, PixelEncoding.RgbaF16,
                ColorSpace, SourceWidth, SourceHeight, Orientation);
        }

        public Task<DecodedImage?> DecodeAsync(
            IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
            => Task.FromResult<DecodedImage?>(CreateFullImage());

        public Task<DecodedImage?> DecodeThumbnailAsync(
            IFileRef file, int minShortEdge, CancellationToken cancellationToken)
        {
            int stride = ThumbnailWidth * 4;
            var pixels = new byte[stride * ThumbnailHeight];
            for (int y = 0; y < ThumbnailHeight; y++)
            {
                for (int x = 0; x < ThumbnailWidth; x++)
                {
                    int offset = y * stride + x * 4;
                    pixels[offset] = 40;
                    pixels[offset + 1] = (byte)(60 + x * 120 / ThumbnailWidth);
                    pixels[offset + 2] = (byte)(80 + y * 100 / ThumbnailHeight);
                    pixels[offset + 3] = 255;
                }
            }
            return Task.FromResult<DecodedImage?>(new DecodedImage(pixels, ThumbnailWidth, ThumbnailHeight, stride,
                PixelEncoding.Bgra32, ImageColorSpace.Srgb));
        }
    }

    private sealed class FixedEncodingPolicy(PixelEncoding encoding) : IPresentationEncodingPolicy
    {
        public PixelEncoding Select(ImageSizeClass sizeClass, PhotoViewer.Core.Memory.PressureLevel pressure) => encoding;
    }

    private sealed class CountingResourceProvider : IAppResourceProvider
    {
        private int _modelRequests;

        public int ModelRequests => Volatile.Read(ref _modelRequests);

        public Stream? OpenAsset(Uri uri)
        {
            if (uri.AbsolutePath.EndsWith("dinov3_vits16.onnx", StringComparison.Ordinal))
                Interlocked.Increment(ref _modelRequests);
            return null;
        }

        public void Reset() => Interlocked.Exchange(ref _modelRequests, 0);
    }

    /// <summary>捕获并还原展示解码器 / 编码策略 / 缩略图兜底解码器的静态注入。</summary>
    private sealed class PresentationInjection
    {
        private static readonly FieldInfo DecoderField = typeof(BitmapLoader)
            .GetField("_presentationDecoder", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo PolicyField = typeof(BitmapLoader)
            .GetField("_encodingPolicy", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo MissingPreviewField = typeof(ThumbnailService)
            .GetField("_missingPreviewDecoder", BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly object? _decoder = DecoderField.GetValue(null);
        private readonly object? _policy = PolicyField.GetValue(null);
        private readonly object? _missingPreview = MissingPreviewField.GetValue(null);

        public static PresentationInjection Capture() => new();

        public void Restore()
        {
            DecoderField.SetValue(null, _decoder);
            PolicyField.SetValue(null, _policy);
            MissingPreviewField.SetValue(null, _missingPreview);
        }
    }

    /// <summary>捕获并还原 AppResources 注入（DINO 失败用例用缺模型资源口径）。</summary>
    private sealed class ResourceInjection
    {
        private static readonly FieldInfo ImplField = typeof(AppResources)
            .GetField("_impl", BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly object? _impl = ImplField.GetValue(null);

        public static ResourceInjection Capture() => new();

        public void Install(IAppResourceProvider provider) => ImplField.SetValue(null, provider);

        public void Restore() => ImplField.SetValue(null, _impl);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public System.Reactive.Concurrency.IScheduler MainThreadScheduler =>
            System.Reactive.Concurrency.ImmediateScheduler.Instance;

        public bool CheckAccess() => true;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) =>
            Task.FromResult(func());
    }
}
