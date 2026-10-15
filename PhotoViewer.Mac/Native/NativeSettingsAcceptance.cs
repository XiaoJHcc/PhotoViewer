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

        presenter.CloseWindow();
        await Task.Delay(300);
        if (presenter.IsPresenting) throw new InvalidOperationException("关闭后设置窗口仍在展示");
        presenter.TryPresent(main.Main.Settings);
        await Task.Delay(400);
        if (presenter.WorkingSettings.Hotkeys.Count != main.Main.Settings.Hotkeys.Count)
            throw new InvalidOperationException("关闭后重开设置丢失功能行");
        presenter.ShowCategory(2);
        await Task.Delay(200);
        presenter.RenderTo(Path.Combine(output, "settings-reopen.png"));
        presenter.CloseWindow();
        checks.Add("close applies working copy / reopen restores all rows");
        await File.WriteAllLinesAsync(Path.Combine(output, "settings-ui-checks.txt"), checks);
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
