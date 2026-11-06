using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// 最多保留一份已开发的 RAW 全分辨率 BGRA 源像素,供紧接的 proxy → Full 与 Full 预热共享同一次 WIC 开发。
/// </summary>
internal static class WindowsRawSourceCache
{
    /// <summary>保留容量的绝对封顶。</summary>
    internal const long RetainCeilingBytes = 256L * 1024 * 1024;

    /// <summary>保留时长:到期由独立定时器释放,命中不续期。</summary>
    internal static readonly TimeSpan RetainDuration = TimeSpan.FromSeconds(3);

    private const long BudgetShareDivisor = 32;
    private const long AvailableSafetyMarginBytes = 64L * 1024 * 1024;
    private const int GateCount = 64;

    private static readonly object RetainLock = new();
    private static readonly SemaphoreSlim[] Gates;
    private static Entry? _retained;
    private static int _developmentCount;

    static WindowsRawSourceCache()
    {
        Gates = new SemaphoreSlim[GateCount];
        for (var index = 0; index < Gates.Length; index++) Gates[index] = new SemaphoreSlim(1, 1);
        MemoryGovernor.Register(new MemoryClient());
    }

    /// <summary>文件身份(存储 id)+ revision(长度 + 修改时间)。</summary>
    internal readonly record struct Key(string StorageId, string Path, StorageRevision Revision);

    /// <summary>保留判定所用的预算快照。</summary>
    internal readonly record struct Snapshot(long BudgetBytes, PressureLevel Level, bool RecoveryHold, long AvailableBytes);

    /// <summary>验收钩子:注入预算快照;null 表示实时读 MemoryGovernor。</summary>
    internal static Snapshot? SnapshotForChecks { get; set; }

    /// <summary>实际发生的 WIC 全分辨率开发次数(独立参考解码不计入)。</summary>
    internal static int DevelopmentCount => Volatile.Read(ref _developmentCount);

    internal static long RetainedBytesForChecks
    {
        get { lock (RetainLock) return _retained?.Image.ByteCount ?? 0; }
    }

    internal static int RetainedCountForChecks
    {
        get { lock (RetainLock) return _retained is null ? 0 : 1; }
    }

    internal static Key? TryCreateKey(IFileRef file, string path)
        => RevisionOf(path) is { } revision ? new Key(file.StorageId.Value, path, revision) : null;

    /// <summary>命中时借出一份租约(调用方 Release);未命中或已过期返回 null。</summary>
    internal static (DecodedImage Image, int? Orientation)? TryAcquire(Key key)
    {
        Entry? discarded = null;
        (DecodedImage Image, int? Orientation)? result = null;
        var snapshot = CurrentSnapshot();
        lock (RetainLock)
        {
            var entry = _retained;
            if (entry is null) return null;
            if (DateTimeOffset.UtcNow >= entry.ExpiresAt || !ShouldRetain(entry.Image.ByteCount,
                snapshot.BudgetBytes, snapshot.Level, snapshot.RecoveryHold, snapshot.AvailableBytes))
            {
                _retained = null;
                discarded = entry;
            }
            else if (entry.Key == key && entry.Image.TryAcquire()) result = (entry.Image, entry.Orientation);
        }
        Release(discarded);
        return result;
    }

