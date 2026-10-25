using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Windows.Core;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// WinUI 3 应用对象。XAML 只承载应用级资源字典(<see cref="XamlControlsResources"/>),
/// 使 WinUI 自带控件模板与 Fluent 主题资源可解析;所有界面仍在 C# 中构建。
/// 生命周期、平台门面注入与主窗口创建保持原顺序。
/// </summary>
public partial class App : Application
{
    private readonly OpenRequestChannel _openRequests;
    private WindowsMainWindow? _window;
    private MainViewModel? _main;
    private SettingsRepository? _settingsRepository;
    private WindowsNativeSettingsPresenter? _settingsPresenter;
    private WindowsNativeToolsPresenter? _toolsPresenter;
    private bool _closed;

    /// <summary>
    /// 初始化应用对象并载入应用级资源。
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <param name="openRequests">启动期外部打开通道(主实例激活也投到这里)</param>
    public App(string[] args, OpenRequestChannel openRequests)
    {
        LaunchArgs = args;
        _openRequests = openRequests;
        InitializeComponent();
        UnhandledException += (_, e) => NativeStartupLog.Fail("Application.UnhandledException", e.Exception);
    }

    /// <summary>命令行参数。</summary>
    public string[] LaunchArgs { get; }

    /// <summary>
    /// 应用启动完成:装配门面、构建共享视图模型与原生主窗口。
    /// </summary>
    /// <param name="args">启动参数</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var smoke = LaunchArgs.Contains("--pv-native-smoke");
            var settingsCheck = LaunchArgs.Contains("--pv-native-settings");
            var settingsHold = LaunchArgs.Contains("--pv-native-settings-hold");
            var toolsCheck = LaunchArgs.Contains("--pv-native-tools");
            var toolsHold = LaunchArgs.Contains("--pv-native-tools-hold");
            var stress = WindowsNativeApplication.StressRequested;
            var perfProbeFolder = WindowsNativeApplication.PerfProbeFolder;
            var thumbProbePaths = WindowsHeifThumbProbe.ParsePaths(LaunchArgs);
            DebugSettings.XamlResourceReferenceFailed += (_, e) =>
                NativeStartupLog.Write("xaml resource failed: " + e.Message);
            DebugSettings.BindingFailed += (_, e) =>
                NativeStartupLog.Write("xaml binding failed: " + e.Message);
            WindowsNativeApplication.InitializeFacades(
                smoke || settingsCheck || settingsHold || toolsCheck || toolsHold
                || stress || perfProbeFolder != null || thumbProbePaths.Length > 0);

            // HEIF 缩略图项对比探针:不需要窗口与视图模型,门面就绪后直接跑完退出。
            if (thumbProbePaths.Length > 0)
            {
                var thumbReportPath = Path.Combine(
                    WindowsNativeApplication.IsolatedOutputDirectory,
                    "heif-thumb-probe.txt");
                NativeStartupLog.Write($"[HeifThumbProbe] start -> {thumbReportPath}");
                var failure = await WindowsHeifThumbProbe.RunAsync(thumbProbePaths, thumbReportPath);
                NativeStartupLog.Write(failure.Length == 0
                    ? $"[HeifThumbProbe] PASS {thumbReportPath}"
                    : $"[HeifThumbProbe] FAIL {failure}");
                Environment.Exit(failure.Length == 0 ? 0 : 1);
                return;
            }

            var settingsRepository = await CreateSettingsRepositoryAsync();
            _settingsRepository = settingsRepository;
            var viewState = WindowsNativePreferences.CreateStore(WindowsNativeApplication.IsolatedMode);
            _main = new MainViewModel(
                settingsRepository,
                viewState,
                _openRequests);
            _settingsPresenter = new WindowsNativeSettingsPresenter(() => WindowsNativeApplication.WindowHandle);
            _toolsPresenter = new WindowsNativeToolsPresenter(() => WindowsNativeApplication.WindowHandle);
            NativeWindowPresenter.Initialize(new NativeWindowPresenterImpl(_settingsPresenter, _toolsPresenter));
            _window = new WindowsMainWindow(_main, viewState);
            _window.Closed += OnWindowClosed;
            _window.Activate();
            WindowsNativeApplication.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            NativeStartupLog.Write("main window activated");

