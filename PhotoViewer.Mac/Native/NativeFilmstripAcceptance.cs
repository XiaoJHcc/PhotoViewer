using AppKit;
using Foundation;
using CoreGraphics;
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
