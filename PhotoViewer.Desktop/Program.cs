using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using ReactiveUI;
using ReactiveUI.Avalonia;
using PhotoViewer.Core;
using PhotoViewer.Desktop.Core;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>
    /// Windows 桌面入口。
    /// 这里会先接收系统传入的图片路径，再启动 Avalonia 主界面。
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .AfterSetup(_ =>
        {
            HeifLoader.Initialize(new LibHeifDecoder());
            MemoryBudget.Initialize(new DefaultMemoryBudget());
            XmpWriter.Initialize(new WindowsXmpWriter());
            SettingsService.ConfigureStorage(SettingsService.CreateFileStorage());
            PublishExternalOpenArgs(args);
             
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