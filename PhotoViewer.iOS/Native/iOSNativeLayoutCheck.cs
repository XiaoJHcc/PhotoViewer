using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.Diagnostics;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I3 验收专用（非产品行为，测试钩子）：`--pv-native-layout-check &lt;目录&gt;` 在模拟器/真机上程序化核对 iPad 主屏——
/// 两向/自动布局与安全区分区、栏位显隐释放空间、胶片栏卡片几何（96×144 / 80 方形）、筛选条与计数几何、
/// 控制栏三分组换行居中与评分方向，以及 [FILM-01–06] 的交互语义（点选不居中、显式定位的中间帧与终点、
/// 用户接管、减少动态效果直接到终点、点星写回自有副本且不切主图、改星后筛选暂留）。
/// 素材要求：目录内 ≥12 张自有夹具（release/native-p2-fixtures/*.jpg 的容器内副本，含 XMP 可写评级），
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
                root.BuildControls();
            });
            await main.FolderVM.OpenFolderAsync(new iOSFolderRef(directory));
            await WaitAsync(() => main.FolderVM.AllFiles.Count >= 12, "夹具目录枚举");
            await WaitAsync(() => main.CurrentFile != null, "首图选中");
            await SettleAsync(root);
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

            await RestoreAsync(root, main, entrySimilarity);
            lines.Add($"PASS 收尾还原：布局模式/栏位显隐/筛选/评级已回到进入本检查时的状态");
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS layout ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
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
            double fileMain = MainScreenLayout.FileBarMainLength(scenario.Row,
                MainScreenLayout.FilterBarMainLength(scenario.Row, (double)safe.Top + MainScreenLayout.FilterMargin), scenario.Row ? 0 : (double)safe.Left);
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

    /// <summary>核对分区拼合：栏位背景铺到屏幕边缘、内容在安全区内、分区首尾相接互不重叠、主图吸收剩余空间。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyPartition(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var view = root.View!;
        var bounds = view.Bounds;
        var safe = view.SafeAreaInsets;
        var panes = root.Panes;
        // 分析栏默认隐藏：本套件不打开它，槽位必须被压成零并把空间让给主图（打开态的几何由 --pv-native-analysis-check 核对）。
        double analysisBand = row ? (double)panes.Analysis.Height : (double)panes.Analysis.Width;
        if (analysisBand > Exact)
            throw new InvalidOperationException($"{scope} 分析栏隐藏时仍占用了空间：{panes.Analysis}");
        double mainLength = row ? (double)bounds.Height : (double)bounds.Width;
        double contentWidth = (double)bounds.Width - (double)safe.Left - (double)safe.Right;
        double contentHeight = (double)bounds.Height - (double)safe.Top - (double)safe.Bottom;

        // 背景铺满屏幕边缘
        var fileBar = root.FileBarView.Frame;
        var controlBackground = ToFrame(panes.ControlBackground);
        if (row)
        {
            if (Math.Abs((double)fileBar.X) > Exact || Math.Abs((double)fileBar.Y) > Exact || Math.Abs((double)fileBar.Width - (double)bounds.Width) > Exact)
                throw new InvalidOperationException($"{scope} 文件栏背景未铺满屏幕顶边/两侧：{fileBar}");
            if (Math.Abs((double)controlBackground.Bottom - (double)bounds.Height) > Exact || Math.Abs((double)controlBackground.Width - (double)bounds.Width) > Exact)
                throw new InvalidOperationException($"{scope} 控制栏背景未铺到屏幕底边：{controlBackground}");
        }
        else
        {
            if (Math.Abs((double)fileBar.X) > Exact || Math.Abs((double)fileBar.Y) > Exact || Math.Abs((double)fileBar.Height - (double)bounds.Height) > Exact)
                throw new InvalidOperationException($"{scope} 文件栏背景未铺满屏幕左边/上下边：{fileBar}");
            if (Math.Abs((double)controlBackground.Right - (double)bounds.Width) > Exact)
                throw new InvalidOperationException($"{scope} 控制栏背景未铺到屏幕右边：{controlBackground}");
        }
        // 内容在安全区内
        RequireInside(root.FilterBar.Frame, safe, bounds, $"{scope} 筛选条内容越出安全区");
        RequireInside(root.Filmstrip.Frame, safe, bounds, $"{scope} 胶片栏内容越出安全区");
        RequireInside(root.ControlScroll.Frame, safe, bounds, $"{scope} 控制栏内容越出安全区");
        // 分区首尾相接：文件栏 → 主图 → 分析栏(0) → 控制栏
        double fileEnd = row ? (double)root.FileDividerView.Frame.Y + (double)root.FileDividerView.Frame.Height : (double)root.FileDividerView.Frame.X + (double)root.FileDividerView.Frame.Width;
        double photoStart = row ? (double)root.PhotoPane.Frame.Y : (double)root.PhotoPane.Frame.X;
        double photoEnd = row ? photoStart + (double)root.PhotoPane.Frame.Height : photoStart + (double)root.PhotoPane.Frame.Width;
        double controlStart = row ? (double)root.ControlDividerView.Frame.Y : (double)root.ControlDividerView.Frame.X;
        if (Math.Abs(fileEnd - photoStart) > Exact) throw new InvalidOperationException($"{scope} 文件栏与主图之间有空带：{fileEnd} vs {photoStart}");
        if (Math.Abs(photoEnd - controlStart) > Exact) throw new InvalidOperationException($"{scope} 主图与控制栏之间有空带：{photoEnd} vs {controlStart}");
        // 分隔线铺满交叉轴（栏位背景与照片之间的一条 1 点线）
        var divider = root.ControlDividerView.Frame;
        bool dividerOk = row
            ? Math.Abs((double)divider.Height - MainScreenLayout.Divider) <= Exact && Math.Abs((double)divider.Width - (double)bounds.Width) <= Exact
            : Math.Abs((double)divider.Width - MainScreenLayout.Divider) <= Exact && Math.Abs((double)divider.Height - (double)bounds.Height) <= Exact;
        if (!dividerOk) throw new InvalidOperationException($"{scope} 控制栏分隔线未铺满交叉轴：{divider}");
        // 主图铺满另一轴（观片满幅，跨安全区延伸到屏幕边缘），主干轴严格夹在两栏之间
        if (row)
        {
            if (Math.Abs((double)root.PhotoPane.Frame.X) > Exact || Math.Abs((double)root.PhotoPane.Frame.Width - (double)bounds.Width) > Exact)
                throw new InvalidOperationException($"{scope} 主图未铺满横向：{root.PhotoPane.Frame}");
            if ((double)root.PhotoPane.Frame.Y < (double)safe.Top - Exact || (double)root.PhotoPane.Frame.Y + (double)root.PhotoPane.Frame.Height > (double)bounds.Height - (double)safe.Bottom + Exact)
                throw new InvalidOperationException($"{scope} 主图主干轴越出安全区：{root.PhotoPane.Frame}");
        }
        else
        {
            if (Math.Abs((double)root.PhotoPane.Frame.Y) > Exact || Math.Abs((double)root.PhotoPane.Frame.Height - (double)bounds.Height) > Exact)
                throw new InvalidOperationException($"{scope} 主图未铺满纵向：{root.PhotoPane.Frame}");
            if ((double)root.PhotoPane.Frame.X < (double)safe.Left - Exact || (double)root.PhotoPane.Frame.X + (double)root.PhotoPane.Frame.Width > (double)bounds.Width - (double)safe.Right + Exact)
                throw new InvalidOperationException($"{scope} 主图主干轴越出安全区：{root.PhotoPane.Frame}");
        }
        RequireFrame(root.Photo.Frame, root.PhotoPane.Bounds, $"{scope} 主图表面未铺满主图分区");
        // 筛选条 + 胶片栏恰好铺满文件栏内容区
        RequireFrame(root.FilterBar.Frame, ToFrame(new LayoutRect(panes.FileContent.X, panes.FileContent.Y, panes.FileContent.Width, FilterMain(root, row))),
            $"{scope} 筛选条未落在文件栏内容区");
        double contentEnd = (double)panes.FileContent.Y + (double)panes.FileContent.Height;
        double filmEnd = (double)root.Filmstrip.Frame.Y + (double)root.Filmstrip.Frame.Height;
        if (Math.Abs(filmEnd - contentEnd) > .5) throw new InvalidOperationException($"{scope} 胶片栏未填满文件栏剩余空间：{filmEnd} vs {contentEnd}");
        lines.Add($"PASS {scope} 分区：{Size(bounds)} safe(T{safe.Top:0.##} L{safe.Left:0.##} B{safe.Bottom:0.##} R{safe.Right:0.##}) 文件栏 {Size(root.FileBarView.Frame)} 主图 {Size(root.PhotoPane.Frame)} 控制栏 {Size(root.ControlScroll.Frame)}；背景铺满边缘、内容在安全区内、三分区首尾相接无重叠、分析栏槽位为 0");
    }

    /// <summary>当前朝向下的筛选条主轴长度（行布局为控件高 + 上下外边距 + 胶片栏带；列布局用实际视图高度回算）。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="row">是否行布局。</param>
    /// <returns>筛选条主轴长度。</returns>
    private static double FilterMain(iOSNativeRootViewController root, bool row)
        => row ? MainScreenLayout.FilterBarMainLength(true, 0) : (double)root.FilterBar.Frame.Height;

    /// <summary>核对筛选条：三个 90×32 下拉、90×27 相似聚类 toggle（I5 起显示）、计数自然宽度、5 点间距与居中。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static void VerifyFilterBar(iOSNativeRootViewController root, bool row, string scope, List<string> lines)
    {
        var bar = root.FilterBar;
        UIButton[] controls = [bar.SortControl, bar.OrderControl, bar.RatingControl];
        foreach (var control in controls)
        {
            RequireFrame(control.Frame, new CGRect(control.Frame.X, control.Frame.Y, MainScreenLayout.FilterControlWidth, MainScreenLayout.FilterControlHeight),
                $"{scope} 筛选控件未保持 90×32");
        }
        RequireFrame(bar.SimilarityToggle.Frame, new CGRect(bar.SimilarityToggle.Frame.X, bar.SimilarityToggle.Frame.Y, MainScreenLayout.FilterControlWidth, MainScreenLayout.FilterToggleHeight),
            $"{scope} 相似聚类 toggle 未保持 90×27");
        if (bar.SimilarityToggle.Hidden) throw new InvalidOperationException($"{scope} I5 起相似聚类入口必须显示（面板已落地，SCOPE-01 解禁）");
        if (Math.Abs((double)bar.CountLabel.Frame.Width - bar.CountWidth) > Exact || bar.CountWidth <= 0)
            throw new InvalidOperationException($"{scope} 计数文本宽度不是自然宽度：{bar.CountLabel.Frame.Width}/{bar.CountWidth}");
        for (int index = 1; index < controls.Length; index++)
        {
            double gap = row
                ? (double)controls[index].Frame.X - (double)controls[index - 1].Frame.Right
                : (double)controls[index].Frame.Y - (double)controls[index - 1].Frame.Bottom;
            if (Math.Abs(gap - MainScreenLayout.FilterGap) > Exact)
                throw new InvalidOperationException($"{scope} 筛选控件间距不是 5 点：{controls[index].Frame}");
        }
        if (row)
        {
            if (Math.Abs((double)bar.CountLabel.Frame.X - (double)controls[2].Frame.Right - MainScreenLayout.FilterGap) > Exact)
                throw new InvalidOperationException($"{scope} 计数未紧随星级筛选 5 点：{bar.CountLabel.Frame}");
            if (Math.Abs((double)bar.SimilarityToggle.Frame.X - (double)bar.CountLabel.Frame.Right - MainScreenLayout.FilterGap) > Exact)
                throw new InvalidOperationException($"{scope} 相似聚类 toggle 未紧随计数 5 点：{bar.SimilarityToggle.Frame}");
            double groupCenter = ((double)controls[0].Frame.Left + (double)bar.SimilarityToggle.Frame.Right) / 2;
            if (Math.Abs(groupCenter - (double)bar.Bounds.Width / 2) > Exact)
                throw new InvalidOperationException($"{scope} 五项未在安全内容区整组居中");
            foreach (var control in controls.Cast<UIView>().Concat([bar.CountLabel, bar.SimilarityToggle]))
                if (Math.Abs((double)control.Frame.GetMidY() - (double)bar.Bounds.Height / 2) > Exact)
                    throw new InvalidOperationException($"{scope} 筛选项交叉轴未居中：{control.Frame}");
            foreach (var control in controls.Append(bar.SimilarityToggle))
                if (control.Configuration == null || control.Layer.BorderWidth != 0)
                    throw new InvalidOperationException($"{scope} 筛选按钮未使用单一原生configuration");
        }
        else
        {
            if (Math.Abs((double)bar.CountLabel.Frame.Y - (double)controls[2].Frame.Bottom - MainScreenLayout.FilterGap) > Exact)
                throw new InvalidOperationException($"{scope} 计数未紧随星级筛选 5 点：{bar.CountLabel.Frame}");
            if (Math.Abs((double)bar.SimilarityToggle.Frame.Y - (double)bar.CountLabel.Frame.Bottom - MainScreenLayout.FilterGap) > Exact)
                throw new InvalidOperationException($"{scope} 相似聚类 toggle 未紧随计数 5 点：{bar.SimilarityToggle.Frame}");
            if (Math.Abs((double)bar.Bounds.Width - MainScreenLayout.FileBarContentWidth) > .5)
                throw new InvalidOperationException($"{scope} 左右布局文件栏内容宽不是 116 点：{bar.Bounds.Width}");
            foreach (var control in controls)
                if (Math.Abs((double)control.Frame.X - (MainScreenLayout.FileBarContentWidth - MainScreenLayout.FilterControlWidth) / 2) > Exact)
                    throw new InvalidOperationException($"{scope} 左右布局筛选控件未在 116 栏内居中：{control.Frame}");
            if (Math.Abs((double)controls[1].Frame.Y - (double)controls[0].Frame.Y - 37) > Exact ||
                Math.Abs((double)controls[2].Frame.Y - (double)controls[1].Frame.Y - 37) > Exact)
                throw new InvalidOperationException($"{scope} 左右布局筛选控件纵向节距不是 37：{controls[2].Frame}");
            double center = (double)controls[0].Frame.X + (double)controls[0].Frame.Width / 2;
            if (Math.Abs(center - MainScreenLayout.FileBarContentWidth / 2) > Exact)
                throw new InvalidOperationException($"{scope} 左右布局筛选条中心不在 58：{center}");
        }
        lines.Add($"PASS {scope} 筛选条：3×(90×32) + 计数 {bar.CountWidth:0.##} 自然宽 + toggle 90×27(显示)；间距 5、{(row ? "五项整组在安全内容区水平居中、交叉轴居中" : "116 栏内居中、纵向节距 37")}");
    }

    /// <summary>核对控制栏：分组数量/子项与共享设置一致、分组外边距与不重叠、子控件尺寸、评分方向随布局方向。</summary>
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
        int ratingCount = main.Settings.ShowRating ? 6 : 0;
        int commandCount = main.ControlVM.EnabledControls.Count(hotkey =>
            iOSControlBar.IsCommandAvailable(hotkey.Command) && AppleSymbolNames.ForCommand(hotkey.Command) != null);
        int expectedGroups = (exifCount > 0 ? 1 : 0) + (ratingCount > 0 ? 1 : 0) + (commandCount > 0 ? 1 : 0);
        if (bar.Groups.Count != expectedGroups || bar.Ratings.Count != ratingCount)
            throw new InvalidOperationException(
                $"{scope} 控制栏分组与共享设置不一致：组 {bar.Groups.Count}/{expectedGroups}、评分 {bar.Ratings.Count}/{ratingCount}（EXIF {exifCount}、命令 {commandCount}）");
        foreach (var group in bar.Groups)
        {
            var frame = group.Frame;
            if ((double)frame.X < ControlBarLayout.Padding - Exact || (double)frame.Y < ControlBarLayout.Padding - Exact ||
                (double)frame.X + (double)frame.Width > (double)bounds.Width - ControlBarLayout.Padding + Exact ||
                (double)frame.Y + (double)frame.Height > (double)bounds.Height - ControlBarLayout.Padding + Exact)
                throw new InvalidOperationException($"{scope} 控制组未保留 10 点外边距：{frame}/{bounds}");
            foreach (var other in bar.Groups.Where(other => !ReferenceEquals(other, group)))
                if (Overlaps(frame, other.Frame)) throw new InvalidOperationException($"{scope} 控制组重叠：{frame} / {other.Frame}");
        }
        // 子项数量直接取共享设置（EXIF 显隐/评分显隐/命令显隐与顺序都可被用户改写）。
        var childCounts = new List<int>();
        if (exifCount > 0) childCounts.Add(exifCount);
        if (ratingCount > 0) childCounts.Add(ratingCount);
        if (commandCount > 0) childCounts.Add(commandCount);
        for (int index = 0; index < bar.Groups.Count; index++)
            if (bar.Groups[index].Subviews.Length != childCounts[index])
                throw new InvalidOperationException($"{scope} 控制组 {index} 子项数量 {bar.Groups[index].Subviews.Length}，预期 {childCounts[index]}");
        if (exifCount > 0) foreach (var tile in bar.Groups[0].Subviews)
        {
            RequireFrame(tile.Frame, new CGRect(tile.Frame.X, tile.Frame.Y, 50, 40), $"{scope} EXIF 信息块未保持 50×40");
            var labels = tile.Subviews.OfType<UILabel>().ToArray();
            if (labels.Length != 2 || labels[1].Font.PointSize <= labels[0].Font.PointSize)
                throw new InvalidOperationException($"{scope} EXIF 标签/值层级丢失");
        }
        foreach (var rating in bar.Ratings)
            RequireFrame(rating.Frame, new CGRect(rating.Frame.X, rating.Frame.Y, 30, 30), $"{scope} 评分按钮未保持 30×30");
        foreach (var command in bar.Commands.Values)
            RequireFrame(command.Frame, new CGRect(command.Frame.X, command.Frame.Y, 40, 40), $"{scope} 命令按钮未保持 40×40");
        for (int index = 1; index < bar.Ratings.Count; index++)
        {
            if (row && (double)bar.Ratings[index].Frame.X <= (double)bar.Ratings[index - 1].Frame.X)
                throw new InvalidOperationException($"{scope} 横排评分方向不是 0→5");
            if (!row && (double)bar.Ratings[index].Frame.Y >= (double)bar.Ratings[index - 1].Frame.Y)
                throw new InvalidOperationException($"{scope} 竖排评分方向不是 5→0");
        }
        // I5 起分析栏/增强预览命令实现可用；是否出现在控制栏仍由共享设置的显隐决定（默认分析栏不显示）。
        if (!iOSControlBar.IsCommandAvailable("ToggleAnalysisView") || !iOSControlBar.IsCommandAvailable("ToggleEnhance"))
            throw new InvalidOperationException($"{scope} I5 起分析栏/增强预览命令必须有原生实现（SCOPE-01 解禁）");
        // TOOLBAR-08：滚动宿主的内容尺寸与控制栏文档一致，偏移落在合法范围（重排只夹取，不重置）。
        if (root.ControlScroll.ContentSize != bar.Frame.Size)
            throw new InvalidOperationException($"{scope} 控制栏滚动内容尺寸 {Size(root.ControlScroll.ContentSize)} 与文档 {Size(bar.Frame.Size)} 不一致");
        var offset = root.ControlScroll.ContentOffset;
        if ((double)offset.X < 0 || (double)offset.Y < 0
            || (double)offset.X > Math.Max(0, (double)root.ControlScroll.ContentSize.Width - (double)root.ControlScroll.Bounds.Width)
            || (double)offset.Y > Math.Max(0, (double)root.ControlScroll.ContentSize.Height - (double)root.ControlScroll.Bounds.Height))
            throw new InvalidOperationException($"{scope} 控制栏滚动偏移越界：{offset}");
        lines.Add($"PASS {scope} 控制栏：{bar.Groups.Count} 组({string.Join("/", bar.Groups.Select(group => group.Subviews.Length))}) 文档 {Size(bounds)} 裁剪区 {Size(root.ControlScroll.Bounds)}；10 点外边距、组内 50×40/30×30/40×40、{(row ? "横排评分 0→5" : "竖排评分 5→0")}、I5 命令入口可用、滚动内容尺寸与偏移合规");
    }

    /// <summary>核对卡片几何（96×144 外框 / 90×138 占位区 / 80 方形照片区 / 星级与文字位置）与身份绑定。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyFilmstripGeometryAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        if (FilmstripGeometry.CardWidth != 96 || FilmstripGeometry.CardHeight != 144 ||
            FilmstripGeometry.HorizontalPadding != 10 || FilmstripGeometry.TopPadding != 5 || FilmstripGeometry.BottomPadding != 10)
            throw new InvalidOperationException($"{scope} 胶片栏不再使用已验证的 96×144 卡片与 10/5/10 内边距");
        var filmstrip = root.Filmstrip;
        bool horizontal = filmstrip.Horizontal;
        if (horizontal != row) throw new InvalidOperationException($"{scope} 胶片栏方向未跟随布局（行布局横向单行、列布局纵向单列）");
        var document = filmstrip.DocumentSize;
        var viewport = filmstrip.Bounds.Size;
        int count = main.FileVM.ThumbnailList.FilteredFiles.Count;
        double step = FilmstripGeometry.Step(horizontal);
        double expected = count * step + (horizontal ? FilmstripGeometry.HorizontalPadding * 2 : FilmstripGeometry.TopPadding + FilmstripGeometry.BottomPadding);
        double documentMain = horizontal ? (double)document.Width : (double)document.Height;
        double viewportMain = horizontal ? (double)viewport.Width : (double)viewport.Height;
        if (Math.Abs(documentMain - Math.Max(viewportMain, expected)) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏文档主轴长度错误：{documentMain}");
        var first = filmstrip.FrameAt(0) ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
        double expectedStart = horizontal ? Math.Max(10, ((double)document.Width - count * 96) / 2) : 10;
        RequireFrame(first, new CGRect(expectedStart, 5, 96, 144), $"{scope} 首卡位置与卡片尺寸");
        var second = filmstrip.FrameAt(count > 1 ? 1 : 0) ?? throw new InvalidOperationException($"{scope} 第二卡几何缺失");
        if (count > 1)
        {
            double cardStep = horizontal ? (double)(second.X - first.X) : (double)(second.Y - first.Y);
            if (Math.Abs(cardStep - step) > Exact) throw new InvalidOperationException($"{scope} 胶片栏步长错误：{cardStep} != {step}");
            if (horizontal ? Math.Abs((double)(second.Y - first.Y)) > Exact : Math.Abs((double)(second.X - first.X)) > Exact)
                throw new InvalidOperationException($"{scope} 胶片栏出现换行或换列：{first} / {second}");
        }
        var cell = await WaitForCardAsync(root, main, 0, scope);
        RequireFrame(cell.Surface.Frame, new CGRect(3, 3, 90, 138), $"{scope} 卡片占位区（96×144 内部）");
        RequireFrame(cell.ThumbnailView.Frame, new CGRect(5, 5, 80, 80), $"{scope} 卡片照片区非 80 方形");
        var labels = cell.Surface.Subviews.OfType<UILabel>().ToArray();
        var name = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - 89) < Exact) ?? throw new InvalidOperationException($"{scope} 卡片文件名缺失");
        var date = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - 103) < Exact) ?? throw new InvalidOperationException($"{scope} 卡片第二行（拍摄时间）缺失");
        var shake = labels.SingleOrDefault(label => Math.Abs((double)label.Frame.Y - 67) < Exact) ?? throw new InvalidOperationException($"{scope} 卡片抖动徽标缺失");
        RequireFrame(name.Frame, new CGRect(2, 89, 86, 12), $"{scope} 卡片文件名");
        RequireFrame(date.Frame, new CGRect(5, 103, 80, 11), $"{scope} 卡片日期");
        RequireFrame(shake.Frame, new CGRect(67, 67, 16, 16), $"{scope} 卡片抖动徽标");
        if (name.LineBreakMode != UILineBreakMode.TailTruncation) throw new InvalidOperationException($"{scope} 卡片文件名未按尾部截断显示");
        for (int rating = 0; rating < cell.StarButtons.Count; rating++)
            RequireFrame(cell.StarButtons[rating].Frame, new CGRect(3 + rating * 14, 116, 14, 14), $"{scope} 卡片星级 {rating}");
        if (!ReferenceEquals(cell.BoundFile, main.FileVM.ThumbnailList.FilteredFiles[0]))
            throw new InvalidOperationException($"{scope} 首卡绑定身份与业务顺序不同");
        if (cell.IsHighlighted != main.FileVM.ThumbnailList.FilteredFiles[0].IsCurrent)
            throw new InvalidOperationException($"{scope} 卡片高亮未取共享 IsCurrent 身份");
        lines.Add($"PASS {scope} 胶片栏：{(horizontal ? "横向单行" : "纵向单列")} 步长 {step}、文档 {Size(document)} 视口 {Size(viewport)}、首卡 {first}、卡片 96×144/(3,3,90,138)/照片 80 方形/星级 14 点、文件名尾部截断、高亮取 IsCurrent");
    }

    /// <summary>隐藏/恢复胶片栏与控制栏，核对空间确实释放给主图。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="row">是否行布局。</param>
    /// <param name="scope">报告范围标签。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyPaneReleaseAsync(iOSNativeRootViewController root, MainViewModel main, bool row, string scope, List<string> lines)
    {
        var safe = root.View!.SafeAreaInsets;
        var bounds = root.View.Bounds;
        double boundsMain = row ? (double)bounds.Height : (double)bounds.Width;
        double leadingInset = row ? (double)safe.Top : (double)safe.Left;
        double trailingInset = row ? (double)safe.Bottom : (double)safe.Right;
        var before = root.PhotoPane.Frame;
        double beforeLeading = row ? (double)before.Y : (double)before.X;
        if (beforeLeading <= leadingInset + .5) throw new InvalidOperationException($"{scope} 主图旁没有可释放的文件栏分区：{before}");
        await UiDispatcher.InvokeAsync(() => main.IsThumbnailViewVisible = false);
        await SettleAsync(root);
        if (!root.FileBarView.Hidden || !root.Filmstrip.Hidden || (double)root.FileBarView.Frame.Height > .5 && (double)root.FileBarView.Frame.Width > .5)
            throw new InvalidOperationException($"{scope} 隐藏胶片栏未收起文件栏：{root.FileBarView.Frame}");
        var released = root.PhotoPane.Frame;
        double releasedLeading = row ? (double)released.Y : (double)released.X;
        if (Math.Abs(releasedLeading - leadingInset) > .5)
            throw new InvalidOperationException($"{scope} 隐藏胶片栏后主图未贴安全区前沿：{released}（安全区 {leadingInset:0.##}）");
        double fileGrowth = (row ? (double)released.Height : (double)released.Width) - (row ? (double)before.Height : (double)before.Width);
        if (Math.Abs(fileGrowth - (beforeLeading - leadingInset)) > .5)
            throw new InvalidOperationException($"{scope} 隐藏胶片栏释放的长度不符：{fileGrowth:0.##} != {beforeLeading - leadingInset:0.##}");
        await UiDispatcher.InvokeAsync(() => main.IsThumbnailViewVisible = true);
        await SettleAsync(root);
        RequireFrame(root.PhotoPane.Frame, before, $"{scope} 恢复胶片栏后主图分区未还原");

        var beforeControl = root.PhotoPane.Frame;
        double beforeTrailing = boundsMain - (row ? (double)beforeControl.Y + (double)beforeControl.Height : (double)beforeControl.X + (double)beforeControl.Width);
        if (beforeTrailing <= trailingInset + .5) throw new InvalidOperationException($"{scope} 主图旁没有可释放的控制栏分区：{beforeControl}");
        await UiDispatcher.InvokeAsync(() => main.IsControlViewVisible = false);
        await SettleAsync(root);
        if (!root.ControlScroll.Hidden) throw new InvalidOperationException($"{scope} 隐藏控制栏未收起控制分区");
        var releasedControl = root.PhotoPane.Frame;
        double releasedTrailing = boundsMain - (row ? (double)releasedControl.Y + (double)releasedControl.Height : (double)releasedControl.X + (double)releasedControl.Width);
        if (Math.Abs(releasedTrailing - trailingInset) > .5)
            throw new InvalidOperationException($"{scope} 隐藏控制栏后主图未贴安全区后沿：{releasedControl}（安全区 {trailingInset:0.##}）");
        double controlGrowth = (row ? (double)releasedControl.Height : (double)releasedControl.Width) - (row ? (double)beforeControl.Height : (double)beforeControl.Width);
        if (Math.Abs(controlGrowth - (beforeTrailing - trailingInset)) > .5)
            throw new InvalidOperationException($"{scope} 隐藏控制栏释放的长度不符：{controlGrowth:0.##} != {beforeTrailing - trailingInset:0.##}");
        await UiDispatcher.InvokeAsync(() => main.IsControlViewVisible = true);
        await SettleAsync(root);
        RequireFrame(root.PhotoPane.Frame, beforeControl, $"{scope} 恢复控制栏后主图分区未还原");
        lines.Add($"PASS {scope} 栏位显隐：隐藏胶片栏释放{(row ? "上" : "左")}侧 {fileGrowth:0.##} 点、隐藏控制栏释放{(row ? "下" : "右")}侧 {controlGrowth:0.##} 点（主图贴安全区为界），恢复后主图分区完全还原");
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

    /// <summary>核对卡片星级写回与筛选连续性（[FILM-05/06]）：命中独立、写回文件、不切主图、改星后暂留、切走后移除。</summary>
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
        if (!Path.GetFullPath(entryFile.File.PathKey).StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal))
            throw new InvalidOperationException($"评分核对不在自有夹具目录内：{entryFile.File.PathKey}");
        int entryRating = (await ExifLoader.LoadExifDataAsync(entryFile.File))?.Rating ?? 0;
        var fixtureRatings = new Dictionary<string, int>(StringComparer.Ordinal);
        int target = entryRating == 3 ? 2 : 3;
        try
        {
            var other = main.FolderVM.AllFiles.FirstOrDefault(candidate => !ReferenceEquals(candidate, entryFile))
                ?? throw new InvalidOperationException("评分核对需要至少两张自有夹具");
            foreach (var candidate in main.FolderVM.AllFiles)
                fixtureRatings[candidate.File.PathKey] = (await ExifLoader.LoadExifDataAsync(candidate.File))?.Rating ?? 0;
            // 另一张副本先与当前照片同星级，才能同时构成"当前照片改星后落出筛选"和"列表里还有符合筛选的照片"。
            if (fixtureRatings[other.File.PathKey] != entryRating) await main.SetRatingAsync(other, entryRating);
            await WaitAsync(() => entryFile.Rating == entryRating && other.Rating == entryRating, "自有副本评级就绪");
            string key = entryRating == 0 ? "None" : $"Eq{entryRating}";
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = key);
            await WaitAsync(() => list.FilteredFiles.Contains(entryFile) && list.FilteredFiles.Contains(other), $"筛选键 {key} 就绪");
            if (list.FilteredFiles.Any(item => item.Rating != entryRating && !ReferenceEquals(item, entryFile)))
                throw new InvalidOperationException($"{key} 筛选列表仍含不符合条件的照片，无法核对 FILM-06 暂留规则");
            int index = list.FilteredFiles.IndexOf(entryFile);
            await UiDispatcher.InvokeAsync(() => list.ScrollToCurrent());
            var card = await WaitForCardAsync(root, main, index, "FILM-06");
            // 命中测试：星级按钮独立命中，点星不会落到整卡选择按钮上
            var star = card.StarButtons[target];
            var starCenter = new CGPoint((double)star.Bounds.Width / 2, (double)star.Bounds.Height / 2);
            var hit = card.HitTest(star.ConvertPointToView(starCenter, card), null);
            if (hit is not UIButton) throw new InvalidOperationException($"星级按钮未独立命中，点星会选中父卡片：{hit?.GetType().Name ?? "null"}");
            if (ReferenceEquals(hit, card.SelectButton)) throw new InvalidOperationException("星级按钮命中了整卡选择按钮");
            var file = entryFile;
            await UiDispatcher.InvokeAsync(() => star.SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await WaitAsync(() => file.Rating == target, $"胶片栏星级按钮写回 {target} 星");
            var written = await ExifLoader.LoadExifDataAsync(file.File);
            if (written?.Rating != target) throw new InvalidOperationException($"星级未写回文件：memory={file.Rating}, file={written?.Rating}");
            if (!ReferenceEquals(main.CurrentFile, file)) throw new InvalidOperationException("星级按钮改变了当前主图身份");
            if (!list.FilteredFiles.Contains(file)) throw new InvalidOperationException("改星后当前照片未按 FILM-06 暂留，选片进度瞬间丢失");
            lines.Add($"PASS FILM-05/06：卡片星级按钮写回自有副本 {target} 星（文件读回一致）、命中不落整卡、主图身份不变；改星后照片仍暂留在 {key} 筛选列表内");
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
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = "All");
            foreach (var pair in fixtureRatings)
            {
                var candidate = main.FolderVM.AllFiles.FirstOrDefault(item => item.File.PathKey == pair.Key);
                if (candidate != null && candidate.Rating != pair.Value) await main.SetRatingAsync(candidate, pair.Value);
            }
            await UiDispatcher.InvokeAsync(() => filter.SelectedRatingFilter = entryFilter);
            if (main.CurrentFile?.File.PathKey != entryFile.File.PathKey)
                await main.FolderVM.OpenImageAsync(entryFile.File);
        }
    }

    /// <summary>收尾：布局模式、栏位显隐与筛选还原到验收默认值。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="similarity">进入检查时的相似面板状态。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, bool similarity)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.IsThumbnailViewVisible = true;
            main.IsControlViewVisible = true;
            main.FileVM.FilterBar.IsSimilarityPanelOpen = similarity;
            main.FileVM.FilterBar.SelectedRatingFilter = "All";
        });
        await SettleAsync(root);
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
    private static async Task SettleAsync(iOSNativeRootViewController root)
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
            using var path = NSIndexPath.FromItemSection(index, 0);
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
