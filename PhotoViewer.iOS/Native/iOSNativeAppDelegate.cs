using Foundation;
using PhotoViewer.iOS.Core;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生壳（UIKit）应用委托：只负责接线（平台门面、共享 VM、外部打开），启动全程不创建 Avalonia 对象。
/// 窗口由 <see cref="iOSNativeSceneDelegate"/> 在 scene 连接时创建：本 app 用 iOS 26 SDK 构建，
/// 系统按 UIScene 生命周期连接 scene（应用委托窗口路径在该 SDK 下已废弃），故窗口只有一条创建口径。
/// </summary>
[Register("iOSNativeAppDelegate")]
public sealed class iOSNativeAppDelegate : UIApplicationDelegate
{
    /// <summary>启动原生壳：接线平台门面、构建共享 VM 与根控制器，等待 scene 连接上屏。</summary>
    /// <param name="application">当前应用。</param>
    /// <param name="launchOptions">启动参数（文件 URL 等由 OpenUrl 处理）。</param>
    /// <returns>始终返回 true（失败会经 BootTrace 落日志并由异常终止，不做静默兜底）。</returns>
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        BootTrace.Mark("native shell: FinishedLaunching begin");
        ObjCRuntime.Runtime.MarshalManagedException += (_, args) => Console.Error.WriteLine("[NativeManagedException] " + args.Exception);
        ObjCRuntime.Runtime.MarshalObjectiveCException += (_, args) => Console.Error.WriteLine("[NativeObjCException] " + args.Exception.Reason);
        iOSNativeShell.CreateRootViewController(NSProcessInfo.ProcessInfo.Arguments);
        BootTrace.Mark("native shell: waiting for scene connection");
        Console.WriteLine($"[Native] UIKit 原生壳接线完成（无 Avalonia 对象）：{iOSNativeShell.Main?.CurrentFile?.Name ?? "未打开文件"}");
        return true;
    }

    /// <summary>处理 iOS 9+ 的文件 URL 打开回调（文件 App「打开方式」、分享菜单）。</summary>
    /// <param name="app">当前应用。</param>
    /// <param name="url">系统传入的文件 URL。</param>
    /// <param name="options">打开参数。</param>
    /// <returns>是否识别并投递。</returns>
    public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        => iOSExternalOpenBridge.PublishNativeFromUrl(url, "iOS:NativeOpenUrl");

    /// <summary>处理旧版 iOS 的文件 URL 打开回调。</summary>
    /// <param name="app">当前应用。</param>
    /// <param name="url">系统传入的文件 URL。</param>
    /// <param name="sourceApplication">来源应用标识。</param>
    /// <param name="annotation">附加参数。</param>
    /// <returns>是否识别并投递。</returns>
    public override bool OpenUrl(UIApplication app, NSUrl url, string sourceApplication, NSObject annotation)
        => iOSExternalOpenBridge.PublishNativeFromUrl(url, "iOS:NativeOpenUrlLegacy");

    /// <summary>声明原生 scene 委托，保证系统走 scene 连接时文件 URL 仍进入原生投递路径。</summary>
    /// <param name="application">当前应用。</param>
    /// <param name="connectingSceneSession">正在连接的场景会话。</param>
    /// <param name="options">场景连接选项。</param>
    /// <returns>原生 scene 配置。</returns>
    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
    {
        var role = connectingSceneSession?.Role ?? UIWindowSceneSessionRole.Application;
        return new UISceneConfiguration("PhotoViewerNative", role) { DelegateType = typeof(iOSNativeSceneDelegate) };
    }
}

/// <summary>
/// 原生壳 scene 委托：scene 连接时经 <see cref="iOSSceneBridge"/> 建立窗口并挂上当前活动壳的根视图
/// （iOS 26 SDK 下的唯一窗口口径），同时处理冷启动与热启动随 scene 到达的文件 URL。
/// </summary>
[Register("iOSNativeSceneDelegate")]
public sealed class iOSNativeSceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    /// <summary>当前 scene 的窗口。</summary>
    [Export("window")]
    public UIWindow? Window { get; set; }

    /// <summary>scene 建立连接：创建 scene 窗口并处理随启动附带的 URLContexts。</summary>
    /// <param name="scene">当前 scene。</param>
    /// <param name="session">正在连接的场景会话。</param>
    /// <param name="connectionOptions">场景连接选项。</param>
    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        BootTrace.Mark("native scene delegate: willConnect");
        if (scene is not UIWindowScene windowScene)
        {
            throw new InvalidOperationException($"iOS 原生壳只支持 UIWindowScene，实际为 {scene.GetType().Name}。");
        }

        Window = iOSSceneBridge.Connect(windowScene);
        Window.MakeKeyAndVisible();
        BootTrace.Mark($"native scene delegate: window visible {Window.Bounds.Width}×{Window.Bounds.Height}");
        iOSSceneBridge.PublishOpenUrlContexts(connectionOptions?.UrlContexts, "iOS:NativeSceneConnect");
    }

    /// <summary>处理应用已启动时 scene 收到的文件 URL 打开请求。</summary>
    /// <param name="scene">当前 scene。</param>
    /// <param name="urlContexts">URL 上下文集合。</param>
    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        BootTrace.Mark("native scene delegate: openUrlContexts");
        iOSSceneBridge.PublishOpenUrlContexts(urlContexts, "iOS:NativeSceneOpenUrl");
    }
}
