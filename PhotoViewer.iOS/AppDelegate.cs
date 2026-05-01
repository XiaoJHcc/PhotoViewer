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
                iOSTextInputWorkarounds.Install();
                HeifLoader.Initialize(new iOSHeifDecoder());
                PerformanceBudget.Initialize(new iOSPerformanceBudget());
                XmpWriter.Initialize(new iOSXmpWriter());
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
        return PublishIncomingUrl(url, source: "iOS:OpenUrl");
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
        return PublishIncomingUrl(url, source: $"iOS:OpenUrl:{sourceApplication}");
    }

    /// <summary>
    /// 将系统传入的 URL 交给 iOS 外部打开桥接层统一处理。
    /// </summary>
    /// <param name="url">系统传入的文件 URL</param>
    /// <param name="source">请求来源标记</param>
    /// <returns>是否成功识别并投递</returns>
    private static bool PublishIncomingUrl(NSUrl url, string source)
    {
        return iOSExternalOpenBridge.PublishFromUrl(url, source);
    }
}