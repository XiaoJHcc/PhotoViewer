using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Platform;

public enum OpenRequestSource
{
    Picker,
    CommandLine,
    SystemOpen,
    Share,
    DragDrop,
    Automation,
}

public sealed record OpenRequest(IReadOnlyList<IStorageItemRef> Items, OpenRequestSource Source)
{
    public TaskCompletionSource? Completion { get; init; }

    public bool HasSameTargets(OpenRequest other)
    {
        if (Items.Count != other.Items.Count) return false;
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index].StorageId != other.Items[index].StorageId) return false;
            if ((Items[index] is IFolderRef) != (other.Items[index] is IFolderRef)) return false;
        }
        return true;
    }
}

public sealed class OpenRequestChannel
{
    private readonly object _syncRoot = new();
    private readonly Queue<OpenRequest> _pending = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _completed;
    private bool _running;

    public bool Publish(OpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0) return false;
        lock (_syncRoot)
        {
            if (_completed) return false;
            if (request.Completion is null && _pending.Any(item => item.Completion is null && request.HasSameTargets(item)))
                return false;
            _pending.Enqueue(request);
        }
        _available.Release();
        return true;
    }

    public async Task RunAsync(
        Func<OpenRequest, CancellationToken, Task> handler,
        Action<OpenRequest, Exception> onError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(onError);
        lock (_syncRoot)
        {
            if (_running) throw new InvalidOperationException("打开请求只允许一个消费者");
            _running = true;
        }

        try
        {
            while (true)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                OpenRequest request;
                lock (_syncRoot)
                {
                    if (_pending.Count == 0)
                    {
                        if (_completed) return;
                        continue;
                    }
                    request = _pending.Dequeue();
                }

                try
                {
                    await handler(request, cancellationToken).ConfigureAwait(false);
                    request.Completion?.TrySetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    request.Completion?.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception exception)
                {
                    request.Completion?.TrySetException(exception);
                    onError(request, exception);
                }
            }
        }
        finally
        {
            lock (_syncRoot) _running = false;
        }
    }

    public void Complete()
    {
        lock (_syncRoot) _completed = true;
        _available.Release();
    }
}
