using System;
using Android.App;
using Android.Content;
using Android.Views;
using Microsoft.ML.OnnxRuntime;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using ReactiveUI.Builder;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳启动路径：不创建 Avalonia 应用/窗口/AvaloniaView。
/// 顺序对齐 iOS <c>iOSNativeShell</c>：主线程调度 → ReactiveUI → 平台门面 →
/// 共享 <see cref="MainViewModel"/>（`nativePresentation: true`） → 根视图。
/// A1b 只给基座占位视图；A2/A3 再接入主图与主屏。
/// </summary>
public static class AndroidNativeShell
{
    private static bool _initialized;
    private static MainViewModel? _main;

    /// <summary>当前原生壳的共享主 VM；未启动时为 null。</summary>
    public static MainViewModel? Main => _main;

    /// <summary>
    /// 初始化原生壳并返回根视图；重复调用返回同一 VM 的新视图。
    /// </summary>
    /// <param name="activity">宿主 Activity（提供 ContentResolver/Toast 上下文）。</param>
    /// <param name="intent">启动 Intent（A2 起用于外部打开；A1b 只记录）。</param>
    /// <returns>可直接交给 <c>SetContentView</c> 的根视图。</returns>
    public static View CreateRootView(Activity activity, Intent? intent)
    {
        InitializePlatform(activity);

        if (_main == null)
        {
            _main = new MainViewModel(nativePresentation: true);
            Console.WriteLine("[Native] 共享 MainViewModel 就绪（nativePresentation=true）");
        }

        Console.WriteLine($"[Native] Android 原生壳就绪（Avalonia.Application.Current={(Avalonia.Application.Current == null ? "null" : "非 null")}）；intent action={intent?.Action}");
        return new AndroidNativeRootView(activity, _main);
    }

    /// <summary>
    /// 平台门面与 ONNX 执行提供程序注入；A1b 先用占位实现，A2/A3/A4 替换。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    private static void InitializePlatform(Activity activity)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var dispatcher = new AndroidUiDispatcher();
        dispatcher.InstallSynchronizationContext();
        UiDispatcher.Initialize(dispatcher);

        var reactiveBuilder = RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();

        FilePicker.Initialize(new AndroidFilePicker());
        AppResources.Initialize(new AndroidAppResourceProvider());
        using (var model = AppResources.OpenAsset(PhotoViewer.Core.AI.DinoModelResources.ModelAssetUri))
        {
            Console.WriteLine($"[Native] DINO 模型资源={(model != null ? "ok" : "missing")}");
        }
        HeifLoader.Initialize(new AndroidLibHeifDecoder());
        PerformanceBudget.Initialize(new AndroidPerformanceBudget());
        MemoryGovernor.Initialize(new AndroidMemoryBudgetProvider());
        XmpWriter.Initialize(new AndroidXmpWriter(activity.ContentResolver!));
        SettingsService.ConfigureStorage(new AndroidSettingsStorage());
        NativeWindowPresenter.Initialize(new AndroidNativeWindowPresenter(activity));
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_Nnapi());
        Console.WriteLine("[Native] 平台门面就绪（dispatcher/heif/budget/governor/xmp/settings/dino）");
    }
}
