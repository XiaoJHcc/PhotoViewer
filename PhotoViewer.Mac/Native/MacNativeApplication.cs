using AppKit;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using ReactiveUI;
using ReactiveUI.Builder;

namespace PhotoViewer.Mac.Native;

/// <summary>独立 AppKit 启动路径，不创建旧壳应用、窗口或主图适配位图。</summary>
public static class MacNativeApplication
{
    /// <summary>初始化原生平台门面并运行 NSApplication；历史启动参数由本路径忽略。</summary>
    public static void Run(string[] args)
    {
        string workingDirectory = Environment.GetEnvironmentVariable("PWD") ?? Environment.CurrentDirectory;
        args = args.Select(argument => argument.StartsWith("--", StringComparison.Ordinal) ? argument : System.IO.Path.GetFullPath(argument, workingDirectory)).ToArray();
        NSApplication.Init();
        Directory.SetCurrentDirectory(workingDirectory);
        ObjCRuntime.Runtime.MarshalManagedException += (_, exception) => Console.Error.WriteLine("[NativeManagedException] " + exception.Exception);
        ObjCRuntime.Runtime.MarshalObjectiveCException += (_, exception) => Console.Error.WriteLine("[NativeObjCException] " + exception.Exception.Reason);
        var application = NSApplication.SharedApplication;
        application.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        var dispatcher = new MacUiDispatcher();
        dispatcher.InstallSynchronizationContext();
        UiDispatcher.Initialize(dispatcher);
        var reactiveBuilder = RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(dispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();
        FilePicker.Initialize(new MacFilePicker());
        AppResources.Initialize(new MacAppResources());
        HeifLoader.Initialize(new MacHeifDecoder());
        RawLoader.Initialize(new MacRawDecoder());
        BitmapLoader.InitializePresentationDecoder(new ApplePresentationDecoder(), new MacPresentationEncodingPolicy());
        PerformanceBudget.Initialize(new DefaultPerformanceBudget());
        MemoryGovernor.Initialize(new MacMemoryBudgetProvider());
        if (args.Contains("--pv-native-smoke"))
        {
            Directory.CreateDirectory("release/native-p2-acceptance");
            PhotoDatabase.Initialize(System.IO.Path.GetFullPath("release/native-p2-acceptance/photos-smoke.db"));
        }
        var settingsRepository = new SettingsRepository(
            CreateSettingsStore(args),
            new DefaultPerformanceBudget(),
            appleKeyboard: true,
            BitmapLoader.AutomaticCacheLimit);
        // 验收会话不读写用户偏好：界面状态留在内存。
        IViewStateStore viewState = args.Contains("--pv-native-smoke")
            ? new InMemorySettingsStore()
            : new AppleViewStateStore();
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_CoreML());
        using var appDelegate = new NativeDelegate(args, settingsRepository, viewState);
        application.Delegate = appDelegate;
        application.Run();
    }

    /// <summary>生产走 Apple 偏好单键；冒烟会话走固定隔离文件，绝不触碰用户真实设置。</summary>
    private static ISettingsStore CreateSettingsStore(string[] args)
    {
        if (!args.Contains("--pv-native-smoke")) return new AppleSettingsStore();
        return new FileSettingsStore(System.IO.Path.GetFullPath("release/native-p2-acceptance/settings-smoke.json"));
    }

