using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;
using AdmissionPermit = PhotoViewer.Core.Memory.MemoryGovernor.AdmissionPermit;

namespace PhotoViewer.Android.Core;

/// <summary>共享读取的结束状态；<see cref="Refused"/> 是内存准入拒绝，不是解码错误。</summary>
internal enum AndroidRawReadStatus
{
    Pending,
    Ready,
    Refused,
    Failed,
    Cancelled,
}

/// <summary>共享读取计数快照（验收对照用，产品路径不读）。</summary>
internal readonly record struct AndroidRawReadStatsSnapshot(
    long Reads,
    long Bytes,
    long Shares,
    long Refusals,
    long Failures,
    long Cancels,
    long Releases,
    long Returned,
    int ActiveEntries);

/// <summary>共享读取的轻量计数：只累加计数器，不逐帧、不逐块写日志。</summary>
internal static class AndroidRawReadStats
{
    private static long _reads;
    private static long _bytes;
    private static long _shares;
    private static long _refusals;
    private static long _failures;
    private static long _cancels;
    private static long _releases;
    private static long _returned;

    internal static void RecordRead(long bytes)
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal static void RecordShare() => Interlocked.Increment(ref _shares);

    internal static void RecordRefusal() => Interlocked.Increment(ref _refusals);

    internal static void RecordFailure() => Interlocked.Increment(ref _failures);

    internal static void RecordCancel() => Interlocked.Increment(ref _cancels);

    internal static void RecordRelease() => Interlocked.Increment(ref _releases);

    internal static void RecordReturned() => Interlocked.Increment(ref _returned);

    internal static AndroidRawReadStatsSnapshot Snapshot() => new(
        Interlocked.Read(ref _reads),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _shares),
        Interlocked.Read(ref _refusals),
        Interlocked.Read(ref _failures),
        Interlocked.Read(ref _cancels),
        Interlocked.Read(ref _releases),
        Interlocked.Read(ref _returned),
        AndroidRawReadLease.ActiveEntryCount);
}

/// <summary>消费者持有的共享读取句柄：同一租约上每人一份，<see cref="Dispose"/> 只减自己那份且幂等。</summary>
internal sealed class AndroidRawReadHandle : IDisposable
{
    private AndroidRawReadLease? _lease;

    internal AndroidRawReadHandle(AndroidRawReadLease lease) => _lease = lease;

    /// <summary>取得/加入共享租约（不阻塞、不做 I/O）；共享键是文件引用身份，不是 StorageId。</summary>
    internal static AndroidRawReadHandle Open(IFileRef file, DecodePriority priority)
        => new(AndroidRawReadLease.Acquire(file, priority));

    /// <summary>
    /// 等待字节就绪。调用方令牌取消只结束本次等待（抛 <see cref="OperationCanceledException"/>），
    /// 共享读取不受影响；非就绪状态返回 null 并给出原因。
    /// </summary>
    internal byte[]? TryGetBytes(CancellationToken ct, out int length, out AndroidRawReadStatus status)
    {
        var lease = _lease ?? throw new ObjectDisposedException(nameof(AndroidRawReadHandle));
        return lease.TryGetBytes(ct, out length, out status);
    }

    internal string? Detail => _lease?.Detail;

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Release();
}

/// <summary>
/// 同一次加载内 RAW 文件字节的共享租约：正式开发（LibRaw 需要整文件随机访问）与内嵌预览在同一条
/// <see cref="IFileRef"/> 对象上重叠时只读一次文件、共用一份池化缓冲。
/// 契约：共享键是对象身份（刷新重建的引用不会命中上一代字节）；只在活跃租约期间存在，最后一个租约释放即
/// 归还缓冲并撤下登记，没有常驻文件缓存；文件字节准入跟随租约范围（P0 放行但记账，后台被拒即放弃且不读文件）；
/// 读取用租约自己的令牌，消费者取消只结束各自的等待，全部撤销才中止读取。
/// </summary>
internal sealed class AndroidRawReadLease
{
    /// <summary>托管缓冲上限：与非寻址流共用同一口径（<see cref="Array.MaxLength"/>）。</summary>
    private static readonly long MaxBufferBytes = Array.MaxLength;

