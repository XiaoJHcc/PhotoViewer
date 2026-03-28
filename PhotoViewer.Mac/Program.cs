using Avalonia;
using ReactiveUI;
using ReactiveUI.Avalonia;
using PhotoViewer.Core;
using PhotoViewer.Core.Settings;
using PhotoViewer.Mac.Core;

namespace PhotoViewer.Mac;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .AfterSetup(_ => 
        {
            HeifLoader.Initialize(new MacHeifDecoder());
            MemoryBudget.Initialize(new DefaultMemoryBudget());
            SettingsService.ConfigureStorage(new MacSettingsStorage());
            // NSApplicationDelegate 的构造需要 AppKit UI 线程就绪，
            // 因此将 Install() 注册为回调，延迟到 OnFrameworkInitializationCompleted() 中执行。
            App.PlatformFrameworkReadyCallback = MacExternalOpenBridge.Install;
            MacExternalOpenBridge.PublishFromPaths(args, source: "MacCommandLine");
        })
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    /// <summary>
    /// 构建 Avalonia 应用。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}