using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.Similarity;

/// <summary>
/// 单条相似项,携带源 <see cref="ImageFile"/> 与 0~1 的相似度分数。
/// </summary>
/// <param name="File">相似的图片文件</param>
/// <param name="Score">相似度分数(0~1,越接近 1 越相似)</param>
public sealed record SimilarityItem(ImageFile File, double Score);

/// <summary>
/// 相似聚类服务(阶段 3 占位实现)。
/// 目前用拍摄时间差做模拟分数:Δt 越小,分数越高;阈值以上的项作为相似聚类返回。
/// 后续阶段接入 pHash / 连拍检测时,只需替换 <see cref="ScoreAsync"/> 的内部算法。
/// </summary>
public static class SimilarityService
{
    /// <summary>低于该分数的候选项不会出现在相似列表中。</summary>
    public const double DefaultThreshold = 0.70;

    /// <summary>分数衰减时间常数(秒)。Δt=2s ≈ 0.967, Δt=20s ≈ 0.717, Δt=60s ≈ 0.368。</summary>
    private const double DecayTauSeconds = 60.0;

    /// <summary>
    /// 计算 <paramref name="current"/> 与池中其他文件的相似度,按分数降序返回不低于 <paramref name="threshold"/> 的项。
    /// </summary>
    /// <param name="current">基准图片</param>
    /// <param name="pool">候选池(通常为 <c>FolderViewModel.AllFiles</c>)</param>
    /// <param name="threshold">分数阈值,默认 <see cref="DefaultThreshold"/></param>
    /// <returns>相似项列表(已按 <see cref="SimilarityItem.Score"/> 降序排序,不含 <paramref name="current"/> 自身)</returns>
    public static Task<IReadOnlyList<SimilarityItem>> FindSimilarAsync(
        ImageFile current,
        IReadOnlyList<ImageFile> pool,
        double threshold = DefaultThreshold)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));
        if (pool == null) throw new ArgumentNullException(nameof(pool));

        var currentDate = current.PhotoDate;
        if (currentDate == null)
        {
            return Task.FromResult<IReadOnlyList<SimilarityItem>>(Array.Empty<SimilarityItem>());
        }

        var results = new List<SimilarityItem>(capacity: Math.Min(16, pool.Count));
        foreach (var file in pool)
        {
            if (ReferenceEquals(file, current)) continue;
            var score = Score(currentDate.Value, file.PhotoDate);
            if (score >= threshold)
            {
                results.Add(new SimilarityItem(file, score));
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return Task.FromResult<IReadOnlyList<SimilarityItem>>(results);
    }

    /// <summary>
    /// 基于拍摄时间差计算单对相似度。两端缺时间则视为不相似(返回 0)。
    /// </summary>
    /// <param name="anchor">基准图的拍摄时间</param>
    /// <param name="other">候选图的拍摄时间</param>
    /// <returns>0~1 的相似度分数</returns>
    private static double Score(DateTimeOffset anchor, DateTimeOffset? other)
    {
        if (other == null) return 0.0;
        var delta = Math.Abs((anchor - other.Value).TotalSeconds);
        return Math.Exp(-delta / DecayTauSeconds);
    }
}
