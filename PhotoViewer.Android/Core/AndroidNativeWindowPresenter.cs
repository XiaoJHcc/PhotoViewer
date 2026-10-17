using System;
using Android.App;
using Android.Widget;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Android.Core;

/// <summary>
/// 原生窗口门面（A1b 占位）：设置/工具页分别在 A4/A5 接入 BottomSheet 与原生工具页。
/// 现在只给一条可见反馈并记录日志，避免共享 VM 的命令静默失败。
/// </summary>
public sealed class AndroidNativeWindowPresenter : INativeWindowPresenter
{
    private readonly Activity _activity;
    private readonly Native.AndroidNativeSettingsPresenter _settings;
    private readonly Native.AndroidToolsPresenter _tools;

    /// <summary>绑定当前原生 Activity 与设置/工具展示器。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="settings">原生设置半屏展示器。</param>
    /// <param name="tools">原生工具页展示器。</param>
    public AndroidNativeWindowPresenter(Activity activity, Native.AndroidNativeSettingsPresenter settings, Native.AndroidToolsPresenter tools)
    {
        _activity = activity;
        _settings = settings;
        _tools = tools;
    }

    /// <summary>展示设置窗口（A4 之前的占位）。</summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public void ShowSettingsWindow(SettingsViewModel settings)
    {
        if (_settings.TryPresent(settings)) return;
        Console.Error.WriteLine("[Native] 原生设置页未能展示");
        Toast.MakeText(_activity, "设置页打开失败", ToastLength.Short)?.Show();
    }

    /// <summary>展示工具页（A5 之前的占位）。</summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    public void ShowOrActivateToolsWindow(ToolsViewModel tools) => _tools.ShowOrActivate(tools);
}
