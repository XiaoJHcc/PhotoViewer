using Foundation;
using UIKit;
using PhotoViewer.iOS.Native;

namespace PhotoViewer.iOS;

/// <summary>
/// 旧壳（Avalonia）的 iOS SceneDelegate。
/// 窗口与 URLContexts 分发都交给共享的 <see cref="iOSSceneBridge"/>：系统会按持久化 scene session 恢复委托类型，
/// 两个壳的 scene 委托必须都能按"当前活动壳"接线，否则切换壳后会出现无窗口（黑屏）。
/// </summary>
[Register("PhotoViewerSceneDelegate")]
public sealed class PhotoViewerSceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    /// <summary>
    /// 当前 Scene 的窗口。
    /// </summary>
    [Export("window")]
    public UIWindow? Window { get; set; }

    /// <summary>
    /// Scene 建立连接时初始化活动壳窗口，并处理随启动附带的 URLContexts。
    /// </summary>
    /// <param name="scene">当前场景。</param>
    /// <param name="session">正在连接的场景会话。</param>
    /// <param name="connectionOptions">场景连接选项。</param>
    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        BootTrace.Mark("legacy scene delegate: willConnect");
        if (scene is not UIWindowScene windowScene)
        {
            throw new InvalidOperationException($"PhotoViewer 只支持 UIWindowScene，实际为 {scene.GetType().Name}。");
        }

        Window = iOSSceneBridge.Connect(windowScene);
        Window.MakeKeyAndVisible();
        iOSSceneBridge.PublishOpenUrlContexts(connectionOptions?.UrlContexts, "iOS:SceneConnect");
    }

    /// <summary>
    /// 处理应用已启动时 scene 收到的文件 URL 打开请求。
    /// </summary>
    /// <param name="scene">当前场景。</param>
    /// <param name="urlContexts">URL 上下文集合。</param>
    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        iOSSceneBridge.PublishOpenUrlContexts(urlContexts, "iOS:SceneOpenUrl");
    }
}
