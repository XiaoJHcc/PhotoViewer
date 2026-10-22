using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private static PhotoLoadSession? _session;
    private static AndroidNativeRootView? _root;
    private static AndroidNativeSettingsPresenter? _settingsPresenter;
    private static AndroidToolsPresenter? _toolsPresenter;
    private static IPresentationImageDecoder? _presentationDecoder;
    private static bool _externalHandlerRegistered;

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
    /// 硬件键盘派发：设置 sheet/文本输入期间不抢键；命中用户绑定且命令可用时执行并消费。
    /// 系统重复（<see cref="KeyEventActions.Multiple"/>）只对共享命令表标记为可重复的命令执行（INPUT-05）。
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
        if (command == null || !AndroidControlBar.IsCommandAvailable(command)) return false;
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

        // 性能探针独占一轮：不建共享 VM 与主界面，测完写报告并结束进程（与 Windows/Mac 同流程）。
        var perfProbeFolder = ResolveLaunchPath(intent?.GetStringExtra("pv-perf-probe"));
        if (!string.IsNullOrWhiteSpace(perfProbeFolder))
            return RunPerfProbe(activity, perfProbeFolder, intent?.GetIntExtra("pv-perf-probe-target", 0) ?? 0);

        if (_main == null)
        {
            _main = new MainViewModel();
            Console.WriteLine("[Native] 共享 MainViewModel 就绪");
        }
        _session ??= new PhotoLoadSession();
        _toolsPresenter = new AndroidToolsPresenter(activity, _main);
        NativeWindowPresenter.Initialize(new AndroidNativeWindowPresenter(activity, _settingsPresenter!, _toolsPresenter));
        RegisterExternalOpen(_main);

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
    private static void ExitShell(Activity activity, string reason)
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
    /// 平台门面与 ONNX 执行提供程序注入。
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
        StorageAccessManager.Initialize(new AndroidStorageAccessManager());
        XmpWriter.Initialize(new AndroidXmpWriter(activity.ContentResolver!));
        SettingsService.ConfigureStorage(new AndroidSettingsStorage());
        AndroidInputDevices.Initialize(activity);
        _settingsPresenter = new AndroidNativeSettingsPresenter(activity);
        NativeSettingsPresenter.Initialize(_settingsPresenter);
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_Nnapi());
        Console.WriteLine("[Native] 平台门面就绪（dispatcher/heif/decode/budget/governor/storage/xmp/settings/dino）");
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
                // OPEN-01：多项按投递顺序取首个可打开项（文件夹或受支持图片），其余忽略。
                var first = request.Items.FirstOrDefault(item => item.StorageItem is IFolderRef
                    || item.StorageItem is IFileRef file && main.FolderVM.IsImageFile(file.Name));
                if (first?.StorageItem is IFolderRef folder)
                {
                    await main.FolderVM.OpenFolderAsync(folder);
                    return;
                }
                if (first?.StorageItem is IFileRef target)
                    await main.FolderVM.OpenExternalFilesAsync([target]);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Native] 外部打开失败：{exception}");
                main.ReportOpenFailure(StatusMessages.OpenFailed(request.Items.FirstOrDefault()?.StorageItem?.Name, null));
            }
        });
    }

    /// <summary>
    /// 处理启动/新 Intent：<c>pv-open</c> 验收路径优先，其次 ACTION_VIEW/SEND 的 content Uri；
    /// 全部合并为一次外部打开请求（OPEN-01），不逐项投递。
    /// </summary>
    /// <param name="intent">系统 Intent。</param>
    public static void HandleIntent(Intent? intent)
    {
        if (intent == null || _main == null) return;
        var items = new List<ExternalOpenItem>();
        string? openPath = ResolveLaunchPath(intent.GetStringExtra("pv-open"));
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            try
            {
                items.Add(new ExternalOpenItem(System.IO.Directory.Exists(openPath)
                    ? new AndroidFolderRef(openPath)
                    : new AndroidFileRef(openPath)));
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
                    items.Add(new ExternalOpenItem(new AndroidFileRef(Application.Context.ContentResolver!, uri, null,
                        uri.LastPathSegment, null, null)));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Native] 外部打开 Uri 解析失败：{exception.Message}");
                }
            }
        }
        if (items.Count > 0) ExternalOpenService.Publish(new ExternalOpenRequest(items, "android-intent"));
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
