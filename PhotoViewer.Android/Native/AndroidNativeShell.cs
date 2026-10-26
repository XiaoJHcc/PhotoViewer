using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Microsoft.ML.OnnxRuntime;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using ReactiveUI.Builder;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳启动路径：直接构建原生根视图。
/// 顺序对齐 iOS <c>iOSNativeShell</c>：主线程调度 → ReactiveUI → 平台门面 →
/// 共享 <see cref="MainViewModel"/> → 根视图。
/// </summary>
public static class AndroidNativeShell
{
    private static bool _initialized;
    private static MainViewModel? _main;
    private static OpenRequestChannel? _openRequests;
    private static PhotoLoadSession? _session;
    private static AndroidNativeRootView? _root;
    private static AndroidNativeSettingsPresenter? _settingsPresenter;
    private static AndroidToolsPresenter? _toolsPresenter;
    private static IPresentationImageDecoder? _presentationDecoder;
    private static AndroidFilePicker? _filePicker;

    /// <summary>当前 SAF 选择器实例（Activity 转发结果与销毁取消用）。</summary>
    internal static AndroidFilePicker? FilePickerForResults => _filePicker;

    /// <summary>当前原生壳的共享主 VM；未启动时为 null。</summary>
    public static MainViewModel? Main => _main;

    /// <summary>关闭主图长按菜单；返回键优先消费。</summary>
    /// <returns>关闭了菜单返回 true。</returns>
    public static bool DismissPhotoMenu() => _root?.DismissPhotoMenu() == true;

    /// <summary>Activity 恢复（SAF 选择器返回）后强制刷新栏位与主图。</summary>
    public static void RefreshAfterResume() => _root?.RefreshAfterResume();

    /// <summary>关闭筛选下拉；返回键优先消费。</summary>
    /// <returns>关闭了返回 true。</returns>
    public static bool DismissFilterDropdown() => _root?.DismissFilterDropdown() == true;

    /// <summary>当前原生壳的展示加载会话。</summary>
    public static PhotoLoadSession? Session => _session;

    /// <summary>
    /// 硬件键盘派发：设置 sheet/文本输入期间不抢键；命中用户绑定且命令登记在共享设置目录时执行并消费。
    /// 系统重复只对共享命令表标记为可重复的命令执行（INPUT-05）；重复标记必须来自真实的
    /// <see cref="KeyEventActions.Down"/> + <see cref="KeyEvent.RepeatCount"/>。
    /// </summary>
    /// <param name="keyCode">原生键码。</param>
    /// <param name="metaState">原生修饰键状态。</param>
    /// <param name="repeated">是否为按住产生的系统重复事件。</param>
    /// <returns>是否消费该按键。</returns>
    public static bool HandleHardwareKey(Keycode keyCode, int metaState, bool repeated = false)
    {
        if (_main == null || _root == null) return false;
        if (AndroidNativeSettingsPresenter.IsOpen) return false;
        var activity = _root.Context as Activity;
        if (activity?.CurrentFocus is global::Android.Widget.EditText) return false;
        var gesture = AndroidInputMapping.FromKeyEvent(keyCode, metaState);
        string? command = InputCommandMap.CommandForGesture(gesture, _main.Settings);
        if (command == null || !InputCommandMap.IsCommandKnown(command)) return false;
        // INPUT-05：不可重复的命令按住只执行一次；已消费的重复事件不再交回系统。
        if (repeated && !InputCommandMap.IsRepeatable(command)) return true;
        Console.WriteLine($"[Native] 键盘命令：{keyCode} → {command}{(repeated ? "（重复）" : "")}");
        _root.Execute(command);
        return true;
    }

