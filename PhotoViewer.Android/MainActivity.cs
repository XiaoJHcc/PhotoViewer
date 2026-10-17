using System;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Microsoft.ML.OnnxRuntime;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using ReactiveUI.Avalonia;

namespace PhotoViewer.Android;

/// <summary>
/// 迁移期 Avalonia 旧壳入口（由 <see cref="AndroidBootstrapActivity"/> 分派）。
/// 普通 <see cref="AndroidApp"/> 不初始化 Avalonia；本 Activity 在 <c>base.OnCreate</c> 前
/// 用 <see cref="AppBuilder"/> 自建 Avalonia 并传入 <see cref="ISingleViewApplicationLifetime"/>，
/// 这样原生壳启动时不会创建任何 Avalonia 对象。
/// </summary>
[Activity(
    Name = "cc.xiaojh.PhotoViewer.MainActivity",
    Label = "PhotoViewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    Exported = false,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaActivity
{
    private const int StoragePermissionRequestCode = 1;
    private IActivityApplicationLifetime? _lifetime;

    /// <summary>
    /// 旧壳入口：先自建 Avalonia（不依赖 Application 类型），再处理权限与外部打开 Intent。
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        EnsureAvalonia();
        base.OnCreate(savedInstanceState);

        // base.OnCreate 只创建 AvaloniaView，不会把它挂成 Activity 的 content view；
        // 这里在 _view 已存在后写入主视图，Content setter 才会 SetContentView + 设置内容。
        Content = _lifetime?.MainViewFactory?.Invoke();
        Console.WriteLine($"[Legacy] 主视图挂载完成（mainView={(Content != null)}）");

        RequestStoragePermissions();
        HandleIncomingIntent(Intent);
    }

    /// <summary>
    /// 处理已运行应用再次收到的新 Intent。
    /// </summary>
    /// <param name="intent">新的系统 Intent。</param>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        if (intent != null)
        {
            Intent = intent;
        }

        HandleIncomingIntent(intent);
    }

    /// <summary>
    /// 系统内存 trim 事件直报统一内存管家（只升不降，周期探测负责降级）。
    /// 映射：RunningLow→Caution；RunningCritical/UiHidden→Warning；Background 及以上→Critical。
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

    /// <summary>
    /// 在 base.OnCreate 前建立 Avalonia 单视图应用；App 会通过 lifetime 创建主视图。
    /// Avalonia 12 把 ISingleViewApplicationLifetime 标为 NotClientImplementable，
    /// 这里实例化 Avalonia.Android 自己的 internal ApplicationLifetime（与
    /// AvaloniaAndroidApplication 使用的是同一个类型），待上游开放公开 API 后移除反射。
    /// </summary>
    private void EnsureAvalonia()
    {
        if (Avalonia.Application.Current != null)
        {
            return;
        }

        var lifetime = CreateAndroidLifetime();
        AppBuilder.Configure<App>()
            .UseAndroid()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "sans-serif"
            })
            .UseReactiveUI(_ => { })
            .AfterSetup(_ =>
            {
                HeifLoader.Initialize(new AndroidLibHeifDecoder());
                PerformanceBudget.Initialize(new AndroidPerformanceBudget());
                MemoryGovernor.Initialize(new AndroidMemoryBudgetProvider());
                XmpWriter.Initialize(new AndroidXmpWriter(ContentResolver!));
                SettingsService.ConfigureStorage(new AndroidSettingsStorage());
                DinoFeatureExtractor.ConfigureSession(options =>
                {
                    options.AppendExecutionProvider_Nnapi();
                });
            })
            .SetupWithLifetime(lifetime);

        // App.OnFrameworkInitializationCompleted 对内部 lifetime 先命中
        // IActivityApplicationLifetime 分支（MainViewFactory），不会填 MainView；
        // 主视图在 base.OnCreate 之后写入（见 OnCreate）。
        _lifetime = lifetime;
        Console.WriteLine("[Legacy] Android Avalonia 旧壳已初始化（由 MainActivity 自建）");
    }

    /// <summary>
    /// 创建 Avalonia.Android 的单视图生命周期实例。
    /// </summary>
    /// <returns>可由 <see cref="AppBuilder.SetupWithLifetime"/> 使用的生命周期。</returns>
    private static IActivityApplicationLifetime CreateAndroidLifetime()
    {
        var type = typeof(AvaloniaActivity).Assembly.GetType("Avalonia.Android.ApplicationLifetime", throwOnError: true)!;
        return (IActivityApplicationLifetime)Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>
    /// 请求 Android 运行时存储权限（仅 API ≤ 32；33+ 由 SAF 接管）。
    /// </summary>
    private void RequestStoragePermissions()
    {
        if ((int)Build.VERSION.SdkInt > 32)
        {
            return;
        }

        var permissions = (int)Build.VERSION.SdkInt >= 30
            ? new[] { Manifest.Permission.ReadExternalStorage }
            : new[] { Manifest.Permission.ReadExternalStorage, Manifest.Permission.WriteExternalStorage };

        bool needsPermission = false;
        foreach (var permission in permissions)
        {
            if (ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
            {
                needsPermission = true;
                break;
            }
        }

        if (needsPermission)
        {
            ActivityCompat.RequestPermissions(this, permissions, StoragePermissionRequestCode);
        }
    }

    /// <summary>
    /// 处理权限请求结果，仅做日志记录。
    /// </summary>
    /// <param name="requestCode">请求编号。</param>
    /// <param name="permissions">权限数组。</param>
    /// <param name="grantResults">授权结果。</param>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == StoragePermissionRequestCode)
        {
            for (int i = 0; i < permissions.Length; i++)
            {
                Console.WriteLine(grantResults[i] == Permission.Granted
                    ? $"Permission granted: {permissions[i]}"
                    : $"Permission denied: {permissions[i]}");
            }
        }
    }

    /// <summary>
    /// 将 Android Intent 交给外部打开桥接层统一解析（仅旧壳；原生壳走原生存储引用）。
    /// </summary>
    /// <param name="intent">系统传入的 Intent。</param>
    private void HandleIncomingIntent(Intent? intent)
    {
        AndroidExternalOpenBridge.PublishFromIntent(this, intent);
    }
}
