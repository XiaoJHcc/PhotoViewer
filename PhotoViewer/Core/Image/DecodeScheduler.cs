using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 解码工作优先级(数值越小越优先)。
/// </summary>
public enum DecodePriority
{
    /// <summary>P0 当前照片:主图解码,立即执行,在途期间后台 worker 暂停取新工作</summary>
    P0_Current = 0,
    /// <summary>P1 高概率目标:可见区域/当前图缩略图</summary>
    P1_HighValue = 1,
    /// <summary>P2 机会型预载:邻居位图、普通排队缩略图</summary>
    P2_Prefetch = 2,
    /// <summary>P3 杂项:分析缓存预热等纯 IO/CPU 任务</summary>
    P3_Background = 3,
}

/// <summary>
/// 统一优先级解码调度器(静态类)。
/// 收编缩略图加载与位图预取两条后台通道:按优先级排队、同 key 去重、
/// P0 在途时后台 worker 暂停取新工作(已在跑的不打断),唯独标记为"可沿 P0 并行"的项例外
/// (翻页方向上的下一、两张预取)。这类项走**独立通道**立即开跑,不占用后台 worker 池,
/// 因此不会被缩略图/远邻预取挤到本轮翻页之后(连翻时 P0 几乎一直在途,后台池根本轮不到它们)。
/// 设计见 Plans/photo-decode-scheduler-plan-1.md §2 Step 4。
/// </summary>
public static class DecodeScheduler
{
    private sealed class WorkItem
    {
        public required string Key;
        public required DecodePriority Priority;
        public required Func<CancellationToken, Task> Work;
        public long Seq;
        /// <summary>P0 在途时仍允许执行（翻页方向上的下一张预取），走独立通道。</summary>
        public bool RunAlongsideP0;
    }

    private static readonly object _gate = new();
    // 队列中的工作项(key 唯一);_activeKeys 覆盖在队 + 在途,用于去重
    private static readonly Dictionary<string, WorkItem> _queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _activeKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _signal = new(0);
    private static readonly CancellationTokenSource _shutdownCts = new();
    private static long _seqCounter;
    private static int _workerCount;
    private static int _p0InFlight;
    // 「可沿 P0 并行」通道当前在执行几个（上限 MaxAlongsideParallelism）
    private static int _runAlongsideP0Active;

    /// <summary>
    /// 后台 worker 并发度上限。由调用侧同步 <c>Settings.NativePreloadParallelism</c>(BitmapPrefetcher 构造时挂钩)。
    /// </summary>
    public static volatile int MaxBackgroundParallelism = 2;

    /// <summary>
    /// 「可沿 P0 并行」通道的并发上限(翻页方向上的前两张预取):与后台 worker 池互不占用,
    /// 否则连翻时它们的解码被缩略图/远邻预取挡在队尾,翻到下一张时还没开始。
    /// </summary>
    public static int MaxAlongsideParallelism = 2;

    /// <summary>P0(当前照片解码)在途计数;> 0 时后台 worker 暂停取新工作</summary>
    public static int P0InFlightCount => Volatile.Read(ref _p0InFlight);

    /// <summary>
    /// 提交一个后台工作项。同 key 已在队/在途则不重复提交;
    /// 若新请求优先级更高且项还在队列中,提升其优先级。
    /// P0 项立即在独立 Task 执行,不进 worker 队列。
    /// </summary>
    /// <param name="key">去重键(如同一路径的缩略图用 path + "#thumb")</param>
    /// <param name="priority">优先级</param>
    /// <param name="work">工作本体,参数为全局停止 token</param>
    /// <param name="runAlongsideP0">
    /// 允许在 P0(当前图解码)在途时执行:只用于"翻页方向上的前两张"预取,并发上限
    /// <see cref="MaxAlongsideParallelism"/>;其余后台项照旧为 P0 让位。
    /// </param>
    public static void Submit(string key, DecodePriority priority, Func<CancellationToken, Task> work, bool runAlongsideP0 = false)
    {
        if (priority == DecodePriority.P0_Current)
        {
            lock (_gate)
            {
                if (!_activeKeys.Add(key)) return; // 同 key P0 已在途
            }
            Interlocked.Increment(ref _p0InFlight);
            _ = Task.Run(async () =>
            {
                try
                {
                    await work(_shutdownCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DecodeScheduler] P0 work failed ({key}): {ex.Message}");
                }
                finally
                {
                    lock (_gate) _activeKeys.Remove(key);
                    Interlocked.Decrement(ref _p0InFlight);
                }
            });
            return;
        }

        lock (_gate)
        {
            if (_activeKeys.Contains(key))
            {
                // 已在队:用最新请求替换工作本体(latest-wins — 旧工作可能携带已取消的轮次 token,
                // 直接保留会导致新一轮请求被去重吞掉),高优先级请求同时提升其优先级;在途:不可干预
                if (_queued.TryGetValue(key, out var existing))
                {
                    existing.Work = work;
                    if (priority < existing.Priority)
                        existing.Priority = priority;
                    existing.RunAlongsideP0 |= runAlongsideP0;
                }
                if (runAlongsideP0) PumpAlongsideLocked();
                return;
            }

            _queued[key] = new WorkItem
            {
                Key = key,
                Priority = priority,
                Work = work,
                Seq = Interlocked.Increment(ref _seqCounter),
                RunAlongsideP0 = runAlongsideP0,
            };
            _activeKeys.Add(key);
            EnsureWorkersLocked();
            if (runAlongsideP0) PumpAlongsideLocked();
        }
        _signal.Release();
    }

