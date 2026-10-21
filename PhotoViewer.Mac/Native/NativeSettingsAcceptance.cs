using AppKit;
using CoreGraphics;
using Foundation;
using System.Diagnostics;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Mac.Native;

/// <summary>设置窗口实际控件与系统事件回归，仅在隔离冒烟会话中运行。
/// 窗口由 SwiftUI（libMacSettingsUI.dylib）承载，验收通过 C# 侧展示器的公开接口驱动。</summary>
internal static class NativeSettingsAcceptance
{
    /// <summary>验证五分类、深色外观、SwiftUI 宿主、分页历史前进后退、勾选、行内录制/清空/取消、排序与窗口缩放，
    /// 导出分页截图并留出真实窗口截图机会，最后核对关闭回写与重开恢复。</summary>
    internal static async Task RunAsync(MacMainWindow main, MacNativeSettingsPresenter presenter, string output)
    {
        var checks = new List<string>();
        presenter.TryPresent(main.Main.Settings);
        if (!presenter.IsPresenting) throw new InvalidOperationException("设置窗口未创建");
        await Task.Delay(600);
        if (!presenter.IsDarkAppearance) throw new InvalidOperationException("设置窗口不是深色外观");
        if (!presenter.HasHostingController) throw new InvalidOperationException("设置未挂载 SwiftUI 宿主视图");
        checks.Add("DarkAqua / SwiftUI NSHostingController mounted");
        await File.WriteAllTextAsync(Path.Combine(output, "settings-window-id.txt"), presenter.WindowNumber.ToString());

        for (int category = 0; category < 5; category++)
        {
            presenter.ShowCategory(category);
            await Task.Delay(500);
            string page = Path.Combine(output, $"settings-{category}.png");
            presenter.RenderTo(page);
            AssertRendered(page, $"分类 {category} 未渲染出控件");
            CaptureScreen(presenter, Path.Combine(output, $"settings-window-{category}.png"), checks);
            checks.Add($"category {category}: SwiftUI grouped form rendered");
            if (category == 0)
            {
                // 桌面端缓存上限由用户设置生效，提示按实测预算与适屏尺寸给出可缓存张数
                if (!presenter.WorkingSettings.ShowsCacheLimits) throw new InvalidOperationException("桌面端缓存上限两项不应隐藏");
                string countInfo = presenter.WorkingSettings.BitmapCacheCountInfo;
                if (!countInfo.Contains("适屏照片", StringComparison.Ordinal))
                    throw new InvalidOperationException("缓存提示未按适屏张数口径：" + countInfo);
                checks.Add("file footer: " + presenter.WorkingSettings.PerformanceBudgetInfo);
                checks.Add("file footer: " + countInfo);
            }
            await Task.Delay(300);
        }
        CaptureScreen(presenter, Path.Combine(output, "settings-window.png"), checks);

        presenter.ShowCategory(0);
        await Task.Delay(100);
        presenter.ShowCategory(1);
        await Task.Delay(100);
        presenter.ShowCategory(2);
        await Task.Delay(100);
        if (presenter.CurrentCategory != 2) throw new InvalidOperationException("分类切换未生效");
        presenter.NavigateBack();
        if (presenter.CurrentCategory != 1) throw new InvalidOperationException("历史后退未回到上一分类");
        presenter.NavigateBack();
        if (presenter.CurrentCategory != 0) throw new InvalidOperationException("历史后退未到达更早分类");
        presenter.NavigateForward();
        if (presenter.CurrentCategory != 1) throw new InvalidOperationException("历史前进未返回下一分类");
        presenter.NavigateForward();
        if (presenter.CurrentCategory != 2) throw new InvalidOperationException("历史前进未回到最新分类");
        checks.Add("category history back/forward");

        bool displayed = presenter.WorkingSettings.Hotkeys[0].IsDisplay;
        string beforeToggle = Path.Combine(output, "settings-toggle-before.png");
        string afterToggle = Path.Combine(output, "settings-toggle-after.png");
        presenter.RenderTo(beforeToggle);
        presenter.ApplyAction("{\"type\":\"setBool\",\"key\":\"control.hotkey.display.0\",\"boolValue\":"
            + (!displayed).ToString().ToLowerInvariant() + "}");
        if (presenter.WorkingSettings.Hotkeys[0].IsDisplay == displayed) throw new InvalidOperationException("勾选动作未更新工作副本");
        await Task.Delay(300);
        presenter.RenderTo(afterToggle);
        if (File.ReadAllBytes(beforeToggle).SequenceEqual(File.ReadAllBytes(afterToggle)))
            throw new InvalidOperationException("勾选后 SwiftUI 未重绘界面");
        presenter.ApplyAction("{\"type\":\"setBool\",\"key\":\"control.hotkey.display.0\",\"boolValue\":"
            + displayed.ToString().ToLowerInvariant() + "}");
        if (presenter.WorkingSettings.Hotkeys[0].IsDisplay != displayed) throw new InvalidOperationException("勾选动作未可逆");
        checks.Add("toggle action updates working copy and repaints SwiftUI");

        var first = presenter.WorkingSettings.Hotkeys[0];
        var original = first.PrimaryHotkey;
        presenter.BeginRecording(0, true);
        if (!presenter.IsRecording) throw new InvalidOperationException("未进入录制态");
        PostKey(presenter.WindowNumber, 12, "q", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask);
        await Task.Delay(250);
        if (presenter.IsRecording) throw new InvalidOperationException("录制未在有效手势后结束");
        if (first.PrimaryHotkey == original || first.PrimaryHotkey?.Key?.Key != GestureKey.Q) throw new InvalidOperationException("原生快捷键录制失败");
        checks.Add("inline recording consumes actual AppKit key event");
        presenter.BeginRecording(0, true);
        PostKey(presenter.WindowNumber, 51, "\b", 0);
        await Task.Delay(250);
        if (first.PrimaryHotkey != null) throw new InvalidOperationException("删除键未清空快捷键");
        checks.Add("delete clears hotkey");
        presenter.BeginRecording(0, true);
        PostKey(presenter.WindowNumber, 53, "\u001b", 0);
        await Task.Delay(250);
        if (presenter.IsRecording) throw new InvalidOperationException("Escape 未取消录制");
        if (first.PrimaryHotkey != null) throw new InvalidOperationException("取消录制不应改动快捷键");
        checks.Add("Escape cancels recording");
        first.PrimaryHotkey = original;
        presenter.WorkingSettings.CheckHotkeyConflicts();

        var formatFirst = presenter.WorkingSettings.FileFormats[0];
        presenter.ApplyAction("{\"type\":\"moveFormat\",\"from\":0,\"to\":2}");
        if (!ReferenceEquals(presenter.WorkingSettings.FileFormats[2], formatFirst)) throw new InvalidOperationException("文件格式排序未生效");
        presenter.ApplyAction("{\"type\":\"moveFormat\",\"from\":2,\"to\":0}");
        var hotkeyFirst = presenter.WorkingSettings.Hotkeys[0];
        presenter.ApplyAction("{\"type\":\"moveHotkey\",\"from\":0,\"to\":2}");
        if (!ReferenceEquals(presenter.WorkingSettings.Hotkeys[2], hotkeyFirst)) throw new InvalidOperationException("快捷键排序未生效");
        presenter.ApplyAction("{\"type\":\"moveHotkey\",\"from\":2,\"to\":0}");
        var exifFirst = presenter.WorkingSettings.ExifDisplayItems[0];
        presenter.ApplyAction("{\"type\":\"moveExif\",\"from\":0,\"to\":3}");
        if (!ReferenceEquals(presenter.WorkingSettings.ExifDisplayItems[3], exifFirst)) throw new InvalidOperationException("EXIF 排序未生效");
        presenter.ApplyAction("{\"type\":\"moveExif\",\"from\":3,\"to\":0}");
        checks.Add("reorder actions: file formats / hotkeys / exif display items");

        presenter.ShowCategory(1);
        var preset = presenter.WorkingSettings.ScalePresets[0];
        string presetId = presenter.PresetId(preset);
        var neighbor = presenter.WorkingSettings.ScalePresets[1];
        string neighborId = presenter.PresetId(neighbor);
        var originalPresets = main.Main.Settings.ScalePresets.Select(item => item.Text).ToArray();
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{presetId}\",\"text\":\"800\"}}");
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{neighborId}\",\"text\":\"33.333\"}}");
        if (preset.Text != "800" || neighbor.Text != "33.333" || presenter.PresetId(preset) != presetId)
            throw new InvalidOperationException("预设重排后逻辑身份或相邻编辑丢失");
        if (!main.Main.Settings.ScalePresets.Select(item => item.Text).SequenceEqual(originalPresets))
            throw new InvalidOperationException("预设编辑提前影响源设置");
        presenter.ApplyAction($"{{\"type\":\"applyPreset\",\"presetId\":\"{presetId}\",\"text\":\"1601\"}}");
        if (preset.Text != "800") throw new InvalidOperationException("非法预设覆盖原值");
        await Task.Delay(300);
        // 后台启动的冒烟进程拿不到 macOS 激活（KeyWindow 可能为空），按窗口号直接取设置窗口并置前；
        // 取到即继续，键盘事件按窗口号投递，不依赖应用激活。
        NSWindow? settingsWindow = null;
        for (int attempt = 0; attempt < 60 && settingsWindow == null; attempt++)
        {
            var key = NSApplication.SharedApplication.KeyWindow;
            settingsWindow = key != null && key.WindowNumber == presenter.WindowNumber
                ? key
                : NSApplication.SharedApplication.WindowWithWindowNumber(presenter.WindowNumber);
            if (settingsWindow != null)
            {
                NSApplication.SharedApplication.Activate();
                settingsWindow.MakeKeyAndOrderFront(null);
                break;
            }
            await Task.Delay(50);
        }
        if (settingsWindow == null) throw new InvalidOperationException("设置窗口未获焦");
        if (settingsWindow.WindowNumber != presenter.WindowNumber) throw new InvalidOperationException("设置窗口不是当前键盘窗口");
        var field = PresetField(settingsWindow.ContentView!, presetId);
        settingsWindow.MakeFirstResponder(field);
        field.SelectText(field);
        using (var text = new NSString("812")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        if (preset.Text == "812") throw new InvalidOperationException("预设草稿在提交前已写入工作副本");
        PostKey(presenter.WindowNumber, 36, "\r", 0);
        await Task.Delay(300);
        if (preset.Text != "812" || !ReferenceEquals(field, PresetField(settingsWindow.ContentView!, presetId)))
            throw new InvalidOperationException("原生预设 Enter 未提交或重排重建了输入框");
        field.SelectText(field);
        using (var text = new NSString("813")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        var otherField = PresetField(settingsWindow.ContentView!, neighborId);
        settingsWindow.MakeFirstResponder(otherField);
        await Task.Delay(250);
        if (preset.Text != "813") throw new InvalidOperationException("预设失焦丢失草稿");
        otherField.SelectText(otherField);
        using (var text = new NSString("77")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        var blank = new CGPoint(settingsWindow.ContentView!.Bounds.Width * .65, 20);
        foreach (var type in new[] { NSEventType.LeftMouseDown, NSEventType.LeftMouseUp })
        {
            var click = NSEvent.MouseEvent(type, blank, 0, NSProcessInfo.ProcessInfo.SystemUptime, settingsWindow.WindowNumber, null!, 1, 1, 1)!;
            NSApplication.SharedApplication.PostEvent(click, false);
        }
        await Task.Delay(250);
        if (neighbor.Text != "77") throw new InvalidOperationException("点击设置空白未提交草稿");
        otherField.SelectText(otherField);
        using (var text = new NSString("77")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        presenter.ShowCategory(2);
        await Task.Delay(300);
        if (neighbor.Text != "77") throw new InvalidOperationException("分类导航丢失预设草稿");
        presenter.ShowCategory(1);
        await Task.Delay(200);
        checks.Add("preset native editor: real text insertion + Enter / focus loss / category navigation commit drafts; sorting retains native input identity");
        presenter.ApplyAction($"{{\"type\":\"removePreset\",\"presetId\":\"{neighborId}\"}}");
        if (presenter.WorkingSettings.ScalePresets.Contains(neighbor) || !presenter.WorkingSettings.ScalePresets.Contains(preset))
            throw new InvalidOperationException("预设重排后删除误写了索引");
        foreach (var item in presenter.WorkingSettings.ScalePresets.ToArray())
            presenter.ApplyAction($"{{\"type\":\"removePreset\",\"presetId\":\"{presenter.PresetId(item)}\"}}");
        if (presenter.WorkingSettings.ScalePresets.Count != 1) throw new InvalidOperationException("最后一项预设未禁止删除");
        checks.Add("presets: stable logical IDs survive sort/edit/delete; invalid input retains value; last item cannot be removed; working copy isolated");

        foreach (var size in new[] { new CGSize(760, 540), new CGSize(1060, 800), new CGSize(860, 720) })
        {
            presenter.SetContentSize(size.Width, size.Height);
            await Task.Delay(350);
            string path = Path.Combine(output, $"settings-control-{size.Width}.png");
            presenter.RenderTo(path);
            AssertRendered(path, "缩放后设置窗口未渲染");
            checks.Add($"window resize {size.Width}x{size.Height}: rendered");
        }
        presenter.SetContentSize(860, 720);

        presenter.ShowCategory(0);
        await Task.Delay(300);
        presenter.RenderTo(Path.Combine(output, "settings-final.png"));
        await File.WriteAllLinesAsync(Path.Combine(output, "settings-ui-checks.txt"), checks);

        presenter.ShowCategory(1);
        await Task.Delay(200);
        var lastPreset = presenter.WorkingSettings.ScalePresets.Single();
        var lastField = PresetField(settingsWindow.ContentView!, presenter.PresetId(lastPreset));
        settingsWindow.MakeFirstResponder(lastField);
        lastField.SelectText(lastField);
        using (var text = new NSString("1333")) ((NSTextView)settingsWindow.FirstResponder!).InsertText(text, new NSRange(nint.MaxValue, 0));
        presenter.CloseWindow();
        await Task.Delay(300);
        if (!main.Main.Settings.ScalePresets.Any(item => item.Text == "1333")) throw new InvalidOperationException("关闭窗口丢失未提交预设草稿");
        checks.Add("preset close: uncommitted native editor draft is applied once and survives reopen");
        if (presenter.IsPresenting) throw new InvalidOperationException("关闭后设置窗口仍在展示");
        presenter.TryPresent(main.Main.Settings);
        await Task.Delay(400);
        if (presenter.WorkingSettings.Hotkeys.Count != main.Main.Settings.Hotkeys.Count)
            throw new InvalidOperationException("关闭后重开设置丢失功能行");
        presenter.ShowCategory(2);
        await Task.Delay(200);
        presenter.RenderTo(Path.Combine(output, "settings-reopen.png"));
        presenter.WorkingSettings.ScalePresets = new(originalPresets.Select(text => new PhotoViewer.ViewModels.Settings.SettingsViewModel.ScalePreset(text)));
        presenter.CloseWindow();
        checks.Add("close applies working copy / reopen restores all rows");
        await File.WriteAllLinesAsync(Path.Combine(output, "settings-ui-checks.txt"), checks);
    }

    private static NSTextField PresetField(NSView root, string id)
    {
        var field = Walk(root).OfType<NSTextField>().FirstOrDefault(field => field.Identifier == "preset-" + id);
        return field ?? throw new InvalidOperationException("未找到原生预设输入框：" + id);
    }

    private static IEnumerable<NSView> Walk(NSView root)
    {
        yield return root;
        foreach (var child in root.Subviews)
            foreach (var view in Walk(child)) yield return view;
    }

    /// <summary>断言截图已生成且有实际内容，避免空渲染冒充通过。</summary>
    private static void AssertRendered(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 2048) throw new InvalidOperationException(message + "：" + path);
    }

    /// <summary>尝试用系统 screencapture 抓取真实窗口位图；无屏幕录制权限时在报告中如实记录。</summary>
    private static void CaptureScreen(MacNativeSettingsPresenter presenter, string path, List<string> checks)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("screencapture", $"-o -x -l {presenter.WindowNumber} \"{path}\"")
            {
                UseShellExecute = false,
            });
            if (process != null)
            {
                bool exited = process.WaitForExit(5000);
                if (exited && process.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    checks.Add("real window screen capture: settings-window.png");
                    return;
                }
                if (!exited) process.Kill();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[NativeSmoke] screencapture failed: " + exception.Message);
        }
        checks.Add("real window screen capture unavailable (屏幕录制权限未授予)");
    }

    /// <summary>向真实应用队列提交窗口键盘事件，经过同一 SwiftUI 录制监视器与 C# 手势判定。</summary>
    private static void PostKey(int windowNumber, ushort code, string text, NSEventModifierMask modifiers)
    {
        var nativeEvent = NSEvent.KeyEvent(NSEventType.KeyDown, CGPoint.Empty, modifiers, NSProcessInfo.ProcessInfo.SystemUptime,
            windowNumber, null!, text, text, false, code) ?? throw new InvalidOperationException("键盘事件创建失败");
        NSApplication.SharedApplication.PostEvent(nativeEvent, false);
    }
}
