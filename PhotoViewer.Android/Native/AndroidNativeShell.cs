using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
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
    private static PhotoLoadSession? _session;
    private static AndroidNativeRootView? _root;
    private static AndroidNativeSettingsPresenter? _settingsPresenter;
    private static AndroidToolsPresenter? _toolsPresenter;
    private static bool _externalHandlerRegistered;

    /// <summary>当前原生壳的共享主 VM；未启动时为 null。</summary>
    public static MainViewModel? Main => _main;

    /// <summary>当前原生壳的展示加载会话。</summary>
    public static PhotoLoadSession? Session => _session;

    /// <summary>
    /// 硬件键盘派发：设置 sheet/文本输入期间不抢键；命中用户绑定且命令可用时执行并消费。
    /// </summary>
    /// <param name="keyCode">原生键码。</param>
    /// <param name="metaState">原生修饰键状态。</param>
    /// <returns>是否消费该按键。</returns>
    public static bool HandleHardwareKey(Keycode keyCode, int metaState)
    {
        if (_main == null || _root == null) return false;
        if (AndroidNativeSettingsPresenter.IsOpen) return false;
        var activity = _root.Context as Activity;
        if (activity?.CurrentFocus is global::Android.Widget.EditText) return false;
        var gesture = AndroidInputMapping.FromKeyEvent(keyCode, metaState);
        string? command = InputCommandMap.CommandForGesture(gesture, _main.Settings);
        if (command == null || !AndroidControlBar.IsCommandAvailable(command)) return false;
        Console.WriteLine($"[Native] 键盘命令：{keyCode} → {command}");
        _root.Execute(command);
        return true;
    }

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
        _session ??= new PhotoLoadSession();
        _toolsPresenter = new AndroidToolsPresenter(activity, _main);
        NativeWindowPresenter.Initialize(new AndroidNativeWindowPresenter(activity, _settingsPresenter!, _toolsPresenter));
        RegisterExternalOpen(_main);

        Console.WriteLine($"[Native] Android 原生壳就绪（Avalonia.Application.Current={(Avalonia.Application.Current == null ? "null" : "非 null")}）；intent action={intent?.Action}");
        _root?.Dispose();
        _root = new AndroidNativeRootView(activity, _main, _session);
        HandleIntent(intent);
        AndroidNativeChecks.MaybeStart(activity, _main, _session, _root, intent?.GetStringExtra("pv-check"));
        return _root;
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
        AndroidDisplayCapabilities.Detect(activity);
        var dispatcher = new AndroidUiDispatcher();
        dispatcher.InstallSynchronizationContext();
        UiDispatcher.Initialize(dispatcher);

        var reactiveBuilder = RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();

        FilePicker.Initialize(new AndroidFilePicker(activity));
        AppResources.Initialize(new AndroidAppResourceProvider());
        using (var model = AppResources.OpenAsset(PhotoViewer.Core.AI.DinoModelResources.ModelAssetUri))
        {
            Console.WriteLine($"[Native] DINO 模型资源={(model != null ? "ok" : "missing")}");
        }
        HeifLoader.Initialize(new AndroidLibHeifDecoder());
        BitmapLoader.InitializePresentationDecoder(new AndroidPresentationDecoder(), new AndroidPresentationEncodingPolicy());
        // Android 手机堆更紧：邻居预取统一走代理档（Full 只在当前图放大时按需解码），避免 3+3 张 24MP Full 同时驻留。
        BitmapLoader.PrefetchFullNeighbors = false;
        PerformanceBudget.Initialize(new AndroidPerformanceBudget());
        MemoryGovernor.Initialize(new AndroidMemoryBudgetProvider());
        StorageAccessManager.Initialize(new AndroidStorageAccessManager());
        XmpWriter.Initialize(new AndroidXmpWriter(activity.ContentResolver!));
        SettingsService.ConfigureStorage(new AndroidSettingsStorage());
        _settingsPresenter = new AndroidNativeSettingsPresenter(activity);
        NativeSettingsPresenter.Initialize(_settingsPresenter);
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_Nnapi());
        Console.WriteLine("[Native] 平台门面就绪（dispatcher/heif/decode/budget/governor/storage/xmp/settings/dino）");
    }

    /// <summary>
    /// 外部打开统一投递：文件夹走共享 FolderVM，文件列表走外部文件打开；未就绪时共享服务自动排队。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    private static void RegisterExternalOpen(MainViewModel main)
    {
        if (_externalHandlerRegistered) return;
        _externalHandlerRegistered = true;
        ExternalOpenService.RegisterHandler(async request =>
        {
            try
            {
                var folder = request.Items.Select(item => item.StorageItem).OfType<IFolderRef>().FirstOrDefault();
                if (folder != null)
                {
                    await main.FolderVM.OpenFolderAsync(folder);
                    return;
                }
                var files = request.Items.Select(item => item.StorageItem).OfType<IFileRef>().ToList();
                if (files.Count > 0) await main.FolderVM.OpenExternalFilesAsync(files);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Native] 外部打开失败：{exception}");
            }
        });
    }

    /// <summary>
    /// 处理启动/新 Intent：<c>pv-open</c> 验收路径优先，其次 ACTION_VIEW/SEND 的 content Uri。
    /// </summary>
    /// <param name="intent">系统 Intent。</param>
    public static void HandleIntent(Intent? intent)
    {
        if (intent == null || _main == null) return;
        var items = new List<PhotoViewer.Core.Storage.IStorageItemRef>();
        string? openPath = ResolveLaunchPath(intent.GetStringExtra("pv-open"));
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            try
            {
                items.Add(System.IO.Directory.Exists(openPath)
                    ? new AndroidFolderRef(openPath)
                    : new AndroidFileRef(openPath));
                Console.WriteLine($"[Native] pv-open：{openPath}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Native] pv-open 失败：{exception.Message}");
            }
        }
        if (intent.Action is Intent.ActionView or Intent.ActionSend or Intent.ActionSendMultiple)
        {
            foreach (var uri in CollectIntentUris(intent))
            {
                if (uri == null) continue;
                try
                {
                    items.Add(new AndroidFileRef(Application.Context.ContentResolver!, uri, null,
                        uri.LastPathSegment, null, null));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Native] 外部打开 Uri 解析失败：{exception.Message}");
                }
            }
        }
        foreach (var item in items) ExternalOpenService.PublishStorageItem(item, "android-intent");
        if (intent.GetBooleanExtra("pv-settings", false))
        {
            Console.WriteLine("[Native] pv-settings：启动后打开设置 sheet（验收）");
            UiDispatcher.Post(() => _main.OpenSettingWindow());
        }
    }

    /// <summary>
    /// 验收路径占位符：<c>@external/…</c> 解析到 App 外部文件目录，<c>@files/…</c> 解析到内部文件目录；
    /// 其他情况按原路径处理。模拟器推送夹具只写入 App 自有目录，不触碰用户照片。
    /// </summary>
    /// <param name="path">原始路径或占位符。</param>
    /// <returns>可访问的绝对路径。</returns>
    private static string? ResolveLaunchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        const string externalPrefix = "@external/";
        const string filesPrefix = "@files/";
        try
        {
            if (path.StartsWith(externalPrefix, StringComparison.Ordinal))
            {
                var root = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
                if (root == null) return null;
                return System.IO.Path.Combine(root, path[externalPrefix.Length..]);
            }
            if (path.StartsWith(filesPrefix, StringComparison.Ordinal))
            {
                var root = Application.Context.FilesDir?.AbsolutePath;
                if (root == null) return null;
                return System.IO.Path.Combine(root, path[filesPrefix.Length..]);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 启动路径解析失败：{exception.Message}");
            return null;
        }
        return path;
    }

    /// <summary>收集 ACTION_VIEW/SEND/SEND_MULTIPLE 中的文件 Uri。</summary>
    /// <param name="intent">系统 Intent。</param>
    /// <returns>去重后的 Uri。</returns>
    private static IEnumerable<global::Android.Net.Uri> CollectIntentUris(Intent intent)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (intent.Data is { } data && seen.Add(data.ToString()!)) yield return data;
        if (intent.ClipData is { } clip)
        {
            for (int index = 0; index < clip.ItemCount; index++)
            {
                var uri = clip.GetItemAt(index)?.Uri;
                if (uri != null && seen.Add(uri.ToString()!)) yield return uri;
            }
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            if (intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri))) is global::Android.Net.Uri single
                && seen.Add(single.ToString()!)) yield return single;
            if (intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri))) is { } many)
            {
                foreach (var item in many)
                    if (item is global::Android.Net.Uri uri && seen.Add(uri.ToString()!)) yield return uri;
            }
        }
    }
}
