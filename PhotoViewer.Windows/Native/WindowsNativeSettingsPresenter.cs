using System;
using System.Threading.Tasks;
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
    private TaskCompletionSource _closed = CompletedSource();

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

        _closed = CompletedSource();
        _session = new NativeSettingsSession(settings);
        _window = new WindowsSettingsWindow(_session.WorkingSettings, _ownerHandle());
        _window.Closed += OnWindowClosed;
        _window.Show();
        return true;
    }

    /// <summary>关闭设置窗口并等待工作副本回写完成;未打开时立即返回。</summary>
    /// <returns>回写与释放结束后的任务。</returns>
    public Task CloseIfOpenAsync()
    {
        var window = _window;
        if (window is null)
        {
            return _closed.Task;
        }

        // 退出路径不等 Closed 事件：先摘掉会话与订阅，再关窗，回写不受窗口事件调度影响。
        var session = _session;
        _session = null;
        window.Closed -= OnWindowClosed;
        _window = null;
        window.Close();
        return CompleteAsync(session);
    }

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

        var session = _session;
        _session = null;
        _ = CompleteAsync(session);
    }

    private Task CompleteAsync(NativeSettingsSession? session)
    {
        if (session is null)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        return ApplyAndDisposeAsync(session, _closed);
    }

    private static async Task ApplyAndDisposeAsync(NativeSettingsSession session, TaskCompletionSource closed)
    {
        try
        {
            if (!await session.ApplyAsync().ConfigureAwait(false))
            {
                NativeStartupLog.Write("设置回写未落盘，保留 last good 设置");
            }
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("设置回写", ex);
        }
        finally
        {
            session.Dispose();
            closed.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }
}
