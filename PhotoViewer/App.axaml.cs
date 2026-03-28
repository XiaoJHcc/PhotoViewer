using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.ViewModels;
using PhotoViewer.Windows;

namespace PhotoViewer;

public partial class App : Application
{
    private static readonly SemaphoreSlim ExternalOpenSemaphore = new(1, 1);

    /// <summary>
    /// 平台层可在 AfterSetup 阶段注册此回调，用于在框架完全初始化后执行
    /// 需要 AppKit/UI 线程就绪才能进行的平台特定初始化（如 macOS NSApplicationDelegate 安装）。
    /// </summary>
    public static Action? PlatformFrameworkReadyCallback { get; set; }

    /// <summary>
    /// 初始化应用资源。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成后创建主视图，并注册外部打开请求处理器。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // 执行平台层注册的延迟初始化回调。
        // 各平台实现内部负责将需要特定线程上下文的操作调度到正确线程。
        PlatformFrameworkReadyCallback?.Invoke();
        PlatformFrameworkReadyCallback = null;

        var vm = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: 使用带自定义标题栏的 MainWindow
                desktop.MainWindow = new MainWindowForWindows
                {
                    DataContext = vm
                };
            }
            else
            {
                // 其他桌面 (Mac): 使用系统原生 MainWindow
                desktop.MainWindow = new MainWindowForMac
                {
                    DataContext = vm
                };
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // 移动端 (Android / iOS): 使用新建的 SingleView
            singleViewPlatform.MainView = new SingleView
            {
                DataContext = vm
            };
        }

        ExternalOpenService.RegisterHandler(request => HandleExternalOpenRequestAsync(vm, request));

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 统一处理平台层投递进来的外部打开请求。
    /// </summary>
    private static async Task HandleExternalOpenRequestAsync(MainViewModel vm, ExternalOpenRequest request)
    {
        await ExternalOpenSemaphore.WaitAsync();

        try
        {
            var storageProvider = await WaitForStorageProviderAsync();
            if (storageProvider == null)
            {
                Console.WriteLine($"External open skipped: storage provider unavailable ({request.Source})");
                return;
            }

            foreach (var item in request.Items)
            {
                if (item.Kind == ExternalOpenItemKind.Folder)
                {
                    var folder = await storageProvider.TryGetFolderFromPathAsync(item.Path);
                    if (folder != null)
                    {
                        await OpenFolderOnUiThreadAsync(vm, folder);
                        return;
                    }

                    continue;
                }

                var file = await storageProvider.TryGetFileFromPathAsync(item.Path);
                if (file == null || !vm.FolderVM.IsImageFile(file.Name))
                {
                    continue;
                }

                await OpenFileOnUiThreadAsync(vm, file);
                return;
            }

            var firstPath = request.Items.FirstOrDefault()?.Path;
            Console.WriteLine($"External open ignored: no supported item resolved ({request.Source}, {firstPath})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"External open failed ({request.Source}): {ex.Message}");
        }
        finally
        {
            ExternalOpenSemaphore.Release();
        }
    }

    /// <summary>
    /// 在 UI 线程中打开文件夹。
    /// </summary>
    private static async Task OpenFolderOnUiThreadAsync(MainViewModel vm, IStorageFolder folder)
    {
        await Dispatcher.UIThread.InvokeAsync(async () => await vm.FolderVM.OpenFolderAsync(folder));
    }

    /// <summary>
    /// 在 UI 线程中打开图片文件。
    /// </summary>
    private static async Task OpenFileOnUiThreadAsync(MainViewModel vm, IStorageFile file)
    {
        await Dispatcher.UIThread.InvokeAsync(async () => await vm.FolderVM.OpenImageAsync(file));
    }

    /// <summary>
    /// 等待主界面的存储提供器可用。
    /// </summary>
    private static async Task<IStorageProvider?> WaitForStorageProviderAsync()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var storageProvider = await Dispatcher.UIThread.InvokeAsync(GetStorageProvider);
            if (storageProvider != null)
            {
                return storageProvider;
            }

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>
    /// 获取当前主界面的存储提供器。
    /// </summary>
    private static IStorageProvider? GetStorageProvider()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return TopLevel.GetTopLevel(desktop.MainWindow)?.StorageProvider;
        }

        if (Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            return TopLevel.GetTopLevel(singleView.MainView)?.StorageProvider;
        }

        return null;
    }
}