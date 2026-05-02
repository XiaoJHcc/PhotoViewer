using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.ViewModels;
using PhotoViewer.Windows;

namespace PhotoViewer;

public partial class App : Application
{
    private static readonly SemaphoreSlim ExternalOpenSemaphore = new(1, 1);
    private static Control? CurrentMobileMainView;

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
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => CreateMobileMainView(vm);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // 移动端 (Android / iOS): 使用新建的 SingleView
            singleViewPlatform.MainView = CreateMobileMainView(vm);
        }

        ExternalOpenService.RegisterHandler(request => HandleExternalOpenRequestAsync(vm, request));

        // 订阅 Avalonia 原生的文件激活事件（macOS: AvnAppDelegate.openFiles/openURLs）。
        // Avalonia 的 NSApplicationDelegate 会拦截系统的"打开方式"事件并通过此接口转发。
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
        {
            activatableLifetime.Activated += (_, e) =>
            {
                if (e is FileActivatedEventArgs fileArgs)
                {
                    _ = HandleFileActivatedAsync(vm, fileArgs);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 处理 Avalonia 原生文件激活事件（macOS "打开方式"、Dock 拖放等）。
    /// Avalonia 已将系统事件解析为 IStorageItem，直接在 UI 线程打开。
    /// </summary>
    private static async Task HandleFileActivatedAsync(MainViewModel vm, FileActivatedEventArgs e)
    {
        await ExternalOpenSemaphore.WaitAsync();

        try
        {
            foreach (var item in e.Files)
            {
                if (item is IStorageFolder folder)
                {
                    await OpenFolderOnUiThreadAsync(vm, folder);
                    return;
                }

                if (item is IStorageFile file && vm.FolderVM.IsImageFile(file.Name))
                {
                    await OpenFileOnUiThreadAsync(vm, file);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"File activated handling failed: {ex.Message}");
        }
        finally
        {
            ExternalOpenSemaphore.Release();
        }
    }

    /// <summary>
    /// 统一处理平台层投递进来的外部打开请求。
    /// </summary>
    private static async Task HandleExternalOpenRequestAsync(MainViewModel vm, ExternalOpenRequest request)
    {
        await ExternalOpenSemaphore.WaitAsync();

        try
        {
            var requiresPathResolution = request.Items.Any(item => item.StorageItem == null);
            var storageProvider = requiresPathResolution ? await WaitForStorageProviderAsync() : null;

            foreach (var item in request.Items)
            {
                if (item.StorageItem is IStorageFolder directFolder)
                {
                    await OpenFolderOnUiThreadAsync(vm, directFolder);
                    return;
                }

                if (item.StorageItem is IStorageFile directFile && vm.FolderVM.IsImageFile(directFile.Name))
                {
                    await OpenFileOnUiThreadAsync(vm, directFile);
                    return;
                }

                if (item.Kind == ExternalOpenItemKind.Folder)
                {
                    var folder = storageProvider == null ? null : await storageProvider.TryGetFolderFromPathAsync(item.Path);
                    if (folder != null)
                    {
                        await OpenFolderOnUiThreadAsync(vm, folder);
                        return;
                    }

                    continue;
                }

                var file = storageProvider == null ? null : await storageProvider.TryGetFileFromPathAsync(item.Path);
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
    /// 创建移动端主视图，并记录当前可用于定位 TopLevel 的根控件。
    /// </summary>
    private static Control CreateMobileMainView(MainViewModel vm)
    {
        var view = new SingleView
        {
            DataContext = vm
        };

        CurrentMobileMainView = view;
        return view;
    }

    /// <summary>
    /// 获取当前激活界面的 TopLevel。
    /// </summary>
    internal static TopLevel? GetCurrentTopLevel()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return TopLevel.GetTopLevel(desktop.MainWindow);
        }

        if (Current?.ApplicationLifetime is IActivityApplicationLifetime)
        {
            return CurrentMobileMainView is null ? null : TopLevel.GetTopLevel(CurrentMobileMainView);
        }

        if (Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            return TopLevel.GetTopLevel(singleView.MainView);
        }

        return null;
    }

    /// <summary>
    /// 获取当前主界面的存储提供器。
    /// </summary>
    private static IStorageProvider? GetStorageProvider()
        => GetCurrentTopLevel()?.StorageProvider;
}