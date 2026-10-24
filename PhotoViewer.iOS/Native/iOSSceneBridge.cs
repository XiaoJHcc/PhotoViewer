using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// scene 窗口与外部打开的接线。
/// 系统的 scene 连接统一交给原生壳根控制器；文件 URL 以一批原生存储引用投递到共享打开通道。
/// </summary>
internal static class iOSSceneBridge
{
    /// <summary>
    /// 为 scene 建立窗口并挂上原生壳的根视图。
    /// </summary>
    /// <param name="windowScene">正在连接的窗口场景。</param>
    /// <returns>已挂好根视图、可直接上屏的窗口。</returns>
    public static UIWindow Connect(UIWindowScene windowScene)
    {
        if (iOSNativeShell.RootViewController is not { } nativeRoot)
        {
            throw new InvalidOperationException("scene 连接时原生壳根控制器尚未接线。");
        }

        // 应用固定深色（Info.plist 的 UIUserInterfaceStyle 是应用级口径，窗口再显式声明一次，
        // 保证窗口及其弹出的所有控制器都不随系统浅色解析动态色）。
        var window = new UIWindow(windowScene)
        {
            RootViewController = nativeRoot,
            OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark,
        };
        BootTrace.Mark("scene bridge: 原生壳接窗口");
        return window;
    }

    /// <summary>
    /// 把 scene 携带的文件 URL 按一批投递进共享打开通道（原生存储引用，顺序即系统顺序）。
    /// </summary>
    /// <param name="urlContexts">URL 上下文集合。</param>
    /// <param name="origin">诊断用的来源标记（scene 连接 / scene 打开）。</param>
    public static void PublishOpenUrlContexts(NSSet<UIOpenUrlContext>? urlContexts, string origin)
    {
        if (urlContexts == null || urlContexts.Count == 0)
        {
            return;
        }

        if (iOSNativeShell.OpenBridge is not { } bridge)
        {
            Console.Error.WriteLine($"[Native] 外部打开桥接未接线，忽略 {urlContexts.Count} 个 {origin} URL");
            return;
        }

        var urls = new List<NSUrl>();
        var allInPlace = true;
        foreach (var contextObject in urlContexts)
        {
            if (contextObject is not UIOpenUrlContext context)
            {
                continue;
            }

            urls.Add(context.Url);
            if (context.Options?.OpenInPlace != true)
            {
                allInPlace = false;
            }
        }

        if (urls.Count == 0)
        {
            return;
        }

        // 就地打开是系统把原文件交给本应用；非就地是分享菜单/其它应用送来的副本。
        var source = allInPlace ? OpenRequestSource.SystemOpen : OpenRequestSource.Share;
        if (!bridge.Publish(urls, source))
        {
            Console.Error.WriteLine($"[Native] 场景 {origin} 的打开请求未被接收");
        }
    }
}
