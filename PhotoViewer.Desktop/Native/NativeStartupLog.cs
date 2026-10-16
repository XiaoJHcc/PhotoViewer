using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生壳启动期诊断：把启动/未处理异常落盘到 <c>%LOCALAPPDATA%\PhotoViewer\native-startup.log</c>。
/// WinUI 的原生启动失败会以 stowed exception(0xC000027B)结束进程且无控制台输出，
/// 没有这份日志就无法区分"起不来"与"起来后崩"。
/// </summary>
internal static class NativeStartupLog
{
    private static readonly object Gate = new();

    /// <summary>日志文件路径。</summary>
    public static string LogPath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoViewer",
        "native-startup.log");

    /// <summary>写入一条带时间戳的阶段记录。</summary>
    /// <param name="message">记录内容</param>
    public static void Write(string message) => Append($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    /// <summary>清空上一轮日志并写入会话头。</summary>
    /// <param name="args">命令行参数</param>
    /// <param name="logPath">可选的隔离日志路径，仅验收入口指定。</param>
    public static void BeginSession(string[] args, string? logPath = null)
    {
        if (logPath != null)
        {
            LogPath = Path.GetFullPath(logPath);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] native shell start: {string.Join(' ', args)}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // 诊断不可用不能影响启动
        }
    }

    /// <summary>登记未处理异常钩子。</summary>
    public static void InstallHooks()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Append($"UNHANDLED {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Append($"UNOBSERVED {e.Exception}");
    }

    /// <summary>记录并吞掉异常，返回异常文本。</summary>
    /// <param name="stage">发生阶段</param>
    /// <param name="ex">异常</param>
    public static string Fail(string stage, Exception ex)
    {
        var text = $"{stage}: {ex}";
        Append(text);
        Console.Error.WriteLine(text);
        return text;
    }

    private static void Append(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 诊断不可用不能影响启动
            }
        }
    }
}
