using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.ML.OnnxRuntime;
using PhotoViewer.Core.AI;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Desktop.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using ReactiveUI;
using ReactiveUI.Builder;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// Windows 原生壳启动器。
/// 原生模式不创建 Avalonia 应用、窗口与调度器;生命周期由 WinUI 3 承载,平台门面在这里注入。
/// </summary>
internal static class WindowsNativeApplication
{
    private static IntPtr _windowHandle;

    /// <summary>
    /// 启动原生壳。
    /// </summary>
    /// <param name="args">命令行参数(非 -- 开头者按外部打开路径处理)</param>
    public static void Run(string[] args)
    {
        var smoke = args.Contains("--pv-native-smoke");
        var settingsCheck = args.Contains("--pv-native-settings");
        var settingsHold = args.Contains("--pv-native-settings-hold");
        NativeStartupLog.BeginSession(args, smoke || settingsCheck || settingsHold
            ? Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "native-startup.log")
            : null);
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

                new NativeApplication(args);
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
    /// <param name="smoke">是否使用验收专用设置和数据库。</param>
    private static void InitializeFacades(bool smoke)
    {
        FilePicker.Initialize(new WindowsFilePicker(() => _windowHandle != IntPtr.Zero
            ? _windowHandle
            : throw new InvalidOperationException("窗口句柄尚未就绪,无法弹出文件选择器。")));
        AppResources.Initialize(new WindowsAppResources());
        HeifLoader.Initialize(new LibHeifDecoder());
        BitmapLoader.InitializePresentationDecoder(new WindowsPresentationDecoder(), new WindowsPresentationEncodingPolicy());
        PerformanceBudget.Initialize(new DefaultPerformanceBudget());
        MemoryGovernor.Initialize(new DefaultMemoryBudgetProvider());
        XmpWriter.Initialize(new WindowsXmpWriter());
        if (smoke)
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

    /// <summary>
    /// WinUI 3 应用对象:承载进程生命周期与主窗口。
    /// </summary>
    private sealed class NativeApplication : Application
    {
        private WindowsMainWindow? _window;
        private MainViewModel? _main;
        private WindowsNativeSettingsPresenter? _settingsPresenter;

        /// <summary>
        /// 初始化应用对象。
        /// </summary>
        /// <param name="args">命令行参数</param>
        public NativeApplication(string[] args)
        {
            LaunchArgs = args;
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
                DebugSettings.XamlResourceReferenceFailed += (_, e) =>
                    NativeStartupLog.Write("xaml resource failed: " + e.Message);
                DebugSettings.BindingFailed += (_, e) =>
                    NativeStartupLog.Write("xaml binding failed: " + e.Message);
                InitializeFacades(smoke || settingsCheck || settingsHold);

                _main = new MainViewModel(nativePresentation: true);
                _settingsPresenter = new WindowsNativeSettingsPresenter(() => _windowHandle);
                NativeWindowPresenter.Initialize(new NativeWindowPresenterImpl(_settingsPresenter));
                _window = new WindowsMainWindow(_main);
                _window.Closed += OnWindowClosed;
                _window.Activate();
                _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
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

                var paths = LaunchArgs.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
                await _window.OpenPathsAsync(paths);
                NativeStartupLog.Write("startup complete");
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

        /// <summary>关闭窗口:先关设置窗口回写工作副本,再释放 GPU 资源与订阅。</summary>
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _settingsPresenter?.CloseIfOpen();
            _window?.Detach();
            NativeStartupLog.Write("window closed");
        }
    }

    /// <summary>
    /// 原生壳的窗口门面:设置窗口转交 presenter;工具页属 P3,本阶段调用即为漂移,直接报错而不是静默无响应。
    /// </summary>
    private sealed class NativeWindowPresenterImpl : INativeWindowPresenter
    {
        private readonly WindowsNativeSettingsPresenter _settings;

        /// <summary>
        /// 创建窗口门面。
        /// </summary>
        /// <param name="settings">设置窗口展示器</param>
        public NativeWindowPresenterImpl(WindowsNativeSettingsPresenter settings) => _settings = settings;

        /// <inheritdoc/>
        public void ShowSettingsWindow(SettingsViewModel settings) => _settings.TryPresent(settings);

        /// <inheritdoc/>
        public void ShowOrActivateToolsWindow(ToolsViewModel tools)
            => throw new InvalidOperationException("工具页属 P3,原生壳不显示入口。");
    }
}
