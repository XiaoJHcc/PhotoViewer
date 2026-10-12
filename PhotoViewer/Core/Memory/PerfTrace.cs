using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoViewer.Core.Memory;

/// <summary>
/// 解码链路计时采样(环形缓冲)。供 <see cref="MemoryGovernor"/> 压力快照落盘与验收门判读。
/// 只记录、不决策;缓冲区有界,线程安全。
/// </summary>
public static class PerfTrace
{
    /// <summary>一次解码计时样本。</summary>
    /// <param name="Time">采样时间(UTC)。</param>
    /// <param name="FileName">文件名(不含目录,控制日志体积)。</param>
    /// <param name="CacheHit">是否缓存命中。</param>
    /// <param name="TotalMs">调用方视角端到端耗时。</param>
    /// <param name="DecodeMs">解码本体耗时(miss 时有效;命中为 0)。</param>
    /// <param name="Width">产物像素宽(命中取缓存项)。</param>
    /// <param name="Height">产物像素高。</param>
    public readonly record struct DecodeSample(
        DateTimeOffset Time, string FileName, bool CacheHit, double TotalMs, double DecodeMs, int Width, int Height);

    private const int Capacity = 200;
    private static readonly object _gate = new();
    private static readonly Queue<DecodeSample> _samples = new(Capacity);

    /// <summary>
    /// 记录一次解码样本;超出容量时丢弃最旧样本。
    /// </summary>
    public static void RecordDecode(string fileName, bool cacheHit, double totalMs, double decodeMs, int width, int height)
    {
        lock (_gate)
        {
            if (_samples.Count >= Capacity) _samples.Dequeue();
            _samples.Enqueue(new DecodeSample(DateTimeOffset.UtcNow, fileName, cacheHit, totalMs, decodeMs, width, height));
        }
    }

    /// <summary>取当前样本快照(旧→新)。</summary>
    public static DecodeSample[] Snapshot()
    {
        lock (_gate) return _samples.ToArray();
    }

    /// <summary>
    /// 最近 miss 样本的统计(供验收门"P0 延迟下降 ≥50%"判读)。
    /// </summary>
    /// <param name="take">参与统计的最近 miss 样本数。</param>
    /// <returns>(样本数, p50, mean);无样本时为 (0, 0, 0)。</returns>
    public static (int Count, double P50, double Mean) RecentMissStats(int take = 50)
    {
        DecodeSample[] snap = Snapshot();
        var miss = snap.Where(s => !s.CacheHit).Select(s => s.TotalMs).OrderBy(x => x).ToList();
        if (miss.Count == 0) return (0, 0, 0);
        var tail = miss.Skip(Math.Max(0, miss.Count - take)).ToList();
        return (tail.Count, tail[tail.Count / 2], tail.Average());
    }
}
