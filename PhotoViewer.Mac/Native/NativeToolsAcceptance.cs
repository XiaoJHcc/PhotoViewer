using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生工具窗口回归，仅在隔离冒烟会话中以 <c>--pv-native-smoke --pv-native-tools &lt;绝对图片路径&gt;</c> 运行。
/// 覆盖：门面入口创建单例窗口（未创建时不占资源）、深色外观与 880×720 内容尺寸、列表页按钮（EXIF 详情 / DINO 诊断，
/// 照片数据统计在 macOS 不显示）、真实按钮点击进入 EXIF 详情页（分组标签渲染出来）、进入 DINO 诊断页
/// （5 张诊断瓦片 + 刚体文本就绪）、返回列表页、关闭后可重开。
/// 窗口按标题从系统窗口列表取得，断言走真实视图树，不使用内部桩。
/// </summary>
internal static class NativeToolsAcceptance
{
    /// <summary>执行工具窗口回归并写出检查清单。</summary>
    /// <param name="window">真实原生主窗口。</param>
    /// <param name="output">报告输出目录。</param>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var checks = new List<string>();
        var main = window.Main;
        var tools = main.Tools;
        try
        {
            var existing = FindToolsWindow();
            if (existing != null) throw new InvalidOperationException("工具窗口在入口调用前已存在");
            checks.Add("entry: tools window is created lazily and absent before the presenter call");

            // 1) 门面入口创建并前置窗口。
            NativeWindowPresenter.ShowOrActivateToolsWindow(tools);
            await WaitAsync(() => FindToolsWindow() is { Window.IsVisible: true }, "工具窗口显示");
            var toolsWindow = FindToolsWindow()!;
            if (toolsWindow.Window.Appearance?.Name != NSAppearance.NameDarkAqua)
                throw new InvalidOperationException($"工具窗口外观不是深色：{toolsWindow.Window.Appearance?.Name}");
            var content = toolsWindow.Window.ContentView ?? throw new InvalidOperationException("工具窗口缺少内容视图");
            if (Math.Abs((double)content.Bounds.Width - 880) > 2 || Math.Abs((double)content.Bounds.Height - 720) > 2)
                throw new InvalidOperationException($"工具窗口内容尺寸不是 880×720：{content.Bounds}");
            if (!toolsWindow.Window.Title.Contains("工具", StringComparison.Ordinal))
                throw new InvalidOperationException($"工具窗口标题不符：{toolsWindow.Window.Title}");
            checks.Add($"window: dark appearance, content {content.Bounds.Width:0}×{content.Bounds.Height:0}, title 工具");

            // 2) 列表页只有两个可用入口（照片数据统计为 Windows 专属）。
            var titles = Buttons(content).Select(button => button.Title).ToArray();
            if (!titles.Contains("EXIF 详情", StringComparer.Ordinal) || !titles.Contains("DINO 诊断", StringComparer.Ordinal))
                throw new InvalidOperationException($"工具列表页缺少入口按钮：{string.Join(" | ", titles)}");
            if (titles.Contains("照片数据统计", StringComparer.Ordinal))
                throw new InvalidOperationException("macOS 不应显示 Windows 专属的照片数据统计入口");
            checks.Add($"list: entries = {string.Join(" / ", titles)}");

            // 3) EXIF 详情页：真实按钮点击后分组标签必须渲染出来。
            Click(FindButton(content, "EXIF 详情"));
            await WaitAsync(() => tools.CanGoBack && tools.ExifDetail?.Groups is { Count: > 0 }
                && tools.ExifDetail.Groups.All(group => group.Tags.Count > 0), "EXIF 分组加载");
            int groups = tools.ExifDetail!.Groups!.Count;
            int tags = tools.ExifDetail.Groups.Sum(group => group.Tags.Count);
            await WaitAsync(() => Labels(content).Count() > tags / 2, "EXIF 行渲染");
            await WaitAsync(() => Buttons(content).Any(button => button.Title == "返回"), "返回按钮出现");
            var back = FindButton(content, "返回");
            checks.Add($"exif: {groups} groups / {tags} tags rendered with back button");

            // 4) DINO 诊断页：5 张原生诊断瓦片 + 刚体文本，数据由共享 presenter 提供。
            Click(back);
            await WaitAsync(() => !tools.CanGoBack, "返回列表页");
            Click(FindButton(content, "DINO 诊断"));
            await WaitAsync(() => tools.DinoPresenter.Status == main.CurrentFile!.Name, "DINO 状态就绪");
            await WaitAsync(() => tools.DinoPresenter.ThumbnailTile.Source != null, "DINO 缩略图就绪");
            int tiles = Descendants(content).OfType<MacDiagnosticTile>().Count();
            if (tiles != 5) throw new InvalidOperationException($"DINO 诊断页瓦片数不是 5：{tiles}");
            var dinoView = Descendants(content).OfType<MacDinoDiagnosticView>().Single();
            var tileViews = Descendants(content).OfType<MacDiagnosticTile>().ToArray();
            var rigid = dinoView.RigidText;
            double tileBottom = tileViews.Max(tile => (double)tile.Frame.Y + (double)tile.Frame.Height);
            if ((double)rigid.Frame.Y < tileBottom)
                throw new InvalidOperationException($"刚体拟合文本仍占瓦片格：text={rigid.Frame}, tileBottom={tileBottom}");
            if (Math.Abs((double)rigid.Frame.X - 16) > .5 || rigid.Superview == null
                || Math.Abs((double)(rigid.Frame.Width - (rigid.Superview.Bounds.Width - 32))) > 1)
                throw new InvalidOperationException($"刚体拟合文本不是网格下方整宽块：text={rigid.Frame}, document={rigid.Superview?.Bounds}");
            checks.Add($"dino: 5 diagnostic tiles, status = {tools.DinoPresenter.Status}, rigid text is a full-width block below the grid");

            // 5) 返回列表页并关闭；重开后仍可用。
            Click(FindButton(content, "返回"));
            await WaitAsync(() => !tools.CanGoBack, "再次返回列表页");
            toolsWindow.CloseWindow();
            await WaitAsync(() => FindToolsWindow() is not { Window.IsVisible: true }, "工具窗口关闭");
            NativeWindowPresenter.ShowOrActivateToolsWindow(tools);
            await WaitAsync(() => FindToolsWindow() is { Window.IsVisible: true }, "工具窗口重开");
            checks.Add("lifecycle: close releases the window and the presenter reopens it");

            // 5b) TOOLS-01：停留在子页时关闭，再从主窗口入口打开应回到列表首页。
            Click(FindButton(content, "DINO 诊断"));
            await WaitAsync(() => tools.CanGoBack, "重开后进入 DINO 页");
            toolsWindow.CloseWindow();
            await WaitAsync(() => FindToolsWindow() is not { Window.IsVisible: true }, "子页状态下关闭");
            window.ShowTools();
            await WaitAsync(() => FindToolsWindow() is { Window.IsVisible: true }, "主窗口入口重开工具窗口");
            if (tools.CanGoBack) throw new InvalidOperationException("从主窗口重新打开工具窗口未回到列表首页");
            checks.Add("entry: reopening from the main window resets a closed window to the tools list");

            // 6) 控制栏 EXIF 信息块点击是另一条入口（与右键菜单「工具」共用同一门面调用）。
            toolsWindow.CloseWindow();
            await WaitAsync(() => FindToolsWindow() is not { Window.IsVisible: true }, "关闭后再点入口");
            var exifGroup = window.ControlBar.Groups.FirstOrDefault()
                ?? throw new InvalidOperationException("控制栏没有 EXIF 分组");
            using (var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, new CGPoint(5, 5), 0,
                NSProcessInfo.ProcessInfo.SystemUptime, window.Window.WindowNumber, null!, 1, 1, 1))
            {
                if (click == null) throw new InvalidOperationException("左键事件创建失败");
                exifGroup.MouseDown(click);
            }
            await WaitAsync(() => FindToolsWindow() is { Window.IsVisible: true }, "控制栏 EXIF 块打开工具窗口");
            checks.Add("entry: control bar EXIF block opens the tools window");
        }
        finally
        {
            FindToolsWindow()?.CloseWindow();
            await Task.Delay(120);
            await File.WriteAllLinesAsync(Path.Combine(output, "native-tools-checks.txt"), checks);
        }
        Console.WriteLine("[NativeSmoke] PASS tools");
    }

    /// <summary>按登记处取得原生工具窗口。</summary>
    private static MacToolsWindow? FindToolsWindow() => MacToolsWindowRegistry.Current;

    /// <summary>深度遍历视图树。</summary>
    private static IEnumerable<NSView> Descendants(NSView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>视图树里的全部按钮。</summary>
    private static IEnumerable<NSButton> Buttons(NSView root) => Descendants(root).OfType<NSButton>();

    /// <summary>视图树里的全部标签，含页面标题与分组标题。</summary>
    private static IEnumerable<string> Labels(NSView root)
        => Descendants(root).OfType<NSTextField>().Select(field => field.StringValue).Where(value => !string.IsNullOrEmpty(value));

    /// <summary>按标题取按钮，找不到时连同现有按钮与视图结构一起报错。</summary>
    private static NSButton FindButton(NSView root, string title)
        => Buttons(root).FirstOrDefault(button => button.Title == title)
           ?? throw new InvalidOperationException($"找不到按钮：{title}（现有按钮：{string.Join(" | ", Buttons(root).Select(button => button.Title))}；直接子视图：{string.Join(" | ", root.Subviews.Select(view => $"{view.GetType().Name}@{view.Frame} hidden={view.Hidden}"))}）");

    /// <summary>真实点击按钮（与鼠标路径同一入口）。</summary>
    private static void Click(NSButton button)
    {
        if (!button.Enabled) throw new InvalidOperationException($"按钮不可用：{button.Title}");
        button.PerformClick(button);
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }
}
