using System;
using System.Collections.Concurrent;
using System.Threading;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 解码像素缓冲池:按尺寸类租用/归还,消灭 42MP 级 byte[] 的 LOH 分配/回收循环
/// (基线实测:每张 124MB 分配自然触发 1 次 gen2 GC,连翻 RSS 峰值 3.37GB 不回落)。
/// 尺寸类:1KB~1MB 用 2 的幂;≥1MB 每八度 8 个子档(粒度 = 2^(k-4),k 为覆盖请求的最小 2 幂指数),
/// 对齐浪费 ≤ 12.5%——33MP 8 位(约 131MB)因此落 128MB 档,不再占 256MB 档。
/// 空闲容量:总字节受 <see cref="MaxIdleBytes"/> 封顶,每个尺寸类另有张数上限(≥16MB 2 个 / ≥1MB 8 个 / 小档 64 个)。
/// 压力:Caution 起清空 ≥1MB 的空闲缓冲,Critical 清空全部;单次释放 ≥64MB 时在线程池后台触发一次 gen2 GC
/// (Trim 契约不阻塞探测线程)。因超帽丢弃的大缓冲累计 ≥256MB 时也后台 gen2 一次(丢弃的数组要等 major GC 才归还系统),
/// <see cref="ReleaseIdle"/> 供缓存腾容量时先释放空闲大缓冲。
/// 约定:解码主路径大图必须经此池;小图/工具图可直接 new(不经 LOH,不 churn)。
/// </summary>
public static class ImageBufferPool
{
    private const int MinBucketShift = 10;    // 1KB 起
    private const int MaxBucketShift = 29;    // 512MB 封顶(超出档域直接 new)
    private const int FineGrainShift = 20;    // 1MB 起改细分档
    private const int SubBucketShift = 4;     // 细分档粒度 2^(k-4):每个八度 8 档
    private const int SubBucketCount = 8;     // 每个八度的新增尺寸类个数
    private const int SmallBucketCount = FineGrainShift - MinBucketShift;
    private const int FineBucketCount = 1 + (MaxBucketShift - FineGrainShift) * SubBucketCount;
    private const int BucketCount = SmallBucketCount + FineBucketCount;
    private const int FineGrainBytes = 1 << FineGrainShift;
    private const int MaxBucketBytes = 1 << MaxBucketShift;
    private const long GcThresholdBytes = 64L * 1024 * 1024;
    // 因超帽丢弃的缓冲累计到该阈值就在线程池触发一次 gen2:大数组变不可达后要等 major GC 才归还系统
    private const long DiscardGcThresholdBytes = 256L * 1024 * 1024;

    private static readonly ConcurrentStack<byte[]>[] _buckets;
    private static readonly int[] _bucketSizes;
    private static readonly int[] _bucketCaps;
    private static long _idleBytes;
    private static long _discardedSinceGc;

    static ImageBufferPool()
    {
        _buckets = new ConcurrentStack<byte[]>[BucketCount];
        _bucketSizes = new int[BucketCount];
        _bucketCaps = new int[BucketCount];
        for (int i = 0; i < BucketCount; i++)
        {
            _buckets[i] = new ConcurrentStack<byte[]>();
            int size = SizeAt(i);
            _bucketSizes[i] = size;
            _bucketCaps[i] = size >= 16 * 1024 * 1024 ? 2 : size >= FineGrainBytes ? 8 : 64;
        }
        MemoryGovernor.Register(new PoolMemoryClient());
    }

    /// <summary>空闲缓冲总字节上限(默认 512MB);归还时若超出,本缓冲直接丢弃。</summary>
    public static long MaxIdleBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>当前空闲缓冲总字节(只读,供缓存/预算记账)。</summary>
    public static long IdleBytes => Interlocked.Read(ref _idleBytes);

    /// <summary>
    /// 租用缓冲:长度 = 尺寸类对齐后的大小(≥ minBytes);超出 512MB 档域直接 new(不入池)。
    /// </summary>
    /// <param name="minBytes">最小字节数。</param>
    public static byte[] Rent(int minBytes)
    {
        if (minBytes <= 0) return [];
        if (!TryGetBucketIndex(minBytes, out int index)) return new byte[minBytes];
        var stack = _buckets[index];
        if (stack.TryPop(out var buffer))
        {
            Interlocked.Add(ref _idleBytes, -buffer.Length);
            return buffer;
        }
        return new byte[_bucketSizes[index]];
    }

    /// <summary>
    /// 归还缓冲。只有长度恰好是某个尺寸类的数组才入池(非本池产物、未对齐长度与超域缓冲直接丢给 GC)。
    /// 因该类张数已满或空闲池超帽而丢给 GC 的大缓冲计入丢弃账,GC 触发见 <see cref="NoteDiscarded"/>。
    /// </summary>
    public static void Return(byte[]? buffer)
    {
        if (buffer == null) return;
        if (!TryGetBucketIndexForLength(buffer.Length, out int index)) return;
        var stack = _buckets[index];
        if (stack.Count >= _bucketCaps[index])
        {
            NoteDiscarded(buffer.Length);
            return;
        }
        if (!TryReserveIdle(_bucketSizes[index]))
        {
            NoteDiscarded(buffer.Length);
            return;
        }
        stack.Push(buffer);
    }

