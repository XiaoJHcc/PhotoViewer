using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 批量分析的显式意图上下文（<see cref="AsyncLocal{T}"/>）：声明"这次取图与推理属于一次分析批次"，
/// 据此把解码准入从按压力级别拒绝改为按实测内存 CAS 准入（见 <see cref="MemoryGovernor.TryAcquire"/>），
/// 并给出批次共用的取消令牌与全局 CPU 调度器。
///
/// 作用域随执行流向下传递；准入拒绝原因记在**本操作自己进入的作用域**上（不跨并行 stage 共享），
/// 由取图入口在退出作用域前抛 <see cref="AnalysisMemoryException"/>。
/// 应用活跃闸独立于内存状态：进后台取消当前代际，回前台换新代际，已取消的批次不自动恢复。
/// </summary>
public static class BatchAnalysisContext
{
    private static readonly AsyncLocal<ScopeState?> Ambient = new();
    private static readonly object ApplicationGate = new();
    private static readonly object RootGate = new();
    private static readonly List<Waiter> RootWaiters = new();
    private static readonly Lazy<ConcurrentExclusiveSchedulerPair> SharedCpuScheduler = new(() =>
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Math.Max(1, Environment.ProcessorCount)));

    private static System.Threading.CancellationTokenSource _generation = new();
    private static bool _applicationActive = true;
    private static int _rootCount;

    static BatchAnalysisContext()
    {
        // Critical 下等 root 也不会获批：唤醒等待者让它们立即明确失败，不空等
        MemoryGovernor.LevelChanged += level =>
        {
            if (level == PressureLevel.Critical) WakeWaiters();
        };
    }

    /// <summary>当前执行流是否处于分析批次内（显式意图，不按压力或平台推断）。</summary>
    public static bool IsActive => Ambient.Value != null;

    /// <summary>批次内 CPU 并行用的全局有界调度器：并发上限 = <see cref="Environment.ProcessorCount"/>，不预留核。</summary>
    public static TaskScheduler CpuScheduler => SharedCpuScheduler.Value.ConcurrentScheduler;

    public static TaskScheduler CpuExclusiveScheduler => SharedCpuScheduler.Value.ExclusiveScheduler;

    /// <summary>应用是否在前台（默认 true），只由宿主生命周期上报驱动，与内存压力无关。</summary>
    public static bool IsApplicationActive
    {
        get { lock (ApplicationGate) return _applicationActive; }
    }

    /// <summary>当前应用代际的取消令牌：进后台即取消，回前台换成新代际（旧代际保持已取消）。</summary>
    public static System.Threading.CancellationToken ApplicationToken
    {
        get { lock (ApplicationGate) return _generation.Token; }
    }

    /// <summary>进入分析作用域（可嵌套，令牌链在调用方令牌、外层作用域令牌与应用代际上）。</summary>
    /// <param name="ct">调用方的取消令牌。</param>
    /// <returns>释放即退出本层作用域。</returns>
    public static IDisposable Enter(System.Threading.CancellationToken ct = default)
    {
        var parent = Ambient.Value;
        var linked = parent == null
            ? System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, ApplicationToken)
            : System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, parent.Token);
        var scope = new ScopeState(parent, linked);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>当前作用域的取消令牌；不在作用域内时返回 <c>default</c>。</summary>
    public static System.Threading.CancellationToken CancellationToken() => Ambient.Value?.Token ?? default;

    /// <summary>
    /// 上报应用前后台切换：进后台取消当前代际（在途批次一并取消），回前台换新代际；
    /// 被取消的批次保持取消状态，不因回前台自动续跑。
    /// </summary>
    /// <param name="active">是否在前台。</param>
    public static void SetApplicationActive(bool active)
    {
        System.Threading.CancellationTokenSource? cancel = null;
        lock (ApplicationGate)
        {
            if (_applicationActive == active) return;
            _applicationActive = active;
            if (active) _generation = new System.Threading.CancellationTokenSource();
            else cancel = _generation;
        }
        cancel?.Cancel();
    }

    /// <summary>记录一次准入拒绝（字节数），供本操作的取图入口抛出。</summary>
    /// <param name="bytes">本次被拒的工作集字节数。</param>
    public static void RecordAdmissionRefusal(long bytes) => RecordAdmissionRefusal(bytes, DefaultRefusalReason);

    /// <summary>记录一次带原因的准入拒绝（原因取实际受限项，用户可见）。</summary>
    internal static void RecordAdmissionRefusal(long bytes, string reason)
        => Ambient.Value?.RecordRefusal(bytes, reason);

    /// <summary>
    /// 若当前作用域有准入拒绝记录则抛出（抛后清空）。取图入口已在内部抛出，
    /// 本方法保留给仍在同一作用域内主动核查的调用方。
    /// </summary>
    public static void ThrowIfAdmissionRefused()
    {
        if (TakeRefusal() is { } refusal) throw refusal;
    }

    /// <summary>清空本操作作用域的拒绝记录（取图入口在会改变结论的步骤前调用）。</summary>
    internal static void ClearAdmissionRefusal() => Ambient.Value?.ClearRefusal();

    /// <summary>取走本操作作用域的拒绝记录；没有则返回 null。</summary>
    internal static AnalysisMemoryException? TakeRefusal()
        => Ambient.Value is { } scope && scope.TryTakeRefusal(out long bytes, out string reason)
            ? new AnalysisMemoryException(reason, bytes)
            : null;

    /// <summary>准入被拒异常：优先用本操作记录的原因，缺失时用兜底原因。</summary>
    /// <param name="bytes">本次工作集字节数。</param>
    /// <param name="fallbackReason">无记录时的短原因。</param>
    internal static AnalysisMemoryException RefusalException(long bytes, string fallbackReason = DefaultRefusalReason)
        => TakeRefusal() ?? new AnalysisMemoryException(fallbackReason, Math.Max(1, bytes));

    /// <summary>当前执行流是否已在一次取图内部（嵌套取图不再等新 root，避免与外层所持 root 成环）。</summary>
    internal static bool InNestedAcquire => Ambient.Value?.IsAcquiring == true;

    internal static void EnterAcquire()
    {
        if (Ambient.Value is { } scope) scope.IsAcquiring = true;
    }

    /// <summary>登记一份分析 root（工作集许可）。</summary>
    internal static void RegisterAnalysisRoot()
    {
        lock (RootGate) _rootCount++;
    }

    /// <summary>归还分析 root 并唤醒全部等待者（计数与唤醒同锁，无丢信号）。</summary>
    internal static void ReleaseAnalysisRoot()
    {
        Waiter[] waiters;
        lock (RootGate)
        {
            if (_rootCount > 0) _rootCount--;
            waiters = RootWaiters.ToArray();
            RootWaiters.Clear();
        }
        foreach (var waiter in waiters) waiter.Release();
    }

    /// <summary>
    /// 等一次 root 释放；当前没有在途 root（没有可释放的工作）时返回 null。
    /// 登记与在途计数同锁读取，不会漏唤醒；返回的等待者必须 Dispose 才能退出等待队列。
    /// </summary>
    internal static Waiter? WaitForAnalysisRootRelease()
    {
        lock (RootGate)
        {
            if (_rootCount == 0 || MemoryGovernor.CurrentLevel == PressureLevel.Critical) return null;
            var waiter = new Waiter();
            RootWaiters.Add(waiter);
            return waiter;
        }
    }

    private static void WakeWaiters()
    {
        Waiter[] waiters;
        lock (RootGate)
        {
            waiters = RootWaiters.ToArray();
            RootWaiters.Clear();
        }
        foreach (var waiter in waiters) waiter.Release();
    }

    private const string DefaultRefusalReason = "分析内存准入被拒：内存不足";

    /// <summary>一次 root 释放的等待凭证；释放或取消后必须 Dispose 以退出等待队列。</summary>
    internal sealed class Waiter : IDisposable
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _done;

        internal Task Task => _completion.Task;

        internal void Release()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _completion.TrySetResult(true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (RootGate) RootWaiters.Remove(this);
        }
    }

    /// <summary>一层分析作用域：取消令牌 + 本操作的准入拒绝记录。</summary>
    private sealed class ScopeState : IDisposable
    {
        private readonly System.Threading.CancellationTokenSource _cts;
        private readonly object _refusalGate = new();
        private string? _refusalReason;
        private long _refusalBytes;
        private int _disposed;

        internal ScopeState(ScopeState? parent, System.Threading.CancellationTokenSource cts)
        {
            Parent = parent;
            _cts = cts;
            Token = cts.Token;
        }

        internal ScopeState? Parent { get; }
        internal System.Threading.CancellationToken Token { get; }
        internal bool IsAcquiring { get; set; }

        internal void RecordRefusal(long bytes, string reason)
        {
            lock (_refusalGate)
            {
                _refusalBytes = bytes;
                _refusalReason = reason;
            }
        }

        internal void ClearRefusal()
        {
            lock (_refusalGate)
            {
                _refusalBytes = 0;
                _refusalReason = null;
            }
        }

        internal bool TryTakeRefusal(out long bytes, out string reason)
        {
            lock (_refusalGate)
            {
                bytes = _refusalBytes;
                reason = _refusalReason ?? string.Empty;
                bool refused = _refusalReason != null;
                _refusalBytes = 0;
                _refusalReason = null;
                return refused;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Ambient.Value = Parent;
            _cts.Dispose();
        }
    }
}
