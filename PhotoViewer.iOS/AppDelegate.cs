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

                // 可选：监听系统内存告警，触发应用内缓存/资源降级逻辑，避免被系统杀死
                UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, __) =>
                {
                    // 1) 记录清理前缓存大小
                    var beforeBytes = BitmapLoader.CurrentCacheSize;

                    // 2) 通知 BitmapLoader 进行快速精简（目标 50% 上限）
                    var (before, after) = BitmapLoader.TrimOnMemoryWarning(0.5);

                    // 3) 记录到 MemoryBudget（iOS 侧存档 MB 值）
                    iOSMemoryBudget.RecordMemoryWarningCacheMB(before);

                    // 4) 广播给 UI：更新 MemoryBudgetInfo
                    var evt = new BitmapLoader.MemoryWarningEvent(
                        beforeMB: before / (1024 * 1024),
                        afterMB:  after / (1024 * 1024),
                        time:     DateTimeOffset.Now
                    );
                    MessageBus.Current.SendMessage(evt);
                });
            });
    }
}