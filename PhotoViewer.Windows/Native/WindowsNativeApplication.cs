using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using ReactiveUI;
using ReactiveUI.Builder;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// Windows 原生壳启动器。
/// 原生模式不创建旧壳应用、窗口与调度器;生命周期由 WinUI 3 承载,平台门面在这里注入。
/// </summary>
internal static class WindowsNativeApplication
{
    /// <summary>主窗口句柄;文件选择器与设置窗口尺寸在窗口创建后读取。</summary>
    internal static IntPtr WindowHandle { get; set; }

    /// <summary>本轮是否为隔离运行(验收 / 压测 / 性能探针):设置、数据库与日志都落到隔离目录。</summary>
    internal static bool IsolatedMode { get; private set; }

    /// <summary>本轮是否要求压测翻页(<c>--pv-stress=...</c>;解析在 <see cref="Run"/> 里完成)。</summary>
    internal static bool StressRequested { get; private set; }

    /// <summary>性能探针素材目录(<c>--pv-perf-probe</c>);null 表示不跑探针。</summary>
    internal static string? PerfProbeFolder { get; private set; }

    /// <summary>
    /// 压测用的后台预取并发覆盖(<c>--pv-stress-native-threads=N</c>);null = 用设置里的值。
    /// 只为并行度扫描服务:隔离设置每轮会被重置,无法靠改设置文件做对照实验。
    /// </summary>
    internal static int? StressNativeThreads { get; private set; }

    /// <summary>隔离产物目录(设置、数据库、日志、报告都在这里)。</summary>
    internal static string IsolatedOutputDirectory => WindowsMainWindow.SmokeOutputDirectory;

    /// <summary>本进程的预算源(含低内存监视线程);重复注入只创建一次。</summary>
    private static WindowsMemoryBudgetProvider? s_memoryBudgetProvider;

    /// <summary>
    /// 启动原生壳。
    /// </summary>
    /// <param name="args">命令行参数(非 -- 开头者按外部打开路径处理)</param>
    public static void Run(string[] args)
    {
        var smoke = args.Contains("--pv-native-smoke");
        var settingsCheck = args.Contains("--pv-native-settings");
        var settingsHold = args.Contains("--pv-native-settings-hold");
        var toolsCheck = args.Contains("--pv-native-tools");
        var toolsHold = args.Contains("--pv-native-tools-hold");

        // 压测与性能探针同样是验收类入口:参数在创建任何窗口/VM 之前解析,
        // 让设置、数据库、启动日志与报告都落在隔离目录,不碰用户的真实设置与照片文件夹。
        PerfProbeFolder = WindowsPerfProbe.ParseFolder(args);
        if (PerfProbeFolder != null) PerfProbeFolder = Path.GetFullPath(PerfProbeFolder);
        StressRequested = StressFlipDriver.TryParseArgs(args, relative => Path.GetFullPath(relative));
        StressNativeThreads = ParseNativeThreads(args);
        var thumbProbe = WindowsHeifThumbProbe.ParsePaths(args).Length > 0;
        IsolatedMode = smoke || settingsCheck || settingsHold || toolsCheck || toolsHold
            || StressRequested || PerfProbeFolder != null || thumbProbe;

        var logName = StressRequested ? "native-startup-stress.log"
            : PerfProbeFolder != null ? "native-startup-probe.log"
            : thumbProbe ? "native-startup-thumb-probe.log"
            : "native-startup.log";
        NativeStartupLog.BeginSession(
            args,
            IsolatedMode ? Path.Combine(WindowsMainWindow.SmokeOutputDirectory, logName) : null);
        NativeStartupLog.InstallHooks();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        NativeStartupLog.Write("com wrappers ready");

        Application.Start(p =>
        {
            try
            {
                // WinUI 在 UI 线程上先建立 DispatcherQueue。这里安装 .NET 的 SynchronizationContext
                // 让 async/await 续体回到 UI 线程,并把同一个队列交给共享的 UiDispatcher 门面。
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new WindowsSynchronizationContext(queue));
                var dispatcher = new WindowsUiDispatcher(queue);
                UiDispatcher.Initialize(dispatcher);
                NativeStartupLog.Write("dispatcher installed");

                RxAppBuilder.CreateReactiveUIBuilder()
                    .WithMainThreadScheduler(dispatcher.MainThreadScheduler)
                    .WithCoreServices()
                    .BuildApp();
                NativeStartupLog.Write("reactiveui ready");

                _ = new App(args);
                NativeStartupLog.Write("application created");
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("Application.Start callback", ex);
                throw;
            }
        });
    }

    /// <summary>
    /// 注入平台门面。顺序要求:<see cref="UiDispatcher.Initialize"/> 必须早于 MainViewModel 构造。
    /// </summary>
    /// <param name="isolated">是否使用验收专用设置和数据库(验收 / 压测 / 性能探针)。</param>
    internal static void InitializeFacades(bool isolated)
    {
        FilePicker.Initialize(new WindowsFilePicker(() => WindowHandle != IntPtr.Zero
            ? WindowHandle
            : throw new InvalidOperationException("窗口句柄尚未就绪,无法弹出文件选择器。")));
        AppResources.Initialize(new WindowsAppResources());
        HeifLoader.Initialize(new LibHeifDecoder());
        BitmapLoader.InitializePresentationDecoder(new WindowsPresentationDecoder(), new WindowsPresentationEncodingPolicy());
        PerformanceBudget.Initialize(new WindowsPerformanceBudget());
        MemoryGovernor.Initialize(s_memoryBudgetProvider ??= new WindowsMemoryBudgetProvider());
        XmpWriter.Initialize(new WindowsXmpWriter());
        if (isolated)
        {
            WindowsMainWindow.ConfigureSmokeStorage();
        }
        else
        {
            SettingsService.ConfigureStorage(SettingsService.CreateFileStorage());
        }
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_DML());
        NativeStartupLog.Write("facades ready");
    }

    /// <summary>解析 <c>--pv-stress-native-threads=N</c>(只用于压测并行度扫描)。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <returns>覆盖值(1–32);未指定或非法返回 null。</returns>
    private static int? ParseNativeThreads(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--pv-stress-native-threads=", StringComparison.Ordinal)) continue;
            if (int.TryParse(arg["--pv-stress-native-threads=".Length..], out var threads) && threads > 0)
            {
                return Math.Clamp(threads, 1, 32);
            }
        }

        return null;
    }
}
