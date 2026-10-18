using System;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// Windows 原生工具窗口展示器:单例、懒创建、关闭释放。
/// 只由共享 <c>NativeWindowPresenter</c> 调用,不另建第二条展示路径;不持有任何工具数据。
/// </summary>
internal sealed class WindowsNativeToolsPresenter
{
    private readonly Func<IntPtr> _ownerHandle;
    private WindowsToolsWindow? _window;
    private ToolsViewModel? _presentedTools;

    /// <summary>创建展示器。</summary>
    /// <param name="ownerHandle">主窗口句柄提供者(取显示器 DPI 与工作区)</param>
    public WindowsNativeToolsPresenter(Func<IntPtr> ownerHandle) => _ownerHandle = ownerHandle;

    /// <summary>工具窗口是否已打开。</summary>
    public bool IsOpen => _window != null;

    /// <summary>当前窗口(验收用)。</summary>
    internal WindowsToolsWindow? Window => _window;

    /// <summary>展示(或激活)工具窗口;已打开时恢复上次分类并前置。</summary>
    /// <param name="tools">共享工具壳视图模型</param>
    /// <returns>恒为 true(原生窗口总可接管)</returns>
    public bool TryPresent(ToolsViewModel tools)
    {
        if (_window != null)
        {
            tools.IsPresented = true;
            _window.Activate();
            return true;
        }

        _presentedTools = tools;
        tools.IsPresented = true;
        _window = new WindowsToolsWindow(tools, _ownerHandle());
        _window.Closed += OnWindowClosed;
        _window.Show();
        return true;
    }

    /// <summary>主窗口关闭或退出前关闭工具窗口,释放页面订阅。</summary>
    public void CloseIfOpen() => _window?.Close();

    /// <summary>窗口关闭:解除引用;下次展示重新创建。</summary>
    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (_window != null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        if (_presentedTools != null)
        {
            _presentedTools.IsPresented = false;
            _presentedTools = null;
        }
    }
}
