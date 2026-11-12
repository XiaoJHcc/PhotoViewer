using System.Diagnostics;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-native-form-check &lt;目录&gt;</c> 核对主屏形态骨架——
/// iPhone 竖屏与两种横屏刘海方向（真实 <c>RequestGeometryUpdate</c> 旋转）、iPad 四种布局模式与
/// 1/3、1/2、2/3 限宽分屏（舞台容器 + 紧凑尺寸类别覆盖），每个配置遍历栏位显隐组合（含手机 EXIF 开关），断言：
/// 形态判定、手机槽位与实际视图 frame、主图适应区内边距与满舞台、适应区与可见控件/栏背景不相交、
/// 按钮命中区、圆角与刘海安全区（贴角耳区按设计允许伸进刘海旁的显示区）、胶片条两端停靠与横屏滚动边缘效果、
/// 胶片条两档选档 / 带厚 / 可见容量（记录实际值，不足 5 张按停止规则记 NOTE）与小档星级角标与抖动点、
/// 筛选与更多常驻（竖屏顶行耳区 vs 带 EXIF 的行位置、横屏合体 vs 胶片栏关闭后的上下耳区）、
/// EXIF 块（竖屏带衬底整块、横屏无衬底且只显示放得下的前几项）、横屏控制栏（外缘 EdgeMargin、次序 EXIF→星级→缩放增强、
/// 几何与胶片栏/上下张开关无关）、横屏镜像、筛选与更多菜单结构（手机形态含 EXIF 开关）、
/// 切换保持当前照片与缩放位置、放大后照片垫在栏位下方。带 <c>--pv-native-ui-snapshots</c> 时把阶段名写入
/// <c>Documents/PhotoViewer/native-ui-snapshot-stage.txt</c> 并停留供外部截图。
/// 需要与 <c>--pv-open &lt;同一目录&gt;</c> 一起使用；所有临时设置、栏位显隐、EXIF 开关、方向与覆盖在收尾还原。
/// 报告写 Documents/PhotoViewer/native-form-checks.txt，打印 <c>[NativeSmoke] PASS form (n checks)</c> / FAIL。
/// </summary>
internal static class iOSNativeFormCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-form-check";
    /// <summary>截图检查点开关。</summary>
    private const string SnapshotsSwitch = "--pv-native-ui-snapshots";
    /// <summary>截图检查点停留时间（覆盖外部轮询与截图）。</summary>
    private static readonly TimeSpan StageHold = TimeSpan.FromMilliseconds(2500);
    /// <summary>几何比较允许误差（点）。</summary>
    private const double Tolerance = .5;
    /// <summary>交互控件最小命中边长（设计令牌 44 点）。</summary>
    private static readonly double HitSize = DesignTokens.ShellGlassButtonSize;

    /// <summary>配置类型：真实旋转、真实窗口布局模式、限宽分屏。</summary>
    private enum SetupKind
    {
        /// <summary>iPhone 真实旋转到指定方向。</summary>
        PhoneOrientation,
        /// <summary>iPad 真实窗口下的布局模式。</summary>
        LayoutMode,
        /// <summary>iPad 限宽舞台 + 紧凑尺寸类别模拟分屏。</summary>
        Split,
    }

    /// <summary>一个形态配置：名称、准备方式、目标方向/模式/宽度比例与期望形态。</summary>
    private sealed record Config(string Name, SetupKind Kind, UIInterfaceOrientationMask Mask, LayoutMode Mode,
        double StageFraction, ScreenForm Expected);

    /// <summary>一个栏位显隐组合：栏位开关、上下张、手机 EXIF 开关与"清空 EXIF 显示项"。</summary>
    private sealed record Combo(string Name, bool Film, bool Control, bool Analysis, bool ShowNavigation, bool Exif, bool NoExif);

    private static readonly Combo[] Combos =
    [
        new("all", true, true, false, true, true, false),
        new("film-hidden", false, true, false, true, true, false),
        new("control-hidden", true, false, false, true, true, false),
        new("analysis-shown", true, true, true, true, true, false),
        new("no-navigation", true, true, false, false, true, false),
        new("no-exif", true, true, false, true, true, true),
        new("exif-hidden", true, true, false, true, false, false),
        new("all-hidden", false, false, false, true, false, false),
    ];

    private static readonly Combo ShownCombo = new("all-shown", true, true, true, true, true, false);

    /// <summary>iPhone 横屏刘海在左时的槽位（供另一方向做镜像比对）。</summary>
    private static PhoneLayout? notchLeftSlots;
    private static double notchLeftWidth;
    /// <summary>横屏控制栏的基准槽位（胶片栏、上下张与 EXIF 全开时），供各开关关闭态比对。</summary>
    private static PhoneLayout? controlColumnSlots;
    /// <summary>筛选菜单写回只做一次（其余组合只核对菜单结构）。</summary>
    private static bool filterMutationChecked;

    /// <summary>解析验收开关；命中即以给定目录启动异步核对。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) directory = args[index + 1];
        }
        if (directory == null) return;
        bool snapshots = args.Contains(SnapshotsSwitch, StringComparer.Ordinal);
        _ = RunAsync(root, session, ResolveDocumentsPath(directory), snapshots);
    }

    /// <summary>相对路径按沙盒 Documents 解析（与壳层 --pv-open 同一口径）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：载入素材 → 逐配置/组合断言 → 截图检查点 → 收尾还原 → 落盘。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    /// <param name="snapshots">是否写截图检查点并停留。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory, bool snapshots)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-form-checks.txt");
        AppSettings? settings = null;
        bool entryFilm = true, entryControl = true, entryAnalysis = false;
        bool entryExifVisible = true;
        var entryExif = new HashSet<string>(StringComparer.Ordinal);
        var entryOrientation = UIInterfaceOrientation.Unknown;
        var entrySort = SortMode.Name;
        var entryOrder = SortOrder.Ascending;
        string entryRatingFilter = "All";
        MainViewModel? main = null;
        try
        {
            main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            await WaitAsync(() => root.ViewIfLoaded?.Window != null && root.Stage.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length == 0) throw new InvalidOperationException($"形态验收需要至少一张自有夹具：{directory}");
            settings = main.Settings.CreateSnapshot();
            entryFilm = main.IsThumbnailViewVisible;
            entryControl = main.IsControlViewVisible;
            entryAnalysis = main.IsAnalysisViewVisible;
            entryExifVisible = main.IsExifViewVisible;
            // 本轮规则以 EXIF 开关打开为默认核对态（竖屏顶行带 EXIF、横屏控制栏带 EXIF 列），收尾还原进入时的值。
            await UiDispatcher.InvokeAsync(() => main.IsExifViewVisible = true);
            foreach (var item in main.Settings.ExifDisplayItems)
                if (item.IsEnabled) entryExif.Add(item.PropertyName);
            // 进入时可能没有任何 EXIF 显示项（历史验收会话留下的隔离设置）：临时启用默认项核对 EXIF 列，
            // 收尾仍按进入时的真实状态还原。
            var workingExif = new HashSet<string>(entryExif, StringComparer.Ordinal);
            if (workingExif.Count == 0)
            {
                foreach (var field in SettingsCatalog.DefaultExifFields)
                    if (field.IsEnabled) workingExif.Add(field.Id);
            }
            entryOrientation = CurrentOrientation(root);
            entrySort = main.FileVM.FilterBar.SortMode;
            entryOrder = main.FileVM.FilterBar.SortOrder;
            entryRatingFilter = main.FileVM.FilterBar.SelectedRatingFilter;

            await main.FolderVM.OpenFolderAsync(new LocalFolderRef(directory));
            await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
            await SettleAsync(root);
            bool phoneDevice = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
            lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），设备={(phoneDevice ? "iPhone" : "iPad")}，" +
                $"窗口 {Size(root.View!.Bounds.Size)}，安全区 {Insets(root.View!.SafeAreaInsets)}，起始方向 {entryOrientation}");

            var configs = BuildConfigs(phoneDevice);
            foreach (var config in configs)
            {
                await ApplyConfigAsync(root, main, config);
                lines.Add($"PASS 配置 {config.Name}：形态 {root.Form}，窗口 {Size(root.Stage.Bounds.Size)}，" +
                    $"方向 {CurrentOrientation(root)}，刘海在左={root.NotchOnLeft}");
                if (root.Form != ScreenForm.Wide) await VerifyFilmTierAsync(root, main, config.Name, lines);
                foreach (var combo in Combos)
                {
                    await ApplyComboAsync(root, main, combo, workingExif);
                    var scope = $"{config.Name}/{combo.Name}";
                    VerifyForm(root, config, scope);
                    VerifyGeometry(root, main, scope);
                    VerifyFilters(root, scope);
                    VerifyMenus(root, main, scope);
                    if (IsLandscapeForm(root) && combo.Control) VerifyControlBar(root, combo, scope);
                    if (config.Kind == SetupKind.Split && combo.Name == "all")
                        VerifySplitStage(root, config, scope);
                    lines.Add($"PASS {scope}：形态/槽位/避让/可见性/命中区/圆角安全区/胶片条两端/筛选与更多常驻/菜单结构通过" +
                        $"（胶片{OnOff(combo.Film)} 控制{OnOff(combo.Control)} 分析{OnOff(combo.Analysis)}" +
                        $"{(combo.ShowNavigation ? " 有导航" : " 无导航")} EXIF开关{OnOff(combo.Exif)}{(combo.NoExif ? " 无EXIF项" : "")}）");
                    if (snapshots && combo.Name == "all") await HoldAsync(config.Name, lines);
                }
                if (IsLandscapeForm(root))
                {
                    if (root.NotchOnLeft)
                    {
                        notchLeftSlots = root.PhoneSlots;
                        notchLeftWidth = root.Stage.Bounds.Width;
                        lines.Add($"PASS {config.Name}：记录刘海在左的槽位（宽 {notchLeftWidth:0.#}）供另一方向镜像比对");
                    }
                    else
                    {
                        VerifyMirror(root, config.Name, lines);
                    }
                }
            }

            await VerifySwitchKeepsPhotoAsync(root, main, configs);
            await VerifyZoomedAsync(root, lines);
            if (!filterMutationChecked) await VerifyFilterMutationAsync(root, main, lines);

            if (snapshots && phoneDevice) await CaptureStateShotsAsync(root, main, workingExif, lines);
            if (snapshots && phoneDevice) await CaptureZoomedShotAsync(root, main, workingExif, lines);

            await RestoreAsync(root, main, settings, entryFilm, entryControl, entryAnalysis, entryExifVisible, entryExif,
                entryOrientation, entrySort, entryOrder, entryRatingFilter);
            lines.Add("PASS 收尾还原：方向、舞台、尺寸类别覆盖、布局模式、栏位显隐、EXIF 开关、EXIF 显示项、筛选回到进入本检查时的状态");
            if (snapshots) await WriteStageAsync("done");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS form ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            if (snapshots) await WriteStageAsync("done");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL form " + exception);
            if (main != null && settings != null)
            {
                try
                {
                    await RestoreAsync(root, main, settings, entryFilm, entryControl, entryAnalysis, entryExifVisible,
                        entryExif, entryOrientation, entrySort, entryOrder, entryRatingFilter);
                }
                catch (Exception restoreFailure)
                {
                    Console.Error.WriteLine("[NativeSmoke] form 还原失败 " + restoreFailure.Message);
                }
            }
        }
    }

    /// <summary>按当前设备给出配置清单：iPhone 三种方向；iPad 四种布局模式加三档限宽分屏。</summary>
    /// <param name="phoneDevice">是否 iPhone。</param>
    /// <returns>配置清单。</returns>
    private static Config[] BuildConfigs(bool phoneDevice) => phoneDevice
        ?
        [
            new("iphone-portrait", SetupKind.PhoneOrientation, UIInterfaceOrientationMask.Portrait, LayoutMode.Auto, 0, ScreenForm.PhonePortrait),
            new("iphone-landscape-notch-left", SetupKind.PhoneOrientation, UIInterfaceOrientationMask.LandscapeRight, LayoutMode.Auto, 0, ScreenForm.PhoneLandscape),
            new("iphone-landscape-notch-right", SetupKind.PhoneOrientation, UIInterfaceOrientationMask.LandscapeLeft, LayoutMode.Auto, 0, ScreenForm.PhoneLandscape),
        ]
        :
        [
            new("ipad-row", SetupKind.LayoutMode, default, LayoutMode.Row, 0, ScreenForm.Wide),
            new("ipad-col", SetupKind.LayoutMode, default, LayoutMode.Col, 0, ScreenForm.Wide),
            new("ipad-auto", SetupKind.LayoutMode, default, LayoutMode.Auto, 0, ScreenForm.Wide),
            new("ipad-phone", SetupKind.LayoutMode, default, LayoutMode.Phone, 0, ScreenForm.PhonePortrait),
            new("ipad-split-1-3", SetupKind.Split, default, LayoutMode.Auto, 1.0 / 3, ScreenForm.PhonePortrait),
            new("ipad-split-1-2", SetupKind.Split, default, LayoutMode.Auto, 0.5, ScreenForm.PhonePortrait),
            new("ipad-split-2-3", SetupKind.Split, default, LayoutMode.Auto, 2.0 / 3, ScreenForm.PhonePortrait),
        ];

    /// <summary>应用一个配置：真实旋转、布局模式或限宽分屏，并等待形态与窗口到位。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="config">配置。</param>
    private static async Task ApplyConfigAsync(iOSNativeRootViewController root, MainViewModel main, Config config)
    {
        switch (config.Kind)
        {
            case SetupKind.PhoneOrientation:
                await UiDispatcher.InvokeAsync(() =>
                {
                    root.StageWidthFraction = null;
                    root.TraitOverrides.RemoveTrait<UITraitHorizontalSizeClass>();
                    main.Settings.LayoutMode = LayoutMode.Auto;
                    root.SupportedOrientationsOverride = config.Mask;
                    root.SetNeedsUpdateOfSupportedInterfaceOrientations();
                });
                await RequestOrientationAsync(root, config.Mask);
                break;
            case SetupKind.LayoutMode:
                await UiDispatcher.InvokeAsync(() =>
                {
                    root.StageWidthFraction = null;
                    root.TraitOverrides.RemoveTrait<UITraitHorizontalSizeClass>();
                    main.Settings.LayoutMode = config.Mode;
                });
                break;
            case SetupKind.Split:
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.LayoutMode = LayoutMode.Auto;
                    root.StageWidthFraction = config.StageFraction;
                    root.TraitOverrides.HorizontalSizeClass = UIUserInterfaceSizeClass.Compact;
                });
                await WaitAsync(() =>
                {
                    double expected = (double)root.View!.Bounds.Width * config.StageFraction;
                    return Math.Abs((double)root.Stage.Bounds.Width - expected) <= 1;
                }, $"{config.Name} 舞台宽度");
                break;
        }
        await WaitAsync(() => root.Form == config.Expected,
            $"{config.Name} 期望形态 {config.Expected}，实际 {root.Form}（窗口 {Size(root.Stage.Bounds.Size)}，" +
            $"尺寸类别 {TraitSizes(root)}，布局模式 {main.Settings.LayoutMode}）");
        await SettleAsync(root);
    }

    /// <summary>请求系统旋转到指定方向并等待界面方向到位；超时把接受状态与实际方向写进失败原因。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="mask">目标方向掩码（单一方向）。</param>
    private static async Task RequestOrientationAsync(iOSNativeRootViewController root, UIInterfaceOrientationMask mask)
    {
        var expected = mask switch
        {
            UIInterfaceOrientationMask.Portrait => UIInterfaceOrientation.Portrait,
            UIInterfaceOrientationMask.LandscapeRight => UIInterfaceOrientation.LandscapeRight,
            UIInterfaceOrientationMask.LandscapeLeft => UIInterfaceOrientation.LandscapeLeft,
            _ => throw new InvalidOperationException($"形态验收只支持单一方向请求：{mask}"),
        };
        var scene = root.View!.Window?.WindowScene
            ?? throw new InvalidOperationException("主屏尚未连接到 window scene，无法请求旋转");
        bool accepted = false;
        string failure = "未收到回调";
        scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(mask), error =>
        {
            accepted = error == null;
            failure = error?.LocalizedDescription ?? string.Empty;
        });
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100);
            if (CurrentOrientation(root) == expected) break;
        }
        var actual = CurrentOrientation(root);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"方向切换超时：请求 {mask}，实际 {actual}（accepted={accepted}，error={failure}，" +
                $"窗口 {Size(root.View!.Bounds.Size)}，形态 {root.Form}）");
        }
        await SettleAsync(root);
    }

    /// <summary>应用一个栏位显隐组合（含手机 EXIF 开关与临时清空 EXIF 显示项）并等待布局落地。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="combo">组合。</param>
    /// <param name="entryExif">进入检查时启用的 EXIF 项。</param>
    private static async Task ApplyComboAsync(iOSNativeRootViewController root, MainViewModel main, Combo combo, HashSet<string> entryExif)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.Settings.ShowNavigationButtons = combo.ShowNavigation;
            main.IsThumbnailViewVisible = combo.Film;
            main.IsControlViewVisible = combo.Control;
            main.IsAnalysisViewVisible = combo.Analysis;
            main.IsExifViewVisible = combo.Exif;
            foreach (var item in main.Settings.ExifDisplayItems)
            {
                bool enabled = !combo.NoExif && entryExif.Contains(item.PropertyName);
                if (item.IsEnabled != enabled) item.IsEnabled = enabled;
            }
            root.Photo.FitToView();
        });
        await WaitAsync(() =>
        {
            if (main.IsThumbnailViewVisible != combo.Film || main.IsControlViewVisible != combo.Control
                || main.IsAnalysisViewVisible != combo.Analysis || main.Settings.ShowNavigationButtons != combo.ShowNavigation
                || main.IsExifViewVisible != combo.Exif)
                return false;
            if (combo.NoExif ? main.ControlVM.EnabledExifItems.Count != 0 : entryExif.Count > 0 && main.ControlVM.EnabledExifItems.Count == 0)
                return false;
            if (root.Form == ScreenForm.Wide)
                return root.FileBarView.Hidden == !combo.Film && root.ControlScroll.Hidden == !combo.Control
                    && root.AnalysisPane.Hidden == !combo.Analysis;
            return root.PhoneSlotsInput.FilmVisible == combo.Film && root.PhoneSlotsInput.ControlVisible == combo.Control
                && root.PhoneSlotsInput.AnalysisVisible == combo.Analysis
                && root.PhoneSlotsInput.ShowNavigation == combo.ShowNavigation
                && root.PhoneSlotsInput.ExifVisible == combo.Exif
                && root.AnalysisPane.Hidden == !combo.Analysis;
        }, "栏位显隐落地");
        await SettleAsync(root);
    }

    /// <summary>断言形态判定与配置期望一致，并核对手机槽位输入的方向分量。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="config">配置。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyForm(iOSNativeRootViewController root, Config config, string scope)
    {
        if (root.Form != config.Expected)
            throw new InvalidOperationException($"{scope} 形态判定 {root.Form} ≠ 期望 {config.Expected}");
        if (root.Form == ScreenForm.Wide) return;
        var input = root.PhoneSlotsInput;
        bool landscape = root.Form == ScreenForm.PhoneLandscape;
        if (input.Landscape != landscape)
            throw new InvalidOperationException($"{scope} 手机槽位输入排布 {input.Landscape} 与形态 {root.Form} 不符");
        if (input.NotchOnLeft != root.NotchOnLeft)
            throw new InvalidOperationException($"{scope} 槽位输入刘海方向 {input.NotchOnLeft} 与判定 {root.NotchOnLeft} 不符");
        if (landscape && root.NotchOnLeft != (CurrentOrientation(root) != UIInterfaceOrientation.LandscapeLeft))
            throw new InvalidOperationException(
                $"{scope} 刘海方向与界面方向不符：方向 {CurrentOrientation(root)}，刘海在左={root.NotchOnLeft}");
    }

    /// <summary>
    /// 手机形态胶片条两档（[FILM-05]）：按胶片条主轴长度复核共享选档、槽位输入里的带厚与可见容量（记录实际值），
    /// 首个可见卡片的 frame 尺寸等于该档卡片尺寸；小档卡片必须带星级角标与抖动点，0 星且无冲突时角标隐藏。
    /// 可见容量不足 5 张时按计划停止规则记 NOTE（配置、方向、窗口、主轴长度、容量与差值），不调整档位尺寸。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="scope">报告范围名。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyFilmTierAsync(iOSNativeRootViewController root, MainViewModel main, string scope,
        List<string> lines)
    {
        var film = root.Filmstrip;
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        // 胶片条只在可见时跟随形态换轴与换档：上一条组合可能刚把胶片栏关掉，这里先恢复再核对。
        if (!main.IsThumbnailViewVisible)
        {
            await UiDispatcher.InvokeAsync(() => main.IsThumbnailViewVisible = true);
            await SettleAsync(root);
        }
        var stage = root.Stage.Bounds;
        double mainLength = portrait ? (double)stage.Width : (double)stage.Height;
        var tier = FilmCardGeometry.ResolveTier(mainLength, portrait);
        if (film.Tier != tier)
            throw new InvalidOperationException(
                $"{scope} 卡片档位 {film.Tier} 与共享选档 {tier} 不一致（胶片条主轴 {mainLength:0.##}）");
        if (film.Horizontal != portrait)
            throw new InvalidOperationException($"{scope} 胶片条轴向 {(film.Horizontal ? "横" : "纵")} 与形态 {root.Form} 不符");
        double band = FilmCardGeometry.BandThickness(tier, portrait);
        if (Math.Abs((double)root.PhoneSlotsInput.FilmBand - band) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 槽位输入带厚 {root.PhoneSlotsInput.FilmBand:0.##} ≠ 共享带厚 {band:0.##}（{tier}）");
        int capacity = FilmCardGeometry.VisibleCapacity(mainLength, tier, portrait);
        bool enough = capacity >= FilmCardGeometry.MinVisible;
        if (!enough)
        {
            // 计划停止规则：某一配置放不下 5 张时记录设备/方向/主轴长度/容量/差值，不改档位尺寸。
            lines.Add(
                $"NOTE {scope} 胶片条可见容量 {capacity} 少于 {FilmCardGeometry.MinVisible} 张：窗口 {Size(stage.Size)}、" +
                $"{(portrait ? "竖屏" : "横屏")}、胶片条主轴 {mainLength:0.##}、{tier} 步长 {FilmCardGeometry.Step(tier, portrait):0.##}、" +
                $"差 {FilmCardGeometry.MinVisible * FilmCardGeometry.Step(tier, portrait) - mainLength:0.##} 点（按计划停止规则不改档位尺寸，留作者裁定）");
        }
        await SettleAsync(root);
        var collection = film.Collection;
        collection.LayoutIfNeeded();
        var visible = collection.IndexPathsForVisibleItems.Select(path => (int)path.Item).Order().ToArray();
        if (visible.Length == 0) throw new InvalidOperationException($"{scope} 胶片条没有可见卡片");
        int firstIndex = visible[0];
        double cardWidth = FilmCardGeometry.Width(tier), cardHeight = FilmCardGeometry.Height(tier);
        var frame = film.FrameAt(firstIndex)
            ?? throw new InvalidOperationException($"{scope} 首个可见卡片 {firstIndex} 缺少布局几何");
        if (Math.Abs((double)frame.Width - cardWidth) > Tolerance || Math.Abs((double)frame.Height - cardHeight) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 首个可见卡片尺寸 {Size(frame)} ≠ {tier} 档 {cardWidth:0.##}×{cardHeight:0.##}");
        if (Math.Abs((double)(portrait ? film.Frame.Height : film.Frame.Width) - band) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 胶片条交叉轴 {(portrait ? (double)film.Frame.Height : (double)film.Frame.Width):0.##} ≠ 带厚 {band:0.##}");
        // 卡片视图（复用池里可能还没建好）：等首个可见卡片的实例出现再核对档位内部视图。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        iOSThumbnailCard? card;
        while ((card = film.CardAt(firstIndex)) == null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{scope} 首个可见卡片视图未创建（索引 {firstIndex}）");
            await Task.Delay(25);
            collection.LayoutIfNeeded();
        }
        string detail;
        if (tier == FilmCardTier.Small)
        {
            // 逐个核对小档外观：角标/抖动点存在、六格星排隐藏、角标只在有星级或有冲突时可见。
            void RequireSmallLook(iOSThumbnailCard target, string where)
            {
                if (target.SmallRatingBadge.Superview == null || target.ShakeDot.Superview == null)
                    throw new InvalidOperationException($"{scope} {where} 未带星级角标与抖动点");
                if (target.StarViews.Any(star => !star.Hidden))
                    throw new InvalidOperationException($"{scope} {where} 仍显示六格星排");
                var bound = target.BoundFile ?? throw new InvalidOperationException($"{scope} {where} 未绑定照片");
                bool visible = Math.Max(0, bound.Rating) > 0 || bound.HasRatingConflict;
                if (target.SmallRatingBadge.Hidden == visible)
                    throw new InvalidOperationException(
                        $"{scope} {where} 星级角标可见性不符：Hidden={target.SmallRatingBadge.Hidden}，星级 {bound.Rating}、冲突 {bound.HasRatingConflict}");
                if (visible && Math.Abs((double)target.SmallRatingBadge.Frame.Height - DesignTokens.FilmCardSmallBadgeHeight) > Tolerance)
                    throw new InvalidOperationException($"{scope} {where} 星级角标高度 {target.SmallRatingBadge.Frame.Height} 不是 12 点");
            }
            async Task<iOSThumbnailCard> WaitForIndexAsync(int index)
            {
                var watch = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    collection.LayoutIfNeeded();
                    if (film.CardAt(index) is { BoundFile: not null } created) return created;
                    if (DateTime.UtcNow > watch)
                        throw new TimeoutException($"{scope} 卡片视图未创建（索引 {index}）");
                    await Task.Delay(25);
                }
            }

            RequireSmallLook(card, $"首个可见卡片 {firstIndex}");
            bool sawZero = false;
            foreach (var index in visible)
            {
                if (film.CardAt(index) is not { BoundFile: { } bound } candidate) continue;
                RequireSmallLook(candidate, $"卡片 {index}");
                sawZero |= Math.Max(0, bound.Rating) == 0 && !bound.HasRatingConflict;
            }
            if (!sawZero)
            {
                // 可见范围里没有 0 星照片时滚到一张 0 星（无冲突）卡片上，明确观察"角标隐藏"。
                var files = main.FileVM.ThumbnailList.FilteredFiles;
                int zeroIndex = -1;
                for (int index = 0; index < files.Count; index++)
                {
                    if (files[index].Rating <= 0 && !files[index].HasRatingConflict) { zeroIndex = index; break; }
                }
                if (zeroIndex >= 0)
                {
                    collection.ScrollToItem(NSIndexPath.FromItemSection(zeroIndex, 0), UICollectionViewScrollPosition.None, false);
                    RequireSmallLook(await WaitForIndexAsync(zeroIndex), $"0 星卡片 {zeroIndex}");
                    sawZero = true;
                }
            }
            if (!sawZero) lines.Add($"NOTE {scope} 夹具内没有 0 星且无冲突的照片，未能观察小档角标隐藏");
            detail = sawZero ? "小档 64×76，0 星时角标隐藏、抖动点存在" : "小档 64×76，星级角标与抖动点可见";
        }
        else
        {
            if (!card.SmallRatingBadge.Hidden || !card.ShakeDot.Hidden)
                throw new InvalidOperationException($"{scope} 大档卡片显示了小档角标/抖动点");
            if (main.Settings.ShowRating && card.StarViews.All(star => star.Hidden))
                throw new InvalidOperationException($"{scope} 大档卡片显示评分时星排却全部隐藏");
            detail = "大档 96×144，六格星排";
        }
        lines.Add($"PASS {scope} 胶片条：主轴 {mainLength:0.##} → {tier}（可见容量 {capacity}、带厚 {band:0.##}），" +
            $"首个可见卡片 {Size(frame)}、{detail}");
    }

    /// <summary>核对槽位/分区避让、主图满舞台、适应区不相交、命中区、圆角安全区与胶片条两端。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="combo">组合。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyGeometry(iOSNativeRootViewController root, MainViewModel main, string scope)
    {
        if (root.Form != ScreenForm.Wide)
        {
            VerifyPhoneGeometry(root, main, scope);
            VerifyCornerRegion(root, scope);
            VerifyFilmstripEdges(root, scope);
        }
        else
        {
            VerifyWideGeometry(root, scope);
        }
        VerifyFitPlacement(root, scope);
        VerifyFitAvoidance(root, scope);
        if (root.Form != ScreenForm.Wide) VerifyHitTargets(root, scope);
    }

    /// <summary>
    /// 手机形态：槽位与实际视图 frame 逐项一致、筛选/更多常驻与合体/单按钮切换、EXIF 块（项数、衬底与整项裁剪）、
    /// 空槽位视图隐藏、槽位输入可复算、主图满舞台。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyPhoneGeometry(iOSNativeRootViewController root, MainViewModel main, string scope)
    {
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        var slots = root.PhoneSlots;
        var recomputed = PhoneScreenLayout.Compute(root.PhoneSlotsInput);
        if (recomputed != slots)
            throw new InvalidOperationException($"{scope} 槽位输入复算结果与最近一次布局不一致");
        if (root.Photo.Frame != root.Stage.Bounds)
            throw new InvalidOperationException($"{scope} 主图视图未铺满舞台：{CGRect(root.Photo.Frame)} vs {CGRect(root.Stage.Bounds)}");
        var bars = root.PhoneBarBackgrounds;
        // 横屏有胶片栏时筛选与更多是合体按钮的两段，胶片栏关闭后是上下耳区的独立圆按钮。
        bool group = !portrait && Has(slots.FilterMoreGroup);
        var entries = new (string Name, LayoutRect Slot, UIView View)[]
        {
            ("Filter", slots.Filter, group ? root.PhoneFilterSegment : root.PhoneFilterButton),
            ("More", slots.More, group ? root.PhoneMoreSegment : root.PhoneMoreButton),
            ("FilterMoreGroup", slots.FilterMoreGroup, root.PhoneFilterMoreGroup),
            ("Exif", slots.Exif, root.PhoneExifBlock),
            ("Navigation", slots.Navigation, root.PhoneNavigationGroup),
            ("Rating", slots.Rating, root.PhoneRatingControl),
            ("FitEnhance", slots.FitEnhance, root.PhoneFitEnhanceGroup),
            ("Filmstrip", slots.Filmstrip, root.Filmstrip),
            ("Analysis", slots.Analysis, root.AnalysisPane),
            ("TopBar", slots.TopBar, bars.Top),
            ("BottomBar", slots.BottomBar, bars.Bottom),
            ("LeadingBar", slots.LeadingBar, bars.Leading),
            ("TrailingBar", slots.TrailingBar, bars.Trailing),
        };
        foreach (var (name, slot, view) in entries)
        {
            bool has = Has(slot);
            if (view.Hidden == has)
                throw new InvalidOperationException($"{scope} {name} 可见性不符：Hidden={view.Hidden}，槽位={Rect(slot)}");
            if (!has) continue;
            var actual = FrameInStage(root, view);
            if (Math.Abs((double)actual.X - slot.X) > Tolerance || Math.Abs((double)actual.Y - slot.Y) > Tolerance
                || Math.Abs((double)actual.Width - slot.Width) > Tolerance || Math.Abs((double)actual.Height - slot.Height) > Tolerance)
                throw new InvalidOperationException($"{scope} {name} frame {CGRect(actual)} 与槽位 {Rect(slot)} 不一致");
        }
        // 筛选与更多常驻：合体模式用两段，单按钮模式用圆按钮，二者不会同时出现。
        if (group)
        {
            if (!root.PhoneFilterButton.Hidden || !root.PhoneMoreButton.Hidden)
                throw new InvalidOperationException($"{scope} 横屏合体模式下仍显示独立筛选/更多圆按钮");
        }
        else
        {
            if (!root.PhoneFilterMoreGroup.Hidden || !root.PhoneFilterSegment.Hidden || !root.PhoneMoreSegment.Hidden)
                throw new InvalidOperationException($"{scope} 合体按钮隐藏时其容器或段仍可见");
            if (root.PhoneFilterButton.Hidden || root.PhoneMoreButton.Hidden)
                throw new InvalidOperationException($"{scope} 筛选/更多未常驻：筛选隐藏={root.PhoneFilterButton.Hidden}，更多隐藏={root.PhoneMoreButton.Hidden}");
        }

        VerifyPhoneExif(root, main, slots, portrait, scope);
    }

    /// <summary>
    /// EXIF 块：项数等于设置里的启用显示项；竖屏是带衬底的整块、横屏是无衬底列且只显示放得下的前几项
    /// （末尾项隐藏，增加一项就会压到星级）；显示与否按共享 <see cref="PhoneScreenLayout.PortraitExifShown"/> 与
    /// <see cref="PhoneScreenLayout.LandscapeExifShown"/>。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="slots">当前槽位。</param>
    /// <param name="portrait">是否竖屏。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyPhoneExif(iOSNativeRootViewController root, MainViewModel main, PhoneLayout slots,
        bool portrait, string scope)
    {
        var input = root.PhoneSlotsInput;
        var block = root.PhoneExifBlock;
        var enabledExif = main.ControlVM.EnabledExifItems;
        if (block.ItemLabels.Count != enabledExif.Count)
            throw new InvalidOperationException(
                $"{scope} EXIF 块项数 {block.ItemLabels.Count} ≠ 启用显示项 {enabledExif.Count}");
        for (int index = 0; index < enabledExif.Count; index++)
        {
            if (block.ItemLabels[index].Text != enabledExif[index].DisplayName
                || block.ItemValues[index].Text != enabledExif[index].DisplayValue)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块第 {index + 1} 项与共享条目不一致：{block.ItemLabels[index].Text} " +
                    $"{block.ItemValues[index].Text}");
        }
        bool expectedShown = portrait
            ? PhoneScreenLayout.PortraitExifShown(in input)
            : PhoneScreenLayout.LandscapeExifShown(in input);
        if (Has(slots.Exif) != expectedShown)
            throw new InvalidOperationException(
                $"{scope} EXIF 槽位可见性 {Has(slots.Exif)} 与共享判定 {expectedShown} 不一致（开关={input.ExifVisible}，项数={input.ExifCount}）");
        if (!expectedShown)
        {
            if (!block.Hidden)
                throw new InvalidOperationException($"{scope} EXIF 不显示时块仍可见");
            return;
        }
        if (block.ShownItemCount != slots.ExifItems)
            throw new InvalidOperationException(
                $"{scope} EXIF 实际显示 {block.ShownItemCount} 项 ≠ 槽位放得下的 {slots.ExifItems} 项");
        for (int index = 0; index < block.ItemLabels.Count; index++)
        {
            bool visible = index < block.ShownItemCount;
            if (block.ItemLabels[index].Hidden == visible)
                throw new InvalidOperationException(
                    $"{scope} EXIF 第 {index + 1} 项可见性 {!block.ItemLabels[index].Hidden} 与显示项数 {block.ShownItemCount} 不符");
        }
        if (block.Bare == portrait)
            throw new InvalidOperationException($"{scope} EXIF 块衬底模式 {block.Bare} 与形态 {(portrait ? "竖屏" : "横屏")} 不符");
        if (portrait)
        {
            var intrinsic = ExifBlockGeometry.Size(enabledExif.Count, horizontal: true);
            if (slots.Exif.Width > intrinsic.Width + Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 竖屏 EXIF 块宽 {slots.Exif.Width:0.#} 超过固有宽 {intrinsic.Width:0.#}");
            if (block.Background.Hidden)
                throw new InvalidOperationException($"{scope} 竖屏 EXIF 块缺少模糊衬底");
            if (Math.Abs((double)block.Layer.CornerRadius - ExifBlockGeometry.CornerRadius) > Tolerance)
                throw new InvalidOperationException($"{scope} 竖屏 EXIF 块圆角 {block.Layer.CornerRadius} ≠ {ExifBlockGeometry.CornerRadius:0.#}");
        }
        else
        {
            var bare = ExifBlockGeometry.Size(enabledExif.Count, horizontal: false, bare: true);
            if (Math.Abs(slots.Exif.Width - bare.Width) > Tolerance)
                throw new InvalidOperationException($"{scope} 横屏无衬底 EXIF 列宽 {slots.Exif.Width:0.#} ≠ {bare.Width:0.#}（52）");
            if (!block.Background.Hidden || (double)block.Layer.CornerRadius > Tolerance)
                throw new InvalidOperationException($"{scope} 横屏 EXIF 列仍带衬底或圆角");
            double pitch = (bare.Height + ExifBlockGeometry.ItemSpacing) / Math.Max(1, enabledExif.Count);
            double expectedHeight = slots.ExifItems * pitch - ExifBlockGeometry.ItemSpacing;
            if (Math.Abs(slots.Exif.Height - expectedHeight) > Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 横屏 EXIF 列高 {slots.Exif.Height:0.#} ≠ {slots.ExifItems} 项整项高 {expectedHeight:0.#}");
            if (slots.ExifItems < enabledExif.Count && Has(slots.Rating)
                && slots.Rating.Y - slots.Exif.MaxY > pitch + Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 横屏 EXIF 裁掉末尾项后仍有空隙 {slots.Rating.Y - slots.Exif.MaxY:0.#} 点可再放一项（整项 {pitch:0.#}）");
        }
    }

    /// <summary>
    /// 圆角与刘海安全区：按每个可见手机控件的舞台矩形分别探测水平、垂直两种安全区布局区域——
    /// 探测到的相对内边距就是该带必须保持的窗口缘距离，核对控件自身到对应舞台边的距离不小于它。
    /// 例外只落在设计明确要求的贴边侧：横屏刘海侧按钮列（合体或上下耳区单按钮、上下张）允许刘海侧贴近，
    /// 下巴侧控制栏（缩放增强、星级、EXIF）允许下巴侧贴近；竖屏无 EXIF 时筛选与更多伸进刘海/灵动岛两旁的
    /// 显示区（上缘贴安全区、两侧贴耳区），只有下缘仍按该带安全区核对。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyCornerRegion(iOSNativeRootViewController root, string scope)
    {
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        var stage = root.Stage.Bounds;
        bool group = !portrait && Has(root.PhoneSlots.FilterMoreGroup);
        var targets = new List<(string Name, UIView View, string Allowed)>();
        if (portrait)
        {
            // 无 EXIF 时顶行上移进刘海两旁的显示区：上、左、右三边由耳区规则接管，只核对下缘。
            string ears = PhoneScreenLayout.TopRowY(root.PhoneSlotsInput) < root.PhoneSlotsInput.Safe.Top - Tolerance
                ? "Top,Left,Right"
                : string.Empty;
            targets.Add(("Filter", root.PhoneFilterButton, ears));
            targets.Add(("More", root.PhoneMoreButton, ears));
            targets.Add(("Navigation", root.PhoneNavigationGroup, string.Empty));
            targets.Add(("FitEnhance", root.PhoneFitEnhanceGroup, string.Empty));
            targets.Add(("Rating", root.PhoneRatingControl, string.Empty));
            targets.Add(("Exif", root.PhoneExifBlock, string.Empty));
        }
        else
        {
            string notchSide = root.NotchOnLeft ? "Left" : "Right";
            string chinSide = root.NotchOnLeft ? "Right" : "Left";
            if (group) targets.Add(("FilterMore", root.PhoneFilterMoreGroup, notchSide));
            else
            {
                targets.Add(("Filter", root.PhoneFilterButton, notchSide + ",Top,Bottom"));
                targets.Add(("More", root.PhoneMoreButton, notchSide + ",Top,Bottom"));
            }
            targets.Add(("Navigation", root.PhoneNavigationGroup, notchSide));
            targets.Add(("FitEnhance", root.PhoneFitEnhanceGroup, chinSide + ",Top,Bottom"));
            targets.Add(("Rating", root.PhoneRatingControl, chinSide));
            targets.Add(("Exif", root.PhoneExifBlock, chinSide));
        }

        void Verify(string name, CGRect frame, string allowed, string edge, double required, double distance)
        {
            if (required <= Tolerance) return;
            if (distance >= required - Tolerance) return;
            if (allowed.Split(',').Contains(edge, StringComparer.Ordinal)) return;
            throw new InvalidOperationException(
                $"{scope} 控件 {name} {CGRect(frame)} 距{edge}边 {distance:0.##} 小于该带安全区 {required:0.##}" +
                $"（本形态只允许 {(allowed.Length == 0 ? "无" : allowed)} 侧贴近）");
        }

        foreach (var (name, view, allowed) in targets)
        {
            if (!IsVisible(root, view)) continue;
            var frame = FrameInStage(root, view);
            var horizontal = root.ProbeInsets(frame, UIViewLayoutRegionAdaptivityAxis.Horizontal);
            var vertical = root.ProbeInsets(frame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            Verify(name, frame, allowed, "Left", (double)horizontal.Left, (double)frame.X);
            Verify(name, frame, allowed, "Right", (double)horizontal.Right, (double)stage.Width - (double)frame.GetMaxX());
            Verify(name, frame, allowed, "Top", (double)vertical.Top, (double)frame.Y);
            Verify(name, frame, allowed, "Bottom", (double)vertical.Bottom, (double)stage.Height - (double)frame.GetMaxY());
        }
    }

    /// <summary>
    /// 胶片条两端：主轴贯穿整个舞台、卡片停在栏位内缘的停靠线上；横屏把顶端筛选/更多与底端上下张登记为滚动边缘元素，
    /// 竖屏不挂接。检查过程会滚到两端再还原原偏移。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyFilmstripEdges(iOSNativeRootViewController root, string scope)
    {
        var film = root.Filmstrip;
        var collection = film.Collection;
        var stage = root.Stage.Bounds;
        bool portrait = root.Form == ScreenForm.PhonePortrait;

        var top = root.FilmstripTopEdgeInteraction;
        var bottom = root.FilmstripBottomEdgeInteraction;
        bool topAttached = root.PhoneFilterMoreGroup.Interactions.Contains(top);
        bool bottomAttached = root.PhoneNavigationGroup.Interactions.Contains(bottom);
        if (portrait)
        {
            if (topAttached || bottomAttached)
                throw new InvalidOperationException($"{scope} 竖屏仍挂接胶片条滚动边缘效果");
        }
        else
        {
            if (!topAttached || !bottomAttached)
                throw new InvalidOperationException(
                    $"{scope} 横屏未挂接胶片条滚动边缘效果（顶部={topAttached}，底部={bottomAttached}）");
            if (!ReferenceEquals(top.ScrollView, collection) || !ReferenceEquals(bottom.ScrollView, collection))
                throw new InvalidOperationException($"{scope} 滚动边缘效果没有指向胶片条集合视图");
        }

        if (!IsVisible(root, film) || film.DisplayCount == 0) return;
        // 手机控件必须压在胶片条之上：横屏胶片条从筛选/更多与上下张两组控件下方滚过。
        // 手机玻璃控件同在一个玻璃容器内，按容器在舞台中的层级比较。
        int filmIndex = StageIndex(root, film);
        int topIndex = StageIndex(root, root.PhoneFilterMoreGroup);
        int bottomIndex = StageIndex(root, root.PhoneNavigationGroup);
        if (filmIndex < 0 || topIndex < filmIndex || bottomIndex < filmIndex)
            throw new InvalidOperationException(
                $"{scope} 手机控件未压在胶片条之上（胶片 {filmIndex}，筛选更多 {topIndex}，上下张 {bottomIndex}）");
        var filmFrame = FrameInStage(root, film);
        if (portrait)
        {
            if (Math.Abs((double)filmFrame.X) > Tolerance || Math.Abs((double)filmFrame.GetMaxX() - (double)stage.Width) > Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 竖屏胶片条未横贯舞台：{CGRect(filmFrame)}，舞台宽 {stage.Width:0.#}");
        }
        else if (Math.Abs((double)filmFrame.Y) > Tolerance || Math.Abs((double)filmFrame.GetMaxY() - (double)stage.Height) > Tolerance)
        {
            throw new InvalidOperationException(
                $"{scope} 横屏胶片条未纵贯舞台：{CGRect(filmFrame)}，舞台高 {stage.Height:0.#}");
        }

        var padding = root.PhoneSlots.FilmstripPadding;
        double lead = portrait ? padding.Left : padding.Top;
        double trail = portrait ? padding.Right : padding.Bottom;
        var document = film.DocumentSize;
        var viewport = collection.Bounds.Size;
        double mainDocument = portrait ? (double)document.Width : (double)document.Height;
        double mainViewport = portrait ? (double)viewport.Width : (double)viewport.Height;
        double limit = portrait ? (double)stage.Width : (double)stage.Height;
        var saved = collection.ContentOffset;
        try
        {
            SetMainOffset(collection, portrait, 0);
            var first = CardStageFrame(root, 0)
                ?? throw new InvalidOperationException($"{scope} 胶片条滚动到起点后没有首张卡片几何");
            double start = portrait ? (double)first.X : (double)first.Y;
            if (start < lead - 1)
                throw new InvalidOperationException(
                    $"{scope} 首张卡片起点 {start:0.#} 越过前导停靠线 {lead:0.#}（即将滚入栏位下方）");

            SetMainOffset(collection, portrait, Math.Max(0, mainDocument - mainViewport));
            var last = CardStageFrame(root, film.DisplayCount - 1)
                ?? throw new InvalidOperationException($"{scope} 胶片条滚动到末端后没有末张卡片几何");
            double end = portrait ? (double)last.GetMaxX() : (double)last.GetMaxY();
            if (end > limit - trail + 1)
                throw new InvalidOperationException(
                    $"{scope} 末张卡片末端 {end:0.#} 越过末端停靠线 {limit - trail:0.#}（{(portrait ? "右侧" : "下方")}留白 {trail:0.#}）");
        }
        finally
        {
            collection.SetContentOffset(saved, false);
            collection.LayoutIfNeeded();
        }
    }

    /// <summary>把胶片条主轴滚到指定偏移（交叉轴保持不变）。</summary>
    /// <param name="collection">胶片条集合视图。</param>
    /// <param name="portrait">是否竖屏（横向胶片条）。</param>
    /// <param name="offset">主轴偏移。</param>
    private static void SetMainOffset(UICollectionView collection, bool portrait, double offset)
    {
        var current = collection.ContentOffset;
        var target = portrait ? new CGPoint(offset, current.Y) : new CGPoint(current.X, offset);
        collection.SetContentOffset(target, false);
        collection.LayoutIfNeeded();
    }

    /// <summary>卡片在舞台坐标中的矩形：胶片条 frame 原点 + 布局给出的文档坐标 − 当前内容偏移。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="index">显示快照索引。</param>
    /// <returns>舞台坐标矩形；索引越界返回 null。</returns>
    private static CGRect? CardStageFrame(iOSNativeRootViewController root, int index)
    {
        var film = root.Filmstrip;
        if (film.FrameAt(index) is not { } card) return null;
        var frame = FrameInStage(root, film);
        var offset = film.ContentOffset;
        return new CGRect(
            (double)frame.X + (double)card.X - (double)offset.X,
            (double)frame.Y + (double)card.Y - (double)offset.Y,
            (double)card.Width, (double)card.Height);
    }

    /// <summary>宽形态：主图满舞台、分区避让作为适应区内边距。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyWideGeometry(iOSNativeRootViewController root, string scope)
    {
        if (root.Photo.Frame != root.Stage.Bounds)
            throw new InvalidOperationException($"{scope} 主图视图未铺满舞台：{CGRect(root.Photo.Frame)} vs {CGRect(root.Stage.Bounds)}");
        var panes = root.Panes;
        var bounds = root.Stage.Bounds;
        var expected = new UIEdgeInsets(
            (nfloat)Math.Max(0, (double)panes.Photo.Y),
            (nfloat)Math.Max(0, (double)panes.Photo.X),
            (nfloat)Math.Max(0, (double)bounds.Height - panes.Photo.MaxY),
            (nfloat)Math.Max(0, (double)bounds.Width - panes.Photo.MaxX));
        var actual = root.Photo.FitInsets;
        if (Math.Abs((double)actual.Top - (double)expected.Top) > Tolerance
            || Math.Abs((double)actual.Left - (double)expected.Left) > Tolerance
            || Math.Abs((double)actual.Bottom - (double)expected.Bottom) > Tolerance
            || Math.Abs((double)actual.Right - (double)expected.Right) > Tolerance)
            throw new InvalidOperationException($"{scope} 宽形态适应区内边距 {Insets(actual)} ≠ 分区避让 {Insets(expected)}");
    }

    /// <summary>
    /// 适应区与其他元素：可见控件/占位/胶片条/分析栏/栏背景都不与适应区相交；
    /// 不在栏背景内的裸控件按行内间距（透明栏规则）外扩后也不相交；适应态照片落在适应区内并至少一轴贴边。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyFitAvoidance(iOSNativeRootViewController root, string scope)
    {
        var fit = FitArea(root);
        if ((double)fit.Width <= 0 || (double)fit.Height <= 0)
            throw new InvalidOperationException($"{scope} 适应区为空：{CGRect(fit)}");
        bool phone = root.Form != ScreenForm.Wide;
        var bars = new List<CGRect>();
        if (phone)
        {
            var views = root.PhoneBarBackgrounds;
            foreach (var bar in new UIView[] { views.Top, views.Bottom, views.Leading, views.Trailing })
                if (IsVisible(root, bar)) bars.Add(FrameInStage(root, bar));
            if (IsVisible(root, root.AnalysisBarBackground)) bars.Add(FrameInStage(root, root.AnalysisBarBackground));
        }
        else
        {
            foreach (var bar in new UIView[] { root.FileBarView, root.ControlBackgroundView, root.AnalysisBarBackground })
                if (IsVisible(root, bar)) bars.Add(FrameInStage(root, bar));
        }
        var elements = new List<(string Name, CGRect Frame)>();
        void Add(string name, UIView view)
        {
            if (IsVisible(root, view)) elements.Add((name, FrameInStage(root, view)));
        }

        if (phone)
        {
            var portrait = root.Form == ScreenForm.PhonePortrait;
            bool group = !portrait && Has(root.PhoneSlots.FilterMoreGroup);
            Add("Filter", group ? root.PhoneFilterSegment : root.PhoneFilterButton);
            Add("More", group ? root.PhoneMoreSegment : root.PhoneMoreButton);
            Add("Exif", root.PhoneExifBlock);
            Add("Navigation", root.PhoneNavigationGroup);
            Add("Rating", root.PhoneRatingControl);
            Add("FitEnhance", root.PhoneFitEnhanceGroup);
            Add("Filmstrip", root.Filmstrip);
            Add("Analysis", root.AnalysisPane);
        }
        else
        {
            Add("FilterBar", root.FilterBar);
            Add("Filmstrip", root.Filmstrip);
            Add("ControlScroll", root.ControlScroll);
            Add("Analysis", root.AnalysisPane);
        }
        foreach (var (name, frame) in elements)
        {
            if (Intersects(fit, frame))
                throw new InvalidOperationException($"{scope} {name} {CGRect(frame)} 与适应区 {CGRect(fit)} 相交");
            if (bars.Any(bar => Contains(bar, frame))) continue;
            double bare = phone ? root.PhoneSlotsInput.RowInset : 0;
            if (Intersects(fit, Expand(frame, bare)))
                throw new InvalidOperationException(
                    $"{scope} 裸控件 {name} {CGRect(frame)} 按行内间距 {bare:0.#} 外扩后与适应区 {CGRect(fit)} 相交");
        }
        foreach (var bar in bars)
        {
            if (Intersects(fit, bar))
                throw new InvalidOperationException($"{scope} 栏背景 {CGRect(bar)} 与适应区 {CGRect(fit)} 相交");
        }
        var photo = PhotoBounds(root.Photo);
        if (photo.Left < (double)fit.X - Tolerance || photo.Top < (double)fit.Y - Tolerance
            || photo.Right > (double)fit.GetMaxX() + Tolerance || photo.Bottom > (double)fit.GetMaxY() + Tolerance)
            throw new InvalidOperationException($"{scope} 适应态照片 {Format(photo)} 超出适应区 {CGRect(fit)}");
        bool touches = Math.Abs(photo.Left - (double)fit.X) <= Tolerance || Math.Abs(photo.Right - (double)fit.GetMaxX()) <= Tolerance
            || Math.Abs(photo.Top - (double)fit.Y) <= Tolerance || Math.Abs(photo.Bottom - (double)fit.GetMaxY()) <= Tolerance;
        if (!touches)
            throw new InvalidOperationException($"{scope} 适应态照片 {Format(photo)} 没有贴住适应区 {CGRect(fit)} 的任何一边");
    }

    /// <summary>主图内边距与手机形态避让一致（宽形态已由 <see cref="VerifyWideGeometry"/> 核对）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyFitPlacement(iOSNativeRootViewController root, string scope)
    {
        if (root.Form == ScreenForm.Wide) return;
        var insets = root.Photo.FitInsets;
        var avoid = root.PhoneSlots.PhotoAvoidance;
        if (Math.Abs((double)insets.Left - avoid.Left) > Tolerance || Math.Abs((double)insets.Top - avoid.Top) > Tolerance
            || Math.Abs((double)insets.Right - avoid.Right) > Tolerance || Math.Abs((double)insets.Bottom - avoid.Bottom) > Tolerance)
            throw new InvalidOperationException($"{scope} 主图内边距 {Insets(insets)} ≠ 手机避让 {avoid}");
    }

    /// <summary>手机形态按钮命中区不小于 44 点（含合体按钮每一段）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyHitTargets(iOSNativeRootViewController root, string scope)
    {
        var buttons = new List<(string Name, UIButton Button)>
        {
            ("Filter", root.PhoneFilterButton),
            ("More", root.PhoneMoreButton),
        };
        var groups = new (string Name, iOSGlassButtonGroup Group)[]
        {
            ("FilterMore", root.PhoneFilterMoreGroup),
            ("Navigation", root.PhoneNavigationGroup),
            ("FitEnhance", root.PhoneFitEnhanceGroup),
        };
        foreach (var (name, group) in groups)
        {
            for (int index = 0; index < group.Buttons.Count; index++) buttons.Add(($"{name}[{index}]", group.Buttons[index]));
        }
        foreach (var (name, button) in buttons)
        {
            if (!IsVisible(root, button)) continue;
            var frame = FrameInStage(root, button);
            if ((double)frame.Width < HitSize - Tolerance || (double)frame.Height < HitSize - Tolerance)
                throw new InvalidOperationException($"{scope} {name} 命中区 {CGRect(frame)} 小于 {HitSize:0.#}×{HitSize:0.#}");
        }
    }

    /// <summary>
    /// 筛选与更多在手机形态常驻：竖屏顶行（带 EXIF 时三者同行贴安全区，不带 EXIF 时上移进刘海两旁的耳区）；
    /// 横屏有胶片栏时是刘海侧列顶端的合体按钮，胶片栏关闭后拆成刘海上下两旁的独立圆按钮（同一耳区口径）。
    /// 两种位置都核对与共享 <see cref="PhoneScreenLayout.TopRowY"/> 的对应关系与菜单入口可用。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyFilters(iOSNativeRootViewController root, string scope)
    {
        if (root.Form == ScreenForm.Wide) return;
        var slots = root.PhoneSlots;
        var input = root.PhoneSlotsInput;
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        bool group = !portrait && Has(slots.FilterMoreGroup);
        if (!Has(slots.Filter) || !Has(slots.More))
            throw new InvalidOperationException(
                $"{scope} 筛选/更多未常驻：Filter={Rect(slots.Filter)}，More={Rect(slots.More)}");
        var filterOwner = group ? root.PhoneFilterSegment : root.PhoneFilterButton;
        var moreOwner = group ? root.PhoneMoreSegment : root.PhoneMoreButton;
        if (filterOwner.Menu == null) throw new InvalidOperationException($"{scope} 筛选入口没有菜单");
        if (moreOwner.Menu == null) throw new InvalidOperationException($"{scope} 更多入口没有菜单");
        if (!portrait)
        {
            string notchSide = root.NotchOnLeft ? "Left" : "Right";
            double notchGap = group
                ? (root.NotchOnLeft ? slots.FilterMoreGroup.X : root.Stage.Bounds.Width - slots.FilterMoreGroup.MaxX)
                : (root.NotchOnLeft ? slots.Filter.X : root.Stage.Bounds.Width - slots.More.MaxX);
            double leadTop = Math.Max(input.Safe.Top, input.LeadBand.Top) + input.RowInset;
            double leadBottom = input.Height
                - Math.Max(Math.Max(input.Safe.Bottom, input.LeadBand.Bottom), input.RowInset);
            if (notchGap > (root.NotchOnLeft ? input.Safe.Left : input.Safe.Right) + Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 筛选/更多未贴{notchSide}侧安全区：距窗口缘 {notchGap:0.#}（安全区 " +
                    $"{(root.NotchOnLeft ? input.Safe.Left : input.Safe.Right):0.#}）");
            if (notchGap < Math.Min(root.NotchOnLeft ? input.Safe.Left : input.Safe.Right, input.EdgeMargin) - Tolerance)
                throw new InvalidOperationException($"{scope} 筛选/更多过于贴近{notchSide}窗口缘：{notchGap:0.#}");
            if (group)
            {
                if (Math.Abs(slots.FilterMoreGroup.Y - leadTop) > Tolerance)
                    throw new InvalidOperationException(
                        $"{scope} 合体按钮未在{notchSide}侧列顶端：Y {slots.FilterMoreGroup.Y:0.#} ≠ {leadTop:0.#}");
                if (!Contains(ToCGRect(slots.FilterMoreGroup), ToCGRect(slots.Filter))
                    || !Contains(ToCGRect(slots.FilterMoreGroup), ToCGRect(slots.More)))
                    throw new InvalidOperationException(
                        $"{scope} 筛选/更多槽位不在合体按钮内：组 {Rect(slots.FilterMoreGroup)}，筛选 {Rect(slots.Filter)}，更多 {Rect(slots.More)}");
            }
            else
            {
                if (Math.Abs(slots.Filter.Y - leadTop) > Tolerance)
                    throw new InvalidOperationException(
                        $"{scope} 胶片栏关闭后筛选未移到{notchSide}上耳区：Y {slots.Filter.Y:0.#} ≠ {leadTop:0.#}");
                if (Math.Abs(slots.More.MaxY - leadBottom) > Tolerance)
                    throw new InvalidOperationException(
                        $"{scope} 胶片栏关闭后更多未落在{notchSide}下耳区：末端 {slots.More.MaxY:0.#} ≠ {leadBottom:0.#}");
                if (Math.Abs(slots.Filter.X - slots.More.X) > Tolerance)
                    throw new InvalidOperationException(
                        $"{scope} 上下耳区按钮未对齐同一列：筛选 X {slots.Filter.X:0.#}，更多 X {slots.More.X:0.#}");
                if (slots.More.Y <= slots.Filter.Y + Tolerance)
                    throw new InvalidOperationException(
                        $"{scope} 上下耳区次序不符：筛选 Y {slots.Filter.Y:0.#}，更多 Y {slots.More.Y:0.#}");
            }
            return;
        }
        // 竖屏：两条顶行信息都与共享行位置一致。
        double rowTop = PhoneScreenLayout.TopRowY(in input);
        double exifRow = Math.Max(input.Safe.Top, input.RowInset);
        if (Math.Abs(slots.Filter.Y - rowTop) > Tolerance || Math.Abs(slots.More.Y - rowTop) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 竖屏顶行 Y {slots.Filter.Y:0.#}/{slots.More.Y:0.#} ≠ 共享行位置 {rowTop:0.#}");
        bool withExif = PhoneScreenLayout.PortraitExifShown(in input);
        if (withExif)
        {
            if (!Has(slots.Exif))
                throw new InvalidOperationException($"{scope} 带 EXIF 的顶行缺少 EXIF 块");
            if (rowTop < exifRow - Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 带 EXIF 的顶行 {rowTop:0.#} 高于安全区行 {exifRow:0.#}");
            double rowCenter = rowTop + input.ButtonSize / 2;
            if (Math.Abs(slots.Exif.Y + slots.Exif.Height / 2 - rowCenter) > Tolerance)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块未与顶行按钮同排居中：块中心 {slots.Exif.Y + slots.Exif.Height / 2:0.#} ≠ 行中心 {rowCenter:0.#}");
        }
        else
        {
            if (rowTop > exifRow + Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 无 EXIF 时顶行 {rowTop:0.#} 未上移进耳区（带 EXIF 行 {exifRow:0.#}）");
            if (input.Safe.Top > input.EdgeMargin && slots.Filter.Y < input.EdgeMargin - Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 耳区按钮上缘距窗口缘 {slots.Filter.Y:0.#} 小于 edgeMargin {input.EdgeMargin:0.#}");
        }
    }

    /// <summary>
    /// 横屏下巴侧控制栏（桌面式）：按钮外缘距下巴窗口缘 <c>EdgeMargin</c>（不避让系统对称安全内缩），
    /// 自上而下为 EXIF 列 → 星级控件 → 缩放适应/雾天增强竖排合体，EXIF 与按钮同一中轴；上下张不在控制栏内。
    /// 几何与胶片栏、上下张开关无关（作废"旋转保持一致"），只有 EXIF 开关改变星级起点。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="combo">当前组合。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyControlBar(iOSNativeRootViewController root, Combo combo, string scope)
    {
        var slots = root.PhoneSlots;
        var input = root.PhoneSlotsInput;
        if (!Has(slots.FitEnhance) || !Has(slots.Rating))
            throw new InvalidOperationException($"{scope} 控制栏可见时缺少缩放增强或星级槽位");
        double chinEdge = root.NotchOnLeft ? root.Stage.Bounds.Width - slots.FitEnhance.MaxX : slots.FitEnhance.X;
        if (Math.Abs(chinEdge - input.EdgeMargin) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 控制栏外缘距下巴窗口缘 {chinEdge:0.#} ≠ EdgeMargin {input.EdgeMargin:0.#}");
        double chinSafe = root.NotchOnLeft ? input.Safe.Right : input.Safe.Left;
        if (chinSafe > input.EdgeMargin + Tolerance && chinEdge >= chinSafe - Tolerance)
            throw new InvalidOperationException(
                $"{scope} 控制栏仍避让对称安全内缩：外缘 {chinEdge:0.#}，安全区 {chinSafe:0.#}");
        if (Math.Abs(slots.Rating.X - slots.FitEnhance.X) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 星级与缩放增强不在同一列：{slots.Rating.X:0.#} vs {slots.FitEnhance.X:0.#}");
        double axis = slots.FitEnhance.X + input.ButtonSize / 2;
        if (Has(slots.Exif) && Math.Abs(slots.Exif.X + slots.Exif.Width / 2 - axis) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} EXIF 列中轴 {slots.Exif.X + slots.Exif.Width / 2:0.#} ≠ 按钮中轴 {axis:0.#}");
        // 次序：EXIF（放得下时）→ 星级 → 缩放增强，项间为组间距。
        double ratingTop = Has(slots.Exif) ? slots.Exif.MaxY + input.GroupGap : double.NaN;
        if (Has(slots.Exif) && Math.Abs(slots.Rating.Y - ratingTop) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 星级未紧接 EXIF 列：星级 Y {slots.Rating.Y:0.#} ≠ {ratingTop:0.#}");
        if (Math.Abs(slots.FitEnhance.Y - (slots.Rating.MaxY + input.GroupGap)) > Tolerance)
            throw new InvalidOperationException(
                $"{scope} 缩放增强未紧接星级：Y {slots.FitEnhance.Y:0.#} ≠ 星级末端 {slots.Rating.MaxY:0.#} + {input.GroupGap:0.#}");
        if (Has(slots.Navigation))
        {
            var navigation = ToCGRect(slots.Navigation);
            foreach (var (name, target) in new (string, LayoutRect)[]
            {
                ("缩放增强", slots.FitEnhance), ("星级", slots.Rating), ("EXIF", slots.Exif),
            })
            {
                if (Has(target) && Intersects(navigation, ToCGRect(target)))
                    throw new InvalidOperationException($"{scope} 上下张 {Rect(slots.Navigation)} 落在控制栏{name} {Rect(target)} 内");
            }
        }
        if (!Has(slots.Exif))
        {
            // 没有 EXIF（开关关或显示项为空）：控制栏背景收起，星级从列顶端起、取全部剩余长度。
            if (Has(slots.TrailingBar))
                throw new InvalidOperationException($"{scope} 无 EXIF 时仍绘制控制栏背景 {Rect(slots.TrailingBar)}");
            double columnTop = Math.Max(input.Safe.Top, input.ControlBand.Top) + input.RowInset;
            if (Math.Abs(slots.Rating.Y - columnTop) > Tolerance)
                throw new InvalidOperationException(
                    $"{scope} 无 EXIF 时星级未从列顶端起：{slots.Rating.Y:0.#} ≠ {columnTop:0.#}");
        }
        if (combo.Name == "all")
        {
            controlColumnSlots = slots;
            return;
        }
        if (controlColumnSlots is not { } reference)
            throw new InvalidOperationException($"{scope} 缺少 EXIF 全开时的控制栏基准");
        // 缩放增强与所有开关无关；星级与 EXIF 只在 EXIF 仍显示时与基准一致（EXIF 关掉本就会变长）。
        if (!SameRect(reference.FitEnhance, slots.FitEnhance))
            throw new InvalidOperationException(
                $"{scope} 缩放增强几何随开关变化：{Rect(reference.FitEnhance)} → {Rect(slots.FitEnhance)}");
        if (Has(slots.Exif)
            && (!SameRect(reference.Rating, slots.Rating) || !SameRect(reference.Exif, slots.Exif)))
            throw new InvalidOperationException(
                $"{scope} 控制栏几何随胶片栏/上下张开关变化：Rating {Rect(reference.Rating)}→{Rect(slots.Rating)}，" +
                $"Exif {Rect(reference.Exif)}→{Rect(slots.Exif)}");
    }

    /// <summary>核对分屏窗口的舞台宽度、右安全区扣除与主图宽度。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="config">配置。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifySplitStage(iOSNativeRootViewController root, Config config, string scope)
    {
        double expected = (double)root.View!.Bounds.Width * config.StageFraction;
        if (Math.Abs((double)root.Stage.Bounds.Width - expected) > 1)
            throw new InvalidOperationException($"{scope} 舞台宽 {(double)root.Stage.Bounds.Width:0.#} ≠ 期望 {expected:0.#}");
        var input = root.PhoneSlotsInput;
        var rootSafe = root.View!.SafeAreaInsets;
        double expectedRight = Math.Max(0, (double)rootSafe.Right - ((double)root.View!.Bounds.Width - (double)root.Stage.Bounds.Width));
        if (Math.Abs(input.Safe.Right - expectedRight) > 1)
            throw new InvalidOperationException($"{scope} 分屏右安全区 {input.Safe.Right:0.#} 未按舞台覆盖区域扣除（期望 {expectedRight:0.#}）");
        if (Math.Abs((double)root.Photo.Bounds.Width - (double)root.Stage.Bounds.Width) > Tolerance)
            throw new InvalidOperationException($"{scope} 主图宽度 {(double)root.Photo.Bounds.Width:0.#} ≠ 舞台宽 {(double)root.Stage.Bounds.Width:0.#}");
    }

    /// <summary>
    /// 核对更多菜单与筛选菜单的结构，以及主图菜单的勾选态：手机形态的栏位组末尾多一项 EXIF（与长按菜单同序同源），
    /// 宽形态不含该项；筛选与更多两个入口在合体与单按钮两种形态下都必须带菜单。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="scope">报告范围名。</param>
    private static void VerifyMenus(iOSNativeRootViewController root, MainViewModel main, string scope)
    {
        bool phoneForm = root.Form != ScreenForm.Wide;
        var moreOwner = !phoneForm || Has(root.PhoneSlots.FilterMoreGroup)
            ? root.PhoneMoreSegment
            : root.PhoneMoreButton;
        var moreMenu = moreOwner.Menu
            ?? throw new InvalidOperationException($"{scope} 更多入口没有菜单");
        if (moreMenu.Children.Length != 1 || moreMenu.Children[0] is not UIDeferredMenuElement)
            throw new InvalidOperationException($"{scope} 更多菜单应为单个延迟元素，实际 {moreMenu.Children.Length} 项");
        var fromSource = Flatten(root.PhotoMenuItems());
        var fromLongPress = Flatten(root.BuildPhotoMenu().Menu.Children);
        if (!fromSource.SequenceEqual(fromLongPress, StringComparer.Ordinal))
            throw new InvalidOperationException($"{scope} 更多菜单内容与主图长按菜单不一致");
        var states = PhotoMenuStates(root.PhotoMenuItems());
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["photo.film"] = main.IsThumbnailViewVisible,
            ["photo.control"] = main.IsControlViewVisible,
            ["photo.analysis"] = main.IsAnalysisViewVisible,
        };
        if (phoneForm) expected["photo.exif"] = main.IsExifViewVisible;
        else if (states.ContainsKey("photo.exif"))
            throw new InvalidOperationException($"{scope} 宽形态主图菜单仍含 EXIF 开关");
        foreach (var (identifier, value) in expected)
        {
            if (states.TryGetValue(identifier, out bool actual) && actual != value)
                throw new InvalidOperationException($"{scope} 主图菜单 {identifier} 勾选 {actual} ≠ 状态 {value}");
        }
        // 手机形态：栏位组 = 胶片栏 / 分析栏 / 控制栏 / EXIF 四项，EXIF 在控制栏之后。
        var titles = Flatten(root.PhotoMenuItems());
        int filmIndex = titles.IndexOf("胶片栏"), controlIndex = titles.IndexOf("控制栏");
        int exifIndex = titles.IndexOf("EXIF");
        if (filmIndex < 0 || controlIndex < 0)
            throw new InvalidOperationException($"{scope} 主图菜单缺少栏位组：{string.Join(" / ", titles)}");
        if (phoneForm)
        {
            if (exifIndex < 0)
                throw new InvalidOperationException($"{scope} 手机形态主图菜单缺少 EXIF 开关：{string.Join(" / ", titles)}");
            if (exifIndex != controlIndex + 1)
                throw new InvalidOperationException(
                    $"{scope} EXIF 开关未紧随控制栏：{string.Join(" / ", titles)}");
        }
        else if (exifIndex >= 0)
        {
            throw new InvalidOperationException($"{scope} 宽形态主图菜单含 EXIF 开关");
        }
        foreach (var button in new[] { root.PhoneFilterButton, root.PhoneFilterSegment })
        {
            var menu = button.Menu ?? throw new InvalidOperationException($"{scope} 筛选按钮没有菜单");
            if (menu.Children.Length != 3)
                throw new InvalidOperationException($"{scope} 筛选菜单应为三个内联分组，实际 {menu.Children.Length}");
            foreach (var child in menu.Children)
            {
                if (child is not UIMenu sub)
                    throw new InvalidOperationException($"{scope} 筛选分组不是 UIMenu");
                if ((sub.Options & (UIMenuOptions.DisplayInline | UIMenuOptions.SingleSelection))
                    != (UIMenuOptions.DisplayInline | UIMenuOptions.SingleSelection))
                    throw new InvalidOperationException($"{scope} 筛选分组缺少 DisplayInline/SingleSelection：{sub.Options}");
                if (sub.Children.Length == 0)
                    throw new InvalidOperationException($"{scope} 筛选分组为空");
                foreach (var element in sub.Children)
                {
                    if (element is not UIAction action)
                        throw new InvalidOperationException($"{scope} 筛选项不是 UIAction");
                    if ((action.Attributes & UIMenuElementAttributes.KeepsMenuPresented) == 0)
                        throw new InvalidOperationException($"{scope} 筛选项「{action.Title}」未保持菜单展开");
                }
            }
        }
    }

    /// <summary>筛选菜单写回：直接调用排序动作处理器，共享筛选状态改变后按钮菜单的新选项转为勾选。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyFilterMutationAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var filter = main.FileVM.FilterBar;
        var original = filter.SortMode;
        var target = filter.SortModes.Select(option => (SortMode)option.Value).FirstOrDefault(value => value != original);
        if (target == original) throw new InvalidOperationException("筛选菜单只有一个排序选项，无法核对写回");
        var result = iOSFilterMenu.Build(filter, () => { });
        if (!result.Handlers.TryGetValue($"sort:{target}", out var handler))
            throw new InvalidOperationException($"筛选菜单缺少 sort:{target} 的处理器");
        await UiDispatcher.InvokeAsync(handler);
        await WaitAsync(() => filter.SortMode == target, "排序写回共享筛选状态");
        string title = filter.SortModes.First(option => (SortMode)option.Value == target).DisplayName;
        await WaitAsync(() =>
        {
            var menu = root.PhoneFilterSegment.Menu;
            return menu != null && SortAction(menu, title) is { State: UIMenuElementState.On };
        }, $"按钮菜单「{title}」转为勾选");
        lines.Add($"PASS 筛选菜单动作：sort:{target} 写回共享筛选状态，按钮菜单新选项「{title}」为勾选");
        await UiDispatcher.InvokeAsync(() => filter.SortMode = original);
        await WaitAsync(() => filter.SortMode == original, "排序还原");
        filterMutationChecked = true;
    }

    /// <summary>在筛选菜单的排序分组里按标题查找 action。</summary>
    /// <param name="menu">筛选菜单。</param>
    /// <param name="title">选项标题。</param>
    /// <returns>命中的 action；没有返回 null。</returns>
    private static UIAction? SortAction(UIMenu menu, string title)
        => menu.Children.OfType<UIMenu>().SelectMany(group => group.Children.OfType<UIAction>())
            .FirstOrDefault(action => string.Equals(action.Title, title, StringComparison.Ordinal));

    /// <summary>切换配置后主图保持当前照片与归一化中心；适应区不变时物理倍率也不变。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="configs">配置清单。</param>
    private static async Task VerifySwitchKeepsPhotoAsync(iOSNativeRootViewController root, MainViewModel main, Config[] configs)
    {
        await ApplyConfigAsync(root, main, configs[0]);
        await ZoomBeyondFitAsync(root, 3);
        await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(.3, .7));
        await SettleAsync(root);
        var before = await UiDispatcher.InvokeAsync(() => (File: main.CurrentFile, Center: root.Photo.Viewport.CenterUV,
            Physical: root.Photo.PhysicalScale, Zoom: (double)root.Photo.ScrollView.ZoomScale, Fit: root.Photo.FitRect));
        if (Math.Abs(before.Center.U - .3) > .02 || Math.Abs(before.Center.V - .7) > .02)
            throw new InvalidOperationException($"切换前未能把视口中心移到 (.3,.7)：({before.Center.U:0.###},{before.Center.V:0.###})");
        await ApplyConfigAsync(root, main, configs[1]);
        await SettleAsync(root);
        var after = await UiDispatcher.InvokeAsync(() => (File: main.CurrentFile, Center: root.Photo.Viewport.CenterUV,
            Physical: root.Photo.PhysicalScale, Zoom: (double)root.Photo.ScrollView.ZoomScale, Fit: root.Photo.FitRect));
        if (!ReferenceEquals(before.File, after.File))
            throw new InvalidOperationException($"切换配置后当前照片变化：{before.File?.Name} → {after.File?.Name}");
        if (Math.Abs(before.Center.U - after.Center.U) > .02 || Math.Abs(before.Center.V - after.Center.V) > .02)
            throw new InvalidOperationException($"切换配置后视口中心 ({before.Center.U:0.###},{before.Center.V:0.###}) → " +
                $"({after.Center.U:0.###},{after.Center.V:0.###}) 偏差超过 0.02");
        bool sameFit = Math.Abs((double)before.Fit.Width - (double)after.Fit.Width) <= Tolerance
            && Math.Abs((double)before.Fit.Height - (double)after.Fit.Height) <= Tolerance;
        if (sameFit)
        {
            if (Math.Abs(before.Physical - after.Physical) > Math.Max(.01, before.Physical * .01))
                throw new InvalidOperationException($"切换配置后物理倍率 {before.Physical:0.###} → {after.Physical:0.###}（适应区相同）");
        }
        else if (Math.Abs(before.Zoom - after.Zoom) > Math.Max(1e-3, before.Zoom * .01))
        {
            throw new InvalidOperationException($"切换配置后相对缩放 {before.Zoom:0.###} → {after.Zoom:0.###}（适应区变化）");
        }
        await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
        await SettleAsync(root);
    }

    /// <summary>放大到超过适应区后核对 contentInset 避让、极限偏移贴边并垫到栏位下方。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyZoomedAsync(iOSNativeRootViewController root, List<string> lines)
    {
        await ZoomBeyondFitAsync(root, 2);
        await SettleAsync(root);
        if (root.Photo.IsFit) throw new InvalidOperationException("放大态检查仍处于适应态");
        var insets = root.Photo.FitInsets;
        var contentInset = root.Photo.ScrollView.ContentInset;
        if ((double)contentInset.Left < (double)insets.Left - Tolerance
            || (double)contentInset.Top < (double)insets.Top - Tolerance
            || (double)contentInset.Right < (double)insets.Right - Tolerance
            || (double)contentInset.Bottom < (double)insets.Bottom - Tolerance)
            throw new InvalidOperationException($"放大态 contentInset {Insets(contentInset)} 未包含适应区避让 {Insets(insets)}");
        var fit = FitArea(root);
        var displayed = await UiDispatcher.InvokeAsync(() => (Width: root.Photo.DisplayWidth * root.Photo.PointScale,
            Height: root.Photo.DisplayHeight * root.Photo.PointScale));
        var scroll = root.Photo.ScrollView;
        await UiDispatcher.InvokeAsync(() => scroll.SetContentOffset(new CGPoint(-(double)contentInset.Left, -(double)contentInset.Top), false));
        await SettleAsync(root);
        var corner = PhotoBounds(root.Photo);
        if (displayed.Width > (double)fit.Width + Tolerance && Math.Abs(corner.Left - (double)fit.X) > 1)
            throw new InvalidOperationException($"左极限照片 {Format(corner)} 未贴住适应区 {CGRect(fit)} 左边");
        if (displayed.Height > (double)fit.Height + Tolerance && Math.Abs(corner.Top - (double)fit.Y) > 1)
            throw new InvalidOperationException($"上极限照片 {Format(corner)} 未贴住适应区 {CGRect(fit)} 上边");
        if (corner.Right <= (double)fit.GetMaxX() + Tolerance && corner.Bottom <= (double)fit.GetMaxY() + Tolerance)
            throw new InvalidOperationException($"放大态照片 {Format(corner)} 没有超出适应区（栏位下方应仍有照片）");
        await UiDispatcher.InvokeAsync(() => scroll.SetContentOffset(new CGPoint(
            Math.Max(0, (double)scroll.ContentSize.Width - (double)scroll.Bounds.Width + (double)contentInset.Right),
            Math.Max(0, (double)scroll.ContentSize.Height - (double)scroll.Bounds.Height + (double)contentInset.Bottom)), false));
        await SettleAsync(root);
        var opposite = PhotoBounds(root.Photo);
        if (displayed.Width > (double)fit.Width + Tolerance && Math.Abs(opposite.Right - (double)fit.GetMaxX()) > 1)
            throw new InvalidOperationException($"右极限照片 {Format(opposite)} 未贴住适应区 {CGRect(fit)} 右边");
        if (displayed.Height > (double)fit.Height + Tolerance && Math.Abs(opposite.Bottom - (double)fit.GetMaxY()) > 1)
            throw new InvalidOperationException($"下极限照片 {Format(opposite)} 未贴住适应区 {CGRect(fit)} 下边");
        lines.Add($"PASS 放大态：contentInset {Insets(contentInset)} 含避让 {Insets(insets)}，极限偏移贴适应区且照片垫到栏位下方（{Format(corner)}）");
        await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
        await SettleAsync(root);
    }

    /// <summary>把主图放大到显示尺寸超过适应区（至少一轴），不改动视口中心语义。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="factor">相对适应区的放大倍数。</param>
    private static async Task ZoomBeyondFitAsync(iOSNativeRootViewController root, double factor)
    {
        double target = await UiDispatcher.InvokeAsync(() =>
        {
            root.Photo.FitToView();
            var viewport = root.Photo.Viewport;
            double width = root.Photo.DisplayWidth, height = root.Photo.DisplayHeight;
            double ratio = Math.Max((double)viewport.FitWidth / Math.Max(1, width), (double)viewport.FitHeight / Math.Max(1, height));
            double physical = ratio * (double)viewport.BackingScale * factor;
            root.Photo.ZoomToPhysical(physical);
            return physical;
        });
        await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - target) <= Math.Max(.02, target * .03),
            $"放大到超过适应区（目标物理 {target:0.###}，实际 {root.Photo.PhysicalScale:0.###}）");
        await SettleAsync(root);
    }

    /// <summary>两向横屏槽位水平镜像：x' = W − x − w，避让左右互换。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyMirror(iOSNativeRootViewController root, string scope, List<string> lines)
    {
        if (notchLeftSlots is not { } left)
            throw new InvalidOperationException($"{scope} 缺少刘海在左的槽位记录，无法核对镜像");
        var right = root.PhoneSlots;
        double width = root.Stage.Bounds.Width;
        if (Math.Abs(width - notchLeftWidth) > Tolerance)
            throw new InvalidOperationException($"{scope} 两个横屏配置窗口宽不同：{width:0.#} vs {notchLeftWidth:0.#}");
        var pairs = new (string Name, LayoutRect Left, LayoutRect Right)[]
        {
            ("Filter", left.Filter, right.Filter), ("More", left.More, right.More),
            ("FilterMoreGroup", left.FilterMoreGroup, right.FilterMoreGroup), ("Exif", left.Exif, right.Exif),
            ("Navigation", left.Navigation, right.Navigation), ("Rating", left.Rating, right.Rating),
            ("FitEnhance", left.FitEnhance, right.FitEnhance), ("Filmstrip", left.Filmstrip, right.Filmstrip),
            ("Analysis", left.Analysis, right.Analysis), ("TopBar", left.TopBar, right.TopBar),
            ("BottomBar", left.BottomBar, right.BottomBar), ("LeadingBar", left.LeadingBar, right.LeadingBar),
            ("TrailingBar", left.TrailingBar, right.TrailingBar),
        };
        foreach (var (name, a, b) in pairs)
        {
            if (Has(a) != Has(b))
                throw new InvalidOperationException($"{scope} {name} 镜像后可见性变化：{Rect(a)} vs {Rect(b)}");
            if (!Has(a)) continue;
            double expectedX = width - a.X - a.Width;
            if (Math.Abs(b.X - expectedX) > Tolerance || Math.Abs(b.Y - a.Y) > Tolerance
                || Math.Abs(b.Width - a.Width) > Tolerance || Math.Abs(b.Height - a.Height) > Tolerance)
                throw new InvalidOperationException($"{scope} {name} 未镜像：左向 {Rect(a)}，右向 {Rect(b)}（期望 X={expectedX:0.#}）");
        }
        var la = left.PhotoAvoidance;
        var ra = right.PhotoAvoidance;
        if (Math.Abs(la.Left - ra.Right) > Tolerance || Math.Abs(la.Right - ra.Left) > Tolerance
            || Math.Abs(la.Top - ra.Top) > Tolerance || Math.Abs(la.Bottom - ra.Bottom) > Tolerance)
            throw new InvalidOperationException($"{scope} 主图避让未镜像：{la} vs {ra}");
        lines.Add($"PASS {scope}：刘海左/右两组槽位与主图避让互为水平镜像（宽 {width:0.#}）");
    }

    /// <summary>
    /// 本轮手机布局的定版截图：竖屏带 EXIF / EXIF 关闭（耳区）/ 四开关全关，
    /// 横屏刘海在左（胶片栏与 EXIF 开）/ 胶片栏关闭（上下耳区单按钮）/ 控制栏关闭，刘海在右各一张。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="workingExif">当前启用的 EXIF 显示项。</param>
    /// <param name="lines">报告行。</param>
    private static async Task CaptureStateShotsAsync(iOSNativeRootViewController root, MainViewModel main,
        HashSet<string> workingExif, List<string> lines)
    {
        var configs = BuildConfigs(phoneDevice: true);
        var stages = new (Config Config, Combo Combo, string Stage)[]
        {
            (configs[0], new Combo("shot-portrait-exif", true, true, false, true, true, false), "u3-portrait-exif"),
            (configs[0], new Combo("shot-portrait-ear", true, true, false, true, false, false), "u3-portrait-ear"),
            (configs[0], new Combo("shot-portrait-all-off", false, false, false, true, false, false), "u3-portrait-all-off"),
            (configs[1], new Combo("shot-landscape-exif", true, true, false, true, true, false), "u3-landscape-notch-left-exif"),
            (configs[1], new Combo("shot-landscape-film-off", false, true, false, true, true, false), "u3-landscape-notch-left-film-off"),
            (configs[1], new Combo("shot-landscape-control-off", true, false, false, true, true, false), "u3-landscape-notch-left-control-off"),
            (configs[2], new Combo("shot-landscape-notch-right", true, true, false, true, true, false), "u3-landscape-notch-right-exif"),
        };
        foreach (var (config, combo, stage) in stages)
        {
            await ApplyConfigAsync(root, main, config);
            await ApplyComboAsync(root, main, combo, workingExif);
            await HoldAsync(stage, lines);
        }
        lines.Add("PASS 定版截图：竖屏带 EXIF / 耳区 / 四开关全关，横屏刘海在左三态与刘海在右各一张");
    }

    /// <summary>iPhone 竖屏放大并平移到角落的截图（照片垫在栏位下方）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="entryExif">进入检查时启用的 EXIF 项。</param>
    /// <param name="lines">报告行。</param>
    private static async Task CaptureZoomedShotAsync(iOSNativeRootViewController root, MainViewModel main, HashSet<string> workingExif, List<string> lines)
    {
        await ApplyConfigAsync(root, main, BuildConfigs(phoneDevice: true)[0]);
        await ApplyComboAsync(root, main, ShownCombo, workingExif);
        await ZoomBeyondFitAsync(root, 2);
        await UiDispatcher.InvokeAsync(() => root.Photo.CenterOnNormalizedPoint(.2, .25));
        await SettleAsync(root);
        await HoldAsync("zoomed-under-bars", lines);
        await UiDispatcher.InvokeAsync(() => root.Photo.FitToView());
        await SettleAsync(root);
    }

    /// <summary>收尾还原：方向、舞台、尺寸类别、布局模式、栏位显隐、EXIF 开关、EXIF 显示项与筛选状态。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="settings">进入本检查时的不可变设置快照。</param>
    /// <param name="film">进入时的胶片栏显隐。</param>
    /// <param name="control">进入时的控制栏显隐。</param>
    /// <param name="analysis">进入时的分析栏显隐。</param>
    /// <param name="exifVisible">进入时的手机 EXIF 开关。</param>
    /// <param name="entryExif">进入时启用的 EXIF 项。</param>
    /// <param name="orientation">进入时的界面方向。</param>
    /// <param name="sort">进入时的排序方式。</param>
    /// <param name="order">进入时的排序方向。</param>
    /// <param name="ratingFilter">进入时的星级筛选。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, AppSettings settings,
        bool film, bool control, bool analysis, bool exifVisible, HashSet<string> entryExif,
        UIInterfaceOrientation orientation, SortMode sort, SortOrder order, string ratingFilter)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            root.StageWidthFraction = null;
            root.TraitOverrides.RemoveTrait<UITraitHorizontalSizeClass>();
            root.AvoidanceTuningOverride = null;
            root.FitInsetsOverride = null;
            main.Settings.ApplySnapshot(settings);
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            main.IsAnalysisViewVisible = analysis;
            main.IsExifViewVisible = exifVisible;
            foreach (var item in main.Settings.ExifDisplayItems)
            {
                bool enabled = entryExif.Contains(item.PropertyName);
                if (item.IsEnabled != enabled) item.IsEnabled = enabled;
            }
            main.FileVM.FilterBar.SortMode = sort;
            main.FileVM.FilterBar.SortOrder = order;
            main.FileVM.FilterBar.SelectedRatingFilter = ratingFilter;
            root.SupportedOrientationsOverride = null;
            root.SetNeedsUpdateOfSupportedInterfaceOrientations();
        });
        await SettleAsync(root);
        var mask = orientation switch
        {
            UIInterfaceOrientation.Portrait => UIInterfaceOrientationMask.Portrait,
            UIInterfaceOrientation.PortraitUpsideDown => UIInterfaceOrientationMask.PortraitUpsideDown,
            UIInterfaceOrientation.LandscapeLeft => UIInterfaceOrientationMask.LandscapeLeft,
            UIInterfaceOrientation.LandscapeRight => UIInterfaceOrientationMask.LandscapeRight,
            _ => UIInterfaceOrientationMask.Portrait,
        };
        if (CurrentOrientation(root) != orientation)
        {
            try
            {
                await RequestOrientationAsync(root, mask);
            }
            catch (InvalidOperationException exception)
            {
                Console.WriteLine("[NativeSmoke] form 收尾旋转未生效：" + exception.Message);
            }
        }
        await UiDispatcher.InvokeAsync(() =>
        {
            root.BuildControls();
            root.Photo.FitToView();
        });
        await SettleAsync(root);
    }

    /// <summary>记录一个截图检查点并停留，供外部脚本截图。</summary>
    /// <param name="stage">检查点名（与截图文件名一致）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task HoldAsync(string stage, List<string> lines)
    {
        lines.Add($"STAGE {stage}");
        await WriteStageAsync(stage);
        await Task.Delay(StageHold);
    }

    /// <summary>写入当前截图阶段名（外部脚本据此截图）。</summary>
    /// <param name="stage">阶段名；<c>done</c> 表示验收结束。</param>
    private static Task WriteStageAsync(string stage)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer", "native-ui-snapshot-stage.txt");
        return File.WriteAllTextAsync(path, stage);
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="label">等待标签。</param>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    /// <summary>等待一轮布局落地（UIKit 布局 + 共享 VM 通知）。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(120);
        await UiDispatcher.InvokeAsync(() =>
        {
            root.View!.LayoutIfNeeded();
            root.Stage.LayoutIfNeeded();
        });
        await Task.Delay(80);
    }

    /// <summary>当前界面方向（以 window scene 的有效几何为准）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>界面方向；无 scene 时为 Unknown。</returns>
    private static UIInterfaceOrientation CurrentOrientation(iOSNativeRootViewController root)
        => root.View?.Window?.WindowScene?.EffectiveGeometry?.InterfaceOrientation ?? UIInterfaceOrientation.Unknown;

    /// <summary>当前是否手机横屏排布。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>手机横屏形态时为 true。</returns>
    private static bool IsLandscapeForm(iOSNativeRootViewController root)
        => root.Form == ScreenForm.PhoneLandscape;

    /// <summary>主图适应区（舞台坐标，由内边距与主图尺寸直算，空态同样有效）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>适应区矩形。</returns>
    private static CGRect FitArea(iOSNativeRootViewController root)
    {
        var bounds = root.Photo.Bounds;
        var insets = root.Photo.FitInsets;
        return new CGRect(
            (double)insets.Left,
            (double)insets.Top,
            Math.Max(0, (double)bounds.Width - (double)insets.Left - (double)insets.Right),
            Math.Max(0, (double)bounds.Height - (double)insets.Top - (double)insets.Bottom));
    }

    /// <summary>照片（含适应区内边距）在舞台坐标中的外接矩形。</summary>
    /// <param name="photo">主图宿主。</param>
    /// <returns>外接矩形。</returns>
    private static (double Left, double Top, double Right, double Bottom) PhotoBounds(iOSPhotoView photo)
    {
        double width = photo.DisplayWidth * photo.PointScale, height = photo.DisplayHeight * photo.PointScale;
        double centerX = (double)photo.Bounds.Width / 2 + photo.Viewport.PanX;
        double centerY = (double)photo.Bounds.Height / 2 + photo.Viewport.PanY;
        return (centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2);
    }

    /// <summary>视图在舞台坐标中的 frame。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="view">目标视图。</param>
    /// <returns>舞台坐标矩形。</returns>
    private static CGRect FrameInStage(iOSNativeRootViewController root, UIView view)
        => view.ConvertRectToView(view.Bounds, root.Stage);

    /// <summary>视图在舞台子视图中的层级序号：取视图所在的那个舞台直接子视图（手机玻璃控件在玻璃容器里）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="view">目标视图。</param>
    /// <returns>舞台子视图序号，不在舞台内时为 -1。</returns>
    private static int StageIndex(iOSNativeRootViewController root, UIView view)
    {
        UIView? current = view;
        while (current != null && !ReferenceEquals(current.Superview, root.Stage)) current = current.Superview;
        return current == null ? -1 : Array.IndexOf(root.Stage.Subviews, current);
    }

    /// <summary>视图自身与全部祖先都未隐藏。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="view">目标视图。</param>
    /// <returns>实际可见时为 true。</returns>
    private static bool IsVisible(iOSNativeRootViewController root, UIView view)
    {
        for (UIView? current = view; current != null && !ReferenceEquals(current, root.View); current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    /// <summary>槽位矩形是否非空。</summary>
    /// <param name="rect">槽位矩形。</param>
    /// <returns>宽度与高度都大于 0 时为 true。</returns>
    private static bool Has(LayoutRect rect) => rect.Width > 0 && rect.Height > 0;

    /// <summary>两个共享矩形是否一致（±0.5）。</summary>
    /// <param name="left">矩形一。</param>
    /// <param name="right">矩形二。</param>
    /// <returns>一致时为 true。</returns>
    private static bool SameRect(LayoutRect left, LayoutRect right)
        => Has(left) == Has(right)
        && Math.Abs(left.X - right.X) <= Tolerance && Math.Abs(left.Y - right.Y) <= Tolerance
        && Math.Abs(left.Width - right.Width) <= Tolerance && Math.Abs(left.Height - right.Height) <= Tolerance;

    /// <summary>共享矩形转 UIKit 矩形。</summary>
    /// <param name="rect">共享矩形。</param>
    /// <returns>UIKit 矩形。</returns>
    private static CGRect ToCGRect(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>两个矩形是否实质相交（重叠超过半个点）。</summary>
    /// <param name="left">矩形一。</param>
    /// <param name="right">矩形二。</param>
    /// <returns>相交时为 true。</returns>
    private static bool Intersects(CGRect left, CGRect right)
    {
        double width = Math.Min((double)left.GetMaxX(), (double)right.GetMaxX()) - Math.Max((double)left.X, (double)right.X);
        double height = Math.Min((double)left.GetMaxY(), (double)right.GetMaxY()) - Math.Max((double)left.Y, (double)right.Y);
        return width > Tolerance && height > Tolerance;
    }

    /// <summary>矩形向外扩张。</summary>
    /// <param name="rect">矩形。</param>
    /// <param name="amount">扩张量（点）。</param>
    /// <returns>扩张后的矩形。</returns>
    private static CGRect Expand(CGRect rect, double amount)
        => amount <= 0 ? rect : rect.Inset((nfloat)(-amount), (nfloat)(-amount));

    /// <summary>外矩形是否完整包含内矩形。</summary>
    /// <param name="outer">外矩形。</param>
    /// <param name="inner">内矩形。</param>
    /// <returns>包含时为 true。</returns>
    private static bool Contains(CGRect outer, CGRect inner)
        => (double)inner.X >= (double)outer.X - Tolerance && (double)inner.Y >= (double)outer.Y - Tolerance
            && (double)inner.GetMaxX() <= (double)outer.GetMaxX() + Tolerance
            && (double)inner.GetMaxY() <= (double)outer.GetMaxY() + Tolerance;

    /// <summary>把菜单元素（含内联子菜单）摊平成标题序列，用于比较菜单内容。</summary>
    /// <param name="elements">顶层元素。</param>
    /// <returns>标题序列。</returns>
    private static List<string> Flatten(IEnumerable<UIMenuElement> elements)
    {
        var titles = new List<string>();
        foreach (var element in elements)
        {
            switch (element)
            {
                case UIMenu menu:
                    titles.Add("[menu]");
                    titles.AddRange(Flatten(menu.Children));
                    break;
                case UIAction action:
                    titles.Add(action.Title);
                    break;
            }
        }
        return titles;
    }

    /// <summary>主图菜单里带勾选态的 action（按标识读取当前勾选）。</summary>
    /// <param name="elements">菜单元素。</param>
    /// <returns>标识 → 是否勾选。</returns>
    private static Dictionary<string, bool> PhotoMenuStates(IEnumerable<UIMenuElement> elements)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            switch (element)
            {
                case UIMenu menu:
                    foreach (var (key, value) in PhotoMenuStates(menu.Children)) states[key] = value;
                    break;
                case UIAction { Identifier: { Length: > 0 } identifier } action:
                    states[identifier] = action.State == UIMenuElementState.On;
                    break;
            }
        }
        return states;
    }

    /// <summary>尺寸格式化。</summary>
    /// <param name="size">尺寸。</param>
    /// <returns>文本。</returns>
    private static string Size(CGSize size) => FormattableString.Invariant($"{size.Width:0.#}×{size.Height:0.#}");

    /// <summary>原生矩形尺寸格式化。</summary>
    /// <param name="rect">原生矩形。</param>
    /// <returns>文本。</returns>
    private static string Size(CGRect rect) => FormattableString.Invariant($"{rect.Width:0.#}×{rect.Height:0.#}");

    /// <summary>矩形格式化。</summary>
    /// <param name="rect">共享矩形。</param>
    /// <returns>文本。</returns>
    private static string Rect(LayoutRect rect) => FormattableString.Invariant($"{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}");

    /// <summary>UIKit 矩形格式化。</summary>
    /// <param name="rect">矩形。</param>
    /// <returns>文本。</returns>
    private static string CGRect(CGRect rect) => FormattableString.Invariant($"{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}");

    /// <summary>照片外接矩形格式化。</summary>
    /// <param name="photo">外接矩形。</param>
    /// <returns>文本。</returns>
    private static string Format((double Left, double Top, double Right, double Bottom) photo)
        => FormattableString.Invariant($"{photo.Left:0.#},{photo.Top:0.#}→{photo.Right:0.#},{photo.Bottom:0.#}");

    /// <summary>内边距格式化。</summary>
    /// <param name="insets">内边距。</param>
    /// <returns>文本。</returns>
    private static string Insets(UIEdgeInsets insets)
        => FormattableString.Invariant($"T{insets.Top:0.#} L{insets.Left:0.#} B{insets.Bottom:0.#} R{insets.Right:0.#}");

    /// <summary>开关格式化。</summary>
    /// <param name="value">状态。</param>
    /// <returns>文本。</returns>
    private static string OnOff(bool value) => value ? "开" : "关";

    /// <summary>尺寸类别格式化。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>文本。</returns>
    private static string TraitSizes(iOSNativeRootViewController root)
        => $"{root.TraitCollection.HorizontalSizeClass}/{root.TraitCollection.VerticalSizeClass}";
}
