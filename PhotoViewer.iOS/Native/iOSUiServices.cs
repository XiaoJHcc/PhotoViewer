using System.Reactive.Concurrency;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using UIKit;
using UniformTypeIdentifiers;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// UIKit 主线程调度器，供共享 Core/ReactiveUI 使用；原生壳不借用 Avalonia 调度器。
/// </summary>
public sealed class iOSUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context = new iOSSynchronizationContext();

    /// <summary>建立可供 Rx 使用的主线程调度器。</summary>
    public iOSUiDispatcher() { MainThreadScheduler = new SynchronizationContextScheduler(_context); }

    /// <summary>主线程 Rx 调度器。</summary>
    public IScheduler MainThreadScheduler { get; }

    /// <summary>判断 UIKit 主线程。</summary>
    public bool CheckAccess() => NSThread.IsMain;

    /// <summary>投递主线程操作；后台回调同样异步进入队列，不阻塞调用线程。</summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级（与 Mac 原生壳同口径：均投递到主队列，不做优先级分层）。</param>
    public void Post(Action action, UiPriority priority = UiPriority.Normal) => NSRunLoop.Main.BeginInvokeOnMainThread(action);

    /// <summary>等待主线程操作完成并传播异常。</summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal) => InvokeAsync(() => { action(); return true; }, priority);

    /// <summary>等待主线程函数并返回结果。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <param name="priority">调度优先级。</param>
    public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
    {
        if (CheckAccess()) return Task.FromResult(func());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { completion.SetResult(func()); } catch (Exception exception) { completion.SetException(exception); } }, priority);
        return completion.Task;
    }

    /// <summary>在创建 VM 前安装 .NET 原生主线程上下文。</summary>
    public void InstallSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(_context);

    /// <summary>将 .NET 异步续体封送到 UIKit 主队列，不借用 Avalonia 调度器。</summary>
    private sealed class iOSSynchronizationContext : SynchronizationContext
    {
        /// <summary>异步投递 .NET 回调。</summary>
        /// <param name="callback">回调委托。</param>
        /// <param name="state">回调状态。</param>
        public override void Post(SendOrPostCallback callback, object? state) => NSRunLoop.Main.BeginInvokeOnMainThread(() => callback(state));

        /// <summary>同步封送回调，主线程直接执行，避免主队列同步等待自锁。</summary>
        /// <param name="callback">回调委托。</param>
        /// <param name="state">回调状态。</param>
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (NSThread.IsMain) callback(state);
            else CoreFoundation.DispatchQueue.MainQueue.DispatchSync(() => callback(state));
        }

        /// <summary>创建同一平台语义的上下文副本。</summary>
        public override SynchronizationContext CreateCopy() => new iOSSynchronizationContext();
    }
}

/// <summary>
/// 原生文件/目录选择器。
/// 移动端主路径是选文件夹（与旧壳 <c>FolderViewModel.OpenFilePickerAsync</c> 同口径），
/// 单文件选择保留同一实现口径供共享接口使用。
/// </summary>
public sealed class iOSFilePicker : IFilePickerService
{
    /// <summary>选择器的委托必须由本类持有：UIKit 的 delegate 是弱引用，只挂在选择器上会被回收。</summary>
    private PickerDelegate? _activeDelegate;

    /// <summary>弹出文档选择器选择一个目录，并保留其安全作用域供整个会话读取。</summary>
    /// <param name="title">标题（iOS 文档选择器不展示自定义标题）。</param>
    /// <returns>用户选择的文件夹；取消或宿主不可用时返回 null。</returns>
    public async Task<IFolderRef?> PickFolderAsync(string title)
    {
        var url = await PickUrlAsync([UTTypes.Folder]);
        return url?.Path is { } path ? new iOSFolderRef(path) : null;
    }

