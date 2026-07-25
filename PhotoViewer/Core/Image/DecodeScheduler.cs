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
/// P0 在途时后台 worker 暂停取新工作(已在跑的不打断)。
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

    /// <summary>
    /// 后台 worker 并发度上限。由调用侧同步 <c>Settings.NativePreloadParallelism</c>(BitmapPrefetcher 构造时挂钩)。
    /// </summary>
    public static volatile int MaxBackgroundParallelism = 2;

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
    public static void Submit(string key, DecodePriority priority, Func<CancellationToken, Task> work)
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
                }
                return;
            }

            _queued[key] = new WorkItem
            {
                Key = key,
                Priority = priority,
                Work = work,
                Seq = Interlocked.Increment(ref _seqCounter),
            };
            _activeKeys.Add(key);
            EnsureWorkersLocked();
        }
        _signal.Release();
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
    /// 后台 worker 长期循环:P0 在途则等待(50ms 轮询),否则按优先级取下一个工作项执行。
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
                lock (_gate) _activeKeys.Remove(item.Key);
            }
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
