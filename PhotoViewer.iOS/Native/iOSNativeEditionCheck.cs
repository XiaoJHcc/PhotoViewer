using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 发行版验收：单版 smoke，以及 AI → 标准 → AI 的跨进程设置、视图状态与特征库往返。
/// 三阶段共用 Documents/PhotoViewer/acceptance/photos-smoke.db，不清理数据库。
/// </summary>
internal static class iOSNativeEditionCheck
{
    private const string LocalSwitch = "--pv-native-edition-check";
    private const string PrepareSwitch = "--pv-native-edition-prepare";
    private const string StandardSwitch = "--pv-native-edition-standard";
    private const string AiSwitch = "--pv-native-edition-ai";
    private const string LocalReportName = "native-edition-checks.txt";
    private const string RoundtripReportName = "native-edition-roundtrip.txt";
    private const string StateFileName = "edition-roundtrip-state.json";
    private const string FixtureFolderName = "edition-roundtrip";
    private const string ProbeFolderName = "edition-probes";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    internal const string SettingsKey = "cc.xiaojh.PhotoViewer.Acceptance.Edition.Settings.json";
    internal const string ViewStatePrefix = "cc.xiaojh.PhotoViewer.Acceptance.Edition.ViewState.";

    private static string DocumentsRoot => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string OutputRoot => Path.Combine(DocumentsRoot, "PhotoViewer");
    private static string StatePath => Path.Combine(OutputRoot, StateFileName);
    private static string FixtureRoot => Path.Combine(OutputRoot, FixtureFolderName);
    private static string ProbeRoot => Path.Combine(OutputRoot, ProbeFolderName);

    private static string PreferencesPath
    {
        get
        {
            string container = Path.GetDirectoryName(DocumentsRoot)!;
            string bundleId = NSBundle.MainBundle.BundleIdentifier ?? "cc.xiaojh.PhotoViewer";
            return Path.Combine(container, "Library", "Preferences", bundleId + ".plist");
        }
    }

    internal static string DatabasePath => Path.Combine(OutputRoot, "acceptance", "photos-smoke.db");

    private enum EditionStage { Local, Prepare, Standard, Ai }

    internal static bool IsRoundtripStage(string[] args)
        => args.Contains(PrepareSwitch, StringComparer.Ordinal)
            || args.Contains(StandardSwitch, StringComparer.Ordinal)
            || args.Contains(AiSwitch, StringComparer.Ordinal);

    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        var stage = ResolveStage(args);
        if (stage == null) return;
        _ = RunAsync(stage.Value, args, root, session);
    }

    private static EditionStage? ResolveStage(string[] args)
    {
        if (args.Contains(LocalSwitch, StringComparer.Ordinal)) return EditionStage.Local;
        if (args.Contains(PrepareSwitch, StringComparer.Ordinal)) return EditionStage.Prepare;
        if (args.Contains(StandardSwitch, StringComparer.Ordinal)) return EditionStage.Standard;
        if (args.Contains(AiSwitch, StringComparer.Ordinal)) return EditionStage.Ai;
        return null;
    }

    private static string StageName(EditionStage stage) => stage switch
    {
        EditionStage.Local => "local",
        EditionStage.Prepare => "prepare",
        EditionStage.Standard => "standard",
        _ => "ai",
    };

    private static async Task RunAsync(EditionStage stage, string[] args,
        iOSNativeRootViewController root, PhotoLoadSession session)
    {
        var lines = new List<string>();
        string report = Path.Combine(OutputRoot, stage == EditionStage.Local ? LocalReportName : RoundtripReportName);
        Directory.CreateDirectory(OutputRoot);
        bool resetReport = stage is EditionStage.Local or EditionStage.Prepare;
        try
        {
            await WriteReportAsync(report, [$"STAGE {StageName(stage)} START"], append: !resetReport);
            ValidateStage(stage);
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            if (stage == EditionStage.Local)
                await RunLocalAsync(root, main, session, RequireDirectory(args), lines);
            else
                await RunRoundtripAsync(stage, main, session, lines);
            lines.Add($"STAGE {StageName(stage)} PASS");
            await WriteReportAsync(report, lines, append: true);
            Console.WriteLine($"[NativeSmoke] PASS edition-{StageName(stage)} ({lines.Count})");
        }
        catch (Exception exception)
        {
            lines.Add($"STAGE {StageName(stage)} FAIL: {exception}");
            await WriteReportAsync(report, lines, append: true);
            Console.Error.WriteLine($"[NativeSmoke] FAIL edition-{StageName(stage)} " + exception);
        }
    }

    private static void ValidateStage(EditionStage stage)
    {
#if PHOTOVIEWER_DINO
        if (stage == EditionStage.Standard)
            throw new InvalidOperationException("--pv-native-edition-standard 只能在标准版构建运行");
#else
        if (stage is EditionStage.Prepare or EditionStage.Ai)
            throw new InvalidOperationException("--pv-native-edition-prepare/ai 只能在 AI 版构建运行");
#endif
    }

    private static string RequireDirectory(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], LocalSwitch, StringComparison.Ordinal)) continue;
            string directory = ResolveDocumentsPath(args[index + 1]);
            if (!Path.GetFullPath(directory).StartsWith(DocumentsRoot, StringComparison.Ordinal))
                throw new InvalidOperationException($"发行版验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            return directory;
        }
        throw new InvalidOperationException($"{LocalSwitch} 缺少目录参数");
    }

    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(DocumentsRoot, path);

    private static async Task RunLocalAsync(iOSNativeRootViewController root, MainViewModel main,
        PhotoLoadSession session, string directory, List<string> lines)
    {
        string[] fixtures = Directory.GetFiles(directory, "*.jpg");
        if (fixtures.Length == 0) throw new InvalidOperationException($"发行版验收需要自有 jpg 夹具：{directory}");
        await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");

        var snapshot = main.Settings.CreateSnapshot();
        var originalFile = main.CurrentFile;
        bool entryAnalysis = main.IsAnalysisViewVisible;
        bool entryFilm = main.IsThumbnailViewVisible;
        bool entryControl = main.IsControlViewVisible;
        try
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                main.Settings.LayoutMode = LayoutMode.Row;
                main.IsThumbnailViewVisible = true;
                main.IsControlViewVisible = true;
                main.IsAnalysisViewVisible = false;
                foreach (var format in main.Settings.FileFormats)
                    if (format.DisplayName is "JPG" or "RAW") format.IsEnabled = true;
            });
            await main.FolderVM.OpenFolderAsync(new LocalFolderRef(directory));
            await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
            await SettleAsync(root);

            VerifyEditionIdentity(lines);
            VerifyFilterBar(root, lines);
