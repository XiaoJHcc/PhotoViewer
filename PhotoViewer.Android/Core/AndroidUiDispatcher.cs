using System;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using PhotoViewer.Android.Native;
using PhotoViewer.Core.Platform;
using ReactiveUI;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 主线程调度器：只使用 <see cref="Looper.MainLooper"/> 与 <see cref="Handler"/>，
/// 不依赖第三方 UI 框架的调度器。
/// </summary>
public sealed class AndroidUiDispatcher : IUiDispatcher
{
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly SynchronizationContext _context;

    /// <summary>建立主线程 Rx 调度器与 .NET 同步上下文。</summary>
    public AndroidUiDispatcher()
    {
        _context = new AndroidSynchronizationContext(_handler);
        MainThreadScheduler = new SynchronizationContextScheduler(_context);
    }

    /// <summary>主线程 Rx 调度器。</summary>
    public IScheduler MainThreadScheduler { get; }

    /// <summary>判断当前线程是否为主 Looper。</summary>
    public bool CheckAccess() => Looper.MyLooper() == Looper.MainLooper;

    /// <summary>投递到主线程队列，不等待。</summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级（与 Mac/iOS 同口径，均投递主队列）。</param>
    public void Post(Action action, UiPriority priority = UiPriority.Normal)
        => _handler.Post(AndroidFrameStats.Enabled ? Measure(FrameStatsKind.Post, action) : action);

    private static Action Measure(FrameStatsKind kind, Action action)
    {
        long queuedAt = AndroidFrameStats.Mark();
        return () => Run(kind, queuedAt, action);
    }

    /// <summary>在主线程执行操作并等待。</summary>
    /// <param name="action">要执行的操作。</param>
    /// <param name="priority">调度优先级。</param>
    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        => InvokeAsync(() => { action(); return true; }, priority);

    /// <summary>在主线程执行函数并返回结果；异常原样传播。</summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <param name="priority">调度优先级。</param>
    public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
    {
        if (CheckAccess())
        {
            long directStart = AndroidFrameStats.Mark();
            try
            {
                return Task.FromResult(func());
            }
            finally
            {
                AndroidFrameStats.Span(FrameStatsKind.Invoke, directStart, AndroidFrameStats.Mark());
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!AndroidFrameStats.Enabled)
        {
            _handler.Post(() => Complete(completion, func));
            return completion.Task;
        }
        long queuedAt = AndroidFrameStats.Mark();
        _handler.Post(() => Run(FrameStatsKind.Invoke, queuedAt, () => Complete(completion, func)));
        return completion.Task;
    }

    /// <summary>把结果或异常写回等待中的任务。</summary>
    private static void Complete<T>(TaskCompletionSource<T> completion, Func<T> func)
    {
        try
        {
            completion.TrySetResult(func());
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <summary>先记排队时间再记回调执行时间；<paramref name="queuedAt"/> 为 0（主线程直接执行）时只记执行段。</summary>
    private static void Run(FrameStatsKind kind, long queuedAt, Action action)
    {
        long startedAt = AndroidFrameStats.Mark();
        AndroidFrameStats.Span(FrameStatsKind.Queue, queuedAt, startedAt);
        try
        {
            action();
        }
        finally
        {
            AndroidFrameStats.Span(kind, startedAt, AndroidFrameStats.Mark());
        }
    }

    /// <summary>在创建共享 VM 前安装 .NET 主线程上下文。</summary>
    public void InstallSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(_context);

    /// <summary>把 .NET 异步续体封送到主 Looper。</summary>
    private sealed class AndroidSynchronizationContext : SynchronizationContext
    {
        private readonly Handler _handler;

        /// <summary>绑定主线程 Handler。</summary>
        /// <param name="handler">主线程 Handler。</param>
        public AndroidSynchronizationContext(Handler handler) => _handler = handler;

        /// <summary>异步投递回调。</summary>
        /// <param name="callback">回调委托。</param>
        /// <param name="state">回调状态。</param>
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!AndroidFrameStats.Enabled)
            {
                _handler.Post(() => callback(state));
                return;
            }
            long queuedAt = AndroidFrameStats.Mark();
            _handler.Post(() => Run(FrameStatsKind.Context, queuedAt, () => callback(state)));
        }

        /// <summary>同步封送：主线程直接执行，其他线程投递后等待。</summary>
        /// <param name="callback">回调委托。</param>
        /// <param name="state">回调状态。</param>
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Looper.MyLooper() == Looper.MainLooper)
            {
                long directStart = AndroidFrameStats.Mark();
                try
                {
                    callback(state);
                }
                finally
                {
                    AndroidFrameStats.Span(FrameStatsKind.Context, directStart, AndroidFrameStats.Mark());
                }
                return;
            }

            using var done = new ManualResetEventSlim();
            Exception? error = null;
            void Deliver()
            {
                try
                {
                    callback(state);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    done.Set();
                }
            }

            if (!AndroidFrameStats.Enabled)
            {
                _handler.Post(new Action(Deliver));
            }
            else
            {
                long queuedAt = AndroidFrameStats.Mark();
                _handler.Post(() => Run(FrameStatsKind.Context, queuedAt, Deliver));
            }
            done.Wait();
            if (error != null)
            {
                throw error;
            }
        }

        /// <summary>创建同一平台语义的上下文副本。</summary>
        public override SynchronizationContext CreateCopy() => new AndroidSynchronizationContext(_handler);
    }
}
