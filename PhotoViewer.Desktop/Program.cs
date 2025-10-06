using System;
using Avalonia;
using Avalonia.ReactiveUI;
using PhotoViewer.Core;
using PhotoViewer.Desktop.Core;

namespace PhotoViewer.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .AfterSetup(_ =>
        {
            // Windows 优先使用 WIC，其它平台仍旧 LibHeifDecoder
            if (OperatingSystem.IsWindows())
                HeifLoader.Initialize(new WindowsHeifDecoder());
            else
                HeifLoader.Initialize(new LibHeifDecoder());
                
            MemoryBudget.Initialize(new DefaultMemoryBudget());
        })
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();
}