using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using ReactiveUI;
using ReactiveUI.Builder;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// UIKit 原生壳启动路径：不创建任何 Avalonia 应用、窗口或主图适配位图。
/// 启动顺序与 <c>PhotoViewer.Mac/Native/MacNativeApplication.cs</c> 一致：
/// 主线程调度器 → ReactiveUI builder → 平台门面 → 共享 <see cref="MainViewModel"/> → 根控制器。
/// </summary>
public static class iOSNativeShell
{
    private static bool _initialized;
    private static MainViewModel? _main;
    private static iOSNativeSettingsPresenter? _settingsPresenter;
    private static iOSNativeRootViewController? _rootViewController;
    private static PhotoLoadSession? _session;

    /// <summary>当前原生壳的共享主 VM；未启动时为 null。</summary>
    public static MainViewModel? Main => _main;

    /// <summary>当前原生壳根控制器；未启动时为 null。</summary>
    public static UIViewController? RootViewController => _rootViewController;

    /// <summary>当前原生壳的展示加载会话（压测与验收共用同一份）。</summary>
    internal static PhotoLoadSession? Session => _session;

    /// <summary>
    /// 接线原生平台门面并构建共享 VM 与根控制器；重复调用返回同一控制器。
    /// </summary>
    /// <param name="args">进程启动参数（压测与验收开关从这里解析）。</param>
    /// <returns>可直接挂到 UIWindow 的根控制器。</returns>
    public static UIViewController CreateRootViewController(string[] args)
    {
        if (_rootViewController != null)
        {
            return _rootViewController;
        }

        BootTrace.Mark("native shell: bootstrap begin");
        BootTrace.MarkAvaloniaAssemblies("native shell: bootstrap begin");
        InitializePlatform();
        var main = InitializeMainViewModel(args);
        _session = new PhotoLoadSession();
        _rootViewController = new iOSNativeRootViewController(main, _session);
        BootTrace.MarkAvaloniaAssemblies("native shell: root view controller ready");
        Console.WriteLine("[Native] UIKit 原生壳就绪（不创建 Avalonia 对象）");
        // 压测与验收都走原生展示会话（PhotoLoadSession）的上屏信号，不经过旧壳 ImageViewModel。
        StressFlipDriver.MaybeStart(main, _session);
        iOSNativePhotoCheck.MaybeStart(args, _rootViewController, _session);
        return _rootViewController;
    }

