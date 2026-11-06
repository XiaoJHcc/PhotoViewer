using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主窗口所在显示器的工作区，换算到根元素坐标系。窗口化弹层会按显示器工作区自行夹取或翻转，
/// 自绘位置的弹层（RATING-04 来源浮窗）必须用同一份可见边界先夹取，否则越过工作区时会被系统二次翻转。
/// </summary>
internal static class WindowsWorkArea
{
    /// <summary>取根元素坐标系下的显示器工作区边界。</summary>
    /// <param name="root">坐标基准元素（弹层的 x/y 与它同坐标系）。</param>
    /// <param name="window">主窗口，用于取显示器与客户区屏幕原点。</param>
    /// <returns>根坐标系里的工作区矩形；平台查询失败时返回 null，交由调用方按窗口内范围退让。</returns>
    internal static Rect? VisibleBoundsIn(FrameworkElement root, Microsoft.UI.Xaml.Window window)
    {
        try
        {
            return Query(root, window);
        }
        catch (Exception)
        {
            // 窗口关闭中、显示器重建或 Interop 句柄失效时不影响弹层展示：退回窗口内范围。
            return null;
        }
    }

    private static Rect? Query(FrameworkElement root, Microsoft.UI.Xaml.Window window)
    {
        var xamlRoot = root.XamlRoot;
        if (xamlRoot == null) return null;
        var scale = xamlRoot.RasterizationScale;
        if (!(scale > 0)) return null;
        var appWindow = window.AppWindow;
        if (appWindow == null) return null;
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
        if (hwnd == IntPtr.Zero) return null;

        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return null;
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        // 根元素通常就是窗口内容，此时客户区原点即根原点；不是同一元素时补上它在内容里的偏移。
        double offsetX = 0;
        double offsetY = 0;
        if (xamlRoot.Content is { } content && !ReferenceEquals(content, root))
        {
            var offset = root.TransformToVisual(content).TransformPoint(new Point(0, 0));
            offsetX = offset.X;
            offsetY = offset.Y;
        }

        return new Rect(
            work.X / scale - origin.X / scale - offsetX,
            work.Y / scale - origin.Y / scale - offsetY,
            work.Width / scale,
            work.Height / scale);
    }

    /// <summary>客户区坐标 → 屏幕物理像素。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="point">客户区坐标，返回屏幕坐标。</param>
    /// <returns>成功返回 true。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    /// <summary>客户区/屏幕点（物理像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