#if PHOTOVIEWER_DINO
            await VerifyDinoEntriesAsync(root, main, lines);
#else
            await VerifyStandardEntriesAsync(root, main, lines);
#endif
            await VerifyCvDiagnosticsAsync(main, lines);
        }
        finally
        {
            await RestoreLocalAsync(root, main, snapshot, entryAnalysis, entryFilm, entryControl, originalFile);
        }
    }

    private static void VerifyEditionIdentity(List<string> lines)
    {
#if PHOTOVIEWER_DINO
        if (!iOSAppCapabilities.HasDino || iOSAppCapabilities.DiagnosticName != "DINO 诊断")
            throw new InvalidOperationException("AI 版能力常量与 PHOTOVIEWER_DINO 不一致");
#else
        if (iOSAppCapabilities.HasDino || iOSAppCapabilities.DiagnosticName != "CV 诊断")
            throw new InvalidOperationException("标准版能力常量与编译条件不一致");
#endif
        lines.Add($"PASS 发行版：{iOSAppCapabilities.EditionName}，诊断页 = {iOSAppCapabilities.DiagnosticName}");
    }

    /// <summary>
    /// 核对宽形态筛选控件（FILTER-07）：一个合体玻璃胶囊内并排两个 44 点段——筛选段弹出与手机相同的
    /// 三分组筛选菜单，更多段承接与手机「更多」同源的主图菜单（单个延迟元素）；AI 版另有相似聚类开关，
    /// 标准版不创建也不为它留矩形。行布局主轴固定为 44 + 2 × 外边距，没有计数、也没有空槽。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyFilterBar(iOSNativeRootViewController root, List<string> lines)
    {
        var bar = root.FilterBar;
        var frames = bar.Frames;
        if (bar.CombinedGroup.Buttons.Count != 2)
            throw new InvalidOperationException($"筛选合体胶囊不是两个段：{bar.CombinedGroup.Buttons.Count}");
        if (!ReferenceEquals(bar.CombinedGroup.Buttons[0], bar.FilterButton)
            || !ReferenceEquals(bar.CombinedGroup.Buttons[1], bar.MoreButton))
            throw new InvalidOperationException("合体胶囊两段不是筛选 / 更多");
        if (bar.FilterButton.Hidden || bar.MoreButton.Hidden)
            throw new InvalidOperationException(
                $"筛选 / 更多段可见性不符：筛选隐藏={bar.FilterButton.Hidden}，更多隐藏={bar.MoreButton.Hidden}");
        if (Math.Abs((double)frames.Control.Width - iOSFilterLayout.GroupWidth) > 1)
            throw new InvalidOperationException(
                $"合体胶囊宽 {frames.Control.Width:0.##} ≠ {iOSFilterLayout.GroupWidth:0.##}（2 × 44）");
        if (bar.FilterButton.Menu is not { } filterMenu)
            throw new InvalidOperationException("筛选段没有菜单");
        if (filterMenu.Children.Length != 3)
            throw new InvalidOperationException($"筛选段菜单不是三个内联分组：{filterMenu.Children.Length}");
        if (bar.MoreButton.Menu is not { Children.Length: 1 } moreMenu || moreMenu.Children[0] is not UIDeferredMenuElement)
            throw new InvalidOperationException("更多段菜单不是与手机「更多」同源的单个延迟元素");
#if PHOTOVIEWER_DINO
        if (bar.SimilarityToggle is not { } toggle || toggle.Hidden)
            throw new InvalidOperationException("AI 版筛选控件缺少相似聚类开关");
        if (Math.Abs((double)toggle.Frame.X - (double)frames.Toggle.X) > 1 || Math.Abs((double)toggle.Frame.Width - (double)frames.Toggle.Width) > 1)
            throw new InvalidOperationException($"AI 版相似聚类开关未按共享矩形落位：{toggle.Frame} vs {frames.Toggle}");
#else
        if (bar.SimilarityToggle != null)
            throw new InvalidOperationException("标准版筛选控件不应创建相似聚类开关");
        if (frames.Toggle.Width > 0 || frames.Toggle.Height > 0)
            throw new InvalidOperationException($"标准版筛选控件不应为相似聚类开关保留矩形：{frames.Toggle}");
#endif
        double expected = iOSFilterLayout.Segment + iOSFilterLayout.Margin * 2;
        if (Math.Abs((double)bar.Frame.Height - expected) > 1)
            throw new InvalidOperationException($"行布局筛选控件高度 {bar.Frame.Height:0.##} ≠ {expected:0.##}");
        if (Math.Abs(bar.MainLength(true, 0) - expected) > 1)
            throw new InvalidOperationException($"行布局筛选控件主轴长度 {bar.MainLength(true, 0):0.##} ≠ {expected:0.##}");
        lines.Add($"PASS 筛选控件：{iOSAppCapabilities.EditionName} 版{(iOSAppCapabilities.HasDino ? "含" : "无")}相似聚类开关，" +
            $"合体胶囊 2 × 44（筛选段三分组菜单、更多段与手机同源），行布局主轴 {expected:0.##} 点（无计数、无空槽）");
    }

