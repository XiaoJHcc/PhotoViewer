using PhotoViewer.iOS.Core;
using PhotoViewer.iOS.Native;
using UIKit;

namespace PhotoViewer.iOS;

public class Application
{
    // This is the main entry point of the application.
    static void Main(string[] args)
    {
        // 崩溃取证最先挂载(真机间歇 SIGABRT 调查);任何托管异常落沙盒日志后再由运行时终止
        BootTrace.Install();
        BootTrace.Mark("Main enter");

        // iOS 不填充托管 Main 的 args（实测为空数组），启动参数一律从 NSProcessInfo 读取（与旧壳压测/外部打开同口径）。
        var launchArguments = NSProcessInfo.ProcessInfo.Arguments;
        BootTrace.Mark("args: " + string.Join(" ", launchArguments));
        BootTrace.MarkAvaloniaAssemblies("Main enter(before UIApplication.Main)");

        // 壳选择：Debug 默认原生壳，Release 默认旧壳；--native / --legacy-ui / NSUserDefaults(PVLegacyUI) 可覆盖。
        // 未选中的壳委托类型不会被加载，因此原生路径不会载入 Avalonia 类型。
        UIApplication.Main(args, null, iOSShellSwitch.SelectDelegateType(launchArguments));
    }
}