            // 重复激活(再次打开当前目录/当前图片):前置窗口,并由协调器按 OPEN-08 原地刷新并保留视口;无路径的二次启动只走前置动作。
            _main.OpenCoordinator.RepeatActivation += OnRepeatActivation;
            WindowsSingleInstance.AttachForeground(BringMainWindowToFront);

            if (smoke)
            {
                await _window.RunSmokeAsync();
                return;
            }

            if (settingsCheck)
            {
                await WindowsSettingsAcceptance.RunAsync(_main, _settingsPresenter!);
                _window.Close();
                return;
            }

            if (settingsHold)
            {
                _settingsPresenter!.TryPresent(_main.Settings);
                return;
            }

            if (toolsCheck)
            {
                await WindowsToolsAcceptance.RunAsync(_main, _window, _toolsPresenter!);
                _window.Close();
                return;
            }

            if (toolsHold)
            {
                await WindowsToolsAcceptance.PrepareHoldAsync(_main, _window, _toolsPresenter!);
                return;
            }

            // 性能探针独占一轮:不开用户路径、不进交互,测完即退出(与 Mac --pv-perf-probe 同流程)。
            if (perfProbeFolder != null)
            {
                var reportPath = Path.Combine(
                    WindowsNativeApplication.IsolatedOutputDirectory,
                    "perf-probe.txt");
                NativeStartupLog.Write($"[PerfProbe] start {perfProbeFolder} -> {reportPath}");
                await WindowsPerfProbe.RunAsync(perfProbeFolder, reportPath);
                NativeStartupLog.Write($"[PerfProbe] done {reportPath}");
                Environment.Exit(0);
                return;
            }

            // 探针目录只作为探针输入,不当成待打开路径;其余非 -- 参数仍按外部打开处理。
            var paths = LaunchArgs
                .Where((arg, index) => !arg.StartsWith("--", StringComparison.Ordinal)
                    && (index == 0 || !string.Equals(LaunchArgs[index - 1], "--pv-perf-probe", StringComparison.Ordinal)))
                .ToList();
            var startupItems = WindowsOpenPaths.FromPaths(paths);
            if (startupItems.Count > 0)
            {
                // 命令行等待打开完成再报 startup complete:验收与脚本依赖这个顺序,而不是只投递。
                await _main.OpenCoordinator.PublishAndWaitAsync(startupItems, OpenRequestSource.CommandLine);
            }

            NativeStartupLog.Write("startup complete");