    /// <summary>
    /// 平台门面、调度器与 ONNX 执行提供程序注入；顺序与 macOS 原生壳保持一致。
    /// </summary>
    private static void InitializePlatform()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var dispatcher = new iOSUiDispatcher();
        dispatcher.InstallSynchronizationContext();
        UiDispatcher.Initialize(dispatcher);
        var reactiveBuilder = RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();
        FilePicker.Initialize(new iOSFilePicker());
        AppResources.Initialize(new iOSAppResources());
        HeifLoader.Initialize(new iOSHeifDecoder());
        RawLoader.Initialize(new iOSRawDecoder());
        BitmapLoader.InitializePresentationDecoder(new ApplePresentationDecoder(), new iOSPresentationEncodingPolicy());
        PerformanceBudget.Initialize(new iOSPerformanceBudget());
        MemoryGovernor.Initialize(new iOSMemoryBudgetProvider());
        StorageAccessManager.Initialize(new iOSStorageAccessManager());
        XmpWriter.Initialize(new iOSXmpWriter());
        _settingsPresenter = new iOSNativeSettingsPresenter();
        NativeSettingsPresenter.Initialize(_settingsPresenter);
        NativeWindowPresenter.Initialize(new iOSWindowPresenter(_settingsPresenter));
        SettingsService.ConfigureStorage(new iOSSettingsStorage());
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_CoreML());
        BootTrace.Mark("native shell: platform facades ready");
    }

    /// <summary>
    /// 构建共享主 VM 并接线外部打开、内存告警、设置落盘与压测驱动。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>共享主 VM。</returns>
    private static MainViewModel InitializeMainViewModel(string[] args)
    {
        var main = new MainViewModel(nativePresentation: true);
        _main = main;
        ExternalOpenService.RegisterHandler(request => HandleExternalOpenAsync(main, request));
        SubscribeMemoryWarning();
        SubscribePersistence(main);

        // P1-M5 压测参数（devicectl/simctl 注入 --pv-stress 系）；相对路径按沙盒 Documents 解析。
        // 激活期间禁止自动锁屏，避免压测中途被系统挂起。
        if (StressFlipDriver.TryParseArgs(NSProcessInfo.ProcessInfo.Arguments, ResolveDocumentsPath))
        {
            UIApplication.SharedApplication.IdleTimerDisabled = true;
        }

        // 验收专用入口（非产品行为）：--pv-open <路径> 直接投递进共享外部打开链路。
        PublishLaunchArgumentOpenPaths(args);
        BootTrace.Mark("native shell: main view model ready");
        return main;
    }

    /// <summary>
    /// 监听系统内存告警：直报统一内存管家（抬级 + 客户端修剪级联由 Governor 驱动），
    /// 并记录告警时的缓存规模供设置页展示（与旧壳同一口径）。
    /// </summary>
    private static void SubscribeMemoryWarning()
    {
        UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, _) =>
        {
            MemoryGovernor.ReportOsEvent(PressureLevel.Warning);
            iOSPerformanceBudget.RecordMemoryWarningCacheMB(BitmapLoader.CurrentCacheSize);
            MessageBus.Current.SendMessage(new BitmapLoader.MemoryWarningEvent(
                sizeMB: BitmapLoader.CurrentCacheSize / (1024 * 1024),
                count: BitmapLoader.CurrentCacheCount,
                time: DateTimeOffset.Now));
        });
    }

    /// <summary>
    /// 退到后台或退出前立即落盘设置（与 Mac 退出前的显式保存同因：常规保存有节流，可能来不及执行）。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    private static void SubscribePersistence(MainViewModel main)
    {
        void Save() => SettingsService.Instance.SaveAsync(main.Settings.CreateSnapshot()).GetAwaiter().GetResult();
        UIApplication.Notifications.ObserveDidEnterBackground((_, _) => Save());
        UIApplication.Notifications.ObserveWillTerminate((_, _) => Save());
    }

    /// <summary>
    /// 共享外部打开请求的 iOS 原生处理：使用原生存储引用直接进入共享文件源，不经过 Avalonia 反射或 StorageProvider。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="request">外部打开请求。</param>
    /// <returns>处理完成的异步任务。</returns>
    private static async Task HandleExternalOpenAsync(MainViewModel main, ExternalOpenRequest request)
    {
        foreach (var item in request.Items)
        {
            if (item.StorageItem is not { } storageItem)
            {
                Console.WriteLine($"[Native] 外部打开项缺少原生引用，已忽略：{item.Path}（{request.Source}）");
                continue;
            }

            if (storageItem is IFolderRef folder)
            {
                await UiDispatcher.InvokeAsync(() => main.FolderVM.OpenFolderAsync(folder)).Unwrap();
                return;
            }

            if (storageItem is IFileRef file && main.FolderVM.IsImageFile(file.Name))
            {
                await UiDispatcher.InvokeAsync(() => main.FolderVM.OpenImageAsync(file)).Unwrap();
                return;
            }
        }

        Console.WriteLine($"[Native] 外部打开未命中可识别图片：{request.Source}");
    }

    /// <summary>
    /// 验收专用（非产品行为）：解析 --pv-open 启动参数并把目标路径按原生引用投递进共享外部打开链路，
    /// 用于无 Files App 交互的自动化验证；路径可为绝对路径或相对沙盒 Documents。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    private static void PublishLaunchArgumentOpenPaths(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pv-open", StringComparison.Ordinal))
            {
                continue;
            }

            var path = ResolveDocumentsPath(args[index + 1]);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Console.WriteLine($"[Native] --pv-open 目标不存在：{path}");
                continue;
            }

            var url = NSUrl.CreateFileUrl(path, Directory.Exists(path), null);
            BootTrace.Mark($"native shell: --pv-open {path}");
            iOSExternalOpenBridge.PublishNativeFromUrl(url, "iOS:LaunchArgument");
        }
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);
}