    /// <summary>
    /// 初始化原生壳并返回根视图；重复调用返回同一 VM 的新视图。
    /// </summary>
    /// <param name="activity">宿主 Activity（提供 ContentResolver/Toast 上下文）。</param>
    /// <param name="intent">启动 Intent（外部打开、验收参数与诊断开关）。</param>
    /// <returns>可直接交给 <c>SetContentView</c> 的根视图。</returns>
    public static View CreateRootView(Activity activity, Intent? intent)
    {
        // 真机 EGL P3 表面在当前合成路径下会把主图整体抬亮（缩略图正常）；默认走 sRGB 保正确，
        // 广色域留 --ez pv-p3 true 诊断，待 EGL/合成传递函数确认后再默认开启。
        AndroidDisplayCapabilities.ForceSrgb = intent?.GetBooleanExtra("pv-p3", false) != true;
        // 压测独占一份隔离设置：否则一轮压测会把用户设置写脏，下一轮也无法从默认值起跑。
        if (!string.IsNullOrWhiteSpace(intent?.GetStringExtra("pv-stress")))
            AndroidSettingsStorage.UseIsolatedProfile("stress");
        InitializePlatform(activity);

        // 内存上限探针（验收）：只读系统退出原因，或接管界面做逐步分配；都不建主界面。
        if (AndroidMemoryCeilingProbe.MaybeReportExitReasons(activity, intent))
        {
            ExitShell(activity, "退出原因报告完成");
            return new View(activity);
        }
        if (AndroidMemoryCeilingProbe.MaybeStart(activity, intent) is { } ceilingView)
            return ceilingView;

        // 性能探针独占一轮：不建共享 VM 与主界面，测完写报告并结束进程（与 Windows/Mac 同流程）。
        var perfProbeFolder = ResolveLaunchPath(intent?.GetStringExtra("pv-perf-probe"));
        if (!string.IsNullOrWhiteSpace(perfProbeFolder))
            return RunPerfProbe(activity, perfProbeFolder, intent?.GetIntExtra("pv-perf-probe-target", 0) ?? 0);

        if (_main == null)
        {
            var settingsRepository = CreateSettingsRepository();
            // 打开请求通道先于 VM 建立：外部打开在界面就绪前即可投递，由 OpenCoordinator 顺序消费。
            _openRequests ??= new OpenRequestChannel();
            // 存储访问租约走 Android SAF 实现（校验/补取持久授权），不再用默认 Local（对 content:// 不校验）。
            var storageAccess = new AndroidStorageAccessSessionFactory(
                new AndroidStorageAccessManager(activity.ContentResolver ?? Application.Context.ContentResolver!));
            _main = new MainViewModel(settingsRepository, new AndroidViewStateStore(), _openRequests, storageAccess);
            Console.WriteLine("[Native] 共享 MainViewModel 就绪");
        }
        _session ??= new PhotoLoadSession();
        _toolsPresenter = new AndroidToolsPresenter(activity, _main);
        NativeWindowPresenter.Initialize(new AndroidNativeWindowPresenter(activity, _settingsPresenter!, _toolsPresenter));

        Console.WriteLine($"[Native] Android 原生壳就绪；intent action={intent?.Action}");
        _root?.Dispose();
        _root = new AndroidNativeRootView(activity, _main, _session);
        HandleIntent(intent);
        AndroidNativeChecks.MaybeStart(activity, _main, _session, _root, intent?.GetStringExtra("pv-check"));
        if (intent != null) MaybeStartStress(activity, intent, _main, _session);
        return _root;
    }

    /// <summary>
    /// 压测入口：Intent extras（<c>pv-stress</c> / <c>pv-stress-count</c> / <c>pv-stress-mode</c>）
    /// 映射到共享 <see cref="StressFlipDriver"/> 参数；日志与 pv-check 报告同目录（内部 <c>files/PhotoViewer/</c>），
    /// 汇总行补上传统计、<c>/proc/self/status</c> 内存读数与 GC 次数。素材只读，不写回照片文件夹。
    /// </summary>
    /// <param name="activity">宿主 Activity（收尾用）。</param>
    /// <param name="intent">启动 Intent。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    private static void MaybeStartStress(Activity activity, Intent intent, MainViewModel main, PhotoLoadSession session)
    {
        var folder = intent.GetStringExtra("pv-stress");
        if (string.IsNullOrWhiteSpace(folder)) return;

        var args = new List<string> { "--pv-stress=" + folder };
        if (intent.HasExtra("pv-stress-count"))
            args.Add("--pv-stress-count=" + intent.GetIntExtra("pv-stress-count", 0));
        var mode = intent.GetStringExtra("pv-stress-mode");
        if (!string.IsNullOrWhiteSpace(mode)) args.Add("--pv-stress-mode=" + mode);
        if (!StressFlipDriver.TryParseArgs(args, path => ResolveLaunchPath(path) ?? path)) return;

        var reportDirectory = Path.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer");
        Directory.CreateDirectory(reportDirectory);
        StressFlipDriver.LogDirectory = reportDirectory;
        AndroidUploadStats.Reset();
        // 压测期间不跑后台特征索引：索引会解 560 缩略图并抢占解码与 CPU，把翻页延迟混成"索引延迟"。
        main.Settings.SimilarityPanelExpanded = false;
        var gc0AtStart = GC.CollectionCount(0);
        var gc2AtStart = GC.CollectionCount(2);
        StressFlipDriver.ExtraSummary = () => AndroidUploadStats.Summary()
            + " " + AndroidProcessMemory.Summary()
            + " gc0Delta=" + (GC.CollectionCount(0) - gc0AtStart)
            + " gc2Delta=" + (GC.CollectionCount(2) - gc2AtStart)
            + " managedMB=" + (GC.GetTotalMemory(false) >> 20);
        // 自动化压测：一轮翻完就结束进程，否则 Activity/隔离库留在前台，下一轮无法从干净进程起跑。
        StressFlipDriver.Completed = () => ExitShell(activity, "压测完成");
        Console.WriteLine($"[Native] 压测启动：{StressFlipDriver.PendingFolderPath} flips={StressFlipDriver.MaxFlips}"
            + $" mode={(StressFlipDriver.AbMode ? "ab" : "sequential")}");
        StressFlipDriver.MaybeStart(main, session);
    }

