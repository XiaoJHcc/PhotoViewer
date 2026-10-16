using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using Windows.Storage.Pickers;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 把 .NET 的 async/await 续体排到 WinUI 的 DispatcherQueue 上。
/// 原生壳不创建 Avalonia,因此必须自己安装这个上下文,否则 await 之后的代码会跑到线程池上。
/// </summary>
internal sealed class WindowsSynchronizationContext : SynchronizationContext
{
    private readonly DispatcherQueue _queue;

    /// <summary>
    /// 创建同步上下文。
    /// </summary>
    /// <param name="queue">UI 线程的 DispatcherQueue</param>
    public WindowsSynchronizationContext(DispatcherQueue queue) => _queue = queue;

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.TryEnqueue(() => d(state)))
        {
            throw new InvalidOperationException("DispatcherQueue 已关闭,无法投递续体。");
        }
    }

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_queue.HasThreadAccess)
        {
            d(state);
            return;
        }

        using var completed = new ManualResetEventSlim(false);
        Exception? failure = null;
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    completed.Set();
                }
            }))
        {
            throw new InvalidOperationException("DispatcherQueue 已关闭,无法同步调用。");
        }

        completed.Wait();
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => new WindowsSynchronizationContext(_queue);
}

/// <summary>
/// <see cref="IUiDispatcher"/> 的 Windows 实现:唯一跨线程入口。
/// </summary>
internal sealed class WindowsUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    /// <summary>
    /// 创建调度器。
    /// </summary>
    /// <param name="queue">UI 线程的 DispatcherQueue</param>
    public WindowsUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue;
        MainThreadScheduler = new SynchronizationContextScheduler(new WindowsSynchronizationContext(queue));
    }

    /// <inheritdoc/>
    public IScheduler MainThreadScheduler { get; }

    /// <inheritdoc/>
    public bool CheckAccess() => _queue.HasThreadAccess;

    /// <inheritdoc/>
    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_queue.TryEnqueue(ToQueuePriority(priority), () => action()))
        {
            throw new InvalidOperationException("DispatcherQueue 已关闭,无法投递工作项。");
        }
    }

    /// <inheritdoc/>
    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        }, priority);
    }

    /// <inheritdoc/>
    public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (_queue.HasThreadAccess && priority == UiPriority.Normal)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(ToQueuePriority(priority), () =>
            {
                try
                {
                    completion.SetResult(func());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("DispatcherQueue 已关闭,无法投递工作项。"));
        }

        return completion.Task;
    }

    /// <summary>将共享调度优先级转换为 WinUI 队列优先级，未知值显式报错。</summary>
    /// <param name="priority">共享调度优先级。</param>
    /// <returns>WinUI 队列优先级。</returns>
    private static DispatcherQueuePriority ToQueuePriority(UiPriority priority) => priority switch
    {
        UiPriority.Normal => DispatcherQueuePriority.Normal,
        UiPriority.Background => DispatcherQueuePriority.Low,
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };
}

/// <summary>
/// 原生文件/文件夹选择器:WinRT 选择器在非打包应用里必须先绑定窗口句柄。
/// </summary>
internal sealed class WindowsFilePicker : IFilePickerService
{
    /// <summary>通用图片项展开用的扩展名(Windows 选择器只接受显式扩展名)。</summary>
    private static readonly string[] GenericImageExtensions =
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".hif", ".avif",
        ".tif", ".tiff", ".webp", ".bmp", ".gif", ".arw", ".cr2", ".cr3",
        ".nef", ".nrw", ".raf", ".rw2", ".orf", ".dng", ".pef", ".srw",
    };

    private readonly Func<IntPtr> _windowHandleProvider;

    /// <summary>
    /// 创建选择器。
    /// </summary>
    /// <param name="windowHandleProvider">返回主窗口句柄;选择器必须绑定它才能在前台弹出</param>
    public WindowsFilePicker(Func<IntPtr> windowHandleProvider) => _windowHandleProvider = windowHandleProvider;

    /// <inheritdoc/>
    public async Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var filter in filters)
        {
            foreach (var extension in ResolveExtensions(filter))
            {
                picker.FileTypeFilter.Add(extension);
            }
        }

        if (picker.FileTypeFilter.Count == 0)
        {
            foreach (var extension in GenericImageExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        var file = await picker.PickSingleFileAsync();
        return file == null ? null : new LocalFileRef(file.Path);
    }

    /// <inheritdoc/>
    public async Task<IFolderRef?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());
        var folder = await picker.PickSingleFolderAsync();
        return folder == null ? null : new LocalFolderRef(folder.Path);
    }

    /// <summary>
    /// 把共享层的过滤器转成 Windows 选择器需要的显式扩展名。
    /// </summary>
    /// <param name="filter">共享层过滤器</param>
    /// <returns>扩展名集合(形如 ".jpg")</returns>
    private static IEnumerable<string> ResolveExtensions(FilePickFilter filter)
    {
        if (filter.IsGenericImage)
        {
            return GenericImageExtensions;
        }

        var extensions = new List<string>(filter.Extensions.Count);
        foreach (var extension in filter.Extensions)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            extensions.Add(extension.StartsWith('.') ? extension : "." + extension);
        }

        return extensions;
    }
}

/// <summary>
/// 原生壳的资源读取：将共享 avares URI 映射到 Windows 头工程的清单资源。
/// 不依赖运行目录、松散文件或单文件发布的解压位置，也不初始化 Avalonia。
/// </summary>
internal sealed class WindowsAppResources : IAppResourceProvider
{
    /// <inheritdoc/>
    public Stream? OpenAsset(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("PhotoViewer", StringComparison.OrdinalIgnoreCase))
        {
            NativeStartupLog.Write($"[资源] 不支持的应用资源 URI:{uri}");
            return null;
        }

        var resourceName = "PhotoViewer.Desktop.NativeAssets." + uri.AbsolutePath.TrimStart('/').Replace('/', '.');
        var stream = typeof(WindowsAppResources).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            NativeStartupLog.Write($"[资源] 缺少清单资源 {resourceName}");
        }

        return stream;
    }
}
