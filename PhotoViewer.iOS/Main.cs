using PhotoViewer.iOS.Core;
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

        // if you want to use a different Application Delegate class from "AppDelegate"
        // you can specify it here.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