#if PHOTOVIEWER_DINO
    private static async Task VerifyDinoEntriesAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        if (main.Tools.IsPhotoStatsAvailable) throw new InvalidOperationException("iOS 不应提供照片数据统计");
        var entries = iOSNativeToolsListViewController.Titles;
        if (!entries.Contains("DINO 诊断", StringComparer.Ordinal))
            throw new InvalidOperationException($"AI 版工具列表缺少 DINO 诊断：{string.Join(" | ", entries)}");
        if (main.FileVM.SimilarityPanel == null || root.SimilarityPane == null)
            throw new InvalidOperationException("AI 版缺少相似聚类面板实例");
        await UiDispatcher.InvokeAsync(() => main.IsAnalysisViewVisible = true);
        await WaitAsync(() => root.AnalysisPane.Presenter.Tiles.Any(tile => tile.ShortLabel == "DINO PCA")
            && root.AnalysisPane.Presenter.Tiles.Any(tile => tile.ShortLabel.StartsWith("Cosine", StringComparison.Ordinal)),
            "分析栏含 DINO PCA / Cosine");
        lines.Add("PASS AI 入口：相似面板、分析栏 PCA/Cosine、工具列表 DINO 诊断");
        await VerifySwitchCombinationsAsync(main, lines);
    }

    private static async Task VerifySwitchCombinationsAsync(MainViewModel main, List<string> lines)
    {
        string probe = CreateGeneratedProbe("switch");
        var original = main.CurrentFile;
        try
        {
            await OpenImageAsync(main, probe);
            var file = main.CurrentFile!;
            string fingerprint = await FingerprintAsync(file);
            await AssertUnextractedAsync(fingerprint, "四开关探针");

            using var display = await BitmapLoader.GetBitmapAsync(file.File, CancellationToken.None)
                ?? throw new InvalidOperationException("四开关探针解码为空");
            foreach (var (dino, cv) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var host = new EditionAnalysisHost
                {
                    CurrentFile = file,
                    DisplayImage = display,
                    ImmediateCompute = dino,
                    ImmediateCvCompute = cv,
                };
                using var presenter = new AnalysisPresenter(host);
                await RefreshAndWaitRouteAsync(presenter);

                if (dino)
                {
                    await WaitAsync(() => presenter.PcaTile.Source != null
                        || presenter.PcaTile.Placeholder == "分析失败",
                        $"DINO={dino} CV={cv} DINO 侧完成");
                    if (presenter.PcaTile.Source == null || presenter.CosineTile.Source == null)
                        throw new InvalidOperationException($"DINO={dino} CV={cv} DINO 侧失败：{presenter.PcaTile.Placeholder}");
                }
                else
                {
                    AssertUnextracted(presenter.PcaTile, $"DINO={dino} CV={cv} DINO 侧");
                }

                if (cv)
                {
                    await WaitAsync(() => presenter.SharpnessTile.Source != null
                        || presenter.SharpnessTile.Placeholder == "分析失败",
                        $"DINO={dino} CV={cv} CV 侧完成");
                    if (presenter.SharpnessTile.Source == null || presenter.ShakeTile.ShakeField == null)
                        throw new InvalidOperationException($"DINO={dino} CV={cv} CV 侧失败：{presenter.SharpnessTile.Placeholder}");
                }
                else
                {
                    AssertUnextracted(presenter.SharpnessTile, $"DINO={dino} CV={cv} CV 侧");
                }

                if (!dino && !cv)
                {
                    await Task.Delay(900);
                    AssertUnextracted(presenter.PcaTile, "DINO=0 CV=0 DINO 侧");
                    AssertUnextracted(presenter.SharpnessTile, "DINO=0 CV=0 CV 侧");
                }
                lines.Add($"PASS 开关组合 DINO={dino} CV={cv}：双侧独立，未开启侧保持未提取");
            }
            await AssertUnextractedAsync(fingerprint, "四开关探针（现算后）");
        }
        finally
        {
            TryDelete(probe);
            if (original != null) await OpenImageAsync(main, original.File.RequireLocalPath());
        }
    }

    private static async Task RefreshAndWaitRouteAsync(AnalysisPresenter presenter)
    {
        var routed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => routed.TrySetResult(true);
        presenter.TilesChanged += OnChanged;
        try
        {
            presenter.Refresh();
            await WaitAsync(() => routed.Task.IsCompleted, "分析栏路由");
        }
        finally
        {
            presenter.TilesChanged -= OnChanged;
        }
    }

    private static void AssertUnextracted(AnalysisTile tile, string label)
    {
        if (tile.Source != null || tile.Placeholder != "未提取")
            throw new InvalidOperationException($"{label}不应现算：{tile.Placeholder}");
    }

    private sealed class EditionAnalysisHost : IAnalysisHost
    {
        public bool AnalysisVisible => true;
        public bool ImmediateCompute { get; init; }
        public bool ImmediateCvCompute { get; init; }
        public ImageFile? CurrentFile { get; init; }
        public DecodedImage? DisplayImage { get; init; }
        public bool EnhancedDisplay => false;
        public bool ThumbnailsBusy => false;
        public event Action? HostChanged;

        public void Raise() => HostChanged?.Invoke();
    }
