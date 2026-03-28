using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AppKit;
using CoreFoundation;
using Foundation;
using PhotoViewer.Core;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 外部打开桥接。
/// 负责接收 Finder / Dock / "打开方式" 传入的文件，并转发给共享层。
/// </summary>
public static class MacExternalOpenBridge
{
    private static MacExternalOpenApplicationDelegate? _applicationDelegate;
    private static bool _isInstalled;

    /// <summary>
    /// 安装 macOS 原生文件打开委托。
    /// 必须在 Avalonia 框架初始化完成后（OnFrameworkInitializationCompleted）调用。
    /// 内部会通过 GCD 主队列异步调度，确保在正确线程上下文中完成安装。
    /// </summary>
    public static void Install()
    {
        if (_isInstalled)
        {
            return;
        }

        // NSApplicationDelegate 的 C# 托管构造函数内部调用 NSApplication.EnsureUIThread()，
        // 要求代码在 GCD 主队列中执行。Avalonia 的调用线程并非 GCD 主队列，
        // 因此通过 DispatchQueue.MainQueue.DispatchAsync 延迟到主队列消息循环运行后执行。
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try
            {
                if (_isInstalled) return;

                // macOS 26 Tahoe (beta) 加强了 EnsureUIThread() 的检查：
                // 即使在 GCD 主队列上，托管构造函数 NSApplicationDelegate..ctor() 内的
                // EnsureUIThread() 仍可能失败。
                // 解决方案：通过 ObjC runtime 的 alloc+init 直接创建实例，
                // 完全绕过 C# 托管构造函数，再用 Runtime.GetNSObject<T> 包装为托管对象。
                var cls = ObjCRuntime.Class.GetHandle(typeof(MacExternalOpenApplicationDelegate));
                if (cls == IntPtr.Zero)
                {
                    Console.Error.WriteLine("[MacExternalOpenBridge] ObjC class handle not found.");
                    return;
                }

                var allocSel = ObjCRuntime.Selector.GetHandle("alloc");
                var initSel  = ObjCRuntime.Selector.GetHandle("init");

                var allocPtr = ObjC_msgSend(cls, allocSel);
                var initPtr  = ObjC_msgSend(allocPtr, initSel);

                _applicationDelegate = ObjCRuntime.Runtime.GetNSObject<MacExternalOpenApplicationDelegate>(initPtr);

                if (_applicationDelegate == null)
                {
                    Console.Error.WriteLine("[MacExternalOpenBridge] 委托实例化失败。");
                    return;
                }

                NSApplication.SharedApplication.Delegate = _applicationDelegate;
                _isInstalled = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MacExternalOpenBridge.Install] 安装失败: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
        });
    }

    /// <summary>
    /// ObjC runtime 消息发送函数，接受 receiver 和 selector 两个参数。
    /// 用于绕过 Xamarin/MAUI 托管绑定的构造函数检查。
    /// </summary>
    /// <param name="receiver">ObjC 对象指针（类指针或实例指针）</param>
    /// <param name="selector">选择子指针</param>
    /// <returns>消息调用的返回值（对象指针）</returns>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjC_msgSend(IntPtr receiver, IntPtr selector);

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
    /// macOS 应用委托。
    /// 负责把系统"打开文档"事件转交给桥接层。
    /// 标记为 internal + [Register] 以确保 ObjC 静态/动态注册器均能通过
    /// Class.GetHandle(typeof(...)) 可靠找到该类型，从而支持 alloc+init 绕过方式。
    /// </summary>
    [Register("PhotoViewerMacExternalOpenDelegate")]
    internal sealed class MacExternalOpenApplicationDelegate : NSApplicationDelegate
    {
        /// <summary>
        /// 处理单文件打开事件。
        /// </summary>
        /// <param name="sender">当前应用</param>
        /// <param name="filename">系统传入的文件路径</param>
        /// <returns>是否成功识别并投递</returns>
        public override bool OpenFile(NSApplication sender, string filename)
        {
            var uri = TryCreateFileUri(filename);
            if (uri == null)
            {
                return false;
            }

            ExternalOpenService.PublishFile(uri, source: "Mac:OpenFile");
            return true;
        }

        /// <summary>
        /// 处理多文件打开事件。
        /// </summary>
        /// <param name="sender">当前应用</param>
        /// <param name="filenames">系统传入的文件路径列表</param>
        public override void OpenFiles(NSApplication sender, string[] filenames)
        {
            PublishFromPaths(filenames, source: "Mac:OpenFiles");
        }

        /// <summary>
        /// 处理 URL 形式的打开事件。
        /// </summary>
        /// <param name="application">当前应用</param>
        /// <param name="urls">系统传入的 URL 列表</param>
        public override void OpenUrls(NSApplication application, NSUrl[] urls)
        {
            PublishFromUrls(urls, source: "Mac:OpenUrls");
        }
    }
}
