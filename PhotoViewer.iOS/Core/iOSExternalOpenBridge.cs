using System;
using System.Collections.Generic;
using System.IO;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 外部打开桥接。
/// 负责接收文件 App / 分享菜单传入的文件 URL，解析为本地路径并保留安全作用域，
/// 再以 iOS 自己的存储引用投递给共享外部打开服务。
/// </summary>
public static class iOSExternalOpenBridge
{
    /// <summary>
    /// 将 iOS 原生文件 URL 发布为外部打开请求。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="source">请求来源标记</param>
    /// <returns>是否成功识别并投递</returns>
    public static bool PublishNativeFromUrl(NSUrl? url, string source)
        => PublishNativeFromUrls(url == null ? [] : [url], source);

    /// <summary>
    /// 将一批 iOS 原生文件 URL 发布为一次外部打开请求（同一请求内的项按顺序尝试打开）。
    /// </summary>
    /// <param name="urls">系统传入的文件 URL 集合</param>
    /// <param name="source">请求来源标记</param>
    /// <returns>是否成功投递至少一项</returns>
    public static bool PublishNativeFromUrls(IEnumerable<NSUrl> urls, string source)
    {
        var items = new List<ExternalOpenItem>();
        foreach (var url in urls)
        {
            var path = TryResolveLocalPath(url);
            if (path == null)
            {
                continue;
            }

            IStorageItemRef item = Directory.Exists(path) ? new iOSFolderRef(path) : new iOSFileRef(path);
            items.Add(new ExternalOpenItem(item));
        }

        if (items.Count == 0)
        {
            return false;
        }

        ExternalOpenService.Publish(new ExternalOpenRequest(items, source));
        return true;
    }

    /// <summary>
    /// 解析文件 URL 的本地绝对路径，并顺带保留安全作用域（后续按路径读取依赖它）。
    /// </summary>
    /// <param name="url">系统传入的 URL</param>
    /// <returns>本地绝对路径；非法或目标不存在时返回 null</returns>
    private static string? TryResolveLocalPath(NSUrl? url)
    {
        if (url == null || !url.IsFileUrl)
        {
            return null;
        }

        iOSStorageAccessManager.RetainUrl(url);

        try
        {
            var path = url.Path;
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return null;
            }

            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
