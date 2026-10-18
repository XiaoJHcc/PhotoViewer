using System;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// Windows 原生设置窗口展示器:工作副本、单例、关闭回写。
/// 只由共享的 NativeWindowPresenter 调用,不另建第二条展示路径。
/// </summary>
internal sealed class WindowsNativeSettingsPresenter : INativeSettingsPresenter
{
    private readonly Func<IntPtr> _ownerHandle;
    private NativeSettingsSession? _session;
    private WindowsSettingsWindow? _window;

    /// <summary>创建展示器。</summary>
    /// <param name="ownerHandle">主窗口句柄提供者(取显示器 DPI 与工作区)</param>
    public WindowsNativeSettingsPresenter(Func<IntPtr> ownerHandle) => _ownerHandle = ownerHandle;

    /// <summary>设置窗口是否已打开。</summary>
    public bool IsOpen => _window != null;

    /// <summary>当前窗口(验收用)。</summary>
    internal WindowsSettingsWindow? Window => _window;

    /// <summary>展示(或激活)设置窗口。</summary>
    /// <param name="settings">当前主设置</param>
    /// <returns>恒为 true(原生窗口总可接管)</returns>
    public bool TryPresent(SettingsViewModel settings)
    {
        if (_window != null)
        {
            _window.Activate();
            return true;
        }

        _session = new NativeSettingsSession(settings);
        _window = new WindowsSettingsWindow(_session.WorkingSettings, settings, _ownerHandle());
        _window.Closed += OnWindowClosed;
        _window.Show();
        return true;
    }

    /// <summary>主窗口关闭或退出前关闭设置窗口,保证工作副本回写一次。</summary>
    public void CloseIfOpen() => _window?.Close();

    /// <summary>窗口关闭:一次性回写并释放工作副本。</summary>
    /// <param name="sender">窗口</param>
    /// <param name="args">事件参数</param>
    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (_window != null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        _session?.Apply();
        _session?.Dispose();
        _session = null;
    }
}
