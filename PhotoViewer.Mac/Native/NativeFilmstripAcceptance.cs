using AppKit;
using Foundation;
using CoreGraphics;
using PhotoViewer.Core.Image;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>真实胶片栏布局/顺序/滚动回归，只读照片并使用冒烟模式的隔离设置。</summary>
internal static class NativeFilmstripAcceptance
{
    /// <summary>按两向布局导出原生几何与截图，并核对从左到右/从上到下的业务顺序。</summary>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        window.Main.Settings.SameNameAsOnePhoto = true;
        window.Main.IsThumbnailViewVisible = true;
        var filter = window.Main.FileVM.FilterBar;
        filter.SortMode = SortMode.Name;
        filter.SortOrder = SortOrder.Ascending;
        await Task.Delay(800);
        var observations = new List<string>();
        foreach (var mode in new[] { LayoutMode.Col, LayoutMode.Row })
        {
            window.Main.Settings.LayoutMode = mode;
            window.Window.SetContentSize(new CGSize(700, 480));
            await Task.Delay(300);
            foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
            {
                filter.SortOrder = order;
                await Task.Delay(250);
                var files = window.Main.FileVM.ThumbnailList.FilteredFiles;
                var expected = order == SortOrder.Ascending ? files.OrderBy(file => file.Name).ToArray() : files.OrderByDescending(file => file.Name).ToArray();
                if (!files.SequenceEqual(expected)) throw new InvalidOperationException("业务文件排序错误");
                int[] indicesToTest = files.Count <= 20
                    ? Enumerable.Range(0, files.Count).ToArray()
                    : [0, 1, 2, files.Count / 4, files.Count / 2, files.Count * 3 / 4, files.Count - 2, files.Count - 1];
                foreach (int index in indicesToTest.Distinct())
                {
                    window.Main.CurrentFile = files[index];
                    window.Main.FileVM.ThumbnailList.ScrollToCurrent();
                    await WaitScrollSettledAsync((MacFilmstripScrollView)window.FilmstripScroll);
                    window.Filmstrip.LayoutSubtreeIfNeeded();
                    using var path = NSIndexPath.FromItemSection(index, 0);
                    var attributes = window.Filmstrip.CollectionViewLayout!.GetLayoutAttributesForItem(path) ?? throw new InvalidOperationException("卡片几何缺失");
                    var frame = attributes.Frame;
                    var clip = window.FilmstripScroll.ContentView.Bounds;
                    if (frame.X < clip.X - 1 || frame.Y < clip.Y - 1 || frame.X + frame.Width > clip.X + clip.Width + 1 || frame.Y + frame.Height > clip.Y + clip.Height + 1)
                        throw new InvalidOperationException($"{mode}/{order} 第 {index} 项未完整滚入：frame={frame}, clip={clip}, document={window.Filmstrip.Frame}");
                    double maximum = mode == LayoutMode.Row ? (double)(window.Filmstrip.Frame.Width - clip.Width) : (double)(window.Filmstrip.Frame.Height - clip.Height);
                    double centered = mode == LayoutMode.Row ? (double)(frame.X + (frame.Width - clip.Width) / 2) : (double)(frame.Y + (frame.Height - clip.Height) / 2);
                    double actual = mode == LayoutMode.Row ? (double)clip.X : (double)clip.Y;
                    if (Math.Abs(actual - Math.Clamp(centered, 0, Math.Max(0, maximum))) > 1)
                        throw new InvalidOperationException($"{mode}/{order} 第 {index} 项未居中：frame={frame}, clip={clip}");
                    if (window.Filmstrip.GetItem(path) is not MacThumbnailItem card || !ReferenceEquals(card.BoundFile, files[index]))
                        throw new InvalidOperationException($"{mode}/{order} 第 {index} 项视图与业务身份不同");
                    VerifyVisibleCards(window, $"{mode}/{order}/index-{index}");
                    if (index > 0)
                    {
                        using var previousPath = NSIndexPath.FromItemSection(index - 1, 0);
                        var previous = window.Filmstrip.CollectionViewLayout.GetLayoutAttributesForItem(previousPath)!.Frame;
                        bool horizontal = mode == LayoutMode.Row;
                        if (horizontal ? Math.Abs((double)(frame.Y - previous.Y)) > .1 || frame.X <= previous.X
                            : Math.Abs((double)(frame.X - previous.X)) > .1 || frame.Y <= previous.Y)
                            throw new InvalidOperationException("胶片栏出现换行、换列或几何顺序颠倒");
                    }
                    observations.Add($"{mode}/{order}/{index}\t{files[index].Name}\t{frame}\tclip={clip}\tdocument={window.Filmstrip.Frame}");
                }
                var savedCurrent = window.Main.CurrentFile ?? throw new InvalidOperationException("居中回归缺少当前照片");
                double extent = mode == LayoutMode.Row ? (double)(window.Filmstrip.Frame.Width - window.FilmstripScroll.ContentView.Bounds.Width)
                    : (double)(window.Filmstrip.Frame.Height - window.FilmstripScroll.ContentView.Bounds.Height);
                if (extent <= 0)
                {
                    if (mode == LayoutMode.Col || files.Count > 8) throw new InvalidOperationException("溢出列表缺少滚动范围");
                    continue;
                }
                await VerifyScrollInteractionsAsync(window, observations);
                var middle = mode == LayoutMode.Row ? new CGPoint(extent / 2, 0) : new CGPoint(0, extent / 2);
                window.FilmstripScroll.ContentView.ScrollToPoint(middle);
                window.FilmstripScroll.ReflectScrolledClipView(window.FilmstripScroll.ContentView);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(250);
                VerifyVisibleCards(window, $"{mode}/{order}/manual-scroll-middle");
                for (int step = 1; step <= 4; step++)
                {
                    double fraction = step / 5.0;
                    var pos = mode == LayoutMode.Row ? new CGPoint(extent * fraction, 0) : new CGPoint(0, extent * fraction);
                    window.FilmstripScroll.ContentView.ScrollToPoint(pos);
                    window.FilmstripScroll.ReflectScrolledClipView(window.FilmstripScroll.ContentView);
                    GC.Collect();
                    await Task.Delay(40);
                    VerifyVisibleCards(window, $"{mode}/{order}/scroll-step-{step}");
                }
                window.FilmstripScroll.ContentView.ScrollToPoint(middle);
                window.FilmstripScroll.ReflectScrolledClipView(window.FilmstripScroll.ContentView);
                await Task.Delay(80);
                if (window.Main.CurrentFile != savedCurrent || Math.Abs((double)(window.FilmstripScroll.ContentView.Bounds.X - middle.X)) > 1 || Math.Abs((double)(window.FilmstripScroll.ContentView.Bounds.Y - middle.Y)) > 1)
                    throw new InvalidOperationException("手动滚动被布局重置或误改当前照片");
                if (mode == LayoutMode.Row)
                {
                    if (!window.FilmstripScroll.HasHorizontalScroller || window.FilmstripScroll.HasVerticalScroller) throw new InvalidOperationException("横向胶片栏系统滚动轴配置错误");
                    using var wheel = new CGEvent(null, CGScrollEventUnit.Line, [-1]);
                    using var nativeWheel = NSEvent.Create(wheel) ?? throw new InvalidOperationException("原生滚轮事件创建失败");
                    window.FilmstripScroll.ScrollWheel(nativeWheel);
                    if (window.FilmstripScroll.ContentView.Bounds.X <= middle.X || window.Main.CurrentFile != savedCurrent)
                        throw new InvalidOperationException("横向胶片栏纵向鼠标滚轮未滚动或误切照片");
                }
                window.Main.IsThumbnailViewVisible = false;
                window.FilmstripScroll.ContentView.ScrollToPoint(CGPoint.Empty);
                window.Main.IsThumbnailViewVisible = true;
                await Task.Delay(450);
                double restored = mode == LayoutMode.Row ? (double)window.FilmstripScroll.ContentView.Bounds.X : (double)window.FilmstripScroll.ContentView.Bounds.Y;
                int restoredIndex = files.IndexOf(savedCurrent);
                using var restoredPath = NSIndexPath.FromItemSection(restoredIndex, 0);
                var restoredFrame = window.Filmstrip.CollectionViewLayout!.GetLayoutAttributesForItem(restoredPath)?.Frame
                    ?? throw new InvalidOperationException("恢复后当前项几何缺失");
                double restoredCenter = mode == LayoutMode.Row
                    ? (double)(restoredFrame.X + (restoredFrame.Width - window.FilmstripScroll.ContentView.Bounds.Width) / 2)
                    : (double)(restoredFrame.Y + (restoredFrame.Height - window.FilmstripScroll.ContentView.Bounds.Height) / 2);
                double restoredExpected = Math.Clamp(restoredCenter, 0, Math.Max(0, extent));
                if (Math.Abs(restored - restoredExpected) > 1 || window.Main.CurrentFile != savedCurrent)
                    throw new InvalidOperationException("重新显示胶片栏未定位当前照片");
                observations.Add($"{mode}/{order}: hide/show restores current photo position");
            }
            window.Window.SetContentSize(new CGSize(900, 650));
            await Task.Delay(200);
            VerifyFilterMenus(window, observations);
            await Task.Delay(250);
            NativeSnapshots.SaveMain(window, System.IO.Path.Combine(output, "filmstrip-" + mode + ".png"));
            NativeSnapshots.SaveView(window.FilmstripScroll, System.IO.Path.Combine(output, "filmstrip-controls-" + mode + ".png"));
        }
        window.Main.Settings.LayoutMode = LayoutMode.Col;
        filter.SortOrder = SortOrder.Ascending;
        window.Main.CurrentFile = window.Main.FileVM.ThumbnailList.FilteredFiles[0];
        await Task.Delay(250);
        NativeSnapshots.SaveMain(window, System.IO.Path.Combine(output, "filmstrip-final.png"));
        await VerifyLazyCardDecodingAsync(window, observations);
        string report = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(window.Main.CurrentFile!.File.PathKey)) == "PhotoViewerTest" ? "filmstrip-five-checks.txt" : "filmstrip-many-checks.txt";
        await File.WriteAllLinesAsync(System.IO.Path.Combine(output, report), observations);
        File.Delete(System.IO.Path.Combine(output, "native-smoke-error.txt"));
    }

    /// <summary>在真实卡片上点击并执行翻页/交换/排序，核对定位条件、动画中间帧与用户接管。</summary>
    private static async Task VerifyScrollInteractionsAsync(MacMainWindow window, List<string> observations)
    {
        var scroll = (MacFilmstripScrollView)window.FilmstripScroll;
        var files = window.Main.FileVM.ThumbnailList.FilteredFiles;
        scroll.ContentView.ScrollToPoint(CGPoint.Empty);
        scroll.ReflectScrolledClipView(scroll.ContentView);
        await Task.Delay(80);
        MacThumbnailItem card;
        using (var clickPath = NSIndexPath.FromItemSection(1, 0))
            card = window.Filmstrip.GetItem(clickPath) as MacThumbnailItem ?? throw new InvalidOperationException("点选回归缺少可见卡片");
        var location = card.View.ConvertPointToView(new CGPoint(15, 15), null);
        using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, location, 0, NSProcessInfo.ProcessInfo.SystemUptime,
            window.Window.WindowNumber, null!, 1, 1, 1) ?? throw new InvalidOperationException("卡片鼠标事件创建失败");
        card.View.MouseDown(click);
        await Task.Delay(450);
        if (window.Main.CurrentFile != files[1] || AxisOffset(scroll) > 1)
            throw new InvalidOperationException("手动点选误触居中或未切图");
        window.Main.CurrentFile = files[^1];
        await Task.Delay(100);
        if (AxisOffset(scroll) > 1) throw new InvalidOperationException("普通 CurrentFile 赋值误触居中");
        window.Main.FileVM.ThumbnailList.ScrollToCurrent();
        double maximum = scroll.Horizontal ? (double)(window.Filmstrip.Frame.Width - scroll.ContentView.Bounds.Width)
            : (double)(window.Filmstrip.Frame.Height - scroll.ContentView.Bounds.Height);
        await Task.Delay(100);
        double intermediate = AxisOffset(scroll);
        if (!NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion && (intermediate <= 1 || intermediate >= maximum - 1))
            throw new InvalidOperationException("居中没有产生动画中间帧");
        await Task.Delay(350);
        if (Math.Abs(AxisOffset(scroll) - maximum) > 1) throw new InvalidOperationException("动画终点未受首尾边界约束");
        window.Execute("Exchange");
        await Task.Delay(450);
        if (window.Main.CurrentFile != files[1] || Math.Abs(AxisOffset(scroll) - maximum) > 1)
            throw new InvalidOperationException("切换上一张误触居中");
        window.Execute("Next");
        await Task.Delay(450);
        if (window.Main.CurrentFile != files[2] || AxisOffset(scroll) >= maximum - 1)
            throw new InvalidOperationException("下一张命令未主动定位");

        window.Main.CurrentFile = files[^1];
        window.Main.FileVM.ThumbnailList.ScrollToCurrent();
        await Task.Delay(60);
        using var wheel = new CGEvent(null, CGScrollEventUnit.Line, [1]);
        using var nativeWheel = NSEvent.Create(wheel) ?? throw new InvalidOperationException("动画接管滚轮创建失败");
        scroll.ScrollWheel(nativeWheel);
        if (scroll.IsAnimating) throw new InvalidOperationException("手动滚动未取消居中动画");
        await Task.Delay(450);
        double interrupted = AxisOffset(scroll);
        await Task.Delay(450);
        if (Math.Abs(AxisOffset(scroll) - interrupted) > 1)
            throw new InvalidOperationException($"手动滚动稳定后位置继续变化：before={interrupted}, after={AxisOffset(scroll)}, animating={scroll.IsAnimating}");

        scroll.ContentView.ScrollToPoint(CGPoint.Empty);
        scroll.ReflectScrolledClipView(scroll.ContentView);
        await Task.Delay(80);
        using (var interruptPath = NSIndexPath.FromItemSection(1, 0))
            card = window.Filmstrip.GetItem(interruptPath) as MacThumbnailItem ?? throw new InvalidOperationException("接管回归缺少可见卡片");
        window.Main.FileVM.ThumbnailList.ScrollToCurrent();
        card.View.MouseDown(click);
        await Task.Delay(450);
        if (AxisOffset(scroll) > 1 || window.Main.CurrentFile != files[1])
            throw new InvalidOperationException("点选未撤销排队中的居中请求");

        window.Main.CurrentFile = files[^1];
        window.Main.FileVM.ThumbnailList.ScrollToCurrent();
        await Task.Delay(80);
        window.Main.CurrentFile = files[0];
        window.Main.FileVM.ThumbnailList.ScrollToCurrent();
        await Task.Delay(450);
        if (AxisOffset(scroll) > 1) throw new InvalidOperationException("连续定位未以最后请求为准");

        window.Main.CurrentFile = files[files.Count / 2];
        var filter = window.Main.FileVM.FilterBar;
        SortOrder original = filter.SortOrder;
        filter.SortOrder = original == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
        await Task.Delay(450);
        if (AxisOffset(scroll) > 1) throw new InvalidOperationException("排序刷新误触居中");
        filter.SortOrder = original;
        await Task.Delay(450);
        if (AxisOffset(scroll) > 1) throw new InvalidOperationException("恢复排序误触居中");
        filter.SelectedRatingFilter = "None";
        await Task.Delay(450);
        if (AxisOffset(scroll) > 1) throw new InvalidOperationException("筛选刷新误触居中");
        filter.SelectedRatingFilter = "All";
        await Task.Delay(150);
        window.Main.CurrentFile = window.Main.FileVM.ThumbnailList.FilteredFiles[^1];
        observations.Add($"{window.Main.Settings.LayoutMode}/{original}: card click, ordinary selection, exchange, sort/filter preserve offset; explicit navigation animates (intermediate={intermediate:0.##}, endpoint={maximum:0.##}); wheel/click cancel; latest request wins");
    }

    /// <summary>
    /// FILM-05：在自有延迟加载素材目录（两张自有好图副本 + 一个故意不可解码的坏文件）上核对卡片身份与几何不等解码、
    /// 滚动不等解码、坏文件仍可选为主图且不呈现伪造图像；作者照片全程只读，收尾还原进入本检查前的目录与当前照片。
    /// </summary>
    private static async Task VerifyLazyCardDecodingAsync(MacMainWindow window, List<string> observations)
    {
        var thumbnailList = window.Main.FileVM.ThumbnailList;
        var entryFile = window.Main.CurrentFile ?? throw new InvalidOperationException("延迟加载回归缺少进入时的当前照片");
        string entryPath = entryFile.File.PathKey;
        var entrySize = window.Window.ContentView!.Bounds.Size;
        bool entryThumbnailVisible = window.Main.IsThumbnailViewVisible;
        window.Main.IsThumbnailViewVisible = true;
        string root = PrepareLazyFixtures();
        try
        {
            await window.Main.FolderVM.OpenFolderAsync(new MacFolderRef(root));
            // 身份与几何先于解码就绪：这里不等待任何解码，先就地取到“仍在异步解码”的证据。
            var files = thumbnailList.FilteredFiles;
            if (files.Count != 3) throw new InvalidOperationException($"延迟加载素材项目数错误：{files.Count}");
            var goods = files.Where(file => !file.Name.StartsWith("broken", StringComparison.Ordinal)).ToArray();
            if (goods.Length != 2 || files.Any(file => string.IsNullOrEmpty(file.Name))) throw new InvalidOperationException("延迟加载素材身份不完整");
            bool stillDecoding = thumbnailList.IsThumbnailLoadingBusy();
            if (!stillDecoding && !goods.Any(file => file.Thumbnail == null))
                throw new InvalidOperationException("打开目录后缩略图已全部解码，无法证明卡片身份与几何不等待解码");
            var cards = new MacThumbnailItem[files.Count];
            for (int index = 0; index < files.Count; index++) cards[index] = await WaitForIndexCardAsync(window, index, files[index]);
            foreach (var card in cards)
            {
                if (card.View.Hidden || card.View.Superview != window.Filmstrip)
                    throw new InvalidOperationException("延迟加载阶段出现未挂载或被隐藏的卡片");
                if (!CardLabels(card).Contains(card.BoundFile!.DisplayName))
                    throw new InvalidOperationException($"卡片未显示业务身份：{card.BoundFile.Name}");
            }
            observations.Add($"list identity/count={files.Count} and all visible cards usable with names while the good thumbnails are still decoding (busy={stillDecoding})");

            await WaitAsync(() => goods.All(file => file.Thumbnail != null), "好图缩略图异步补齐");
            await WaitAsync(() => goods.All(file => file.PhotoDate != null), "好图日期就绪");
            if (!thumbnailList.FilteredFiles.SequenceEqual(files))
                throw new InvalidOperationException("后台评级阶段改变了业务列表顺序，无法按索引核对卡片");
            foreach (var card in cards)
                if (CardLabels(card).Count < 2) throw new InvalidOperationException($"卡片缺少文件名或第二行日期：{card.BoundFile!.Name}");
            observations.Add($"good thumbnails and card name/date arrive asynchronously for {string.Join("/", goods.Select(file => file.Name))}");

            // 视口小于全部卡片时滚一下：立刻核对可见卡片身份，不需要等待任何解码。
            window.Window.SetContentSize(new CGSize(700, 420));
            await Task.Delay(150);
            var scroll = (MacFilmstripScrollView)window.FilmstripScroll;
            double extent = (double)(window.Filmstrip.Frame.Height - scroll.ContentView.Bounds.Height);
            if (extent <= 1) throw new InvalidOperationException($"延迟加载素材在竖排下未产生滚动范围：{extent}");
            var beforeScroll = GeometricVisibleIndexes(window);
            scroll.ContentView.ScrollToPoint(new CGPoint(0, extent));
            scroll.ReflectScrolledClipView(scroll.ContentView);
            await Task.Delay(40);
            window.Filmstrip.LayoutSubtreeIfNeeded();
            if (AxisOffset(scroll) <= 1) throw new InvalidOperationException("延迟加载素材的滚动没有发生");
            var visible = GeometricVisibleIndexes(window);
            if (!visible.Except(beforeScroll).Any())
                throw new InvalidOperationException($"滚动后可见卡片没有变化，无法核对滚动不等解码：offset={AxisOffset(scroll):0.##}, extent={extent:0.##}, clip={scroll.ContentView.Bounds}, before=[{string.Join(",", beforeScroll.Order())}], after=[{string.Join(",", visible.Order())}]");
            VerifyVisibleCards(window, "lazy-fixtures/manual-scroll");
            foreach (int index in visible)
                if (!CardLabels(cards[index]).Contains(files[index].DisplayName))
                    throw new InvalidOperationException($"滚动后卡片未保留业务身份：{files[index].Name}");
            observations.Add($"scrolling rebinds visible cards to their business identity immediately (visible=[{string.Join(",", visible.Order())}], offset={AxisOffset(scroll):0.##})");

            // 点坏文件卡片：身份保留、不崩、不呈现伪造图像；再点好图卡片：主图正常出图。
            var broken = files.Single(file => file.Name.StartsWith("broken", StringComparison.Ordinal));
            int brokenIndex = files.IndexOf(broken);
            int goodIndex = files.IndexOf(goods[0]);
            scroll.ContentView.ScrollToPoint(CGPoint.Empty);
            scroll.ReflectScrolledClipView(scroll.ContentView);
            await Task.Delay(60);
            await ClickCardAsync(window, await WaitForIndexCardAsync(window, brokenIndex, broken));
            if (!ReferenceEquals(window.Main.CurrentFile, broken)) throw new InvalidOperationException("坏文件卡片未保留业务身份为当前照片");
            await WaitAsync(() => window.Session.Error.Length > 0, "坏文件解码失败可见");
            if (window.Session.Image != null) throw new InvalidOperationException("坏文件在真实读图路径下呈现了伪造图像");
            observations.Add($"undecodable card stays selectable as the current photo and reports '{window.Session.Error}' without a fabricated image");

            await ClickCardAsync(window, await WaitForIndexCardAsync(window, goodIndex, goods[0]));
            if (!ReferenceEquals(window.Main.CurrentFile, goods[0])) throw new InvalidOperationException("好图卡片未切回主图身份");
            await WaitAsync(() => window.Session.Image != null && window.Session.Error.Length == 0, "好图主图出图");
            observations.Add($"{goods[0].Name} still shows the main photo after the undecodable card");
        }
        finally
        {
            window.Window.SetContentSize(entrySize);
            window.Main.IsThumbnailViewVisible = entryThumbnailVisible;
            if (window.Main.CurrentFile?.File.PathKey != entryPath) await window.Main.FolderVM.OpenImageAsync(new MacFileRef(entryPath));
            await Task.Delay(150);
        }
    }

    /// <summary>创建/刷新本套件自有的延迟加载素材目录：两张自有好图副本加一个故意不可解码的坏文件（幂等，作者照片只读）。</summary>
    private static string PrepareLazyFixtures()
    {
        string root = System.IO.Path.GetFullPath("release/native-p2-acceptance/filmstrip-lazy-fixtures");
        Directory.CreateDirectory(root);
        foreach (string stale in Directory.GetFiles(root)) File.Delete(stale);
        string sources = System.IO.Path.GetFullPath("release/native-p2-fixtures");
        foreach (string name in new[] { "color-srgb.jpg", "color-p3.jpg" })
            File.Copy(System.IO.Path.Combine(sources, name), System.IO.Path.Combine(root, name), true);
        File.WriteAllBytes(System.IO.Path.Combine(root, "broken.jpg"),
            System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("PhotoViewer filmstrip broken fixture: not an image\n", 24))));
        return root;
    }

    /// <summary>在真实卡片上派发左键按下（与既有卡片点选回归同一路径）。</summary>
    private static async Task ClickCardAsync(MacMainWindow window, MacThumbnailItem card)
    {
        var location = card.View.ConvertPointToView(new CGPoint(15, 15), null);
        using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, location, 0, NSProcessInfo.ProcessInfo.SystemUptime,
            window.Window.WindowNumber, null!, 1, 1, 1) ?? throw new InvalidOperationException("卡片鼠标事件创建失败");
        card.View.MouseDown(click);
        await Task.Delay(80);
    }

    /// <summary>按布局几何与当前裁剪区算出真正可见的业务索引（实例化集合可能多于几何可见项）。</summary>
    private static HashSet<int> GeometricVisibleIndexes(MacMainWindow window)
    {
        var clip = window.FilmstripScroll.ContentView.Bounds;
        return window.Filmstrip.CollectionViewLayout!.GetLayoutAttributesForElements(clip)
            .Where(attribute => attribute.IndexPath != null)
            .Where(attribute => { var intersect = CGRect.Intersect(attribute.Frame, clip); return intersect.Width > 1 && intersect.Height > 1; })
            .Select(attribute => (int)attribute.IndexPath!.Item)
            .ToHashSet();
    }

    /// <summary>等待指定业务索引的卡片真实建立并绑定到该身份。</summary>
    private static async Task<MacThumbnailItem> WaitForIndexCardAsync(MacMainWindow window, int index, ImageFile file)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            window.Filmstrip.LayoutSubtreeIfNeeded();
            using (var path = NSIndexPath.FromItemSection(index, 0))
                if (window.Filmstrip.GetItem(path) is MacThumbnailItem card && ReferenceEquals(card.BoundFile, file)) return card;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"胶片栏第 {index} 项（{file.Name}）卡片未建立");
            await Task.Delay(25);
        }
    }

    /// <summary>卡片当前显示的非空文字（文件名与第二行日期），隐藏的抖动徽标不计入。</summary>
    private static List<string> CardLabels(MacThumbnailItem card)
    {
        var labels = new List<string>();
        var pending = new Queue<NSView>([card.View]);
        while (pending.Count > 0)
            foreach (var subview in pending.Dequeue().Subviews)
            {
                if (subview is NSTextField field && !field.Hidden && !string.IsNullOrEmpty(field.StringValue)) labels.Add(field.StringValue);
                pending.Enqueue(subview);
            }
        return labels;
    }

    /// <summary>轮询等待真实状态并设定有限超时，失败不输出虚假的通过报告。</summary>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(name);
            await Task.Delay(25);
        }
    }

    /// <summary>等待胶片栏 350ms 缓出动画结束并稳定。</summary>
    private static async Task WaitScrollSettledAsync(MacFilmstripScrollView scroll)
    {
        await Task.Delay(420);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (scroll.IsAnimating && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        await Task.Delay(30);
    }

    /// <summary>核对当前可见裁剪区内的每一张卡片均已实例化、挂载、未隐藏且 Frame 与布局属性严格一致（无空缺）。</summary>
    private static void VerifyVisibleCards(MacMainWindow window, string context)
    {
        var clip = window.FilmstripScroll.ContentView.Bounds;
        var files = window.Main.FileVM.ThumbnailList.FilteredFiles;
        var visibleIndexes = window.Filmstrip.IndexPathsForVisibleItems.ToArray<NSIndexPath>().Select(p => (int)p.Item).ToHashSet();
        var expectedAttrs = window.Filmstrip.CollectionViewLayout!.GetLayoutAttributesForElements(clip);
        foreach (var attr in expectedAttrs)
        {
            var intersect = CGRect.Intersect(attr.Frame, clip);
            if (intersect.IsEmpty || intersect.Width <= 1 || intersect.Height <= 1) continue;
            var indexPath = attr.IndexPath ?? throw new InvalidOperationException($"{context} 缺少 IndexPath");
            int itemIndex = (int)indexPath.Item;
            if (!visibleIndexes.Contains(itemIndex))
                throw new InvalidOperationException($"{context} 第 {itemIndex} 项未进入 IndexPathsForVisibleItems（出现空缺），visible=[{string.Join(",", visibleIndexes.Order())}], clip={clip}, frame={attr.Frame}");
            if (window.Filmstrip.GetItem(indexPath) is not MacThumbnailItem card)
                throw new InvalidOperationException($"{context} 第 {itemIndex} 项可见卡片未实例化（出现空缺），clip={clip}, frame={attr.Frame}");
            if (!ReferenceEquals(card.BoundFile, files[itemIndex]))
                throw new InvalidOperationException($"{context} 第 {itemIndex} 项可见卡片绑定文件错位");
            if (card.View.Superview != window.Filmstrip || card.View.Hidden)
                throw new InvalidOperationException($"{context} 第 {itemIndex} 项可见卡片未挂载或被隐藏");
            if (Math.Abs((double)(card.View.Frame.X - attr.Frame.X)) > .5 || Math.Abs((double)(card.View.Frame.Y - attr.Frame.Y)) > .5)
                throw new InvalidOperationException($"{context} 第 {itemIndex} 项可见卡片 Frame 错位：actual={card.View.Frame}, expected={attr.Frame}, clip={clip}");
        }
    }

    /// <summary>读取当前真实裁剪视口的滚动主轴位置。</summary>
    private static double AxisOffset(NSScrollView scroll)
        => scroll is MacFilmstripScrollView { Horizontal: true } ? (double)scroll.ContentView.Bounds.X : (double)scroll.ContentView.Bounds.Y;

    /// <summary>打开真实系统筛选菜单，以屏幕几何验证左边缘对齐、垂直无重叠、选中标记及键盘选择事件。</summary>
    private static void VerifyFilterMenus(MacMainWindow window, List<string> observations)
    {
        var popups = window.Window.ContentView!.Subviews.SelectMany(view => view.Subviews).OfType<NSPopUpButton>().ToArray();
        if (popups.Length != 3) throw new InvalidOperationException("筛选下拉框数量错误");
        foreach (var popup in popups)
        {
            Exception? failure = null;
            bool inspected = false;
            var clickPoint = popup.ConvertPointToView(new CGPoint(popup.Bounds.Width / 2, popup.Bounds.Height / 2), null);
            using var inspect = NSTimer.CreateTimer(.2, _ =>
            {
                try
                {
                    var buttonFrame = window.Window.ConvertRectToScreen(popup.ConvertRectToView(popup.Bounds, null));
                    var menuFrame = popup.Menu!.AccessibilityFrame;
                    inspected = true;
                    if (menuFrame.Height <= 0 || Math.Abs((double)(menuFrame.X - buttonFrame.X)) > 1 || menuFrame.Width < buttonFrame.Width)
                        throw new InvalidOperationException($"筛选菜单与按钮未对齐：button={buttonFrame}, menu={menuFrame}");
                    double gap = (double)(buttonFrame.Y - menuFrame.Y - menuFrame.Height);
                    if (gap < 2 || gap > 8)
                        throw new InvalidOperationException($"筛选菜单垂直间距错误：gap={gap}, button={buttonFrame}, menu={menuFrame}");
                    if (popup.SelectedItem?.State != NSCellStateValue.On)
                        throw new InvalidOperationException("筛选菜单丢失选中标记");
                    observations.Add($"filter menu {popup.TitleOfSelectedItem}: button={buttonFrame}, menu={menuFrame}, gap={gap}; selected checkmark retained");
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    popup.Menu!.CancelTracking();
                    using var release = NSEvent.MouseEvent(NSEventType.LeftMouseUp, clickPoint, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                        window.Window.WindowNumber, null!, 1, 1, 0) ?? throw new InvalidOperationException("筛选鼠标释放事件创建失败");
                    NSApplication.SharedApplication.PostEvent(release, false);
                }
            });
            NSRunLoop.Main.AddTimer(inspect, NSRunLoopMode.Common);
            using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, clickPoint, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                window.Window.WindowNumber, null!, 1, 1, 1) ?? throw new InvalidOperationException("筛选鼠标事件创建失败");
            popup.MouseDown(click);
            if (failure != null) throw failure;
            if (!inspected) throw new InvalidOperationException("鼠标点击未打开筛选菜单");
        }
        var sort = popups[0];
        var filter = window.Main.FileVM.FilterBar;
        SortMode originalMode = filter.SortMode;
        using var select = NSTimer.CreateTimer(.2, _ =>
        {
            foreach (ushort keyCode in new ushort[] { 125, 125, 36 })
            {
                string text = keyCode == 36 ? "\r" : "\uf701";
                var key = NSEvent.KeyEvent(NSEventType.KeyDown, CGPoint.Empty, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                    window.Window.WindowNumber, null!, text, text, false, keyCode) ?? throw new InvalidOperationException("菜单键盘事件创建失败");
                NSApplication.SharedApplication.PostEvent(key, false);
            }
        });
        using var timeout = NSTimer.CreateTimer(3, _ => sort.Menu!.CancelTracking());
        NSRunLoop.Main.AddTimer(select, NSRunLoopMode.Common);
        NSRunLoop.Main.AddTimer(timeout, NSRunLoopMode.Common);
        sort.PerformClick(sort);
        timeout.Invalidate();
        if (sort.IndexOfSelectedItem != 1 || filter.SortMode != (SortMode)filter.SortModes[1].Value)
            throw new InvalidOperationException("对齐后的原生菜单选择未更新筛选业务属性");
        filter.SortMode = originalMode;
        observations.Add("native menu keyboard selection updates shared sort mode");
    }
}
