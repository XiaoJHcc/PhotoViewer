using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Windows.Core;
using ReactiveUI;
using ReactiveUI.Builder;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// Windows 原生壳启动器。
/// 原生模式不创建 Avalonia 应用、窗口与调度器;生命周期由 WinUI 3 承载,平台门面在这里注入。
/// </summary>
internal static class WindowsNativeApplication
{
    /// <summary>主窗口句柄;文件选择器与设置窗口尺寸在窗口创建后读取。</summary>
    internal static IntPtr WindowHandle { get; set; }

    /// <summary>
    /// 启动原生壳。
    /// </summary>
    /// <param name="args">命令行参数(非 -- 开头者按外部打开路径处理)</param>
    public static void Run(string[] args)
    {
        var smoke = args.Contains("--pv-native-smoke");
        var settingsCheck = args.Contains("--pv-native-settings");
        var settingsHold = args.Contains("--pv-native-settings-hold");
        var toolsCheck = args.Contains("--pv-native-tools");
        var toolsHold = args.Contains("--pv-native-tools-hold");
        NativeStartupLog.BeginSession(
            args,
            smoke || settingsCheck || settingsHold || toolsCheck || toolsHold
                ? Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "native-startup.log")
                : null);
        NativeStartupLog.InstallHooks();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        NativeStartupLog.Write("com wrappers ready");

        Application.Start(p =>
        {
            try
            {
                // WinUI 在 UI 线程上先建立 DispatcherQueue。这里安装 .NET 的 SynchronizationContext
                // 让 async/await 续体回到 UI 线程,并把同一个队列交给共享的 UiDispatcher 门面。
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new WindowsSynchronizationContext(queue));
                var dispatcher = new WindowsUiDispatcher(queue);
                UiDispatcher.Initialize(dispatcher);
                NativeStartupLog.Write("dispatcher installed");

                RxAppBuilder.CreateReactiveUIBuilder()
                    .WithMainThreadScheduler(dispatcher.MainThreadScheduler)
                    .WithCoreServices()
                    .BuildApp();
                NativeStartupLog.Write("reactiveui ready");

                _ = new App(args);
                NativeStartupLog.Write("application created");
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("Application.Start callback", ex);
                throw;
            }
        });
    }

    /// <summary>
    /// 注入平台门面。顺序要求:<see cref="UiDispatcher.Initialize"/> 必须早于 MainViewModel 构造。
    /// </summary>
    /// <param name="smoke">是否使用验收专用设置和数据库。</param>
    internal static void InitializeFacades(bool smoke)
    {
        FilePicker.Initialize(new WindowsFilePicker(() => WindowHandle != IntPtr.Zero
            ? WindowHandle
            : throw new InvalidOperationException("窗口句柄尚未就绪,无法弹出文件选择器。")));
        AppResources.Initialize(new WindowsAppResources());
        HeifLoader.Initialize(new LibHeifDecoder());
        BitmapLoader.InitializePresentationDecoder(new WindowsPresentationDecoder(), new WindowsPresentationEncodingPolicy());
        PerformanceBudget.Initialize(new DefaultPerformanceBudget());
        MemoryGovernor.Initialize(new DefaultMemoryBudgetProvider());
        XmpWriter.Initialize(new WindowsXmpWriter());
        if (smoke)
        {
            WindowsMainWindow.ConfigureSmokeStorage();
        }
        else
        {
            SettingsService.ConfigureStorage(SettingsService.CreateFileStorage());
        }
        DinoFeatureExtractor.ConfigureSession(options => options.AppendExecutionProvider_DML());
        NativeStartupLog.Write("facades ready");
    }
}