#else
    private static async Task VerifyStandardEntriesAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        if (main.FileVM.FilterBar.IsSimilarityPanelOpen)
            throw new InvalidOperationException("标准版读取到相似面板展开态并生效");
        var entries = iOSNativeToolsListViewController.Titles;
        if (!entries.Contains("CV 诊断", StringComparer.Ordinal) || entries.Contains("DINO 诊断", StringComparer.Ordinal))
            throw new InvalidOperationException($"标准版工具列表入口不符：{string.Join(" | ", entries)}");
        await UiDispatcher.InvokeAsync(() => main.IsAnalysisViewVisible = true);
        await WaitAsync(() => root.AnalysisPane.Presenter.Tiles.Count(tile => tile.Kind == AnalysisTileKind.Diagnostic) == 2,
            "标准版分析栏只有锐度/抖动两张诊断瓦片");
        if (root.AnalysisPane.Presenter.Tiles.Any(tile => tile.ShortLabel is "DINO PCA" or "Cosine 16,16"))
            throw new InvalidOperationException("标准版分析栏出现 DINO 瓦片");
        lines.Add("PASS 标准入口：无相似面板/toggle、无 DINO 瓦片；工具列表 = EXIF 详情 / CV 诊断");
    }
#endif

    private static async Task VerifyCvDiagnosticsAsync(MainViewModel main, List<string> lines)
    {
        var source = main.CurrentFile ?? throw new InvalidOperationException("CV 诊断缺少当前照片");
        string probe = Path.Combine(ProbeRoot, $"cv-{Guid.NewGuid():N}{Path.GetExtension(source.Name)}");
        Directory.CreateDirectory(ProbeRoot);
        File.Copy(source.File.RequireLocalPath(), probe, true);
        try
        {
            await OpenImageAsync(main, probe);
            var file = main.CurrentFile!;
            string fingerprint = await FingerprintAsync(file);
            await AssertUnextractedAsync(fingerprint, "CV 探针");

            using var presenter = new CvDiagnosticPresenter();
            presenter.SetSource(file);
            await WaitAsync(() => presenter.SharpnessTile.Source != null || presenter.Status.StartsWith("失败", StringComparison.Ordinal),
                "CV 诊断完成");
            var tile = presenter.SharpnessTile.Source
                ?? throw new InvalidOperationException("CV 诊断未出图：" + presenter.Status);

            using var display = await BitmapLoader.GetBitmapAsync(file.File, CancellationToken.None)
                ?? throw new InvalidOperationException("CV 诊断展示解码产物为空");
            using var analysis = ColorEngine.ToAnalysisSrgb8(display);
            var grid = await CvGridExtractor.ExtractAsync(analysis);
            using var expected = HeatmapBitmapBuilder.BuildViridis(
                CvHeatmap.BuildSharpness(grid), CvDiagnosticPresenter.CvGridPixels, CvDiagnosticPresenter.CvGridPixels);
            if (!SamePixels(tile, expected))
                throw new InvalidOperationException(
                    $"CV 诊断锐度图与 ToAnalysisSrgb8 参考不一致（{tile.Width}×{tile.Height} vs {expected.Width}×{expected.Height}）");
            float diagonal = MathF.Sqrt((float)analysis.Width * analysis.Width + (float)analysis.Height * analysis.Height);
            if (!SameField(presenter.ShakeTile.ShakeField, CvHeatmap.BuildShakeField(grid, diagonal)))
                throw new InvalidOperationException("CV 诊断抖动矢量场与参考不一致");
            double ratio = (double)analysis.Width / analysis.Height;
            if (Math.Abs(presenter.AspectRatio - ratio) > 0.01)
                throw new InvalidOperationException($"CV 诊断宽高比 {presenter.AspectRatio:0.####} 未取显示朝向 {ratio:0.####}");
            if (analysis.Orientation != 1) throw new InvalidOperationException("CV 分析图方向未归 1");
            await AssertUnextractedAsync(fingerprint, "CV 探针（现算后）");
            lines.Add($"PASS 真实 CV：analysis {analysis.Width}×{analysis.Height} sRGB8 与 presenter 一致，ratio {ratio:0.###}，未写库");
        }
        finally
        {
            TryDelete(probe);
            if (source != null) await OpenImageAsync(main, source.File.RequireLocalPath());
        }
    }

    private static async Task RunRoundtripAsync(EditionStage stage, MainViewModel main,
        PhotoLoadSession session, List<string> lines)
    {
        if (stage == EditionStage.Prepare)
        {
#if PHOTOVIEWER_DINO
            await PrepareAsync(main, session, lines);
#else
            throw new InvalidOperationException("prepare 阶段只能在 AI 版构建运行");
#endif
        }
        else if (stage == EditionStage.Standard)
        {
            await VerifyStandardAsync(main, session, lines);
        }
        else
        {
            await VerifyAiAsync(main, session, lines);
        }
    }

