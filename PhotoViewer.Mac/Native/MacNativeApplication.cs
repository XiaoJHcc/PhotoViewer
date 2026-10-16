using AppKit;
using Foundation;
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

/// <summary>独立 AppKit 启动路径，不创建 Avalonia 应用、窗口或主图适配位图。</summary>
public static class MacNativeApplication
{
    /// <summary>初始化原生平台门面并运行 NSApplication，旧壳由显式启动开关保留。</summary>
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
        BitmapLoader.InitializePresentationDecoder(new MacPresentationDecoder());
        PerformanceBudget.Initialize(new DefaultPerformanceBudget());
        MemoryGovernor.Initialize(new DefaultMemoryBudgetProvider());
        XmpWriter.Initialize(new MacXmpWriter());
        if (args.Contains("--pv-native-smoke"))
        {
            Directory.CreateDirectory("release/native-p2-acceptance");
            SettingsService.ConfigureStorage(new FileSettingsStorage(System.IO.Path.GetFullPath("release/native-p2-acceptance/settings-smoke.json")));
            PhotoDatabase.Initialize(System.IO.Path.GetFullPath("release/native-p2-acceptance/photos-smoke.db"));
        }
        else SettingsService.ConfigureStorage(new MacSettingsStorage());
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_CoreML());
        using var appDelegate = new NativeDelegate(args);
        application.Delegate = appDelegate;
        application.Run();
    }

    /// <summary>AppKit 生命周期与外部打开，持有原生窗口避免托管 GC 回收平台 delegate。</summary>
    private sealed class NativeDelegate(string[] args) : NSApplicationDelegate
    {
        private MacMainWindow? _mainWindow;
        private MacNativeSettingsPresenter? _settings;
        private WindowPresenter? _presenter;
        /// <summary>创建共享 VM 与原生窗口，处理命令行或提前到达的 Finder 请求。</summary>
        public override async void DidFinishLaunching(NSNotification notification)
        {
            try
            {
                var main = new MainViewModel(nativePresentation: true);
                _mainWindow = new MacMainWindow(main, args.Contains("--pv-native-smoke"));
                _settings = new MacNativeSettingsPresenter();
                _mainWindow.SetSettingsPresenter(_settings);
                NativeSettingsPresenter.Initialize(_settings);
                _presenter = new WindowPresenter(_settings, _mainWindow);
                NativeWindowPresenter.Initialize(_presenter);
                if (OperatingSystem.IsMacOSVersionAtLeast(14)) NSApplication.SharedApplication.Activate();
                else NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
                await _mainWindow.OpenPathsAsync(args.Where(path => !path.StartsWith("--", StringComparison.Ordinal)));
                Console.WriteLine("[Native] AppKit + Metal 主窗口启动成功，打开路径：" + string.Join(" | ", args.Where(path => !path.StartsWith("--", StringComparison.Ordinal))));
                if (args.Contains("--pv-native-smoke")) _ = NativeAcceptanceRunner.RunAsync(_mainWindow, _settings, args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                if (args.Contains("--pv-native-smoke")) { Environment.Exit(1); return; }
                using var alert = new NSAlert { MessageText = "原生窗口启动失败", InformativeText = exception.Message };
                alert.RunModal(); NSApplication.SharedApplication.Terminate(null);
            }
        }
        /// <summary>Finder 文件事件进入共享外部打开队列。</summary>
        public override void OpenFiles(NSApplication sender, string[] filenames)
        {
            foreach (var filename in filenames)
                if (Directory.Exists(filename)) ExternalOpenService.PublishFolder(new Uri(filename), "MacNativeOpenFiles");
                else ExternalOpenService.PublishFile(new Uri(filename), "MacNativeOpenFiles");
            sender.ReplyToOpenOrPrint(NSApplicationDelegateReply.Success);
        }
        /// <summary>文件 URL 打开事件。</summary>
        public override void OpenUrls(NSApplication application, NSUrl[] urls)
        {
            foreach (var url in urls) if (url.IsFileUrl && url.Path is { } path) ExternalOpenService.PublishFile(new Uri(path), "MacNativeOpenUrls");
        }
        /// <summary>关闭最后窗口后退出 app。</summary>
        public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
        /// <summary>退出前关闭设置工作副本并完成持久化，再释放原生展示，避免保存节流来不及执行。</summary>
        public override void WillTerminate(NSNotification notification)
        {
            _settings?.CloseWindow();
            _presenter?.Dispose();
            if (_mainWindow != null)
            {
                SettingsService.Instance.SaveAsync(_mainWindow.Main.Settings.CreateSnapshot()).GetAwaiter().GetResult();
                _mainWindow.Dispose();
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
