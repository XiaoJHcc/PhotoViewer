using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 一条相似项：源 <see cref="ImageFile"/> 与 0~1 的相似度分数（cosine ∈ [-1,1]，映射到 [0,1]）。
/// </summary>
/// <param name="File">相似的图片文件</param>
/// <param name="Score">相似度分数（0~1，越接近 1 越相似）</param>
public sealed record SimilarityItem(ImageFile File, double Score);

/// <summary>
/// 基于 DINOv3 [CLS] 特征向量的相似聚类服务。
/// - 先算当前图片的特征向量（命中缓存直接用，否则推理一次并回写数据库）；
/// - 候选池里已有特征的并行计算 cosine；缺失的**不**触发推理（用"边看边算"的策略，避免一次切图触发成百上千次推理）；
/// - 按分数降序返回阈值以上项，携带一个 tiebreaker：拍摄时间接近的优先。
/// </summary>
public static class SimilarityService
{
    /// <summary>低于该分数的候选项不会出现在相似列表中。cosine ≥ 0.5 粗略对应"视觉接近"。</summary>
    public const double DefaultThreshold = 0.75;

    /// <summary>单次聚类最多返回的相似项数量上限（避免超大文件夹返回一屏以外的东西）。</summary>
    private const int MaxResults = 64;

    /// <summary>
    /// 计算 <paramref name="current"/> 与池中其他文件的相似度。<br/>
    /// <paramref name="current"/> 必算（优先命中缓存，否则推理并写回）；池内各项只读缓存，未命中不触发推理。
    /// </summary>
    /// <param name="current">基准图片</param>
    /// <param name="pool">候选池（通常为 <c>ThumbnailListViewModel.FilteredFiles</c>）</param>
    /// <param name="threshold">分数阈值，默认 <see cref="DefaultThreshold"/></param>
    /// <param name="ct">取消令牌</param>
    /// <returns>相似项列表（按分数降序，不含 <paramref name="current"/> 自身）</returns>
    public static async Task<IReadOnlyList<SimilarityItem>> FindSimilarAsync(
        ImageFile current,
        IReadOnlyList<ImageFile> pool,
        double threshold = DefaultThreshold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(pool);

        var anchor = await DinoFeatureCache.GetOrComputeAsync(current, ct).ConfigureAwait(false);
        if (anchor == null) return Array.Empty<SimilarityItem>();

        var results = new ConcurrentBag<SimilarityItem>();
        var anchorTime = current.PhotoDate;

        // 候选池"只读缓存 + miss 后台补算"：当前调用只看数据库已有的项，保证面板响应时间稳定；
        // 新滑到的图片在被查看时由 anchor 路径带入数据库，下一次就能参与聚类。
        var tasks = new List<Task>(pool.Count);
        foreach (var candidate in pool)
        {
            if (ReferenceEquals(candidate, current)) continue;

            tasks.Add(Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                var vector = await DinoFeatureCache.TryReadAsync(candidate, ct).ConfigureAwait(false);
                if (vector == null) return;

                var score = CosineToUnit(Cosine(anchor, vector));
                if (score >= threshold)
                {
                    results.Add(new SimilarityItem(candidate, score));
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SimilarityItem>();
        }

        ct.ThrowIfCancellationRequested();

        // 拍摄时间差仅作 tiebreaker：分数相同时更近的排前面。
        var ordered = new List<SimilarityItem>(results);
        ordered.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            var da = TimeDistanceSeconds(anchorTime, a.File.PhotoDate);
            var db = TimeDistanceSeconds(anchorTime, b.File.PhotoDate);
            return da.CompareTo(db);
        });

        if (ordered.Count > MaxResults)
        {
            ordered.RemoveRange(MaxResults, ordered.Count - MaxResults);
        }
        return ordered;
    }

    /// <summary>两个等长向量的 cosine 相似度（两侧已 L2 归一化时等价于点积）。</summary>
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary>把 cosine ∈ [-1, 1] 映射到 [0, 1]；归一化向量的 cosine 理论上已非负，但留余量。</summary>
    private static double CosineToUnit(double cos) => Math.Clamp((cos + 1.0) * 0.5, 0.0, 1.0);

    /// <summary>拍摄时间差的绝对秒数；缺任一端视为无穷远，让 tiebreaker 不强行干预。</summary>
    private static double TimeDistanceSeconds(DateTimeOffset? anchor, DateTimeOffset? other)
    {
        if (!anchor.HasValue || !other.HasValue) return double.MaxValue;
        return Math.Abs((anchor.Value - other.Value).TotalSeconds);
    }
}
