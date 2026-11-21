using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.Diagnostics;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：`--pv-native-layout-check &lt;目录&gt;` 在模拟器/真机上程序化核对宽形态主屏——
/// 浮层外壳的分区（主图满舞台、分区表达为适应区内边距、栏位背景铺满边缘、
/// 上下布局内容收在安全区内且横屏不为底部手势条保留安全区、
/// 左右布局栏位内容纵贯窗口且窗口控件只推筛选按钮）、
/// 栏位显隐把对应一侧内边距退回安全区并释放适应区长度、宽形态固定大档的胶片卡片几何（带厚 / 可见容量 ≥5 /
/// 96×144 内部）、宽形态筛选控件（两段合体胶囊 2 × 48）与控制栏（EXIF 块按方向取衬底/无衬底变体 / 星级控件 / 44 点圆按钮），
/// 以及 [FILM-01–06] 的交互语义（点选不居中、显式定位的中间帧与终点、用户接管、减少动态效果直接到终点、
/// 点星写回自有副本且不切主图、改星后筛选暂留）、卡片输入分流（指针点星写值 / 触摸只选中）、
/// 卡片长按与辅助点按的星级弹窗（方向、落位、原地抬手保持、滑动提交一次、越界不写），
/// 工具区共用单个背景宿主与「分析 + 控制」连续并集（SHELL-12：接缝 8、外边距 10、显示评分关闭不留槽），
/// 还有胶片栏显示快照回归（live 筛选列表换短、UIKit 重载前的旧索引仍按同一快照回答，不越界）。
/// 素材要求：目录内 ≥12 张自有夹具（Fixtures/jpeg/*.jpg 的容器内副本，含 XMP 可写评级），
/// 且目录必须位于本 App 沙盒内 —— 任何情况下都不写用户照片或 TestCase。
/// 报告写 Documents/PhotoViewer/native-layout-checks.txt，打印 <c>[NativeSmoke] PASS layout</c> / FAIL。
/// </summary>
internal static class iOSNativeLayoutCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-layout-check";
    /// <summary>代码内固定几何允许的误差。</summary>
    private const double Exact = .01;
    /// <summary>布局沉淀等待（UIKit 一轮布局 + 共享 VM 通知）。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(160);

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
        _ = RunAsync(root, session, ResolveDocumentsPath(directory));
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义，与壳层 --pv-open 同一口径）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：等主屏就绪与素材载入 → 逐项断言 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-layout-checks.txt");
        var entryExif = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            bool entrySimilarity = main.FileVM.FilterBar.IsSimilarityPanelOpen;
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"布局验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length < 12) throw new InvalidOperationException($"布局验收需要 ≥12 张自有夹具，实际 {fixtures.Length}（{directory}）");
            await UiDispatcher.InvokeAsync(() =>
            {
                main.Settings.ShowRating = true;
                main.IsThumbnailViewVisible = true;
                main.IsControlViewVisible = true;
                main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
                main.FileVM.FilterBar.SelectedRatingFilter = "All";
                foreach (var format in main.Settings.FileFormats)
                    if (format.DisplayName == "JPG") format.IsEnabled = true;
                // FILM-02 端点用真实上一张/下一张按钮：用户可能在设置里隐藏了它们，验收会话临时打开（收尾由快照还原）。
                foreach (var hotkey in main.Settings.Hotkeys)
                    if (hotkey.Command is "Previous" or "Next") hotkey.IsDisplay = true;
                // 控制栏 EXIF 块几何需要真实显示项：隔离设置里可能全部关闭，临时打开四个默认项（收尾还原）。
                foreach (var item in main.Settings.ExifDisplayItems)
                {
                    entryExif[item.PropertyName] = item.IsEnabled;
                    if (SettingsCatalog.DefaultExifFields.Any(field => field.IsEnabled && field.Id == item.PropertyName))
                        item.IsEnabled = true;
                }
                root.BuildControls();
            });
            await main.FolderVM.OpenFolderAsync(new LocalFolderRef(directory));
            await WaitAsync(() => main.FolderVM.AllFiles.Count >= 12, "夹具目录枚举");
            await WaitAsync(() => main.CurrentFile != null, "首图选中");
            await SettleAsync(root);
            VerifyToolRegionMath("工具区");
            // 本检查会经真实评分路径改写自有副本：先记下进入时的星级，收尾一律按磁盘真值还原。
            var entryRatings = main.FolderVM.AllFiles.ToDictionary(
                file => file.File.StorageId.Value, file => Math.Max(0, file.Rating), StringComparer.Ordinal);
            var entryLayout = main.Settings.LayoutMode;
            bool entryShowNavigation = main.Settings.ShowNavigationButtons;
            lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），主屏安全区 insets={Insets(root.View!)}");

            if (root.Form == ScreenForm.Wide)
            {
                await VerifyRatingHiddenAsync(root, main, lines);
                // 一、两向显式布局与自动布局：当前朝向 + 尝试驱动系统旋转得到的另一朝向
                var startOrientation = IsLandscape(root);
                await VerifyOrientationAsync(root, main, startOrientation, lines);
                var flipped = startOrientation ? UIInterfaceOrientationMask.Portrait : UIInterfaceOrientationMask.Landscape;
                if (await RotateAsync(root, flipped))
                {
                    bool rotated = IsLandscape(root);
                    lines.Add($"PASS 系统旋转：{(startOrientation ? "landscape" : "portrait")} → {(rotated ? "landscape" : "portrait")}（requestGeometryUpdate 生效）");
                    await VerifyOrientationAsync(root, main, rotated, lines);
                    await RotateAsync(root, startOrientation ? UIInterfaceOrientationMask.Landscape : UIInterfaceOrientationMask.Portrait);
                }
                else
                {
                    lines.Add($"NOTE 系统旋转未被接受（{lastRotationFailure}）：本机未驱动方向切换，改用同一份布局数学核对另一朝向的尺寸口径");
                    VerifySyntheticOrientation(root, main, lines);
                }
                await UiDispatcher.InvokeAsync(() => main.Settings.LayoutMode = LayoutMode.Row);
                await SettleAsync(root);
            }
            else
            {
                // 手机形态：宽形态两向分区与筛选条/控制栏不参与，外壳取共享手机槽位。
                await UiDispatcher.InvokeAsync(() => main.Settings.ShowNavigationButtons = true);
                await SettleAsync(root);
                await VerifyPhoneShellAsync(root, main, lines);
                await VerifyCardPopupAsync(root, main, root.Form == ScreenForm.PhonePortrait,
                    root.Form == ScreenForm.PhonePortrait ? "手机竖屏" : "手机横屏", lines);
            }

            // 二、胶片栏交互（[FILM-01–03]）
            await VerifyFilmstripAsync(root, main, lines, session);

            // 三、卡片星级写回与筛选连续性（[FILM-05/06]）
            await VerifyRatingAsync(root, main, lines, directory);

            // 五、胶片栏显示快照：重载前的陈旧索引不得读到缩短后的 live 筛选列表
            await VerifyDisplaySnapshotAsync(root, main, lines);

            await RestoreAsync(root, main, entrySimilarity, entryExif, entryLayout, entryShowNavigation);
            await RestoreRatingsAsync(main, entryRatings);
            lines.Add($"PASS 收尾还原：布局模式/栏位显隐/筛选/评级已回到进入本检查时的状态");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS layout ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            // 失败路径也把临时打开的 EXIF 显示项还原，避免留下与进入时不同的隔离设置。
            if (entryExif.Count > 0 && iOSNativeShell.Main is { } restoreTarget)
            {
                try
                {
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        foreach (var item in restoreTarget.Settings.ExifDisplayItems)
                            if (entryExif.TryGetValue(item.PropertyName, out bool enabled) && item.IsEnabled != enabled)
                                item.IsEnabled = enabled;
                    });
                }
                catch (Exception restoreFailure)
                {
                    Console.Error.WriteLine("[NativeSmoke] layout EXIF 显示项还原失败 " + restoreFailure.Message);
                }
            }
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL layout " + exception);
        }
    }

    /// <summary>
    /// 手机形态外壳（SHELL-08、SHELL-09）：宽形态浮层全部收起，主图分区与主图视图铺满舞台，
    /// 适应区、胶片条档位与各手机控件的 frame 逐项等于按当前输入重算的共享手机槽位。
    /// 两向布局与栏位显隐的组合由 form 检查覆盖，这里证明主屏消费的是同一份共享几何。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyPhoneShellAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        if (root.Form == ScreenForm.Wide) throw new InvalidOperationException("手机外壳核对要求手机形态");
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        var stage = root.Stage.Bounds;
        if (!root.FilterBar.Hidden || !root.ControlScroll.Hidden || !root.FileBarView.Hidden || !root.FileDividerView.Hidden)
            throw new InvalidOperationException(
                $"手机形态仍显示宽形态浮层：筛选={!root.FilterBar.Hidden} 控制栏={!root.ControlScroll.Hidden} " +
                $"文件栏={!root.FileBarView.Hidden} 分隔线={!root.FileDividerView.Hidden}");
        RequireFrame(root.PhotoPane.Frame, new CGRect(0, 0, stage.Width, stage.Height), "手机形态主图分区未铺满舞台");
        RequireFrame(root.Photo.Frame, new CGRect(0, 0, stage.Width, stage.Height), "手机形态主图视图未铺满舞台");

        var slots = PhoneScreenLayout.Compute(root.PhoneSlotsInput);
        var avoid = slots.PhotoAvoidance;
        var photo = new LayoutRect(avoid.Left, avoid.Top,
            Math.Max(0, (double)stage.Width - avoid.Left - avoid.Right), Math.Max(0, (double)stage.Height - avoid.Top - avoid.Bottom));
        RequireFitInsets(root, photo, stage, "手机形态适应区");
        RequireFrame(root.Photo.FitRect, ToFrame(photo), "手机形态适应区与共享避让不一致");
        RequireOverlaysInFitArea(root, portrait ? "手机竖屏" : "手机横屏");

        if (root.Filmstrip.Horizontal != portrait)
            throw new InvalidOperationException($"手机形态胶片条轴向 {root.Filmstrip.Horizontal} 与{(portrait ? "竖屏横向" : "横屏纵向")}不符");
        var tier = FilmCardGeometry.ResolveTier(portrait ? (double)stage.Width : (double)stage.Height, portrait);
        if (root.Filmstrip.Tier != tier)
            throw new InvalidOperationException($"手机形态胶片条档位 {root.Filmstrip.Tier} ≠ 共享选档 {tier}");

        RequireSlot(root.PhoneFilterGlass, slots.Filter, "筛选");
        RequireSlot(root.PhoneMoreButton, slots.More, "更多");
        RequireSlot(root.PhoneFilterMoreGroup, slots.FilterMoreGroup, "筛选/更多合体");
        RequireSlot(root.PhoneNavigationGroup, slots.Navigation, "上下张");
        RequireSlot(root.PhoneRatingControl, slots.Rating, "星级控件");
        RequireSlot(root.PhoneCommandsGroup, slots.Commands, "可选命令");
        RequireSlot(root.PhoneExifBlock, slots.Exif, "EXIF 块");
        var bars = root.PhoneBarBackgrounds;
        RequireSlot(bars.Top, slots.TopBar, "顶栏背景");
        RequireSlot(bars.Bottom, slots.BottomBar, "底栏背景");
        RequireSlot(bars.Leading, slots.LeadingBar, "刘海侧栏背景");
        RequireSlot(bars.Trailing, slots.TrailingBar, "工具区背景");
        if (root.PhoneExifBlock.ShownItemCount != slots.ExifItems)
            throw new InvalidOperationException(
                $"手机形态 EXIF 显示 {root.PhoneExifBlock.ShownItemCount} 项 ≠ 共享裁剪 {slots.ExifItems} 项");
        lines.Add($"PASS 手机外壳（{(portrait ? "竖屏" : "横屏")}）：宽形态浮层收起、主图满舞台、适应区 " +
            $"{(double)root.Photo.FitRect.Width:0.#}×{(double)root.Photo.FitRect.Height:0.#}、胶片条 {tier}、" +
            $"筛选/更多/上下张/星级/可选命令/EXIF 与栏背景逐项等于共享槽位，空态提示/提示条/计算中药丸与导航图落在适应区内");

        // SETTINGS-17「显示上下张按钮」两态：关闭后合体收起，勾选的独立上一张/下一张按 SETTINGS-08 进入可选命令组；
        // 槽位与命令按钮按同一份共享解析重算，打开后恢复。
        await UiDispatcher.InvokeAsync(() => main.Settings.ShowNavigationButtons = false);
        await SettleAsync(root);
        try
        {
            var off = PhoneScreenLayout.Compute(root.PhoneSlotsInput);
            if (off.Navigation.Width > 0 || off.Navigation.Height > 0)
                throw new InvalidOperationException($"关闭显示上下张后槽位仍占位：{off.Navigation}");
            RequireSlot(root.PhoneNavigationGroup, off.Navigation, "上下张（关闭）");
            RequireSlot(root.PhoneCommandsGroup, off.Commands, "可选命令（关闭组合开关）");
            var expected = PhoneControlCommands.Resolve(
                main.ControlVM.EnabledControls.Select(hotkey => hotkey.Command), showNavigationButtons: false);
            if (root.PhoneSlotsInput.CommandCount != expected.Length
                || root.PhoneCommandsGroup.Buttons.Count != expected.Length)
                throw new InvalidOperationException(
                    $"关闭上下张后命令组 {root.PhoneCommandsGroup.Buttons.Count} 段 ≠ 共享解析 {expected.Length} 项");
            if (expected.Length > 0)
            {
                double size = root.PhoneSlotsInput.CommandButtonSize;
                double mainLength = portrait ? off.Commands.Width : off.Commands.Height;
                if (Math.Abs(mainLength - expected.Length * size) > 1)
                    throw new InvalidOperationException(
                        $"命令组主轴 {mainLength:0.##} ≠ {expected.Length} × {size:0.##}");
            }
            if (portrait && Math.Abs(off.Commands.Width - slots.Commands.Width) <= 1
                && off.Rating.Width < slots.Rating.Width - 1)
                throw new InvalidOperationException(
                    $"竖屏关闭上下张后星级未取回让出的长度：{slots.Rating.Width:0.##} → {off.Rating.Width:0.##}");
            lines.Add($"PASS 显示上下张按钮关闭：{(portrait ? "底部行合体收起、星级按释放长度重排" : "刘海侧上下张收起")}，" +
                $"独立上下张按共享解析进入可选命令组（{expected.Length} 项）");
        }
        finally
        {
            await UiDispatcher.InvokeAsync(() => main.Settings.ShowNavigationButtons = true);
            await SettleAsync(root);
        }
        RequireSlot(root.PhoneNavigationGroup, PhoneScreenLayout.Compute(root.PhoneSlotsInput).Navigation, "上下张（恢复）");
    }

    /// <summary>
    /// 主图区的浮层（空态提示、STATE-03 提示条与计算中药丸）都落在适应区内：栏位与按钮是浮层，
    /// 这些提示不能压到栏位或裸按钮上。导航图由 gesture 检查在放大态单独核对。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围名。</param>
    private static void RequireOverlaysInFitArea(iOSNativeRootViewController root, string scope)
    {
        var fit = root.Photo.FitRect;
        RequireInsideFit(root.HintLabel, fit, scope + " 空态提示");
        RequireInsideFit(root.Toast, fit, scope + " 状态提示条");
        RequireInsideFit(root.EnhanceBusyLabel, fit, scope + " 计算中药丸");
    }

    /// <summary>视图矩形完整落在适应区内（±1 点容差）。</summary>
    /// <param name="view">浮层视图。</param>
    /// <param name="fit">主图适应区。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireInsideFit(UIView view, CGRect fit, string context)
    {
        var frame = view.Frame;
        if ((double)frame.Width <= 0 || (double)frame.Height <= 0)
            throw new InvalidOperationException($"{context} 尺寸非法：{frame}（适应区 {fit}）");
        if ((double)frame.X < (double)fit.X - 1 || (double)frame.Y < (double)fit.Y - 1
            || (double)frame.GetMaxX() > (double)fit.GetMaxX() + 1 || (double)frame.GetMaxY() > (double)fit.GetMaxY() + 1)
            throw new InvalidOperationException($"{context} 越出主图适应区：{frame} / {fit}");
    }

    /// <summary>控件或栏背景与槽位：槽位非空时必须可见且 frame 与槽位一致，槽位为空时必须隐藏。</summary>
    /// <param name="view">原生控件或栏背景。</param>
    /// <param name="slot">共享槽位矩形。</param>
    /// <param name="name">报告名。</param>
    private static void RequireSlot(UIView view, LayoutRect slot, string name)
    {
        bool visible = slot.Width > 0 && slot.Height > 0;
        if (view.Hidden == visible)
            throw new InvalidOperationException($"手机形态 {name} 可见性 {!view.Hidden} 与槽位 {slot} 不符");
        if (visible) RequireFrame(view.Frame, ToFrame(slot), $"手机形态 {name} 与共享槽位不一致", 1);
    }

    /// <summary>核对某一朝向下的两向与自动布局：安全区、分区拼合、栏位显隐、筛选条、控制栏与卡片几何。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="orientation">当前界面朝向。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyOrientationAsync(iOSNativeRootViewController root, MainViewModel main, bool landscape, List<string> lines)
    {
        string label = landscape ? "landscape" : "portrait";
        foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col })
        {
            await UiDispatcher.InvokeAsync(() => main.Settings.LayoutMode = mode);
            await SettleAsync(root);
            if (main.IsRowLayout != (mode == LayoutMode.Row)) throw new InvalidOperationException($"{label}/{mode} 布局方向未按设置生效");
            VerifyPartition(root, main, mode == LayoutMode.Row, $"{label}/{mode}", lines);
            VerifyFilterBar(root, mode == LayoutMode.Row, $"{label}/{mode}", lines);
            VerifyControlBar(root, main, mode == LayoutMode.Row, $"{label}/{mode}", lines);
            await VerifyFilmstripGeometryAsync(root, main, mode == LayoutMode.Row, $"{label}/{mode}", lines);
            await VerifyPaneReleaseAsync(root, main, mode == LayoutMode.Row, $"{label}/{mode}", lines);
            await VerifyCardPopupAsync(root, main, mode == LayoutMode.Row, $"{label}/{mode}", lines);
        }
        await UiDispatcher.InvokeAsync(() => main.Settings.LayoutMode = LayoutMode.Auto);
        await SettleAsync(root);
        VerifyColumnSafeInjection((double)root.Stage.Bounds.Width, (double)root.Stage.Bounds.Height, label);
        // 横屏贴角带只在手机横屏排布下存在（宽形态两向分区不消费这两条带）。
        if (root.Form == ScreenForm.PhoneLandscape) VerifyLandscapeCornerRegions(root, label, lines);
        bool expectRow = root.View!.Bounds.Height >= root.View.Bounds.Width;
        if (main.IsRowLayout != expectRow)
            throw new InvalidOperationException($"{label}/Auto 自动布局未按窗口方向选择分栏（IsRowLayout={main.IsRowLayout}）");
        lines.Add($"PASS {label}/Auto：按窗口内容方向选择{(expectRow ? "上下" : "左右")}分栏，未随照片横竖切换");
    }

    /// <summary>系统旋转不可用时的兜底核对：用同一份布局数学检查另一朝向的尺寸口径（真实视图仍按当前朝向断言）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifySyntheticOrientation(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var bounds = root.View!.Bounds;
        var raw = root.View.SafeAreaInsets;
        // 用真实尺寸与真实安全区把主屏转 90°：两向共用同一份布局数学，故翻转后的口径可直接核对。
        (bool Row, double Width, double Height)[] cases =
            [(true, (double)bounds.Width, (double)bounds.Height), (false, (double)bounds.Height, (double)bounds.Width)];
        foreach (var scenario in cases)
        {
            // 翻转后宽大于高即为横屏主屏：其布局安全区同样不为底部手势条保留空间。
            var safe = ShellPaneLayout.SafeInsets(Edges(raw), scenario.Width, scenario.Height);
            double band = FilmCardGeometry.BandThickness(root.Filmstrip.Tier, scenario.Row);
            double fileMain = scenario.Row
                ? root.FilterBar.MainLength(true, (double)safe.Top + MainScreenLayout.FilterMargin) + band + MainScreenLayout.Divider
                : FilmSideGeometry.Measure((double)safe.Left, band,
                    FilmCardGeometry.CrossLead(FilmCardTier.Large, false),
                    FilmCardGeometry.CrossTrail(FilmCardTier.Large, false), MobileFilterLayout.GroupWidth).Width
                    + MainScreenLayout.Divider;
            double controlMain = MainScreenLayout.ControlBarMainLength(scenario.Row ? 60 : 244);
            var panes = ShellPaneLayout.Partition(scenario.Row, scenario.Width, scenario.Height, safe, fileMain, controlMain, 0);
            if ((double)panes.Photo.Width < 1 || (double)panes.Photo.Height < 1)
                throw new InvalidOperationException($"翻转尺寸 {scenario.Width}×{scenario.Height} 主图被压成零尺寸：{panes.Photo}");
            double boxMain = (scenario.Row ? scenario.Height : scenario.Width)
                - (scenario.Row ? (double)safe.Top + (double)safe.Bottom : (double)safe.Left + (double)safe.Right);
            double expectedMain = Math.Max(1, boxMain - fileMain - controlMain);
            double actualMain = scenario.Row ? (double)panes.Photo.Height : (double)panes.Photo.Width;
            if (Math.Abs(actualMain - expectedMain) > 1)
                throw new InvalidOperationException($"翻转尺寸 {scenario.Width}×{scenario.Height} 主图未吸收剩余主轴空间：{actualMain:0.##} != {expectedMain:0.##}");
            bool chain = scenario.Row
                ? Math.Abs((double)panes.FileDivider.MaxY - (double)panes.Photo.Y) < Exact && Math.Abs((double)panes.ControlDivider.Y - (double)panes.Photo.MaxY) < Exact
                : Math.Abs((double)panes.FileDivider.MaxX - (double)panes.Photo.X) < Exact && Math.Abs((double)panes.ControlDivider.X - (double)panes.Photo.MaxX) < Exact;
            if (!chain) throw new InvalidOperationException($"翻转尺寸 {scenario.Width}×{scenario.Height} 分区未首尾相接");
            bool edges = scenario.Row
                ? Math.Abs((double)panes.FileBar.Y) < Exact && Math.Abs((double)panes.ControlBackground.MaxY - scenario.Height) < Exact
                : Math.Abs((double)panes.FileBar.X) < Exact && Math.Abs((double)panes.ControlBackground.MaxX - scenario.Width) < Exact;
            if (!edges) throw new InvalidOperationException($"翻转尺寸 {scenario.Width}×{scenario.Height} 栏位背景未铺到屏幕边缘");
            lines.Add($"NOTE 翻转尺寸核对 {scenario.Width:0}×{scenario.Height:0}（{(scenario.Row ? "上下" : "左右")}）：主图 {Size(panes.Photo)}，栏位背景铺满边缘、分区首尾相接（真实视图当前为 {Size(bounds)}，系统安全区 T{raw.Top:0.##} L{raw.Left:0.##} B{raw.Bottom:0.##} R{raw.Right:0.##}，布局安全区 B{safe.Bottom:0.##}）");
        }
    }

    /// <summary>
    /// 核对浮层外壳的分区（SHELL-11）：主图分区与主图视图恒为整个舞台，分区矩形改为表达主图<b>适应区</b>的四边内边距；
    /// 文件栏背景、两条分隔线、控制栏滚动内容与分析栏槽位逐项等于共享 <see cref="ShellPaneLayout.Partition"/> 的结果，
    /// 工具区背景是分析/控制共用的单个宿主（几何取可见并集、只在有分析或实际 EXIF 时显示），
    /// 栏位背景按安全区口径铺到屏幕边缘、内容收在安全区内、适应区与任何可见栏位都不相交。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyPartition(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var view = root.View!;
        var stage = root.Stage.Bounds;
        if (root.Stage.Frame != view.Bounds)
            throw new InvalidOperationException($"{scope} 舞台未铺满根视图（本检查不启用限宽）：{root.Stage.Frame} / {view.Bounds}");
        var rawSafe = view.SafeAreaInsets;
        // 横屏主屏（含 iPad 行布局）不为底部 Home 指示条保留安全区；刘海、屏幕圆角与窗口控件仍由各元素带的探针避让。
        bool landscape = (double)stage.Width > (double)stage.Height;
        var layoutSafe = ShellPaneLayout.SafeInsets(Edges(rawSafe), (double)stage.Width, (double)stage.Height);
        var safe = new UIEdgeInsets((nfloat)layoutSafe.Top, (nfloat)layoutSafe.Left, (nfloat)layoutSafe.Bottom, (nfloat)layoutSafe.Right);
        if (landscape && Math.Abs(layoutSafe.Bottom) > Exact)
            throw new InvalidOperationException($"{scope} 横屏仍为底部手势条保留安全区：B{layoutSafe.Bottom:0.##}");
        if (!landscape && Math.Abs(layoutSafe.Bottom - (double)rawSafe.Bottom) > Exact)
            throw new InvalidOperationException(
                $"{scope} 竖屏改动了底部安全区：B{layoutSafe.Bottom:0.##} ≠ 系统 B{rawSafe.Bottom:0.##}");
        var panes = root.Panes;
        // 横屏行布局的内容因此铺到窗口下缘，不为手势条留空。
        if (landscape && row && Math.Abs((double)panes.ControlContent.MaxY - (double)stage.Height) > Exact)
            throw new InvalidOperationException(
                $"{scope} 横屏行布局控制栏内容未铺到窗口下缘：{panes.ControlContent.MaxY:0.##} ≠ {stage.Height:0.##}");
        // 分析栏默认隐藏：本套件不打开它，槽位必须被压成零并把空间让给主图（打开态的几何由 --pv-native-analysis-check 核对）。
        double analysisBand = row ? (double)panes.Analysis.Height : (double)panes.Analysis.Width;
        if (analysisBand > Exact)
            throw new InvalidOperationException($"{scope} 分析栏隐藏时仍占用了空间：{panes.Analysis}");

        // 主图分区与主图视图都是整个舞台（栏位是浮层，不再切走主图空间）。
        RequireFrame(root.PhotoPane.Frame, new CGRect(0, 0, stage.Width, stage.Height), $"{scope} 主图分区未铺满舞台");
        RequireFrame(root.Photo.Frame, new CGRect(0, 0, stage.Width, stage.Height), $"{scope} 主图视图未铺满舞台");
        // 分区矩形表达为适应区：四边内边距与分区矩形在舞台中的位置逐项相等，适应区本身也等于分区矩形。
        RequireFitInsets(root, panes.Photo, stage, $"{scope} 主图适应区");
        RequireFrame(root.Photo.FitRect, ToFrame(panes.Photo), $"{scope} 适应区与分区矩形不一致");
        RequireOverlaysInFitArea(root, scope);

        // 栏位背景、分隔线与滚动容器逐项等于共享分区结果。
        RequireFrame(root.FileBarView.Frame, ToFrame(panes.FileBar), $"{scope} 文件栏背景与分区不一致");
        RequireFrame(root.FileDividerView.Frame, ToFrame(panes.FileDivider), $"{scope} 文件栏分隔线与分区不一致");
        // 分析栏与控制栏共用一个工具区背景宿主：三个兼容访问器指向同一视图，几何恒等于分析/控制并集。
        if (!ReferenceEquals(root.ToolBackgroundView, root.ControlBackgroundView)
            || !ReferenceEquals(root.AnalysisBarBackground, root.ToolBackgroundView)
            || !ReferenceEquals(root.PhoneBarBackgrounds.Trailing, root.ToolBackgroundView))
            throw new InvalidOperationException($"{scope} 控制栏背景/分析栏背景/手机右栏背景不是同一个工具区宿主");
        RequireFrame(root.ToolBackgroundView.Frame, ToFrame(panes.ControlBackground), $"{scope} 工具区背景与分区不一致");
        RequireFrame(root.ControlDividerView.Frame, ToFrame(panes.ControlDivider), $"{scope} 控制栏分隔线与分区不一致");
        RequireFrame(root.ControlScroll.Frame, ToFrame(panes.ControlContent), $"{scope} 控制栏滚动容器与内容区不一致");
        // 背景只由「有分析栏或实际 EXIF」决定，几何不随显隐变化（只有评分/命令时透明）。
        bool backgroundShown = !root.AnalysisPane.Hidden
            || (main.IsControlViewVisible && main.ControlVM.EnabledExifItems.Count > 0);
        if (root.ToolBackgroundView.Hidden == backgroundShown)
            throw new InvalidOperationException(
                $"{scope} 工具区背景可见性不符：Hidden={root.ToolBackgroundView.Hidden}，分析={!root.AnalysisPane.Hidden}、EXIF 项 {main.ControlVM.EnabledExifItems.Count}");

        // 栏位背景铺到屏幕边缘（共享浮层口径：行布局背景补回上下、列布局补回左右）。
        var fileBar = root.FileBarView.Frame;
        var controlBackground = root.ControlBackgroundView.Frame;
        var fileDivider = root.FileDividerView.Frame;
        var controlDivider = root.ControlDividerView.Frame;
        double fileBarMaxY = (double)fileBar.Y + (double)fileBar.Height;
        double fileBarMaxX = (double)fileBar.X + (double)fileBar.Width;
        double fileDividerMaxY = (double)fileDivider.Y + (double)fileDivider.Height;
        double fileDividerMaxX = (double)fileDivider.X + (double)fileDivider.Width;
        double controlBackgroundMaxY = (double)controlBackground.Y + (double)controlBackground.Height;
        double controlBackgroundMaxX = (double)controlBackground.X + (double)controlBackground.Width;
        if (row)
        {
            if (Math.Abs((double)fileBar.X) > Exact || Math.Abs((double)fileBar.Y) > Exact
                || Math.Abs((double)fileBar.Width - (double)stage.Width) > Exact)
                throw new InvalidOperationException($"{scope} 文件栏背景未铺满屏幕顶边/两侧：{fileBar}");
            if (Math.Abs(controlBackgroundMaxY - (double)stage.Height) > Exact
                || Math.Abs((double)controlBackground.Width - (double)stage.Width) > Exact
                || Math.Abs((double)controlBackground.Y - (double)controlDivider.Y) > Exact)
                throw new InvalidOperationException($"{scope} 控制栏背景未从分隔线铺到屏幕底边：{controlBackground}");
            if (Math.Abs((double)fileDivider.Width - (double)stage.Width) > Exact
                || Math.Abs(fileDividerMaxY - (double)panes.Photo.Y) > Exact
                || Math.Abs((double)controlDivider.Y - (double)panes.Photo.MaxY) > Exact
                || Math.Abs(fileBarMaxY + MainScreenLayout.Divider - (double)panes.Photo.Y) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 分隔线未收在主图分区两侧：文件 {fileDivider} / 控制 {controlDivider} / 主图 {panes.Photo}");
        }
        else
        {
            if (Math.Abs((double)fileBar.X) > Exact || Math.Abs((double)fileBar.Y) > Exact
                || Math.Abs((double)fileBar.Height - (double)stage.Height) > Exact)
                throw new InvalidOperationException($"{scope} 文件栏背景未铺满屏幕左边/上下边：{fileBar}");
            if (Math.Abs(controlBackgroundMaxX - (double)stage.Width) > Exact
                || Math.Abs((double)controlBackground.Height - (double)stage.Height) > Exact
                || Math.Abs((double)controlBackground.X - (double)controlDivider.X) > Exact)
                throw new InvalidOperationException($"{scope} 控制栏背景未从分隔线铺到屏幕右边：{controlBackground}");
            if (Math.Abs((double)fileDivider.Height - (double)stage.Height) > Exact
                || Math.Abs(fileDividerMaxX - (double)panes.Photo.X) > Exact
                || Math.Abs((double)controlDivider.X - (double)panes.Photo.MaxX) > Exact
                || Math.Abs(fileBarMaxX + MainScreenLayout.Divider - (double)panes.Photo.X) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 分隔线未收在主图分区两侧：文件 {fileDivider} / 控制 {controlDivider} / 主图 {panes.Photo}");
        }
        // 内容收在布局安全区内：行布局上下都在安全区内（横屏不为底部手势条保留空间）；左右布局纵贯窗口——
        // 顶部/底部安全区（状态栏、Home 指示条、圆角）只推筛选按钮自身，不推胶片列表与控制栏内容。
        if (row)
        {
            RequireInside(root.FilterBar.Frame, safe, stage, $"{scope} 筛选条内容越出安全区");
            RequireInside(root.Filmstrip.Frame, safe, stage, $"{scope} 胶片栏内容越出安全区");
            RequireInside(root.ControlScroll.Frame, safe, stage, $"{scope} 控制栏内容越出安全区");
        }
        else
        {
            RequireColumnSpan(root.Filmstrip.Frame, stage, $"{scope} 胶片栏");
            RequireColumnSpan(root.ControlScroll.Frame, stage, $"{scope} 控制栏内容");
            if (Math.Abs((double)panes.FileContent.Y) > Exact
                || Math.Abs((double)panes.FileContent.Height - (double)stage.Height) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局文件栏内容未纵贯窗口：{panes.FileContent}（顶部/底部安全区不应推列表）");
            if (Math.Abs((double)panes.ControlContent.Y) > Exact
                || Math.Abs((double)panes.ControlContent.Height - (double)stage.Height) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局控制栏内容未纵贯窗口：{panes.ControlContent}（顶部/底部安全区不应推右栏）");
            double controlTop = (double)root.FilterBar.Frames.Control.Y;
            if (Math.Abs(controlTop - FilterLeadingInset(root, false)) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局合体胶囊未从贴角带 + 外边距起排：Y {controlTop:0.##} ≠ {FilterLeadingInset(root, false):0.##}");
            if (controlTop < (double)safe.Top - Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局合体胶囊越入顶部安全区：Y {controlTop:0.##}，安全区 {safe.Top:0.##}");
            // 入口组与大档卡片同宽并靠内侧末端对齐（右缘 = ContentTrailing）；右缘是最内侧栏缘，不是窗口侧。
            if ((double)root.FilterBar.Frames.Control.X < (double)safe.Left - Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局合体胶囊越入前导安全区：{root.FilterBar.Frames.Control}（安全区 L{safe.Left:0.##}）");
        }
        // 文件栏主轴长度 = 筛选控件主轴 + 胶片卡片带厚 + 分隔线（行布局）；
        // 列布局 = 左栏宽（FilmSideGeometry：安全区前导 + 卡片带 + 尾侧内边距）+ 分隔线（相似面板打开时才另加柱宽，本检查保持关闭）。
        var filmSide = row ? default(FilmSideMeasure) : root.FilmSide;
        double band = FilmCardGeometry.BandThickness(root.Filmstrip.Tier, row);
        double leadingInset = FilterLeadingInset(root, row);
        double filterMain = root.FilterBar.MainLength(row, leadingInset);
        double expectedFileMain = row
            ? filterMain + band + MainScreenLayout.Divider
            : filmSide.Width + MainScreenLayout.Divider;
        double actualFileMain = row
            ? (double)panes.FileBar.Height - (double)safe.Top + MainScreenLayout.Divider
            : (double)panes.FileBar.Width + MainScreenLayout.Divider;
        if (Math.Abs(actualFileMain - expectedFileMain) > .5)
            throw new InvalidOperationException(
                $"{scope} 文件栏主轴 {actualFileMain:0.##} ≠ 筛选 {filterMain:0.##} + 带厚 {band:0.##}（{root.Filmstrip.Tier}）+ 分隔线/左栏宽 {expectedFileMain:0.##}");
        if (!row)
        {
            // 左栏测量等于共享 FilmSideGeometry 的同参数结果：安全区前导、卡片带厚、两侧交叉轴内边距与 96 入口组。
            var measured = FilmSideGeometry.Measure((double)safe.Left, band,
                FilmCardGeometry.CrossLead(root.Filmstrip.Tier, false),
                FilmCardGeometry.CrossTrail(root.Filmstrip.Tier, false), MobileFilterLayout.GroupWidth);
            if (Math.Abs(filmSide.Width - measured.Width) > Exact
                || Math.Abs(filmSide.ContentTrailing - measured.ContentTrailing) > Exact
                || Math.Abs(filmSide.FilmstripX - measured.FilmstripX) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 左栏测量 {filmSide} ≠ 共享 FilmSideGeometry {measured}（safe L{safe.Left:0.##}，带厚 {band:0.##}）");
            if (Math.Abs((double)panes.FileContent.X - filmSide.FilmstripX) > Exact
                || Math.Abs((double)panes.FileContent.Width - band) > .5
                || Math.Abs((double)panes.FileContent.Y) > Exact
                || Math.Abs((double)panes.FileContent.Height - (double)stage.Height) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局文件栏内容未收在胶片条上：{panes.FileContent}（期望 X {filmSide.FilmstripX:0.##}、带厚 {band:0.##}、纵贯窗口）");
            // 筛选/更多合体组与列表共用同一条内侧末端：header 右缘 = 卡片外框右缘 = ContentTrailing（不是带内居中）。
            double expectedHeaderX = filmSide.ContentTrailing - MobileFilterLayout.GroupWidth;
            RequireFrame(root.FilmEntryBandFrame,
                new CGRect(expectedHeaderX, 0, MobileFilterLayout.GroupWidth, (double)stage.Height),
                $"{scope} 左栏入口带（header 实际 x + 整高）");
            if (Math.Abs((double)root.FilterBar.Frames.Control.MaxX - filmSide.ContentTrailing) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 筛选/更多合体组右缘 {(double)root.FilterBar.Frames.Control.MaxX:0.##} 未落内容末端 {filmSide.ContentTrailing:0.##}（期望 X {expectedHeaderX:0.##}）");
        }
        // 筛选条 + 胶片栏恰好铺满文件栏内容区；行布局的筛选控件另按自己的贴角带让开圆角/窗口控件，
        // 列布局的筛选 header 铺满整条左栏（入口组在其中靠内侧末端对齐）。
        if (row)
        {
            var bandProbe = root.ProbeInsets(
                new CGRect(0, (double)safe.Top, (double)stage.Width, DesignTokens.ShellGlassButtonSize),
                UIViewLayoutRegionAdaptivityAxis.Horizontal);
            double filterLeft = Math.Max((double)safe.Left, (double)bandProbe.Left);
            double filterWidth = Math.Max(1, (double)stage.Width - filterLeft - (double)safe.Right);
            RequireFrame(root.FilterBar.Frame,
                ToFrame(new LayoutRect(filterLeft, panes.FileContent.Y, filterWidth, filterMain)),
                $"{scope} 筛选条未落在文件栏内容区");
            double contentEnd = (double)panes.FileContent.MaxY;
            double filmEnd = (double)root.Filmstrip.Frame.Y + (double)root.Filmstrip.Frame.Height;
            if (Math.Abs(filmEnd - contentEnd) > .5)
                throw new InvalidOperationException($"{scope} 胶片栏未填满文件栏剩余空间：{filmEnd} vs {contentEnd}");
        }
        else
        {
            // 列布局 header 恒为 (0, 0, 左栏宽, filterMain)：它不再与列表同宽，也不再按内容区 x 落位。
            RequireFrame(root.FilterBar.Frame, ToFrame(new LayoutRect(0, 0, filmSide.Width, filterMain)),
                $"{scope} 列布局筛选 header 未铺满左栏");
            if (Math.Abs((double)root.Filmstrip.Frame.Width - band) > .5)
                throw new InvalidOperationException($"{scope} 列布局胶片栏交叉轴 {root.Filmstrip.Frame.Width:0.##} 不是带厚 {band:0.##}");
            // 列布局胶片条纵贯窗口、从筛选 header 下方滚过：顶端滚动边缘交互挂在筛选控件上，筛选条压在列表之上。
            RequireFrame(root.Filmstrip.Frame,
                ToFrame(new LayoutRect(filmSide.FilmstripX, 0, band, (double)stage.Height)),
                $"{scope} 列布局胶片栏未纵贯窗口");
            if (!root.FilterBar.Interactions.Contains(root.FilmstripTopEdgeInteraction))
                throw new InvalidOperationException($"{scope} 列布局未把胶片条的顶端滚动边缘交互挂在筛选控件上");
            if (StageIndexOf(root, root.FilterBar) < StageIndexOf(root, root.Filmstrip))
                throw new InvalidOperationException($"{scope} 列布局筛选控件未压在胶片列表之上");
            if (!root.Filmstrip.Hidden)
            {
                var firstCard = root.Filmstrip.FrameAt(0)
                    ?? throw new InvalidOperationException($"{scope} 列布局首卡几何缺失");
                // SetEdgePadding 已扣掉一次该档主轴前导：首卡恰好停在 header 下缘（filterMain），不再 filterMain + 5。
                if (Math.Abs((double)firstCard.Y - filterMain) > .5)
                    throw new InvalidOperationException(
                        $"{scope} 列布局首卡未停在 header 下缘：Y {firstCard.Y:0.##} ≠ filterMain {filterMain:0.##}");
                double cardRight = (double)root.Filmstrip.Frame.X + (double)firstCard.X + (double)firstCard.Width;
                if (Math.Abs(cardRight - filmSide.ContentTrailing) > .5)
                    throw new InvalidOperationException(
                        $"{scope} 列布局卡片外框右缘 {cardRight:0.##} 未与 header 右缘 {filmSide.ContentTrailing:0.##} 同线（左栏入口带 {root.FilmEntryBandFrame}）");
            }
        }
        // 适应区与任何可见栏位不相交（照片只在无遮挡区里适应）。
        var fit = root.Photo.FitRect;
        foreach (var (name, bar) in new (string, UIView)[]
                 {
                     ("文件栏背景", root.FileBarView), ("工具区背景", root.ToolBackgroundView),
                 })
        {
            if (bar.Hidden || (double)bar.Frame.Width <= 0 || (double)bar.Frame.Height <= 0) continue;
            if (Overlaps(fit, bar.Frame))
                throw new InvalidOperationException($"{scope} 适应区 {fit} 与可见{name} {bar.Frame} 相交");
        }
        lines.Add(
            $"PASS {scope} 分区：舞台 {Size(stage)} safe(T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##}) " +
            $"适应区 {Size(fit)} @({fit.X:0.##},{fit.Y:0.##})、文件栏主轴 {actualFileMain:0.##}（筛选 {filterMain:0.##} + 带厚 {band:0.##} + 分隔线/左栏宽）、" +
            $"主图分区与主图视图满舞台、栏位背景/分隔线与分区逐项一致、背景铺满边缘、" +
            $"{(row ? "内容在安全区内" : $"栏位内容纵贯窗口（顶部/底部安全区只推筛选按钮）、左栏 header/nav/卡片外框同落末端 {filmSide.ContentTrailing:0.##}")}、适应区不与可见栏位相交、空态提示/提示条/计算中药丸落在适应区内");
    }

    /// <summary>
    /// 核对适应区内边距等于分区矩形在舞台中的四边距离（浮层外壳把分区表达为主图适应区）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="photo">共享分区算出的主图矩形（舞台坐标）。</param>
    /// <param name="stage">舞台尺寸。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireFitInsets(iOSNativeRootViewController root, LayoutRect photo, CGRect stage, string context)
    {
        var insets = root.Photo.FitInsets;
        if (Math.Abs((double)insets.Left - photo.X) > Exact
            || Math.Abs((double)insets.Top - photo.Y) > Exact
            || Math.Abs((double)insets.Right - ((double)stage.Width - photo.MaxX)) > Exact
            || Math.Abs((double)insets.Bottom - ((double)stage.Height - photo.MaxY)) > Exact)
            throw new InvalidOperationException(
                $"{context}：适应区内边距 T{insets.Top:0.##} L{insets.Left:0.##} B{insets.Bottom:0.##} R{insets.Right:0.##} " +
                $"≠ 分区矩形 {photo}（舞台 {(double)stage.Width:0.##}×{(double)stage.Height:0.##}）");
    }

    /// <summary>
    /// 核对宽形态筛选控件（FILTER-07）：筛选 / 更多两段合体胶囊（2 × 48）逐项等于 <see cref="MobileFilterLayout"/> 的
    /// 纯几何结果；行布局整组（含 AI 版相似聚类开关）按自然宽在文件栏内容区内居中、开关隔 8 点紧随，
    /// 列布局 header 铺满整条左栏，合体胶囊与相似聚类开关都靠同一条内容末端（<see cref="FilmSideMeasure.ContentTrailing"/>）
    /// 右对齐、从前导让位（入口带 + 外边距）起排。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyFilterBar(iOSNativeRootViewController root, bool row, string scope, List<string> lines)
    {
        var bar = root.FilterBar;
        double contentWidth = (double)bar.Bounds.Width;
        double cardWidth = FilmCardGeometry.Width(root.Filmstrip.Tier);
        double leadingInset = FilterLeadingInset(root, row);
        var toggle = bar.SimilarityToggle;
        double toggleWidth = toggle == null ? 0 : row ? NaturalWidth(toggle) : cardWidth;
        double? contentTrailing = row ? null : root.FilmSide.ContentTrailing;
        var expected = MobileFilterLayout.Compute(row, contentWidth, leadingInset, toggleWidth, contentTrailing);

        if (bar.CombinedGroup.Buttons.Count != 2)
            throw new InvalidOperationException($"{scope} 筛选合体胶囊不是两个段：{bar.CombinedGroup.Buttons.Count}");
        if (!ReferenceEquals(bar.CombinedGroup.Buttons[0], bar.FilterButton)
            || !ReferenceEquals(bar.CombinedGroup.Buttons[1], bar.MoreButton))
            throw new InvalidOperationException($"{scope} 合体胶囊两段不是筛选 / 更多");
        if (bar.CombinedGroup.Vertical)
            throw new InvalidOperationException($"{scope} 合体胶囊竖排，筛选 / 更多应沿水平方向并排");
        if (bar.FilterButton.Hidden || bar.MoreButton.Hidden)
            throw new InvalidOperationException(
                $"{scope} 筛选 / 更多段可见性不符：筛选隐藏={bar.FilterButton.Hidden}，更多隐藏={bar.MoreButton.Hidden}");
        if (bar.FilterButton.MenuSnapshot == null)
            throw new InvalidOperationException($"{scope} 筛选段没有菜单");
        if (!bar.FilterButton.ShowsMenuAsPrimaryAction || !bar.MoreButton.ShowsMenuAsPrimaryAction)
            throw new InvalidOperationException($"{scope} 筛选 / 更多主操作接线不符");
        if (bar.MoreButton.Menu == null) throw new InvalidOperationException($"{scope} 更多段没有菜单");

        if (Math.Abs(bar.MainLength(row, leadingInset) - expected.MainLength) > Exact)
            throw new InvalidOperationException(
                $"{scope} 筛选控件主轴长度 {bar.MainLength(row, leadingInset):0.##} ≠ 纯几何 {expected.MainLength:0.##}");
        if (Math.Abs((double)bar.Frame.Height - expected.MainLength) > Exact)
            throw new InvalidOperationException($"{scope} 筛选控件高度 {bar.Frame.Height:0.##} ≠ 主轴长度 {expected.MainLength:0.##}");
        RequireFrame(ToFrame(bar.Frames.Control), ToFrame(expected.Control), $"{scope} 合体胶囊矩形与纯几何不一致");
        RequireFrame(ToFrame(bar.Frames.Filter), ToFrame(expected.Filter), $"{scope} 筛选段矩形与纯几何不一致");
        RequireFrame(ToFrame(bar.Frames.More), ToFrame(expected.More), $"{scope} 更多段矩形与纯几何不一致");
        RequireFrame(bar.CombinedGroup.ConvertRectToView(bar.CombinedGroup.Bounds, bar), ToFrame(expected.Control),
            $"{scope} 合体胶囊未铺满控件矩形");
        RequireFrame(bar.FilterButton.ConvertRectToView(bar.FilterButton.Bounds, bar.CombinedGroup), ToFrame(expected.Filter),
            $"{scope} 筛选段原生矩形与共享矩形不一致");
        RequireFrame(bar.MoreButton.ConvertRectToView(bar.MoreButton.Bounds, bar.CombinedGroup), ToFrame(expected.More),
            $"{scope} 更多段原生矩形与共享矩形不一致");
        if (Math.Abs((double)bar.CombinedGroup.Bounds.Height - MobileFilterLayout.Segment) > Exact)
            throw new InvalidOperationException(
                $"{scope} 合体胶囊高 {bar.CombinedGroup.Bounds.Height:0.##} 不是控件高 {MobileFilterLayout.Segment:0.##}");
        // 两段首尾相接：更多段紧贴筛选段右缘，两段 48×44，合体与大档卡片同宽。
        if (Math.Abs((double)expected.More.X - (double)expected.Filter.MaxX) > Exact
            || Math.Abs((double)expected.Filter.Width - MobileFilterLayout.SegmentWidth) > Exact
            || Math.Abs((double)expected.More.Width - MobileFilterLayout.SegmentWidth) > Exact
            || Math.Abs((double)expected.Control.Width - cardWidth) > Exact)
            throw new InvalidOperationException(
                $"{scope} 筛选 / 更多两段不是首尾相接的 48 点等宽段，或合体与卡片不同宽：{expected.Control} / {expected.Filter} / {expected.More}");
        if (toggle != null)
        {
            if (toggle.Hidden) throw new InvalidOperationException($"{scope} AI 版相似聚类开关必须显示");
            RequireFrame(ToFrame(bar.Frames.Toggle), ToFrame(expected.Toggle), $"{scope} 相似聚类开关矩形与纯几何不一致");
            RequireFrame(toggle.Frame, ToFrame(expected.Toggle), $"{scope} 相似聚类开关原生 frame 与共享矩形不一致");
            // 配置只在展开状态变化时重写，读屏值每次按真值刷新（重复刷新不碰按钮配置，避免重置系统玻璃态）。
            bool open = iOSNativeShell.Main?.FileVM.FilterBar.IsSimilarityPanelOpen ?? false;
            string expectedValue = open ? "已展开" : "已收起";
            if (toggle.AccessibilityValue != expectedValue)
                throw new InvalidOperationException(
                    $"{scope} 相似聚类开关读屏值 {toggle.AccessibilityValue ?? "null"} ≠ {expectedValue}");
            int updates = bar.ToggleConfigurationUpdates;
            bar.Refresh();
            bar.Refresh();
            if (bar.ToggleConfigurationUpdates != updates)
                throw new InvalidOperationException(
                    $"{scope} 展开状态未变时重复刷新仍重写相似聚类开关配置（{updates} → {bar.ToggleConfigurationUpdates}）");
            if (toggle.AccessibilityValue != expectedValue)
                throw new InvalidOperationException($"{scope} 重复刷新后相似聚类开关读屏值失准：{toggle.AccessibilityValue ?? "null"}");
        }
        else if (bar.Frames.Toggle.Width > 0 || bar.Frames.Toggle.Height > 0)
        {
            throw new InvalidOperationException($"{scope} 标准版不应为相似聚类开关保留矩形：{bar.Frames.Toggle}");
        }
        if (row)
        {
            if (Math.Abs((double)expected.Control.Y - MobileFilterLayout.Margin) > Exact
                || Math.Abs((double)expected.Control.Height - MobileFilterLayout.Segment) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 行布局合体胶囊未保持上下外边距与 44 点高：{expected.Control}");
            double groupWidth = (double)expected.Control.Width + (toggle == null ? 0 : MobileFilterLayout.Gap + toggleWidth);
            double groupCenter = (double)expected.Control.X + groupWidth / 2;
            if (Math.Abs(groupCenter - contentWidth / 2) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 筛选控件未在文件栏内容区整组居中：中心 {groupCenter:0.##} / {contentWidth / 2:0.##}");
            if (toggle != null
                && Math.Abs((double)expected.Toggle.X - (double)expected.Control.MaxX - MobileFilterLayout.Gap) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 相似聚类开关未隔 {MobileFilterLayout.Gap:0.##} 点紧随合体胶囊：{expected.Toggle}/{expected.Control}");
        }
        else
        {
            if (contentTrailing is not { } trailing)
                throw new InvalidOperationException($"{scope} 列布局缺少左栏内容末端");
            if (Math.Abs(contentWidth - root.FilmSide.Width) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局 header 宽 {contentWidth:0.##} 不是左栏宽 {root.FilmSide.Width:0.##}");
            if (Math.Abs((double)expected.Control.Width - MobileFilterLayout.GroupWidth) > Exact
                || Math.Abs((double)expected.Control.MaxX - trailing) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 合体胶囊未与内容末端右对齐：{expected.Control}（末端 {trailing:0.##}）");
            if (Math.Abs((double)expected.Control.Y - leadingInset) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 合体胶囊未从前导让位起排：Y={expected.Control.Y:0.##}/{leadingInset:0.##}");
            if (toggle != null
                && (Math.Abs((double)toggle.Frame.Width - cardWidth) > Exact
                    || Math.Abs((double)toggle.Frame.GetMaxX() - trailing) > Exact
                    || Math.Abs((double)toggle.Frame.Y - (double)expected.Control.MaxY - MobileFilterLayout.Gap) > Exact))
                throw new InvalidOperationException(
                    $"{scope} 相似聚类开关未与合体胶囊同落末端 {trailing:0.##}（下方隔 {MobileFilterLayout.Gap:0.##}、卡片列宽）：{toggle.Frame}");
            // 列主轴仍包含 header/toggle 下方的那份外边距：前导 + 合体 + （开关 + 间距）+ 外 10。
            double expectedMain = leadingInset + MobileFilterLayout.Segment
                + (toggle == null ? 0 : MobileFilterLayout.Gap + MobileFilterLayout.Segment) + MobileFilterLayout.Margin;
            if (Math.Abs((double)expected.MainLength - expectedMain) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 列布局主轴 {expected.MainLength:0.##} ≠ 前导 {leadingInset:0.##} + 合体 {MobileFilterLayout.Segment:0.##}"
                    + $"+ {(toggle == null ? "无开关" : $"开关 {MobileFilterLayout.Gap:0.##}+{MobileFilterLayout.Segment:0.##}")} + 外 {MobileFilterLayout.Margin:0.##}");
        }
        foreach (var view in toggle == null ? new UIView[] { bar.CombinedGroup } : new UIView[] { bar.CombinedGroup, toggle })
        {
            var bounds = view.Bounds;
            var center = view.Center;
            var transform = view.Transform;
            var pressed = CGAffineTransform.MakeScale((nfloat)1.08, (nfloat)1.08);
            try
            {
                view.Transform = pressed;
                bar.Arrange(row, contentWidth, cardWidth, leadingInset, contentTrailing);
                bar.Arrange(row, contentWidth, cardWidth, leadingInset, contentTrailing);
                if (view.Bounds != bounds || view.Center != center)
                    throw new InvalidOperationException($"{scope} 筛选玻璃重排改变了未变换几何或按压变换");
            }
            finally
            {
                view.Transform = transform;
                view.Bounds = bounds;
                view.Center = center;
            }
        }
        lines.Add($"PASS {scope} 筛选玻璃：重复重排保留 bounds/center 与按压变换");
        lines.Add(row
            ? $"PASS {scope} 筛选控件：合体胶囊 96 点（筛选 48 + 更多 48，与大档卡片同宽）在 {contentWidth:0.##} 内容区整组居中，" +
                $"{(toggle == null ? "标准版无相似聚类开关" : $"AI 版开关 {toggleWidth:0.##} 点隔 {MobileFilterLayout.Gap:0.##} 紧随其后")}；行高 {expected.MainLength:0.##}（无计数、无空槽）"
            : $"PASS {scope} 筛选控件：header 铺满左栏 {contentWidth:0.##} 点，合体胶囊 96 点（与大档卡片同宽）与" +
                $"{(toggle == null ? "（标准版无相似聚类开关）" : $"AI 版开关 {toggleWidth:0.##} 点")}同落内容末端 {(contentTrailing ?? 0):0.##}" +
                $"（右对齐、不是带内居中；Y={expected.Control.Y:0.##} = 入口带 + 外边距）；列主轴 {expected.MainLength:0.##}");
    }

    /// <summary>
    /// 筛选控件列布局的前导让位：实际入口带（header 的 x 范围 + 整高）在该带上的系统上缘适应 + 外边距；
    /// 行布局只看外边距。与根控制器探针口径一致，按钮因此只按自身带被推下，不推列表与右栏；
    /// 入口带右缘即内容末端，列表交叉轴宽度不再参与按钮的角适应。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="row">是否行布局。</param>
    /// <returns>前导让位（点）。</returns>
    private static double FilterLeadingInset(iOSNativeRootViewController root, bool row)
    {
        if (row) return MainScreenLayout.FilterMargin;
        var probe = root.ProbeInsets(root.FilmEntryBandFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
        return (double)probe.Top + FilmSideGeometry.Padding;
    }

    /// <summary>
    /// 横屏贴角带的圆角避让（SHELL-01／SHELL-09／SHELL-10）：下缘取该带位置上<b>原生边距区域的圆角适应</b>
    /// （iOS 26 的 <c>marginsLayoutRegionWithCornerAdaptation:</c>，探针边距归零、不从安全区补边距），
    /// 与 Home 指示条安全区无关——原生读数不论比手势条余量大还是小都原样进入几何，不减基线、不按阈值猜测；
    /// 上缘取安全区探测与圆角适应的较大者。规则同时核对真实探针读数、角落/中段的 API 行为与合成读数。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    internal static void VerifyLandscapeCornerRegions(iOSNativeRootViewController root, string scope, List<string> lines)
    {
        var input = root.PhoneSlotsInput;
        var stage = root.Stage.Bounds;
        if ((double)input.Safe.Bottom > Exact)
            throw new InvalidOperationException($"{scope} 横屏仍为底部手势条保留安全区：B{input.Safe.Bottom:0.##}");

        void VerifyBand(string name, CGRect frame, EdgeInsets band)
        {
            var safe = root.ProbeInsets(frame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            var corner = root.ProbeCornerInsets(frame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            var (top, bottom) = iOSNativeRootViewController.LandscapeBandEdges(safe, corner);
            if (Math.Abs(band.Top - top) > Exact || Math.Abs(band.Bottom - bottom) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 横屏{name}贴角带未原样采用原生读数：带 T{band.Top:0.##} B{band.Bottom:0.##}，"
                    + $"规则 T{top:0.##} B{bottom:0.##}"
                    + $"（安全区探针 T{safe.Top:0.##} B{safe.Bottom:0.##}，圆角探针 T{corner.Top:0.##} B{corner.Bottom:0.##}）");
        }

        // 圆角/控件适应只按真实入口带（合体实际 x 范围 + 整高）探测：列表交叉轴宽度不再参与按钮的角适应。
        // 手机横屏入口是筛选/更多两枚 44 点圆按钮（共 88），与宽形态 iPad 的 96 点筛选/更多合体不是同一宽度。
        VerifyBand("刘海侧入口带", root.FilmEntryBandFrame, input.LeadBand);
        double phoneGroupWidth = DesignTokens.ShellGlassButtonSize * 2;
        if (Math.Abs((double)root.FilmEntryBandFrame.Width - phoneGroupWidth) > Exact
            || Math.Abs((double)root.FilmEntryBandFrame.Y) > Exact
            || Math.Abs((double)root.FilmEntryBandFrame.Height - (double)stage.Height) > Exact)
            throw new InvalidOperationException(
                $"{scope} 横屏入口带不是合体实际 x 范围 + 整高：{root.FilmEntryBandFrame}（合体宽 {phoneGroupWidth:0.##}）");
        var controlProbe = root.ProbeInsets(root.ControlBandFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
        var controlCorner = root.ProbeCornerInsets(root.ControlBandFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
        var (controlTop, controlBottom) = iOSNativeRootViewController.LandscapeBandEdges(controlProbe, controlCorner);
        if (Math.Abs(input.ControlBand.Top - controlTop) > Exact || Math.Abs(input.ControlBand.Bottom - controlBottom) > Exact)
            throw new InvalidOperationException($"{scope} 控制内容列未采用自身位置的圆角读数");

        // 圆角探针只能带圆角/窗口控件适应：屏幕中段的整列探针不应有上下适应，否则边距区域混入了安全区。
        double middleStrip = Math.Min(60, (double)stage.Width);
        var middleFrame = new CGRect(((double)stage.Width - middleStrip) / 2, 0, middleStrip, (double)stage.Height);
        var middle = root.ProbeCornerInsets(middleFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
        if ((double)middle.Top > Exact || (double)middle.Bottom > Exact)
        {
            var middleSafe = root.ProbeInsets(middleFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            throw new InvalidOperationException(
                $"{scope} 圆角探针在屏幕中段仍读出上下适应 {middle.Top:0.##}/{middle.Bottom:0.##}"
                + $"（同位置安全区探针 {middleSafe.Top:0.##}/{middleSafe.Bottom:0.##}）："
                + "原生边距区域混入了安全区或系统最小边距，不能作为独立圆角来源");
        }

        // 角落探针必须读出圆角适应：这是「原生边距区域确实提供独立圆角来源」的实测依据，
        // 读不出时横屏贴角带只能退回相减/猜 Home，属于必须交回的阻塞。
        double patchWidth = Math.Min(60, (double)stage.Width);
        double patchHeight = Math.Min(60, (double)stage.Height);
        var patchFrame = new CGRect(root.NotchOnLeft ? (double)stage.Width - patchWidth : 0,
            (double)stage.Height - patchHeight, patchWidth, patchHeight);
        var patch = root.ProbeCornerInsets(patchFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
        if (Math.Max((double)patch.Top, (double)patch.Bottom) <= Exact)
        {
            var patchSafe = root.ProbeInsets(patchFrame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            throw new InvalidOperationException(
                $"{scope} 屏幕角落的圆角探针读出 {patch.Top:0.##}/{patch.Bottom:0.##}"
                + $"（同位置安全区探针 {patchSafe.Top:0.##}/{patchSafe.Bottom:0.##}）："
                + "原生边距区域没有提供独立圆角适应，横屏贴角带无法在不猜 Home 的前提下保留圆角");
        }

        // 合成读数：底缘圆角读数不论大小（含小于 Home 手势条余量、甚至为 0 的值）都原样保留；上缘取两者较大者。
        foreach (double bottom in new[] { 0d, 6d, 26d, 34d })
        {
            var (top, kept) = iOSNativeRootViewController.LandscapeBandEdges(
                new UIEdgeInsets(62, 24, 21, 24), new UIEdgeInsets(10, 0, (nfloat)bottom, 0));
            if (Math.Abs(top - 62) > Exact || Math.Abs(kept - bottom) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 合成圆角读数 {bottom:0.##} 未被原样保留：T{top:0.##} B{kept:0.##}");
        }
        var raised = iOSNativeRootViewController.LandscapeBandEdges(
            new UIEdgeInsets(0, 0, 0, 0), new UIEdgeInsets(26, 0, 0, 0));
        if (Math.Abs(raised.Top - 26) > Exact)
            throw new InvalidOperationException($"{scope} 圆角适应大于安全区时上缘未取较大者：{raised.Top:0.##}");

        // 槽位层面：同一窗口下加大控制列底缘圆角，可用区间收紧、整组随之上移，末端仍在圆角之内。
        var exif = ExifBlockGeometry.Size(4, horizontal: false, bare: true);
        var layoutInput = new PhoneLayoutInput(
            Landscape: true, NotchOnLeft: true, Width: (double)stage.Width, Height: (double)stage.Height,
            Safe: new EdgeInsets(24, 0, 24, 0),
            LeadBand: new EdgeInsets(24, 18, 0, 0),
            ControlBand: new EdgeInsets(0, 18, 0, 0),
            FilmVisible: true, ControlVisible: true, ExifVisible: true, AnalysisVisible: true, ShowNavigation: true,
            FilterWidth: DesignTokens.ShellGlassButtonSize,
            FilmBand: FilmCardGeometry.BandThickness(FilmCardTier.Small, false),
            FilmOuterPad: FilmCardGeometry.CrossLead(FilmCardTier.Small, false),
            AnalysisCross: 67, ExifCount: 4, ExifWidth: exif.Width, ExifHeight: exif.Height,
            ExifItemSpacing: ExifBlockGeometry.ItemSpacing, RatingMinLength: RatingControlGeometry.CompactMinLength,
            ButtonSize: DesignTokens.ShellGlassButtonSize, EdgeMargin: DesignTokens.ShellPhoneEdgeMargin,
            RowInset: DesignTokens.ShellPhoneRowInset, GroupGap: DesignTokens.ShellGlassGroupGap,
            CommandCount: 2, CommandButtonSize: DesignTokens.ShellPhoneCommandSize);
        var flat = PhoneScreenLayout.Compute(layoutInput with
        {
            LeadBand = layoutInput.LeadBand with { Bottom = 0 },
            ControlBand = layoutInput.ControlBand with { Bottom = 0 },
        });
        foreach (double corner in new[] { 6d, 26d, 34d })
        {
            var slots = PhoneScreenLayout.Compute(layoutInput with
            {
                LeadBand = layoutInput.LeadBand with { Bottom = corner },
                ControlBand = layoutInput.ControlBand with { Bottom = corner },
            });
            double columnTop = Math.Max(layoutInput.Safe.Top, layoutInput.ControlBand.Top) + ToolRegionGeometry.Padding;
            double columnBottom = (double)stage.Height
                - Math.Max(layoutInput.Safe.Bottom, layoutInput.ControlBand.Bottom) - ToolRegionGeometry.Padding;
            double groupTop = slots.Exif.Width > 0 ? slots.Exif.Y : slots.Rating.Y;
            if (groupTop < columnTop - Exact || slots.Commands.MaxY > columnBottom + Exact)
                throw new InvalidOperationException(
                    $"{scope} 圆角读数 {corner:0.##} 未进入控制列区间：组顶 {groupTop:0.##}，"
                    + $"命令末端 {slots.Commands.MaxY:0.##}，可用 {columnTop:0.##}..{columnBottom:0.##}");
            if (corner > ToolRegionGeometry.Padding + Exact
                && !(slots.Commands.MaxY < flat.Commands.MaxY - Exact))
                throw new InvalidOperationException(
                    $"{scope} 圆角读数 {corner:0.##} 未收紧控制列：命令末端 {slots.Commands.MaxY:0.##}"
                    + $" / 零圆角 {flat.Commands.MaxY:0.##}");
        }
        lines.Add($"{scope} 横屏圆角：两条贴角带下缘原样采用原生边距区域读数"
            + $"（刘海 {input.LeadBand.Bottom:0.##}、控制 {input.ControlBand.Bottom:0.##}），上缘取安全区与圆角的较大者，"
            + "合成圆角 0/6/26/34 全部原样保留并通过槽位收紧");
    }

    /// <summary>
    /// 人工注入安全区的纯几何核对：同一份窗口控件安全区（顶 24 / 底 20）在左右布局只推动筛选按钮
    /// （按钮自身贴角带 + 外边距），文件栏、控制栏与分析栏内容仍纵贯窗口；上下布局仍把内容收在安全区内。
    /// 探针值与真实窗口无关，因此这条断言不依赖设备是否有刘海/圆角。
    /// </summary>
    /// <param name="width">窗口宽。</param>
    /// <param name="height">窗口高。</param>
    /// <param name="scope">报告范围标签。</param>
    private static void VerifyColumnSafeInjection(double width, double height, string scope)
    {
        var injected = new UIEdgeInsets(24, 12, 20, 8);
        const double fileMain = MainScreenLayout.FileBarContentWidth + MainScreenLayout.Divider + 12;
        const double controlMain = 84;
        const double analysisCross = 64;
        var column = ShellPaneLayout.Partition(false, width, height, Edges(injected), fileMain, controlMain, analysisCross);
        if (Math.Abs((double)column.FileContent.Y) > Exact || Math.Abs((double)column.FileContent.Height - height) > Exact)
            throw new InvalidOperationException(
                $"{scope} 注入安全区 T24 B20 后列布局文件栏内容未纵贯窗口：{column.FileContent}（舞台高 {height:0.##}）");
        if (Math.Abs((double)column.ControlContent.Y) > Exact || Math.Abs((double)column.ControlContent.Height - height) > Exact)
            throw new InvalidOperationException(
                $"{scope} 注入安全区 T24 B20 后列布局控制栏内容未纵贯窗口：{column.ControlContent}（舞台高 {height:0.##}）");
        if (Math.Abs((double)column.Analysis.Y) > Exact || Math.Abs((double)column.Analysis.Height - height) > Exact
            || Math.Abs((double)column.Analysis.Width - analysisCross) > Exact)
            throw new InvalidOperationException(
                $"{scope} 注入安全区后列布局分析栏未纵贯窗口：{column.Analysis}（高度应为 {height:0.##}）");
        // 分析栏与控制栏同时有时消除内部 1 点分隔线：控制主轴从输入扣 1，控制内容补回原先分隔线区域，
        // 背景取分析 + 控制的连续并集；控制栏单独时仍保留朝主图的一条外分隔线。
        double measuredControl = controlMain - 1;
        double photoWidth = width - fileMain - measuredControl - analysisCross;
        if (Math.Abs((double)column.Photo.Width - photoWidth) > .5)
            throw new InvalidOperationException(
                $"{scope} 注入安全区后列布局主图宽度不只扣左右栏：{column.Photo.Width:0.##} ≠ {photoWidth:0.##}");
        if (Math.Abs((double)column.Analysis.MaxX - (width - measuredControl)) > Exact
            || Math.Abs((double)column.ControlContent.X - (width - measuredControl)) > Exact
            || Math.Abs((double)column.ControlContent.Width - measuredControl) > Exact)
            throw new InvalidOperationException(
                $"{scope} 工具区接缝未消除内部 1 点：分析末端 {column.Analysis.MaxX:0.##}，控制内容 {column.ControlContent}");
        if (column.ControlDivider.Width > Exact || column.ControlDivider.Height > Exact)
            throw new InvalidOperationException($"{scope} 分析栏与控制栏相邻时仍保留内部隔线：{column.ControlDivider}");
        var union = new LayoutRect(column.Analysis.X, 0, width - column.Analysis.X, height);
        if (column.ControlBackground != union)
            throw new InvalidOperationException(
                $"{scope} 列布局工具区背景 {column.ControlBackground} ≠ 分析/控制并集 {union}");
        var controlOnlyColumn = ShellPaneLayout.Partition(false, width, height, Edges(injected), fileMain, controlMain, 0);
        if (Math.Abs(controlOnlyColumn.ControlDivider.Width - MainScreenLayout.Divider) > Exact
            || Math.Abs(controlOnlyColumn.ControlBackground.X - (width - controlMain)) > Exact
            || Math.Abs(controlOnlyColumn.ControlContent.X - (width - controlMain + MainScreenLayout.Divider)) > Exact)
            throw new InvalidOperationException(
                $"{scope} 控制栏单独时外分隔线/背景口径改变：{controlOnlyColumn.ControlDivider} / {controlOnlyColumn.ControlBackground}");
        // 同一份安全区在上下布局仍把内容收在安全区内（顶 24 / 底 20）。
        var row = ShellPaneLayout.Partition(true, width, height, Edges(injected), fileMain, controlMain, 0);
        if (Math.Abs((double)row.FileContent.Y - 24) > Exact
            || Math.Abs((double)row.ControlContent.MaxY - (height - 20)) > Exact)
            throw new InvalidOperationException(
                $"{scope} 注入安全区后上下布局未按安全区收内容：文件 {row.FileContent}，控制 {row.ControlContent}");
        var rowBoth = ShellPaneLayout.Partition(true, width, height, Edges(injected), fileMain, controlMain, analysisCross);
        var rowUnion = new LayoutRect(0, rowBoth.Analysis.Y, width, height - rowBoth.Analysis.Y);
        if (rowBoth.ControlDivider.Width > Exact || rowBoth.ControlDivider.Height > Exact
            || rowBoth.ControlBackground != rowUnion
            || Math.Abs((double)rowBoth.ControlContent.Y - rowBoth.Analysis.MaxY) > Exact
            || Math.Abs((double)rowBoth.ControlContent.MaxY - (height - 20)) > Exact)
            throw new InvalidOperationException(
                $"{scope} 上下布局工具区未合并：分析 {rowBoth.Analysis}、控制内容 {rowBoth.ControlContent}、背景 {rowBoth.ControlBackground}");
        // 左栏几何取共享 FilmSideGeometry：安全区前导 + 卡片带 + 尾侧内边距，入口带靠共同内容末端右对齐。
        // 大档 safe 0 → 宽 116 / 末端 106 / header X 10 / 卡片 X 10，safe 62 → 168 / 158 / 62 / 62；
        // 小档 safe 0 → 106 / 98 / 10 / 34，safe 62 → 134 / 126 / 38 / 62（带厚 80、两侧内边距 8、手机入口 88）。
        VerifyFilmSide("大档无刘海", 0, FilmCardTier.Large, 116, 106, 10, 10, scope);
        VerifyFilmSide("大档刘海 62", 62, FilmCardTier.Large, 168, 158, 62, 62, scope);
        VerifyFilmSide("小档无刘海", 0, FilmCardTier.Small, 106, 98, 10, 34, scope);
        VerifyFilmSide("小档刘海 62", 62, FilmCardTier.Small, 134, 126, 38, 62, scope);
        // 合体胶囊与相似聚类开关只随共同末端移动：不传末端时按 header 宽 − 外边距；显式传末端时同落该线。
        const double contentWidth = 168;
        const double trailing = 158;
        var shallow = MobileFilterLayout.Compute(false, contentWidth, 10, 0, trailing);
        var deep = MobileFilterLayout.Compute(false, contentWidth, 34, 0, trailing);
        var implicitTrailing = MobileFilterLayout.Compute(false, MainScreenLayout.FileBarContentWidth, 10, 0);
        var withToggle = MobileFilterLayout.Compute(false, contentWidth, 10, FilmCardGeometry.Width(FilmCardTier.Small), trailing);
        if (Math.Abs((double)shallow.Control.Y - 10) > Exact || Math.Abs((double)deep.Control.Y - 34) > Exact
            || Math.Abs((double)shallow.Control.MaxX - trailing) > Exact || Math.Abs((double)deep.Control.MaxX - trailing) > Exact
            || Math.Abs((double)implicitTrailing.Control.MaxX - (MainScreenLayout.FileBarContentWidth - MainScreenLayout.FilterMargin)) > Exact
            || Math.Abs((double)withToggle.Toggle.MaxX - trailing) > Exact)
            throw new InvalidOperationException(
                $"{scope} 列布局合体胶囊未只随前导让位/共同末端移动：{shallow.Control} / {deep.Control} / 缺省末端 {implicitTrailing.Control} / 开关 {withToggle.Toggle}");
    }

    /// <summary>
    /// 左栏测量表（<see cref="FilmSideGeometry.Measure"/>）：栏宽、内容末端、入口带 X（末端 − 入口宽）与卡片外框 X
    /// （胶片条 X + 该档交叉轴内边距）。卡片外框右缘恒等于内容末端，header、上下张与卡片因此同线。
    /// </summary>
    /// <param name="name">档位/安全区名称。</param>
    /// <param name="leadingSafe">刘海侧安全区。</param>
    /// <param name="tier">卡片档位。</param>
    /// <param name="width">期望栏宽。</param>
    /// <param name="trailing">期望内容末端。</param>
    /// <param name="headerX">期望入口带 X。</param>
    /// <param name="cardX">期望卡片外框 X。</param>
    /// <param name="scope">报告范围标签。</param>
    private static void VerifyFilmSide(string name, double leadingSafe, FilmCardTier tier, double width, double trailing,
        double headerX, double cardX, string scope)
    {
        double band = FilmCardGeometry.BandThickness(tier, false);
        double cross = FilmCardGeometry.CrossLead(tier, false);
        double entryWidth = tier == FilmCardTier.Large
            ? MobileFilterLayout.GroupWidth
            : DesignTokens.ShellGlassButtonSize * 2;
        var measure = FilmSideGeometry.Measure(leadingSafe, band, cross, cross, entryWidth);
        double actualHeaderX = measure.ContentTrailing - entryWidth;
        double actualCardX = measure.FilmstripX + cross;
        if (Math.Abs(measure.Width - width) > Exact || Math.Abs(measure.ContentTrailing - trailing) > Exact
            || Math.Abs(actualHeaderX - headerX) > Exact || Math.Abs(actualCardX - cardX) > Exact
            || Math.Abs(actualCardX + FilmCardGeometry.Width(tier) - measure.ContentTrailing) > Exact)
            throw new InvalidOperationException(
                $"{scope} 左栏测量 {name} 宽 {measure.Width:0.##}/{width:0.##}、末端 {measure.ContentTrailing:0.##}/{trailing:0.##}、"
                + $"header X {actualHeaderX:0.##}/{headerX:0.##}、卡片 X {actualCardX:0.##}/{cardX:0.##}"
                + "（卡片外框右缘必须等于内容末端）");
    }

    /// <summary>工具区共享纯数学（SHELL-12）：内边距 10、接缝 8、组间距 10，独立控制 64 / 相邻 54，分析框为瓦片 + 18 / + 20。</summary>
    /// <param name="scope">报告范围标签。</param>
    private static void VerifyToolRegionMath(string scope)
    {
        if (Math.Abs(ToolRegionGeometry.Padding - 10) > Exact || Math.Abs(ToolRegionGeometry.Spacing - 8) > Exact
            || Math.Abs(ToolRegionGeometry.GroupSpacing - 10) > Exact)
            throw new InvalidOperationException(
                $"{scope} 工具区内边距/接缝/组间距 {ToolRegionGeometry.Padding}/{ToolRegionGeometry.Spacing}/{ToolRegionGeometry.GroupSpacing} 不是 10/8/10");
        double independent = ToolRegionGeometry.ControlWidth(44, false), joined = ToolRegionGeometry.ControlWidth(44, true);
        if (Math.Abs(independent - 64) > Exact || Math.Abs(joined - 54) > Exact)
            throw new InvalidOperationException($"{scope} 控制区宽 {independent:0.##}/{joined:0.##} 不是 64/54");
        if (Math.Abs(ToolRegionGeometry.AnalysisWidth(100, false) - 120) > Exact
            || Math.Abs(ToolRegionGeometry.AnalysisWidth(100, true) - 118) > Exact
            || Math.Abs(ToolRegionGeometry.ControlLeadingInset(true)) > Exact
            || Math.Abs(ToolRegionGeometry.ControlLeadingInset(false) - 10) > Exact
            || Math.Abs(ToolRegionGeometry.CenteredStart(0, 100, 40) - 30) > Exact
            || Math.Abs(ToolRegionGeometry.CenteredStart(0, 20, 40)) > Exact)
            throw new InvalidOperationException(
                $"{scope} 分析框 {ToolRegionGeometry.AnalysisWidth(100, true)}/{ToolRegionGeometry.AnalysisWidth(100, false)} 或控制前导内边距不随相邻关系变化");

    }

    /// <summary>「显示评分」关闭：控制栏移除星级组、不保留空槽，其余分组仍按同一份流式几何排布（TOOLBAR-03）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyRatingHiddenAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var bar = root.ControlBar;
        await UiDispatcher.InvokeAsync(() => main.Settings.ShowRating = false);
        try
        {
            await WaitAsync(() => !root.ControlBar.HasRatingGroup && root.ControlBar.RatingControl.Hidden, "显示评分关闭落到控制栏");
            await SettleAsync(root);
            if (bar.HasRatingGroup || !bar.RatingControl.Hidden || bar.Groups.Contains(bar.RatingControl))
                throw new InvalidOperationException("显示评分关闭后控制栏仍保留星级组或空槽");
            VerifyControlBar(root, main, root.Filmstrip.Horizontal, "宽形态/无评分", lines);
        }
        finally
        {
            await UiDispatcher.InvokeAsync(() => main.Settings.ShowRating = true);
            await WaitAsync(() => bar.HasRatingGroup, "显示评分恢复");
            await SettleAsync(root);
        }
        if (!bar.HasRatingGroup) throw new InvalidOperationException("显示评分恢复后控制栏缺少星级组");
    }

    /// <summary>原生量得的自然宽度（与控件同一口径：SizeThatFits 向上取整，含 configuration 内边距与弹出指示器）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <returns>宽度，至少 1 点。</returns>
    private static double NaturalWidth(UIButton button)
    {
        double width = Math.Ceiling((double)button.SizeThatFits(new CGSize(0, (nfloat)DesignTokens.ShellGlassButtonSize)).Width);
        if (width <= 0) width = Math.Ceiling((double)button.IntrinsicContentSize.Width);
        return Math.Max(1, width);
    }

    /// <summary>
    /// 核对控制栏：分组顺序固定为 EXIF → 星级 → 命令、分组尺寸取共享 <see cref="ToolbarFlowLayout"/>、
    /// 分组外边距与不重叠；星级控件在组内铺满并按共享几何落在展开/紧凑形态，命令圆按钮 44×44 按共享偏移排布。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM（分组组成以当前设置为准，硬编码数量会把设置回写误判为回归）。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyControlBar(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var bar = root.ControlBar;
        var bounds = bar.Bounds;
        // 本套件分析栏保持隐藏：控制栏独立，交叉轴前导内边距仍是一份工具区内边距（10）。
        if (bar.JoinedToAnalysis)
            throw new InvalidOperationException($"{scope} 分析栏隐藏时控制栏仍按相邻工具区排布（前导内边距被清零）");
        // 左右布局：控制栏按自身列位置读取原生上下可用区，不沿用整窗安全区（尤其不强制扣底边手势条）。
        if (!row)
        {
            var stage = root.Stage.Bounds;
            double leading = ToolRegionGeometry.ControlLeadingInset(bar.JoinedToAnalysis);
            var frame = new CGRect((double)root.ControlScroll.Frame.X + leading, 0,
                (double)root.ControlScroll.Frame.Width - leading - ToolRegionGeometry.Padding, stage.Height);
            var probe = root.ProbeInsets(frame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            var corners = root.ProbeCornerInsets(frame, UIViewLayoutRegionAdaptivityAxis.Vertical);
            double top = Math.Max((double)probe.Top, (double)corners.Top);
            double bottom = stage.Width > stage.Height ? (double)corners.Bottom : Math.Max((double)probe.Bottom, (double)corners.Bottom);
            if (Math.Abs((double)bar.MainInsets.Top - top) > .5
                || Math.Abs((double)bar.MainInsets.Bottom - bottom) > .5)
                throw new InvalidOperationException(
                    $"{scope} 控制栏主轴安全区 T{bar.MainInsets.Top:0.##} B{bar.MainInsets.Bottom:0.##} ≠ 自身列探针 T{probe.Top:0.##} B{probe.Bottom:0.##}");
        }
        int exifCount = main.ControlVM.EnabledExifItems.Count;
        int commandCount = main.ControlVM.EnabledControls.Count(hotkey =>
            iOSToolbarView.IsCommandAvailable(hotkey.Command) && AppleSymbolNames.ForCommand(hotkey.Command) != null);
        int expectedGroups = (exifCount > 0 ? 1 : 0) + (bar.HasRatingGroup ? 1 : 0) + (commandCount > 0 ? 1 : 0);
        if (bar.Groups.Count != expectedGroups || bar.HasRatingGroup != main.Settings.ShowRating)
            throw new InvalidOperationException(
                $"{scope} 控制栏分组与共享设置不一致：组 {bar.Groups.Count}/{expectedGroups}、星级分组 {bar.HasRatingGroup}/{main.Settings.ShowRating}（EXIF {exifCount}、命令 {commandCount}）");
        // 固定顺序 EXIF → 星级 → 命令；星级组就是常驻控件本身。
        int groupIndex = 0;
        if (exifCount > 0)
        {
            if (!ReferenceEquals(bar.Groups[groupIndex], bar.ExifGroup))
                throw new InvalidOperationException($"{scope} 控制栏第 {groupIndex + 1} 组不是 EXIF 信息块");
            groupIndex++;
        }
        if (bar.HasRatingGroup)
        {
            if (!ReferenceEquals(bar.Groups[groupIndex], bar.RatingControl))
                throw new InvalidOperationException($"{scope} 控制栏第 {groupIndex + 1} 组不是星级控件");
            groupIndex++;
        }
        int commandIndex = groupIndex;
        // 命令组是玻璃容器里不接收触摸的逻辑矩形（命令按钮与星级控件平级挂在内容区），本身没有子视图。
        if (commandCount > 0)
        {
            if (commandIndex >= bar.Groups.Count || bar.Commands.Count != commandCount)
                throw new InvalidOperationException(
                    $"{scope} 命令分组缺失或命令数量不符：组 {commandIndex}/{bar.Groups.Count}、命令 {bar.Commands.Count}/{commandCount}");
            if (bar.Groups[commandIndex].Subviews.Length != 0 || bar.Groups[commandIndex].UserInteractionEnabled)
                throw new InvalidOperationException($"{scope} 命令分组不是无交互空逻辑矩形：子视图 {bar.Groups[commandIndex].Subviews.Length}");
        }
        // 分组沿主轴可用长度先扣除控制栏按自身列位置读取的主轴安全区（宽形态左右布局的上下可用区）。
        double mainLeading = row ? (double)bar.MainInsets.Left : (double)bar.MainInsets.Top;
        double mainTrailing = row ? (double)bar.MainInsets.Right : (double)bar.MainInsets.Bottom;
        double availableMain = row ? (double)root.ControlScroll.Bounds.Width : (double)root.ControlScroll.Bounds.Height;
        foreach (var group in bar.Groups)
        {
            var frame = group.Frame;
            double mainStart = row ? (double)frame.X : (double)frame.Y;
            double mainEnd = row ? (double)frame.GetMaxX() : (double)frame.GetMaxY();
            double mainLimit = row ? (double)bounds.Width : (double)bounds.Height;
            if (mainStart < mainLeading + ToolbarFlowLayout.Padding - Exact
                || mainEnd > mainLimit - mainTrailing - ToolbarFlowLayout.Padding + Exact
                || (double)frame.X < ToolbarFlowLayout.Padding - Exact || (double)frame.Y < ToolbarFlowLayout.Padding - Exact
                || (double)frame.X + (double)frame.Width > (double)bounds.Width - ToolbarFlowLayout.Padding + Exact
                || (double)frame.Y + (double)frame.Height > (double)bounds.Height - ToolbarFlowLayout.Padding + Exact)
                throw new InvalidOperationException(
                    $"{scope} 控制组未保留 10 点外边距/主轴安全区（T{mainLeading:0.##} B{mainTrailing:0.##}）：{frame}/{bounds}");
            foreach (var other in bar.Groups.Where(other => !ReferenceEquals(other, group)))
                if (Overlaps(frame, other.Frame)) throw new InvalidOperationException($"{scope} 控制组重叠：{frame} / {other.Frame}");
        }
        string exifSummary = "无 EXIF 分组";
        if (exifCount > 0)
        {
            // EXIF 组就是新 EXIF 块本身：上下布局是有衬底的整块，左右布局取无衬底变体（SHELL-12、TOOLBAR-05）；
            // 整块尺寸与逐项矩形都取共享几何，块内各项落在项目矩形内、标签在数值上方。
            var exif = bar.ExifGroup!;
            exif.LayoutIfNeeded();
            bool bare = !row;
            var (specWidth, specHeight) = ExifBlockGeometry.Size(exifCount, row, bare);
            double specMain = row ? specWidth : specHeight;
            // 无衬底纵排使用固有项宽（52，且不超过按钮与两侧边距之和 64），与同列星级/按钮同轴。
            double specCross = row ? specHeight : specWidth;
            double frameMain = row ? (double)exif.Frame.Width : (double)exif.Frame.Height;
            double frameCross = row ? (double)exif.Frame.Height : (double)exif.Frame.Width;
            if (Math.Abs(frameMain - specMain) > Exact || Math.Abs(frameCross - specCross) > Exact)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块 {frameMain:0.##}×{frameCross:0.##} 与共享{(bare ? "无衬底" : "衬底")}尺寸 {specMain:0.##}×{specCross:0.##} 不一致");
            if (exif.Horizontal != row)
                throw new InvalidOperationException($"{scope} EXIF 块排向 {exif.Horizontal} 未随布局方向 {row}");
            if (exif.Bare != bare)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块衬底模式 {exif.Bare} 与{(row ? "上下布局（带衬底）" : "左右布局（无衬底）")}不符");
            if (exif.ItemLabels.Count != exifCount || exif.ItemValues.Count != exifCount)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块项数 {exif.ItemLabels.Count}/{exif.ItemValues.Count} ≠ 启用项 {exifCount}");
            for (int index = 0; index < exifCount; index++)
            {
                var label = exif.ItemLabels[index];
                var value = exif.ItemValues[index];
                var item = ExifBlockGeometry.ItemRect(index, exifCount, row,
                    row ? (double)exif.Bounds.Width : (double)exif.Bounds.Height, bare);
                // 运行时把项目矩形按块 bounds 夹取（无衬底纵排 52 内尾部截断），这里按同一口径核对。
                double itemWidth = Math.Min(item.Width, (double)exif.Bounds.Width);
                double itemX = Math.Clamp(item.X, 0, Math.Max(0, (double)exif.Bounds.Width - itemWidth));
                item = new LayoutRect(itemX, item.Y, itemWidth, item.Height);
                if (Math.Abs((double)label.Font.PointSize - DesignTokens.ToolbarExifLabelFontSize) > Exact
                    || Math.Abs((double)value.Font.PointSize - DesignTokens.ToolbarExifValueFontSize) > Exact)
                    throw new InvalidOperationException($"{scope} EXIF 第 {index + 1} 项字号不是 10/12：{label.Font.PointSize}/{value.Font.PointSize}");
                if ((double)value.Font.PointSize <= (double)label.Font.PointSize)
                    throw new InvalidOperationException($"{scope} EXIF 第 {index + 1} 项标签/值层级丢失");
                if ((double)label.Frame.GetMaxY() > (double)value.Frame.Y + Exact)
                    throw new InvalidOperationException($"{scope} EXIF 第 {index + 1} 项标签未在数值上方：{label.Frame}/{value.Frame}");
                if ((double)label.Frame.X < (double)item.X - Exact
                    || (double)label.Frame.GetMaxX() > (double)item.MaxX + Exact
                    || Math.Abs((double)label.Frame.Width - (double)item.Width) > Exact)
                    throw new InvalidOperationException($"{scope} EXIF 第 {index + 1} 项未落在共享项目矩形内：{label.Frame} / {item}");
                if ((double)label.Frame.Y < (double)item.Y - Exact || (double)value.Frame.GetMaxY() > (double)item.MaxY + Exact)
                    throw new InvalidOperationException($"{scope} EXIF 第 {index + 1} 项越出项目矩形：{label.Frame}/{value.Frame} / {item}");
            }
            if (row)
            {
                if (exif.Background.Hidden || exif.Background.Effect is not UIBlurEffect)
                    throw new InvalidOperationException($"{scope} 上下布局 EXIF 块缺少模糊材质衬底");
                if (Math.Abs((double)exif.Layer.CornerRadius - ExifBlockGeometry.CornerRadius) > Exact)
                    throw new InvalidOperationException(
                        $"{scope} 上下布局 EXIF 块圆角 {exif.Layer.CornerRadius} ≠ {ExifBlockGeometry.CornerRadius:0.##}");
            }
            else
            {
                if (!exif.Background.Hidden)
                    throw new InvalidOperationException($"{scope} 左右布局 EXIF 块仍显示模糊衬底");
                if ((double)exif.Layer.CornerRadius > Exact)
                    throw new InvalidOperationException($"{scope} 左右布局 EXIF 块仍有圆角 {exif.Layer.CornerRadius}");
                if (Math.Abs((double)exif.Bounds.Width - specWidth) > Exact)
                    throw new InvalidOperationException(
                        $"{scope} 无衬底 EXIF 列宽 {exif.Bounds.Width:0.##} ≠ 固有项宽 {specWidth:0.##}（无额外内外边距）");
            }
            exifSummary = $"{frameMain:0.##}×{frameCross:0.##}（{exifCount} 项{(row ? "横排带衬底" : "竖排无衬底")}）";
        }
        // 星级分组：共享尺寸占位，控件在组内铺满，胶囊按共享几何落在展开/紧凑形态。
        double innerMain = Math.Max(1, availableMain - mainLeading - mainTrailing);
        var ratingSize = ToolbarFlowLayout.Rating(Math.Max(1, innerMain - ToolbarFlowLayout.Padding * 2));
        string ratingSummary = "无星级分组";
        if (bar.HasRatingGroup)
        {
            var control = bar.RatingControl;
            if (control.Horizontal != row)
                throw new InvalidOperationException($"{scope} 星级控件主轴方向未随布局方向：{control.Horizontal}/{row}");
            double frameMain = row ? (double)control.Frame.Width : (double)control.Frame.Height;
            if (Math.Abs(frameMain - ratingSize.Main) > Exact)
                throw new InvalidOperationException($"{scope} 星级分组主轴 {frameMain:0.##} 与共享尺寸 {ratingSize.Main:0.##} 不一致");
            double capsuleMain = row ? (double)control.Bounds.Width : (double)control.Bounds.Height;
            var form = RatingControlGeometry.ResolveForm(capsuleMain);
            if (control.Form != form)
                throw new InvalidOperationException($"{scope} 星级控件形态 {control.Form} 与共享判定 {form} 不一致");
            var capsule = control.CapsuleFrame;
            double capsuleWidth = capsule.Width, capsuleHeight = capsule.Height;
            if (Math.Abs((row ? capsuleHeight : capsuleWidth) - RatingControlGeometry.Thickness) > Exact)
                throw new InvalidOperationException($"{scope} 星级胶囊交叉轴 {capsuleWidth:0.##}×{capsuleHeight:0.##} 不是 44 点");
            double expectedMain = form == RatingControlForm.Expanded ? RatingControlGeometry.ExpandedLength : capsuleMain;
            if (Math.Abs((row ? capsuleWidth : capsuleHeight) - expectedMain) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 星级胶囊主轴 {(row ? capsuleWidth : capsuleHeight):0.##} 与 {form} 形态预期 {expectedMain:0.##} 不一致");
            var rowFrame = control.Row.ConvertRectToView(control.Row.Bounds, control);
            if (rowFrame != control.Capsule.Frame || !ReferenceEquals(control.Row.Superview, control.Capsule.ContentView))
                throw new InvalidOperationException($"{scope} 展开态星排未铺满玻璃胶囊内容区：{rowFrame}/{control.Capsule.Frame}");
            ratingSummary = $"{form} 胶囊 {(int)capsuleWidth}×{(int)capsuleHeight}";
        }
        // 命令圆按钮：44×44，与星级控件平级挂在玻璃容器内容区，位置按共享偏移换算到命令组局部坐标。
        var commands = bar.Commands.Values.ToArray();
        foreach (var command in commands)
            RequireFrame(command.Frame, new CGRect(command.Frame.X, command.Frame.Y, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize),
                $"{scope} 命令按钮未保持 44×44");
        if (commandCount > 0)
        {
            var group = bar.Groups[commandIndex];
            for (int index = 0; index < commands.Length; index++)
            {
                double expected = ToolbarFlowLayout.CommandOffset(index);
                var frame = commands[index].ConvertRectToView(commands[index].Bounds, group);
                double actual = row ? (double)frame.X : (double)frame.Y;
                double cross = row ? (double)frame.Y : (double)frame.X;
                double expectedCross = row
                    ? (double)(group.Bounds.Height - ToolbarFlowLayout.CommandSize) / 2
                    : (double)(group.Bounds.Width - ToolbarFlowLayout.CommandSize) / 2;
                if (Math.Abs(actual - expected) > Exact || Math.Abs(cross - expectedCross) > Exact)
                    throw new InvalidOperationException(
                        $"{scope} 命令按钮 {index} 在命令组内偏移 ({actual:0.##},{cross:0.##}) 与共享偏移 ({expected:0.##},{expectedCross:0.##}) 不一致");
            }
        }
        // 分析栏/增强预览命令实现可用；是否出现在控制栏仍由共享设置的显隐决定（默认分析栏不显示）。
        if (!iOSToolbarView.IsCommandAvailable("ToggleAnalysisView") || !iOSToolbarView.IsCommandAvailable("ToggleEnhance"))
            throw new InvalidOperationException($"{scope} I5 起分析栏/增强预览命令必须有原生实现（SCOPE-01 解禁）");
        // TOOLBAR-08：滚动宿主的内容尺寸与控制栏文档一致，偏移落在合法范围（重排只夹取，不重置）。
        if (root.ControlScroll.ContentSize != bar.Frame.Size)
            throw new InvalidOperationException($"{scope} 控制栏滚动内容尺寸 {Size(root.ControlScroll.ContentSize)} 与文档 {Size(bar.Frame.Size)} 不一致");
        var offset = root.ControlScroll.ContentOffset;
        if ((double)offset.X < 0 || (double)offset.Y < 0
            || (double)offset.X > Math.Max(0, (double)root.ControlScroll.ContentSize.Width - (double)root.ControlScroll.Bounds.Width)
            || (double)offset.Y > Math.Max(0, (double)root.ControlScroll.ContentSize.Height - (double)root.ControlScroll.Bounds.Height))
            throw new InvalidOperationException($"{scope} 控制栏滚动偏移越界：{offset}");
        lines.Add($"PASS {scope} 控制栏：{bar.Groups.Count} 组（EXIF {exifSummary}/星级 {ratingSummary}/命令 {commandCount}）文档 {Size(bounds)} 裁剪区 {Size(root.ControlScroll.Bounds)}；10 点外边距、EXIF 块按共享尺寸逐项落位、命令 44×44 按共享偏移、I5 命令入口可用、滚动内容尺寸与偏移合规");
    }

    /// <summary>
    /// 核对宽形态卡片几何（[FILM-05]）：固定大档、带厚等于共享
    /// <see cref="FilmCardGeometry.BandThickness"/>、可见容量不少于 5 张；卡片矩形取共享
    /// <see cref="FilmCardGeometry.ItemFrame"/>，内部几何按档位（大档 96×144 / 90×138 / 80 方形 / 六格星级，
    /// 小档 64×76 / 60×72 / 54 方形 / 一行文件名 / 星级角标与抖动点）与身份绑定逐项核对。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyFilmstripGeometryAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        // 大档逐位等于旧 96×144 口径（Mac / Windows / Android 继续用旧类型，共享两档不得漂移）。
        if (FilmstripGeometry.CardWidth != DesignTokens.FilmCardWidth || FilmstripGeometry.CardHeight != DesignTokens.FilmCardHeight ||
            FilmstripGeometry.HorizontalPadding != DesignTokens.FilmListPadLeft ||
            FilmstripGeometry.TopPadding != DesignTokens.FilmListPadTop ||
            FilmstripGeometry.BottomPadding != DesignTokens.FilmListPadBottom)
            throw new InvalidOperationException($"{scope} 胶片栏不再使用已验证的 96×144 卡片与 10/5/10 内边距");
        var filmstrip = root.Filmstrip;
        bool horizontal = filmstrip.Horizontal;
        if (horizontal != row) throw new InvalidOperationException($"{scope} 胶片栏方向未跟随布局（行布局横向单行、列布局纵向单列）");
        var document = filmstrip.DocumentSize;
        var viewport = filmstrip.Bounds.Size;
        int count = main.FileVM.ThumbnailList.FilteredFiles.Count;
        var tier = filmstrip.Tier;
        // 列布局的胶片条从筛选 header 下方滚过：两端留白 = 停靠线 − 该档主轴前导（首卡因此恰好停在 header 下缘），
        // 行布局不留白。
        double lead = row ? 0 : Math.Max(0, (double)root.FilterBar.Frame.Height - FilmCardGeometry.MainLead(tier, false));
        double filmMain = horizontal ? (double)viewport.Width : (double)viewport.Height;
        double innerMain = Math.Max(1, filmMain - lead);
        double filmCross = horizontal ? (double)viewport.Height : (double)viewport.Width;
        const FilmCardTier expectedTier = FilmCardTier.Large;
        if (tier != expectedTier)
            throw new InvalidOperationException(
                $"{scope} 宽形态卡片档位 {tier} ≠ 固定大档（胶片条主轴 {filmMain:0.##}）");
        int capacity = FilmCardGeometry.VisibleCapacity(filmMain, tier, horizontal);
        if (capacity < FilmCardGeometry.MinVisible)
            throw new InvalidOperationException(
                $"{scope} 可见容量 {capacity} 少于 {FilmCardGeometry.MinVisible} 张（胶片条主轴 {filmMain:0.##}，{tier}）");
        double band = FilmCardGeometry.BandThickness(tier, horizontal);
        if (Math.Abs(filmCross - band) > .5)
            throw new InvalidOperationException($"{scope} 胶片条带厚 {filmCross:0.##} ≠ 共享带厚 {band:0.##}（{tier}）");
        double cardWidth = FilmCardGeometry.Width(tier), cardHeight = FilmCardGeometry.Height(tier);
        double step = FilmCardGeometry.Step(tier, horizontal);
        double expected = count * step + FilmCardGeometry.MainLead(tier, horizontal) + FilmCardGeometry.MainTrail(tier, horizontal);
        double documentMain = horizontal ? (double)document.Width : (double)document.Height;
        if (Math.Abs(documentMain - (Math.Max(innerMain, expected) + lead)) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏文档主轴长度错误：{documentMain}");
        var first = filmstrip.FrameAt(0) ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
        // 共享卡片矩形的跨轴基准：横向取文档主轴长度，纵向取视口宽度；列布局再沿主轴后移筛选控件高。
        double sharedContentWidth = horizontal ? documentMain : (double)viewport.Width;
        var expectedFirst = FilmCardGeometry.ItemFrame(tier, horizontal, 0, count, sharedContentWidth);
        RequireFrame(first, new CGRect(expectedFirst.X, expectedFirst.Y + (horizontal ? 0 : lead), expectedFirst.Width, expectedFirst.Height),
            $"{scope} 首卡位置与卡片尺寸（{tier}）");
        if (!horizontal)
        {
            // 首卡恰好停在 header 下缘（停靠线 = filterMain，不再叠加一份主轴前导），外框右缘与 header 同线。
            if (Math.Abs((double)first.Y - (double)root.FilterBar.Frame.Height) > .5)
                throw new InvalidOperationException(
                    $"{scope} 列布局首卡未停在 header 下缘：Y {first.Y:0.##} ≠ {root.FilterBar.Frame.Height:0.##}");
            double cardRight = (double)root.Filmstrip.Frame.X + (double)first.X + (double)first.Width;
            if (Math.Abs(cardRight - root.FilmSide.ContentTrailing) > .5)
                throw new InvalidOperationException(
                    $"{scope} 列布局卡片外框右缘 {cardRight:0.##} 未与 header 右缘 {root.FilmSide.ContentTrailing:0.##} 同线");
        }
        if (Math.Abs((double)first.Width - cardWidth) > Exact || Math.Abs((double)first.Height - cardHeight) > Exact)
            throw new InvalidOperationException($"{scope} 首卡尺寸 {Size(first)} ≠ {tier} 档 {cardWidth:0.##}×{cardHeight:0.##}");
        var second = filmstrip.FrameAt(count > 1 ? 1 : 0) ?? throw new InvalidOperationException($"{scope} 第二卡几何缺失");
        if (count > 1)
        {
            double cardStep = horizontal ? (double)(second.X - first.X) : (double)(second.Y - first.Y);
            if (Math.Abs(cardStep - step) > Exact) throw new InvalidOperationException($"{scope} 胶片栏步长错误：{cardStep} != {step}");
            if (horizontal ? Math.Abs((double)(second.Y - first.Y)) > Exact : Math.Abs((double)(second.X - first.X)) > Exact)
                throw new InvalidOperationException($"{scope} 胶片栏出现换行或换列：{first} / {second}");
        }
        var cell = await WaitForCardAsync(root, main, 0, scope);
        var labels = cell.Surface.Subviews.OfType<UILabel>().ToArray();
        string detail;
        if (tier == FilmCardTier.Large)
        {
            RequireFrame(cell.Surface.Frame, new CGRect(DesignTokens.FilmCardInset, DesignTokens.FilmCardInset,
                DesignTokens.FilmCardSurfaceWidth, DesignTokens.FilmCardSurfaceHeight), $"{scope} 卡片占位区（96×144 内部）");
            RequireFrame(cell.ThumbnailView.Frame, new CGRect(DesignTokens.FilmCardThumbInset, DesignTokens.FilmCardThumbInset,
                DesignTokens.FilmCardSurfaceWidth - DesignTokens.FilmCardThumbInset * 2,
                DesignTokens.FilmCardSurfaceWidth - DesignTokens.FilmCardThumbInset * 2), $"{scope} 卡片照片区非 80 方形");
            var name = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - DesignTokens.FilmCardNameY) < Exact)
                ?? throw new InvalidOperationException($"{scope} 卡片文件名缺失");
            var date = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - DesignTokens.FilmCardSecondLineY) < Exact)
                ?? throw new InvalidOperationException($"{scope} 卡片第二行（拍摄时间）缺失");
            var shake = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - 67) < Exact)
                ?? throw new InvalidOperationException($"{scope} 卡片抖动徽标缺失");
            RequireFrame(name.Frame, new CGRect(2, DesignTokens.FilmCardNameY, DesignTokens.FilmCardNameWidth,
                DesignTokens.FilmCardNameHeight), $"{scope} 卡片文件名");
            RequireFrame(date.Frame, new CGRect(5, DesignTokens.FilmCardSecondLineY, DesignTokens.FilmCardSecondLineWidth,
                DesignTokens.FilmCardSecondLineHeight), $"{scope} 卡片日期");
            RequireFrame(shake.Frame, new CGRect(67, 67, DesignTokens.FilmCardBadgeSize, DesignTokens.FilmCardBadgeSize),
                $"{scope} 卡片抖动徽标");
            if (name.LineBreakMode != UILineBreakMode.TailTruncation) throw new InvalidOperationException($"{scope} 卡片文件名未按尾部截断显示");
            for (int rating = 0; rating < cell.StarViews.Count; rating++)
                RequireFrame(cell.StarViews[rating].Frame, new CGRect(DesignTokens.FilmCardInset + rating * DesignTokens.FilmCardStarSize,
                    DesignTokens.FilmCardStarY, DesignTokens.FilmCardStarSize, DesignTokens.FilmCardStarSize), $"{scope} 卡片星级 {rating}");
            if (!cell.SmallRatingBadge.Hidden) throw new InvalidOperationException($"{scope} 大档卡片显示了小档星级角标");
            if (!cell.ShakeDot.Hidden) throw new InvalidOperationException($"{scope} 大档卡片显示了小档抖动点");
            detail = "卡片 96×144/(3,3,90,138)/照片 80 方形/星级 14 点";
        }
        else
        {
            RequireFrame(cell.Surface.Frame, new CGRect(DesignTokens.FilmCardSmallInset, DesignTokens.FilmCardSmallInset,
                DesignTokens.FilmCardSmallWidth - DesignTokens.FilmCardSmallInset * 2,
                DesignTokens.FilmCardSmallHeight - DesignTokens.FilmCardSmallInset * 2), $"{scope} 小档卡片占位区（64×76 内部）");
            RequireFrame(cell.ThumbnailView.Frame, new CGRect(DesignTokens.FilmCardSmallThumbInset, DesignTokens.FilmCardSmallThumbInset,
                DesignTokens.FilmCardSmallThumbSize, DesignTokens.FilmCardSmallThumbSize), $"{scope} 小档卡片照片区非 54 方形");
            var name = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - DesignTokens.FilmCardSmallNameY) < Exact)
                ?? throw new InvalidOperationException($"{scope} 小档卡片文件名缺失");
            double surfaceWidth = DesignTokens.FilmCardSmallWidth - DesignTokens.FilmCardSmallInset * 2;
            RequireFrame(name.Frame, new CGRect(2, DesignTokens.FilmCardSmallNameY, surfaceWidth - 4,
                DesignTokens.FilmCardSmallNameHeight), $"{scope} 小档卡片文件名");
            if (Math.Abs((double)name.Font.PointSize - DesignTokens.FilmCardSmallNameFontSize) > .01)
                throw new InvalidOperationException($"{scope} 小档文件名未使用 9 点字号");
            if (name.LineBreakMode != UILineBreakMode.TailTruncation) throw new InvalidOperationException($"{scope} 小档文件名未按尾部截断显示");
            if (labels.Any(label => Math.Abs((double)label.Frame.Y - DesignTokens.FilmCardSecondLineY) < Exact))
                throw new InvalidOperationException($"{scope} 小档卡片仍显示第二行");
            if (cell.StarViews.Any(star => !star.Hidden)) throw new InvalidOperationException($"{scope} 小档卡片仍显示六格星排");
            RequireFrame(cell.ShakeDot.Frame, new CGRect(
                DesignTokens.FilmCardSmallThumbInset + DesignTokens.FilmCardSmallThumbSize
                    - DesignTokens.FilmCardSmallShakeDotInset - DesignTokens.FilmCardSmallShakeDotSize,
                DesignTokens.FilmCardSmallThumbInset + DesignTokens.FilmCardSmallShakeDotInset,
                DesignTokens.FilmCardSmallShakeDotSize, DesignTokens.FilmCardSmallShakeDotSize), $"{scope} 小档抖动点");
            var firstFile = main.FileVM.ThumbnailList.FilteredFiles[0];
            int rating = Math.Max(0, firstFile.Rating);
            // 0 星且无冲突时角标隐藏（[FILM-05] 小档），有星级或冲突时贴在照片区右下角内缩 2 点。
            bool badgeVisible = rating > 0 || firstFile.HasRatingConflict;
            if (cell.SmallRatingBadge.Hidden == badgeVisible)
                throw new InvalidOperationException(
                    $"{scope} 小档星级角标可见性不符：Hidden={cell.SmallRatingBadge.Hidden}，星级 {rating}、冲突 {firstFile.HasRatingConflict}");
            if (badgeVisible)
            {
                double thumbRight = DesignTokens.FilmCardSmallThumbInset + DesignTokens.FilmCardSmallThumbSize;
                double bottom = thumbRight - DesignTokens.FilmCardBadgeInset;
                if (Math.Abs((double)cell.SmallRatingBadge.Frame.GetMaxX() - (thumbRight - DesignTokens.FilmCardBadgeInset)) > Exact
                    || Math.Abs((double)cell.SmallRatingBadge.Frame.GetMaxY() - bottom) > Exact)
                    throw new InvalidOperationException($"{scope} 小档星级角标未贴在照片区右下角内缩 2 点：{cell.SmallRatingBadge.Frame}");
                if (Math.Abs((double)cell.SmallRatingBadge.Frame.Height - DesignTokens.FilmCardSmallBadgeHeight) > .01)
                    throw new InvalidOperationException($"{scope} 小档星级角标高度不是 12 点：{cell.SmallRatingBadge.Frame.Height}");
            }
            if (cell.SmallRatingDigit.Text != rating.ToString())
                throw new InvalidOperationException($"{scope} 小档角标数字与共享星级不一致：{cell.SmallRatingDigit.Text}");
            detail = "卡片 64×76/(2,2,60,72)/照片 54 方形/一行文件名/星级角标与抖动点";
        }
        if (!ReferenceEquals(cell.BoundFile, main.FileVM.ThumbnailList.FilteredFiles[0]))
            throw new InvalidOperationException($"{scope} 首卡绑定身份与业务顺序不同");
        if (cell.IsHighlighted != main.FileVM.ThumbnailList.FilteredFiles[0].IsCurrent)
            throw new InvalidOperationException($"{scope} 卡片高亮未取共享 IsCurrent 身份");
        lines.Add($"PASS {scope} 胶片栏（{tier}，可见容量 {capacity}）：{(horizontal ? "横向单行" : "纵向单列")} 主轴 {filmMain:0.##}、带厚 {filmCross:0.##}、步长 {step}、文档 {Size(document)}、首卡 {first}、{detail}、文件名尾部截断、高亮取 IsCurrent");
    }

    /// <summary>
    /// 隐藏/恢复胶片栏与控制栏：浮层外壳里栏位不再切走主图空间，而是把对应一侧的适应区内边距释放给主图。
    /// 隐藏后该侧内边距退回 <see cref="ShellPaneLayout.SafeInsets"/> 编码的安全区值（行布局上下、列布局左右为 0，
    /// 横屏行布局的底部安全区已归零），
    /// 适应区主轴长度必须恰好增长同等长度；恢复后内边距与适应区完全还原。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyPaneReleaseAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        // 栏位隐藏后内边距退回布局安全区口径：横屏行布局同样不为底部手势条保留空间。
        var safe = ShellPaneLayout.SafeInsets(Edges(root.View!.SafeAreaInsets),
            (double)root.Stage.Bounds.Width, (double)root.Stage.Bounds.Height);
        var stage = root.Stage.Bounds;
        var before = root.Panes;
        var beforeInsets = root.Photo.FitInsets;
        var beforeFit = root.Photo.FitRect;
        double beforeMain = row ? (double)beforeFit.Height : (double)beforeFit.Width;
        double beforeLeading = row ? (double)beforeInsets.Top : (double)beforeInsets.Left;
        // 隐藏后该侧内边距退回共享浮层口径：行布局保留顶边安全区，列布局为 0。
        double expectedLeading = row ? (double)safe.Top : 0;
        if (beforeLeading <= expectedLeading + .5)
            throw new InvalidOperationException($"{scope} 主图旁没有可释放的文件栏分区：{before.Photo}（内边距 {beforeLeading:0.##}）");
        await UiDispatcher.InvokeAsync(() => main.IsThumbnailViewVisible = false);
        await SettleAsync(root);
        if (!root.FileBarView.Hidden || !root.Filmstrip.Hidden || !root.FilterBar.Hidden || !root.FileDividerView.Hidden)
            throw new InvalidOperationException(
                $"{scope} 隐藏胶片栏未收起文件栏浮层：背景 {root.FileBarView.Frame}、筛选 {root.FilterBar.Frame}");
        var releasedInsets = root.Photo.FitInsets;
        double releasedLeading = row ? (double)releasedInsets.Top : (double)releasedInsets.Left;
        if (Math.Abs(releasedLeading - expectedLeading) > .5)
            throw new InvalidOperationException(
                $"{scope} 隐藏胶片栏后适应区{(row ? "上" : "左")}侧内边距 {releasedLeading:0.##} 未退回安全区 {expectedLeading:0.##}：{releasedInsets}");
        RequireFitInsets(root, root.Panes.Photo, stage, $"{scope} 隐藏胶片栏后适应区");
        var releasedFit = root.Photo.FitRect;
        double releasedMain = row ? (double)releasedFit.Height : (double)releasedFit.Width;
        double fileGrowth = releasedMain - beforeMain;
        if (Math.Abs(fileGrowth - (beforeLeading - expectedLeading)) > .5)
            throw new InvalidOperationException(
                $"{scope} 隐藏胶片栏释放的适应区长度不符：{fileGrowth:0.##} != {beforeLeading - expectedLeading:0.##}");
        await UiDispatcher.InvokeAsync(() => main.IsThumbnailViewVisible = true);
        await SettleAsync(root);
        RequireInsetsEquals(root.Photo.FitInsets, beforeInsets, $"{scope} 恢复胶片栏后适应区内边距未还原");
        RequireFrame(root.Photo.FitRect, beforeFit, $"{scope} 恢复胶片栏后适应区未还原");

        double beforeTrailing = row
            ? (double)stage.Height - ((double)beforeFit.Y + (double)beforeFit.Height)
            : (double)stage.Width - ((double)beforeFit.X + (double)beforeFit.Width);
        // 隐藏控制栏后该侧内边距同样退回布局安全区口径：行布局保留底边安全区（横屏已归零），列布局为 0。
        double expectedTrailing = row ? (double)safe.Bottom : 0;
        if (beforeTrailing <= expectedTrailing + .5)
            throw new InvalidOperationException($"{scope} 主图旁没有可释放的控制栏分区：{before.Photo}（内边距 {beforeTrailing:0.##}）");
        await UiDispatcher.InvokeAsync(() => main.IsControlViewVisible = false);
        await SettleAsync(root);
        if (!root.ControlScroll.Hidden || !root.ControlBackgroundView.Hidden || !root.ControlDividerView.Hidden)
            throw new InvalidOperationException($"{scope} 隐藏控制栏未收起控制栏浮层：{root.ControlBackgroundView.Frame}");
        var controlInsets = root.Photo.FitInsets;
        var controlFitNow = root.Photo.FitRect;
        double releasedTrailing = row
            ? (double)stage.Height - ((double)controlFitNow.Y + (double)controlFitNow.Height)
            : (double)stage.Width - ((double)controlFitNow.X + (double)controlFitNow.Width);
        if (Math.Abs(releasedTrailing - expectedTrailing) > .5)
            throw new InvalidOperationException(
                $"{scope} 隐藏控制栏后适应区{(row ? "下" : "右")}侧内边距 {releasedTrailing:0.##} 未退回安全区 {expectedTrailing:0.##}：{controlInsets}");
        RequireFitInsets(root, root.Panes.Photo, stage, $"{scope} 隐藏控制栏后适应区");
        var controlFit = root.Photo.FitRect;
        double controlGrowth = (row ? (double)controlFit.Height : (double)controlFit.Width) - beforeMain;
        if (Math.Abs(controlGrowth - (beforeTrailing - expectedTrailing)) > .5)
            throw new InvalidOperationException(
                $"{scope} 隐藏控制栏释放的适应区长度不符：{controlGrowth:0.##} != {beforeTrailing - expectedTrailing:0.##}");
        await UiDispatcher.InvokeAsync(() => main.IsControlViewVisible = true);
        await SettleAsync(root);
        RequireInsetsEquals(root.Photo.FitInsets, beforeInsets, $"{scope} 恢复控制栏后适应区内边距未还原");
        RequireFrame(root.Photo.FitRect, beforeFit, $"{scope} 恢复控制栏后适应区未还原");
        lines.Add($"PASS {scope} 栏位显隐：隐藏胶片栏把{(row ? "上" : "左")}侧内边距退回安全区并释放 {fileGrowth:0.##} 点、隐藏控制栏把{(row ? "下" : "右")}侧退回安全区并释放 {controlGrowth:0.##} 点，恢复后内边距与适应区完全还原");
    }

    /// <summary>核对两个内边距逐项相等。</summary>
    /// <param name="actual">实际内边距。</param>
    /// <param name="expected">预期内边距。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireInsetsEquals(UIEdgeInsets actual, UIEdgeInsets expected, string context)
    {
        if (Math.Abs((double)actual.Top - (double)expected.Top) > Exact
            || Math.Abs((double)actual.Left - (double)expected.Left) > Exact
            || Math.Abs((double)actual.Bottom - (double)expected.Bottom) > Exact
            || Math.Abs((double)actual.Right - (double)expected.Right) > Exact)
            throw new InvalidOperationException(
                $"{context}：实际 T{actual.Top:0.##} L{actual.Left:0.##} B{actual.Bottom:0.##} R{actual.Right:0.##}，" +
                $"预期 T{expected.Top:0.##} L{expected.Left:0.##} B{expected.Bottom:0.##} R{expected.Right:0.##}");
    }

    /// <summary>核对 [FILM-01–03]：点选不居中、显式定位的中间帧与终点、端点钳制、用户接管、减少动态效果。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    /// <param name="session">展示会话（空图时的守卫用）。</param>
    /// <summary>真实的上一张/下一张按钮：宽形态取控制栏命令，手机形态取上下张合体。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="command">Previous 或 Next。</param>
    /// <returns>可发送真实点按动作的按钮。</returns>
    private static UIButton NavigationButton(iOSNativeRootViewController root, string command)
    {
        if (root.Form == ScreenForm.Wide) return root.ControlBar.Commands[command];
        var buttons = root.PhoneNavigationGroup.Buttons;
        return command == "Next" ? buttons[1] : buttons[0];
    }

    private static async Task VerifyFilmstripAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines, PhotoLoadSession session)
    {
        var filmstrip = root.Filmstrip;
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (files.Count < 12) throw new InvalidOperationException($"胶片栏交互核对需要 ≥12 张：{files.Count}");
        // 点选：先离开首项位置，确保"不居中"可观测（[FILM-01]）
        await UiDispatcher.InvokeAsync(() => main.FileVM.ThumbnailList.SelectImageCommand(files[6]));
        await SettleAsync(root);
        MainOffset(filmstrip, out double beforeTap, out _);
        var tapTarget = files[4];
        var card = await WaitForCardAsync(root, main, 4, "FILM-01");
        await UiDispatcher.InvokeAsync(() => card.SelectButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        await WaitAsync(() => ReferenceEquals(main.CurrentFile, tapTarget), "点选切换当前照片");
        await Task.Delay(500);
        MainOffset(filmstrip, out double afterTap, out _);
        if (Math.Abs(afterTap - beforeTap) > .5)
            throw new InvalidOperationException($"点选卡片后列表被移动（{beforeTap:0.##} → {afterTap:0.##}），违反 FILM-01 不居中");
        var tappedCard = await WaitForCardAsync(root, main, 4, "FILM-01 高亮");
        if (!tappedCard.IsHighlighted) throw new InvalidOperationException("点选后卡片未取得当前项高亮");
        lines.Add($"PASS FILM-01：点选第 5 张（偏移保持 {afterTap:0.##}）不居中、立即高亮、主图身份随之切换");

        // 显式定位：先滚到起点并把当前项放到远端（目标偏移远离视线），再用真实"下一张"按钮触发（[FILM-02]）
        await UiDispatcher.InvokeAsync(() =>
        {
            filmstrip.Collection.SetContentOffset(CGPoint.Empty, false);
            main.FileVM.ThumbnailList.SelectImageCommand(files[6]);
        });
        await SettleAsync(root);
        MainOffset(filmstrip, out double startOffset, out double viewportMain);
        var next = NavigationButton(root, "Next");
        var samples = new List<double>();
        await UiDispatcher.InvokeAsync(() => next.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMilliseconds(700))
        {
            MainOffset(filmstrip, out double sample, out _);
            samples.Add(sample);
            await Task.Delay(25);
        }
        if (!ReferenceEquals(main.CurrentFile, files[7])) throw new InvalidOperationException("真实下一张按钮未切换照片");
        var targetFrame = filmstrip.FrameAt(7) ?? throw new InvalidOperationException("目标卡片几何缺失");
        double documentMain = filmstrip.Horizontal ? (double)filmstrip.DocumentSize.Width : (double)filmstrip.DocumentSize.Height;
        double targetOffset = FilmstripGeometry.CenterOffset(filmstrip.Horizontal ? (double)targetFrame.X : (double)targetFrame.Y,
            filmstrip.Horizontal ? (double)targetFrame.Width : (double)targetFrame.Height, viewportMain, documentMain);
        double finalOffset = samples[^1];
        if (Math.Abs(finalOffset - targetOffset) > .5)
            throw new InvalidOperationException($"显式定位终点错误：{finalOffset:0.##} != {targetOffset:0.##}（采样 [{string.Join(",", samples.Select(sample => sample.ToString("0.#")))}]；文档 {Size(filmstrip.DocumentSize)} 视口 {Size(filmstrip.Bounds.Size)} 动画中 {filmstrip.IsAnimating} 偏移 {filmstrip.ContentOffset.X:0.##},{filmstrip.ContentOffset.Y:0.##}）");
        bool midFrame = samples.Any(sample => Math.Abs(sample - startOffset) > 2 && Math.Abs(sample - targetOffset) > 2);
        if (!midFrame && Math.Abs(targetOffset - startOffset) > 40)
            throw new InvalidOperationException($"显式定位没有中间帧，疑似直接跳到终点（起点 {startOffset:0.##} 终点 {targetOffset:0.##}，采样 {samples.Count}）");
        lines.Add($"PASS FILM-02：真实下一张按钮切换后 350ms 缓出居中（起点 {startOffset:0.##} → 终点 {finalOffset:0.##}，采样 {samples.Count} 帧，中间帧 {(midFrame ? "有" : "无需（位移 <2）")}）");

        // 端点：末张再下一张不环绕、首张再上一张不环绕且保持在合法边界（[FILM-02]）
        await UiDispatcher.InvokeAsync(() => main.FileVM.ThumbnailList.SelectImageCommand(files[^1]));
        var lastNext = NavigationButton(root, "Next");
        await UiDispatcher.InvokeAsync(() => lastNext.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        await SettleAsync(root);
        if (!ReferenceEquals(main.CurrentFile, files[^1])) throw new InvalidOperationException("末张再下一张发生了环绕");
        await UiDispatcher.InvokeAsync(() => main.FileVM.ThumbnailList.SelectImageCommand(files[0]));
        var firstPrevious = NavigationButton(root, "Previous");
        await UiDispatcher.InvokeAsync(() => firstPrevious.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        await SettleAsync(root);
        if (!ReferenceEquals(main.CurrentFile, files[0])) throw new InvalidOperationException("首张再上一张发生了环绕");
        MainOffset(filmstrip, out double clamped, out _);
        if (clamped < -Exact || clamped > Math.Max(0, documentMain - viewportMain) + Exact)
            throw new InvalidOperationException($"首张定位越界：{clamped:0.##}，合法上界 {Math.Max(0, documentMain - viewportMain):0.##}");
        lines.Add($"PASS FILM-02 端点：末张/首张不再翻页（不环绕），定位停留在合法范围 {clamped:0.##}");

        // 用户接管：动画中点按列表（真实滚动委托钩子）后保留用户位置（[FILM-03]）
        await UiDispatcher.InvokeAsync(() =>
        {
            filmstrip.Collection.SetContentOffset(CGPoint.Empty, false);
            main.FileVM.ThumbnailList.SelectImageCommand(files[2]);
        });
        await SettleAsync(root);
        var takeoverTrigger = NavigationButton(root, "Next");
        await UiDispatcher.InvokeAsync(() => takeoverTrigger.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        await Task.Delay(90);
        await UiDispatcher.InvokeAsync(() =>
        {
            filmstrip.Collection.Delegate?.DraggingStarted(filmstrip.Collection);
            SetMainOffset(filmstrip, 40);
        });
        await Task.Delay(500);
        MainOffset(filmstrip, out double afterTakeover, out _);
        if (Math.Abs(afterTakeover - 40) > .5 || filmstrip.IsAnimating)
            throw new InvalidOperationException($"用户接管后自动定位未撤销：偏移 {afterTakeover:0.##}，动画中 {filmstrip.IsAnimating}");
        lines.Add($"PASS FILM-03：动画中点按列表即接管（偏移停在用户位置 {afterTakeover:0.##}，动画已撤销）");

        // 减少动态效果：注入系统开关后显式请求直接到终点（[FILM-03]）
        filmstrip.ReduceMotionProvider = () => true;
        try
        {
            await UiDispatcher.InvokeAsync(() =>
            {
                filmstrip.Collection.SetContentOffset(CGPoint.Empty, false);
                main.FileVM.ThumbnailList.SelectImageCommand(files[6]);
            });
            await SettleAsync(root);
            var reduceNext = NavigationButton(root, "Next");
            await UiDispatcher.InvokeAsync(() => reduceNext.SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await Task.Delay(30);
            MainOffset(filmstrip, out double reducedOffset, out _);
            var reducedTarget = filmstrip.FrameAt(7) ?? throw new InvalidOperationException("减少动态效果核对缺少目标卡片几何");
            double reducedExpected = FilmstripGeometry.CenterOffset(filmstrip.Horizontal ? (double)reducedTarget.X : (double)reducedTarget.Y,
                filmstrip.Horizontal ? (double)reducedTarget.Width : (double)reducedTarget.Height, viewportMain, documentMain);
            if (Math.Abs(reducedExpected - startOffset) < 40)
                throw new InvalidOperationException($"减少动态效果核对的目标位移太小（{reducedExpected:0.##}），无法区分直接定位与动画：请检查夹具数量与视口宽度");
            if (Math.Abs(reducedOffset - reducedExpected) > .5)
                throw new InvalidOperationException($"减少动态效果下未直接到终点：{reducedOffset:0.##} != {reducedExpected:0.##}");
            lines.Add($"PASS FILM-03 减少动态效果：注入开关后显式定位 30ms 内直接到终点（{reducedOffset:0.##}，位移 {Math.Abs(reducedExpected - startOffset):0.##} 点），跳过 350ms 动画");
        }
        finally
        {
            filmstrip.ReduceMotionProvider = () => UIAccessibility.IsReduceMotionEnabled;
        }
        if (session.Image == null) throw new InvalidOperationException("主图会话在布局验收期间未保持有效");
        lines.Add($"PASS 主图链路：胶片栏交互期间主图仍上屏（物理视口 {session.IsProxy switch { true => "代理档", false => "Full 档" }}，视图 {Size(root.Photo.Bounds)}）");
    }

    /// <summary>
    /// 卡片星级写回、输入分流与筛选连续性（[FILM-05/06]）：指针点星照常写回自有副本、不切主图；
    /// 手指落在星排上落到整卡选择按钮（只选中、不写值）；改星后照片暂留、切走后落出筛选。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    /// <param name="directory">自有夹具目录。</param>
    private static async Task VerifyRatingAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines, string directory)
    {
        var filter = main.FileVM.FilterBar;
        var list = main.FileVM.ThumbnailList;
        string entryFilter = filter.SelectedRatingFilter;
        var entryFile = main.CurrentFile ?? throw new InvalidOperationException("评分核对缺少当前照片");
        string entryPath = entryFile.File.RequireLocalPath();
        if (!Path.GetFullPath(entryPath).StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal))
            throw new InvalidOperationException($"评分核对不在自有夹具目录内：{entryPath}");
        int entryRating = (await ExifLoader.LoadExifDataAsync(entryFile.File))?.Rating ?? 0;
        var fixtureRatings = new Dictionary<string, int>(StringComparer.Ordinal);
        int target = entryRating == 3 ? 2 : 3;
        // 每次真正落盘前的提交都经根控制器 RatingCommitted 计数（控件、弹窗与卡片入口同一出口）。
        var commits = new List<(ImageFile File, int Value)>();
        Action<ImageFile, int> observer = (file, value) => commits.Add((file, value));
        root.RatingCommitted += observer;
        try
        {
            var other = main.FolderVM.AllFiles.FirstOrDefault(candidate => !ReferenceEquals(candidate, entryFile))
                ?? throw new InvalidOperationException("评分核对需要至少两张自有夹具");
            foreach (var candidate in main.FolderVM.AllFiles)
                fixtureRatings[candidate.File.StorageId.Value] = (await ExifLoader.LoadExifDataAsync(candidate.File))?.Rating ?? 0;
            // 另一张副本先与当前照片同星级，才能同时构成"当前照片改星后落出筛选"和"列表里还有符合筛选的照片"。
            if (fixtureRatings[other.File.StorageId.Value] != entryRating) await main.SetRatingAsync(other, entryRating);
            await WaitAsync(() => entryFile.Rating == entryRating && other.Rating == entryRating, "自有副本评级就绪");
            string key = entryRating == 0 ? "None" : $"Eq{entryRating}";
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = key);
            await WaitAsync(() => list.FilteredFiles.Contains(entryFile) && list.FilteredFiles.Contains(other), $"筛选键 {key} 就绪");
            if (list.FilteredFiles.Any(item => item.Rating != entryRating && !ReferenceEquals(item, entryFile)))
                throw new InvalidOperationException($"{key} 筛选列表仍含不符合条件的照片，无法核对 FILM-06 暂留规则");
            // 筛选变化后胶片栏 reload 是异步的：等 UI 数据源与列表模型对齐再取卡片，避免 layout 期间旧索引越界。
            await WaitAsync(() => root.Filmstrip.Collection.DataSource?.GetItemsCount(root.Filmstrip.Collection, 0) == list.FilteredFiles.Count, "胶片栏数据源与筛选列表对齐");
            await SettleAsync(root);

            // —— 输入分流（非当前卡片）：指针点星写值不选中；直接触摸落在星排上只选中、不写值。
            int otherIndex = list.FilteredFiles.IndexOf(other);
            var pointerCard = await WaitForCardAsync(root, main, otherIndex, "FILM-05 指针点星");
            int pointerValue = entryRating == 1 ? 3 : 1;
            commits.Clear();
            await UiDispatcher.InvokeAsync(() => pointerCard.HandleStarTap(pointerValue, indirectPointer: true));
            await WaitAsync(() => other.Rating == pointerValue, $"卡片星排指针点星写回 {pointerValue} 星");
            var otherWritten = await ExifLoader.LoadExifDataAsync(other.File);
            if (otherWritten?.Rating != pointerValue)
                throw new InvalidOperationException($"指针点星未写回文件：memory={other.Rating}, file={otherWritten?.Rating}");
            if (commits.Count != 1 || !ReferenceEquals(commits[0].File, other) || commits[0].Value != pointerValue)
                throw new InvalidOperationException(
                    $"指针点星提交次数/对象不符：{string.Join(",", commits.Select(pair => $"{pair.File.Name}:{pair.Value}"))}");
            if (!ReferenceEquals(main.CurrentFile, entryFile))
                throw new InvalidOperationException($"卡片星排指针点星改变了当前主图身份：{main.CurrentFile?.Name}");
            if (pointerCard.IsHighlighted)
                throw new InvalidOperationException("指针点星把卡片选中了（只应写值）");
            lines.Add($"PASS FILM-05 指针点星：非当前卡片第 {otherIndex + 1} 张指针点 {pointerValue} 星 → 恰好 1 次写回（文件读回一致）、主图仍是 {entryFile.Name}、卡片未被选中");

            int touchValue = pointerValue == 1 ? 4 : 1;
            // 先把另一张副本的星级还原成筛选值，触摸选中它时它才不会立刻落出筛选列表（FILM-06 的移除只在改星后发生）。
            await main.SetRatingAsync(other, entryRating);
            await WaitAsync(() => other.Rating == entryRating, "另一张副本星级还原");
            await SettleAsync(root);
            var touchCard = await WaitForCardAsync(root, main, list.FilteredFiles.IndexOf(other), "FILM-05 触摸选中");
            commits.Clear();
            await UiDispatcher.InvokeAsync(() => touchCard.HandleStarTap(touchValue, indirectPointer: false));
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, other), "手指落在星排上选中该卡");
            var afterTouch = await ExifLoader.LoadExifDataAsync(other.File);
            if (commits.Count != 0)
                throw new InvalidOperationException($"触摸点星发生了写回：{string.Join(",", commits.Select(pair => pair.Value))}");
            if (afterTouch?.Rating != entryRating || other.Rating != entryRating)
                throw new InvalidOperationException($"触摸点星改写了星级：memory={other.Rating}, file={afterTouch?.Rating}");
            // 卡片高亮来自共享 IsCurrent，经属性通知封送到主队列刷新：等它落地再断言。
            await WaitAsync(() => touchCard.IsHighlighted, "触摸点星后卡片取得当前项高亮");
            lines.Add($"PASS FILM-05 触摸分流：手指落在星排上（第 {touchValue} 格）只选中卡片（当前照片 {other.Name}、高亮到位），0 次提交、星级保持 {entryRating}");
            // 还原当前照片，后面的 FILM-06 才能在同一筛选列表里观察暂留与移除。
            await main.FolderVM.OpenImageAsync(entryFile.File);
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, entryFile), "当前照片回到进入时的一张");
            await WaitAsync(() => root.Filmstrip.Collection.DataSource?.GetItemsCount(root.Filmstrip.Collection, 0) == list.FilteredFiles.Count, "胶片栏数据源重新对齐");
            await SettleAsync(root);

            int index = list.FilteredFiles.IndexOf(entryFile);
            await UiDispatcher.InvokeAsync(() => list.ScrollToCurrent());
            var card = await WaitForCardAsync(root, main, index, "FILM-06");
            var file = entryFile;
            commits.Clear();
            await UiDispatcher.InvokeAsync(() => card.HandleStarTap(target, indirectPointer: true));
            await WaitAsync(() => file.Rating == target, $"卡片星排指针点星写回 {target} 星");
            var written = await ExifLoader.LoadExifDataAsync(file.File);
            if (written?.Rating != target) throw new InvalidOperationException($"星级未写回文件：memory={file.Rating}, file={written?.Rating}");
            if (commits.Count != 1 || commits[0].Value != target)
                throw new InvalidOperationException($"当前卡片指针点星提交次数/值不符：{string.Join(",", commits.Select(pair => pair.Value))}");
            if (!ReferenceEquals(main.CurrentFile, file)) throw new InvalidOperationException("卡片星排指针点星改变了当前主图身份");
            if (!list.FilteredFiles.Contains(file)) throw new InvalidOperationException("改星后当前照片未按 FILM-06 暂留，选片进度瞬间丢失");
            lines.Add($"PASS FILM-05/06：当前卡片指针点星写回自有副本 {target} 星（文件读回一致）、恰好 1 次提交、主图身份不变；改星后照片仍暂留在 {key} 筛选列表内");
            await UiDispatcher.InvokeAsync(() => main.CurrentFile = other);
            bool removed = false;
            var removalWatch = Stopwatch.StartNew();
            while (removalWatch.Elapsed < TimeSpan.FromSeconds(6))
            {
                if (!list.FilteredFiles.Contains(file)) { removed = true; break; }
                await Task.Delay(25);
            }
            if (!removed)
                throw new InvalidOperationException($"切走后未移除不符合筛选的照片：当前={main.CurrentFile?.Name}，改星文件={file.Name}:{file.Rating}，筛选={filter.SelectedRatingFilter}，列表={string.Join(",", list.FilteredFiles.Select(item => $"{item.Name}:{item.Rating}"))}");
            int expected = main.FolderVM.AllFiles.Count(candidate => candidate.Rating == entryRating);
            if (list.FilteredFiles.Contains(file) || list.FilteredFiles.Count != expected)
                throw new InvalidOperationException($"切走后筛选列表与 {key} 不一致：{list.FilteredFiles.Count}/{expected}");
            lines.Add($"PASS FILM-06：切走后落出 {key}，筛选列表计数与共享筛选一致（{list.FilteredFiles.Count}）");
        }
        finally
        {
            root.RatingCommitted -= observer;
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = "All");
            foreach (var pair in fixtureRatings)
            {
                var candidate = main.FolderVM.AllFiles.FirstOrDefault(item => item.File.StorageId.Value == pair.Key);
                if (candidate != null && candidate.Rating != pair.Value) await main.SetRatingAsync(candidate, pair.Value);
            }
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = entryFilter);
            if (main.CurrentFile?.File.StorageId != entryFile.File.StorageId)
                await main.FolderVM.OpenImageAsync(entryFile.File);
        }
    }

    /// <summary>
    /// 卡片长按与指针辅助点按（[FILM-05] 长按入口）：起手打开星级弹窗（单条目、锚点=卡片、方向取共享方向表），
    /// 原地抬手保持打开；同一手势滑到弹窗星排 4 号格抬手 → 恰好一次写回 4 并在写回成功后收起；
    /// 滑出星排主轴后抬手 → 不写回并收起；辅助点按只打开弹窗不写回；卡片不得再挂系统上下文菜单。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyCardPopupAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var list = main.FileVM.ThumbnailList;
        var files = list.FilteredFiles;
        if (files.Count == 0) throw new InvalidOperationException($"{scope} 卡片长按核对缺少照片");
        var file = main.CurrentFile != null && files.Contains(main.CurrentFile) ? main.CurrentFile : files[0];
        if (file.HasRatingConflict)
            throw new InvalidOperationException($"{scope} 卡片长按核对要求非冲突夹具：{file.Name}");
        int index = list.FilteredFiles.IndexOf(file);
        await UiDispatcher.InvokeAsync(() => list.ScrollToCurrent());
        var card = await WaitForCardAsync(root, main, index, $"{scope} 卡片长按");
        var popup = root.RatingPopup;
        if (popup.IsOpen) popup.Close();
        if (card.Interactions.OfType<UIContextMenuInteraction>().Any())
            throw new InvalidOperationException($"{scope} 卡片仍挂系统上下文菜单（长按入口已改为星级弹窗）");
        // 共享方向表：卡片入口宽形态行布局向下、列布局向右（手机形态另有表，本套件只跑宽形态）。
        var expectedDirection = RatingControlGeometry.PopupDirection(root.Form, main.IsRowLayout, root.NotchOnLeft,
            RatingPopupAnchor.Card);
        // 宽形态按行/列查表（行向下、列向右）；手机竖屏向上、手机横屏朝主图内侧（与刘海侧相反）。
        var documented = root.Form switch
        {
            ScreenForm.PhonePortrait => RatingPopupDirection.Up,
            ScreenForm.PhoneLandscape => root.NotchOnLeft ? RatingPopupDirection.Right : RatingPopupDirection.Left,
            _ => row ? RatingPopupDirection.Down : RatingPopupDirection.Right,
        };
        if (expectedDirection != documented)
            throw new InvalidOperationException(
                $"{scope} 卡片弹窗方向表给出 {expectedDirection}（期望 {documented}）");
        if (file.Rating != 2) await main.SetRatingAsync(file, 2);
        await WaitAsync(() => file.Rating == 2, "卡片长按核对起始星级");
        var commits = new List<int>();
        Action<ImageFile, int> observer = (target, value) => { if (ReferenceEquals(target, file)) commits.Add(value); };
        root.RatingCommitted += observer;
        var reduceMotion = popup.ReduceMotionProvider;
        // 关闭入场弹簧：面板在动画中的缩放变换会改变子视图的坐标换算，验收需要静止几何。
        popup.ReduceMotionProvider = () => true;
        try
        {
            var origin = new CGPoint((nfloat)((double)card.Bounds.Width / 2), (nfloat)((double)card.Bounds.Height / 2));
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Began, origin));
            await WaitAsync(() => popup.IsOpen && popup.TrackingRow != null, "长按打开星级弹窗");
            if (popup.Mode != iOSRatingPopup.RatingPopupMode.Single)
                throw new InvalidOperationException($"{scope} 长按弹窗内容形态 {popup.Mode}（非冲突应为 Single）");
            if (popup.AnchorKind != RatingPopupAnchor.Card || !ReferenceEquals(popup.AnchorView, card))
                throw new InvalidOperationException($"{scope} 长按弹窗锚点不是发起卡片：{popup.AnchorKind}");
            if (popup.Direction != expectedDirection)
                throw new InvalidOperationException($"{scope} 长按弹窗方向 {popup.Direction} ≠ 共享方向 {expectedDirection}");
            RequirePopupPlacement(root, popup, $"{scope} 长按弹窗");
            if (commits.Count != 0) throw new InvalidOperationException($"{scope} 长按打开弹窗发生了写回：{string.Join(",", commits)}");
            // 原地抬手（未越过点按阈值）：弹窗保持打开，等待直接点选。
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Ended, origin));
            await Task.Delay(120);
            if (!popup.IsOpen)
                throw new InvalidOperationException($"{scope} 长按原地抬手后弹窗被收起（应保持打开）");
            if (commits.Count != 0) throw new InvalidOperationException($"{scope} 原地抬手发生了写回：{string.Join(",", commits)}");
            lines.Add(
                $"PASS {scope} 卡片长按：Began 打开单条目弹窗（锚点=卡片、方向 {expectedDirection}、面板 {Size(ToFrame(popup.PanelFrame))} @({popup.PanelFrame.X:0.##},{popup.PanelFrame.Y:0.##}) 按共享 Placement 落位并夹在安全区内），" +
                "原地抬手保持打开且 0 次写回");

            // 第二次长按：滑到弹窗星排 4 号格抬手 → 恰好一次写回 4，写回成功后收起。
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Began, origin));
            var tracked = popup.TrackingRow ?? throw new InvalidOperationException($"{scope} 第二次长按未登记弹窗星排");
            var slot = RatingControlGeometry.SlotRect(4, (double)tracked.Bounds.Width, (double)tracked.Bounds.Height,
                tracked.Horizontal, tracked.Pitch);
            var slotCenterInRow = new CGPoint((nfloat)(slot.X + slot.Width / 2), (nfloat)(slot.Y + slot.Height / 2));
            var slotCenterInCard = card.ConvertPointFromView(slotCenterInRow, tracked);
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Changed, slotCenterInCard));
            if (tracked.Preview != 4)
                throw new InvalidOperationException($"{scope} 长按滑到 4 号格后预览值 {tracked.Preview?.ToString() ?? "空"}（应为 4）");
            if (commits.Count != 0) throw new InvalidOperationException($"{scope} 长按滑动预览阶段就发生了写回：{string.Join(",", commits)}");
            if (file.Rating != 2) throw new InvalidOperationException($"{scope} 长按预览改写了真值：{file.Rating}");
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Ended, slotCenterInCard));
            await WaitAsync(() => commits.Count == 1, "长按滑动提交恰好一次");
            if (commits[0] != 4)
                throw new InvalidOperationException($"{scope} 长按滑动提交值 {commits[0]}（应为 4）");
            await WaitAsync(() => !popup.IsOpen && file.Rating == 4, "写回成功后弹窗收起");
            var disk = await ExifLoader.LoadExifDataAsync(file.File);
            if (disk?.Rating != 4)
                throw new InvalidOperationException($"{scope} 长按提交未写回文件：memory={file.Rating}, file={disk?.Rating}");
            lines.Add($"PASS {scope} 卡片长按滑动：预览阶段不写回，抬手恰好 1 次提交 4 星（文件读回一致），写回成功后弹窗收起");

            // 第三次长按：滑回星排主轴之外再抬手 → 不写回、弹窗收起。
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Began, origin));
            tracked = popup.TrackingRow ?? throw new InvalidOperationException($"{scope} 第三次长按未登记弹窗星排");
            var offPoint = OffRowPoint(card, tracked, popup, origin);
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Changed, offPoint));
            if (tracked.Preview != null)
                throw new InvalidOperationException($"{scope} 滑出星排主轴后仍保留预览 {tracked.Preview}");
            await UiDispatcher.InvokeAsync(() => card.HandleLongPress(UIGestureRecognizerState.Ended, offPoint));
            await WaitAsync(() => !popup.IsOpen, "越界抬手收起弹窗");
            if (commits.Count != 1)
                throw new InvalidOperationException($"{scope} 越界抬手发生了写回：{string.Join(",", commits)}");
            if (file.Rating != 4) throw new InvalidOperationException($"{scope} 越界抬手改写了星级：{file.Rating}");
            lines.Add($"PASS {scope} 卡片长按越界：滑出星排主轴后抬手 0 次写回、星级保持 4、弹窗收起");

            // 指针辅助点按：只打开弹窗、不写回。
            await UiDispatcher.InvokeAsync(() => card.HandleSecondaryClick());
            await WaitAsync(() => popup.IsOpen, "辅助点按打开星级弹窗");
            if (popup.AnchorKind != RatingPopupAnchor.Card || !ReferenceEquals(popup.AnchorView, card))
                throw new InvalidOperationException($"{scope} 辅助点按弹窗锚点不是该卡片");
            if (commits.Count != 1) throw new InvalidOperationException($"{scope} 辅助点按发生了写回：{string.Join(",", commits)}");
            popup.Close();
            if (card.Interactions.OfType<UIContextMenuInteraction>().Any())
                throw new InvalidOperationException($"{scope} 卡片挂上了系统上下文菜单（卡片菜单已退役）");
            lines.Add($"PASS {scope} 卡片辅助点按：打开单条目弹窗、0 次写回，卡片无系统上下文菜单");
        }
        finally
        {
            root.RatingCommitted -= observer;
            popup.ReduceMotionProvider = reduceMotion;
            if (popup.IsOpen) popup.Close();
        }
    }

    /// <summary>
    /// 长按手势「滑回行外」的落点（卡片坐标）：候选依次是卡片中心、星排主轴起点再退一格（仍在面板内）、
    /// 卡片两端；必须同时满足「共享 <see cref="RatingControlGeometry.ExternalInBand"/> 判定在带外」与
    /// 「离起手点的位移越过 <see cref="RatingControlGeometry.TapSlop"/>」——否则抬手会被共享状态机当成
    /// 未移动的点按而保持弹窗打开（那正是原地抬手的行为）。
    /// </summary>
    /// <param name="card">发起长按的卡片。</param>
    /// <param name="row">弹窗星排。</param>
    /// <param name="popup">星级弹窗（入口与面板矩形）。</param>
    /// <param name="origin">卡片中心（卡片坐标）。</param>
    /// <returns>卡片坐标中的落点。</returns>
    private static CGPoint OffRowPoint(iOSThumbnailCard card, iOSRatingRow row, iOSRatingPopup popup,
        CGPoint origin)
    {
        var host = popup.Host;
        var rowRect = row.ConvertRectToView(row.Bounds, host);
        var rowBand = new LayoutRect(rowRect.X, rowRect.Y, rowRect.Width, rowRect.Height);

        bool InBand(CGPoint pointInCard)
        {
            var point = card.ConvertPointToView(pointInCard, host);
            return RatingControlGeometry.ExternalInBand((double)point.X, (double)point.Y, popup.AnchorRect,
                popup.PanelFrame, rowBand, row.Horizontal);
        }

        var fallbackInRow = row.Horizontal
            ? new CGPoint((nfloat)(RatingControlGeometry.RowStart((double)row.Bounds.Width, row.Pitch) - row.Pitch),
                (nfloat)((double)row.Bounds.Height / 2))
            : new CGPoint((nfloat)((double)row.Bounds.Width / 2),
                (nfloat)(RatingControlGeometry.RowStart((double)row.Bounds.Height, row.Pitch) - row.Pitch));
        CGPoint[] candidates =
        [
            origin,
            card.ConvertPointFromView(fallbackInRow, row),
            new CGPoint(2, (nfloat)((double)card.Bounds.Height / 2)),
            new CGPoint((nfloat)((double)card.Bounds.Width - 2), (nfloat)((double)card.Bounds.Height / 2)),
        ];
        double required = RatingControlGeometry.TapSlop + 1;
        foreach (var candidate in candidates)
        {
            double dx = (double)candidate.X - (double)origin.X;
            double dy = (double)candidate.Y - (double)origin.Y;
            if (dx * dx + dy * dy < required * required) continue;
            if (InBand(candidate)) continue;
            return candidate;
        }
        throw new InvalidOperationException("长按越界核对找不到星排主轴之外、且越过点按阈值的落点");
    }

    /// <summary>核对弹窗面板落位：逐项等于共享 <see cref="RatingControlGeometry.PlacePopup"/> 的结果，并整体夹在宿主安全区内缩边距之内。</summary>
    /// <param name="root">原生根控制器（弹窗宿主）。</param>
    /// <param name="popup">星级弹窗。</param>
    /// <param name="context">失败信息。</param>
    private static void RequirePopupPlacement(iOSNativeRootViewController root, iOSRatingPopup popup, string context)
    {
        var host = popup.Host;
        var safe = host.SafeAreaInsets;
        double edge = DesignTokens.PopupRatingSourcesEdgeMargin;
        var bounds = new LayoutRect((double)safe.Left + edge, (double)safe.Top + edge,
            Math.Max(1, (double)host.Bounds.Width - (double)safe.Left - (double)safe.Right - edge * 2),
            Math.Max(1, (double)host.Bounds.Height - (double)safe.Top - (double)safe.Bottom - edge * 2));
        var expected = RatingControlGeometry.PlacePopup(popup.AnchorRect, bounds, popup.PanelFrame.Width,
            popup.PanelFrame.Height, popup.Direction, DesignTokens.RatingPopupGap);
        RequireFrame(ToFrame(popup.PanelFrame), ToFrame(expected), $"{context} 面板落位与共享 Placement 不一致");
        if (popup.PanelFrame.X < bounds.X - Exact || popup.PanelFrame.Y < bounds.Y - Exact
            || popup.PanelFrame.MaxX > bounds.MaxX + Exact || popup.PanelFrame.MaxY > bounds.MaxY + Exact)
            throw new InvalidOperationException($"{context} 面板 {popup.PanelFrame} 越出安全区内缩边界 {bounds}");
    }

    /// <summary>
    /// 胶片栏显示快照回归：共享筛选列表已经换短、UIKit 还没重载的那一小段窗口里，
    /// 数据源条数、卡片绑定、布局几何与文档尺寸必须仍按同一份显示快照回答旧索引，不能落到 live 列表上。
    /// 用文件格式筛选把 live 列表一次缩到空，复现"重载前的陈旧索引"窗口；旧实现会在
    /// <c>Source.GetCell</c> 按旧索引读缩短后的 live 列表而越界。检查在自有夹具目录内进行，
    /// 收尾恢复全部格式与当前照片，不改变业务筛选身份、不写用户素材。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyDisplaySnapshotAsync(iOSNativeRootViewController root, MainViewModel main, List<string> lines)
    {
        var filmstrip = root.Filmstrip;
        var collection = filmstrip.Collection;
        var list = main.FileVM.ThumbnailList;
        await SettleAsync(root);
        await WaitAsync(() => filmstrip.DisplayCount == list.FilteredFiles.Count && filmstrip.DisplayCount >= 3,
            "显示快照与筛选列表对齐");
        int displayed = filmstrip.DisplayCount;
        var lastDisplayed = filmstrip.DisplayFiles[displayed - 1];
        var documentBefore = filmstrip.DocumentSize;
        var entryFile = main.CurrentFile;
        var formats = main.Settings.FileFormats.ToArray();
        var entryEnabled = formats.Select(item => item.IsEnabled).ToArray();
        try
        {
            int liveDuring = -1, displayDuring = -1, sourceCount = -1;
            ImageFile? bound = null;
            CGRect? staleFrame = null;
            CGSize documentDuring = CGSize.Empty;
            IntPtr pathHandle = IntPtr.Zero;
            bool pathShared = false;
            await UiDispatcher.InvokeAsync(() =>
            {
                // 同一段主线程代码：格式筛选同步换掉 live 列表，胶片栏的 reload 只能排在本回调之后执行。
                foreach (var format in formats) format.IsEnabled = false;
                liveDuring = list.FilteredFiles.Count;
                displayDuring = filmstrip.DisplayCount;
                var source = collection.DataSource ?? throw new InvalidOperationException("胶片栏缺少数据源");
                sourceCount = (int)source.GetItemsCount(collection, 0);
                // NSIndexPath 由 UIKit/Foundation 规范化：同一 (item, section) 回同一个托管包装，
                // 任何一处 Dispose（含布局内的 using）都会把这里持有的这份一起作废。
                var oldPath = NSIndexPath.FromItemSection(displayed - 1, 0);
                pathHandle = oldPath.Handle;
                pathShared = ReferenceEquals(NSIndexPath.FromItemSection(displayed - 1, 0), oldPath);
                collection.ScrollToItem(oldPath, filmstrip.Horizontal
                    ? UICollectionViewScrollPosition.Right : UICollectionViewScrollPosition.Bottom, false);
                collection.LayoutIfNeeded();
                IntPtr currentHandle = oldPath.Handle;
                if (pathHandle == IntPtr.Zero || currentHandle != pathHandle)
                    throw new InvalidOperationException(
                        $"滚动/布局作废了调用方持有的索引路径包装：handle 0x{pathHandle:x} → 0x{currentHandle:x}");
                bound = (collection.CellForItem(oldPath) as iOSThumbnailCard)?.BoundFile;
                staleFrame = filmstrip.FrameAt(displayed - 1);
                documentDuring = filmstrip.DocumentSize;
            });
            if (liveDuring != 0)
                throw new InvalidOperationException($"格式筛选未把 live 列表缩到空，无法构造陈旧索引窗口：{liveDuring}");
            if (displayDuring != displayed || sourceCount != displayed)
                throw new InvalidOperationException(
                    $"陈旧索引窗口内数据源未按显示快照回答：数据源 {sourceCount}、显示 {displayDuring}，进入窗口时 {displayed}");
            if (!ReferenceEquals(bound, lastDisplayed))
                throw new InvalidOperationException(
                    $"陈旧索引窗口内旧索引取到别的照片：{bound?.Name ?? "null"}，应为 {lastDisplayed.Name}");
            if (staleFrame is not { } frame || (double)frame.Width < 1 || (double)frame.Height < 1)
                throw new InvalidOperationException($"陈旧索引窗口内布局丢失旧索引几何：{staleFrame}");
            double documentMainBefore = filmstrip.Horizontal ? (double)documentBefore.Width : (double)documentBefore.Height;
            double documentMainDuring = filmstrip.Horizontal ? (double)documentDuring.Width : (double)documentDuring.Height;
            if (Math.Abs(documentMainDuring - documentMainBefore) > .5)
                throw new InvalidOperationException(
                    $"陈旧索引窗口内文档尺寸跟随了 live 列表：{documentMainDuring:0.##} != {documentMainBefore:0.##}");

            // reload 排在主队列里：SettleAsync 的等待让队列先跑完，再核对快照、数据源与 UIKit 条数一起归零。
            await SettleAsync(root);
            int sourceAfter = (int)(collection.DataSource?.GetItemsCount(collection, 0) ?? -1);
            if (list.FilteredFiles.Count != 0 || filmstrip.DisplayCount != 0 || sourceAfter != 0 ||
                collection.NumberOfItemsInSection(0) != 0)
                throw new InvalidOperationException(
                    $"重载后显示快照未跟随 live 列表：显示 {filmstrip.DisplayCount}、数据源 {sourceAfter}、live {list.FilteredFiles.Count}");
            if (filmstrip.FrameAt(0) != null || filmstrip.CardAt(0) != null)
                throw new InvalidOperationException("重载后仍按旧快照保留卡片或几何");
            lines.Add($"NOTE 索引路径共享：同一 (item, section) 的 FromItemSection 返回{(pathShared ? "同一托管包装" : "不同托管包装")}；滚动/布局后调用方持有的包装句柄仍为 0x{pathHandle:x}，产品路径只创建不释放");
            lines.Add($"PASS 显示快照：live 筛选列表 {displayed} → 0 的陈旧索引窗口内，数据源({sourceCount})/卡片({lastDisplayed.Name})/布局/文档尺寸仍读同一快照、旧索引不越界（调用方索引路径未被布局释放）；reload 后快照与 live 列表一起归零");
        }
        finally
        {
            // 设置仓库保存后会以新快照重建格式项，直改旧项可能被拍回；按 id 重取并重试到列表恢复。
            string[] formatIds = formats.Select(item => item.Id).ToArray();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    foreach (var live in main.Settings.FileFormats)
                    {
                        int index = Array.IndexOf(formatIds, live.Id);
                        if (index >= 0) live.IsEnabled = entryEnabled[index];
                    }
                });
                await SettleAsync(root);
                if (filmstrip.DisplayCount == list.FilteredFiles.Count && filmstrip.DisplayCount >= 12) break;
                await Task.Delay(150);
            }
        }

        await WaitAsync(() => filmstrip.DisplayCount == list.FilteredFiles.Count && filmstrip.DisplayCount >= 12,
            "恢复格式后显示快照重新对齐");
        if (filmstrip.FrameAt(filmstrip.DisplayCount - 1) == null)
            throw new InvalidOperationException($"恢复格式后末项布局缺失：{filmstrip.DisplayCount}");
        if (entryFile != null && !ReferenceEquals(main.CurrentFile, entryFile))
            await UiDispatcher.InvokeAsync(() => main.CurrentFile = entryFile);
        lines.Add($"PASS 显示快照恢复：格式恢复后快照与 live 列表同为 {filmstrip.DisplayCount} 张，末项几何可定位，当前照片保持 {entryFile?.Name ?? "无"}");
    }

    /// <summary>收尾：布局模式、栏位显隐、筛选、EXIF 显示项与相似面板还原到验收默认值。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="similarity">进入检查时的相似面板状态。</param>
    /// <param name="entryExif">进入检查时的 EXIF 显示项开关。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, bool similarity,
        IReadOnlyDictionary<string, bool> entryExif, LayoutMode entryLayout, bool entryShowNavigation)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.IsThumbnailViewVisible = true;
            main.IsControlViewVisible = true;
            main.FileVM.FilterBar.IsSimilarityPanelOpen = similarity;
            main.FileVM.FilterBar.SelectedRatingFilter = "All";
            main.Settings.LayoutMode = entryLayout;
            main.Settings.ShowNavigationButtons = entryShowNavigation;
            foreach (var item in main.Settings.ExifDisplayItems)
                if (entryExif.TryGetValue(item.PropertyName, out bool enabled) && item.IsEnabled != enabled)
                    item.IsEnabled = enabled;
        });
        await SettleAsync(root);
    }

    /// <summary>把自有副本的星级写回进入本检查时的磁盘真值（布局验收会经真实评分路径改星）。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="entryRatings">进入时按存储身份记下的星级。</param>
    private static async Task RestoreRatingsAsync(MainViewModel main, IReadOnlyDictionary<string, int> entryRatings)
    {
        foreach (var pair in entryRatings)
        {
            var candidate = main.FolderVM.AllFiles.FirstOrDefault(item => item.File.StorageId.Value == pair.Key);
            if (candidate != null && candidate.Rating != pair.Value) await main.SetRatingAsync(candidate, pair.Value);
        }
        foreach (var pair in entryRatings)
        {
            var candidate = main.FolderVM.AllFiles.FirstOrDefault(item => item.File.StorageId.Value == pair.Key);
            if (candidate != null && candidate.Rating != pair.Value)
                throw new InvalidOperationException($"自有副本 {candidate.Name} 星级还原失败：{candidate.Rating} ≠ {pair.Value}");
        }
    }

    /// <summary>取主轴偏移与视口主轴长度。</summary>
    /// <param name="filmstrip">胶片栏。</param>
    /// <param name="offset">输出：主轴偏移。</param>
    /// <param name="viewportMain">输出：视口主轴长度。</param>
    private static void MainOffset(iOSFilmstripView filmstrip, out double offset, out double viewportMain)
    {
        var position = filmstrip.ContentOffset;
        offset = filmstrip.Horizontal ? (double)position.X : (double)position.Y;
        var bounds = filmstrip.Bounds.Size;
        viewportMain = filmstrip.Horizontal ? (double)bounds.Width : (double)bounds.Height;
    }

    /// <summary>写入主轴偏移（另一轴保持不变）。</summary>
    /// <param name="filmstrip">胶片栏。</param>
    /// <param name="offset">目标主轴偏移。</param>
    private static void SetMainOffset(iOSFilmstripView filmstrip, double offset)
    {
        var current = filmstrip.ContentOffset;
        filmstrip.Collection.SetContentOffset(filmstrip.Horizontal ? new CGPoint(offset, current.Y) : new CGPoint(current.X, offset), false);
    }

    /// <summary>请求系统切换界面方向并等待生效（失败返回 false，不伪造成功）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="mask">目标方向掩码。</param>
    /// <returns>是否真正切换。</returns>
    private static async Task<bool> RotateAsync(iOSNativeRootViewController root, UIInterfaceOrientationMask mask)
    {
        var scene = root.View!.Window?.WindowScene;
        if (scene == null) return false;
        bool accepted = false;
        string failure = "未收到回调";
        scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(mask), error =>
        {
            accepted = error == null;
            failure = error?.LocalizedDescription ?? "";
        });
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);
            // 方向以真实视图尺寸判定（布局也只看尺寸）；iOS 26 起 InterfaceOrientation 已废弃。
            if (accepted && IsLandscape(root) == (mask == UIInterfaceOrientationMask.Landscape)) return true;
        }
        lastRotationFailure = $"accepted={accepted} error={failure}";
        return false;
    }

    /// <summary>最近一次旋转请求失败的原因（报告用）。</summary>
    private static string lastRotationFailure = "";

    /// <summary>当前是否横向（按真实视图尺寸判定：布局只看尺寸，且 iOS 26 起 InterfaceOrientation 已废弃）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <returns>宽度大于等于高度时为 true。</returns>
    private static bool IsLandscape(iOSNativeRootViewController root)
    {
        var bounds = root.View!.Bounds;
        return (double)bounds.Width >= (double)bounds.Height;
    }

    /// <summary>等待主屏与列表沉淀一轮布局与共享通知。</summary>
    /// <param name="root">原生根控制器。</param>
    internal static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() =>
        {
            root.View!.LayoutIfNeeded();
            root.Filmstrip.Collection.LayoutIfNeeded();
        });
        await Task.Delay(60);
    }

    /// <summary>等待目标索引的卡片被真实创建并绑定到该业务身份。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="index">目标业务索引。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <returns>已绑定的卡片。</returns>
    private static async Task<iOSThumbnailCard> WaitForCardAsync(iOSNativeRootViewController root, MainViewModel main, int index, string scope)
    {
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        if (index < 0 || index >= files.Count) throw new InvalidOperationException($"{scope} 目标索引越界：{index}/{files.Count}");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        // 几何与身份核对不依赖进入时的滚动位置：先让目标索引进入可见范围（程序化最小滚动，不播放动画）。
        if (!IsVisible(root, index))
        {
            // 共享实例：随后的 LayoutIfNeeded/CardAt 会用同一个包装，这里不得释放。
            var path = NSIndexPath.FromItemSection(index, 0);
            root.Filmstrip.Collection.ScrollToItem(path, UICollectionViewScrollPosition.None, false);
        }
        while (true)
        {
            root.Filmstrip.Collection.LayoutIfNeeded();
            if (root.Filmstrip.CardAt(index) is { } card && ReferenceEquals(card.BoundFile, files[index])) return card;
            if (DateTime.UtcNow > deadline)
            {
                var offset = root.Filmstrip.ContentOffset;
                string visible = string.Join(",", root.Filmstrip.Collection.IndexPathsForVisibleItems.Select(path => path.Item).Order());
                throw new TimeoutException($"{scope} 胶片栏卡片未创建（索引 {index}；项目 {files.Count}，偏移 {offset.X:0.##},{offset.Y:0.##}，可见 {visible}，胶片栏 {root.Filmstrip.Frame}）");
            }
            await Task.Delay(25);
        }
    }

    /// <summary>目标索引当前是否已有卡片视图（判定是否需要先滚入可见范围）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="index">业务索引。</param>
    /// <returns>是否已创建对应卡片。</returns>
    private static bool IsVisible(iOSNativeRootViewController root, int index)
        => root.Filmstrip.Collection.IndexPathsForVisibleItems.Any(path => (int)path.Item == index);

    /// <summary>把共享布局矩形转成 UIKit 矩形（验收读取合成分区用）。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit 矩形。</returns>
    private static CGRect ToFrame(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>UIKit 安全区 → 共享四边值（左、上、右、下）。</summary>
    /// <param name="insets">系统安全区或注入值。</param>
    /// <returns>共享四边值。</returns>
    private static EdgeInsets Edges(UIEdgeInsets insets)
        => new((double)insets.Left, (double)insets.Top, (double)insets.Right, (double)insets.Bottom);

    /// <summary>核对矩形整体位于安全区内。</summary>
    /// <param name="frame">待核对矩形。</param>
    /// <param name="safe">安全区。</param>
    /// <param name="bounds">屏幕尺寸。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireInside(CGRect frame, UIEdgeInsets safe, CGRect bounds, string context)
    {
        if ((double)frame.X < (double)safe.Left - Exact || (double)frame.Y < (double)safe.Top - Exact ||
            (double)frame.Right > (double)bounds.Width - (double)safe.Right + Exact || (double)frame.Bottom > (double)bounds.Height - (double)safe.Bottom + Exact)
            throw new InvalidOperationException($"{context}：{frame} / bounds {bounds} / safe(T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##})");
    }

    /// <summary>
    /// 左右布局的栏位内容纵贯窗口：上缘 0、高等于舞台高（顶部/底部安全区不推列表与右栏）。
    /// 只看纵向——右栏内容按设计铺到窗口外缘，不避让左右安全内缩。
    /// </summary>
    /// <param name="frame">栏位内容矩形（舞台坐标）。</param>
    /// <param name="stage">舞台尺寸。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireColumnSpan(CGRect frame, CGRect stage, string context)
    {
        if (Math.Abs((double)frame.Y) > Exact || Math.Abs((double)frame.Height - (double)stage.Height) > Exact)
            throw new InvalidOperationException(
                $"{context} 未纵贯窗口：{frame} / 舞台 {stage}（顶部/底部安全区不应推栏位内容）");
    }

    /// <summary>视图所在舞台子视图的层级序号（不在舞台内为 -1）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="view">目标视图。</param>
    /// <returns>舞台直接子视图的序号。</returns>
    private static int StageIndexOf(iOSNativeRootViewController root, UIView view)
    {
        UIView? current = view;
        while (current != null && !ReferenceEquals(current.Superview, root.Stage)) current = current.Superview;
        return current == null ? -1 : Array.IndexOf(root.Stage.Subviews, current);
    }

    /// <summary>隐藏栏位后核对主图主轴变长（隐藏起始栏时归零原点，隐藏末端栏时保持原点）。</summary>
    /// <param name="actual">当前主图分区。</param>
    /// <param name="before">隐藏前的主图分区。</param>
    /// <param name="released">应释放的主轴长度。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="context">失败信息。</param>
    /// <param name="leading">该栏是否位于主图之前（行布局为上、列布局为左）。</param>
    /// <summary>按允许误差比较原生 frame，失效时报告实际与预期。</summary>
    /// <param name="actual">实际矩形。</param>
    /// <param name="expected">预期矩形。</param>
    /// <param name="context">失败信息。</param>
    /// <param name="tolerance">允许误差。</param>
    private static void RequireFrame(CGRect actual, CGRect expected, string context, double tolerance = Exact)
    {
        if (Math.Abs((double)(actual.X - expected.X)) > tolerance || Math.Abs((double)(actual.Y - expected.Y)) > tolerance ||
            Math.Abs((double)(actual.Width - expected.Width)) > tolerance || Math.Abs((double)(actual.Height - expected.Height)) > tolerance)
            throw new InvalidOperationException($"{context}：实际 {actual}，预期 {expected}");
    }

    /// <summary>核对视图颜色与设计令牌一致（按 CGColor 分量比较，不依赖动态色解析）。</summary>
    /// <param name="actual">实际颜色（未设置时为 null，按不一致处理）。</param>
    /// <param name="token">0xAARRGGBB 令牌。</param>
    /// <param name="context">失败信息。</param>
    private static void RequireColor(UIColor? actual, uint token, string context)
    {
        var expected = iOSColors.FromToken(token);
        var left = actual?.CGColor.Components ?? [];
        var right = expected.CGColor.Components ?? [];
        if (actual == null || left.Length != right.Length || left.Zip(right).Any(pair => Math.Abs((double)(pair.First - pair.Second)) > .01))
            throw new InvalidOperationException($"{context}：实际 {actual?.ToString() ?? "null"}（{left.Length} 分量），预期令牌 0x{token:X8}（{right.Length} 分量）");
    }

    /// <summary>轮询条件并设有限超时，失败即抛出不输出虚假通过。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="name">条件名称（超时信息用）。</param>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    /// <summary>两个矩形是否相交（CGRect 绑定不提供 IntersectsWith，这里按区间判交）。</summary>
    /// <param name="left">矩形一。</param>
    /// <param name="right">矩形二。</param>
    /// <returns>是否相交。</returns>
    private static bool Overlaps(CGRect left, CGRect right)
        => (double)left.X < (double)right.X + (double)right.Width && (double)right.X < (double)left.X + (double)left.Width &&
           (double)left.Y < (double)right.Y + (double)right.Height && (double)right.Y < (double)left.Y + (double)left.Height;

    /// <summary>格式化尺寸与安全区，便于报告复核分数点。</summary>
    /// <param name="size">尺寸。</param>
    /// <returns>可读文本。</returns>
    private static string Size(CGSize size) => $"{size.Width:0.##}×{size.Height:0.##}";

    /// <summary>格式化矩形尺寸。</summary>
    /// <param name="rect">矩形。</param>
    /// <returns>可读文本。</returns>
    private static string Size(CGRect rect) => $"{rect.Width:0.##}×{rect.Height:0.##}";

    /// <summary>格式化共享布局矩形尺寸。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>可读文本。</returns>
    private static string Size(LayoutRect rect) => $"{rect.Width:0.##}×{rect.Height:0.##}";

    /// <summary>格式化安全区。</summary>
    /// <param name="view">根视图。</param>
    /// <returns>可读文本。</returns>
    private static string Insets(UIView view)
    {
        var safe = view.SafeAreaInsets;
        return $"T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##}";
    }
}
