using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
#if PHOTOVIEWER_DINO
using Microsoft.ML.OnnxRuntime;
#endif
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using ReactiveUI;
using ReactiveUI.Builder;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// UIKit 原生壳启动路径：不创建任何旧壳应用、窗口或主图适配位图。
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
    // 系统内存压力源:必须由静态字段持有,否则托管对象被回收后 dispatch source 即失效
    private static DispatchSource.MemoryPressure? _memoryPressureSource;

    /// <summary>当前原生壳的共享主 VM；未启动时为 null。</summary>
    public static MainViewModel? Main => _main;

    /// <summary>当前原生壳根控制器；未启动时为 null。</summary>
    public static UIViewController? RootViewController => _rootViewController;

    /// <summary>当前原生壳的展示加载会话（压测与验收共用同一份）。</summary>
    internal static PhotoLoadSession? Session => _session;

    /// <summary>本进程唯一的打开请求通道与桥接实例（应用委托创建，scene 与主屏共用）。</summary>
    private static OpenRequestChannel? _openRequests;
    private static iOSExternalOpenBridge? _openBridge;

    /// <summary>
    /// 与 scene、主屏共用的外部打开桥接；界面就绪前的系统打开请求先在通道内排队。
    /// </summary>
    internal static iOSExternalOpenBridge? OpenBridge => _openBridge;

    /// <summary>
    /// 接线原生平台门面并构建共享 VM 与根控制器；重复调用返回同一控制器。
    /// </summary>
    /// <param name="args">进程启动参数（压测与验收开关从这里解析）。</param>
    /// <param name="openRequests">应用委托持有的打开请求通道（与 scene 共用）。</param>
    /// <param name="openBridge">应用委托持有的外部打开桥接（与 scene 共用）。</param>
    /// <returns>可直接挂到 UIWindow 的根控制器。</returns>
    public static UIViewController CreateRootViewController(
        string[] args,
        OpenRequestChannel openRequests,
        iOSExternalOpenBridge openBridge)
    {
        if (_rootViewController != null)
        {
            return _rootViewController;
        }

        _openRequests = openRequests ?? throw new ArgumentNullException(nameof(openRequests));
        _openBridge = openBridge ?? throw new ArgumentNullException(nameof(openBridge));

        BootTrace.Mark("native shell: bootstrap begin");
        InitializePlatform();
        if (iOSNativeEditionCheck.IsRoundtripStage(args))
            PhotoDatabase.Initialize(iOSNativeEditionCheck.DatabasePath);
#if PHOTOVIEWER_DINO
        else if (iOSNativeBatchCheck.DatabasePath(args) is { } batchDatabase)
            PhotoDatabase.Initialize(batchDatabase);
#endif
        var main = InitializeMainViewModel(args);
        ApplyLaunchLayoutMode(args, main);
        ApplyLaunchAnalysisOpen(args, main);
        _session = new PhotoLoadSession();
        _rootViewController = new iOSNativeRootViewController(main, _session, _openBridge, IsAutomatedValidation(args));
        if (IsAutomatedValidation(args)) UIApplication.SharedApplication.IdleTimerDisabled = true;
        Console.WriteLine("[Native] UIKit 原生壳就绪");
        iOSNativePhotoCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeGestureCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeLayoutCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeFormCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeRatingControlCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeInputCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeAnalysisCheck.MaybeStart(args, _rootViewController, _session);
#if PHOTOVIEWER_DINO
        iOSNativeSimilarityCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeBatchCheck.MaybeStart(args);
#endif
        iOSNativeToolsCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeSheetShotCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeFaultCheck.MaybeStart(args, _rootViewController, _session);
        iOSNativeEditionCheck.MaybeStart(args, _rootViewController, _session);
#if PHOTOVIEWER_DINO
        iOSDinoCheck.MaybeStart(args);
#endif
        StartPerfProbe(args);
        StartDecodeOptimize(args);
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pv-heif-thumbnail-check", StringComparison.Ordinal)) continue;
            string directory = ResolveDocumentsPath(args[index + 1]);
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer", "heif-thumbnail-checks.txt");
            var decoder = new iOSHeifDecoder();
            _ = AppleHeifThumbnailChecks.RunAsync(directory, reportPath,
                (file, size) => Task.Run(() => iOSHeifDecoder.DecodeSubsampledWithImageIO(file.TryGetLocalPath(), size ?? 0)),
                decoder.DecodeAsync);
            break;
        }
        if (args.Contains("--pv-native-texture-cache-check", StringComparer.Ordinal))
        {
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                string report = AppleTextureCacheChecks.Run();
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "native-texture-cache-checks.txt"), report);
                Console.WriteLine($"[NativeSmoke] {(report.Contains("FAIL", StringComparison.Ordinal) ? "FAIL" : "PASS")} texture-cache");
            });
        }
        if (args.Contains("--pv-native-raw-scheduling-check", StringComparer.Ordinal))
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
            _ = AppleRawDecodeChecks.RunAsync(Path.Combine(directory, "native-raw-scheduling-checks.txt"));
        }
        return _rootViewController;
    }

    /// <summary>
    /// 验收专用（非产品行为）：<c>--pv-perf-probe &lt;目录&gt;</c> 对目录内图片做分段性能计时，
    /// 报告写 Documents/PhotoViewer/perf-probe.txt；相对路径按沙盒 Documents 解析。产品路径不调用。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    private static void StartPerfProbe(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pv-perf-probe", StringComparison.Ordinal)) continue;
            string directory = ResolveDocumentsPath(args[index + 1]);
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PhotoViewer", "perf-probe.txt");
            var budget = new iOSMemoryBudgetProvider();
            Console.WriteLine($"[PerfProbe] start {directory} -> {reportPath}");
            _ = ApplePerfProbe.RunAsync(directory, reportPath,
                () => (AppleMemoryFootprint.GetPhysFootprintBytes(), budget.GetAvailableBytes()));
        }
    }

    /// <summary>
    /// 验收专用（非产品行为）：<c>--pv-decode-optimize &lt;目录&gt;</c> 对照解码优化候选，
    /// 可选 <c>--pv-decode-full</c> 改为 JPEG/HEIF 真实全分辨率 8 位 P3；
    /// 报告写 Documents/PhotoViewer/decode-optimization.txt；相对路径按沙盒 Documents 解析。产品路径不调用。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    private static void StartDecodeOptimize(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pv-decode-optimize", StringComparison.Ordinal)) continue;
            string directory = ResolveDocumentsPath(args[index + 1]);
            bool fullResolution = args.Contains("--pv-decode-full", StringComparer.Ordinal);
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PhotoViewer", "decode-optimization.txt");
            Console.WriteLine($"[DecodeOptimize] start {directory} -> {reportPath}{(fullResolution ? " full=1" : "")}");
            _ = RunDecodeOptimizeAsync(directory, reportPath, fullResolution);
            return;
        }
    }

    /// <summary>执行解码优化探针并打印结论；异常只报告，不影响原生壳其它验收。</summary>
    /// <param name="directory">素材目录（绝对路径）。</param>
    /// <param name="reportPath">报告文本路径。</param>
    /// <param name="fullResolution">是否测 JPEG/HEIF 真实全分辨率 8 位 P3。</param>
    private static async Task RunDecodeOptimizeAsync(string directory, string reportPath, bool fullResolution)
    {
        try
        {
            bool passed = await AppleDecodeOptimizationProbe.RunAsync(directory, reportPath, fullResolution);
            Console.WriteLine($"[DecodeOptimize] {(passed ? "PASS" : "FAIL")} {reportPath}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[DecodeOptimize] FAIL " + exception);
        }
    }

    /// <summary>是否程序化验收会话：带 <c>--pv-*</c> 启动参数时不读写用户窗口偏好。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>是否为验收会话。</returns>
    private static bool IsAutomatedValidation(string[] args)
        => args.Any(argument => argument.StartsWith("--pv-", StringComparison.Ordinal));

    /// <summary>
    /// 生产走 Apple 偏好单键；验收会话走沙盒内的隔离文件，不读写用户真实设置。
    /// 发行版往返使用验收专用 Apple 偏好键，验证实际 NSUserDefaults 存储。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>设置仓库（尚未读取）。</returns>
    private static SettingsRepository CreateSettingsRepository(string[] args)
    {
        ISettingsStore store;
        if (iOSNativeEditionCheck.IsRoundtripStage(args))
        {
            store = new AppleSettingsStore(iOSNativeEditionCheck.SettingsKey);
        }
        else if (IsAutomatedValidation(args))
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer", "acceptance");
            Directory.CreateDirectory(directory);
            store = new FileSettingsStore(Path.Combine(directory, "settings.json"));
        }
        else
        {
            store = new AppleSettingsStore();
        }

        return new SettingsRepository(
            store,
            new iOSPerformanceBudget(),
            appleKeyboard: true,
            BitmapLoader.AutomaticCacheLimit);
    }

    /// <summary>
    /// 验收专用（非产品行为）：<c>--pv-layout-mode row|col|auto|phone</c> 在启动期固定布局模式，
    /// 供模拟器截图核对两向主屏与手机形态（iPadOS 不允许 <c>requestGeometryUpdate</c> 触发旋转，
    /// 见 Plans/native-rebuild-phase-ipad.md 发现清单）。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="main">共享主 VM。</param>
    private static void ApplyLaunchLayoutMode(string[] args, MainViewModel main)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pv-layout-mode", StringComparison.Ordinal)) continue;
            main.Settings.LayoutMode = args[index + 1] switch
            {
                "row" => LayoutMode.Row,
                "col" => LayoutMode.Col,
                "auto" => LayoutMode.Auto,
                "phone" => LayoutMode.Phone,
                _ => throw new InvalidOperationException($"--pv-layout-mode 只接受 row/col/auto/phone：{args[index + 1]}"),
            };
            Console.WriteLine($"[Native] --pv-layout-mode {args[index + 1]}");
        }
    }

    /// <summary>
    /// 验收专用（非产品行为）：<c>--pv-analysis-open</c> 启动即展开分析栏，
    /// 供真机压测在"分析栏可见（每张升 Full）"条件下采集内存与延迟；产品路径无此开关。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="main">共享主 VM。</param>
    private static void ApplyLaunchAnalysisOpen(string[] args, MainViewModel main)
    {
        if (!args.Contains("--pv-analysis-open", StringComparer.Ordinal)) return;
        main.IsAnalysisViewVisible = true;
        Console.WriteLine("[Native] --pv-analysis-open：分析栏启动即展开（验收会话）");
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
        BitmapPrefetcher.RawAlongsideLookahead = 4;
        PerformanceBudget.Initialize(new iOSPerformanceBudget());
        // 主屏形态能力：iOS 实现了手机形态，布局模式在设置页出现「手机」，自动模式按尺寸类别进入手机形态。
        LayoutCapabilities.PhoneLayoutSupported = true;
        // iPad 的缓存容量由实测预算推导,忽略用户缓存上限(自动口径在 BitmapLoader 内生效)
        BitmapLoader.AutomaticCacheLimit = true;
        MemoryGovernor.Initialize(new iOSMemoryBudgetProvider());
        _settingsPresenter = new iOSNativeSettingsPresenter();
        NativeSettingsPresenter.Initialize(_settingsPresenter);
        NativeWindowPresenter.Initialize(new iOSWindowPresenter(_settingsPresenter));
#if PHOTOVIEWER_DINO
        InstallOnnxRuntimeStaticResolver();
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_CoreML());
#endif
        BootTrace.Mark("native shell: platform facades ready");
    }

