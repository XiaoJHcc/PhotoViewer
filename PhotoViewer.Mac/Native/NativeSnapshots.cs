using AppKit;
using System.Diagnostics;

namespace PhotoViewer.Mac.Native;

/// <summary>原生控件离屏导出与主窗口真实截屏，仅用于验收。</summary>
public static class NativeSnapshots
{
    /// <summary>保存原生设置控件实际绘制结果。</summary>
    public static void SaveView(NSView view, string path)
    {
        using var bitmap = view.BitmapImageRepForCachingDisplayInRect(view.Bounds) ?? throw new InvalidOperationException("原生截图缓冲创建失败");
        view.CacheDisplay(view.Bounds, bitmap);
        using var data = bitmap.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png) ?? throw new InvalidOperationException("原生截图编码失败");
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>由系统合成器截取指定窗口，包含 Metal、原生材质与滚动内容；需要屏幕录制权限。</summary>
    public static void SaveMain(MacMainWindow window, string path)
    {
        var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("-x");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(window.Window.WindowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("系统窗口截屏无法启动");
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            throw new TimeoutException("系统窗口截屏超时");
        }
        if (process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("系统窗口截屏失败，请检查屏幕录制权限");
    }
}
