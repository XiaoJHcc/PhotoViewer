using System;
using System.Reflection;
using Avalonia;
using Foundation;
using UIKit;
using PhotoViewer.iOS.Core;

namespace PhotoViewer.iOS;

/// <summary>
/// 自定义 iOS SceneDelegate。
/// 在保留 Avalonia 单视图初始化流程的同时，补充文件 URL 的冷启动与热启动分发。
/// </summary>
[Register("PhotoViewerSceneDelegate")]
public sealed class PhotoViewerSceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    private static readonly MethodInfo? InitWindowMethod =
        Type.GetType("Avalonia.iOS.AvaloniaSceneDelegate, Avalonia.iOS", throwOnError: false)
            ?.GetMethod("InitWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>
    /// 当前 Scene 的窗口。
    /// </summary>
    [Export("window")]
    public UIWindow? Window { get; set; }

    /// <summary>
    /// Scene 建立连接时初始化 Avalonia 窗口，并处理随启动附带的 URLContexts。
    /// </summary>
    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (session.Configuration.Name is not null || scene is not UIWindowScene windowScene)
        {
            return;
        }

        Window = new UIWindow(windowScene);
        if (!TryInitializeAvaloniaWindow(Window))
        {
            return;
        }

        Window.MakeKeyAndVisible();
        PublishOpenUrlContexts(connectionOptions?.UrlContexts, source: "iOS:SceneConnect");
    }

    /// <summary>
    /// 处理应用已启动时 scene 收到的文件 URL 打开请求。
    /// </summary>
    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        PublishOpenUrlContexts(urlContexts, source: "iOS:SceneOpenUrl");
    }

    /// <summary>
    /// 初始化 Avalonia 单视图窗口。
    /// </summary>
    private static bool TryInitializeAvaloniaWindow(UIWindow window)
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
            if (window == null || lifetime == null || InitWindowMethod == null)
            {
                return false;
            }

            InitWindowMethod.Invoke(null, [window, lifetime]);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Scene window initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 将 Scene URLContexts 中的文件 URL 发布到共享层。
    /// </summary>
    private static void PublishOpenUrlContexts(NSSet<UIOpenUrlContext>? urlContexts, string source)
    {
        if (urlContexts == null)
        {
            return;
        }

        foreach (var contextObject in urlContexts)
        {
            if (contextObject is UIOpenUrlContext context)
            {
                _ = iOSExternalOpenBridge.PublishFromUrl(context.Url, source);
            }
        }
    }
}