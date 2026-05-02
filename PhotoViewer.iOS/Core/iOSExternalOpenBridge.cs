using System;
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

}
