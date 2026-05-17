using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;

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

    // ── 清除特征数据库(开发者用)─────────────────────────────────────────────

    private bool _clearDbConfirming;
    /// <summary>
    /// 二次确认状态:首次点击进入确认态,3 秒内再次点击才真正清库。
    /// 用 IsVisible 切换两段文案,避免引入弹窗依赖。
    /// </summary>
    public bool ClearDbConfirming
    {
        get => _clearDbConfirming;
        private set
        {
            this.RaiseAndSetIfChanged(ref _clearDbConfirming, value);
            this.RaisePropertyChanged(nameof(ClearDbButtonText));
        }
    }

    /// <summary>清除按钮当前文案:首次为"清除特征数据库",确认态为"再次点击确认清除"。</summary>
    public string ClearDbButtonText => _clearDbConfirming ? "再次点击确认清除" : "清除特征数据库";

    private string _clearDbStatus = "";
    /// <summary>清除完成后的状态文本(成功 / 失败 / 操作进行中);空字符串表示无状态。</summary>
    public string ClearDbStatus
    {
        get => _clearDbStatus;
        private set => this.RaiseAndSetIfChanged(ref _clearDbStatus, value);
    }

    private bool _clearDbBusy;
    /// <summary>清除任务正在执行,按钮置灰避免重入。</summary>
    public bool ClearDbBusy
    {
        get => _clearDbBusy;
        private set => this.RaiseAndSetIfChanged(ref _clearDbBusy, value);
    }

    /// <summary>
    /// AI 设置页"清除特征数据库"按钮命令。
    /// 首次点击进入确认态(3 秒内有效);确认态再次点击才真正执行 — 关闭连接、删 photos.db / -wal / -shm、
    /// 清空 <see cref="DinoFeatureCache"/> 进程缓存,然后重建空库。
    /// 不重启应用;相似聚类面板会在下次 OnPanelOpened 时自动统计未提取数。
    /// 用 lazy-init 避免触碰主 ctor — partial 类同义。
    /// </summary>
    public ReactiveCommand<Unit, Unit> ClearDatabaseCommand =>
        _clearDatabaseCommand ??= ReactiveCommand.CreateFromTask(ExecuteClearDatabaseAsync);
    private ReactiveCommand<Unit, Unit>? _clearDatabaseCommand;

    private async Task ExecuteClearDatabaseAsync()
    {
        if (_clearDbBusy) return;

        if (!_clearDbConfirming)
        {
            ClearDbConfirming = true;
            ClearDbStatus = "";
            // 3 秒后若用户没再点,自动复位
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                if (_clearDbConfirming) ClearDbConfirming = false;
            });
            return;
        }

        ClearDbConfirming = false;
        ClearDbBusy = true;
        ClearDbStatus = "清除中…";
        try
        {
            await PhotoDatabase.DeleteDatabaseAsync().ConfigureAwait(true);
            DinoFeatureCache.InvalidateAll();
            ShakeFlagService.InvalidateAll();
            AnalysisResultCache.InvalidateAll();
            ClearDbStatus = "已清除";
        }
        catch (Exception ex)
        {
            ClearDbStatus = $"失败:{ex.Message}";
            Console.WriteLine($"[Settings] clear database failed: {ex.Message}");
        }
        finally
        {
            ClearDbBusy = false;
        }
    }
}
