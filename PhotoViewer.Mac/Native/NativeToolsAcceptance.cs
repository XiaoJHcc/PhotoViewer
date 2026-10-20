using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;
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
                throw new InvalidOperationException("工具窗口标题或深色外观异常");
            if (Math.Abs((double)content.Bounds.Width - 880) > 2 || Math.Abs((double)content.Bounds.Height - 720) > 2)
                throw new InvalidOperationException($"工具窗口内容尺寸异常：{content.Bounds}");
            if (host.Window.ContentViewController is not NSSplitViewController || host.Sidebar.RowCount != 2)
                throw new InvalidOperationException("工具窗口缺少原生常驻双分类侧栏");
            if (Descendants(content).OfType<NSButton>().Any(button => button.Title is "返回" or "照片数据统计"))
                throw new InvalidOperationException("Mac 工具窗口仍有首页返回或 Windows 专属入口");
            await WaitAsync(() => tools.CurrentTool is ExifDetailViewModel, "默认 EXIF 落点");
            checks.Add("window: lazy singleton, dark 880×720, unified toolbar and native persistent two-category sidebar; default EXIF");

            await WaitAsync(() => tools.ExifDetail?.Groups is { Count: > 0 }, "EXIF 分组加载");
            int groups = tools.ExifDetail!.Groups!.Count;
            int tags = tools.ExifDetail.Groups.Sum(group => group.Tags.Count);
            await WaitAsync(() => Descendants(content).OfType<NSTextField>().Count(field => field.Selectable) > tags, "EXIF 可复制文本");
            if (!Descendants(content).OfType<NSBox>().Any()) throw new InvalidOperationException("EXIF 容器未原生化");
            checks.Add($"exif: {groups} groups / {tags} tags, native NSBox containers, selectable metadata text");

            ClickCategory(host, 1);
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel, "侧栏真实点击进入 DINO");
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
            checks.Add("dino: native sidebar click, shared presenter ready, 3+2 tiles and full-width selectable rigid text");
            var sameWindow = host.Window;
            window.ShowTools();
            await Task.Delay(150);
            if (host.Window != sameWindow || tools.CurrentTool is not DinoDebugViewModel) throw new InvalidOperationException("重复打开未保留工具页");
            ClickCategory(host, 0);
            await WaitAsync(() => tools.CurrentTool is ExifDetailViewModel && !host.Sidebar.Hidden, "常驻侧栏切回 EXIF");
            await SaveWindowAsync(host.Window, Path.Combine(output, "native-tools-exif.png"));
            ClickCategory(host, 1);
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel, "再次进入 DINO");
            host.CloseWindow();
            window.ShowTools();
            await WaitAsync(() => host.IsOpen && tools.CurrentTool is ExifDetailViewModel, "关闭后主入口重开默认 EXIF");
            if (host.Window != sameWindow) throw new InvalidOperationException("工具窗口重开非同一实例");
            checks.Add("lifecycle: switching never removes categories, open window preserves current page, closed window reopens same instance at default EXIF");

            var file = main.CurrentFile;
            main.CurrentFile = null;
            await WaitAsync(() => tools.CurrentTool is DinoDebugViewModel && host.Sidebar.SelectedRow == 1, "无照片自动落 DINO");
            if (host.Sidebar.Delegate!.ShouldSelectRow(host.Sidebar, 0)) throw new InvalidOperationException("无 EXIF 仍可选择 EXIF 页");
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

    private static void ClickCategory(MacToolsWindow host, int row)
    {
        var table = host.Sidebar;
        table.LayoutSubtreeIfNeeded();
        var label = Descendants(table).OfType<NSTextField>().First(field => field.StringValue == (row == 0 ? "EXIF 详情" : "DINO 诊断"));
        var point = label.ConvertPointToView(new CGPoint(label.Bounds.Width / 2, label.Bounds.Height / 2), null);
        using var up = NSEvent.MouseEvent(NSEventType.LeftMouseUp, point, 0, NSProcessInfo.ProcessInfo.SystemUptime, host.Window.WindowNumber, null!, 1, 1, 0)!;
        NSApplication.SharedApplication.PostEvent(up, false);
        using var down = NSEvent.MouseEvent(NSEventType.LeftMouseDown, point, 0, NSProcessInfo.ProcessInfo.SystemUptime, host.Window.WindowNumber, null!, 1, 1, 1)!;
        table.MouseDown(down);
    }

    private static IEnumerable<NSView> Descendants(NSView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task WaitAsync(Func<bool> condition, string name, int timeoutSeconds = 60)
    {
        for (int i = 0; i < timeoutSeconds * 40; i++) { if (condition()) return; await Task.Delay(25); }
        throw new InvalidOperationException("等待超时：" + name);
    }
}