    /// <summary>
    /// 主动释放空闲缓冲(从大档开始),返回实际释放字节数。
    /// 缓存腾容量时优先走这里:空闲池字节同样计入 footprint,但只有释放并 major GC 后才真正归还系统。
    /// </summary>
    /// <param name="bytes">期望释放的字节数(不足则尽量释放)。</param>
    public static long ReleaseIdle(long bytes)
    {
        if (bytes <= 0) return 0;
        long released = 0;
        for (int i = BucketCount - 1; i >= 0 && released < bytes; i--)
            released += DrainBucket(i);
        if (released > 0) NoteDiscarded(released);
        return released;
    }

    /// <summary>
    /// 累计因超帽丢弃的缓冲字节;达到阈值时在线程池触发一次非阻塞 gen2 并清零。
    /// iOS 是 Mono SGen,丢弃的大数组要等 major GC 才把内存还给系统。
    /// </summary>
    private static void NoteDiscarded(long bytes)
    {
        if (bytes <= 0) return;
        long total = Interlocked.Add(ref _discardedSinceGc, bytes);
        if (total < DiscardGcThresholdBytes) return;
        if (Interlocked.CompareExchange(ref _discardedSinceGc, 0, total) != total) return;
        ThreadPool.QueueUserWorkItem(static _ => GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false));
    }

    /// <summary>请求字节数 → 尺寸类下标(长度向上取整到该类的固定大小)。</summary>
    private static bool TryGetBucketIndex(int minBytes, out int index)
    {
        if (minBytes > MaxBucketBytes)
        {
            index = -1;
            return false;
        }
        if (minBytes < FineGrainBytes)
        {
            index = Math.Max(MinBucketShift, Log2Ceil(minBytes)) - MinBucketShift;
            return true;
        }

        int k = Log2Ceil(minBytes);
        int subShift = k - SubBucketShift;
        int units = (int)(((long)minBytes + (1 << subShift) - 1) >> subShift);
        index = SmallBucketCount + FineBucketIndex(k, units);
        return true;
    }

    /// <summary>数组长度 → 尺寸类下标;不是本池尺寸类时返回 false。</summary>
    private static bool TryGetBucketIndexForLength(int length, out int index)
    {
        index = -1;
        if (length < (1 << MinBucketShift) || length > MaxBucketBytes) return false;
        if (length < FineGrainBytes)
        {
            if ((length & (length - 1)) != 0) return false;
            index = Log2Ceil(length) - MinBucketShift;
            return true;
        }

        int k = Log2Ceil(length);
        int subShift = k - SubBucketShift;
        int granularity = 1 << subShift;
        if ((length & (granularity - 1)) != 0) return false;
        int units = length >> subShift;
        if (units is < 9 or > 16) return false;
        int candidate = SmallBucketCount + FineBucketIndex(k, units);
        if (_bucketSizes[candidate] != length) return false;
        index = candidate;
        return true;
    }

    /// <summary>细分档下标:1MB 独占首档,其余按八度 k 与档内 8 等分的位置排列。</summary>
    private static int FineBucketIndex(int k, int units)
        => k == FineGrainShift
            ? 0
            : 1 + (k - FineGrainShift - 1) * SubBucketCount + (units - 9);

    /// <summary>尺寸类下标 → 该类缓冲长度。</summary>
    private static int SizeAt(int index)
    {
        if (index < SmallBucketCount) return 1 << (index + MinBucketShift);
        int fine = index - SmallBucketCount;
        if (fine == 0) return FineGrainBytes;
        int k = FineGrainShift + 1 + (fine - 1) / SubBucketCount;
        int units = 9 + (fine - 1) % SubBucketCount;
        return units << (k - SubBucketShift);
    }

    private static int Log2Ceil(int value)
    {
        int shift = 0;
        while ((1 << shift) < value) shift++;
        return shift;
    }

    /// <summary>以 CAS 预留空闲字节;超过 <see cref="MaxIdleBytes"/> 时不入池。</summary>
    private static bool TryReserveIdle(int size)
    {
        while (true)
        {
            long idle = Interlocked.Read(ref _idleBytes);
            if (idle + size > MaxIdleBytes) return false;
            if (Interlocked.CompareExchange(ref _idleBytes, idle + size, idle) == idle) return true;
        }
    }

    /// <summary>清空一个尺寸类并返回释放的字节数。</summary>
    private static long DrainBucket(int index)
    {
        long released = 0;
        while (_buckets[index].TryPop(out var buffer))
        {
            Interlocked.Add(ref _idleBytes, -buffer.Length);
            released += buffer.Length;
        }
        return released;
    }

    /// <summary>
    /// 池内存客户端:Caution 起清空 ≥1MB 的空闲缓冲,Critical 清空全部;释放量达到 64MB 时
    /// 在线程池上串行 gen2 回收一次,把归还给 GC 的大块 LOH 交还系统(不阻塞调用方)。
    /// </summary>
    private sealed class PoolMemoryClient : IMemoryClient
    {
        public string Name => "ImageBufferPool";

        public long FootprintBytes => IdleBytes;

        public void Trim(PressureLevel level)
        {
            if (level < PressureLevel.Caution) return;
            bool clearAll = level >= PressureLevel.Critical;
            long released = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                if (!clearAll && _bucketSizes[i] < FineGrainBytes) continue;
                released += DrainBucket(i);
            }
            if (released >= GcThresholdBytes)
                ThreadPool.QueueUserWorkItem(static _ => GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false));
        }
    }
}