    /// <summary>
    /// 性能探针：目标长边取屏幕物理长边 × 2（无主图视口可比），跑完写 <c>files/PhotoViewer/perf-probe.txt</c>
    /// 并结束进程；返回一个占位视图交给 <c>SetContentView</c>，不构建用户界面。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="folder">素材目录（只读）。</param>
    /// <returns>占位视图。</returns>
    private static View RunPerfProbe(Activity activity, string folder, int targetOverride)
    {
        var reportDirectory = Path.Combine(activity.FilesDir!.AbsolutePath, "PhotoViewer");
        var reportPath = Path.Combine(reportDirectory, "perf-probe.txt");
        var metrics = activity.Resources?.DisplayMetrics;
        var displayLongEdge = metrics == null ? 1080 : Math.Max(metrics.WidthPixels, metrics.HeightPixels);
        // 默认视口长边 ×2；验收可用 --ei pv-perf-probe-target 指定别的代理目标
        var proxyLongEdge = targetOverride > 0 ? targetOverride : Math.Max(displayLongEdge * 2, 1);
        var decoder = _presentationDecoder ?? new AndroidPresentationDecoder();
        Console.WriteLine($"[PerfProbe] start {folder} -> {reportPath} T={proxyLongEdge}"
            + $" display={metrics?.WidthPixels}x{metrics?.HeightPixels}");
        AndroidPerfProbe.RunAsync(folder, reportPath, decoder, proxyLongEdge).ContinueWith(task =>
        {
            if (task.IsFaulted)
                Console.Error.WriteLine($"[PerfProbe] 失败：{task.Exception?.GetBaseException()}");
            else
                Console.WriteLine($"[PerfProbe] done {reportPath}");
            ExitShell(activity, "性能探针完成");
        }, TaskScheduler.Default);
        return new View(activity);
    }

