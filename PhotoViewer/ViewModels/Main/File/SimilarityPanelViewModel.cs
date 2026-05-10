using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 相似聚类面板的状态枚举。
/// </summary>
public enum SimilarityPanelState
{
    /// <summary>当前文件夹无任何照片已在数据库中算过特征。</summary>
    Empty,
    /// <summary>已有部分照片入库，但仍有未算过的。</summary>
    Partial,
    /// <summary>当前文件夹每张都已在库，显示完整聚类结果。</summary>
    Full,
    /// <summary>正在批量提取特征中。</summary>
    Indexing
}

/// <summary>
/// 相似聚类面板的视图模型。
/// 订阅 <see cref="MainViewModel.CurrentFile"/> 切换，异步调用 <see cref="SimilarityService"/>
/// 计算相似项并暴露给 <c>SimilarityListView</c>；同时通过 <see cref="ThumbnailListViewModel"/>
/// 的加载队列，触发相似项的缩略图加载。
/// 展开时执行三态判定（Empty/Partial/Full），并支持手动触发全文件夹批量特征提取。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _thumbnailList;
    private readonly FolderViewModel _folder;

    private CancellationTokenSource? _computeCts;

    /// <summary>
    /// 一次性抑制位：由 <see cref="SelectItemCommand"/> 置位，
    /// 目的是让"点击相似项切换主图"这一动作不反过来刷新相似面板本身（避免锚点漂移）。
    /// 只对"由自身触发的那一次 <see cref="MainViewModel.CurrentFile"/> 变更"生效，随即复位。
    /// </summary>
    private bool _suppressNextRecompute;

    private IReadOnlyList<SimilarityItem> _similarItems = Array.Empty<SimilarityItem>();
    /// <summary>当前相似项列表（已按分数降序），绑定到 SimilarityListView 的 ItemsSource。</summary>
    public IReadOnlyList<SimilarityItem> SimilarItems
    {
        get => _similarItems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _similarItems, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(HasItems));
        }
    }

    /// <summary>是否无相似项，用于显示占位文案。</summary>
    public bool IsEmpty => _similarItems.Count == 0;

    /// <summary>是否有相似项（便于直接绑定 IsVisible）。</summary>
    public bool HasItems => _similarItems.Count > 0;

    /// <summary>当前布局是否为行布局（分栏位于上下，决定列表方向、对齐）。</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>主视图模型引用，模板内绑定 IsCurrent / Settings 用。</summary>
    public MainViewModel Main => _main;

    /// <summary>设置引用，供模板绑定 ShowRating 等。</summary>
    public SettingsViewModel Settings => _main.Settings;

    // ── 三态状态机 ──────────────────────────────────────────────────────────

    private SimilarityPanelState _panelState = SimilarityPanelState.Empty;

    /// <summary>记录 Indexing 从哪个状态触发，用于决定进度条显示位置。</summary>
    private bool _indexingFromEmpty = true;

    /// <summary>面板当前状态，驱动三态 UI 切换。</summary>
    public SimilarityPanelState PanelState
    {
        get => _panelState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _panelState, value);
            this.RaisePropertyChanged(nameof(IsStateEmpty));
            this.RaisePropertyChanged(nameof(IsStatePartial));
            this.RaisePropertyChanged(nameof(IsStateFull));
            this.RaisePropertyChanged(nameof(IsStateIndexing));
            this.RaisePropertyChanged(nameof(IsStateIndexingFromEmpty));
            this.RaisePropertyChanged(nameof(IsStateEmptyOrIndexingFromEmpty));
            this.RaisePropertyChanged(nameof(IsStatePartialOrIndexing));
        }
    }

    public bool IsStateEmpty => _panelState == SimilarityPanelState.Empty;
    public bool IsStatePartial => _panelState == SimilarityPanelState.Partial;
    public bool IsStateFull => _panelState == SimilarityPanelState.Full;
    public bool IsStateIndexing => _panelState == SimilarityPanelState.Indexing;

    /// <summary>Indexing 从 Empty 触发时为 true，用于在面板中央显示进度条。</summary>
    public bool IsStateIndexingFromEmpty
        => _panelState == SimilarityPanelState.Indexing && _indexingFromEmpty;

    /// <summary>Empty 态或从 Empty 触发 Indexing 时为 true，保持空态区块可见（按钮原地变进度条）。</summary>
    public bool IsStateEmptyOrIndexingFromEmpty
        => _panelState == SimilarityPanelState.Empty
        || (_panelState == SimilarityPanelState.Indexing && _indexingFromEmpty);

    /// <summary>Partial 态或从 Partial 触发 Indexing 时为 true，保持列表+底部按钮区可见。</summary>
    public bool IsStatePartialOrIndexing
        => _panelState == SimilarityPanelState.Partial
        || (_panelState == SimilarityPanelState.Indexing && !_indexingFromEmpty);

    /// <summary>IndexTotal 为 0 时进度条显示不定态。</summary>
    public bool IsIndexTotalUnknown => _indexTotal == 0;

    private int _indexProgress;
    /// <summary>批量提取进度（已完成张数）。</summary>
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
    /// <summary>Partial 态下未计算的张数。</summary>
    public int UnindexedCount
    {
        get => _unindexedCount;
        private set => this.RaiseAndSetIfChanged(ref _unindexedCount, value);
    }

    private FolderFeatureIndexer? _indexer;

    // ── 构造 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构造相似聚类面板视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="thumbnailList">主缩略图列表 VM，候选池取自其 <see cref="ThumbnailListViewModel.FilteredFiles"/></param>
    /// <param name="folder">文件源 VM，用于获取全文件夹列表</param>
    public SimilarityPanelViewModel(MainViewModel main, ThumbnailListViewModel thumbnailList, FolderViewModel folder)
    {
        _main = main;
        _thumbnailList = thumbnailList;
        _folder = folder;

        _main.WhenAnyValue(x => x.IsRowLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRowLayout)));

        _main.WhenAnyValue(x => x.CurrentFile)
            .Subscribe(file =>
            {
                if (_suppressNextRecompute)
                {
                    _suppressNextRecompute = false;
                    return;
                }
                _ = RecomputeAsync(file);
            });

        // 候选池来自主缩略图列表的 FilteredFiles（已应用同名分组与筛选）；
        // 列表整体替换时（开关分组、改筛选、切文件夹）同步重算，避免相似面板出现"被合并掉的伴侣文件"或"自身的另一格式"。
        _thumbnailList.WhenAnyValue(x => x.FilteredFiles)
            .Skip(1)
            .Subscribe(__ => { _ = RecomputeAsync(_main.CurrentFile); });

        // 切换/加载文件夹后重新执行三态判定，避免面板停留在旧文件夹的空态。
        _folder.AllFilesChanged += () =>
        {
            if (_main.Settings.SimilarityPanelExpanded)
                _ = EvaluatePanelStateAsync();
        };
    }

    // ── 公共命令 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 选中相似项（命令绑定），切换主视图当前图片但保留主缩略图列表的锚点高亮。
    /// 同时抑制本次 <see cref="MainViewModel.CurrentFile"/> 变更对相似面板的反向刷新，
    /// 保持原锚点对应的相似列表不被重排，避免操作混乱。
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
    /// 面板展开时调用：执行三态判定，决定显示 Empty / Partial / Full。
    /// </summary>
    public void OnPanelOpened()
    {
        _ = EvaluatePanelStateAsync();
    }

    /// <summary>
    /// 触发全文件夹批量特征提取（Empty 态"提取全部"按钮 / Partial 态"补齐全部"按钮）。
    /// </summary>
    public void StartIndexingCommand()
    {
        if (_panelState == SimilarityPanelState.Indexing) return;
        _ = RunIndexingAsync();
    }

    // ── 三态判定 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查当前文件夹的特征覆盖度，更新 <see cref="PanelState"/>。
    /// </summary>
    private async Task EvaluatePanelStateAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0)
        {
            PanelState = SimilarityPanelState.Empty;
            return;
        }

        int indexed = 0;
        int total = 0;

        foreach (var file in files)
        {
            // 无法计算指纹的文件跳过，不计入分母
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            if (!file.ModifiedDate.HasValue)
                await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

            var input = Core.Database.PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
            if (!input.CaptureTime.HasValue) continue;

            total++;
            var fingerprint = Core.Database.PhotoFingerprint.Compute(input);
            var record = await Core.Database.PhotoDatabase.GetAsync(fingerprint).ConfigureAwait(false);
            if (record?.FeatureVector != null && record.FeatureModel == DinoModelResources.ModelId)
                indexed++;
        }

        int unindexed = total - indexed;
        UnindexedCount = unindexed;

        Dispatcher.UIThread.Post(() =>
        {
            if (total == 0 || indexed == 0)
                PanelState = SimilarityPanelState.Empty;
            else if (unindexed > 0)
                PanelState = SimilarityPanelState.Partial;
            else
                PanelState = SimilarityPanelState.Full;
        });
    }

    // ── 批量提取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动批量特征提取任务。切换文件夹后任务继续后台跑完（本期不可中断）。
    /// </summary>
    private async Task RunIndexingAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0) return;

        _indexingFromEmpty = _panelState == SimilarityPanelState.Empty;
        _indexer = new FolderFeatureIndexer();
        IndexProgress = 0;
        IndexTotal = files.Count;
        PanelState = SimilarityPanelState.Indexing;

        _indexer.ProgressChanged += OnIndexProgress;

        try
        {
            await _indexer.RunAsync(files).ConfigureAwait(false);
        }
        finally
        {
            _indexer.ProgressChanged -= OnIndexProgress;
            _indexer = null;
        }

        // 提取完成后重新判定状态并刷新聚类列表
        await EvaluatePanelStateAsync().ConfigureAwait(false);
        _ = RecomputeAsync(_main.CurrentFile);
    }

    private void OnIndexProgress(IndexProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IndexProgress = progress.Completed;
            IndexTotal = progress.Total;
        });
    }

    // ── 相似度计算 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 重新计算当前图片的相似项；旧的计算被取消。
    /// </summary>
    private async Task RecomputeAsync(ImageFile? current)
    {
        _computeCts?.Cancel();
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
            var results = await SimilarityService.FindSimilarAsync(current, pool, ct: token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            UpdateOnUi(results, token);
        }
        catch (OperationCanceledException)
        {
            // 已被新的计算覆盖，忽略
        }
        catch (Exception ex)
        {
            Console.WriteLine("相似度计算失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 在 UI 线程提交计算结果，并触发缩略图加载。
    /// </summary>
    private void UpdateOnUi(IReadOnlyList<SimilarityItem> results, CancellationToken token)
    {
        Dispatcher.UIThread.Post(() =>
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