            // 压测在窗口与 PhotoLoadSession 就绪后启动(与 Mac 同一注入点)。
            if (stress)
            {
                // 压测期间不跑后台特征索引:索引会解 560 缩略图并抢占解码与 CPU,把翻页延迟混成"索引延迟"。
                _main.Settings.SimilarityPanelExpanded = false;
                // 并行度扫描:隔离设置每轮被重置,用启动参数覆盖后台预取并发(BitmapPrefetcher 订阅该属性)。
                if (WindowsNativeApplication.StressNativeThreads is int stressThreads)
                {
                    _main.Settings.NativePreloadParallelism = stressThreads;
                    NativeStartupLog.Write($"stress: preload parallelism override={stressThreads}");
                }
                WindowsUploadStats.Reset();
                HeifDecodeStats.Reset();
                StressFlipDriver.LogDirectory = WindowsNativeApplication.IsolatedOutputDirectory;
                // 汇总行补三项进程级指标:后台预取并发、第二轮 GC 次数增量(长尾归因)、私有内存与峰值工作集。
                var stressThreadsEffective = _main.Settings.NativePreloadParallelism;
                var gc2AtStart = GC.CollectionCount(2);
                StressFlipDriver.ExtraSummary = () =>
                {
                    using var process = System.Diagnostics.Process.GetCurrentProcess();
                    return _window.BuildStressSummary()
                        + HeifDecodeStats.Summary()
                        + $" preload={stressThreadsEffective}"
                        + $" gc2Delta={GC.CollectionCount(2) - gc2AtStart}"
                        + $" privateMB={process.PrivateMemorySize64 >> 20}"
                        + $" peakWSMB={process.PeakWorkingSet64 >> 20}";
                };
                // 自动化压测:一轮翻完就退出进程,否则窗口留在前台占用隔离数据库,下一轮无法重置。
                StressFlipDriver.Completed = () =>
                {
                    NativeStartupLog.Write("stress: done, exiting");
                    Environment.Exit(0);
                };
                NativeStartupLog.Write("stress: start, similarity panel collapsed, feature indexing off");
                StressFlipDriver.MaybeStart(_main, _window.Session);
            }
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("OnLaunched", ex);
            throw;
        }
    }

    /// <summary>重复激活当前目标:把已有主窗口前置;原地刷新由协调器按 OPEN-08 完成,不重新 Fit(OPEN-06/08)。</summary>
    private void OnRepeatActivation() => BringMainWindowToFront();

    /// <summary>把主窗口前置;无路径的二次启动也走这里,关闭后不再抢前台。</summary>
    private void BringMainWindowToFront()
    {
        if (_closed)
        {
            return;
        }

        UiDispatcher.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            _window?.BringToFront();
        });
    }

    /// <summary>
    /// 建立设置仓库并完成首次读取：隔离运行落验收目录，普通运行落用户档。
    /// 默认值按 <see cref="WindowsPerformanceBudget"/> 推导，缓存容量由用户设置决定。
    /// </summary>
    /// <returns>已初始化的仓库。</returns>
    private static async Task<SettingsRepository> CreateSettingsRepositoryAsync()
    {
        var path = WindowsNativeApplication.SettingsFilePath;
        var repository = new SettingsRepository(
            new FileSettingsStore(path),
            new WindowsPerformanceBudget(),
            appleKeyboard: false,
            automaticCacheLimit: false);
        await repository.InitializeAsync();
        NativeStartupLog.Write(
            $"settings: path={path} outcome={repository.LoadOutcome}"
            + $" canCommit={repository.CanCommit}"
            + (repository.LastLoadError is { } error ? $" error={error}" : string.Empty));
        return repository;
    }

    /// <summary>
    /// 关闭窗口:先关设置/工具窗口并统一落盘,再释放界面资源与订阅,
    /// 最后结束主视图模型(取消外部打开消费者、在途打开与设置订阅);两段释放互不重叠。
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        try
        {
            // 设置窗口关闭即提交工作副本;提交全部走 ConfigureAwait(false),此处同步等待不会死锁。
            _settingsPresenter?.CloseIfOpenAsync().GetAwaiter().GetResult();
            _toolsPresenter?.CloseIfOpen();
            _settingsRepository?.FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("退出时回写设置", ex);
        }

        try
        {
            if (_main != null)
            {
                _main.OpenCoordinator.RepeatActivation -= OnRepeatActivation;
            }

            _window?.Detach();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("退出时释放界面资源", ex);
        }

        // 界面资源先归还,再结束视图模型与单实例键:两者释放对象不重叠。
        try
        {
            _main?.Dispose();
            WindowsSingleInstance.Shutdown();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("退出时释放主视图模型", ex);
        }

        NativeStartupLog.Write("window closed");
    }

    /// <summary>
    /// 原生壳的窗口门面:设置与工具窗口分别转交各自的 presenter,不另建展示路径。
    /// </summary>
    private sealed class NativeWindowPresenterImpl : INativeWindowPresenter
    {
        private readonly WindowsNativeSettingsPresenter _settings;
        private readonly WindowsNativeToolsPresenter _tools;

        /// <summary>
        /// 创建窗口门面。
        /// </summary>
        /// <param name="settings">设置窗口展示器</param>
        /// <param name="tools">工具窗口展示器</param>
        public NativeWindowPresenterImpl(
            WindowsNativeSettingsPresenter settings,
            WindowsNativeToolsPresenter tools)
        {
            _settings = settings;
            _tools = tools;
        }

        /// <inheritdoc/>
        public void ShowSettingsWindow(SettingsViewModel settings) => _settings.TryPresent(settings);

        /// <inheritdoc/>
        public void ShowOrActivateToolsWindow(ToolsViewModel tools) => _tools.TryPresent(tools);
    }
}
