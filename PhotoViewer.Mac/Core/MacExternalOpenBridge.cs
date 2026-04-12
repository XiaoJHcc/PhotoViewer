using System;
using System.Collections.Generic;
using System.IO;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 外部打开桥接。
/// 负责将命令行参数中的文件路径转换为外部打开请求并转发给共享层。
/// "打开方式" / Dock 拖放等系统事件由 Avalonia 原生的 AvnAppDelegate 拦截，
/// 通过 IActivatableLifetime.Activated (FileActivatedEventArgs) 在 App.axaml.cs 中统一处理。
/// </summary>
public static class MacExternalOpenBridge
{

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
}
