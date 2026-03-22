using System;
using System.Collections.Generic;
using System.IO;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 外部打开桥接。
/// 负责接收文件 App / 分享菜单传入的文件 URL，并转发给共享层。
/// </summary>
public static class iOSExternalOpenBridge
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, NSUrl> ActiveSecurityScopedUrls = new(StringComparer.Ordinal);

    /// <summary>
    /// 将 iOS 原生文件 URL 发布为外部打开请求。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="source">请求来源标记</param>
    /// <returns>是否成功识别并投递</returns>
    public static bool PublishFromUrl(NSUrl? url, string source)
    {
        var uri = TryCreateFileUri(url);
        if (uri == null)
        {
            return false;
        }

        ExternalOpenService.PublishFile(uri, source);
        return true;
    }

    /// <summary>
    /// 将 iOS 原生 URL 转换为本地文件 URI。
    /// </summary>
    /// <param name="url">系统传入的 URL</param>
    private static Uri? TryCreateFileUri(NSUrl? url)
    {
        if (url == null || !url.IsFileUrl)
        {
            return null;
        }

        TryRetainSecurityScopedAccess(url);

        try
        {
            var path = url.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return new Uri(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 尝试保留安全作用域资源访问权限。
    /// 某些文件提供者只授予 URL 级访问；保留访问可提高后续单图回退成功率。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    private static void TryRetainSecurityScopedAccess(NSUrl url)
    {
        var key = url.AbsoluteString;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (ActiveSecurityScopedUrls.ContainsKey(key))
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
                ActiveSecurityScopedUrls[key] = url;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"StartAccessingSecurityScopedResource skipped: {ex.Message}");
        }
    }
}
