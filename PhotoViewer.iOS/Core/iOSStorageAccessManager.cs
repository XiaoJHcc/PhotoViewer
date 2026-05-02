using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 安全作用域存储访问协调器。
/// 负责保留长期授权，并为按需读写创建临时访问作用域。
/// </summary>
public sealed class iOSStorageAccessManager : IPlatformStorageAccessManager
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, NSUrl> RetainedByPath = new(StringComparer.Ordinal);

    /// <summary>
    /// 尝试保留存储项的长期访问权限。
    /// </summary>
    /// <param name="item">目标存储项</param>
    public void Retain(IStorageItem item)
    {
        var path = GetLocalPath(item);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (RetainedByPath.ContainsKey(path))
            {
                return;
            }
        }

        var url = NSUrl.CreateFileUrl(path, null);
        if (url == null)
        {
            return;
        }

        try
        {
            if (!url.StartAccessingSecurityScopedResource())
            {
                return;
            }

            lock (SyncRoot)
            {
                RetainedByPath[path] = url;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS retain skipped for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试获取一次性的访问作用域。
    /// 若该路径已被长期保留，则无需额外创建作用域。
    /// </summary>
    /// <param name="item">目标存储项</param>
    /// <returns>访问作用域；不需要或创建失败时返回 null</returns>
    public IDisposable? TryAcquireScope(IStorageItem item)
    {
        var path = GetLocalPath(item);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (SyncRoot)
        {
            if (RetainedByPath.ContainsKey(path))
            {
                return null;
            }
        }

        var url = NSUrl.CreateFileUrl(path, null);
        if (url == null)
        {
            return null;
        }

        try
        {
            return url.StartAccessingSecurityScopedResource()
                ? new TemporaryScope(url)
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS scope skipped for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将系统 URL 直接登记为长期授权访问。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    public static void RetainUrl(NSUrl? url)
    {
        if (url == null || !url.IsFileUrl)
        {
            return;
        }

        var path = url.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (RetainedByPath.ContainsKey(path))
            {
                return;
            }
        }

        try
        {
            if (!url.StartAccessingSecurityScopedResource())
            {
                return;
            }

            lock (SyncRoot)
            {
                RetainedByPath[path] = url;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS retain url skipped for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// 提取存储项对应的本地路径。
    /// </summary>
    /// <param name="item">目标存储项</param>
    /// <returns>本地路径；不可用时返回 null</returns>
    private static string? GetLocalPath(IStorageItem item)
    {
        var localPath = item.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            return Path.GetFullPath(localPath);
        }

        var itemPath = item.Path?.LocalPath;
        return string.IsNullOrWhiteSpace(itemPath) ? null : Path.GetFullPath(itemPath);
    }

    /// <summary>
    /// 一次性安全作用域访问对象，释放时自动结束访问。
    /// </summary>
    private sealed class TemporaryScope(NSUrl url) : IDisposable
    {
        private NSUrl? _url = url;

        public void Dispose()
        {
            var currentUrl = _url;
            if (currentUrl == null)
            {
                return;
            }

            _url = null;

            try
            {
                currentUrl.StopAccessingSecurityScopedResource();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageAccess] iOS scope release skipped: {ex.Message}");
            }
        }
    }
}