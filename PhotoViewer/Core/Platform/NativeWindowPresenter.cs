using System;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Core.Platform;

/// <summary>
/// 原生窗口展示器接口。
/// 由 UI 层实现具体的窗口创建与展示逻辑,ViewModel 只负责发起调用,不直接引用窗口类型。
/// </summary>
public interface INativeWindowPresenter
{
    /// <summary>
    /// 展示设置窗口(桌面端模态对话框)。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    void ShowSettingsWindow(SettingsViewModel settings);

    /// <summary>
    /// 展示工具窗口(桌面端单例);已打开时直接前置,不重复创建。
    /// </summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    void ShowOrActivateToolsWindow(ToolsViewModel tools);
}

/// <summary>
/// 原生窗口展示门面。
/// 启动期由 UI 层经 <see cref="Initialize"/> 注入实现。
/// </summary>
public static class NativeWindowPresenter
{
    private static INativeWindowPresenter? _impl;

    /// <summary>
    /// 注入 UI 层的窗口展示实现。
    /// </summary>
    /// <param name="impl">窗口展示器实现。</param>
    public static void Initialize(INativeWindowPresenter impl)
    {
        _impl = impl ?? throw new ArgumentNullException(nameof(impl));
    }

    /// <summary>当前注入的实现;未初始化时抛出异常(暴露接线遗漏)。</summary>
    private static INativeWindowPresenter Impl
        => _impl ?? throw new InvalidOperationException("NativeWindowPresenter 未初始化:请在应用启动时调用 NativeWindowPresenter.Initialize。");

    /// <summary>
    /// 展示设置窗口(桌面端模态对话框)。
    /// </summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    public static void ShowSettingsWindow(SettingsViewModel settings) => Impl.ShowSettingsWindow(settings);

    /// <summary>
    /// 展示工具窗口(桌面端单例);已打开时直接前置,不重复创建。
    /// </summary>
    /// <param name="tools">工具壳 ViewModel。</param>
    public static void ShowOrActivateToolsWindow(ToolsViewModel tools) => Impl.ShowOrActivateToolsWindow(tools);
}
