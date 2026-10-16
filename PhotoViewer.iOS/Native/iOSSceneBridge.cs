using System.Reflection;
using Foundation;
using PhotoViewer.iOS.Core;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// scene 窗口与外部打开的共用接线。
/// 系统会按持久化的 scene session 恢复委托类型，且本 app 未声明 scene manifest，
/// 因此无论哪个壳的 scene 委托收到连接，都按"当前活动壳"接线：
/// 原生壳未接线 ⇒ 旧壳（Avalonia 单视图），反之亦然，避免出现无窗口（黑屏）。
/// </summary>
internal static class iOSSceneBridge
{
    /// <summary>Avalonia 的 scene 窗口初始化入口（旧壳在 scene 模式下唯一可用的接线口径）。</summary>
    private static readonly MethodInfo? AvaloniaInitWindowMethod =
        Type.GetType("Avalonia.iOS.AvaloniaSceneDelegate, Avalonia.iOS", throwOnError: false)
            ?.GetMethod("InitWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>
    /// 为 scene 建立窗口并挂上当前活动壳的根视图。
    /// </summary>
    /// <param name="windowScene">正在连接的窗口场景。</param>
    /// <returns>已挂好根视图、可直接上屏的窗口。</returns>
    public static UIWindow Connect(UIWindowScene windowScene)
    {
        var window = new UIWindow(windowScene);
        if (iOSNativeShell.RootViewController is { } nativeRoot)
        {
            window.RootViewController = nativeRoot;
            BootTrace.Mark("scene bridge: 原生壳接窗口");
            return window;
        }

        var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
        if (AvaloniaInitWindowMethod == null || lifetime == null)
        {
            throw new InvalidOperationException("scene 连接时两种壳都未接线：既无原生壳根控制器，也无 Avalonia 应用生命周期。");
        }

        AvaloniaInitWindowMethod.Invoke(null, [window, lifetime]);
        BootTrace.Mark("scene bridge: 旧壳接窗口");
        return window;
    }

    /// <summary>
    /// 把 scene 携带的文件 URL 投递进共享外部打开链路，按当前活动壳选择发布口径
    /// （原生壳不加载 Avalonia，必须走原生存储引用）。
    /// </summary>
    /// <param name="urlContexts">URL 上下文集合。</param>
    /// <param name="source">请求来源标记。</param>
    public static void PublishOpenUrlContexts(NSSet<UIOpenUrlContext>? urlContexts, string source)
    {
        if (urlContexts == null)
        {
            return;
        }

        var native = iOSNativeShell.RootViewController != null;
        foreach (var contextObject in urlContexts)
        {
            if (contextObject is not UIOpenUrlContext context)
            {
                continue;
            }

            if (native)
            {
                iOSExternalOpenBridge.PublishNativeFromUrl(context.Url, source);
            }
            else
            {
                _ = iOSExternalOpenBridge.PublishFromUrl(context.Url, source);
            }
        }
    }
}