    /// <summary>验收专用性能探针目录（<c>--pv-perf-probe</c> 的值）；未指定时为 null。</summary>
    private static string? PerfProbeDirectory(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], "--pv-perf-probe", StringComparison.Ordinal)) return args[index + 1];
        return null;
    }

    /// <summary>验收专用 RAW 对拍素材目录（<c>--pv-perf-raw-compare</c> 的值）；未指定时为 null。</summary>
    private static string? RawCompareDirectory(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], "--pv-perf-raw-compare", StringComparison.Ordinal)) return args[index + 1];
        return null;
    }

    /// <summary>非开关参数即打开目标；探针目录只作为探针输入，不交给打开链路。</summary>
    private static IEnumerable<string> OpenTargets(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            if (index > 0 && string.Equals(args[index - 1], "--pv-perf-probe", StringComparison.Ordinal)) continue;
            if (index > 0 && string.Equals(args[index - 1], "--pv-perf-raw-compare", StringComparison.Ordinal)) continue;
            yield return args[index];
        }
    }

    /// <summary>AppKit 生命周期与外部打开，持有原生窗口避免托管 GC 回收平台 delegate。</summary>
    private sealed class NativeDelegate(
        string[] args,
        SettingsRepository settingsRepository,
        IViewStateStore viewState) : NSApplicationDelegate
    {
        private MacMainWindow? _mainWindow;
        private MacNativeSettingsPresenter? _settings;
        private WindowPresenter? _presenter;
        private MainViewModel? _main;

        /// <summary>外部打开请求通道：Finder 事件可早于界面就绪到达，由协调器在 VM 建立后顺序消费。</summary>
        private readonly OpenRequestChannel _openRequests = new();
        /// <summary>创建共享 VM 与原生窗口，处理命令行或提前到达的 Finder 请求。</summary>
        public override async void DidFinishLaunching(NSNotification notification)
        {
            try
            {
                await settingsRepository.InitializeAsync();
                _main = new MainViewModel(settingsRepository, viewState, _openRequests);
                _mainWindow = new MacMainWindow(_main, args.Contains("--pv-native-smoke"));
                _settings = new MacNativeSettingsPresenter();
                _mainWindow.SetSettingsPresenter(_settings);
                NativeSettingsPresenter.Initialize(_settings);
                _presenter = new WindowPresenter(_settings, _mainWindow);
                NativeWindowPresenter.Initialize(_presenter);
                if (OperatingSystem.IsMacOSVersionAtLeast(14)) NSApplication.SharedApplication.Activate();
                else NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
                var openTargets = OpenTargets(args).ToArray();
                var openItems = MacOpenPaths.FromPaths(openTargets);
                if (openItems.Count > 0)
                {
                    await _main.OpenCoordinator.PublishAndWaitAsync(openItems, OpenRequestSource.CommandLine);
                }
                Console.WriteLine("[Native] AppKit + Metal 主窗口启动成功，打开路径：" + string.Join(" | ", openTargets));
                if (args.Contains("--pv-native-smoke")) _ = NativeAcceptanceRunner.RunAsync(_mainWindow, _settings, args);
                if (StressFlipDriver.TryParseArgs(args, relative => System.IO.Path.GetFullPath(relative)))
                    StressFlipDriver.MaybeStart(_main, _mainWindow.Session);
                if (PerfProbeDirectory(args) is { } probeDirectory)
                {
                    string reportPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "PhotoViewer", "perf-probe.txt");
                    Console.WriteLine($"[PerfProbe] start {probeDirectory} -> {reportPath}");
                    await ApplePerfProbe.RunAsync(probeDirectory, reportPath, () => (Environment.WorkingSet, -1L));
                    Console.WriteLine($"[PerfProbe] done {reportPath}");
                    Environment.Exit(0);
                }

                if (RawCompareDirectory(args) is { } rawCompareDirectory)
                {
                    string reportPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "PhotoViewer", "raw-acceptance.txt");
                    string workDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pv-raw-acceptance");
                    Console.WriteLine($"[RawAcceptance] start {rawCompareDirectory} -> {reportPath}");
                    bool passed = await ApplePerfProbe.RunRawAcceptanceAsync(rawCompareDirectory, workDirectory, reportPath);
                    Console.WriteLine($"[RawAcceptance] {(passed ? "PASS" : "FAIL")} {reportPath}");
                    Environment.Exit(passed ? 0 : 1);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                if (args.Contains("--pv-native-smoke")) { Environment.Exit(1); return; }
                using var alert = new NSAlert { MessageText = "原生窗口启动失败", InformativeText = exception.Message };
                alert.RunModal(); NSApplication.SharedApplication.Terminate(null);
            }
        }
        /// <summary>Finder 文件事件：一批合成一个打开请求，目录与文件都转本地引用。</summary>
        public override void OpenFiles(NSApplication sender, string[] filenames)
        {
            PublishOpenItems(MacOpenPaths.FromPaths(filenames));
            sender.ReplyToOpenOrPrint(NSApplicationDelegateReply.Success);
        }
        /// <summary>文件 URL 打开事件：同样按一批投递，保留系统给出的顺序。</summary>
        public override void OpenUrls(NSApplication application, NSUrl[] urls)
            => PublishOpenItems(MacOpenPaths.FromUrls(urls));

        /// <summary>把系统打开项投进请求通道；界面就绪前排队，重复激活由协调器只前置窗口。</summary>
        private void PublishOpenItems(IReadOnlyList<IStorageItemRef> items)
        {
            if (items.Count == 0) return;
            if (!_openRequests.Publish(new OpenRequest(items, OpenRequestSource.SystemOpen)))
                Console.Error.WriteLine("[Open] 请求通道已关闭，丢弃系统打开事件");
        }
        /// <summary>关闭最后窗口后退出 app。</summary>
        public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
        /// <summary>退出前关闭设置工作副本并完成持久化，再释放原生展示，避免保存节流来不及执行。</summary>
        public override void WillTerminate(NSNotification notification)
        {
            try
            {
                _settings?.CloseWindowAsync().GetAwaiter().GetResult();
                _presenter?.Dispose();
                if (_mainWindow != null)
                {
                    // 同一仓库：等所有在途写入真正完成（设置会话的关闭回写也在其中）。
                    settingsRepository.FlushAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("[Native] 退出保存设置失败：" + exception);
            }
            finally
            {
                // 界面资源先归还，再结束主视图模型（外部打开消费者、文件源会话与设置订阅）。
                _mainWindow?.Dispose();
                _main?.Dispose();
            }
        }
    }

    /// <summary>共享设置/工具窗口门面的 AppKit 实现；工具窗口为单例，未创建时按需创建。</summary>
    private sealed class WindowPresenter(MacNativeSettingsPresenter settings, MacMainWindow window) : INativeWindowPresenter, IDisposable
    {
        private MacToolsWindow? _tools;
        /// <summary>显示原生设置。</summary>
        public void ShowSettingsWindow(SettingsViewModel viewModel) => settings.TryPresent(viewModel);
        /// <summary>显示或前置唯一的原生工具窗口；打开前把当前照片同步给工具页。</summary>
        public void ShowOrActivateToolsWindow(ToolsViewModel tools)
        {
            if (_tools == null)
            {
                _tools = new MacToolsWindow(window.Main, window.IsAutomatedValidation);
                MacToolsWindowRegistry.Current = _tools;
            }
            _tools.SyncCurrentFile();
            _tools.ShowOrActivate();
        }
        /// <summary>退出时释放工具窗口的订阅与视图。</summary>
        public void Dispose()
        {
            MacToolsWindowRegistry.Current = null;
            _tools?.Dispose();
            _tools = null;
        }
    }
}

/// <summary>原生工具窗口登记处：实例由展示器创建，验收据此断言单例、关闭与重开。</summary>
internal static class MacToolsWindowRegistry
{
    /// <summary>当前原生工具窗口；未创建时为 null。</summary>
    internal static MacToolsWindow? Current { get; set; }
}
