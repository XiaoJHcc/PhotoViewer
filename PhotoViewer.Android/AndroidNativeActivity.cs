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
/// 不写 Activity label：显示名只由 application label（根 props 的 PhotoViewerDisplayName）决定。
/// </summary>
[Activity(
    Name = "cc.xiaojh.PhotoViewer.AndroidNativeActivity",
    Theme = "@style/Theme.PhotoViewer.Native",
    Icon = "@drawable/icon",
    Exported = false,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.Touchscreen)]
public sealed class AndroidNativeActivity : AppCompatActivity
{
    /// <summary>诊断开关的 Intent extra 名：请求 F16 窗口像素格式（只在 <c>pv-check=glass</c> 时生效）。</summary>
    private const string GlassF16Extra = "pv-glass-f16";

    /// <summary>本次 Activity 承载的原生根视图（按键派发前核对窗口内弹层是否打开）。</summary>
    private Native.AndroidNativeRootView? _root;

    /// <summary>
    /// 创建原生壳根视图。
    /// </summary>
    /// <param name="savedInstanceState">系统恢复状态。</param>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false);
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && Window!.Attributes is { } attributes)
        {
            attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            Window!.Attributes = attributes;
        }
        // 明确请求广色域窗口（API 26+）；不设置时窗口默认按 sRGB 合成，EGL 也拿不到 P3 表面。
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            Window!.ColorMode = ActivityColorMode.WideColorGamut;
        // 诊断用的 F16 请求必须早于根视图与窗口表面建立：窗口格式一旦有内容再改会重建表面。
        MaybeRequestGlassF16Window(Intent);
        var root = Native.AndroidNativeShell.CreateRootView(this, Intent);
        _root = root as Native.AndroidNativeRootView;
        SetContentView(root);
        ApplySystemBarVisibility();
        OnBackPressedDispatcher.AddCallback(this, new NativeBackCallback(this));
        if (Intent?.GetBooleanExtra("pv-settings", false) == true)
        {
            Window?.DecorView?.Post(() => Native.AndroidNativeShell.Main?.OpenSettingWindow());
        }
        Console.WriteLine("[Native] AndroidNativeActivity 就绪");
    }

    /// <summary>
    /// 仅诊断：把窗口像素格式请求成 F16（<c>android.graphics.PixelFormat.RGBA_F16</c>，API 26+），
    /// 供阶段 0 核对广色域窗口走的是 8 位缓冲还是 F16 缓冲；广色域窗口模式（<see cref="Window.ColorMode"/>）保持不变。
    /// 只在 <c>pv-check=glass</c> 且 <c>pv-glass-f16</c> 为真时生效，且只在启动时生效（运行期新 Intent 不改窗口格式）；
    /// 默认启动完全不经过这里。这里只发出「请求」，系统是否接受由运行期的实际缓冲决定，
    /// 日志与读数都不得声称已生效。
    /// </summary>
    /// <param name="intent">启动 Intent。</param>
    private void MaybeRequestGlassF16Window(Intent? intent)
    {
        if (intent == null) return;
        // 只有显式带这个 extra（adb --ez pv-glass-f16 true，且为布尔类型）才继续：
        // 默认启动在这里就返回，窗口行为一字不变。
        if (!intent.GetBooleanExtra(GlassF16Extra, false)) return;
        if (!string.Equals(intent.GetStringExtra("pv-check"), "glass", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Native] {GlassF16Extra}：已忽略（仅 pv-check=glass 生效）");
            return;
        }
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            Console.WriteLine($"[Native] {GlassF16Extra}：已忽略（RGBA_F16 需要 API 26，当前 API {(int)Build.VERSION.SdkInt}）");
            return;
        }
        Window!.SetFormat(global::Android.Graphics.Format.RgbaF16);
        // mAttrs.format 只是我们刚写进去的请求值；真实缓冲格式只能由 SurfaceFlinger 侧的证据判断。
        Console.WriteLine($"[Native] {GlassF16Extra}：已请求窗口像素格式 RGBA_F16（API {(int)Build.VERSION.SdkInt}，"
            + $"ColorMode={Window.ColorMode}，请求值={Window.Attributes?.Format}）；"
            + "系统是否接受由运行期实际缓冲决定，此处不作断言");
    }

    /// <summary>配置变化不重建 Activity，按新方向同步状态栏与手势条。</summary>
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBarVisibility();
    }

    /// <summary>系统界面或对话框返回后重新应用当前方向的状态栏可见性。</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) ApplySystemBarVisibility();
    }

    /// <summary>当前系统状态栏是否可见（layout 验收读取）。</summary>
    internal bool IsStatusBarVisibleForChecks() => AndroidSystemBars.IsStatusBarVisible(this);

    private void ApplySystemBarVisibility() => AndroidSystemBars.Apply(this);

    /// <summary>
    /// Activity 可见（进入前台）：恢复后台预取，并按存活预取器重新计划邻居与当前图 Full 预热。
    /// 暂停/恢复只挂在 OnStart/OnStop 上——半屏、多窗口或系统对话框只触发 OnPause，界面仍在用，不该停预取。
    /// </summary>
    protected override void OnStart()
    {
        base.OnStart();
        Native.AndroidNativeShell.SetApplicationActive(true);
    }

    /// <summary>
    /// Activity 不可见（进后台/被完全遮盖/正在退出）：停后台预取并取消尚未开跑与在途的后台工作。
    /// </summary>
    protected override void OnStop()
    {
        base.OnStop();
        Native.AndroidNativeShell.SetApplicationActive(false);
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
    /// 硬件键盘：按键首次按下与系统重复交给共享命令表；未命中或设置页打开时按系统默认处理。
    /// 重复只认 <see cref="KeyEventActions.Down"/> + <see cref="KeyEvent.RepeatCount"/> &gt; 0；
    /// <see cref="KeyEventActions.Multiple"/> 是输入法的多字符提交，不冒充长按，原样交还系统。
    /// </summary>
    /// <param name="e">按键事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        // 主窗口内弹层打开期间暂停照片全局快捷键：方向键/回车/Esc 由面板内容消费（INPUT-01、MENU-03）。
        if (e != null && _root?.IsGlassPopupOpen == true) return base.DispatchKeyEvent(e);
        if (e != null && e.Action == KeyEventActions.Down)
        {
            var keyCode = e.KeyCode;
            if (keyCode is not (Keycode.Back or Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute
                or Keycode.Camera or Keycode.Power))
            {
                if (Native.AndroidNativeShell.HandleHardwareKey(keyCode, (int)e.MetaState, e.RepeatCount > 0)) return true;
            }
        }
        return base.DispatchKeyEvent(e);
    }

    /// <summary>
    /// SAF 选择器结果：交 <see cref="AndroidFilePicker"/> 实例收口（持久授权与存储引用）。
    /// </summary>
    /// <param name="requestCode">请求码。</param>
    /// <param name="resultCode">结果码。</param>
    /// <param name="data">返回 Intent。</param>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Native.AndroidNativeShell.FilePickerForResults?.HandleActivityResult(requestCode, resultCode, data);
    }

    /// <summary>
    /// Activity 销毁：等待中的 SAF 请求按取消收口；主窗真正退出（非配置重建）时释放根视图与共享 VM。
    /// 配置重建（字体/语言/密度等）保留 VM 与当前文件夹，由新 Activity 复用。
    /// </summary>
    protected override void OnDestroy()
    {
        Native.AndroidNativeShell.FilePickerForResults?.CancelPending();
        if (!IsChangingConfigurations) Native.AndroidNativeShell.DisposeMain();
        base.OnDestroy();
    }

    /// <summary>返回键：设置 sheet 打开时优先关闭/回上级；否则结束原生 Activity。</summary>
    private sealed class NativeBackCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly Activity _activity;

        /// <summary>建立始终启用的返回回调。</summary>
        /// <param name="activity">宿主 Activity。</param>
        public NativeBackCallback(Activity activity) : base(true) => _activity = activity;

        /// <summary>返回处理：浮窗/菜单/下拉/设置/工具依次优先消费，最后结束 Activity。</summary>
        public override void HandleOnBackPressed()
        {
            if (Native.AndroidNativeShell.DismissPhotoMenu()) return;
            if (Native.AndroidNativeShell.DismissRatingPopup()) return;
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
            // UiHidden 只表示界面不可见（切到后台或被系统界面遮住），不是内存压力读数：
            // 把它当压力会让每次切出都裁缓存、并让回前台多等一轮 RecoveryHold 观察期，
            // 后台的后台预取由 BitmapPrefetcher 的应用活跃闸处理，不借用压力通道。
            TrimMemory.Background => PressureLevel.Critical,
            TrimMemory.Moderate => PressureLevel.Critical,
            TrimMemory.Complete => PressureLevel.Critical,
            _ => PressureLevel.Normal,
        };
        if (target != PressureLevel.Normal)
            MemoryGovernor.ReportOsEvent(target);
    }
}
