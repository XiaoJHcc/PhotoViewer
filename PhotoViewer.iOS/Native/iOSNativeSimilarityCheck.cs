using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I5 验收专用（非产品行为，测试钩子）：<c>--pv-native-similarity-check &lt;目录&gt;</c> 在模拟器/真机上程序化核对
/// 相似聚类面板——筛选条 toggle 与共享 <c>IsSimilarityPanelOpen</c> 双向一致并真实点击开合、
/// 两向让位几何（行布局从胶片栏右侧切出一段、列布局扩宽文件栏）与 106 点最小宽度、收起后逐项还原、
/// 列表态/空态与共享 VM 一致、卡片第二行分数百分比与柱状条、"提取全部"真实按钮点击、
/// 相似项卡片点选切主图但保留胶片栏锚点（双高亮）。
/// 素材要求：目录内至少 3 张自有 jpg；只写 App 沙盒内的工作副本，不写用户照片或 TestCase。
/// 报告写 Documents/PhotoViewer/native-similarity-checks.txt。
/// </summary>
internal static class iOSNativeSimilarityCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-similarity-check";
    /// <summary>布局/动画沉淀等待。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    /// <summary>几何允许误差（点）。</summary>
    private const double Tolerance = 1.5;

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

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：等主屏与素材就绪 → 逐项断言 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-similarity-checks.txt");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"相似验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length < 3) throw new InvalidOperationException($"相似验收需要 ≥3 张自有夹具，实际 {fixtures.Length}（{directory}）");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            string workspace = StageWorkspace(fixtures);
            var snapshot = main.Settings.CreateSnapshot();
            var originalFile = main.CurrentFile;
            bool entryAnalysis = main.IsAnalysisViewVisible, entryFilm = main.IsThumbnailViewVisible, entryControl = main.IsControlViewVisible;
            bool entrySimilarity = main.FileVM.FilterBar.IsSimilarityPanelOpen;
            try
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.ShowRating = true;
                    main.Settings.LayoutMode = LayoutMode.Row;
                    main.IsThumbnailViewVisible = true;
                    main.IsControlViewVisible = true;
                    main.IsAnalysisViewVisible = false;
                    main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
                    main.FileVM.FilterBar.SelectedRatingFilter = "All";
                    EnsureJpegEnabled(main);
                });
                await main.FolderVM.OpenFolderAsync(new iOSFolderRef(workspace));
                await WaitAsync(() => main.FolderVM.AllFiles.Count >= 3, "工作副本枚举");
                await WaitAsync(() => main.CurrentFile != null && session.Image != null, "首图解码");
                await SettleAsync(root);
                var filter = main.FileVM.FilterBar;
                var panel = root.SimilarityPane;
                if (root.FilterBar.SimilarityToggle.Hidden) throw new InvalidOperationException("相似聚类 toggle 仍被隐藏（SCOPE-01 应解禁）");
                lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具 → 工作副本 {workspace}");

                // 一、toggle 与共享状态双向一致 + 真实点击开合。
                filter.IsSimilarityPanelOpen = false;
                await SettleAsync(root);
                if (!panel.Hidden) throw new InvalidOperationException("收起时相似面板应为隐藏");
                filter.IsSimilarityPanelOpen = true;
                await SettleAsync(root);
                if (panel.Hidden) throw new InvalidOperationException("展开后相似面板仍隐藏");
                await UiDispatcher.InvokeAsync(() => root.FilterBar.SimilarityToggle.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                await WaitAsync(() => !filter.IsSimilarityPanelOpen && panel.Hidden, "真实点击收起面板");
                await UiDispatcher.InvokeAsync(() => root.FilterBar.SimilarityToggle.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                await WaitAsync(() => filter.IsSimilarityPanelOpen && !panel.Hidden, "真实点击展开面板");
                await SettleAsync(root);
                lines.Add("PASS toggle：与共享 IsSimilarityPanelOpen 双向一致，真实点击可收起并重开");

                // 二、两向让位几何与收起还原（每向自取收起态基线）。
                foreach (var row in new[] { true, false })
                {
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        main.Settings.LayoutMode = row ? LayoutMode.Row : LayoutMode.Col;
                        filter.IsSimilarityPanelOpen = false;
                    });
                    await SettleAsync(root);
                    if (main.IsRowLayout != row) throw new InvalidOperationException($"{row} 方向未按设置生效");
                    double baselineFilm = (double)root.Filmstrip.Frame.Width;
                    double baselineFile = (double)root.FileBarView.Frame.Width;
                    await UiDispatcher.InvokeAsync(() => filter.IsSimilarityPanelOpen = true);
                    await SettleAsync(root);
                    var content = root.Panes.FileContent;
                    var similarityColumn = main.FileVM.SimilarityPanel.HasItems
                        ? SimilarityPanelGeometry.RowWidth(true, main.FileVM.SimilarityPanel.SimilarItems.Count, row ? content.Width : content.Height)
                        : SimilarityPanelGeometry.RowWidth(false, 0, row ? content.Width : content.Height);
                    if (row)
                    {
                        if ((double)root.Filmstrip.Frame.Width >= baselineFilm)
                            throw new InvalidOperationException($"行布局展开后胶片栏未让位：{root.Filmstrip.Frame.Width} ≥ {baselineFilm}");
                        if (Math.Abs((double)panel.Frame.Y - (double)root.Filmstrip.Frame.Y) > Tolerance)
                            throw new InvalidOperationException($"行布局相似面板未与胶片栏顶边对齐：panel {panel.Frame} film {root.Filmstrip.Frame}");
                        if ((double)panel.Frame.X < (double)root.Filmstrip.Frame.Right - Tolerance)
                            throw new InvalidOperationException($"行布局相似面板未排在胶片栏右侧：panel {panel.Frame} film {root.Filmstrip.Frame}");
                        if (Math.Abs((double)((double)root.Filmstrip.Frame.Width + (double)root.SimilarityPane.Frame.Width + SimilarityPanelGeometry.Margin) - (double)content.Width) > Tolerance)
                            throw new InvalidOperationException($"行布局胶片栏 + 相似面板 + 外边距未占满文件栏内容宽：{root.Filmstrip.Frame.Width}+{panel.Frame.Width}+{SimilarityPanelGeometry.Margin} vs {content.Width}");
                    }
                    else
                    {
                        if ((double)root.FileBarView.Frame.Width <= baselineFile)
                            throw new InvalidOperationException($"列布局展开后文件栏未扩宽：{root.FileBarView.Frame.Width} ≤ {baselineFile}");
                        if (Math.Abs((double)panel.Frame.X - ((double)content.X + MainScreenLayout.FileBarContentWidth)) > Tolerance)
                            throw new InvalidOperationException($"列布局相似面板未紧贴 116 栏右侧：panel {panel.Frame} content {content}");
                    }
                    if (panel.CrossSize < SimilarityPanelGeometry.MinWidth)
                        throw new InvalidOperationException($"相似面板交叉轴 {panel.CrossSize} 小于 MinWidth 106");
                    if (panel.Bounds.Width <= 0 || panel.Bounds.Height <= 0)
                        throw new InvalidOperationException($"相似面板尺寸非法：{panel.Bounds}");
                    lines.Add($"PASS 几何 {(row ? "row" : "col")}：panel {panel.Bounds.Width:0}×{panel.Bounds.Height:0} cross={panel.CrossSize:0} " +
                        $"filmstrip {baselineFilm:0}→{root.Filmstrip.Frame.Width:0} fileBar {baselineFile:0}→{root.FileBarView.Frame.Width:0} (column={similarityColumn:0})");

                    await UiDispatcher.InvokeAsync(() => filter.IsSimilarityPanelOpen = false);
                    await SettleAsync(root);
                    if (!panel.Hidden) throw new InvalidOperationException("收起后相似面板未隐藏");
                    if (Math.Abs((double)root.Filmstrip.Frame.Width - baselineFilm) > Tolerance
                        || Math.Abs((double)root.FileBarView.Frame.Width - baselineFile) > Tolerance)
                        throw new InvalidOperationException($"收起后文件栏/胶片栏几何未还原：{root.FileBarView.Frame}/{root.Filmstrip.Frame}");
                    lines.Add($"PASS 收起 {(row ? "row" : "col")}：文件栏与胶片栏几何逐项还原");
                }

                // 三、空态与共享 VM 一致（工作副本尚未提取特征时）。
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.LayoutMode = LayoutMode.Row;
                    filter.IsSimilarityPanelOpen = true;
                });
                await SettleAsync(root);
                await WaitAsync(() => !main.FileVM.SimilarityPanel.IsStateIndexing && !panel.IsListVisible
                    || main.FileVM.SimilarityPanel.HasItems, "空态统计稳定");
                var similarity = main.FileVM.SimilarityPanel;
                if (!similarity.HasItems)
                {
                    var labels = Labels(panel).ToArray();
                    if (similarity.ShowUnindexedPlaceholder != labels.Contains("尚未提取特征", StringComparer.Ordinal))
                        throw new InvalidOperationException($"未提取占位与 VM 不一致：{string.Join(" | ", labels)}");
                    if (similarity.ShowFullPlaceholder != labels.Contains("无相似照片", StringComparer.Ordinal))
                        throw new InvalidOperationException($"无相似照片占位与 VM 不一致：{string.Join(" | ", labels)}");
                    lines.Add($"PASS 空态：unindexed={similarity.UnindexedCount}，占位与共享 VM 一致（{string.Join(" | ", labels)}）");
                }
                else
                {
                    lines.Add($"NOTE 工作副本此前已提取特征，直接核对列表态（{similarity.SimilarItems.Count} 项）");
                }

                // 四、真实点击"提取全部/补齐全部"→ 入列表态；卡片第二行分数与柱状条。
                if (!similarity.HasItems)
                {
                    if (!similarity.ShowActionButton) throw new InvalidOperationException("空态没有可点击的提取按钮");
                    await UiDispatcher.InvokeAsync(() => panel.EmptyActionButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
                    await WaitAsync(() => similarity.IsStateIndexing || similarity.HasItems, "提取全部开始");
                    await WaitAsync(() => similarity.HasItems && !similarity.IsStateIndexing, "提取完成并出现相似项", 2400);
                }
                await SettleAsync(root);
                if (!similarity.HasItems) throw new InvalidOperationException("索引后仍无相似项");
                if (!panel.IsListVisible) throw new InvalidOperationException("有相似项时列表态未显示");
                await WaitAsync(() => panel.List.VisibleCells.OfType<iOSThumbnailCard>().Any(), "相似卡片可见");
                // 行布局面板宽度必须在项目出现后按卡片数重新切出（否则停靠在右的动作区会把列表挤成零宽）。
                if (main.IsRowLayout)
                {
                    double contentWidth = root.Panes.FileContent.Width;
                    double expectedPanel = Math.Min(
                        Math.Max(SimilarityPanelGeometry.MinWidth, panel.Items.Count * FilmstripGeometry.CardWidth + SimilarityPanelGeometry.ListPadding + SimilarityPanelGeometry.BorderWidth * 2),
                        contentWidth / 2);
                    if (Math.Abs((double)panel.Bounds.Width - expectedPanel) > 2)
                        throw new InvalidOperationException($"行布局面板宽度未随 {panel.Items.Count} 个相似项重算：{panel.Bounds.Width} 预期 {expectedPanel}");
                }
                var cards = panel.List.VisibleCells.OfType<iOSThumbnailCard>().ToArray();
                var card = cards.First(entry => entry.SecondLineProgress != null);
                if (string.IsNullOrEmpty(card.SecondLineText) || !card.SecondLineText!.EndsWith('%'))
                    throw new InvalidOperationException($"相似卡片第二行不是百分比：{card.SecondLineText}");
                if (!card.IsSecondLineBarVisible) throw new InvalidOperationException("相似卡片第二行缺少柱状条");
                lines.Add($"PASS 列表态：{similarity.SimilarItems.Count} 项，卡片第二行 {card.SecondLineText} + 柱状条（{card.SecondLineProgress:0.00}）");

                // 五、相似项卡片点选：切主图但保留胶片栏锚点（双高亮）。
                await VerifySelectionAsync(root, main, workspace, lines);

                // 六、收起面板后几何完全还原。
                await UiDispatcher.InvokeAsync(() => filter.IsSimilarityPanelOpen = false);
                await SettleAsync(root);
                if (!panel.Hidden) throw new InvalidOperationException("收起后相似面板未隐藏");
                lines.Add("PASS 收起：面板隐藏");

                await RestoreAsync(root, main, snapshot, entryAnalysis, entryFilm, entryControl, entrySimilarity, originalFile);
                await File.WriteAllLinesAsync(report, lines);
                Console.WriteLine($"[NativeSmoke] PASS similarity ({lines.Count} checks)");
            }
            finally
            {
                await UiDispatcher.InvokeAsync(() =>
                {
                    main.Settings.ApplySnapshot(snapshot);
                    main.IsAnalysisViewVisible = entryAnalysis;
                    main.IsThumbnailViewVisible = entryFilm;
                    main.IsControlViewVisible = entryControl;
                    main.FileVM.FilterBar.IsSimilarityPanelOpen = entrySimilarity;
                    if (originalFile != null) main.CurrentFile = originalFile;
                });
                await SettleAsync(root);
                FlushSettings(main);
                if (!File.Exists(report)) await File.WriteAllLinesAsync(report, lines);
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL similarity " + exception);
        }
    }

    /// <summary>
    /// 相似项卡片点选：在独立探针子目录放两份唯一名字的素材（指纹互不相同，入库后互为相似项）→ 走共享
    /// "提取全部"入口建库 → 真实点击相似面板里的另一张 → 断言面板卡片与胶片栏卡片同时高亮、锚点身份与显示身份分离。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="workspace">工作副本目录（探针取其前两张复制）。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifySelectionAsync(iOSNativeRootViewController root, MainViewModel main, string workspace, List<string> lines)
    {
        string probeFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"sim-probe-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(probeFolder);
        string first = Path.Combine(probeFolder, "probe-a.jpg");
        string second = Path.Combine(probeFolder, "probe-b.jpg");
        var sources = Directory.GetFiles(workspace, "*.jpg").OrderBy(path => new FileInfo(path).Length).Take(2).ToArray();
        File.Copy(sources[0], first, true);
        File.Copy(sources[1], second, true);
        try
        {
            await main.FolderVM.OpenImageAsync(new iOSFileRef(first));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(first), "探针 A 成为当前照片");
            var similarity = main.FileVM.SimilarityPanel;
            similarity.StartIndexingCommand();
            await WaitAsync(() => similarity.SimilarItems.Count > 0 && !similarity.IsStateIndexing, "探针目录的相似项出现", 4000);
            var panel = root.SimilarityPane;
            await SettleAsync(root);
            await WaitAsync(() => panel.List.VisibleCells.OfType<iOSThumbnailCard>().Any(), "相似卡片可见");

            var anchor = main.CurrentFile!;
            var item = similarity.SimilarItems.First(entry => !ReferenceEquals(entry.File, anchor));
            var card = panel.List.VisibleCells.OfType<iOSThumbnailCard>().FirstOrDefault(entry => ReferenceEquals(entry.BoundFile, item.File))
                ?? throw new InvalidOperationException("相似面板未显示目标相似卡片");
            await UiDispatcher.InvokeAsync(() => card.SelectButton.SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, item.File), "点击相似卡片切换主图");
            await SettleAsync(root);
            if (!item.File.IsCurrentImage) throw new InvalidOperationException("被点中的相似项未成为主图身份");
            if (!anchor.IsCurrent) throw new InvalidOperationException("胶片栏锚点高亮被相似项点击抢走（原始设计要求保留）");
            if (anchor.IsCurrentImage) throw new InvalidOperationException("锚点的显示身份未让给被点中的相似项");
            await WaitAsync(() => panel.List.VisibleCells.OfType<iOSThumbnailCard>().Any(entry => entry.BoundFile == item.File && entry.IsHighlighted),
                "相似面板卡片高亮");
            var anchorIndex = main.FileVM.ThumbnailList.FilteredFiles.IndexOf(anchor);
            await WaitAsync(() => root.Filmstrip.CardAt(anchorIndex) is { IsHighlighted: true }, "胶片栏锚点保持高亮");
            lines.Add($"PASS 选中：相似卡片 {item.File.DisplayName} 与胶片栏锚点 {anchor.DisplayName} 同时高亮，锚点保留");
        }
        finally
        {
            Directory.Delete(probeFolder, true);
        }
    }

    /// <summary>验收环境自愈：确保 JPEG 格式启用（历史验收会话被强杀可能遗留全格式关闭）。</summary>
    private static void EnsureJpegEnabled(MainViewModel main)
    {
        foreach (var format in main.Settings.FileFormats)
            if (format.DisplayName == "JPG") format.IsEnabled = true;
    }

    /// <summary>收尾时把设置同步落盘，避免验收进程被强杀导致回写丢失。</summary>
    private static void FlushSettings(MainViewModel main)
        => PhotoViewer.Core.Settings.SettingsService.Instance.SaveAsync(main.Settings.CreateSnapshot()).GetAwaiter().GetResult();

    /// <summary>把夹具复制成 App 自建工作副本（唯一名字，避免与既有入库指纹混淆），只保留 3 张控制耗时。</summary>
    /// <param name="fixtures">源目录内的 jpg 列表。</param>
    /// <returns>工作副本目录。</returns>
    private static string StageWorkspace(string[] fixtures)
    {
        string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"similarity-fixtures-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(workspace);
        int index = 0;
        foreach (var fixture in fixtures.OrderBy(path => new FileInfo(path).Length).Take(3))
            File.Copy(fixture, Path.Combine(workspace, $"sim-{++index:00}-{Path.GetFileName(fixture)}"), true);
        return workspace;
    }

    /// <summary>收尾：恢复进入检查时的设置快照、布局与当前照片。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="snapshot">设置快照。</param>
    /// <param name="analysis">进入时的分析栏可见性。</param>
    /// <param name="film">进入时的胶片栏可见性。</param>
    /// <param name="control">进入时的控制栏可见性。</param>
    /// <param name="similarity">进入时的相似面板状态。</param>
    /// <param name="originalFile">进入时的当前照片。</param>
    private static async Task RestoreAsync(iOSNativeRootViewController root, MainViewModel main, SettingsModel snapshot,
        bool analysis, bool film, bool control, bool similarity, ImageFile? originalFile)
    {
        await UiDispatcher.InvokeAsync(() =>
        {
            main.Settings.ApplySnapshot(snapshot);
            main.IsAnalysisViewVisible = analysis;
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            main.FileVM.FilterBar.IsSimilarityPanelOpen = similarity;
            if (originalFile != null) main.CurrentFile = originalFile;
        });
        await SettleAsync(root);
    }

    /// <summary>面板子树里实际可见的非空文本（隐藏的空状态/列表态文案不计入）。</summary>
    /// <param name="root">根视图。</param>
    /// <returns>可见文本。</returns>
    private static IEnumerable<string> Labels(UIView root)
    {
        foreach (var view in Descendants(root))
            if (view is UILabel label && !string.IsNullOrEmpty(label.Text) && IsVisible(label))
                yield return label.Text;
    }

    /// <summary>沿父链判断视图是否真的可见。</summary>
    /// <param name="view">目标视图。</param>
    /// <returns>可见为 true。</returns>
    private static bool IsVisible(UIView view)
    {
        for (var current = view; current != null; current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    /// <summary>深度遍历视图树。</summary>
    /// <param name="root">根视图。</param>
    /// <returns>全部子孙视图。</returns>
    private static IEnumerable<UIView> Descendants(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="label">等待标签。</param>
    /// <param name="attempts">轮询次数（每次 25ms）。</param>
    private static async Task WaitAsync(Func<bool> condition, string label, int attempts = 2400)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    /// <summary>等待 UIKit 一轮布局与共享 VM 通知沉淀。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() => root.View!.LayoutIfNeeded());
        await Task.Delay(Settle);
    }
}
