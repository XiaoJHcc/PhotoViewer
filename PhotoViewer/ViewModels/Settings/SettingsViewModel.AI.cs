using System;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel
{
    //////////////
    /// AI / 相似聚类
    //////////////

    /// <summary>相似聚类阈值滑块的下限（75%）。</summary>
    public const double SimilarityThresholdMin = 0.75;

    /// <summary>相似聚类阈值滑块的上限（95%）。</summary>
    public const double SimilarityThresholdMax = 0.95;

    /// <summary>相似聚类最多返回项数滑块的下限。</summary>
    public const int SimilarityMaxResultsMin = 1;

    /// <summary>相似聚类最多返回项数滑块的上限。</summary>
    public const int SimilarityMaxResultsMax = 32;

    private double _similarityThreshold = 0.85;
    /// <summary>
    /// 相似聚类列表中显示的最低相似度（cosine 归一化分数，0~1）。
    /// </summary>
    public double SimilarityThreshold
    {
        get => _similarityThreshold;
        set
        {
            var clamped = Math.Clamp(value, SimilarityThresholdMin, SimilarityThresholdMax);
            this.RaiseAndSetIfChanged(ref _similarityThreshold, clamped);
        }
    }

    private int _similarityMaxResults = 8;
    /// <summary>
    /// 相似聚类一次最多返回的项数（1~32）。
    /// </summary>
    public int SimilarityMaxResults
    {
        get => _similarityMaxResults;
        set
        {
            var clamped = Math.Clamp(value, SimilarityMaxResultsMin, SimilarityMaxResultsMax);
            this.RaiseAndSetIfChanged(ref _similarityMaxResults, clamped);
            this.RaisePropertyChanged(nameof(SimilarityMaxResultsExp));
        }
    }

    /// <summary>
    /// 1~32 的指数映射（0~1）；让滑条在低值区间分辨率更高，匹配 1/2/4/8/16/32 的直觉。
    /// </summary>
    public double SimilarityMaxResultsExp
    {
        get => ToExp(SimilarityMaxResults, SimilarityMaxResultsMin, SimilarityMaxResultsMax);
        set => SimilarityMaxResults = FromExp(value, SimilarityMaxResultsMin, SimilarityMaxResultsMax);
    }
}
