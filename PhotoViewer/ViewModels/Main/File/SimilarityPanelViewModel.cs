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
/// 相似聚类面板的视图模型。
/// 订阅 <see cref="MainViewModel.CurrentFile"/> 切换,异步调用 <see cref="SimilarityService"/>
/// 计算相似项并暴露给 <c>SimilarityListView</c>;同时通过 <see cref="ThumbnailListViewModel"/>
/// 的加载队列,触发相似项的缩略图加载。
/// 面板由两组正交开关驱动:<see cref="HasItems"/> 控制列表/占位,
/// <see cref="ShowActionArea"/>(= <see cref="HasUnindexed"/> 或 <see cref="IsStateIndexing"/>) 控制按钮/进度条区域。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _thumbnailList;
    private readonly FolderViewModel _folder;

    private CancellationTokenSource? _computeCts;

    /// <summary>
    /// 一次性抑制位:由 <see cref="SelectItemCommand"/> 置位,
    /// 目的是让"点击相似项切换主图"这一动作不反过来刷新相似面板本身(避免锚点漂移)。
    /// 只对"由自身触发的那一次 <see cref="MainViewModel.CurrentFile"/> 变更"生效,随即复位。
    /// </summary>
    private bool _suppressNextRecompute;

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

        // 候选池来自主缩略图列表的 FilteredFiles(已应用同名分组与筛选);
        // 列表整体替换时(开关分组、改筛选、切文件夹)同步重算,避免相似面板出现"被合并掉的伴侣文件"或"自身的另一格式"。
        _thumbnailList.WhenAnyValue(x => x.FilteredFiles)
            .Skip(1)
            .Subscribe(__ => { _ = RecomputeAsync(_main.CurrentFile); });

        // 切换/加载文件夹后重新统计未提取数量,避免面板停留在旧文件夹的状态。
        _folder.AllFilesChanged += () =>
        {
            if (_main.Settings.SimilarityPanelExpanded)
                _ = EvaluateUnindexedAsync();
        };
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
    /// 面板展开时调用:重新统计当前文件夹未提取张数。
    /// </summary>
    public void OnPanelOpened()
    {
        _ = EvaluateUnindexedAsync();
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
    /// 扫描当前文件夹,更新 <see cref="UnindexedCount"/>。
    /// </summary>
    private async Task EvaluateUnindexedAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0)
        {
            Dispatcher.UIThread.Post(() => UnindexedCount = 0);
            return;
        }

        int unindexed = 0;

        foreach (var file in files)
        {
            // 无法计算指纹的文件跳过,不计入分母
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            if (!file.ModifiedDate.HasValue)
                await file.LoadBasicPropertiesAsync().ConfigureAwait(false);

            var input = Core.Database.PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
            if (!input.CaptureTime.HasValue) continue;

            var fingerprint = Core.Database.PhotoFingerprint.Compute(input);
            var record = await Core.Database.PhotoDatabase.GetAsync(fingerprint).ConfigureAwait(false);
            if (record?.FeatureVector == null || record.FeatureModel != DinoModelResources.ModelId)
                unindexed++;
        }

        Dispatcher.UIThread.Post(() => UnindexedCount = unindexed);
    }

    // ── 批量提取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动批量特征提取任务。切换文件夹后任务继续后台跑完(本期不可中断)。
    /// </summary>
    private async Task RunIndexingAsync()
    {
        var files = _folder.AllFiles;
        if (files == null || files.Count == 0) return;

        _indexer = new FolderFeatureIndexer();
        IndexProgress = 0;
        IndexTotal = files.Count;
        IsStateIndexing = true;

        _indexer.ProgressChanged += OnIndexProgress;

        try
        {
            await _indexer.RunAsync(files).ConfigureAwait(false);
        }
        finally
        {
            _indexer.ProgressChanged -= OnIndexProgress;
            _indexer = null;
            Dispatcher.UIThread.Post(() => IsStateIndexing = false);
        }

        // 提取完成后重新判定未提取数量并刷新聚类列表
        await EvaluateUnindexedAsync().ConfigureAwait(false);
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
    /// 重新计算当前图片的相似项;旧的计算被取消。
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
