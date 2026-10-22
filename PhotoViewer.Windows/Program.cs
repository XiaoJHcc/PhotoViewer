using System;

namespace PhotoViewer.Windows;

/// <summary>
/// Windows 桌面入口:Debug 与 Release 都直接进入 WinUI 3 原生壳。
/// COM/WinRT 初始化、平台门面与生命周期均由 <see cref="Native.WindowsNativeApplication"/> 负责。
/// </summary>
sealed class Program
{
    /// <summary>
    /// 启动原生壳;命令行中的路径参数由启动流程作为外部打开请求处理。
    /// </summary>
    /// <param name="args">命令行参数</param>
    [STAThread]
    public static void Main(string[] args)
    {
        Native.WindowsNativeApplication.Run(args);
    }
}
