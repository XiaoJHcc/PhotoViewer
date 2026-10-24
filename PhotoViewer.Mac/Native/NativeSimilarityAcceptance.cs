using AppKit;
using CoreGraphics;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生相似聚类面板回归，仅在隔离冒烟会话中以
/// <c>--pv-native-smoke --pv-native-similarity &lt;绝对图片路径&gt;</c> 运行。
/// 覆盖：筛选条 toggle 与共享 `IsSimilarityPanelOpen` 双向一致；
/// 行/列两向分别在"收起 → 展开 → 收起"中核对让位几何（行布局从胶片栏右侧切出一段并缩短胶片栏、
/// 列布局扩宽文件栏）、面板交叉轴不小于旧壳 MinWidth 106、收起后逐项还原到本方向基线；
/// 列表态与空态互斥且与共享 VM 一致。只读自有测试素材，不写用户照片。
/// </summary>
internal static class NativeSimilarityAcceptance
{
    /// <summary>执行相似聚类面板回归并写出检查清单。</summary>
    /// <param name="window">真实原生主窗口。</param>
    /// <param name="output">报告输出目录。</param>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var checks = new List<string>();
        var main = window.Main;
        var filter = main.FileVM.FilterBar;
        var panel = window.Similarity;
        bool originalOpen = filter.IsSimilarityPanelOpen;
        var originalMode = main.Settings.LayoutMode;
        try
        {
            var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
            var filePane = root.Subviews.Single(view => view.Subviews.Contains(window.FilmstripScroll));

            await Task.Run(() => filter.IsSimilarityPanelOpen = true);
            await SettleAsync(window);
            if (!window.SimilarityToggleIsOn || panel.Hidden) throw new InvalidOperationException("后台相似开关通知未同步到主线程 UI");
            main.IsThumbnailViewVisible = false;
            await SettleAsync(window);
            if (!window.SimilarityToggleIsOn || !panel.Hidden) throw new InvalidOperationException("隐藏文件栏未保留逻辑开关或仍显示面板");
            main.IsThumbnailViewVisible = true;
            await SettleAsync(window);
            if (!window.SimilarityToggleIsOn || panel.Hidden) throw new InvalidOperationException("恢复文件栏未恢复已开启面板");
            checks.Add("toggle: background notification dispatched to UI; file bar hide/restore retains logical state and restores visible panel");

            // 1) toggle 与共享状态双向一致，并真实点击往返（回归"按钮点不动"）。
            filter.IsSimilarityPanelOpen = false;
            await SettleAsync(window);
            if (!panel.Hidden) throw new InvalidOperationException("收起时相似聚类面板应为隐藏");
            if (window.SimilarityToggleIsOn)
                throw new InvalidOperationException("收起状态下 toggle 未显示为未勾选");
            VerifyToggleText(window.SimilarityToggle, "收起");
            NativeSnapshots.SaveView(window.SimilarityToggle, Path.Combine(output, "native-similarity-toggle-off.png"));
            VerifyDragZonesClear(window);
            filter.IsSimilarityPanelOpen = true;
            await SettleAsync(window);
            if (!window.SimilarityToggleIsOn)
                throw new InvalidOperationException("展开后 toggle 未随共享状态勾选");
            if (panel.Hidden) throw new InvalidOperationException("展开后相似聚类面板仍隐藏");
            VerifyToggleText(window.SimilarityToggle, "展开");
            NativeSnapshots.SaveView(window.SimilarityToggle, Path.Combine(output, "native-similarity-toggle-on.png"));
            checks.Add("toggle: mirrors IsSimilarityPanelOpen in both directions");

            // 真实点击必须能开合面板：程序化赋值走的是另一条路径，掩盖过"点击读回旧状态"的回归。
            ClickToggle(window);
            await SettleAsync(window);
            if (filter.IsSimilarityPanelOpen || !panel.Hidden)
                throw new InvalidOperationException("点击 toggle 未能收起面板");
            ClickToggle(window);
            await SettleAsync(window);
            if (!filter.IsSimilarityPanelOpen || panel.Hidden)
                throw new InvalidOperationException("再次点击 toggle 未能展开面板");
            checks.Add("toggle: real click closes and reopens the panel (shared state follows)");
            filter.IsSimilarityPanelOpen = false;
            await SettleAsync(window);

            // 2) 逐方向核对让位几何与收起还原（每向自取收起态基线，避免跨向比较）。
            foreach (var row in new[] { true, false })
            {
                main.Settings.LayoutMode = row ? LayoutMode.Row : LayoutMode.Col;
                filter.IsSimilarityPanelOpen = false;
                await SettleAsync(window);
                if (main.IsRowLayout != row) throw new InvalidOperationException($"{row} 方向未按设置生效");
                var baselinePane = filePane.Frame;
                double baselineFilm = (double)window.FilmstripScroll.Frame.Width;

                filter.IsSimilarityPanelOpen = true;
                await SettleAsync(window);
                var film = window.FilmstripScroll;
                if (row)
                {
                    if ((double)film.Frame.Width >= baselineFilm)
                        throw new InvalidOperationException($"行布局展开后胶片栏未让出宽度：{film.Frame.Width} ≥ {baselineFilm}");
                    if (Math.Abs((double)panel.Frame.Top - (double)film.Frame.Top) > 1)
                        throw new InvalidOperationException($"行布局相似面板未与胶片栏顶边对齐：panel {panel.Frame} film {film.Frame}");
                    if ((double)panel.Frame.Left < (double)film.Frame.Right - 1)
                        throw new InvalidOperationException($"行布局相似面板未排在胶片栏右侧：panel {panel.Frame} film {film.Frame}");
                    if (Math.Abs((double)panel.Frame.Height - (double)panel.CrossSize) > 1)
                        throw new InvalidOperationException("行布局相似面板高度未按内容决定");
                }
                else if ((double)filePane.Frame.Width <= (double)baselinePane.Width)
                {
                    throw new InvalidOperationException($"列布局展开后文件栏未扩宽：{filePane.Frame.Width} ≤ {baselinePane.Width}");
                }
                if ((double)panel.CrossSize < 106)
                    throw new InvalidOperationException($"相似面板交叉轴 {panel.CrossSize} 小于旧壳 MinWidth 106");
                if (panel.Bounds.Width <= 0 || panel.Bounds.Height <= 0)
                    throw new InvalidOperationException($"相似面板尺寸非法：{panel.Bounds}");
                checks.Add($"geometry {(row ? "row" : "col")}: panel {panel.Bounds.Width:0}×{panel.Bounds.Height:0} cross={panel.CrossSize:0} filmstrip {baselineFilm:0}→{film.Frame.Width:0}");

                filter.IsSimilarityPanelOpen = false;
                await SettleAsync(window);
                if (!panel.Hidden) throw new InvalidOperationException("收起后相似聚类面板未隐藏");
                if (Math.Abs((double)filePane.Frame.Width - (double)baselinePane.Width) > 0.5
                    || Math.Abs((double)filePane.Frame.Height - (double)baselinePane.Height) > 0.5)
                    throw new InvalidOperationException($"收起后文件栏几何未还原：{filePane.Frame} 基线 {baselinePane}");
                if (Math.Abs((double)window.FilmstripScroll.Frame.Width - baselineFilm) > 0.5)
                    throw new InvalidOperationException($"收起后胶片栏宽度未还原：{window.FilmstripScroll.Frame.Width} 基线 {baselineFilm}");
                checks.Add($"collapse {(row ? "row" : "col")}: file pane and filmstrip geometry restored");
            }

            // 3) 列表态与空态互斥，且与共享 VM 一致。
            filter.IsSimilarityPanelOpen = true;
            await SettleAsync(window);
            var similarity = main.FileVM.SimilarityPanel;
            int items = similarity.SimilarItems.Count;
            if (similarity.HasItems != items > 0)
                throw new InvalidOperationException($"面板 HasItems 与相似项数不一致：{similarity.HasItems} / {items}");
            var labels = Labels(panel).ToArray();
            if (similarity.HasItems)
            {
                if (labels.Contains("尚未提取特征", StringComparer.Ordinal))
                    throw new InvalidOperationException("有相似项时仍显示空状态文案");
                checks.Add($"list: {items} similar items rendered, unindexed = {similarity.UnindexedCount}");
            }
            else
            {
                // 与共享 VM 的占位语义逐项对齐：有未提取时显示"尚未提取特征"+按钮，全部已入库的空结果显示"无相似照片"。
                bool unindexedPlaceholder = similarity.ShowUnindexedPlaceholder;
                bool fullPlaceholder = similarity.ShowFullPlaceholder;
                if (unindexedPlaceholder != labels.Contains("尚未提取特征", StringComparer.Ordinal))
                    throw new InvalidOperationException($"未提取占位与 VM 不一致：{string.Join(" | ", labels)}");
                if (fullPlaceholder != labels.Contains("无相似照片", StringComparer.Ordinal))
                    throw new InvalidOperationException($"无相似照片占位与 VM 不一致：{string.Join(" | ", labels)}");
                checks.Add($"empty: unindexedPlaceholder={unindexedPlaceholder} fullPlaceholder={fullPlaceholder} unindexed={similarity.UnindexedCount}");
            }
            NativeSnapshots.SaveMain(window, Path.Combine(output, "native-similarity.png"));

            // 5) 相似项卡片选中语义（原始设计）：点面板里的项 → 该卡片高亮 + 主图切换，
            //    胶片栏的锚点高亮保持不变；两处高亮同时存在。
            await VerifyCardSelectionAsync(main, window, output, checks);
        }
        finally
        {
            main.Settings.LayoutMode = originalMode;
            filter.IsSimilarityPanelOpen = originalOpen;
            await SettleAsync(window);
            await File.WriteAllLinesAsync(Path.Combine(output, "native-similarity-checks.txt"), checks);
        }
        Console.WriteLine("[NativeSmoke] PASS similarity");
    }

    /// <summary>真实点击筛选条 toggle（与鼠标同一入口），回归"按钮点不动"。</summary>
    /// <param name="window">原生主窗口。</param>
    private static void ClickToggle(MacMainWindow window)
    {
        var toggle = window.SimilarityToggle;
        if (!toggle.Enabled) throw new InvalidOperationException("相似聚类 toggle 处于禁用状态");
        toggle.PerformClick(toggle);
    }

    /// <summary>
    /// 相似聚类 toggle 的标题必须始终是业务文案（含 attributed/alternate 三条路径）：
    /// 只设 AttributedTitle 时旧写法会露出系统占位串 "Button"。
    /// </summary>
    /// <param name="toggle">筛选条 toggle。</param>
    /// <param name="stage">当前状态描述（用于报错）。</param>
    private static void VerifyToggleText(NSButton toggle, string stage)
    {
        if (toggle.Title != "相似聚类" || toggle.AlternateTitle != "相似聚类")
            throw new InvalidOperationException($"{stage}状态 toggle 标题异常：Title={toggle.Title} AlternateTitle={toggle.AlternateTitle}");
        if (toggle.AttributedTitle?.Value != "相似聚类" || toggle.AttributedAlternateTitle?.Value != "相似聚类")
            throw new InvalidOperationException($"{stage}状态 toggle 富文本标题异常：{toggle.AttributedTitle?.Value} / {toggle.AttributedAlternateTitle?.Value}");
    }

    /// <summary>顶边悬停拖动区必须绕开筛选条全部可点控件，否则拖动层会盖住按钮使其时灵时不灵。</summary>
    private static void VerifyDragZonesClear(MacMainWindow window)
    {
        var toggle = window.SimilarityToggle.Frame;
        foreach (var zone in window.DragZones)
        {
            if (zone.Frame.IntersectsWith(toggle))
                throw new InvalidOperationException($"拖动区 {zone.Frame} 盖住了相似聚类 toggle {toggle}");
        }
    }

    /// <summary>
    /// 相似项卡片选中语义：在独立探针子目录里放三份唯一名字的素材（指纹互不相同，入库后互为相似项）
    /// 加一个无法解码的孤立文件（永远计入未提取，让列表态动作区可见）→ 走共享"提取全部"入口建库 →
    /// 先核对行布局宽度含停靠区（1/2 项时列表能完整显示卡片）→ 点相似面板里的另一张 →
    /// 断言面板卡片与胶片栏卡片同时高亮、锚点身份与显示身份分离。
    /// 探针目录写在验收输出目录下并在结束时删除。
    /// </summary>
    private static async Task VerifyCardSelectionAsync(MainViewModel main, MacMainWindow window, string output, List<string> checks)
    {
        var fixture = main.CurrentFile ?? throw new InvalidOperationException("缺少当前照片");
        string probeFolder = Path.Combine(output, $"sim-probe-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(probeFolder);
        string first = Path.Combine(probeFolder, "probe-a.jpg");
        string second = Path.Combine(probeFolder, "probe-b.jpg");
        string third = Path.Combine(probeFolder, "probe-c.jpg");
        string orphan = Path.Combine(probeFolder, "probe-d.jpg");
        File.Copy(fixture.File.RequireLocalPath(), first, true);
        File.Copy(fixture.File.RequireLocalPath(), second, true);
        File.Copy(fixture.File.RequireLocalPath(), third, true);
        // 不可解码且没有拍摄时间的孤立文件：SIM-02 视为未提取，列表态停靠区因此可见（SIM-07 的宽度条件）。
        await File.WriteAllTextAsync(orphan, "not an image");
        try
        {
            await main.FolderVM.OpenImageAsync(new LocalFileRef(first));
            await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(first), "探针 A 成为当前照片");
            var similarity = main.FileVM.SimilarityPanel;
            similarity.StartIndexingCommand();
            var pool = main.FolderVM.AllFiles;
            await WaitAsync(() => similarity.SimilarItems.Count >= 2
                && similarity.SimilarItems.All(entry => pool.Contains(entry.File)), "探针目录的相似项出现");
            await VerifyRowListWidthsAsync(main, window, similarity, checks);
            await WaitAsync(() => similarity.SimilarItems.Count >= 2, "两个相似项就绪");

            var anchor = main.CurrentFile!;
            var item = similarity.SimilarItems.First(entry => !ReferenceEquals(entry.File, anchor));
            similarity.SelectItemCommand(item);
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, item.File), "点相似项切换主图");
            await WaitAsync(() => FindCard(window.Similarity.List, item.File) is { IsHighlighted: true }, "相似面板卡片高亮");
            await WaitAsync(() => FindCard(window.Filmstrip, anchor) is { IsHighlighted: true }, "胶片栏锚点保持高亮");

            if (item.File.IsCurrentImage is false) throw new InvalidOperationException("被点中的相似项未成为主图身份");
            if (anchor.IsCurrent is false) throw new InvalidOperationException("胶片栏锚点高亮被相似项点击抢走（原始设计要求保留）");
            if (anchor.IsCurrentImage) throw new InvalidOperationException("锚点的显示身份未让给被点中的相似项");
            checks.Add($"selection: similar card {item.File.DisplayName} and filmstrip anchor {anchor.DisplayName} highlighted together");
            if (window.Similarity.List is { } cards)
                NativeSnapshots.SaveView(cards, Path.Combine(output, "native-similarity-cards.png"));
            NativeSnapshots.SaveView(window.Filmstrip, Path.Combine(output, "native-similarity-filmstrip.png"));
        }
        finally
        {
            Directory.Delete(probeFolder, true);
        }
    }

    /// <summary>
    /// SIM-07：行布局面板宽度必须含列表态右侧停靠区。两个相似项时列表容得下两张卡（2 × 96 + 列表内边距 8），
    /// 收窄到 1 项时仍不小于单卡宽 96——未预留停靠区时列表会被动作区挤成几点的窄条。
    /// </summary>
    private static async Task VerifyRowListWidthsAsync(MainViewModel main, MacMainWindow window,
        SimilarityPanelViewModel similarity, List<string> checks)
    {
        var mode = main.Settings.LayoutMode;
        int maxResults = main.Settings.SimilarityMaxResults;
        var list = window.Similarity.List ?? throw new InvalidOperationException("相似面板缺少列表");
        var scroll = list.EnclosingScrollView ?? throw new InvalidOperationException("相似面板列表缺少滚动宿主");
        double ListWidth()
        {
            window.Window.ContentView?.LayoutSubtreeIfNeeded();
            list.LayoutSubtreeIfNeeded();
            return (double)scroll.ContentView.Bounds.Width;
        }
        try
        {
            main.Settings.SimilarityMaxResults = Math.Max(2, maxResults);
            main.Settings.LayoutMode = LayoutMode.Row;
            await SettleAsync(window);
            await WaitAsync(() => similarity.SimilarItems.Count == 2, "两个相似项");
            await WaitAsync(() => similarity.HasUnindexed && !similarity.IsStateIndexing, "列表态停靠区（未提取计数）出现");
            if (!similarity.ShowActionArea) throw new InvalidOperationException("列表态动作区未显示，无法核对停靠区宽度");
            double twoItemList = ListWidth();
            double twoItemExpected = 2 * FilmstripGeometry.CardWidth + SimilarityPanelGeometry.ListPadding;
            if (Math.Abs(twoItemList - twoItemExpected) > 1.5)
                throw new InvalidOperationException($"行布局 2 项时列表宽 {twoItemList:0.##} 不是卡片 + 内边距的 {twoItemExpected:0.##}（停靠区未计入面板宽度）");
            if (list.IndexPathsForVisibleItems.Count < 2)
                throw new InvalidOperationException($"行布局 2 项时只显示 {list.IndexPathsForVisibleItems.Count} 张卡片");

            main.Settings.SimilarityMaxResults = 1;
            await WaitAsync(() => similarity.SimilarItems.Count == 1, "最多返回 1 项");
            await SettleAsync(window);
            double oneItemList = ListWidth();
            if (oneItemList + .5 < FilmstripGeometry.CardWidth)
                throw new InvalidOperationException($"行布局 1 项时列表宽 {oneItemList:0.##} 小于卡片宽 {FilmstripGeometry.CardWidth}（停靠区未计入面板宽度）");
            double oneItemExpected = FilmstripGeometry.CardWidth + SimilarityPanelGeometry.ListPadding;
            if (Math.Abs(oneItemList - oneItemExpected) > 1.5)
                throw new InvalidOperationException($"行布局 1 项时列表宽 {oneItemList:0.##} 不是单卡 + 内边距的 {oneItemExpected:0.##}");
            checks.Add($"row width: 1 item list {oneItemList:0} and 2 items list {twoItemList:0} both fit their cards (dock width reserved, SIM-07)");
        }
        finally
        {
            main.Settings.SimilarityMaxResults = maxResults;
            main.Settings.LayoutMode = mode;
            await SettleAsync(window);
        }
    }

    /// <summary>在集合视图里按文件找到已实现的卡片；未进入可见范围时返回 null。</summary>
    private static MacThumbnailItem? FindCard(NSCollectionView? collection, ImageFile file)
    {
        if (collection == null) return null;
        foreach (var path in collection.IndexPathsForVisibleItems)
        {
            if (collection.GetItem(path) is MacThumbnailItem card && ReferenceEquals(card.BoundFile, file)) return card;
        }
        return null;
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    /// <summary>面板子树里实际可见的非空文本（隐藏的空状态/列表态文案不计入）。</summary>
    private static IEnumerable<string> Labels(NSView root)
    {
        foreach (var view in Descendants(root))
            if (view is NSTextField field && !string.IsNullOrEmpty(field.StringValue) && IsVisible(field))
                yield return field.StringValue;
    }

    /// <summary>沿父链判断视图是否真的可见。</summary>
    private static bool IsVisible(NSView view)
    {
        for (NSView? current = view; current != null; current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    /// <summary>深度遍历视图树。</summary>
    private static IEnumerable<NSView> Descendants(NSView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>切换布局后等待根视图重排完成。</summary>
    private static async Task SettleAsync(MacMainWindow window)
    {
        await Task.Delay(220);
        window.Window.ContentView?.LayoutSubtreeIfNeeded();
        await Task.Delay(120);
    }
}
