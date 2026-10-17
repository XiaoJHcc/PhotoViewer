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

    /// <summary>绑定当前原生 Activity。</summary>
    /// <param name="activity">宿主 Activity。</param>
    public AndroidNativeWindowPresenter(Activity activity) => _activity = activity;

    /// <summary>展示设置窗口（A4 之前的占位）。</summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public void ShowSettingsWindow(SettingsViewModel settings)
    {
        Console.WriteLine("[Native] 设置 BottomSheet 将在 A4 接入");
        Toast.MakeText(_activity, "设置页将在 A4 接入", ToastLength.Short)?.Show();
    }

    /// <summary>展示工具页（A5 之前的占位）。</summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    public void ShowOrActivateToolsWindow(ToolsViewModel tools)
    {
        Console.WriteLine("[Native] 工具页将在 A5 接入");
        Toast.MakeText(_activity, "工具页将在 A5 接入", ToastLength.Short)?.Show();
    }
}
