using System;
using System.IO;
using System.Threading.Tasks;

namespace PhotoViewer.iOS;

/// <summary>
/// iOS 启动期崩溃取证(2026-10-13 真机间歇 SIGABRT/SIGSEGV 调查):Main 最早期挂载
/// UnhandledException / UnobservedTaskException,异常全文与启动里程碑追加写入
/// 沙盒 Documents/PhotoViewer/ios-crash.log(devicectl copy 可取回;写盘失败静默)。
/// </summary>
internal static class BootTrace
{
    private static string? _logPath;

    /// <summary>挂载异常捕获并确定日志路径(任何异常绝不外抛,取证代码不得影响主流程)。</summary>
    public static void Install()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "ios-crash.log");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write($"UNHANDLED IsTerminating={e.IsTerminating}: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                Write($"UNOBSERVED: {e.Exception}");
        }
        catch { /* 静默 */ }
    }

    /// <summary>写一个启动里程碑。</summary>
    /// <param name="mark">里程碑名。</param>
    public static void Mark(string mark) => Write($"mark: {mark}");

    /// <summary>追加一行(带时间戳)。</summary>
    private static void Write(string line)
    {
        try
        {
            if (_logPath == null) return;
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {line}\n");
        }
        catch { /* 静默 */ }
    }
}
