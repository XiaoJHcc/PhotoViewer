using System;
using System.Collections.Concurrent;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 解码像素缓冲池:按 2 的幂分档租用/归还,消灭 42MP 级 byte[] 的 LOH 分配/回收循环
/// (基线实测:每张 124MB 分配自然触发 1 次 gen2 GC,连翻 RSS 峰值 3.37GB 不回落)。
/// 档帽:≥16MB 每档 4 个,≥1MB 每档 16 个,小档 64 个;Critical 压力整池清空。
/// 约定:解码主路径大图必须经此池;小图/工具图可直接 new(不经 LOH,不 churn)。
/// </summary>
public static class ImageBufferPool
{
    private const int MinBucketShift = 10; // 1KB 起
    private const int MaxBucketShift = 29; // 512MB 封顶(42MP BGRA ≈ 124MB → 128MB 桶)

    private static readonly ConcurrentStack<byte[]>[] _buckets;

    static ImageBufferPool()
    {
        _buckets = new ConcurrentStack<byte[]>[MaxBucketShift - MinBucketShift + 1];
        for (int i = 0; i < _buckets.Length; i++)
            _buckets[i] = new ConcurrentStack<byte[]>();
        MemoryGovernor.Register(new PoolMemoryClient());
    }

    /// <summary>
    /// 租用缓冲:长度 = 不小于 minBytes 的最小 2 幂(分档对齐);超出档域直接 new(不入池)。
    /// </summary>
    /// <param name="minBytes">最小字节数。</param>
    public static byte[] Rent(int minBytes)
    {
        if (minBytes <= 0) return [];
        int shift = BucketShift(minBytes);
        if (shift > MaxBucketShift) return new byte[minBytes];
        var stack = _buckets[shift - MinBucketShift];
        return stack.TryPop(out var buf) ? buf : new byte[1 << shift];
    }

    /// <summary>
    /// 归还缓冲。非池化来源(非 2 幂长度)与超帽部分直接丢给 GC。
    /// </summary>
    public static void Return(byte[]? buffer)
    {
        if (buffer == null) return;
        int len = buffer.Length;
        if ((len & (len - 1)) != 0) return; // 非 2 幂,非本池产物
        int shift = Log2(len);
        if (shift < MinBucketShift || shift > MaxBucketShift) return;
        var stack = _buckets[shift - MinBucketShift];
        if (stack.Count >= BucketCap(shift)) return;
        stack.Push(buffer);
    }

    private static int BucketShift(int minBytes)
    {
        int s = MinBucketShift;
        while ((1 << s) < minBytes) s++;
        return s;
    }

    private static int BucketCap(int shift) => shift >= 24 ? 4 : shift >= 20 ? 16 : 64;

    private static int Log2(int v)
    {
        int s = 0;
        while ((1 << s) < v) s++;
        return s;
    }

    /// <summary>池内存客户端:Critical 整池清空(其余级别保留,服务在途解码复用)。</summary>
    private sealed class PoolMemoryClient : IMemoryClient
    {
        public string Name => "ImageBufferPool";
        public long FootprintBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _buckets.Length; i++)
                    total += (long)_buckets[i].Count << (i + MinBucketShift);
                return total;
            }
        }
        public void Trim(PressureLevel level)
        {
            if (level < PressureLevel.Critical) return;
            foreach (var bucket in _buckets)
                bucket.Clear();
        }
    }
}