    /// <summary>
    /// 命中则借出;否则开发一次。同文件按固定条带闸门串行,后来者等到闸门后通常直接复用前人成果;
    /// 等待用调用方自己的取消令牌,取消只放弃本次等待,不影响别人的开发与借用中的源。
    /// </summary>
    internal static async Task<(DecodedImage Image, int? Orientation)?> AcquireOrDevelopAsync(
        Key key,
        CancellationToken cancellationToken,
        Func<Task<(DecodedImage? Image, int? Orientation)>> develop)
    {
        if (TryAcquire(key) is { } retained) return retained;

        var gate = GateFor(key.StorageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return TryAcquire(key) is { } ready ? ready : await DevelopAsync(key, develop).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>清空保留项(压力修剪与验收共用)。</summary>
    internal static void Drop() => DropRetained(null);

    internal static void ResetForChecks()
    {
        Drop();
        Interlocked.Exchange(ref _developmentCount, 0);
    }

    /// <summary>保留闸门:容量 min(封顶, 预算/32)、压力与恢复观察期、可用量余量。</summary>
    internal static bool ShouldRetain(long bytes, long budgetBytes, PressureLevel level, bool recoveryHold, long availableBytes)
    {
        if (bytes <= 0 || level > PressureLevel.Normal || recoveryHold) return false;
        if (budgetBytes <= 0) return false;
        if (bytes > Math.Min(RetainCeilingBytes, budgetBytes / BudgetShareDivisor)) return false;
        return availableBytes >= 0 && bytes + AvailableSafetyMarginBytes <= availableBytes;
    }

    private static bool CanRetain(long bytes)
    {
        var snapshot = CurrentSnapshot();
        return ShouldRetain(bytes, snapshot.BudgetBytes, snapshot.Level, snapshot.RecoveryHold, snapshot.AvailableBytes);
    }

    private static Snapshot CurrentSnapshot()
    {
        if (SnapshotForChecks is { } injected) return injected;

        long budget = MemoryGovernor.BudgetBytes;
        long footprint = MemoryGovernor.FootprintBytes;
        long systemAvailable = MemoryGovernor.GetSystemMemoryInfo().AvailableBytes;
        // 进程余量同样收窄可用量:自己已逼近预算时不再留源。
        long headroom = budget > 0 && footprint > 0 ? Math.Max(0, budget - footprint) : long.MaxValue;
        return new Snapshot(budget, MemoryGovernor.CurrentLevel, MemoryGovernor.InRecoveryHold,
            systemAvailable < 0 ? -1 : Math.Min(systemAvailable, headroom));
    }

    private static void TryRetain(Key key, DecodedImage image, int? orientation)
    {
        if (!CanRetain(image.ByteCount)) return;
        if (!image.TryAcquire()) return;

        var entry = new Entry(key, image, orientation, DateTimeOffset.UtcNow + RetainDuration);
        var timer = new Timer(static state => DropRetained(state as Entry), entry, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        entry.Timer = timer;
        Entry? previous;
        lock (RetainLock)
        {
            previous = _retained;
            _retained = entry;
            // 定时器在锁内启动:发布前不会触发,不会留下没人清的过期条目。
            timer.Change(RetainDuration, Timeout.InfiniteTimeSpan);
        }

        Release(previous);
        // 发布期间压力级联的 Trim 可能先于本次发布跑过:复查后只撤自己这一份。
        if (!CanRetain(image.ByteCount)) DropRetained(entry);
    }

    private static void DropRetained(Entry? only)
    {
        Entry? entry;
        lock (RetainLock)
        {
            entry = _retained;
            if (entry is null || (only is not null && !ReferenceEquals(entry, only))) return;
            _retained = null;
        }

        Release(entry);
    }

    /// <summary>归还保留项自己那一份租约,幂等(定时器、Trim、复查可能同时到达)。</summary>
    private static void Release(Entry? entry)
    {
        if (entry is null || Interlocked.Exchange(ref entry.Released, 1) != 0) return;
        entry.Timer?.Dispose();
        entry.Timer = null;
        entry.Image.Release();
    }

    private static async Task<(DecodedImage Image, int? Orientation)?> DevelopAsync(
        Key key,
        Func<Task<(DecodedImage? Image, int? Orientation)>> develop)
    {
        Interlocked.Increment(ref _developmentCount);

        (DecodedImage? Image, int? Orientation) developed;
        try
        {
            developed = await develop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawSourceCache] RAW 源开发失败:{key.Path} {exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message}");
            return null;
        }

        if (developed.Image is not { } image) return null;

        // 开发期间文件被改/删:像素可能是两代内容的混合,不当作有效源。
        if (RevisionOf(key.Path) is not { } revision || !revision.Equals(key.Revision))
        {
            image.Release();
            Console.WriteLine($"[WindowsRawSourceCache] RAW 开发期间文件变化,丢弃本次源:{key.Path}");
            return null;
        }

        TryRetain(key, image, developed.Orientation);
        return (image, developed.Orientation);
    }

    private static StorageRevision? RevisionOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new StorageRevision(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WindowsRawSourceCache] 读取文件 revision 失败:{path} {exception.GetType().Name} {exception.Message}");
            return null;
        }
    }

    /// <summary>固定条带闸门:同一 StorageId 始终落同一条,零增长。</summary>
    private static SemaphoreSlim GateFor(string storageId)
        => Gates[(int)((uint)StringComparer.Ordinal.GetHashCode(storageId) % (uint)GateCount)];

    private sealed class Entry
    {
        internal Entry(Key key, DecodedImage image, int? orientation, DateTimeOffset expiresAt)
        {
            Key = key;
            Image = image;
            Orientation = orientation;
            ExpiresAt = expiresAt;
        }

        internal Key Key { get; }

        internal DecodedImage Image { get; }

        internal int? Orientation { get; }

        internal DateTimeOffset ExpiresAt { get; }

        internal Timer? Timer { get; set; }

        internal int Released;
    }

    private sealed class MemoryClient : IMemoryClient
    {
        public string Name => "WindowsRawSourceCache";

        public long FootprintBytes => RetainedBytesForChecks;

        public void Trim(PressureLevel level) => DropRetained(null);
    }
}