    /// <summary>
    /// 在「可沿 P0 并行」通道有空位时立即开跑队首的同通道工作项(不占用后台 worker 池)。
    /// 调用方必须持有 <see cref="_gate"/>。
    /// </summary>
    private static void PumpAlongsideLocked()
    {
        while (Volatile.Read(ref _runAlongsideP0Active) < Math.Max(1, MaxAlongsideParallelism))
        {
            WorkItem? next = null;
            foreach (var item in _queued.Values)
            {
                if (!item.RunAlongsideP0) continue;
                if (next == null || item.Priority < next.Priority ||
                    (item.Priority == next.Priority && item.Seq < next.Seq))
                {
                    next = item;
                }
            }
            if (next == null) return;
            _queued.Remove(next.Key);
            _runAlongsideP0Active++;
            _ = Task.Run(() => RunItemAsync(next, alongsideLane: true));
        }
    }

    /// <summary>
    /// 执行一个已从队列取出的工作项;异常吞掉并记日志,绝不弄死调用方。
    /// 工作项的优先级(以及"沿 P0 并行"标记)在这里进入 <see cref="DecodeContext"/>,
    /// 于是它内部发起的解码在平台解码器那里也按同一优先级排队,不用给每层调用加参数。
    /// </summary>
    private static async Task RunItemAsync(WorkItem item, bool alongsideLane)
    {
        using var context = DecodeContext.Enter(item.Priority, alongsideLane);
        try
        {
            await item.Work(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DecodeScheduler] work failed ({item.Key}): {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _activeKeys.Remove(item.Key);
                if (alongsideLane)
                {
                    _runAlongsideP0Active--;
                    PumpAlongsideLocked();
                }
            }
        }
    }

    /// <summary>
    /// 标记一段 P0(当前照片解码)在途区间,供不走 Submit 的主图解码路径使用;
    /// 期间后台 worker 暂停取新工作。using 结束即解除。
    /// </summary>
    public static IDisposable TrackP0()
    {
        Interlocked.Increment(ref _p0InFlight);
        return new P0Scope();
    }

    private sealed class P0Scope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _p0InFlight);
        }
    }

    /// <summary>
    /// 查询指定优先级在队列中的工作项数量(不含在途)。
    /// </summary>
    public static int GetQueuedCount(DecodePriority priority)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (var item in _queued.Values)
                if (item.Priority == priority) count++;
            return count;
        }
    }

    /// <summary>
    /// 清空队列中 key 以指定后缀结尾的工作项(如 "#thumb" 清理全部待加载缩略图),返回清理数量。
    /// 在途项不受影响。
    /// </summary>
    public static int ClearQueuedByKeySuffix(string keySuffix)
    {
        lock (_gate)
        {
            var keys = _queued.Keys
                .Where(k => k.EndsWith(keySuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in keys)
            {
                _queued.Remove(k);
                _activeKeys.Remove(k);
            }
            return keys.Count;
        }
    }

    /// <summary>
    /// 全局停止:取消所有在途工作项的 token,worker 循环退出。
    /// </summary>
    public static void Shutdown() => _shutdownCts.Cancel();

    private static void EnsureWorkersLocked()
    {
        int target = Math.Max(1, MaxBackgroundParallelism);
        while (_workerCount < target)
        {
            _workerCount++;
            _ = Task.Run(WorkerLoopAsync);
        }
    }

    /// <summary>
    /// 后台 worker 长期循环:P0 在途时暂停取新工作,否则按优先级取下一个工作项执行。
    /// 「可沿 P0 并行」的项由 <see cref="PumpAlongsideLocked"/> 在独立通道执行,这里只在 P0 空闲时兜底接到它们。
    /// 工作项异常吞掉并记日志,绝不弄死循环。并发度下调时超额 worker 自行退出。
    /// </summary>
    private static async Task WorkerLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            WorkItem? item = null;
            lock (_gate)
            {
                if (_workerCount > Math.Max(1, MaxBackgroundParallelism))
                {
                    _workerCount--;
                    return;
                }
                if (Volatile.Read(ref _p0InFlight) == 0)
                    item = DequeueNextLocked();
            }

            if (item == null)
            {
                try
                {
                    await _signal.WaitAsync(50, _shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            await RunItemAsync(item, alongsideLane: false).ConfigureAwait(false);
        }
    }

    /// <summary>取优先级最高(数值最小)、同优先级按提交先后(FIFO)的下一个工作项。</summary>
    private static WorkItem? DequeueNextLocked()
    {
        WorkItem? best = null;
        foreach (var item in _queued.Values)
        {
            if (best == null || item.Priority < best.Priority ||
                (item.Priority == best.Priority && item.Seq < best.Seq))
            {
                best = item;
            }
        }
        if (best != null) _queued.Remove(best.Key);
        return best;
    }
}
