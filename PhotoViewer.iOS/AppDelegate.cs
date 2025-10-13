using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using PhotoViewer.Core;
using PhotoViewer.iOS.Core;
using ReactiveUI;
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
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI()
            .AfterSetup(_ => 
            {
                HeifLoader.Initialize(new iOSHeifDecoder());
                MemoryBudget.Initialize(new iOSMemoryBudget());

                // 监听系统内存告警：清理至“触发时缓存大小”的 80%，并仅上报触发时快照
                UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, __) =>
                {
                    var (beforeBytes, beforeCount, _) = BitmapLoader.TrimToCurrentRatio(0.8);

                    // 记录触发时缓存大小（MB）
                    iOSMemoryBudget.RecordMemoryWarningCacheMB(beforeBytes);

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
}