using System;
using System.Reactive.Concurrency;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Platform;

/// <summary>
/// UI 线程调度优先级。
/// </summary>
public enum UiPriority
{
    /// <summary>常规优先级,按 UI 队列顺序执行。</summary>
    Normal,

    /// <summary>后台优先级,待 UI 空闲后再执行。</summary>
    Background,
}

/// <summary>
/// UI 线程调度器接口。
/// 由 UI 层实现具体的线程封送逻辑,Core 与 ViewModel 只依赖本接口,不直接引用 UI 框架。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// 检查当前线程是否为 UI 线程。
    /// </summary>
    /// <returns>当前在 UI 线程上时返回 true。</returns>
    bool CheckAccess();

    /// <summary>
    /// 用于 Rx 流的 UI 线程调度器(供 <c>ObserveOn</c> 等使用)。
    /// </summary>
    IScheduler MainThreadScheduler { get; }

    /// <summary>
    /// 将操作投递到 UI 线程队列,不等待执行完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    void Post(Action action, UiPriority priority = UiPriority.Normal);

    /// <summary>
    /// 在 UI 线程执行操作并等待完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal);

    /// <summary>
    /// 在 UI 线程执行函数并等待返回值。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <param name="priority">调度优先级。</param>
    Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal);
}

/// <summary>
/// UI 线程调度门面。
/// 启动期由 UI 层经 <see cref="Initialize"/> 注入实现,Core 与 ViewModel 统一通过本类回到 UI 线程。
/// </summary>
public static class UiDispatcher
{
    private static IUiDispatcher? _impl;

    /// <summary>
    /// 注入 UI 层的调度器实现,必须在任何调度调用之前完成。
    /// </summary>
    /// <param name="impl">UI 层调度器实现。</param>
    public static void Initialize(IUiDispatcher impl)
    {
        _impl = impl ?? throw new ArgumentNullException(nameof(impl));
    }

    /// <summary>当前注入的实现;未初始化时抛出异常(暴露接线遗漏,而非静默错线程执行)。</summary>
    private static IUiDispatcher Impl
        => _impl ?? throw new InvalidOperationException("UiDispatcher 未初始化:请在应用启动时调用 UiDispatcher.Initialize。");

    /// <summary>
    /// 检查当前线程是否为 UI 线程。
    /// </summary>
    /// <returns>当前在 UI 线程上时返回 true。</returns>
    public static bool CheckAccess() => Impl.CheckAccess();

    /// <summary>
    /// 用于 Rx 流的 UI 线程调度器(供 <c>ObserveOn</c> 等使用)。
    /// </summary>
    public static IScheduler MainThreadScheduler => Impl.MainThreadScheduler;

    /// <summary>
    /// 将操作投递到 UI 线程队列,不等待执行完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public static void Post(Action action, UiPriority priority = UiPriority.Normal) => Impl.Post(action, priority);

    /// <summary>
    /// 在 UI 线程执行操作并等待完成。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public static Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal) => Impl.InvokeAsync(action, priority);

    /// <summary>
    /// 在 UI 线程执行函数并等待返回值。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <param name="priority">调度优先级。</param>
    public static Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Impl.InvokeAsync(func, priority);
}
