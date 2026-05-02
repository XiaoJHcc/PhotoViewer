using System;
using System.IO;
using System.Reflection;
using Avalonia.Platform.Storage;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 外部打开桥接。
/// 负责接收文件 App / 分享菜单传入的文件 URL，并转发给共享层。
/// </summary>
public static class iOSExternalOpenBridge
{

    /// <summary>
    /// 将 iOS 原生文件 URL 发布为外部打开请求。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="source">请求来源标记</param>
    /// <returns>是否成功识别并投递</returns>
    public static bool PublishFromUrl(NSUrl? url, string source)
    {
        var storageItem = TryCreateStorageItem(url);
        if (storageItem != null)
        {
            ExternalOpenService.PublishStorageItem(storageItem, source);
            return true;
        }

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

        iOSStorageAccessManager.RetainUrl(url);

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
    /// 尝试通过 Avalonia.iOS 内部的 IOSStorageItem 工厂创建真实存储项。
    /// 这样可以直接复用 Avalonia 的 security-scoped URL 行为，避免再次经过路径反解。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    /// <returns>成功时返回真实存储项，否则返回 null</returns>
    private static IStorageItem? TryCreateStorageItem(NSUrl? url)
    {
        if (url == null || !url.IsFileUrl)
        {
            return null;
        }

        iOSStorageAccessManager.RetainUrl(url);

        try
        {
            var storageItemType = Type.GetType("Avalonia.iOS.Storage.IOSStorageItem, Avalonia.iOS", throwOnError: false);
            var createItemMethod = storageItemType?.GetMethod(
                "CreateItem",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(NSUrl), typeof(NSUrl)],
                modifiers: null);

            return createItemMethod?.Invoke(null, [url, url]) as IStorageItem;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create Avalonia iOS storage item: {ex.Message}");
            return null;
        }
    }

}
