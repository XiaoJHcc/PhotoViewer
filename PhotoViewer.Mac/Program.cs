using Avalonia;
using ReactiveUI;
using ReactiveUI.Avalonia;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Memory;
using PhotoViewer.Mac.Core;
using Microsoft.ML.OnnxRuntime;
using System;
using System.IO;

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
            RawLoader.Initialize(new MacRawDecoder());
            PerformanceBudget.Initialize(new DefaultPerformanceBudget());
            MemoryGovernor.Initialize(new DefaultMemoryBudgetProvider());
            XmpWriter.Initialize(new MacXmpWriter());
            SettingsService.ConfigureStorage(new MacSettingsStorage());
            DinoFeatureExtractor.ConfigureSession(options =>
            {
                options.AppendExecutionProvider_CoreML();
            });
            MacExternalOpenBridge.PublishFromPaths(args, source: "MacCommandLine");
            // P1-M5 压测参数(--pv-stress 系,常规运行无此参数、完全惰性);相对路径按当前工作目录解析
            StressFlipDriver.TryParseArgs(args, Path.GetFullPath);
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