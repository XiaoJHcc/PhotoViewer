using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
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
            DebugSettings.XamlResourceReferenceFailed += (_, e) =>
                NativeStartupLog.Write("xaml resource failed: " + e.Message);
            DebugSettings.BindingFailed += (_, e) =>
                NativeStartupLog.Write("xaml binding failed: " + e.Message);
            WindowsNativeApplication.InitializeFacades(
                smoke || settingsCheck || settingsHold || toolsCheck || toolsHold);

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
