using AppKit;
using Foundation;
using System.Reactive.Concurrency;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 主线程调度器，供共享 Core/ReactiveUI 使用。</summary>
public sealed class MacUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context = new MacSynchronizationContext();
    /// <summary>建立可供 Rx 使用的主线程调度器。</summary>
    public MacUiDispatcher() { MainThreadScheduler = new SynchronizationContextScheduler(_context); }
    /// <summary>主线程 Rx 调度器。</summary>
    public IScheduler MainThreadScheduler { get; }
    /// <summary>判断 AppKit 主线程。</summary>
    public bool CheckAccess() => NSThread.IsMain;
    /// <summary>投递主线程操作；后台回调同样异步进入队列，不阻塞调用线程。</summary>
    public void Post(Action action, UiPriority priority = UiPriority.Normal) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
    /// <summary>等待主线程操作完成并传播异常。</summary>
    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal) => InvokeAsync(() => { action(); return true; }, priority);
    /// <summary>等待主线程函数并返回结果。</summary>
    public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
    {
        if (CheckAccess()) return Task.FromResult(func());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { completion.SetResult(func()); } catch (Exception exception) { completion.SetException(exception); } }, priority);
        return completion.Task;
    }

    /// <summary>将 .NET 异步续体封送到 AppKit。</summary>
    private sealed class MacSynchronizationContext : SynchronizationContext
    {
        /// <summary>异步投递 .NET 回调。</summary>
        public override void Post(SendOrPostCallback callback, object? state) => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => callback(state));
        /// <summary>同步封送回调，主线程直接执行。</summary>
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (NSThread.IsMain) callback(state);
            else NSApplication.SharedApplication.InvokeOnMainThread(() => callback(state));
        }
        /// <summary>创建同一平台语义的上下文副本。</summary>
        public override SynchronizationContext CreateCopy() => new MacSynchronizationContext();
    }

    /// <summary>在创建 VM 前安装 .NET 原生主线程上下文。</summary>
    public void InstallSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(_context);
}

/// <summary>原生文件/目录选择器。</summary>
public sealed class MacFilePicker : IFilePickerService
{
    /// <summary>以原生 OpenPanel 选择一个目录。</summary>
    public Task<IFolderRef?> PickFolderAsync(string title)
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.Title = title;
        panel.CanChooseDirectories = true;
        panel.CanChooseFiles = false;
        panel.AllowsMultipleSelection = false;
        return Task.FromResult<IFolderRef?>(panel.RunModal() == 1 && panel.Url?.Path is { } path ? new Core.MacFolderRef(path) : null);
    }
    /// <summary>选择图片文件，后续格式合法性由共享 FolderVM 按配置校验。</summary>
    public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.Title = title;
        panel.CanChooseDirectories = false;
        panel.CanChooseFiles = true;
        panel.AllowsMultipleSelection = false;
        return Task.FromResult<IFileRef?>(panel.RunModal() == 1 && panel.Url?.Path is { } path ? new Core.MacFileRef(path) : null);
    }
}

/// <summary>原生壳打包资源读取，模型路径沿用共享资源 URI。</summary>
public sealed class MacAppResources : IAppResourceProvider
{
    /// <summary>读取 app bundle 内资源，缺失由共享模型资源服务报告。</summary>
    public Stream? OpenAsset(Uri uri)
    {
        string relative = uri.AbsolutePath.TrimStart('/');
        string path = System.IO.Path.Combine(NSBundle.MainBundle.ResourcePath, relative);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }
}
