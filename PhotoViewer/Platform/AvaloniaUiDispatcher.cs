using System;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Avalonia.Threading;
using PhotoViewer.Core.Platform;
using ReactiveUI.Avalonia;

namespace PhotoViewer.Platform;

/// <summary>
/// Avalonia 实现的 UI 线程调度器,包装 <see cref="Dispatcher.UIThread"/>。
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>
    /// 用于 Rx 流的 UI 线程调度器(供 <c>ObserveOn</c> 等使用)。
    /// </summary>
    public IScheduler MainThreadScheduler => AvaloniaScheduler.Instance;

    /// <summary>
    /// 检查当前线程是否为 UI 线程。
    /// </summary>
    /// <returns>当前在 UI 线程上时返回 true。</returns>
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    /// <summary>
    /// 将操作投递到 UI 线程队列,不等待执行完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public void Post(Action action, UiPriority priority = UiPriority.Normal)
        => Dispatcher.UIThread.Post(action, Map(priority));

    /// <summary>
    /// 在 UI 线程执行操作并等待完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public async Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        => await Dispatcher.UIThread.InvokeAsync(action, Map(priority));

    /// <summary>
    /// 在 UI 线程执行函数并等待返回值。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <param name="priority">调度优先级。</param>
    public async Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
        => await Dispatcher.UIThread.InvokeAsync(func, Map(priority));

    /// <summary>映射到 Avalonia 调度优先级。</summary>
    private static DispatcherPriority Map(UiPriority priority) => priority switch
    {
        UiPriority.Background => DispatcherPriority.Background,
        _ => DispatcherPriority.Normal,
    };
}