    /// <summary>非寻址流的第一段准入额度，也是逐段增长的步长基数。</summary>
    private const long UnknownLengthReserveBytes = 64L << 20;

    private const int UnknownLengthChunkBytes = 81920;

    /// <summary>登记表与租约状态的唯一锁：只做字段读写与字典操作，锁内没有 I/O，也不跑取消回调。</summary>
    private static readonly object Gate = new();

    private static readonly Dictionary<IFileRef, AndroidRawReadLease> Active =
        new(ReferenceEqualityComparer.Instance);

    private readonly IFileRef _file;
    private readonly CancellationTokenSource _readCancellation = new();

    private DecodePriority _priority;
    private Task<AndroidRawReadStatus>? _readTask;
    private List<AdmissionPermit>? _permits;
    private byte[]? _buffer;
    private int _length;
    private int _holders = 1;
    private bool _closed;
    private bool _completed;
    private bool _cancellationDisposed;
    private AndroidRawReadStatus _status = AndroidRawReadStatus.Pending;
    private string? _detail;

    private AndroidRawReadLease(IFileRef file, DecodePriority priority)
    {
        _file = file;
        _priority = priority;
    }

    internal static int ActiveEntryCount
    {
        get { lock (Gate) return Active.Count; }
    }

    internal string? Detail
    {
        get { lock (Gate) return _detail; }
    }

    internal static AndroidRawReadLease Acquire(IFileRef file, DecodePriority priority)
    {
        lock (Gate)
        {
            if (Active.TryGetValue(file, out var existing) && existing.TryJoin(priority))
            {
                AndroidRawReadStats.RecordShare();
                return existing;
            }

            // 已结束（失败/取消/关闭）的租约不再并入：换成新的一次读取，重试路径因此能重新打开文件。
            var lease = new AndroidRawReadLease(file, priority);
            Active[file] = lease;
            return lease;
        }
    }

    internal byte[]? TryGetBytes(CancellationToken ct, out int length, out AndroidRawReadStatus status)
    {
        ct.ThrowIfCancellationRequested();
        var task = EnsureReadStarted();

        // 只终止本人等待；读取任务用的是租约令牌，任一消费者取消都不会打断别人或提前归还缓冲。
        var outcome = ct.CanBeCanceled
            ? task.WaitAsync(ct).GetAwaiter().GetResult()
            : task.GetAwaiter().GetResult();

        status = outcome;
        if (outcome != AndroidRawReadStatus.Ready)
        {
            length = 0;
            return null;
        }

        lock (Gate)
        {
            length = _length;
            return _buffer;
        }
    }

    /// <summary>释放一份租约；最后一个释放负责撤下登记，并归还缓冲与许可（读取在途时交给读取任务收尾）。</summary>
    internal void Release()
    {
        List<AdmissionPermit>? permits = null;
        byte[]? buffer = null;
        CancellationTokenSource? abort = null;
        CancellationTokenSource? finished = null;

        lock (Gate)
        {
            if (_holders <= 0) return;
            _holders--;
            AndroidRawReadStats.RecordRelease();
            if (_holders > 0) return;

            _closed = true;
            if (!_completed && _readTask == null)
            {
                // 从未开始读取：没有读取任务会收尾，登记与取消源必须在这里撤掉。
                _status = AndroidRawReadStatus.Cancelled;
                _detail = "读取开始前消费者已全部释放";
                _completed = true;
                RemoveLocked();
                finished = TakeCancellationLocked();
            }
            else if (!_completed)
            {
                // 读取在途：由读取任务收尾（缓冲与许可都在它手里），取消在锁外执行。
                abort = _readCancellation;
            }
            else
            {
                buffer = _buffer;
                _buffer = null;
                permits = _permits;
                _permits = null;
                RemoveLocked();
                finished = TakeCancellationLocked();
            }
        }

        if (abort != null)
        {
            try { abort.Cancel(); }
            catch (ObjectDisposedException) { /* 读取刚好完成并收尾，无待中止的读取 */ }
            return;
        }

        finished?.Dispose();
        if (buffer != null)
        {
            AndroidRawReadStats.RecordReturned();
            ImageBufferPool.Return(buffer);
        }
        if (permits != null)
            foreach (var permit in permits) permit.Dispose();
    }