    /// <summary>自动化收尾：关闭 Activity 后结束进程，下一轮从干净进程开始（对齐 Windows 的 Environment.Exit）。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="reason">日志用原因。</param>
    internal static void ExitShell(Activity activity, string reason)
    {
        Console.WriteLine($"[Native] {reason}，结束进程");
        var handler = new Handler(Looper.MainLooper!);
        handler.Post(() =>
        {
            try { activity.FinishAndRemoveTask(); }
            catch (Exception exception) { Console.Error.WriteLine($"[Native] Activity 结束失败：{exception.Message}"); }
            handler.PostDelayed(() => global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()), 200);
        });
    }

    /// <summary>
    /// 主窗销毁（Activity 真正退出，非配置重建）：释放根视图、共享 VM、请求通道与展示会话。
    /// 全部释放路径都幂等，重复调用安全；配置重建时保留 VM，避免丢当前文件夹。
    /// </summary>
    internal static void DisposeMain()
    {
        var root = _root;
        _root = null;
        var main = _main;
        _main = null;
        _openRequests = null;
        root?.Dispose();
        main?.Dispose();
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    /// <summary>
    /// 建立共享设置仓库并在主界面之前完成一次读取：后续 ViewModel、缓存与预算直接消费
    /// <see cref="SettingsRepository.Current"/>。读取本身在后台线程进行，这里只同步等待结果
    /// （SharedPreferences 读取不依赖 UI 线程，不会死锁）。
    /// </summary>
    /// <returns>已完成初始化的设置仓库。</returns>
    private static SettingsRepository CreateSettingsRepository()
    {
        var repository = new SettingsRepository(
            new AndroidSettingsStorage(),
            new AndroidPerformanceBudget(),
            appleKeyboard: false,
            automaticCacheLimit: true);
        try
        {
            repository.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 设置仓库初始化失败：{exception}");
        }

        Console.WriteLine($"[Native] 设置仓库就绪：{repository.LoadOutcome} canCommit={repository.CanCommit}"
            + (repository.LastLoadError is { } error ? $" error={error}" : string.Empty));
        return repository;
    }

    /// <summary>
    /// 平台门面与 ONNX 执行提供程序注入。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    private static void InitializePlatform(Activity activity)
    {
        if (_initialized)
        {
            // Activity 重建（字体/语言等）后选择器要指向新实例；旧请求按取消收口。
            _filePicker?.Rebind(activity);
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

        _filePicker = new AndroidFilePicker(activity);
        FilePicker.Initialize(_filePicker);
        AppResources.Initialize(new AndroidAppResourceProvider());
        using (var model = AppResources.OpenAsset(PhotoViewer.Core.AI.DinoModelResources.ModelAssetUri))
        {
            Console.WriteLine($"[Native] DINO 模型资源={(model != null ? "ok" : "missing")}");
        }
        ConfigureHeifFastDecoder();
        HeifLoader.Initialize(new AndroidLibHeifDecoder());
        _presentationDecoder = new AndroidPresentationDecoder();
        BitmapLoader.InitializePresentationDecoder(_presentationDecoder, new AndroidPresentationEncodingPolicy());
        // 邻居预取档位跟随实际需求：适应态只取代理；放大或分析栏可见时 ±1 邻居取 Full（有准入与预算兜底）。
        BitmapLoader.PrefetchFullNeighbors = true;
        PerformanceBudget.Initialize(new AndroidPerformanceBudget());
        MemoryGovernor.Initialize(new AndroidMemoryBudgetProvider());
        // Android 14+ 前台不再收到 RUNNING_* trim 回调，缓存容量改由实测预算推导（忽略用户两项上限）。
        BitmapLoader.AutomaticCacheLimit = true;
        // 空闲像素缓冲按预算的 1/8 封顶（模拟器 ~1.1GB 预算 → ~140MB），大图缓冲不长期占着不还。
        long governorBudget = MemoryGovernor.BudgetBytes;
        ImageBufferPool.MaxIdleBytes = governorBudget > 0
            ? Math.Min(512L * 1024 * 1024, governorBudget / 8)
            : 512L * 1024 * 1024;
        AndroidInputDevices.Initialize(activity);
        _settingsPresenter = new AndroidNativeSettingsPresenter(activity);
        NativeSettingsPresenter.Initialize(_settingsPresenter);
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_Nnapi());
        Console.WriteLine("[Native] 平台门面就绪（dispatcher/heif/decode/budget/governor/storage/settings/dino）");
    }

    /// <summary>
    /// 配置共享 HEIF 快速解码器：原生库取 APK 的 <c>libpvhevc.so</c>，并发与空闲上下文按移动端收敛。
    /// </summary>
    /// <remarks>
    /// 空闲上下文按最近一帧的 YUV 平面占内存，手机上只留两个；同时在转换的图像数默认 2，
    /// 预算不足 3 GB 的设备降到 1，避免两幅全画幅的 YUV 平面同时驻留。
    /// 原生库缺失时加载器返回 <see cref="IntPtr.Zero"/>，解码器退回默认探测并按不可用处理，
    /// HEIF 继续走系统 <c>BitmapFactory</c> 与 libheif。
    /// </remarks>
    private static void ConfigureHeifFastDecoder()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = NativeLibrary.Load("libpvhevc.so");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] pvhevc 加载失败（{exception.GetType().Name}）：{exception.Message}");
        }

        long budget = new AndroidMemoryBudgetProvider().GetBudgetBytes();
        int maxImages = budget < 3L * 1024 * 1024 * 1024 ? 1 : 2;
        int contexts = System.Environment.ProcessorCount;
        FastHeifDecoder.Configure(new FastHeifOptions
        {
            LoadLibrary = () => handle,
            MaxConcurrentContexts = contexts,
            MaxCurrentContextsExtra = 2,
            MaxConcurrentImages = maxImages,
            MaxIdleContexts = 2,
        });
        Console.WriteLine($"[Native] HEIF 快速解码 pvhevc={(handle != IntPtr.Zero ? "ok" : "不可用")}"
            + $" contexts={contexts}+2 images={maxImages} budget={budget >> 20}MB");
    }

    /// <summary>
    /// 处理启动/新 Intent：<c>pv-open</c> 验收路径、ACTION_VIEW 与 ACTION_SEND(_MULTIPLE) 的 Uri
    /// 合并为一批投递到共享 <see cref="OpenRequestChannel"/>（OPEN-01），由 <see cref="OpenCoordinator"/> 顺序消费。
    /// </summary>
    /// <param name="intent">系统 Intent。</param>
    public static void HandleIntent(Intent? intent)
    {
        _ = HandleIntentAsync(intent).ContinueWith(
            completed => Console.Error.WriteLine($"[Native] 外部打开请求失败：{completed.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 解析 Intent 并等待本次请求处理完成（验收据此断言，不靠固定延时）。
    /// 来源映射：<c>pv-open</c>=<see cref="OpenRequestSource.Automation"/>、ACTION_VIEW=<see cref="OpenRequestSource.SystemOpen"/>、
    /// ACTION_SEND(_MULTIPLE)=<see cref="OpenRequestSource.Share"/>；Data/ClipData/EXTRA_STREAM 的 Uri 去重后合并。
    /// </summary>
    /// <param name="intent">系统 Intent。</param>
    internal static async Task HandleIntentAsync(Intent? intent)
    {
        var main = _main;
        if (intent == null || main == null) return;

        var items = new List<IStorageItemRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool automation = false;

        string? openPath = ResolveLaunchPath(intent.GetStringExtra("pv-open"));
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            try
            {
                IStorageItemRef root = System.IO.Directory.Exists(openPath)
                    ? new AndroidFolderRef(openPath)
                    : new AndroidFileRef(openPath);
                if (seen.Add(root.StorageId.Value)) items.Add(root);
                automation = true;
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
                    var file = await CreateExternalFileAsync(uri).ConfigureAwait(false);
                    if (file != null && seen.Add(file.StorageId.Value)) items.Add(file);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Native] 外部打开 Uri 解析失败：{exception.Message}");
                }
            }
        }

        // 先投递打开请求再处理设置参数：设置 sheet 不被文件夹枚举阻塞（与原行为一致）。
        Task? opening = items.Count > 0
            ? main.OpenCoordinator.PublishAndWaitAsync(
                items, automation ? OpenRequestSource.Automation : SourceForAction(intent.Action))
            : null;
        HandleSettingsIntent(intent, main);
        if (opening != null) await opening;
    }

    /// <summary>Intent Action → 打开来源（ACTION_VIEW 归系统打开，分享归 Share）。</summary>
    private static OpenRequestSource SourceForAction(string? action) => action switch
    {
        Intent.ActionSend or Intent.ActionSendMultiple => OpenRequestSource.Share,
        _ => OpenRequestSource.SystemOpen,
    };

    /// <summary>
    /// 外部打开 Uri → 存储引用。<c>file://</c> 直接回本地路径快路径；<c>content://</c> 在后台线程
    /// 查询真实显示名（DocumentsProvider 投影或通用 <c>_display_name</c>）：MediaStore 这类
    /// 只有数字 documentId 的 Uri 末段不是文件名，不查属性就无法按扩展名判定类型而整条请求被丢弃。
    /// </summary>
    /// <param name="uri">外部传入的 Uri。</param>
    /// <returns>存储引用；Uri 无法转为可读目标时返回 null。</returns>
    private static async Task<IFileRef?> CreateExternalFileAsync(global::Android.Net.Uri uri)
    {
        if (string.Equals(uri.Scheme, ContentResolver.SchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            string? local = uri.Path;
            if (string.IsNullOrEmpty(local)) return null;
            Console.WriteLine($"[Native] 外部打开本地路径：{local}");
            return new AndroidFileRef(local);
        }

        var resolver = Application.Context.ContentResolver;
        if (resolver == null) return null;
        return await AndroidFileRef.CreateForContentUriAsync(resolver, uri).ConfigureAwait(false);
    }

    /// <summary>验收参数 <c>pv-settings</c> / <c>pv-settings-page</c>：打开设置 sheet 并落到指定分类页。</summary>
    private static void HandleSettingsIntent(Intent intent, MainViewModel main)
    {
        if (intent.GetBooleanExtra("pv-settings", false))
        {
            Console.WriteLine("[Native] pv-settings：启动后打开设置 sheet（验收）");
            UiDispatcher.Post(() => main.OpenSettingWindow());
        }
        // 验收：设置 sheet 直接落到指定分类页（真机禁用输入注入时用于核对提示行）。
        var settingsPage = intent.GetStringExtra("pv-settings-page");
        if (!string.IsNullOrWhiteSpace(settingsPage))
        {
            Console.WriteLine($"[Native] pv-settings-page：{settingsPage}");
            new Handler(Looper.MainLooper!).PostDelayed(
                () => AndroidNativeSettingsPresenter.CurrentSheet?.ShowPageForChecks(settingsPage), 700);
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
