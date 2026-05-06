using ReactiveUI;

using Foundation;
using UIKit;
using Avalonia;
using Avalonia.iOS;

using ReactiveUI.Avalonia;
using PhotoViewer.Core;
using PhotoViewer.Core.Settings;
using PhotoViewer.iOS.Core;

using System;
using System.Runtime.Versioning;

namespace PhotoViewer.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    /// <summary>
    /// 自定义 Avalonia AppBuilder，并注入 iOS 平台能力实现。
    /// </summary>
    /// <param name="builder">Avalonia 构建器</param>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { })
            .AfterSetup(_ => 
            {
                 HeifLoader.Initialize(new iOSHeifDecoder());
                 PerformanceBudget.Initialize(new iOSPerformanceBudget());
                 StorageAccessManager.Initialize(new iOSStorageAccessManager());
                 XmpWriter.Initialize(new iOSXmpWriter());
                 NativeSettingsPresenter.Initialize(new iOSNativeSettingsPresenter());
                 SettingsService.ConfigureStorage(new iOSSettingsStorage());
 
                // 监听系统内存告警：清理至“触发时缓存大小”的 80%，并仅上报触发时快照
                UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, __) =>
                {
                    var (beforeBytes, beforeCount, _) = BitmapLoader.TrimToCurrentRatio(0.8);

                    // 记录触发时缓存大小（MB）
                    iOSPerformanceBudget.RecordMemoryWarningCacheMB(beforeBytes);

                    // 广播给 UI（仅显示触发时的大小、数量与时间）
                    var evt = new BitmapLoader.MemoryWarningEvent(
                        sizeMB: beforeBytes / (1024 * 1024),
                        count:  beforeCount,
                        time:   DateTimeOffset.Now
                    );
                    MessageBus.Current.SendMessage(evt);
                });
            });
    }

    /// <summary>
    /// 处理 iOS 9+ 的文件 URL 打开回调。
    /// </summary>
    /// <param name="app">当前应用</param>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="options">打开参数</param>
    /// <returns>是否成功识别并投递</returns>
    [Export("application:openURL:options:")]
    public new bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
    {
        iOSStorageAccessManager.RetainUrl(url);
        return base.OpenUrl(app, url, options);
    }

    /// <summary>
    /// 处理旧版 iOS 的文件 URL 打开回调。
    /// </summary>
    /// <param name="app">当前应用</param>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="sourceApplication">来源应用标识</param>
    /// <param name="annotation">附加参数</param>
    /// <returns>是否成功识别并投递</returns>
    [Export("application:openURL:sourceApplication:annotation:")]
    public bool OpenUrl(UIApplication app, NSUrl url, string sourceApplication, NSObject annotation)
    {
        iOSStorageAccessManager.RetainUrl(url);
        return base.OpenUrl(app, url, new NSDictionary());
    }

    /// <summary>
    /// 处理 iOS 13+ scene 冷启动时附带的 URLContexts。
    /// Avalonia 的默认 SceneDelegate 不消费连接选项中的文件 URL，这里提前转成挂起的外部打开请求。
    /// </summary>
    /// <param name="application">当前应用</param>
    /// <param name="connectingSceneSession">正在连接的场景会话</param>
    /// <param name="options">场景连接选项</param>
    /// <returns>Avalonia 默认的场景配置</returns>
    [Export("application:configurationForConnectingSceneSession:options:")]
    [SupportedOSPlatform("ios13.0")]
    public new UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
    {
        var role = connectingSceneSession?.Role ?? UIWindowSceneSessionRole.Application;
        if (connectingSceneSession?.Role is null)
        {
            Console.WriteLine("[iOS Scene] Missing scene role, falling back to application role.");
        }

        return new UISceneConfiguration("PhotoViewer", role)
        {
            DelegateType = typeof(PhotoViewerSceneDelegate)
        };
    }
}
