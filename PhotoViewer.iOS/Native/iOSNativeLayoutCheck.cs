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
/// 浮层外壳的分区（主图满舞台、分区表达为适应区内边距、栏位背景铺满边缘、内容收在安全区内）、
/// 栏位显隐把对应一侧内边距退回安全区并释放适应区长度、胶片栏两档卡片几何（选档 / 带厚 / 可见容量 ≥5 /
/// 60×72 与 96×144 内部）、宽形态筛选控件（三合体与单胶囊）与控制栏（EXIF 块 / 星级控件 / 44 点圆按钮），
/// 以及 [FILM-01–06] 的交互语义（点选不居中、显式定位的中间帧与终点、用户接管、减少动态效果直接到终点、
/// 点星写回自有副本且不切主图、改星后筛选暂留）、卡片输入分流（指针点星写值 / 触摸只选中）、
/// 卡片长按与辅助点按的星级弹窗（方向、落位、原地抬手保持、滑动提交一次、越界不写），
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
            // 本检查会经真实评分路径改写自有副本：先记下进入时的星级，收尾一律按磁盘真值还原。
            var entryRatings = main.FolderVM.AllFiles.ToDictionary(
                file => file.File.StorageId.Value, file => Math.Max(0, file.Rating), StringComparer.Ordinal);
            lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），主屏安全区 insets={Insets(root.View!)}");

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

            // 二、胶片栏交互（[FILM-01–03]）
            await VerifyFilmstripAsync(root, main, lines, session);

            // 三、卡片星级写回与筛选连续性（[FILM-05/06]）
            await VerifyRatingAsync(root, main, lines, directory);

            // 五、胶片栏显示快照：重载前的陈旧索引不得读到缩短后的 live 筛选列表
            await VerifyDisplaySnapshotAsync(root, main, lines);

            await RestoreAsync(root, main, entrySimilarity, entryExif);
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
        var safe = root.View.SafeAreaInsets;
        // 用真实尺寸与真实安全区把主屏转 90°：两向共用同一份布局数学，故翻转后的口径可直接核对。
        (bool Row, double Width, double Height)[] cases =
            [(true, (double)bounds.Width, (double)bounds.Height), (false, (double)bounds.Height, (double)bounds.Width)];
        foreach (var scenario in cases)
        {
            double band = FilmCardGeometry.BandThickness(root.Filmstrip.Tier, scenario.Row);
            double fileMain = scenario.Row
                ? root.FilterBar.MainLength(true, (double)safe.Top + MainScreenLayout.FilterMargin) + band + MainScreenLayout.Divider
                : band + MainScreenLayout.Divider + (double)safe.Left;
            double controlMain = MainScreenLayout.ControlBarMainLength(scenario.Row ? 60 : 244);
            var panes = iOSMainScreenLayout.Compute(scenario.Row, scenario.Width, scenario.Height, safe, fileMain, controlMain, 0);
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
            lines.Add($"NOTE 翻转尺寸核对 {scenario.Width:0}×{scenario.Height:0}（{(scenario.Row ? "上下" : "左右")}）：主图 {Size(panes.Photo)}，栏位背景铺满边缘、分区首尾相接（真实视图当前为 {Size(bounds)}，安全区实际的 T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##}）");
        }
    }

    /// <summary>
    /// 核对浮层外壳的分区（SHELL-11）：主图分区与主图视图恒为整个舞台，分区矩形改为表达主图<b>适应区</b>的四边内边距；
    /// 文件栏背景、两条分隔线、控制栏背景/滚动内容与分析栏背景逐项等于 <see cref="iOSMainScreenLayout"/> 的结果，
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
        var safe = view.SafeAreaInsets;
        var panes = root.Panes;
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

        // 栏位背景、分隔线与滚动容器逐项等于共享分区结果。
        RequireFrame(root.FileBarView.Frame, ToFrame(panes.FileBar), $"{scope} 文件栏背景与分区不一致");
        RequireFrame(root.FileDividerView.Frame, ToFrame(panes.FileDivider), $"{scope} 文件栏分隔线与分区不一致");
        RequireFrame(root.ControlBackgroundView.Frame, ToFrame(panes.ControlBackground), $"{scope} 控制栏背景与分区不一致");
        RequireFrame(root.ControlDividerView.Frame, ToFrame(panes.ControlDivider), $"{scope} 控制栏分隔线与分区不一致");
        RequireFrame(root.ControlScroll.Frame, ToFrame(panes.ControlContent), $"{scope} 控制栏滚动容器与内容区不一致");
        RequireFrame(root.AnalysisBarBackground.Frame, ToFrame(panes.Analysis), $"{scope} 分析栏背景未随隐藏槽位归零");

        // 栏位背景铺到屏幕边缘（iOSMainScreenLayout 的安全区口径：行布局补回上下、列布局补回左右）。
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
                || Math.Abs((double)controlBackground.X - (double)controlDivider.X - MainScreenLayout.Divider) > Exact)
                throw new InvalidOperationException($"{scope} 控制栏背景未从分隔线铺到屏幕右边：{controlBackground}");
            if (Math.Abs((double)fileDivider.Height - (double)stage.Height) > Exact
                || Math.Abs(fileDividerMaxX - (double)panes.Photo.X) > Exact
                || Math.Abs((double)controlDivider.X - (double)panes.Photo.MaxX) > Exact
                || Math.Abs(fileBarMaxX + MainScreenLayout.Divider - (double)panes.Photo.X) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 分隔线未收在主图分区两侧：文件 {fileDivider} / 控制 {controlDivider} / 主图 {panes.Photo}");
        }
        // 内容收在安全区内。
        RequireInside(root.FilterBar.Frame, safe, stage, $"{scope} 筛选条内容越出安全区");
        RequireInside(root.Filmstrip.Frame, safe, stage, $"{scope} 胶片栏内容越出安全区");
        RequireInside(root.ControlScroll.Frame, safe, stage, $"{scope} 控制栏内容越出安全区");
        // 文件栏主轴长度 = 筛选控件主轴 + 胶片卡片带厚 + 分隔线（行布局）；
        // 列布局 = 带厚 + 分隔线 + 安全区前导（相似面板打开时才另加柱宽，本检查保持关闭）。
        double leadingInset = (double)safe.Top + MainScreenLayout.FilterMargin;
        double filterMain = root.FilterBar.MainLength(row, leadingInset);
        double band = FilmCardGeometry.BandThickness(root.Filmstrip.Tier, row);
        double expectedFileMain = row
            ? filterMain + band + MainScreenLayout.Divider
            : band + MainScreenLayout.Divider + (double)safe.Left;
        double actualFileMain = row
            ? (double)panes.FileBar.Height - (double)safe.Top + MainScreenLayout.Divider
            : (double)panes.FileBar.Width + MainScreenLayout.Divider;
        if (Math.Abs(actualFileMain - expectedFileMain) > .5)
            throw new InvalidOperationException(
                $"{scope} 文件栏主轴 {actualFileMain:0.##} ≠ 筛选 {filterMain:0.##} + 带厚 {band:0.##}（{root.Filmstrip.Tier}）+ 分隔线/安全区 {expectedFileMain:0.##}");
        // 筛选条 + 胶片栏恰好铺满文件栏内容区。
        RequireFrame(root.FilterBar.Frame,
            ToFrame(new LayoutRect(panes.FileContent.X, panes.FileContent.Y, panes.FileContent.Width, filterMain)),
            $"{scope} 筛选条未落在文件栏内容区");
        if (row)
        {
            double contentEnd = (double)panes.FileContent.MaxY;
            double filmEnd = (double)root.Filmstrip.Frame.Y + (double)root.Filmstrip.Frame.Height;
            if (Math.Abs(filmEnd - contentEnd) > .5)
                throw new InvalidOperationException($"{scope} 胶片栏未填满文件栏剩余空间：{filmEnd} vs {contentEnd}");
        }
        else if (Math.Abs((double)root.Filmstrip.Frame.Width - band) > .5)
        {
            throw new InvalidOperationException($"{scope} 列布局胶片栏交叉轴 {root.Filmstrip.Frame.Width:0.##} 不是带厚 {band:0.##}");
        }
        // 适应区与任何可见栏位不相交（照片只在无遮挡区里适应）。
        var fit = root.Photo.FitRect;
        foreach (var (name, bar) in new (string, UIView)[]
                 {
                     ("文件栏背景", root.FileBarView), ("控制栏背景", root.ControlBackgroundView),
                     ("分析栏背景", root.AnalysisBarBackground),
                 })
        {
            if (bar.Hidden || (double)bar.Frame.Width <= 0 || (double)bar.Frame.Height <= 0) continue;
            if (Overlaps(fit, bar.Frame))
                throw new InvalidOperationException($"{scope} 适应区 {fit} 与可见{name} {bar.Frame} 相交");
        }
        lines.Add(
            $"PASS {scope} 分区：舞台 {Size(stage)} safe(T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##}) " +
            $"适应区 {Size(fit)} @({fit.X:0.##},{fit.Y:0.##})、文件栏主轴 {actualFileMain:0.##}（筛选 {filterMain:0.##} + 带厚 {band:0.##} + 分隔线/安全区）、" +
            $"主图分区与主图视图满舞台、栏位背景/分隔线与分区逐项一致、背景铺满边缘、内容在安全区内、适应区不与可见栏位相交");
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
    /// 核对宽形态筛选控件（FILTER-07）：各矩形逐项等于共享 <see cref="WideFilterLayout.Compute"/> 按原生自然宽度
    /// 算出的结果；行布局必须是三段合体、在文件栏内容区内整组居中且 AI 版开关隔组间距紧随其后，
    /// 列布局必须是单胶囊（宽等于胶片卡片列宽、在带内居中）且开关在下方同宽。
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
        double leadingInset = (double)root.View!.SafeAreaInsets.Top + MainScreenLayout.FilterMargin;
        var toggle = bar.SimilarityToggle;
        double toggleWidth = toggle == null ? 0 : NaturalWidth(toggle);
        var expected = WideFilterLayout.Compute(row, contentWidth, cardWidth, leadingInset,
            NaturalWidth(bar.SortSegment), NaturalWidth(bar.OrderSegment), NaturalWidth(bar.RatingSegment),
            NaturalWidth(bar.CapsuleButton), toggle != null, toggleWidth);
        if (bar.Mode != expected.Mode)
            throw new InvalidOperationException($"{scope} 筛选形态 {bar.Mode} ≠ 共享计算 {expected.Mode}");
        if (Math.Abs(bar.MainLength(row, leadingInset) - expected.MainLength) > Exact)
            throw new InvalidOperationException(
                $"{scope} 筛选控件主轴长度 {bar.MainLength(row, leadingInset):0.##} ≠ 共享计算 {expected.MainLength:0.##}");
        if (Math.Abs((double)bar.Frame.Height - expected.MainLength) > Exact)
            throw new InvalidOperationException($"{scope} 筛选控件高度 {bar.Frame.Height:0.##} ≠ 主轴长度 {expected.MainLength:0.##}");
        RequireFrame(ToFrame(bar.Frames.Control), ToFrame(expected.Control), $"{scope} 筛选控件整体矩形与共享计算不一致");
        RequireFrame(ToFrame(bar.Frames.Sort), ToFrame(expected.Sort), $"{scope} 排序段矩形与共享计算不一致");
        RequireFrame(ToFrame(bar.Frames.Order), ToFrame(expected.Order), $"{scope} 方向段矩形与共享计算不一致");
        RequireFrame(ToFrame(bar.Frames.Rating), ToFrame(expected.Rating), $"{scope} 星级段矩形与共享计算不一致");
        bool segmented = expected.Mode == FilterControlMode.Segmented;
        if (bar.SegmentedContainer.Hidden == segmented)
            throw new InvalidOperationException($"{scope} 三段容器可见性不符：Hidden={bar.SegmentedContainer.Hidden}，形态={expected.Mode}");
        if (bar.CapsuleButton.Hidden != segmented)
            throw new InvalidOperationException($"{scope} 单胶囊可见性不符：Hidden={bar.CapsuleButton.Hidden}，形态={expected.Mode}");
        if (segmented)
        {
            if (bar.SegmentedContainer.ContentView.Subviews.OfType<UIButton>().Count() != 3)
                throw new InvalidOperationException($"{scope} 三段容器内不是恰好三个段按钮");
            RequireFrame(bar.SegmentedContainer.Frame, ToFrame(expected.Control), $"{scope} 三段容器未铺满控件矩形");
            RequireFrame(bar.SortSegment.Frame, ToFrame(expected.Sort), $"{scope} 排序段原生 frame 与共享矩形不一致");
            RequireFrame(bar.OrderSegment.Frame, ToFrame(expected.Order), $"{scope} 方向段原生 frame 与共享矩形不一致");
            RequireFrame(bar.RatingSegment.Frame, ToFrame(expected.Rating), $"{scope} 星级段原生 frame 与共享矩形不一致");
        }
        else
        {
            RequireFrame(bar.CapsuleButton.Frame, ToFrame(expected.Control), $"{scope} 单胶囊未铺满控件矩形");
        }
        if (toggle != null)
        {
            if (toggle.Hidden) throw new InvalidOperationException($"{scope} AI 版相似聚类开关必须显示");
            RequireFrame(ToFrame(bar.Frames.Toggle), ToFrame(expected.Toggle), $"{scope} 相似聚类开关矩形与共享计算不一致");
            RequireFrame(toggle.Frame, ToFrame(expected.Toggle), $"{scope} 相似聚类开关原生 frame 与共享矩形不一致");
        }
        else if (bar.Frames.Toggle.Width > 0 || bar.Frames.Toggle.Height > 0)
        {
            throw new InvalidOperationException($"{scope} 标准版不应为相似聚类开关保留矩形：{bar.Frames.Toggle}");
        }
        if (row)
        {
            if (!segmented)
                throw new InvalidOperationException($"{scope} 行布局三段放不下却退回单胶囊：内容宽 {contentWidth:0.##}");
            if (Math.Abs((double)expected.Control.Y - WideFilterLayout.Margin) > Exact
                || Math.Abs((double)expected.Control.Height - WideFilterLayout.Height) > Exact)
                throw new InvalidOperationException($"{scope} 行布局筛选控件未保持上下外边距与 44 点高：{expected.Control}");
            double groupWidth = (double)expected.Control.Width + (toggle == null ? 0 : WideFilterLayout.Gap + toggleWidth);
            double groupCenter = (double)expected.Control.X + groupWidth / 2;
            if (Math.Abs(groupCenter - contentWidth / 2) > Exact)
                throw new InvalidOperationException($"{scope} 筛选控件未在文件栏内容区整组居中：中心 {groupCenter:0.##} / {contentWidth / 2:0.##}");
            if (toggle != null
                && Math.Abs((double)expected.Toggle.X - (double)expected.Control.MaxX - WideFilterLayout.Gap) > Exact)
                throw new InvalidOperationException(
                    $"{scope} 相似聚类开关未隔 {WideFilterLayout.Gap:0.##} 点紧随三段：{expected.Toggle}/{expected.Control}");
        }
        else
        {
            if (segmented)
                throw new InvalidOperationException($"{scope} 列布局三段竟然放进了卡片列宽：{expected.Control}");
            if (Math.Abs(contentWidth - FilmCardGeometry.BandThickness(root.Filmstrip.Tier, false)) > Exact)
                throw new InvalidOperationException($"{scope} 列布局筛选控件宽度不是胶片卡片带厚：{contentWidth:0.##}");
            if (Math.Abs((double)expected.Control.Width - cardWidth) > Exact
                || Math.Abs((double)expected.Control.X - (contentWidth - cardWidth) / 2) > Exact)
                throw new InvalidOperationException($"{scope} 单胶囊未按卡片列宽在带内居中：{expected.Control}");
            if (toggle != null
                && (Math.Abs((double)toggle.Frame.Width - cardWidth) > Exact
                    || Math.Abs((double)toggle.Frame.Y - (double)expected.Control.MaxY - WideFilterLayout.Gap) > Exact))
                throw new InvalidOperationException($"{scope} 相似聚类开关未在单胶囊下方同宽：{toggle.Frame}");
            if (Math.Abs((double)expected.Control.Y - leadingInset) > Exact)
                throw new InvalidOperationException($"{scope} 单胶囊未从前导让位起排：Y={expected.Control.Y:0.##}/{leadingInset:0.##}");
        }
        lines.Add(row
            ? $"PASS {scope} 筛选控件：三段合计 {expected.Control.Width:0.##} 点（排序 {expected.Sort.Width:0.##} / 方向 {expected.Order.Width:0.##} / 星级 {expected.Rating.Width:0.##}）在 {contentWidth:0.##} 内容区整组居中，" +
                $"{(toggle == null ? "标准版无相似聚类开关" : $"AI 版开关 {toggleWidth:0.##} 点隔 {WideFilterLayout.Gap:0.##} 紧随其后")}；行高 {expected.MainLength:0.##}（无计数、无空槽）"
            : $"PASS {scope} 筛选控件：单胶囊 {cardWidth:0.##} 点在 {contentWidth:0.##} 带内居中（Y={expected.Control.Y:0.##}），" +
                $"{(toggle == null ? "标准版无相似聚类开关" : $"AI 版开关在下方隔 {WideFilterLayout.Gap:0.##} 同宽")}；列主轴 {expected.MainLength:0.##}");
    }

    /// <summary>原生量得的自然宽度（与控件同一口径：SizeThatFits 向上取整，含 configuration 内边距与弹出指示器）。</summary>
    /// <param name="button">目标按钮。</param>
    /// <returns>宽度，至少 1 点。</returns>
    private static double NaturalWidth(UIButton button)
    {
        double width = Math.Ceiling((double)button.SizeThatFits(new CGSize(0, (nfloat)WideFilterLayout.Height)).Width);
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
        if (commandCount > 0 && (commandIndex >= bar.Groups.Count || bar.Groups[commandIndex].Subviews.Length != commandCount))
            throw new InvalidOperationException(
                $"{scope} 命令分组缺失或子项数量不符：{commandIndex}/{bar.Groups.Count}、{bar.Groups.ElementAtOrDefault(commandIndex)?.Subviews.Length}/{commandCount}");
        foreach (var group in bar.Groups)
        {
            var frame = group.Frame;
            if ((double)frame.X < ToolbarFlowLayout.Padding - Exact || (double)frame.Y < ToolbarFlowLayout.Padding - Exact ||
                (double)frame.X + (double)frame.Width > (double)bounds.Width - ToolbarFlowLayout.Padding + Exact ||
                (double)frame.Y + (double)frame.Height > (double)bounds.Height - ToolbarFlowLayout.Padding + Exact)
                throw new InvalidOperationException($"{scope} 控制组未保留 10 点外边距：{frame}/{bounds}");
            foreach (var other in bar.Groups.Where(other => !ReferenceEquals(other, group)))
                if (Overlaps(frame, other.Frame)) throw new InvalidOperationException($"{scope} 控制组重叠：{frame} / {other.Frame}");
        }
        string exifSummary = "无 EXIF 分组";
        if (exifCount > 0)
        {
            // EXIF 组就是新 EXIF 块本身：整块尺寸取共享尺寸，块内逐项落在共享项目矩形内、标签在数值上方。
            var exif = bar.ExifGroup!;
            exif.LayoutIfNeeded();
            var spec = ToolbarFlowLayout.Exif(exifCount, row);
            double frameMain = row ? (double)exif.Frame.Width : (double)exif.Frame.Height;
            double frameCross = row ? (double)exif.Frame.Height : (double)exif.Frame.Width;
            if (Math.Abs(frameMain - spec.Main) > Exact || Math.Abs(frameCross - spec.Cross) > Exact)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块 {frameMain:0.##}×{frameCross:0.##} 与共享尺寸 {spec.Main:0.##}×{spec.Cross:0.##} 不一致");
            if (exif.Horizontal != row)
                throw new InvalidOperationException($"{scope} EXIF 块排向 {exif.Horizontal} 未随布局方向 {row}");
            if (exif.ItemLabels.Count != exifCount || exif.ItemValues.Count != exifCount)
                throw new InvalidOperationException(
                    $"{scope} EXIF 块项数 {exif.ItemLabels.Count}/{exif.ItemValues.Count} ≠ 启用项 {exifCount}");
            for (int index = 0; index < exifCount; index++)
            {
                var label = exif.ItemLabels[index];
                var value = exif.ItemValues[index];
                var item = ExifBlockGeometry.ItemRect(index, exifCount, row, row ? (double)exif.Bounds.Width : (double)exif.Bounds.Height);
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
            if (exif.Background.Effect is not UIBlurEffect)
                throw new InvalidOperationException($"{scope} EXIF 块缺少模糊材质衬底");
            exifSummary = $"{frameMain:0.##}×{frameCross:0.##}（{exifCount} 项{(row ? "横排" : "竖排")}）";
        }
        // 星级分组：共享尺寸占位，控件在组内铺满，胶囊按共享几何落在展开/紧凑形态。
        double availableMain = row ? (double)root.ControlScroll.Bounds.Width : (double)root.ControlScroll.Bounds.Height;
        var ratingSize = ToolbarFlowLayout.Rating(Math.Max(1, availableMain - ToolbarFlowLayout.Padding * 2));
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
            if (control.Row.Frame != control.Capsule.Frame)
                throw new InvalidOperationException($"{scope} 展开态星排未铺满胶囊：{control.Row.Frame}/{control.Capsule.Frame}");
            ratingSummary = $"{form} 胶囊 {(int)capsuleWidth}×{(int)capsuleHeight}";
        }
        // 命令圆按钮：44×44，沿命令组主轴按共享偏移排布。
        foreach (var command in bar.Commands.Values)
            RequireFrame(command.Frame, new CGRect(command.Frame.X, command.Frame.Y, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize),
                $"{scope} 命令按钮未保持 44×44");
        if (commandCount > 0)
        {
            var group = bar.Groups[commandIndex];
            for (int index = 0; index < commandCount; index++)
            {
                double expected = ToolbarFlowLayout.CommandOffset(index);
                var frame = group.Subviews[index].Frame;
                double actual = row ? (double)frame.X : (double)frame.Y;
                if (Math.Abs(actual - expected) > Exact)
                    throw new InvalidOperationException($"{scope} 命令按钮 {index} 偏移 {actual:0.##} 与共享偏移 {expected:0.##} 不一致");
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
    /// 核对卡片两档几何（[FILM-05]）：胶片条主轴长度按共享选档规则决定档位、带厚等于共享
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
        double filmMain = horizontal ? (double)viewport.Width : (double)viewport.Height;
        double filmCross = horizontal ? (double)viewport.Height : (double)viewport.Width;
        var expectedTier = FilmCardGeometry.ResolveTier(filmMain, horizontal);
        if (tier != expectedTier)
            throw new InvalidOperationException(
                $"{scope} 卡片档位 {tier} 与共享选档 {expectedTier} 不一致（胶片条主轴 {filmMain:0.##}）");
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
        if (Math.Abs(documentMain - Math.Max(filmMain, expected)) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏文档主轴长度错误：{documentMain}");
        var first = filmstrip.FrameAt(0) ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
        // 共享卡片矩形的跨轴基准：横向取文档主轴长度，纵向取视口宽度。
        double sharedContentWidth = horizontal ? documentMain : (double)viewport.Width;
        var expectedFirst = FilmCardGeometry.ItemFrame(tier, horizontal, 0, count, sharedContentWidth);
        RequireFrame(first, new CGRect(expectedFirst.X, expectedFirst.Y, expectedFirst.Width, expectedFirst.Height),
            $"{scope} 首卡位置与卡片尺寸（{tier}）");
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
    /// 隐藏后该侧内边距退回 <see cref="iOSMainScreenLayout"/> 编码的安全区值（行布局上下、列布局左右为 0），
    /// 适应区主轴长度必须恰好增长同等长度；恢复后内边距与适应区完全还原。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyPaneReleaseAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var safe = root.View!.SafeAreaInsets;
        var stage = root.Stage.Bounds;
        var before = root.Panes;
        var beforeInsets = root.Photo.FitInsets;
        var beforeFit = root.Photo.FitRect;
        double beforeMain = row ? (double)beforeFit.Height : (double)beforeFit.Width;
        double beforeLeading = row ? (double)beforeInsets.Top : (double)beforeInsets.Left;
        // 隐藏后该侧内边距退回 iOSMainScreenLayout 的安全区口径：行布局保留顶边安全区，列布局为 0。
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
        // 隐藏控制栏后该侧内边距同样退回安全区口径：行布局保留底边安全区，列布局为 0。
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
        var next = root.ControlBar.Commands["Next"];
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
        var lastNext = root.ControlBar.Commands["Next"];
        await UiDispatcher.InvokeAsync(() => lastNext.SendActionForControlEvents(UIControlEvent.TouchUpInside));
        await SettleAsync(root);
        if (!ReferenceEquals(main.CurrentFile, files[^1])) throw new InvalidOperationException("末张再下一张发生了环绕");
        await UiDispatcher.InvokeAsync(() => main.FileVM.ThumbnailList.SelectImageCommand(files[0]));
        var firstPrevious = root.ControlBar.Commands["Previous"];
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
        var takeoverTrigger = root.ControlBar.Commands["Next"];
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
            var reduceNext = root.ControlBar.Commands["Next"];
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
        if (expectedDirection != (row ? RatingPopupDirection.Down : RatingPopupDirection.Right))
            throw new InvalidOperationException(
                $"{scope} 卡片弹窗方向表 {(row ? "行" : "列")} 布局给出 {expectedDirection}（期望 {(row ? "Down" : "Right")}）");
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
        IReadOnlyDictionary<string, bool> entryExif)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.IsThumbnailViewVisible = true;
            main.IsControlViewVisible = true;
            main.FileVM.FilterBar.IsSimilarityPanelOpen = similarity;
            main.FileVM.FilterBar.SelectedRatingFilter = "All";
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
