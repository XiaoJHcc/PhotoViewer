using AppKit;
using Foundation;
using CoreGraphics;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>真实胶片条布局/顺序/滚动回归，只读照片并使用冒烟模式的隔离设置。</summary>
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
                for (int index = 0; index < files.Count; index++)
                {
                    window.Main.CurrentFile = files[index];
                    await Task.Delay(100);
                    window.Filmstrip.LayoutSubtreeIfNeeded();
                    using var path = NSIndexPath.FromItemSection(index, 0);
                    var attributes = window.Filmstrip.CollectionViewLayout!.GetLayoutAttributesForItem(path) ?? throw new InvalidOperationException("卡片几何缺失");
                    var frame = attributes.Frame;
                    var clip = window.FilmstripScroll.ContentView.Bounds;
                    if (frame.X < clip.X - 1 || frame.Y < clip.Y - 1 || frame.X + frame.Width > clip.X + clip.Width + 1 || frame.Y + frame.Height > clip.Y + clip.Height + 1)
                        throw new InvalidOperationException($"{mode}/{order} 第 {index} 项未完整滚入：frame={frame}, clip={clip}, document={window.Filmstrip.Frame}");
                    if (window.Filmstrip.GetItem(path) is not MacThumbnailItem card || !ReferenceEquals(card.BoundFile, files[index]))
                        throw new InvalidOperationException($"{mode}/{order} 第 {index} 项视图与业务身份不同");
                    if (index > 0)
                    {
                        using var previousPath = NSIndexPath.FromItemSection(index - 1, 0);
                        var previous = window.Filmstrip.CollectionViewLayout.GetLayoutAttributesForItem(previousPath)!.Frame;
                        bool horizontal = mode == LayoutMode.Row;
                        if (horizontal ? Math.Abs((double)(frame.Y - previous.Y)) > .1 || frame.X <= previous.X
                            : Math.Abs((double)(frame.X - previous.X)) > .1 || frame.Y <= previous.Y)
                            throw new InvalidOperationException("胶片条出现换行、换列或几何顺序颠倒");
                    }
                    observations.Add($"{mode}/{order}/{index}\t{files[index].Name}\t{frame}\tclip={clip}\tdocument={window.Filmstrip.Frame}");
                }
                var savedCurrent = window.Main.CurrentFile;
                double extent = mode == LayoutMode.Row ? (double)(window.Filmstrip.Frame.Width - window.FilmstripScroll.ContentView.Bounds.Width)
                    : (double)(window.Filmstrip.Frame.Height - window.FilmstripScroll.ContentView.Bounds.Height);
                if (extent <= 0)
                {
                    if (mode == LayoutMode.Col || files.Count > 8) throw new InvalidOperationException("溢出列表缺少滚动范围");
                    continue;
                }
                var middle = mode == LayoutMode.Row ? new CGPoint(extent / 2, 0) : new CGPoint(0, extent / 2);
                window.FilmstripScroll.ContentView.ScrollToPoint(middle);
                window.FilmstripScroll.ReflectScrolledClipView(window.FilmstripScroll.ContentView);
                await Task.Delay(250);
                if (window.Main.CurrentFile != savedCurrent || Math.Abs((double)(window.FilmstripScroll.ContentView.Bounds.X - middle.X)) > 1 || Math.Abs((double)(window.FilmstripScroll.ContentView.Bounds.Y - middle.Y)) > 1)
                    throw new InvalidOperationException("手动滚动被布局重置或误改当前照片");
                if (mode == LayoutMode.Row)
                {
                    if (!window.FilmstripScroll.HasHorizontalScroller || window.FilmstripScroll.HasVerticalScroller) throw new InvalidOperationException("横向胶片条系统滚动轴配置错误");
                    using var wheel = new CGEvent(null, CGScrollEventUnit.Line, [-1]);
                    using var nativeWheel = NSEvent.Create(wheel) ?? throw new InvalidOperationException("原生滚轮事件创建失败");
                    window.FilmstripScroll.ScrollWheel(nativeWheel);
                    if (window.FilmstripScroll.ContentView.Bounds.X <= middle.X || window.Main.CurrentFile != savedCurrent)
                        throw new InvalidOperationException("横向胶片条纵向鼠标滚轮未滚动或误切照片");
                }
            }
            window.Window.SetContentSize(new CGSize(900, 650));
            await Task.Delay(200);
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
}
