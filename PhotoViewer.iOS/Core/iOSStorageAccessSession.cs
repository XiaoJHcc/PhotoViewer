using Foundation;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 可释放的安全作用域会话。
/// 持有一个根 URL（文件或目录）并按需附加其他 URL，按路径引用计数与系统
/// <c>StartAccessingSecurityScopedResource</c>/<c>StopAccessingSecurityScopedResource</c> 配对：
/// 同一路径重复获取不重复启动，<see cref="Dispose"/> 释放本会话持有的全部计数。
/// Start 与 Stop 始终使用系统交回的同一个 <see cref="NSUrl"/> 实例——按路径重建的 URL 不带沙盒扩展，
/// 启动必然失败，这是外部文件授权失效的根因。
/// </summary>
public sealed class iOSStorageAccessSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ISecurityScopeAccess _scope;
    private bool _disposed;

    /// <summary>创建会话并可同时保留根 URL 的访问权限。</summary>
    /// <param name="rootUrl">根文件或目录 URL；null 表示先建空会话再按需附加。</param>
    /// <exception cref="StorageException">根 URL 无法获取安全作用域访问权限。</exception>
    public iOSStorageAccessSession(NSUrl? rootUrl = null)
        : this(PlatformSecurityScopeAccess.Instance, rootUrl)
    {
    }

    /// <summary>创建会话并注入系统作用域调用面（验收替身用）。</summary>
    /// <param name="scope">系统作用域调用面。</param>
    /// <param name="rootUrl">根文件或目录 URL；null 表示先建空会话再按需附加。</param>
    /// <exception cref="StorageException">根 URL 无法获取安全作用域访问权限。</exception>
    internal iOSStorageAccessSession(ISecurityScopeAccess scope, NSUrl? rootUrl = null)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (rootUrl != null)
        {
            Acquire(rootUrl);
        }
    }

    /// <summary>当前持有访问权限的路径数量（每个路径只计一次）。</summary>
    public int AccessiblePathCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>获取一个 URL 的访问权限；同一路径重复调用只增加本会话的引用计数，不重复启动系统作用域。</summary>
    /// <param name="url">系统授予的文件或目录 URL。</param>
    /// <exception cref="StorageException">URL 非本地文件，或系统拒绝授予访问权限。</exception>
    public void Acquire(NSUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var path = ResolvePath(url);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(path, out var existing))
            {
                existing.References++;
                return;
            }

            bool started;
            try
            {
                started = _scope.Start(url);
            }
            catch (Exception ex)
            {
                throw new StorageException(StorageError.AccessDenied, $"无法获取 {path} 的访问权限：{ex.Message}", ex);
            }

            if (!started)
            {
                throw new StorageException(StorageError.AccessDenied, $"无法获取 {path} 的访问权限");
            }

            _entries[path] = new Entry(url);
        }
    }

    /// <summary>
    /// 尝试获取一个 URL 的访问权限：失败只记录日志并返回 false，由调用方决定回退（沙盒路径不需要作用域）。
    /// </summary>
    /// <param name="url">系统授予的文件或目录 URL。</param>
    /// <returns>本会话已持有该路径的访问权限时返回 true。</returns>
    internal bool TryAcquire(NSUrl url)
    {
        try
        {
            Acquire(url);
            return true;
        }
        catch (StorageException ex)
        {
            Console.WriteLine($"[StorageAccess] {ex.Message}");
            return false;
        }
    }

    /// <summary>释放一个 URL 的一次引用；计数归零时结束该路径的系统作用域。</summary>
    /// <param name="url">先前获取过的 URL。</param>
    /// <returns>该路径仍被本会话持有时返回 true。</returns>
    public bool Release(NSUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!TryGetPath(url, out var path))
        {
            return false;
        }

        NSUrl? released = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var existing))
            {
                return false;
            }

            if (--existing.References > 0)
            {
                return true;
            }

            _entries.Remove(path);
            released = existing.Url;
        }

        Stop(released);
        return false;
    }

    /// <summary>判断给定 URL 的路径是否仍被本会话持有。</summary>
    /// <param name="url">目标 URL。</param>
    /// <returns>持有访问权限时返回 true。</returns>
    public bool Holds(NSUrl url)
    {
        if (!TryGetPath(url, out var path))
        {
            return false;
        }

        lock (_gate)
        {
            return _entries.ContainsKey(path);
        }
    }

    /// <summary>结束本会话持有的全部系统作用域；可重复调用。</summary>
    public void Dispose()
    {
        NSUrl[] released;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            released = _entries.Values.Select(entry => entry.Url).ToArray();
            _entries.Clear();
        }

        foreach (var url in released)
        {
            Stop(url);
        }
    }

    /// <summary>结束一次系统作用域；失败只记录日志，不掩盖释放过程中的其他错误。</summary>
    /// <param name="url">启动作用域时使用的同一 URL 实例。</param>
    private void Stop(NSUrl? url)
    {
        if (url == null)
        {
            return;
        }

        try
        {
            _scope.Stop(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS session release skipped: {ex.Message}");
        }
    }

    /// <summary>解析 URL 的本地绝对路径，非文件 URL 视为不可访问。</summary>
    /// <param name="url">目标 URL。</param>
    /// <returns>规范化的绝对路径。</returns>
    private static string ResolvePath(NSUrl url)
    {
        if (!TryGetPath(url, out var path))
        {
            throw new StorageException(StorageError.AccessDenied, $"不是可访问的本地文件 URL：{url}");
        }

        return path;
    }

    /// <summary>尝试解析 URL 的本地绝对路径。</summary>
    /// <param name="url">目标 URL。</param>
    /// <param name="path">解析结果。</param>
    /// <returns>是本地文件 URL 且路径有效时返回 true。</returns>
    private static bool TryGetPath(NSUrl url, out string path)
    {
        path = string.Empty;
        if (!url.IsFileUrl || string.IsNullOrWhiteSpace(url.Path))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(url.Path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS session ignored invalid path: {ex.Message}");
            return false;
        }
    }

    /// <summary>单个路径的系统作用域与本会话引用计数。</summary>
    private sealed class Entry(NSUrl url)
    {
        /// <summary>启动系统作用域时使用的 URL。</summary>
        public NSUrl Url { get; } = url;

        /// <summary>本会话对该路径的引用次数。</summary>
        public int References { get; set; } = 1;
    }
}
