using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 独立工具类窗口的公共外壳:深色 caption、任务栏隐藏、按 owner DPI 定物理尺寸并居中。
/// 设置窗口与工具窗口共用这一份,避免两套 DPI/标题栏代码各自漂移。
/// </summary>
internal static class WindowsWindowChrome
{
    /// <summary>
    /// 配置 Win11 风格的深色标题栏与窗口能力:caption 按钮透明融入 Mica、不进任务栏。
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="resizable">是否允许缩放</param>
    /// <param name="maximizable">是否允许最大化</param>
    public static void Configure(Window window, bool resizable = true, bool maximizable = false)
    {
        window.AppWindow.IsShownInSwitchers = false;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = maximizable;
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = window.AppWindow.TitleBar;
        var transparent = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        titleBar.BackgroundColor = transparent;
        titleBar.InactiveBackgroundColor = transparent;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// 激活前按主窗口 DPI 设定物理尺寸、最小尺寸并居中到主窗口所在显示器的工作区。
    /// 必须在 <c>Activate()</c> 之前调用:激活后 <c>AppWindow.Resize</c> 不会让 XAML 内容重新布局。
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="ownerHandle">主窗口句柄</param>
    /// <param name="width">逻辑宽</param>
    /// <param name="height">逻辑高</param>
    /// <param name="minWidth">最小逻辑宽</param>
    /// <param name="minHeight">最小逻辑高</param>
    public static void SizeBeforeShow(
        Window window,
        IntPtr ownerHandle,
        double width,
        double height,
        double minWidth,
        double minHeight)
    {
        var ownerDpi = ownerHandle != IntPtr.Zero ? GetDpiForWindow(ownerHandle) : 0u;
        var systemDpi = GetDpiForSystem();
        var dpi = ownerDpi > 0 ? ownerDpi : systemDpi;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var physicalWidth = (int)Math.Round(width * scale);
        var physicalHeight = (int)Math.Round(height * scale);
        window.AppWindow.Resize(new SizeInt32(physicalWidth, physicalHeight));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);
        }

        var area = ownerHandle != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(ownerHandle), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - physicalWidth) / 2),
            work.Y + Math.Max(0, (work.Height - physicalHeight) / 2)));
    }

    /// <summary>读取系统 DPI(无宿主窗口时使用)。</summary>
    /// <returns>DPI</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>读取窗口 DPI。</summary>
    /// <param name="hwnd">窗口句柄</param>
    /// <returns>DPI</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
