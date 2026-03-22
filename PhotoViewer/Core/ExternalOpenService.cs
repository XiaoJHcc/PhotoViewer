using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PhotoViewer.Core;

/// <summary>
/// 外部打开项类型。
/// </summary>
public enum ExternalOpenItemKind
{
    File,
    Folder
}

/// <summary>
/// 单个外部打开项。
/// </summary>
public sealed class ExternalOpenItem
{
    public ExternalOpenItem(ExternalOpenItemKind kind, Uri path)
    {
        Kind = kind;
        Path = path;
    }

    public ExternalOpenItemKind Kind { get; }

    public Uri Path { get; }
}

/// <summary>
/// 外部打开请求。
/// </summary>
public sealed class ExternalOpenRequest
{
    public ExternalOpenRequest(IReadOnlyList<ExternalOpenItem> items, string source, bool preferFolderContext = true)
    {
        Items = items;
        Source = source;
        PreferFolderContext = preferFolderContext;
    }

    public IReadOnlyList<ExternalOpenItem> Items { get; }

    public string Source { get; }

    public bool PreferFolderContext { get; }
}

/// <summary>
/// 外部打开服务。
/// 平台层可以在 UI 初始化前先投递请求，应用层就绪后再统一消费。
/// </summary>
public static class ExternalOpenService
{
    private static readonly object SyncRoot = new();
    private static readonly Queue<ExternalOpenRequest> PendingRequests = new();
    private static Func<ExternalOpenRequest, Task>? _handler;

    /// <summary>
    /// 发布单文件打开请求。
    /// </summary>
    public static void PublishFile(Uri path, string source, bool preferFolderContext = true)
    {
        PublishFiles([path], source, preferFolderContext);
    }

    /// <summary>
    /// 发布多文件打开请求。
    /// </summary>
    public static void PublishFiles(IEnumerable<Uri> paths, string source, bool preferFolderContext = true)
    {
        var items = paths
            .Select(path => new ExternalOpenItem(ExternalOpenItemKind.File, path))
            .ToList();

        if (items.Count == 0)
        {
            return;
        }

        Publish(new ExternalOpenRequest(items, source, preferFolderContext));
    }

    /// <summary>
    /// 发布文件夹打开请求。
    /// </summary>
    public static void PublishFolder(Uri path, string source)
    {
        Publish(new ExternalOpenRequest([new ExternalOpenItem(ExternalOpenItemKind.Folder, path)], source));
    }

    /// <summary>
    /// 发布外部打开请求。
    /// </summary>
    public static void Publish(ExternalOpenRequest request)
    {
        Func<ExternalOpenRequest, Task>? handlerToInvoke;

        lock (SyncRoot)
        {
            if (_handler == null)
            {
                PendingRequests.Enqueue(request);
                return;
            }

            handlerToInvoke = _handler;
        }

        _ = handlerToInvoke(request);
    }

    /// <summary>
    /// 注册请求处理器，并立即补消费所有挂起请求。
    /// </summary>
    public static void RegisterHandler(Func<ExternalOpenRequest, Task> handler)
    {
        List<ExternalOpenRequest> pending;

        lock (SyncRoot)
        {
            _handler = handler;
            pending = PendingRequests.ToList();
            PendingRequests.Clear();
        }

        foreach (var request in pending)
        {
            _ = handler(request);
        }
    }
}