#if PHOTOVIEWER_DINO
    private static async Task PrepareAsync(MainViewModel main, PhotoLoadSession session, List<string> lines)
    {
        ClearAcceptanceState();
        Directory.CreateDirectory(FixtureRoot);
        string path = Path.Combine(FixtureRoot, $"fixture-{Guid.NewGuid():N}.jpg");
        EnhancementAcceptance.CreateLowContrastJpeg(path);
        string relative = Path.Combine("PhotoViewer", FixtureFolderName, Path.GetFileName(path));

        await OpenImageAsync(main, path);
        await WaitAsync(() => session.Image != null, "prepare 夹具解码");
        var file = main.CurrentFile!;
        string fingerprint = await FingerprintAsync(file);
        await AssertUnextractedAsync(fingerprint, "prepare 夹具");

        var indexer = new FolderFeatureIndexer();
        await indexer.RunAsync([file]);
        if (indexer.LastError != null) throw new InvalidOperationException("特征索引失败：" + indexer.LastError);

        main.Settings.SimilarityThreshold = 0.91;
        main.Settings.SimilarityMaxResults = 13;
        main.Settings.AnalysisImmediateCompute = true;
        main.Settings.AnalysisImmediateCvCompute = false;
        main.Settings.SimilarityPanelExpanded = true;
        new AppleViewStateStore(ViewStatePrefix).SetBool(SettingsViewStateKeys.SimilarityPanelExpanded, true);
        if (!await main.SettingsRepository.CommitAsync(main.Settings.CreateSnapshot()))
            throw new InvalidOperationException("prepare 设置保存失败");
        await main.SettingsRepository.FlushAsync();
        NSUserDefaults.StandardUserDefaults.Synchronize();
        await WaitForPreferencesPersistedAsync(main.Settings.ShowZoomIndicator, "prepare");

        var db = await CaptureDbAsync(fingerprint);
        if (db.Features.Count == 0 || db.Patches.Count == 0 || db.Cv == null)
            throw new InvalidOperationException("prepare 特征索引不完整");
        if (!db.Features.Any(entry => entry.ModelId == DinoModelResources.ModelId))
            throw new InvalidOperationException($"prepare 缺少当前模型 {DinoModelResources.ModelId}");
        if (db.Cv.Spec != CvGridResult.CurrentVersion)
            throw new InvalidOperationException($"prepare CV 版本不是当前 schema：{db.Cv.Spec}");

        var state = new EditionState
        {
            FixtureRelativePath = relative,
            Fingerprint = fingerprint,
            Zoom = main.Settings.ShowZoomIndicator,
            SimilarityThreshold = 0.91,
            SimilarityMaxResults = 13,
            ImmediateDino = true,
            ImmediateCv = false,
            SimilarityPanelExpanded = true,
            Features = db.Features,
            Patches = db.Patches,
            Cv = db.Cv,
        };
        await SaveStateAsync(state);
        lines.Add($"PASS prepare：fingerprint={fingerprint}，{Describe(db)}，fixture={Path.GetFileName(path)}");
    }
