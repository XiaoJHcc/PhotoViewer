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

        // iOS 不填充托管 Main 的 args（实测为空数组），启动参数一律从 NSProcessInfo 读取。
        var launchArguments = NSProcessInfo.ProcessInfo.Arguments;
        BootTrace.Mark("args: " + string.Join(" ", launchArguments));

        // 启动 UIKit 原生壳；--native 等历史参数不再参与壳选择，传入时按普通启动参数忽略。
        BootTrace.Mark("shell: native (UIKit)");
        UIApplication.Main(args, null, typeof(iOSNativeAppDelegate));
    }
}
