using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Mac.Native;

internal static class NativeToolsAcceptance
{
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var checks = new List<string>();
        var main = window.Main;
        var tools = main.Tools;
        try
        {
            if (MacToolsWindowRegistry.Current != null) throw new InvalidOperationException("工具窗口未懒创建");
            NativeWindowPresenter.ShowOrActivateToolsWindow(tools);
            await WaitAsync(() => MacToolsWindowRegistry.Current is { IsOpen: true }, "工具窗口显示");
            var host = MacToolsWindowRegistry.Current!;
            var content = host.Window.ContentView!;
            if (host.Window.Appearance?.Name != NSAppearance.NameDarkAqua || host.Window.Title != "工具")
                throw new InvalidOperationException($"工具窗口标题或深色外观异常: title={host.Window.Title} appearance={host.Window.Appearance?.Name}");
            if (Math.Abs((double)content.Bounds.Width - 880) > 2 || Math.Abs((double)content.Bounds.Height - 720) > 2)
                throw new InvalidOperationException($"工具窗口内容尺寸异常：{content.Bounds}");
            var controller = host.Window.ContentViewController;
            string controllerType = controller?.GetType().Name ?? string.Empty;
            string controllerClassName = controller?.Class?.Name ?? string.Empty;
            if (controller == null || !(controllerType.Contains("NSHostingController") || controllerClassName.Contains("NSHostingController")))
                throw new InvalidOperationException("工具窗口未使用 SwiftUI 宿主外壳");
            await WaitAsync(() => host.SelectedCategory == 0, "SwiftUI 侧栏默认选中 EXIF");
            if (Descendants(content).OfType<NSButton>().Any(button => button.Title is "返回" or "照片数据统计"))
                throw new InvalidOperationException("Mac 工具窗口仍有首页返回或 Windows 专属入口");
            await WaitAsync(() => tools.CurrentTool is ExifDetailViewModel, "默认 EXIF 落点");
            checks.Add("window: lazy singleton, dark 880×720, unified toolbar, SwiftUI NSHostingController sidebar with two categories; default EXIF");

            await WaitAsync(() => tools.ExifDetail?.Groups is { Count: > 0 }, "EXIF 分组加载");
            int groups = tools.ExifDetail!.Groups!.Count;
            int tags = tools.ExifDetail.Groups.Sum(group => group.Tags.Count);
            var page = Descendants(content).OfType<MacExifDetailView>().Single();
            var exifTable = Descendants(page).OfType<NSTableView>().SingleOrDefault()
                ?? throw new InvalidOperationException("EXIF 页未使用表格承载");
            if (exifTable.HeaderView != null || exifTable.ColumnCount != 1 || exifTable.SelectionHighlightStyle != NSTableViewSelectionHighlightStyle.None)
                throw new InvalidOperationException("EXIF 表格不是无表头单列无选中高亮");
            int expectedRows = 1 + (tools.ExifDetail.IsLoading ? 1 : 0) + groups + tags;
            await WaitAsync(() => exifTable.RowCount == expectedRows, "EXIF 表格行数");
            checks.Add($"exif: {groups} groups / {tags} tags, single virtualized NSTableView ({expectedRows} rows)");
            {
                checks.Add(MeasureExif(page, host.Window, tools.ExifDetail, "jpg"));
                string arwPath = System.IO.Path.GetFullPath("../TestCase/A7C01489.ARW");
                if (File.Exists(arwPath))
                {
                    var arwFile = new ImageFile(new MacFileRef(arwPath));
                    var arw = new ExifDetailViewModel(arwFile);
                    await WaitAsync(() => arw.Groups is { Count: > 0 } && !arw.IsLoading, "大 EXIF 加载");
                    checks.Add(MeasureExif(page, host.Window, arw, "arw"));
                    page.Show(tools.ExifDetail);
                }
                else checks.Add("exif timing (arw): fixture missing " + arwPath);
            }

            {
                // SwiftUI 侧栏收尾可能再改一次详情栏宽度，宽度变化会按设计清掉现场选择框；先等宽度稳定再点击。
                await WaitStableAsync(() => (double)exifTable.Frame.Width, 0.5, 300, "EXIF 表格宽度稳定");
                var scroll = Descendants(page).OfType<NSScrollView>().First();
                scroll.ContentView.ScrollToPoint(CGPoint.Empty);
                scroll.ReflectScrolledClipView(scroll.ContentView);
                content.LayoutSubtreeIfNeeded();
                host.Window.DisplayIfNeeded();
                var tagCells = Descendants(exifTable).OfType<MacExifDetailView.ExifTagCellView>().ToArray();
                if (tagCells.Length == 0) throw new InvalidOperationException("EXIF 标签行未实例化");
                if (tagCells.Any(cell => Descendants(cell).OfType<NSTextField>().Any()))
                    throw new InvalidOperationException("EXIF 标签行仍有文本控件");
                var cell = tagCells.FirstOrDefault(candidate => candidate.Tag != null && candidate.TextRect(MacExifDetailView.ExifTextColumn.Value).Width > 20)
                    ?? throw new InvalidOperationException("未找到可用于点击测试的 EXIF 值列");
                string expected = cell.Tag!.Value;
                string? accessible = cell.AccessibilityValue?.ToString();
                if (string.IsNullOrEmpty(accessible) || !accessible.Contains(cell.Tag.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("EXIF 标签行缺少无障碍文本值");
                var rect = cell.TextRect(MacExifDetailView.ExifTextColumn.Value);
                var point = cell.ConvertPointToView(new CGPoint((double)rect.GetMidX(), (double)rect.GetMidY()), null);
                using var down = NSEvent.MouseEvent(NSEventType.LeftMouseDown, point, 0, NSProcessInfo.ProcessInfo.SystemUptime, host.Window.WindowNumber, null!, 1, 1, 1)!;
                using var up = NSEvent.MouseEvent(NSEventType.LeftMouseUp, point, 0, NSProcessInfo.ProcessInfo.SystemUptime, host.Window.WindowNumber, null!, 1, 1, 1)!;
                NSApplication.SharedApplication.PostEvent(up, false);
                exifTable.MouseDown(down);
                await Task.Delay(200);
                var overlay = page.SelectionField ?? throw new InvalidOperationException("点击 EXIF 文本未生成可选覆盖框");
                if (overlay.StringValue != expected) throw new InvalidOperationException("覆盖框文本与标签值不一致");
                if (host.Window.FirstResponder != overlay.CurrentEditor || overlay.CurrentEditor == null)
                    throw new InvalidOperationException("覆盖框未成为当前编辑框");
                await SaveWindowAsync(host.Window, Path.Combine(output, "native-tools-exif-selection.png"));
                var pasteboard = NSPasteboard.GeneralPasteboard;
                var snapshot = new List<NSPasteboardItem>();
                foreach (var item in pasteboard.PasteboardItems ?? [])
                {
                    var copy = new NSPasteboardItem();
                    foreach (string type in item.Types)
                    {
                        var data = item.GetDataForType(type);
                        if (data != null) copy.SetDataForType(data, type);
                    }
                    snapshot.Add(copy);
                }
                overlay.SelectText(overlay);
                var editor = overlay.CurrentEditor as NSTextView;
                NSApplication.SharedApplication.SendAction(new ObjCRuntime.Selector("copy:"), editor, editor);
                await Task.Delay(150);
                string? copied = pasteboard.GetStringForType(NSPasteboard.NSPasteboardTypeString);
                pasteboard.ClearContents();
                if (snapshot.Count > 0) pasteboard.WriteObjects(snapshot.ToArray());
                if (copied != expected) throw new InvalidOperationException($"EXIF 文本复制内容不符：{copied}");
                scroll.ContentView.ScrollToPoint(new CGPoint(0, Math.Max(0, (double)scroll.DocumentView!.Frame.Height)));
                scroll.ReflectScrolledClipView(scroll.ContentView);
                await Task.Delay(100);
                if (page.SelectionField != null) throw new InvalidOperationException("滚动出视口后覆盖框未移除");
                checks.Add($"selection: tag cells draw text without controls and expose AX value; click on value rect covers one shared selectable field ({expected.Length} chars), copy round-trips via pasteboard, overlay cleared on scroll");
            }

            ClickCategory(host, 1);
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel && host.SelectedCategory == 1, "侧栏选择进入 DINO");
            await WaitAsync(() => tools.DinoPresenter.Status == main.CurrentFile!.Name && tools.DinoPresenter.ThumbnailTile.Source != null, "DINO 就绪", 180);
            var dino = Descendants(content).OfType<MacDinoDiagnosticView>().Single();
            var tiles = Descendants(dino).OfType<MacDiagnosticTile>().ToArray();
            if (tiles.Length != 5) throw new InvalidOperationException("DINO 瓦片数不是 5");
            double bottom = tiles.Max(tile => (double)tile.Frame.Bottom);
            if (dino.RigidText.Frame.Y < bottom || !dino.RigidText.Selectable || Math.Abs((double)dino.RigidText.Frame.Width - ((double)dino.RigidText.Superview!.Bounds.Width - 32)) > 1)
                throw new InvalidOperationException("DINO 刚体文本非网格下方整宽可复制原生文本");
            var firstRow = tiles.Take(3).ToArray();
            var secondRow = tiles.Skip(3).ToArray();
            if (firstRow.Any(tile => Math.Abs((double)(tile.Frame.Y - firstRow[0].Frame.Y)) > .5)
                || secondRow.Any(tile => Math.Abs((double)(tile.Frame.Y - secondRow[0].Frame.Y)) > .5))
                throw new InvalidOperationException("DINO 未保持 3+2 上沿对齐");
            await SaveWindowAsync(host.Window, Path.Combine(output, "native-tools-dino.png"));
            checks.Add("dino: SwiftUI sidebar selection binding, shared presenter ready, 3+2 tiles and full-width selectable rigid text");
            var sameWindow = host.Window;
            window.ShowTools();
            await Task.Delay(150);
            if (host.Window != sameWindow || tools.CurrentTool is not DinoDebugViewModel) throw new InvalidOperationException("重复打开未保留工具页");
            ClickCategory(host, 0);
            await WaitAsync(() => tools.CurrentTool is ExifDetailViewModel && host.SelectedCategory == 0, "常驻侧栏切回 EXIF");
            {
                var scroll = Descendants(page).OfType<NSScrollView>().First();
                scroll.ContentView.ScrollToPoint(CGPoint.Empty);
                scroll.ReflectScrolledClipView(scroll.ContentView);
            }
            await SaveWindowAsync(host.Window, Path.Combine(output, "native-tools-exif.png"));
            ClickCategory(host, 1);
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel && host.SelectedCategory == 1, "再次进入 DINO");
            host.CloseWindow();
            window.ShowTools();
            await WaitAsync(() => host.IsOpen && tools.CurrentTool is ExifDetailViewModel, "关闭后主入口重开默认 EXIF");
            if (host.Window != sameWindow) throw new InvalidOperationException("工具窗口重开非同一实例");
            checks.Add("lifecycle: switching never removes categories, open window preserves current page, closed window reopens same instance at default EXIF");

            var file = main.CurrentFile;
            main.CurrentFile = null;
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel && host.SelectedCategory == 1, "无照片自动落 DINO");
            host.SelectCategoryForTesting(0);
            await Task.Delay(200);
            if (host.SelectedCategory != 1 || tools.CurrentTool is not DinoDebugViewModel)
                throw new InvalidOperationException("无 EXIF 仍可选择 EXIF 页");
            main.CurrentFile = file;
            checks.Add("availability: absent photo disables EXIF and falls back to DINO without a tools home page");
            host.CloseWindow();
            var exifGroup = window.ControlBar.Groups.First();
            using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, new CGPoint(5, 5), 0, NSProcessInfo.ProcessInfo.SystemUptime, window.Window.WindowNumber, null!, 1, 1, 1)!;
            exifGroup.MouseDown(click);
            await WaitAsync(() => host.IsOpen, "EXIF 控制栏入口");
            checks.Add("entry: control bar EXIF group opens the same tools window");
        }
        finally
        {
            MacToolsWindowRegistry.Current?.CloseWindow();
            await File.WriteAllLinesAsync(Path.Combine(output, "native-tools-checks.txt"), checks);
        }
        Console.WriteLine("[NativeSmoke] PASS tools");
    }

    /// <summary>测量 EXIF 页在一次展示与逐段滚动中的耗时，输出行数、标签数与已实例化文本控件数。</summary>
    private static string MeasureExif(MacExifDetailView page, NSWindow window, ExifDetailViewModel viewModel, string label)
    {
        var content = window.ContentView!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        page.Show(viewModel);
        content.LayoutSubtreeIfNeeded();
        window.DisplayIfNeeded();
        CoreAnimation.CATransaction.Flush();
        double showMs = sw.Elapsed.TotalMilliseconds;
        var scroll = Descendants(page).OfType<NSScrollView>().First();
        double docH = (double)scroll.DocumentView!.Frame.Height, viewH = (double)scroll.ContentView.Bounds.Height;
        var steps = new List<double>();
        for (double y = 0; y <= docH - viewH; y += viewH / 4)
        {
            sw.Restart();
            scroll.ContentView.ScrollToPoint(new CGPoint(0, y));
            scroll.ReflectScrolledClipView(scroll.ContentView);
            content.LayoutSubtreeIfNeeded();
            window.DisplayIfNeeded();
            CoreAnimation.CATransaction.Flush();
            steps.Add(sw.Elapsed.TotalMilliseconds);
        }
        steps.Sort();
        int fields = Descendants(page).OfType<NSTextField>().Count();
        int tags = viewModel.Groups?.Sum(group => group.Tags.Count) ?? 0;
        string line = $"exif timing ({label}): show+layout+display {showMs:F1} ms; scroll {steps.Count} steps p50 {steps[steps.Count / 2]:F2} ms p95 {steps[(int)(steps.Count * .95)]:F2} ms max {steps[^1]:F2} ms; tags {tags}; text fields {fields}; doc {docH:F0} pt";
        Console.WriteLine("[ToolsTiming] " + line);
        return line;
    }

    private static async Task SaveWindowAsync(NSWindow window, string path)
    {
        await Task.Delay(250);
        window.ContentView!.LayoutSubtreeIfNeeded();
        window.DisplayIfNeeded();
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/screencapture")
        {
            UseShellExecute = false,
            ArgumentList = { "-o", "-x", "-l", window.WindowNumber.ToString(), path },
        });
        if (process == null) throw new InvalidOperationException("系统工具窗口截图未启动");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(path)) throw new InvalidOperationException("系统工具窗口截图不可用");
    }

    /// <summary>经 SwiftUI 侧栏选择绑定切换分类（与用户点击同一条绑定路径），并等待 C# 接收。</summary>
    private static void ClickCategory(MacToolsWindow host, int row)
    {
        int before = host.SelectedCategory;
        host.SelectCategoryForTesting(row);
        if (row == before) return;
        for (int i = 0; i < 40 && host.SelectedCategory != row; i++) Thread.Sleep(10);
    }

    private static IEnumerable<NSView> Descendants(NSView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    /// <summary>等待某个尺寸连续 stableMilliseconds 毫秒不再变化。</summary>
    private static async Task WaitStableAsync(Func<double> value, double tolerance, int stableMilliseconds, string name)
    {
        var start = DateTime.UtcNow;
        double last = value();
        var changedAt = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalSeconds < 10)
        {
            await Task.Delay(25);
            double current = value();
            if (Math.Abs(current - last) > tolerance) { last = current; changedAt = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - changedAt).TotalMilliseconds >= stableMilliseconds) return;
        }
        throw new InvalidOperationException("等待稳定超时：" + name);
    }

    private static async Task WaitAsync(Func<bool> condition, string name, int timeoutSeconds = 60)
    {
        for (int i = 0; i < timeoutSeconds * 40; i++) { if (condition()) return; await Task.Delay(25); }
        throw new InvalidOperationException("等待超时：" + name);
    }
}