#endif

    private static async Task VerifyStandardAsync(MainViewModel main, PhotoLoadSession session, List<string> lines)
    {
        var state = await LoadStateAsync();
        await OpenStateFixtureAsync(main, session, state);
        AssertSettings(main, state);
        AssertPanelState(main, state, restored: false);
        if (main.Settings.ShowZoomIndicator != state.Zoom)
            throw new InvalidOperationException(
                $"AI 版保存的通用设置未跨进程恢复：actual={main.Settings.ShowZoomIndicator} expected={state.Zoom}");

        string fingerprint = await FingerprintAsync(main.CurrentFile!);
        if (fingerprint != state.Fingerprint)
            throw new InvalidOperationException($"fixture 指纹变化：{state.Fingerprint} → {fingerprint}");

        var before = await CaptureDbAsync(fingerprint);
        AssertSameDb(state, before);
        await VerifyModelRowsAsync(state);
        main.Settings.ShowZoomIndicator = !state.Zoom;
        if (!await main.SettingsRepository.CommitAsync(main.Settings.CreateSnapshot()))
            throw new InvalidOperationException("标准版通用设置保存失败");
        await WaitForPreferencesPersistedAsync(!state.Zoom, "standard");
        AssertSettings(main, state);
        var after = await CaptureDbAsync(fingerprint);
        AssertSameDb(state, after);
        lines.Add($"PASS standard：设置与视图状态保留；{Describe(after)} 未改写；缩放={main.Settings.ShowZoomIndicator}");
    }

    private static async Task VerifyAiAsync(MainViewModel main, PhotoLoadSession session, List<string> lines)
    {
        var state = await LoadStateAsync();
        await OpenStateFixtureAsync(main, session, state);
        AssertSettings(main, state);
        if (main.Settings.ShowZoomIndicator != !state.Zoom)
            throw new InvalidOperationException(
                $"标准版保存的通用设置未跨进程恢复：actual={main.Settings.ShowZoomIndicator} expected={!state.Zoom}");
        AssertPanelState(main, state, restored: true);

        string fingerprint = await FingerprintAsync(main.CurrentFile!);
        if (fingerprint != state.Fingerprint)
            throw new InvalidOperationException($"fixture 指纹变化：{state.Fingerprint} → {fingerprint}");
        var actual = await CaptureDbAsync(fingerprint);
        AssertSameDb(state, actual);
        await VerifyModelRowsAsync(state);
#if PHOTOVIEWER_DINO
        if (!actual.Features.Any(entry => entry.ModelId == DinoModelResources.ModelId))
            throw new InvalidOperationException($"AI 往返后缺少当前模型 {DinoModelResources.ModelId}");
#endif
        lines.Add($"PASS ai：通用设置翻转、DINO/CV 开关与相似面板恢复；{Describe(actual)} 内容一致");
        CleanupAcceptanceState();
    }

    private static async Task OpenStateFixtureAsync(MainViewModel main, PhotoLoadSession session, EditionState state)
    {
        string path = Path.Combine(DocumentsRoot, state.FixtureRelativePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"跨进程 fixture 不存在，请先运行 prepare：{path}");
        await OpenImageAsync(main, path);
        await WaitAsync(() => session.Image != null, "往返 fixture 解码");
    }

    private static async Task OpenImageAsync(MainViewModel main, string path)
    {
        await main.FolderVM.OpenImageAsync(new LocalFileRef(path));
        await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(path), "当前照片切换");
        await main.CurrentFile!.LoadBasicPropertiesAsync();
    }

    private static void AssertSettings(MainViewModel main, EditionState state)
    {
        var settings = main.Settings;
        if (Math.Abs(settings.SimilarityThreshold - state.SimilarityThreshold) > 0.0001
            || settings.SimilarityMaxResults != state.SimilarityMaxResults
            || settings.AnalysisImmediateCompute != state.ImmediateDino
            || settings.AnalysisImmediateCvCompute != state.ImmediateCv)
            throw new InvalidOperationException(
                $"DINO/相似/CV 已存设置被改写：actual threshold={settings.SimilarityThreshold:0.###} max={settings.SimilarityMaxResults} " +
                $"dino={settings.AnalysisImmediateCompute} cv={settings.AnalysisImmediateCvCompute}；" +
                $"expected threshold={state.SimilarityThreshold:0.###} max={state.SimilarityMaxResults} " +
                $"dino={state.ImmediateDino} cv={state.ImmediateCv}");
        if (settings.SimilarityPanelExpanded != state.SimilarityPanelExpanded)
            throw new InvalidOperationException(
                $"相似面板展开态存储值被改写：actual={settings.SimilarityPanelExpanded} expected={state.SimilarityPanelExpanded}");
    }

    private static void AssertPanelState(MainViewModel main, EditionState state, bool restored)
    {
        bool open = main.FileVM.FilterBar.IsSimilarityPanelOpen;
        if (restored && open != state.SimilarityPanelExpanded)
            throw new InvalidOperationException(
                $"AI 版未恢复相似面板展开态：actual={open} expected={state.SimilarityPanelExpanded}");
        if (!restored && open)
            throw new InvalidOperationException("标准版恢复了相似面板展开态");
    }

    private static async Task WaitForPreferencesPersistedAsync(bool expectedZoom, string label)
    {
        NSUserDefaults.StandardUserDefaults.Synchronize();
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (PreferencesMatch(expectedZoom)) return;
            NSUserDefaults.StandardUserDefaults.Synchronize();
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            $"{label} 偏好未落盘：path={PreferencesPath} actual={DescribePreferences()} " +
            $"expected threshold=0.91 max=13 dino=True cv=False panel=True zoom={expectedZoom}");
    }

    private static bool PreferencesMatch(bool expectedZoom)
    {
        var dictionary = NSMutableDictionary.FromFile(PreferencesPath);
        if (dictionary == null) return false;
        var panelValue = dictionary[ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded];
        bool panel = panelValue is NSNumber number ? number.BoolValue
            : bool.TryParse(panelValue?.ToString(), out bool parsed) && parsed;
        if (!panel) return false;
        string? json = dictionary[SettingsKey]?.ToString();
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return Math.Abs(root.GetProperty("similarityThreshold").GetDouble() - 0.91) < 0.0001
                && root.GetProperty("similarityMaxResults").GetInt32() == 13
                && root.GetProperty("analysisImmediateCompute").GetBoolean()
                && !root.GetProperty("analysisImmediateCvCompute").GetBoolean()
                && root.GetProperty("showZoomIndicator").GetBoolean() == expectedZoom;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DescribePreferences()
    {
        var dictionary = NSMutableDictionary.FromFile(PreferencesPath);
        if (dictionary == null) return "missing";
        string panel = dictionary[ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded]?.ToString() ?? "null";
        string? json = dictionary[SettingsKey]?.ToString();
        string settings = json == null ? "null" : (json.Length > 120 ? json[..120] + "…" : json);
        return $"panel={panel} settings={settings}";
    }

    private static async Task VerifyModelRowsAsync(EditionState state)
    {
        foreach (var expected in state.Features)
        {
            if (await VerifyModelRowAsync("photo_features", "cls_vector", state.Fingerprint, expected) is { } diff)
                throw new InvalidOperationException("CLS 行核对失败：" + diff);
        }
        foreach (var expected in state.Patches)
        {
            if (await VerifyModelRowAsync("photo_patches", "patch_tokens", state.Fingerprint, expected) is { } diff)
                throw new InvalidOperationException("patch 行核对失败：" + diff);
        }
    }

    private static async Task<string?> VerifyModelRowAsync(string table, string column,
        string fingerprint, EditionFeatureSnapshot expected)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE fingerprint = $fp AND model_id = $m LIMIT 1;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        command.Parameters.AddWithValue("$m", expected.ModelId);
        object? value = await command.ExecuteScalarAsync();
        if (value is not byte[] blob) return $"缺少 model_id={expected.ModelId} 的行";
        if (blob.Length != expected.Bytes) return $"model_id={expected.ModelId} 字节 {expected.Bytes}→{blob.Length}";
        if (Sha256Hex(blob) != expected.Sha256) return $"model_id={expected.ModelId} SHA256 变化";
        return null;
    }

    private static async Task AssertUnextractedAsync(string fingerprint, string label)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        foreach (string table in new[] { "photo_features", "photo_patches" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE fingerprint = $fp;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            long count = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
            if (count != 0) throw new InvalidOperationException($"{label} 已存在于 {table}");
        }
        await using var photos = connection.CreateCommand();
        photos.CommandText = "SELECT cv_grid FROM photos WHERE fingerprint = $fp;";
        photos.Parameters.AddWithValue("$fp", fingerprint);
        object? cv = await photos.ExecuteScalarAsync();
        if (cv is byte[] { Length: > 0 }) throw new InvalidOperationException($"{label} 已存在于 photos.cv_grid");
    }

    private static async Task<EditionState> CaptureDbAsync(string fingerprint)
    {
        var state = new EditionState { Fingerprint = fingerprint };
        await using var connection = PhotoDatabase.OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT model_id, cls_vector FROM photo_features WHERE fingerprint = $fp ORDER BY model_id;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                byte[] blob = (byte[])reader.GetValue(1);
                state.Features.Add(new EditionFeatureSnapshot
                {
                    ModelId = reader.GetString(0), Bytes = blob.Length, Sha256 = Sha256Hex(blob),
                });
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT model_id, patch_tokens FROM photo_patches WHERE fingerprint = $fp ORDER BY model_id;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                byte[] blob = (byte[])reader.GetValue(1);
                state.Patches.Add(new EditionFeatureSnapshot
                {
                    ModelId = reader.GetString(0), Bytes = blob.Length, Sha256 = Sha256Hex(blob),
                });
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cv_grid, cv_grid_spec, cv_image_width, cv_image_height FROM photos WHERE fingerprint = $fp;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
            {
                byte[] blob = (byte[])reader.GetValue(0);
                state.Cv = new EditionCvSnapshot
                {
                    Spec = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Width = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Height = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Bytes = blob.Length, Sha256 = Sha256Hex(blob),
                };
            }
        }
        return state;
    }

    private static void AssertSameDb(EditionState expected, EditionState actual)
    {
        if (Diff("photo_features", expected.Features, actual.Features) is { } features)
            throw new InvalidOperationException("photo_features 被改写：" + features);
        if (Diff("photo_patches", expected.Patches, actual.Patches) is { } patches)
            throw new InvalidOperationException("photo_patches 被改写：" + patches);
        if (Diff(expected.Cv, actual.Cv) is { } cv)
            throw new InvalidOperationException("photos.cv_grid 被改写：" + cv);
    }

    private static string? Diff(string label, List<EditionFeatureSnapshot> expected, List<EditionFeatureSnapshot> actual)
    {
        if (expected.Count != actual.Count) return $"{label} 行数 {expected.Count}→{actual.Count}";
        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].ModelId != actual[index].ModelId)
                return $"{label} 第 {index} 行 model_id {expected[index].ModelId}→{actual[index].ModelId}";
            if (expected[index].Bytes != actual[index].Bytes)
                return $"{label} model_id={expected[index].ModelId} 字节 {expected[index].Bytes}→{actual[index].Bytes}";
            if (expected[index].Sha256 != actual[index].Sha256)
                return $"{label} model_id={expected[index].ModelId} SHA256 变化";
        }
        return null;
    }

    private static string? Diff(EditionCvSnapshot? expected, EditionCvSnapshot? actual)
    {
        if (expected == null && actual == null) return null;
        if (expected == null) return "多出 CV 行";
        if (actual == null) return "CV 行丢失";
        if (expected.Spec != actual.Spec) return $"版本 {expected.Spec}→{actual.Spec}";
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"尺寸 {expected.Width}×{expected.Height}→{actual.Width}×{actual.Height}";
        if (expected.Bytes != actual.Bytes) return $"字节 {expected.Bytes}→{actual.Bytes}";
        if (expected.Sha256 != actual.Sha256) return "SHA256 变化";
        return null;
    }

    private static string Describe(EditionState state)
    {
        string features = string.Join(",", state.Features.Select(entry => $"{entry.ModelId}:{entry.Sha256[..12]}"));
        string patches = string.Join(",", state.Patches.Select(entry => $"{entry.ModelId}:{entry.Sha256[..12]}"));
        string cv = state.Cv == null ? "absent" : $"{state.Cv.Spec} {state.Cv.Width}x{state.Cv.Height} {state.Cv.Sha256[..12]}";
        return $"features=[{features}] patches=[{patches}] cv={cv}";
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> FingerprintAsync(ImageFile file)
        => await AnalysisDataReader.ComputeFingerprintAsync(file, CancellationToken.None)
            ?? throw new InvalidOperationException("无法计算照片指纹");

    private static string CreateGeneratedProbe(string label)
    {
        Directory.CreateDirectory(ProbeRoot);
        string path = Path.Combine(ProbeRoot, $"{label}-{Guid.NewGuid():N}.jpg");
        EnhancementAcceptance.CreateLowContrastJpeg(path);
        return path;
    }

    private static void ClearAcceptanceState()
    {
        TryDelete(StatePath);
        try { if (Directory.Exists(FixtureRoot)) Directory.Delete(FixtureRoot, true); } catch { }
        NSUserDefaults.StandardUserDefaults.RemoveObject(
            ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded);
        NSUserDefaults.StandardUserDefaults.Synchronize();
    }

    private static void CleanupAcceptanceState()
    {
        TryDelete(StatePath);
        try { if (Directory.Exists(FixtureRoot)) Directory.Delete(FixtureRoot, true); } catch { }
        var defaults = NSUserDefaults.StandardUserDefaults;
        defaults.RemoveObject(SettingsKey);
        defaults.RemoveObject(ViewStatePrefix + SettingsViewStateKeys.SimilarityPanelExpanded);
        defaults.Synchronize();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static async Task SaveStateAsync(EditionState state)
        => await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state, EditionStateJsonContext.Default.EditionState));

    private static async Task<EditionState> LoadStateAsync()
    {
        if (!File.Exists(StatePath))
            throw new InvalidOperationException("跨进程状态缺失，请先运行 --pv-native-edition-prepare");
        return JsonSerializer.Deserialize(await File.ReadAllTextAsync(StatePath), EditionStateJsonContext.Default.EditionState)
            ?? throw new InvalidOperationException("跨进程状态无法解析");
    }

    private static Task WriteReportAsync(string path, List<string> lines, bool append)
        => append
            ? File.AppendAllLinesAsync(path, lines)
            : File.WriteAllLinesAsync(path, lines);

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
        for (int index = 0; index < a.Length; index++)
            if (BitConverter.SingleToInt32Bits(a[index]) != BitConverter.SingleToInt32Bits(b[index])) return false;
        return true;
    }

    private static async Task RestoreLocalAsync(iOSNativeRootViewController root, MainViewModel main,
        AppSettings snapshot, bool analysis, bool film, bool control, ImageFile? originalFile)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.Settings.ApplySnapshot(snapshot);
            main.IsAnalysisViewVisible = analysis;
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            if (originalFile != null) main.CurrentFile = originalFile;
        });
        await SettleAsync(root);
    }

    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() => root.View!.LayoutIfNeeded());
        await Task.Delay(Settle);
    }

    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("等待超时：" + label);
    }
}

internal sealed class EditionState
{
    public string FixtureRelativePath { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public bool Zoom { get; set; }
    public double SimilarityThreshold { get; set; }
    public int SimilarityMaxResults { get; set; }
    public bool ImmediateDino { get; set; }
    public bool ImmediateCv { get; set; }
    public bool SimilarityPanelExpanded { get; set; }
    public List<EditionFeatureSnapshot> Features { get; set; } = [];
    public List<EditionFeatureSnapshot> Patches { get; set; } = [];
    public EditionCvSnapshot? Cv { get; set; }
}

internal sealed class EditionFeatureSnapshot
{
    public string ModelId { get; set; } = "";
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class EditionCvSnapshot
{
    public string? Spec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EditionState))]
internal partial class EditionStateJsonContext : JsonSerializerContext;
