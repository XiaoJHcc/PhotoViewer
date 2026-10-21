using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using PhotoViewer.Windows.Core.Heif;
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
    private WindowsMainWindow? _window;
    private MainViewModel? _main;
    private WindowsNativeSettingsPresenter? _settingsPresenter;
    private WindowsNativeToolsPresenter? _toolsPresenter;

    /// <summary>
    /// 初始化应用对象并载入应用级资源。
    /// </summary>
    /// <param name="args">命令行参数</param>
    public App(string[] args)
    {
        LaunchArgs = args;
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

            _main = new MainViewModel();
            _settingsPresenter = new WindowsNativeSettingsPresenter(() => WindowsNativeApplication.WindowHandle);
            _toolsPresenter = new WindowsNativeToolsPresenter(() => WindowsNativeApplication.WindowHandle);
            NativeWindowPresenter.Initialize(new NativeWindowPresenterImpl(_settingsPresenter, _toolsPresenter));
            _window = new WindowsMainWindow(_main);
            _window.Closed += OnWindowClosed;
            _window.Activate();
            WindowsNativeApplication.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            NativeStartupLog.Write("main window activated");

            ExternalOpenService.RegisterHandler(OnExternalOpenRequested);

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
            await _window.OpenPathsAsync(paths);
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

    /// <summary>
    /// 外部打开请求:Windows 本地路径可直接解析(无安全作用域),文件夹走打开文件夹、图片走打开图片。
    /// </summary>
    private Task OnExternalOpenRequested(ExternalOpenRequest request)
    {
        var main = _main;
        if (main == null)
        {
            return Task.CompletedTask;
        }

        return UiDispatcher.InvokeAsync(() => ResolveAsync(main, request)).Unwrap();
    }

    /// <summary>在 UI 线程上解析并打开外部请求。</summary>
    private static async Task ResolveAsync(MainViewModel main, ExternalOpenRequest request)
    {
        foreach (var item in request.Items)
        {
            if (item.StorageItem is IFolderRef directFolder)
            {
                await main.FolderVM.OpenFolderAsync(directFolder);
                return;
            }

            if (item.StorageItem is IFileRef directFile && main.FolderVM.IsImageFile(directFile.Name))
            {
                await main.FolderVM.OpenExternalFilesAsync(new[] { directFile });
                return;
            }

            var path = item.Path?.LocalPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (item.Kind == ExternalOpenItemKind.Folder)
            {
                if (Directory.Exists(path))
                {
                    await main.FolderVM.OpenFolderAsync(new LocalFolderRef(path));
                    return;
                }

                continue;
            }

            if (!File.Exists(path) || !main.FolderVM.IsImageFile(System.IO.Path.GetFileName(path)))
            {
                continue;
            }

            await main.FolderVM.OpenExternalFilesAsync(new[] { (IFileRef)new LocalFileRef(path) });
            return;
        }

        NativeStartupLog.Write($"外部打开未解析出可用项({request.Source})");
    }

    /// <summary>关闭窗口:先关设置/工具窗口(设置窗口回写工作副本),再释放 GPU 资源与订阅。</summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _settingsPresenter?.CloseIfOpen();
        _toolsPresenter?.CloseIfOpen();
        _window?.Detach();
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
