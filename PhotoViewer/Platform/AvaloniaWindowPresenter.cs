using Avalonia.Controls;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using PhotoViewer.Views;
using PhotoViewer.Views.Tools;

namespace PhotoViewer.Platform;

/// <summary>
/// Avalonia 实现的窗口展示器,负责桌面端设置/工具窗口的创建与单例管理。
/// </summary>
public sealed class AvaloniaWindowPresenter : INativeWindowPresenter
{
    // 桌面端单例工具窗口引用
    private ToolsWindow? _toolsWindow;

    /// <summary>
    /// 展示设置窗口(CenterOwner + 模态)。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public void ShowSettingsWindow(SettingsViewModel settings)
    {
        if (App.GetCurrentTopLevel() is not Window owner)
        {
            return;
        }

        var settingsWindow = new SettingsWindow
        {
            DataContext = settings
        };
        settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        settingsWindow.ShowDialog(owner);
    }

    /// <summary>
    /// 展示工具窗口(桌面端单例);已打开时直接前置,不重复创建。
    /// </summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    public void ShowOrActivateToolsWindow(ToolsViewModel tools)
    {
        if (_toolsWindow != null)
        {
            _toolsWindow.Activate();
            return;
        }

        if (App.GetCurrentTopLevel() is not Window owner)
        {
            return;
        }

        _toolsWindow = new ToolsWindow
        {
            DataContext = tools
        };
        _toolsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _toolsWindow.Closed += (_, _) => _toolsWindow = null;
        _toolsWindow.Show(owner);
    }
}
