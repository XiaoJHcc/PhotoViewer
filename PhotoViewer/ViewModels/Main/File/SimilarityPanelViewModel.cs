using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 相似聚类面板的视图模型。
/// 仅当面板展开且文件栏可见时,才在 <see cref="MainViewModel.CurrentFile"/> 切换后异步调用
/// <see cref="SimilarityService"/> 计算相似项(切图不隐式推理,SCOPE-01),并暴露给
/// <c>SimilarityListView</c>;同时通过 <see cref="ThumbnailListViewModel"/> 的加载队列,
/// 触发相似项的缩略图加载。面板关闭时取消在途计算,重新展开后按当前图重算。
/// 面板由两组正交开关驱动:<see cref="HasItems"/> 控制列表/占位,
/// <see cref="ShowActionArea"/>(= <see cref="HasUnindexed"/> 或 <see cref="IsStateIndexing"/>) 控制按钮/进度条区域。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _thumbnailList;
    private readonly FolderViewModel _folder;

    private CancellationTokenSource? _computeCts;

    /// <summary>未提取统计的单飞任务:并发调用共享同一轮扫描。</summary>
    private Task? _unindexedTask;

    /// <summary>
    /// 一次性抑制位:由 <see cref="SelectItemCommand"/> 置位,
    /// 目的是让"点击相似项切换主图"这一动作不反过来刷新相似面板本身(避免锚点漂移)。
    /// 只对"由自身触发的那一次 <see cref="MainViewModel.CurrentFile"/> 变更"生效,随即复位。
    /// </summary>
    private bool _suppressNextRecompute;

    /// <summary>
    /// 当前相似列表的锚点文件(即"以谁为中心找相似")。
    /// 点击相似项切主图时锚点不变,仅在主缩略图列表正常切图时更新。
    /// </summary>
    private ImageFile? _anchorFile;

    /// <summary>面板是否展开且文件栏可见（面板真正可见的唯一判据；不可见时不触发任何隐式推理）。</summary>
    private bool IsPanelVisible => _main.Settings.SimilarityPanelExpanded && _main.IsThumbnailViewVisible;

    private IReadOnlyList<SimilarityItem> _similarItems = Array.Empty<SimilarityItem>();
    /// <summary>当前相似项列表(已按分数降序),绑定到 SimilarityListView 的 ItemsSource。</summary>
    public IReadOnlyList<SimilarityItem> SimilarItems
    {
        get => _similarItems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _similarItems, value);
            this.RaisePropertyChanged(nameof(HasItems));
            this.RaisePropertyChanged(nameof(ShowFullPlaceholder));
            this.RaisePropertyChanged(nameof(ShowUnindexedPlaceholder));
            this.RaisePropertyChanged(nameof(ActionButtonText));
        }
    }

    /// <summary>是否有相似项(便于直接绑定 IsVisible)。</summary>
    public bool HasItems => _similarItems.Count > 0;

    /// <summary>当前布局是否为行布局(分栏位于上下,决定列表方向、对齐)。</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>主视图模型引用,模板内绑定 IsCurrent / Settings 用。</summary>
    public MainViewModel Main => _main;

    /// <summary>设置引用,供模板绑定 ShowRating 等。</summary>
    public SettingsViewModel Settings => _main.Settings;

    // ── 进度与覆盖度 ───────────────────────────────────────────────────────

    private bool _isStateIndexing;
    /// <summary>是否正在批量提取特征。</summary>
    public bool IsStateIndexing
    {
        get => _isStateIndexing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isStateIndexing, value);
            this.RaisePropertyChanged(nameof(ShowActionArea));
            this.RaisePropertyChanged(nameof(ShowActionButton));
            this.RaisePropertyChanged(nameof(ShowActionAreaError));
        }
    }

    /// <summary>IndexTotal 为 0 时进度条显示不定态。</summary>
    public bool IsIndexTotalUnknown => _indexTotal == 0;

    private int _indexProgress;
    /// <summary>批量提取进度(已完成张数)。</summary>
    public int IndexProgress
    {
        get => _indexProgress;
        private set => this.RaiseAndSetIfChanged(ref _indexProgress, value);
    }

    private int _indexTotal;
    /// <summary>批量提取总张数。</summary>
    public int IndexTotal
    {
        get => _indexTotal;
        private set
        {
            this.RaiseAndSetIfChanged(ref _indexTotal, value);
            this.RaisePropertyChanged(nameof(IsIndexTotalUnknown));
        }
    }

    private int _unindexedCount;
    /// <summary>未计算特征的张数;= 0 表示当前文件夹全部已入库。</summary>
    public int UnindexedCount
    {
        get => _unindexedCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unindexedCount, value);
            this.RaisePropertyChanged(nameof(HasUnindexed));
            this.RaisePropertyChanged(nameof(ShowActionArea));
            this.RaisePropertyChanged(nameof(ShowActionButton));
            this.RaisePropertyChanged(nameof(ShowActionAreaError));
            this.RaisePropertyChanged(nameof(ShowFullPlaceholder));
            this.RaisePropertyChanged(nameof(ShowUnindexedPlaceholder));
            this.RaisePropertyChanged(nameof(ActionButtonText));
        }
    }

    /// <summary>是否存在未提取特征的照片。</summary>
    public bool HasUnindexed => _unindexedCount > 0;

    /// <summary>是否显示底部按钮/进度条区域。</summary>
    public bool ShowActionArea => HasUnindexed || IsStateIndexing;

    /// <summary>按钮(非提取中时)是否可见。</summary>
    public bool ShowActionButton => HasUnindexed && !IsStateIndexing;

    /// <summary>按钮文案:已有列表则为"补齐全部",否则"提取全部"。</summary>
    public string ActionButtonText => HasItems ? "补齐全部" : "提取全部";

    /// <summary>
    /// "无相似照片"占位:无列表项 且 无未提取(全部已入库的空结果)。
    /// 此时没有按钮区,占位文字居中显示。
    /// </summary>
    public bool ShowFullPlaceholder => !HasItems && !HasUnindexed;

    /// <summary>
    /// "尚未提取特征"占位:无列表项 且 存在未提取。
    /// 此时按钮区可见,占位文字与按钮共用 StackPanel 居中排布。
    /// </summary>
    public bool ShowUnindexedPlaceholder => !HasItems && HasUnindexed;

    private string? _indexError;
    /// <summary>
    /// 批量提取失败时的错误信息;空态与列表态共用同一份,不只在无相似项时赋值(SIM-10)。
    /// </summary>
    public string? IndexError
    {
        get => _indexError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _indexError, value);
            this.RaisePropertyChanged(nameof(HasIndexError));
            this.RaisePropertyChanged(nameof(ShowActionAreaError));
        }
    }

    /// <summary>是否存在提取错误(便于绑定 IsVisible)。</summary>
    public bool HasIndexError => _indexError != null;

    /// <summary>
    /// 错误文案是否应显示在动作区:列表态(有相似项)与空态都可绑定,
    /// 让错误不再只挂在空态栈里(SIM-10)。
    /// </summary>
    public bool ShowActionAreaError => HasIndexError && ShowActionArea;

    private FolderFeatureIndexer? _indexer;

    // ── 构造 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构造相似聚类面板视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="thumbnailList">主缩略图列表 VM,候选池取自其 <see cref="ThumbnailListViewModel.FilteredFiles"/></param>
    /// <param name="folder">文件源 VM,用于获取全文件夹列表</param>
    public SimilarityPanelViewModel(MainViewModel main, ThumbnailListViewModel thumbnailList, FolderViewModel folder)
    {
        _main = main;
        _thumbnailList = thumbnailList;
        _folder = folder;

        _main.WhenAnyValue(x => x.IsRowLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRowLayout)));

        // 切图只在面板展开且可见时重算;关闭状态下仅记住锚点,不触发任何解码或推理。
        _main.WhenAnyValue(x => x.CurrentFile)
            .Subscribe(file =>
            {
                if (_suppressNextRecompute)
                {
                    _suppressNextRecompute = false;
                    return;
                }
                _anchorFile = file;
                if (IsPanelVisible) _ = RecomputeAsync(file);
            });

        // 候选池来自主缩略图列表的 FilteredFiles(已应用同名分组与筛选);
        // 列表整体替换时(开关分组、改筛选、切文件夹)同步重算,避免相似面板出现"被合并掉的伴侣文件"或"自身的另一格式"。
        // 使用 _anchorFile 而非 CurrentFile,避免用户从相似列表选中某项后改星级触发 FilteredFiles 重建时锚点漂移。
        _thumbnailList.WhenAnyValue(x => x.FilteredFiles)
            .Skip(1)
            .Subscribe(__ => { if (IsPanelVisible) _ = RecomputeAsync(_anchorFile); });

        // 设置变化（阈值 / 最多数量）即时重算当前列表。
        _main.Settings.WhenAnyValue(x => x.SimilarityThreshold, x => x.SimilarityMaxResults)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(UiDispatcher.MainThreadScheduler)
            .Subscribe(tuple => { if (IsPanelVisible) _ = RecomputeAsync(_anchorFile); });

        // 面板展开（或文件栏重新可见）时按当前图重算;关闭时取消在途任务。
        _main.Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.SimilarityPanelExpanded))
                OnPanelVisibilityChanged();
        };

        _main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsThumbnailViewVisible))
                OnPanelVisibilityChanged();
        };

        // 切换/加载文件夹后重新统计未提取数量,避免面板停留在旧文件夹的状态。
        _folder.AllFilesChanged += () =>
        {
            if (IsPanelVisible)
                _ = EvaluateUnindexedAsync();
        };

        // 面板展开即按当前锚点算一次:旧壳靠切图时顺带算,原生壳只在面板可见时算(SCOPE-01 不在切图隐式推理)。
        _main.Settings.WhenAnyValue(x => x.SimilarityPanelExpanded)
            .Skip(1)
            .Where(open => open)
            .Subscribe(open =>
            {
                _ = EvaluateUnindexedAsync();
                _ = RecomputeAsync(_anchorFile ?? _main.CurrentFile);
            });
    }

    // ── 公共命令 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 选中相似项(命令绑定),切换主视图当前图片但保留主缩略图列表的锚点高亮。
    /// 同时抑制本次 <see cref="MainViewModel.CurrentFile"/> 变更对相似面板的反向刷新,
    /// 保持原锚点对应的相似列表不被重排,避免操作混乱。
    /// </summary>
    /// <param name="item">被点击的相似项</param>
    public void SelectItemCommand(SimilarityItem item)
    {
        if (item?.File == null) return;
        if (ReferenceEquals(item.File, _main.CurrentFile)) return;

        _suppressNextRecompute = true;
        _main.SetCurrentImageKeepAnchor(item.File);
    }

    /// <summary>
    /// 面板展开时调用:按当前图重算相似列表,并重新统计当前文件夹未提取张数。
    /// 旧壳的 toggle 与原生面板首次可见都走这里;面板已关闭时由 <see cref="OnPanelVisibilityChanged"/> 丢弃。
    /// </summary>
    public void OnPanelOpened()
    {
        _anchorFile = _main.CurrentFile;
        _ = EvaluateUnindexedAsync();
        _ = RecomputeAsync(_anchorFile);
    }

    /// <summary>
    /// 面板可见性变化:展开且文件栏可见时按当前图重算;否则取消在途计算,后续切图不再触发推理。
    /// </summary>
    private void OnPanelVisibilityChanged()
    {
        if (IsPanelVisible)
            OnPanelOpened();
        else
            CancelPendingCompute();
    }

    /// <summary>取消在途相似度计算(面板关闭或文件栏隐藏)。</summary>
    private void CancelPendingCompute()
    {
        _computeCts?.Cancel();
        _computeCts = null;
    }

    /// <summary>
    /// 触发全文件夹批量特征提取("提取全部" / "补齐全部" 按钮)。
    /// </summary>
    public void StartIndexingCommand()
    {
        if (IsStateIndexing) return;
        _ = RunIndexingAsync();
    }

    // ── 未提取统计 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 扫描当前文件夹,按指纹聚合后更新 <see cref="UnindexedCount"/>。
    /// 同次曝光的 RAW+HEIF/JPG 共享指纹,只计为一张待提取。
    /// 单飞:展开面板、文件夹变化、提取完成都会调用,大文件夹各跑一遍会叠加成全量 EXIF 风暴。
    /// </summary>
    private Task EvaluateUnindexedAsync()
    {
        if (_unindexedTask is { IsCompleted: false })
        {
            return _unindexedTask;
        }

        _unindexedTask = EvaluateUnindexedCoreAsync();
        return _unindexedTask;
    }

    /// <summary>未提取统计的实际工作体。</summary>
    private async Task EvaluateUnindexedCoreAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0)
        {
            UiDispatcher.Post(() => UnindexedCount = 0);
            return;
        }

        var groups = await FolderFeatureIndexer.GroupByFingerprintAsync(files).ConfigureAwait(false);

        // 逐组查询库状态:串行即可,并行开连接反而会撞 SQLite 池上限。
        int unindexed = 0;

        foreach (var group in groups)
        {
            try
            {
                if (string.IsNullOrEmpty(group.Fingerprint))
                {
                    // 无拍摄时间或加载异常的孤立文件,也算"未提取"以保持按钮可见
                    unindexed++;
                    continue;
                }

                // 三路齐备才算已提取:DINO CLS + patch + CV grid 任一缺失即需补齐。
                var missing = await FolderFeatureIndexer.EvaluateMissingPartsAsync(group.Fingerprint).ConfigureAwait(false);
                if (missing.AnyMissing) unindexed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimilarityPanel] EvaluateUnindexed skip {group.Representative.Name}: {ex.Message}");
                unindexed++;
            }
        }

        UiDispatcher.Post(() => UnindexedCount = unindexed);
    }

    // ── 批量提取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动批量特征提取任务。切换文件夹后任务继续后台跑完(本期不可中断)。
    /// 进度按"指纹组"推进 — 同次曝光的 RAW+HEIF/JPG 合并为一组,只跑一次推理。
    /// </summary>
    private async Task RunIndexingAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0) return;

        _indexer = new FolderFeatureIndexer();
        IndexProgress = 0;
        IndexTotal = Math.Max(1, _unindexedCount);
        IndexError = null;
        IsStateIndexing = true;

        _indexer.ProgressChanged += OnIndexProgress;

        try
        {
            await _indexer.RunAsync(files).ConfigureAwait(false);
        }
        finally
        {
            var error = _indexer.LastError;
            _indexer.ProgressChanged -= OnIndexProgress;
            _indexer = null;
            UiDispatcher.Post(() =>
            {
                IsStateIndexing = false;
                IndexError = error;
            });
        }

        // 提取完成后重新判定未提取数量并刷新聚类列表
        await EvaluateUnindexedAsync().ConfigureAwait(false);
        // 新数据已落库,按指纹批量回填 ImageFile.IsShake — 直接读 CV grid + 尺寸,不再触发解码
        await ShakeFlagService.EvaluateAsync(_folder.AllFiles).ConfigureAwait(false);
        // 提取完成时面板可能已被收起,只在真正可见时重算相似列表。
        if (IsPanelVisible) _ = RecomputeAsync(_main.CurrentFile);
    }

    private void OnIndexProgress(IndexProgress progress)
    {
        UiDispatcher.Post(() =>
        {
            IndexProgress = progress.Completed;
            IndexTotal = progress.Total;
        });
    }

    // ── 相似度计算 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 重新计算当前图片的相似项;旧的计算被取消。
    /// 原生壳只在面板展开时计算(共享 5.4/SCOPE-01:切图不隐式推理);旧壳照旧随切图计算。
    /// 面板收起时先取消在途计算再返回 — 否则收起后那一轮仍会在后台扫全文件夹。
    /// </summary>
    private async Task RecomputeAsync(ImageFile? current)
    {
        _computeCts?.Cancel();
        if (_main.UsesNativePresentation && !_main.Settings.SimilarityPanelExpanded) return;

        var cts = new CancellationTokenSource();
        _computeCts = cts;
        var token = cts.Token;

        if (current == null)
        {
            UpdateOnUi(Array.Empty<SimilarityItem>(), token);
            return;
        }

        var pool = _thumbnailList.FilteredFiles;
        try
        {
            var results = await SimilarityService.FindSimilarAsync(
                    current,
                    pool,
                    threshold: _main.Settings.SimilarityThreshold,
                    maxResults: _main.Settings.SimilarityMaxResults,
                    ct: token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            UpdateOnUi(results, token);
        }
        catch (OperationCanceledException)
        {
            // 已被新的计算覆盖,忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine("相似度计算失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 在 UI 线程提交计算结果,并触发缩略图加载。
    /// </summary>
    private void UpdateOnUi(IReadOnlyList<SimilarityItem> results, CancellationToken token)
    {
        UiDispatcher.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            SimilarItems = results;

            foreach (var item in results)
            {
                _thumbnailList.QueueThumbnailLoad(item.File, priority: true);
            }
        });
    }
}
