using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PhotoViewer.Android;

/// <summary>
/// 唯一 launcher / 外部打开入口：转发到原生壳。
/// 自身不初始化任何 UI 框架，也不承载界面，启动 <see cref="AndroidNativeActivity"/> 后立即结束。
/// </summary>
[Activity(
    Name = "cc.xiaojh.PhotoViewer.AndroidBootstrapActivity",
    Label = "PhotoViewer",
    Theme = "@style/Theme.PhotoViewer.Native",
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
    /// 转发原始 Action/Data/ClipData/Extras 到原生壳。
    /// </summary>
    /// <param name="intent">系统传入的 Intent。</param>
    private void Dispatch(Intent? intent)
    {
        var next = new Intent(this, typeof(AndroidNativeActivity));

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

        Console.WriteLine("[Bootstrap] → native 壳");
        StartActivity(next);
        Finish();
    }
}