    /// <summary>弹出文档选择器选择单个图片文件；格式合法性仍由共享 FolderVM 按配置校验。</summary>
    /// <param name="title">标题（iOS 文档选择器不展示自定义标题）。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件；取消或宿主不可用时返回 null。</returns>
    public async Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        var url = await PickUrlAsync(BuildContentTypes(filters));
        return url?.Path is { } path ? new iOSFileRef(path) : null;
    }

    /// <summary>把共享过滤项换算成系统类型（无扩展名可换算时退回图片类型）。</summary>
    /// <param name="filters">共享层过滤项。</param>
    /// <returns>去重后的系统类型数组。</returns>
    private static UTType[] BuildContentTypes(IReadOnlyList<FilePickFilter> filters)
    {
        var types = new List<UTType>();
        foreach (var filter in filters)
        {
            if (filter.IsGenericImage)
            {
                types.Add(UTTypes.Image);
                continue;
            }

            foreach (var extension in filter.Extensions)
            {
                if (UTType.CreateFromExtension(extension.TrimStart('*', '.')) is { } type && !types.Contains(type))
                {
                    types.Add(type);
                }
            }
        }

        return types.Count > 0 ? types.ToArray() : [UTTypes.Image];
    }

    /// <summary>弹出文档选择器并等待选择结果。</summary>
    /// <param name="contentTypes">可选类型。</param>
    /// <returns>选中的 URL；取消或宿主不可用时返回 null。</returns>
    private Task<NSUrl?> PickUrlAsync(UTType[] contentTypes)
    {
        var completion = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (iOSViewControllerLocator.GetTopMostViewController() is not { } host)
        {
            Console.WriteLine("[Native] 文档选择器无可展示宿主，已取消");
            completion.SetResult(null);
            return completion.Task;
        }

        var picker = new UIDocumentPickerViewController(contentTypes, asCopy: false);
        picker.AllowsMultipleSelection = false;
        var pickerDelegate = new PickerDelegate(url =>
        {
            _activeDelegate = null;
            completion.SetResult(url);
        });
        _activeDelegate = pickerDelegate;
        picker.Delegate = pickerDelegate;
        host.PresentViewController(picker, true, null);
        return completion.Task;
    }

    /// <summary>文档选择器回调；选中时立即保留安全作用域，保证后续按路径读取可用。</summary>
    private sealed class PickerDelegate(Action<NSUrl?> completed) : UIDocumentPickerDelegate
    {
        /// <summary>选择完成后回调（多选选择器返回首个 URL，本类只允许单选）。</summary>
        /// <param name="controller">选择器。</param>
        /// <param name="urls">选中的 URL 集合。</param>
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            var url = urls.FirstOrDefault();
            if (url != null) iOSStorageAccessManager.RetainUrl(url);
            completed(url);
        }

        /// <summary>用户取消选择。</summary>
        /// <param name="controller">选择器。</param>
        public override void WasCancelled(UIDocumentPickerViewController controller) => completed(null);
    }
}

/// <summary>
/// 原生壳打包资源读取：从 app bundle 读模型等资源。
/// 旧壳经 Avalonia avares 资源读取，原生壳不加载 Avalonia，故模型按 BundleResource 拷入 bundle（与 Mac 同口径）。
/// </summary>
public sealed class iOSAppResources : IAppResourceProvider
{
    /// <summary>读取 app bundle 内资源，缺失由共享模型资源服务报告。</summary>
    /// <param name="uri">共享资源 URI（avares:// 形式，路径部分即 bundle 内相对路径）。</param>
    /// <returns>资源流；不存在返回 null。</returns>
    public Stream? OpenAsset(Uri uri)
    {
        string relative = uri.AbsolutePath.TrimStart('/');
        string path = System.IO.Path.Combine(NSBundle.MainBundle.ResourcePath, relative);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }
}

/// <summary>
/// 共享窗口门面在 iOS 原生壳的实现：设置页走原生半屏浮窗；
/// 工具页（EXIF 详情 / DINO 诊断）留待 I5 里程碑，调用即显式失败而非静默无效。
/// </summary>
public sealed class iOSWindowPresenter(iOSNativeSettingsPresenter settings) : INativeWindowPresenter
{
    /// <summary>展示原生设置页。</summary>
    /// <param name="viewModel">共享设置 ViewModel。</param>
    public void ShowSettingsWindow(SettingsViewModel viewModel) => settings.TryPresent(viewModel);

    /// <summary>iOS 原生工具页尚未实现（I5 里程碑）。</summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    public void ShowOrActivateToolsWindow(ToolsViewModel tools)
        => throw new NotSupportedException("iOS 原生工具页未实现（I5 里程碑）：EXIF 详情与 DINO 诊断届时接入 UIKit 承载页。");
}
