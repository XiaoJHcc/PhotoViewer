using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PhotoViewer.Android;

/// <summary>
/// 唯一 launcher / 外部打开入口：解析启动开关后分派到原生壳或 Avalonia 旧壳。
/// 自身不初始化任何 UI 框架，也不承载界面，启动目标 Activity 后立即结束。
/// </summary>
[Activity(
    Name = "cc.xiaojh.PhotoViewer.AndroidBootstrapActivity",
    Label = "PhotoViewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataMimeType = "image/*")]
[IntentFilter(
    [Intent.ActionSend, Intent.ActionSendMultiple],
    Categories = [Intent.CategoryDefault],
    DataMimeType = "image/*")]
public sealed class AndroidBootstrapActivity : Activity
{
    /// <summary>
    /// 冷启动：分派到目标壳。
    /// </summary>
    /// <param name="savedInstanceState">系统恢复状态。</param>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Dispatch(Intent);
    }

    /// <summary>
    /// 已运行应用再次收到 Intent（launcher 复用或外部打开）时重新分派。
    /// </summary>
    /// <param name="intent">新的系统 Intent。</param>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Dispatch(intent);
    }

    /// <summary>
    /// 选择壳并转发原始 Action/Data/ClipData/Extras。
    /// </summary>
    /// <param name="intent">系统传入的 Intent。</param>
    private void Dispatch(Intent? intent)
    {
        bool legacy = UseLegacyShell(intent);
        Type target = legacy ? typeof(MainActivity) : typeof(AndroidNativeActivity);
        var next = new Intent(this, target);

        if (intent != null)
        {
            next.SetAction(intent.Action);
            next.SetData(intent.Data);
            next.ClipData = intent.ClipData;
            next.SetFlags(intent.Flags);
            if (intent.Extras != null)
            {
                next.PutExtras(intent.Extras);
            }
        }

        Console.WriteLine($"[Bootstrap] → {(legacy ? "legacy" : "native")} 壳");
        StartActivity(next);
        Finish();
    }

    /// <summary>
    /// Debug 默认原生、Release 默认旧壳；<c>--legacy-ui</c> / <c>--native</c> 可显式覆盖。
    /// 启动开关来自 <c>adb shell am start ... --es pv-flags "--legacy-ui"</c>。
    /// </summary>
    /// <param name="intent">系统传入的 Intent。</param>
    /// <returns>是否进入 Avalonia 旧壳。</returns>
    private static bool UseLegacyShell(Intent? intent)
    {
        string? flags = intent?.GetStringExtra("pv-flags");
#if DEBUG
        return flags?.Contains("--legacy-ui", StringComparison.Ordinal) == true;
#else
        return flags?.Contains("--native", StringComparison.Ordinal) != true;
#endif
    }
}
