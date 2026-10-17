using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using ReactiveUI;
using ReactiveUI.Avalonia;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Memory;
using PhotoViewer.Desktop.Core;
using Microsoft.ML.OnnxRuntime;

namespace PhotoViewer.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>
    /// Windows 桌面入口。Debug 默认原生壳(<c>--legacy-ui</c> 退出);Release 默认旧壳(<c>--native</c> 进入)。
    /// 与 macOS 同一套分流口径;原生分支不创建任何 Avalonia 对象。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        var native = args.Contains("--native");
#if DEBUG
        native = !args.Contains("--legacy-ui");
#endif
        if (native)
        {
            Native.WindowsNativeApplication.Run(args);
            return;
        }

        RunLegacy(args);
    }

    /// <summary>
    /// 启动迁移期旧壳(Avalonia):先接收系统传入的图片路径,再启动 Avalonia 主界面。
    /// </summary>
    /// <param name="args">命令行参数</param>
    private static void RunLegacy(string[] args) => BuildAvaloniaApp()
        .AfterSetup(_ =>
        {
            HeifLoader.Initialize(new LibHeifDecoder());
            PerformanceBudget.Initialize(new DefaultPerformanceBudget());
            MemoryGovernor.Initialize(new DefaultMemoryBudgetProvider());
            XmpWriter.Initialize(new WindowsXmpWriter());
            SettingsService.ConfigureStorage(SettingsService.CreateFileStorage());
            DinoFeatureExtractor.ConfigureSession(options =>
            {
                options.AppendExecutionProvider_DML();
            });
            PublishExternalOpenArgs(args);
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

    /// <summary>
    /// 将命令行参数中的有效文件路径发布为外部打开请求。
    /// </summary>
    /// <param name="args">应用启动参数</param>
    private static void PublishExternalOpenArgs(IEnumerable<string> args)
    {
        var fileUris = args
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(TryCreateFileUri)
            .OfType<Uri>()
            .ToList();

        if (fileUris.Count == 0)
        {
            return;
        }

        ExternalOpenService.PublishFiles(fileUris, source: "DesktopCommandLine");
    }

    /// <summary>
    /// 将本地文件路径转换为 file:// URI。
    /// </summary>
    /// <param name="path">命令行中的原始路径</param>
    private static Uri? TryCreateFileUri(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return new Uri(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }
}