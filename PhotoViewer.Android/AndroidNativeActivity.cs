using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android;

/// <summary>
/// Android 原生壳 Activity：不继承 AvaloniaActivity，也不创建任何 Avalonia 对象。
/// A1b 只承载 <see cref="Native.AndroidNativeShell"/> 的基座根视图；A2/A3 再接主图与主屏。
/// </summary>
[Activity(
    Name = "cc.xiaojh.PhotoViewer.AndroidNativeActivity",
    Label = "PhotoViewer",
    Theme = "@style/Theme.PhotoViewer.Native",
    Icon = "@drawable/icon",
    Exported = false,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class AndroidNativeActivity : AppCompatActivity
{
    /// <summary>
    /// 创建原生壳根视图；这里不调用 Avalonia。
    /// </summary>
    /// <param name="savedInstanceState">系统恢复状态。</param>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false);
        var root = Native.AndroidNativeShell.CreateRootView(this, Intent);
        SetContentView(root);
        Console.WriteLine("[Native] AndroidNativeActivity 就绪（未初始化 Avalonia）");
    }

    /// <summary>
    /// 运行期新 Intent（外部打开）暂只记录；A2 接入原生存储引用后再处理。
    /// </summary>
    /// <param name="intent">新的系统 Intent。</param>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Console.WriteLine($"[Native] OnNewIntent action={intent?.Action} data={intent?.Data}");
    }

    /// <summary>
    /// 系统内存 trim 事件直报统一内存管家（与旧壳同口径，见 MainActivity）。
    /// </summary>
    /// <param name="level">系统 trim 级别。</param>
    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);
        var target = level switch
        {
            TrimMemory.RunningLow => PressureLevel.Caution,
            TrimMemory.RunningCritical => PressureLevel.Warning,
            TrimMemory.UiHidden => PressureLevel.Warning,
            TrimMemory.Background => PressureLevel.Critical,
            TrimMemory.Moderate => PressureLevel.Critical,
            TrimMemory.Complete => PressureLevel.Critical,
            _ => PressureLevel.Normal,
        };
        if (target != PressureLevel.Normal)
            MemoryGovernor.ReportOsEvent(target);
    }
}
