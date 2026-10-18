using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android;

/// <summary>
/// Android 原生壳 Activity：直接承载 <see cref="Native.AndroidNativeShell"/> 的根视图。
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
    /// 创建原生壳根视图。
    /// </summary>
    /// <param name="savedInstanceState">系统恢复状态。</param>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false);
        // 明确请求广色域窗口（API 26+）；不设置时窗口默认按 sRGB 合成，EGL 也拿不到 P3 表面。
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            Window!.ColorMode = ActivityColorMode.WideColorGamut;
        var root = Native.AndroidNativeShell.CreateRootView(this, Intent);
        SetContentView(root);
        OnBackPressedDispatcher.AddCallback(this, new NativeBackCallback(this));
        if (Intent?.GetBooleanExtra("pv-settings", false) == true)
        {
            Window?.DecorView?.Post(() => Native.AndroidNativeShell.Main?.OpenSettingWindow());
        }
        Console.WriteLine("[Native] AndroidNativeActivity 就绪");
    }

    /// <summary>Activity 恢复（SAF 选择器返回）后强制刷新栏位与主图。</summary>
    protected override void OnResume()
    {
        base.OnResume();
        Native.AndroidNativeShell.RefreshAfterResume();
    }

    /// <summary>
    /// 运行期新 Intent（外部打开）：交给原生壳解析并投递共享外部打开链路。
    /// </summary>
    /// <param name="intent">新的系统 Intent。</param>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Console.WriteLine($"[Native] OnNewIntent action={intent?.Action} data={intent?.Data}");
        Native.AndroidNativeShell.HandleIntent(intent);
    }

    /// <summary>
    /// 硬件键盘：按键按下/重复交给共享命令表；未命中或设置页打开时按系统默认处理。
    /// </summary>
    /// <param name="e">按键事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && e.Action is KeyEventActions.Down or KeyEventActions.Multiple)
        {
            var keyCode = e.KeyCode;
            if (keyCode is not (Keycode.Back or Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute
                or Keycode.Camera or Keycode.Power))
            {
                if (Native.AndroidNativeShell.HandleHardwareKey(keyCode, (int)e.MetaState,
                    e.Action == KeyEventActions.Multiple)) return true;
            }
        }
        return base.DispatchKeyEvent(e);
    }

    /// <summary>
    /// SAF 选择器结果：交 <see cref="AndroidFilePicker"/> 收口（持久授权与存储引用）。
    /// </summary>
    /// <param name="requestCode">请求码。</param>
    /// <param name="resultCode">结果码。</param>
    /// <param name="data">返回 Intent。</param>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        AndroidFilePicker.HandleActivityResult(requestCode, resultCode, data);
    }

    /// <summary>返回键：设置 sheet 打开时优先关闭/回上级；否则结束原生 Activity。</summary>
    private sealed class NativeBackCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly Activity _activity;

        /// <summary>建立始终启用的返回回调。</summary>
        /// <param name="activity">宿主 Activity。</param>
        public NativeBackCallback(Activity activity) : base(true) => _activity = activity;

        /// <summary>返回处理。</summary>
        public override void HandleOnBackPressed()
        {
            if (Native.AndroidNativeShell.DismissPhotoMenu()) return;
            if (Native.AndroidNativeShell.DismissFilterDropdown()) return;
            if (Native.AndroidNativeSettingsPresenter.HandleBack()) return;
            if (Native.AndroidToolsPresenter.HandleBack()) return;
            _activity.Finish();
        }
    }

    /// <summary>
    /// 系统内存 trim 事件直报统一内存管家（只升不降，周期探测负责降级）。
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
