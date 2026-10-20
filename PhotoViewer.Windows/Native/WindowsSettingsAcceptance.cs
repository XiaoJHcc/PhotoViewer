using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// W-M5 设置窗口定向验收:真实窗口、五分类切换、控件绑定、工作副本隔离与关闭回写。
/// 由 <c>--pv-native-settings</c> 触发,只读自建隔离设置,不写用户配置。
/// </summary>
internal static class WindowsSettingsAcceptance
{
    /// <summary>运行设置窗口验收。</summary>
    /// <param name="main">主视图模型</param>
    /// <param name="presenter">设置窗口展示器</param>
    /// <returns>任务</returns>
    public static async Task RunAsync(MainViewModel main, WindowsNativeSettingsPresenter presenter)
    {
        var checks = new List<string>();
        try
        {
            Directory.CreateDirectory(WindowsMainWindow.SmokeOutputDirectory);
            main.OpenSettingWindow();
            var window = presenter.Window ?? throw new InvalidOperationException("设置窗口未创建");
            await WaitAsync(() => window.Root.XamlRoot != null && window.Root.ActualWidth > 0, "设置窗口布局");
            await WaitAsync(() => window.Root.ActualWidth > 900, "设置窗口尺寸");
            window.Root.UpdateLayout();
            var scale = window.Root.XamlRoot!.RasterizationScale;
            var windowWidth = window.Window.AppWindow.Size.Width / scale;

            Require(window.Root.RequestedTheme == ElementTheme.Dark, "设置窗口不是深色主题");
            Require(window.Navigation is NavigationView, "设置窗口分类不是 NavigationView");
            Require(window.TitleBarControl is TitleBar, "设置窗口没有 TitleBar 控件");
            Require(window.Window.SystemBackdrop is MicaBackdrop, "设置窗口没有 Mica 背景");
            Require(window.Navigation.MenuItems.Count == 5, "分类数不是 5");
            Require(window.Pages.Count == 5, "页面数不是 5");
            Require(FindDescendant<NumberBox>(window.Pages[0]) != null, "文件页没有 NumberBox");
            Require(Math.Abs(windowWidth - WindowsLayout.SettingsWindowWidth) < 4,
                $"窗口宽度不是 960 DIP: {windowWidth:0.0}");
            checks.Add($"PASS window: dark, Mica, NavigationView + TitleBar + NumberBox, 5 categories, window={windowWidth:0} DIP, content={window.Root.ActualWidth:0} DIP");

            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.Left))) == "←",
                "方向键显示文本不是箭头");
            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.D2))) == "2",
                "数字键显示文本仍是枚举名");
            Require(WindowsInput.FormatGesture(AppGesture.FromKey(new AppKeyGesture(GestureKey.Oem3))) == "`",
                "OEM 键显示文本仍是枚举名");
            Require(WindowsInput.FormatGesture(AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp))) == "滚轮上",
                "滚轮显示文本不是“滚轮上”");
            checks.Add("PASS gesture text: arrows and mouse wheel format");

            for (var index = 0; index < 5; index++)
            {
                window.Navigation.SelectedItem = window.Navigation.MenuItems[index];
                await Task.Delay(40);
                window.Root.UpdateLayout();
                Require(window.Pages[index].Visibility == Visibility.Visible, $"分类 {index} 页面未显示");
                for (var other = 0; other < 5; other++)
                {
                    if (other != index)
                    {
                        Require(window.Pages[other].Visibility == Visibility.Collapsed,
                            $"分类 {index} 下页面 {other} 仍然可见");
                    }
                }
            }

            checks.Add("PASS navigation: every category shows exactly one page");
            window.Pages[4].UpdateLayout();
            Require(FindDescendant<InfoBar>(window.Pages[4]) != null, "AI 页没有 InfoBar");

            // 回到文件页做控件绑定检查。
            window.Navigation.SelectedItem = window.Navigation.MenuItems[0];
            await Task.Delay(60);
            window.Root.UpdateLayout();

            var toggle = FindDescendant<ToggleSwitch>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有 ToggleSwitch");
            toggle.IsOn = !toggle.IsOn;
            await Task.Delay(20);
            Require(window.Settings.SameNameAsOnePhoto == toggle.IsOn, "开关没有写回工作副本");
            Require(main.Settings.SameNameAsOnePhoto != toggle.IsOn, "工作副本泄漏到主设置");

            var numberBox = FindDescendant<NumberBox>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有数值框");
            numberBox.Value = 3;
            await Task.Delay(20);
            Require(window.Settings.NativePreloadParallelism == 3, "数值框没有写回工作副本");

            var slider = FindDescendant<Slider>(window.Pages[0])
                ?? throw new InvalidOperationException("文件页没有数值滑条");
            var memoryBefore = window.Settings.BitmapCacheMaxMemory;
            slider.Value = slider.Value >= 0.5 ? 0.25 : 0.75;
            await Task.Delay(20);
            Require(window.Settings.BitmapCacheMaxMemory != memoryBefore, "数值滑条没有写回工作副本");
            checks.Add("PASS controls: toggle + NumberBox + slider write through to work copy");

            var list = FindDescendant<ListView>(window.Pages[2]);
            if (list == null)
            {
                throw new InvalidOperationException("控制页没有快捷键列表");
            }

            Require(list.Items.Count == window.Settings.Hotkeys.Count, "快捷键列表行数与共享集合不一致");
            var first = window.Settings.Hotkeys[0];
            if (list.ItemsSource is not System.Collections.IList rows || rows.Count < 2)
            {
                throw new InvalidOperationException("快捷键列表没有可重排的行集合");
            }

            var firstRow = rows[0];
            rows.RemoveAt(0);
            rows.Insert(1, firstRow);
            await Task.Delay(80);
            Require(window.Settings.Hotkeys[1] == first, "列表重排没有回写到共享集合");
            checks.Add("PASS list: UI reorder writes back to shared collection");

            var recordButton = FindDescendant<Button>(list)
                ?? throw new InvalidOperationException("快捷键行没有录制按钮");
            var recordPeer = new ButtonAutomationPeer(recordButton);
            recordPeer.Invoke();
            await Task.Delay(40);
            var recordText = FindDescendant<TextBlock>(recordButton);
            Require(recordText?.Text == "按下按键…", "录制态按钮文本未切换");
            window.Recorder.Cancel();
            await Task.Delay(40);
            Require(recordText?.Text != "按下按键…", "取消录制后按钮文本未复位");
            checks.Add("PASS hotkey record: begin/cancel updates button state");

            var beforeClose = main.Settings.SameNameAsOnePhoto;
            var working = window.Settings.SameNameAsOnePhoto;
            window.Close();
            await WaitAsync(() => !presenter.IsOpen, "设置窗口关闭");
            Require(main.Settings.SameNameAsOnePhoto == working, "关闭后工作副本没有回写");
            Require(beforeClose != working, "回写断言前值未变化");
            checks.Add("PASS lifecycle: work copy applied exactly once on close");

            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            checks.Add("FAIL " + exception);
            NativeStartupLog.Fail("native settings acceptance", exception);
            presenter.CloseIfOpen();
        }
        finally
        {
            File.WriteAllText(
                Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "settings-checks.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件</param>
    /// <param name="what">失败描述</param>
    /// <returns>任务</returns>
    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("等待超时:" + what);
    }

    /// <summary>深度优先查找第一个指定类型子元素。</summary>
    /// <typeparam name="T">元素类型</typeparam>
    /// <param name="root">根</param>
    /// <returns>元素;找不到返回 null</returns>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>断言。</summary>
    /// <param name="condition">条件</param>
    /// <param name="message">失败描述</param>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