#if PHOTOVIEWER_DINO
    /// <summary>
    /// onnxruntime.xcframework 内是静态库，App 链接后没有独立动态镜像；托管侧 DllImport 名仍为
    /// "onnxruntime"。把该程序集的 DllImport 解析映射到主程序句柄（等价于 <c>__Internal</c>），
    /// 入口符号由 csproj 写入 <c>_ProcessedReferenceNativeSymbol</c>（即 mtouch-symbols.list）显式导出。
    /// </summary>
    private static void InstallOnnxRuntimeStaticResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, static (name, _, _) =>
            string.Equals(name, "onnxruntime", StringComparison.Ordinal)
                ? NativeLibrary.GetMainProgramHandle()
                : IntPtr.Zero);
    }
#endif

    /// <summary>
    /// 构建共享主 VM 并接线内存告警、设置落盘与压测驱动；
    /// 外部打开通道来自应用委托（与 scene/主屏共用），存储访问租约走 iOS 安全作用域会话。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>共享主 VM。</returns>
    private static MainViewModel InitializeMainViewModel(string[] args)
    {
        // 主 VM 构造前完成设置仓库读取：首帧即拿到持久化设置，避免默认值闪现后回填。
        var repository = CreateSettingsRepository(args);
        repository.InitializeAsync().GetAwaiter().GetResult();
        IViewStateStore viewState = iOSNativeEditionCheck.IsRoundtripStage(args)
            ? new AppleViewStateStore(iOSNativeEditionCheck.ViewStatePrefix)
            : IsAutomatedValidation(args) ? new InMemorySettingsStore() : new AppleViewStateStore();
        var main = new MainViewModel(
            repository,
            viewState,
            _openRequests ?? throw new InvalidOperationException("打开请求通道尚未接线"),
            new iOSStorageAccessSessionFactory());
        _main = main;
        // 桥接与主 VM 共用同一通道；协调器建立后即可等待本批处理完成（验收路径）。
        _openBridge?.Attach(main.OpenCoordinator);
        SubscribeMemoryWarning();
        SubscribeMemoryPressure();
        SubscribePersistence(main);

        // 压测参数（devicectl/simctl 注入 --pv-stress 系）；相对路径按沙盒 Documents 解析。
        // 激活期间禁止自动锁屏，避免压测中途被系统挂起；纹理字节采样只在这条路径注入，
        // 普通运行不额外读渲染器状态。
        if (StressFlipDriver.TryParseArgs(NSProcessInfo.ProcessInfo.Arguments, ResolveDocumentsPath))
        {
            StressFlipDriver.TextureBytesProvider = static () => _rootViewController?.Photo.TextureBytes ?? 0;
            StressFlipDriver.ExtraSummary = static () => FormattableString.Invariant(
                $"viewport={_rootViewController?.Photo.Bounds.Width}x{_rootViewController?.Photo.Bounds.Height} backing={_rootViewController?.Photo.TraitCollection.DisplayScale} proxyTarget={BitmapLoader.ProxyTargetLongEdge}");
            UIApplication.SharedApplication.IdleTimerDisabled = true;
        }

        // 验收专用入口（非产品行为）：--pv-open <路径> 直接投递进共享打开通道。
        PublishLaunchArgumentOpenPaths(args);
        BootTrace.Mark("native shell: main view model ready");
        return main;
    }

    /// <summary>
    /// 订阅系统内存压力源（DispatchSource）：Warn → Warning、Critical → Critical，
    /// 经 <see cref="MemoryGovernor.ReportOsEvent"/> 直报（只升不降，回 Normal 交给周期探测的迟滞退级）。
    /// 与 UIKit 的 DidReceiveMemoryWarning 互补：压力源在系统告警之前就能给出预警。
    /// 对象由静态字段持有，托管侧回收会让 dispatch source 失效。
    /// </summary>
    private static void SubscribeMemoryPressure()
    {
        try
        {
            var source = new DispatchSource.MemoryPressure(
                MemoryPressureFlags.Warn | MemoryPressureFlags.Critical,
                DispatchQueue.MainQueue);
            source.SetEventHandler(() =>
            {
                var flags = source.PressureFlags;
                var level = flags.HasFlag(MemoryPressureFlags.Critical) ? PressureLevel.Critical
                    : flags.HasFlag(MemoryPressureFlags.Warn) ? PressureLevel.Warning
                    : PressureLevel.Normal;
                if (level == PressureLevel.Normal) return;
                MemoryGovernor.ReportOsEvent(level);
            });
            source.Resume();
            _memoryPressureSource = source;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Native] 内存压力源订阅失败: {ex.Message}");
        }
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
    /// 退到后台时先按系统后台限制终止显式批量提取,再立即落盘设置;
    /// 退出前先落盘再结束主视图模型（与 Mac 的 Close/Flush/Dispose 顺序同口径）。
    /// 只听后台/前台切换:控制中心、弹窗与来电中断（WillResignActive）不算后台,不得终止提取。
    /// 回前台换新应用代际但不自动续跑提取,用户再次提取只补缺失的部分（SIM-03）。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    private static void SubscribePersistence(MainViewModel main)
    {
        void Flush() => main.SettingsRepository.FlushAsync().GetAwaiter().GetResult();
        UIApplication.Notifications.ObserveDidEnterBackground((_, _) =>
        {
            BatchAnalysisContext.SetApplicationActive(false);
            Flush();
        });
        UIApplication.Notifications.ObserveWillEnterForeground((_, _) =>
            BatchAnalysisContext.SetApplicationActive(true));
        UIApplication.Notifications.ObserveWillTerminate((_, _) =>
        {
            try
            {
                Flush();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("[Native] 退出保存设置失败：" + exception);
            }
            finally
            {
                DisposeMain();
            }
        });
    }

    /// <summary>
    /// 结束原生壳生命周期：释放根视图与展示会话，再结束共享主视图模型（外部打开消费者、
    /// 文件源会话与设置订阅）。全部释放幂等，重复调用安全；设置只由 <see cref="MainViewModel.Dispose"/> 释放一次。
    /// </summary>
    internal static void DisposeMain()
    {
        var root = _rootViewController;
        _rootViewController = null;
        var main = _main;
        _main = null;
        var session = _session;
        _session = null;
        root?.Dispose();
        main?.Dispose();
        session?.Dispose();
        _openBridge = null;
        _openRequests = null;
    }

    /// <summary>
    /// 验收专用（非产品行为）：解析 --pv-open 启动参数并把目标路径按原生引用投递进共享打开通道，
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
            _openBridge?.Publish(url, OpenRequestSource.Automation);
        }
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);
}
