using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 外部打开协调器：消费 <see cref="MainViewModel.OpenRequests"/>，单个消费者按投递顺序逐个打开。
/// 一批只取首个可打开目标（文件夹或受支持图片），全部状态改动在 UI 线程完成；
/// 重复激活当前目标只重新选中（不再 fit、不再重新枚举），失败统一经 <see cref="MainViewModel.OpenFailed"/> 上报。
/// 每个主窗口一个实例，不持有静态队列或静态处理器。
/// </summary>
public sealed class OpenCoordinator : IDisposable
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private bool _disposed;

    /// <summary>创建协调器并立即启动唯一消费者（界面就绪前的请求已在通道内排队）。</summary>
    /// <param name="main">所属主视图模型。</param>
    public OpenCoordinator(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _consumer = Task.Run(ConsumeAsync);
    }

    public Task PublishAndWaitAsync(
        IReadOnlyList<IStorageItemRef> items,
        OpenRequestSource source,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new OpenRequest(items, source) { Completion = completion };
        if (!_main.OpenRequests.Publish(request))
            throw new InvalidOperationException("打开请求通道已关闭");
        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 重复外部激活落在当前目标时触发：平台壳据此把窗口前置即可，不重新打开。
    /// </summary>
    public event Action? RepeatActivation;

    /// <summary>结束协调器：关闭请求通道、取消消费者，在途打开由文件源会话自行取消。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.OpenRequests.Complete();
        _cancellation.Cancel();
        _ = _consumer.ContinueWith(
            _ => _cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>单消费者循环：顺序 await 每个请求，异常经 <see cref="OpenRequestChannel.RunAsync"/> 统一出口。</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await _main.OpenRequests
                .RunAsync(HandleAsync, ReportFailure, _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // 关闭时的取消不是失败。
        }
        catch (Exception exception)
        {
            ReportFailure(null, exception);
        }
    }

    /// <summary>把单个请求封送到 UI 线程执行；调用方的顺序等待保证同一批只处理一个目标。</summary>
    private Task HandleAsync(OpenRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        return UiDispatcher.InvokeAsync(() => OpenAsync(request, cancellationToken)).Unwrap();
    }

    private async Task OpenAsync(OpenRequest request, CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested) return;

        var target = SelectTarget(request);
        if (target == null)
        {
            // 不支持的类型、空批次或无法识别的 URL：忽略并保持现状（OPEN-06）。
            Console.WriteLine($"[Open] 请求没有可打开目标（{request.Source}）");
            return;
        }

        switch (target)
        {
            case IFolderRef folder:
                if (HasSameStorageId(folder, _main.FolderVM.CurrentFolder))
                {
                    RepeatActivation?.Invoke();
                    return;
                }
                await _main.FolderVM.OpenFolderAsync(folder, resetView: true);
                return;

            case IFileRef file:
                var current = _main.CurrentFile;
                if (current != null &&
                    (HasSameStorageId(file, current.File) ||
                     current.Group?.Files.Any(member => HasSameStorageId(file, member.File)) == true))
                {
                    RepeatActivation?.Invoke();
                    return;
                }
                await _main.FolderVM.OpenImageAsync(file, resetView: true);
                return;
        }
    }

    /// <summary>按投递顺序取首个可打开项：文件夹或当前启用格式的图片。</summary>
    private IStorageItemRef? SelectTarget(OpenRequest request)
    {
        foreach (var item in request.Items)
        {
            switch (item)
            {
                case IFolderRef:
                    return item;
                case IFileRef file when _main.FolderVM.IsImageFile(file.Name):
                    return item;
            }
        }

        return null;
    }

    /// <summary>失败统一出口：日志 + UI 线程上的 STATE-03 文案，不静默、不产生未观察异常。</summary>
    private void ReportFailure(OpenRequest? request, Exception exception)
    {
        Console.Error.WriteLine($"[Open] 外部打开失败（{request?.Source.ToString() ?? "unknown"}）：{exception}");
        var name = request == null ? null : TargetName(request);
        try
        {
            UiDispatcher.Post(() => _main.ReportOpenFailure(StatusMessages.OpenFailed(name, exception.Message)));
        }
        catch (Exception dispatchFailure)
        {
            Console.Error.WriteLine($"[Open] 失败提示无法投递：{dispatchFailure.Message}");
        }
    }

    private static string? TargetName(OpenRequest request)
    {
        foreach (var item in request.Items)
        {
            return item.Name;
        }

        return null;
    }

    private static bool HasSameStorageId(IStorageItemRef? left, IStorageItemRef? right)
        => left != null && right != null && left.StorageId == right.StorageId;
}