    private CancellationTokenSource? TakeCancellationLocked()
    {
        if (!_closed || _cancellationDisposed) return null;
        _cancellationDisposed = true;
        return _readCancellation;
    }

    private bool TryJoin(DecodePriority priority)
    {
        if (_closed) return false;
        if (_completed && _status != AndroidRawReadStatus.Ready) return false;
        _holders++;
        if (priority < _priority) _priority = priority;
        return true;
    }

    private Task<AndroidRawReadStatus> EnsureReadStarted()
    {
        lock (Gate)
        {
            if (_readTask != null) return _readTask;
            if (_closed) return Task.FromResult(AndroidRawReadStatus.Cancelled);
            return _readTask = Task.Run(ReadAsync);
        }
    }

    /// <summary>唯一一次文件读取：准入 → 打开 → 分块读 → 发布缓冲。</summary>
    private async Task<AndroidRawReadStatus> ReadAsync()
    {
        var token = _readCancellation.Token;
        var permits = new List<AdmissionPermit>();
        byte[]? buffer = null;
        int length = 0;
        bool transferred = false;

        try
        {
            using var stream = await _file.OpenReadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            long size = stream.CanSeek ? stream.Length : -1;
            if (size > MaxBufferBytes)
                return Settle(AndroidRawReadStatus.Failed, $"文件 {size} 字节超过托管缓冲上限");

            // 准入先于整文件 I/O：后台被拒就不再读（不走昂贵回落），P0 豁免但记账。
            var first = TryReserve(size > 0 ? size : UnknownLengthReserveBytes);
            if (first == null)
            {
                AndroidRawReadStats.RecordRefusal();
                return Settle(AndroidRawReadStatus.Refused, $"内存准入被拒（level={MemoryGovernor.CurrentLevel}）");
            }
            permits.Add(first);

            byte[] resolved;
            if (size > 0)
            {
                resolved = DecodedImage.RentPixels((int)size);
                buffer = resolved;
                length = await FillAsync(stream, resolved, (int)size, token).ConfigureAwait(false);
                if (length < 0) return Settle(AndroidRawReadStatus.Failed, "读取不完整（流提前结束）");
            }
            else
            {
                var collected = await ReadUnknownLengthAsync(stream, permits, token).ConfigureAwait(false);
                if (collected.Buffer == null) return Settle(collected.Failure, collected.Detail);
                resolved = collected.Buffer;
                buffer = resolved;
                length = collected.Length;
            }

            // 分段申请的额度合并成实际分配量（多退少补），许可与缓冲同生共死。
            for (int i = 0; i < permits.Count - 1; i++) permits[i].SetActual(0);
            permits[^1].SetActual(resolved.Length);
            AndroidRawReadStats.RecordRead(resolved.Length);

            transferred = true;
            Publish(resolved, length, permits);
            return AndroidRawReadStatus.Ready;
        }
        catch (OperationCanceledException)
        {
            AndroidRawReadStats.RecordCancel();
            return Settle(AndroidRawReadStatus.Cancelled, "消费者全部撤销，读取中止");
        }
        catch (Exception exception)
        {
            AndroidRawReadStats.RecordFailure();
            return Settle(AndroidRawReadStatus.Failed, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (!transferred)
            {
                if (buffer != null) AndroidRawReadStats.RecordReturned();
                ImageBufferPool.Return(buffer);
                foreach (var permit in permits) permit.Dispose();
            }

            CancellationTokenSource? finished;
            lock (Gate) finished = TakeCancellationLocked();
            finished?.Dispose();
        }
    }

    /// <summary>文件字节准入：按当前最强的消费者优先级申请；与 P0 合并后不再按后台拒绝。</summary>
    private AdmissionPermit? TryReserve(long bytes)
    {
        DecodePriority priority;
        lock (Gate) priority = _priority;
        if (MemoryGovernor.TryAcquire(bytes, priority, out var permit)) return permit;

        DecodePriority stronger;
        lock (Gate) stronger = _priority;
        if (stronger == priority) return null;
        return MemoryGovernor.TryAcquire(bytes, stronger, out var retried) ? retried : null;
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, int length, CancellationToken token)
    {
        int read = 0;
        while (read < length)
        {
            token.ThrowIfCancellationRequested();
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), token).ConfigureAwait(false);
            if (chunk <= 0) return -1;
            read += chunk;
        }
        return read;
    }

    /// <summary>
    /// 非寻址流（长度未知）：按段准入读到临时流，再复制进池化缓冲（允许一次临时复制，不是零拷贝）。
    /// 分段预算覆盖临时流按翻倍增长的容量上界；复制瞬间临时流与池化缓冲同时驻留，最终缓冲另申请一次临时许可
    /// ——P0 放行但记账，后台被拒即中止并归还未用的许可。上限与已知长度路径同为 <see cref="MaxBufferBytes"/>。
    /// </summary>
    private async Task<(byte[]? Buffer, int Length, AndroidRawReadStatus Failure, string Detail)> ReadUnknownLengthAsync(
        Stream stream, List<AdmissionPermit> permits, CancellationToken token)
    {
        using var copy = new MemoryStream();
        var chunk = new byte[UnknownLengthChunkBytes];
        long admitted = UnknownLengthReserveBytes;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (count <= 0) break;

            long next = copy.Length + count;
            if (next > MaxBufferBytes)
                return (null, 0, AndroidRawReadStatus.Failed, $"非寻址流超过托管缓冲上限 {MaxBufferBytes} 字节");

            // MemoryStream 容量按翻倍增长，写完后可能是数据量的近 2 倍：分段预算按翻倍上界算。
            long needed = Math.Max(next, 256) * 2;
            if (needed > admitted)
            {
                long extra = Math.Max(UnknownLengthReserveBytes, needed - admitted);
                var more = TryReserve(extra);
                if (more == null)
                {
                    AndroidRawReadStats.RecordRefusal();
                    return (null, 0, AndroidRawReadStatus.Refused, $"非寻址流增长到 {next >> 20}MB 时准入被拒");
                }
                permits.Add(more);
                admitted += extra;
            }
            copy.Write(chunk, 0, count);
        }

        long length = copy.Length;
        if (length <= 0) return (null, 0, AndroidRawReadStatus.Failed, "非寻址流为空");

        var final = TryReserve(length);
        if (final == null)
        {
            AndroidRawReadStats.RecordRefusal();
            return (null, 0, AndroidRawReadStatus.Refused, $"非寻址流定长 {length >> 20}MB 的缓冲准入被拒");
        }
        permits.Add(final);

        var buffer = DecodedImage.RentPixels((int)length);
        try
        {
            copy.Position = 0;
            await copy.ReadExactlyAsync(buffer.AsMemory(0, (int)length), token).ConfigureAwait(false);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
        return (buffer, (int)length, AndroidRawReadStatus.Ready, string.Empty);
    }

    /// <summary>发布缓冲；消费者在读取完成前已全部撤销时立刻归还。</summary>
    private void Publish(byte[] buffer, int length, List<AdmissionPermit> permits)
    {
        bool returnNow;
        lock (Gate)
        {
            _buffer = buffer;
            _length = length;
            _permits = permits;
            _status = AndroidRawReadStatus.Ready;
            _completed = true;
            returnNow = _closed;
            if (returnNow)
            {
                _buffer = null;
                _permits = null;
                RemoveLocked();
            }
        }

        if (!returnNow) return;
        AndroidRawReadStats.RecordReturned();
        ImageBufferPool.Return(buffer);
        foreach (var permit in permits) permit.Dispose();
    }

    /// <summary>记录失败/取消并撤下登记（缓冲与许可由读取任务的 finally / <see cref="Release"/> 收尾）。</summary>
    private AndroidRawReadStatus Settle(AndroidRawReadStatus status, string detail)
    {
        lock (Gate)
        {
            _status = status;
            _detail = detail;
            _completed = true;
            RemoveLocked();
        }
        return status;
    }

    private void RemoveLocked()
    {
        if (Active.TryGetValue(_file, out var current) && ReferenceEquals(current, this)) Active.Remove(_file);
    }
}
