using Foundation;
using PhotoViewer.iOS.Core;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// scene 窗口与外部打开的接线。
/// 系统的 scene 连接统一交给原生壳根控制器；文件 URL 直接以原生存储引用投递。
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
    /// 把 scene 携带的文件 URL 投递进共享外部打开链路（原生存储引用）。
    /// </summary>
    /// <param name="urlContexts">URL 上下文集合。</param>
    /// <param name="source">请求来源标记。</param>
    public static void PublishOpenUrlContexts(NSSet<UIOpenUrlContext>? urlContexts, string source)
    {
        if (urlContexts == null)
        {
            return;
        }

        foreach (var contextObject in urlContexts)
        {
            if (contextObject is not UIOpenUrlContext context)
            {
                continue;
            }

            iOSExternalOpenBridge.PublishNativeFromUrl(context.Url, source);
        }
    }
}
