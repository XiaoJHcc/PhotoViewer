using System;
using System.Collections.Generic;
using System.IO;
using AppKit;
using CoreFoundation;
using Foundation;
using ObjCRuntime;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 外部打开桥接。
/// 负责接收 Finder / Dock / "打开方式" 传入的文件，并转发给共享层。
/// 通过 NSAppleEventManager 注册 'odoc' (Open Documents) Apple Event 处理程序，
/// 不替换 Avalonia 的 NSApplicationDelegate，兼容 macOS 26+。
/// </summary>
public static class MacExternalOpenBridge
{
    private static bool _isInstalled;

    /// <summary>
    /// 安装 macOS 原生文件打开事件处理。
    /// 通过 NSAppleEventManager 注册 kAEOpenDocuments ('odoc') 事件处理程序，
    /// 不涉及 NSApplicationDelegate，避免 macOS 26 的 EnsureUIThread() 限制。
    /// </summary>
    public static void Install()
    {
        if (_isInstalled) return;
        _isInstalled = true;

        var aem = NSAppleEventManager.SharedAppleEventManager;
        // kCoreEventClass = 'aevt' (0x61657674), kAEOpenDocuments = 'odoc' (0x6f646f63)
        aem.SetEventHandler(
            new AppleEventHandler(),
            new Selector("handleOpenDocuments:withReply:"),
            (AEEventClass)0x61657674,
            (AEEventID)0x6f646f63);
    }

    /// <summary>
    /// 将一组本地文件路径发布为外部打开请求。
    /// </summary>
    /// <param name="paths">系统传入的本地路径</param>
    /// <param name="source">请求来源标记</param>
    public static void PublishFromPaths(IEnumerable<string>? paths, string source)
    {
        PublishResolvedUris(ResolveFileUris(paths), source);
    }

    /// <summary>
    /// 将一组 macOS 原生 URL 发布为外部打开请求。
    /// </summary>
    /// <param name="urls">系统传入的 URL</param>
    /// <param name="source">请求来源标记</param>
    public static void PublishFromUrls(IEnumerable<NSUrl>? urls, string source)
    {
        var fileUris = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (urls != null)
        {
            foreach (var url in urls)
            {
                var uri = TryCreateFileUri(url);
                if (uri != null && seen.Add(uri.AbsoluteUri))
                {
                    fileUris.Add(uri);
                }
            }
        }

        PublishResolvedUris(fileUris, source);
    }

    /// <summary>
    /// 统一发布解析成功的文件 URI。
    /// </summary>
    /// <param name="fileUris">已解析的文件 URI</param>
    /// <param name="source">请求来源标记</param>
    private static void PublishResolvedUris(IReadOnlyList<Uri> fileUris, string source)
    {
        if (fileUris.Count == 0)
        {
            return;
        }

        if (fileUris.Count == 1)
        {
            ExternalOpenService.PublishFile(fileUris[0], source);
            return;
        }

        ExternalOpenService.PublishFiles(fileUris, source);
    }

    /// <summary>
    /// 将本地路径列表转换为去重后的 file:// URI 列表。
    /// </summary>
    /// <param name="paths">原始路径集合</param>
    private static List<Uri> ResolveFileUris(IEnumerable<string>? paths)
    {
        var fileUris = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (paths == null)
        {
            return fileUris;
        }

        foreach (var path in paths)
        {
            var uri = TryCreateFileUri(path);
            if (uri != null && seen.Add(uri.AbsoluteUri))
            {
                fileUris.Add(uri);
            }
        }

        return fileUris;
    }

    /// <summary>
    /// 将 macOS 原生 URL 转换为本地文件 URI。
    /// </summary>
    /// <param name="url">系统 URL</param>
    private static Uri? TryCreateFileUri(NSUrl? url)
    {
        if (url == null || !url.IsFileUrl)
        {
            return null;
        }

        return TryCreateFileUri(url.Path);
    }

    /// <summary>
    /// 将本地路径转换为 file:// URI。
    /// </summary>
    /// <param name="path">原始路径</param>
    private static Uri? TryCreateFileUri(string? path)
    {
        try
        {
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
    /// Apple Event 处理器。
    /// 接收 kAEOpenDocuments ('odoc') 事件，从中提取文件 URL 并转发给共享层。
    /// 继承 NSObject 以满足 NSAppleEventManager.SetEventHandler 的要求。
    /// </summary>
    private sealed class AppleEventHandler : NSObject
    {
        /// <summary>
        /// 处理 'odoc' Apple Event，从事件描述符中提取文件 URL 列表。
        /// </summary>
        /// <param name="evt">系统传入的 Apple Event 描述符</param>
        /// <param name="reply">回复描述符（未使用）</param>
        [Export("handleOpenDocuments:withReply:")]
        public void HandleOpenDocuments(NSAppleEventDescriptor evt, NSAppleEventDescriptor reply)
        {
            // 文件列表位于 keyDirectObject ('----') 参数中
            var directObject = evt.ParamDescriptorForKeyword(0x2d2d2d2d); // '----'
            if (directObject == null) return;

            var urls = new List<NSUrl>();
            int count = (int)directObject.NumberOfItems;
            for (int i = 1; i <= count; i++)
            {
                var desc = directObject.DescriptorAtIndex(i);
                var url = desc?.StringValue != null ? new NSUrl(desc.StringValue) : null;
                if (url != null) urls.Add(url);
            }

            if (urls.Count > 0)
            {
                PublishFromUrls(urls, source: "Mac:AppleEvent:odoc");
            }
        }
    }
}
