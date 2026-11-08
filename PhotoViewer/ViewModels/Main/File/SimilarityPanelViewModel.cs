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
using PhotoViewer.Core.Storage;
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
/// 批量提取每轮自带文件快照、取消源(链接应用活跃代际与本 VM 生命周期)与根存储访问租约:
/// 关闭面板、换目录与桌面窗口失焦都不中断,只有应用进入后台(系统后台限制)或宿主释放才终止,
/// 已写入的部分保留、回前台不隐式续跑(SIM-03)。
/// </summary>
public class SimilarityPanelViewModel : ReactiveObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _thumbnailList;
    private readonly FolderViewModel _folder;

    /// <summary>本 VM 生命周期取消源:释放即取消在途统计与提取,UI 线程不等任务结束。</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly object _unindexedGate = new();
    private readonly object _progressGate = new();

    private CancellationTokenSource? _computeCts;
    private bool _disposed;

    /// <summary>统计单飞:非空即有一轮在跑,新请求复用它;完成与清空在同一次 UI 提交内落定。</summary>
    private TaskCompletionSource<bool>? _unindexedRun;

    /// <summary>统计代际:目录变化与提取落库只递增它,旧代际的结果不落字段。</summary>
    private int _unindexedVersion;

    /// <summary>已提交到 UI 字段的代际。</summary>
    private int _unindexedCommitted;

    private IndexProgress? _pendingProgress;
    private int _pendingProgressBatch;
    private bool _progressPostScheduled;

    /// <summary>提取批次代际(UI 线程推进):上一轮迟到的进度投递据此丢弃。</summary>
    private int _indexBatch;

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

        // 面板展开/收起与文件栏显隐都由可见性变化统一处理(展开即按当前图重算并统计)。
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

        // 目录变化:先让在途统计失效(不得提交旧目录),面板可见时立刻重扫。
        _folder.AllFilesChanged += () =>
        {
            if (IsPanelVisible)
                _ = EvaluateUnindexedAsync(invalidate: true);
            else
                Interlocked.Increment(ref _unindexedVersion);
        };
    }

    /// <summary>
    /// 释放:只取消在途相似度计算与批量提取,不阻塞 UI 线程等任务结束。
    /// 在途提取的收尾(归还存储租约、已入库部分保留)由后台任务自己完成。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _computeCts?.Cancel();
        _computeCts = null;
        // 不释放令牌源:在途任务仍要读 token 才能自己收尾。
        _lifetimeCts.Cancel();

        lock (_unindexedGate)
        {
            _unindexedRun?.TrySetResult(false);
            _unindexedRun = null;
        }
        lock (_progressGate)
        {
            _pendingProgress = null;
            _progressPostScheduled = false;
        }
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

    /// <summary>面板展开时按当前图重算，并重新核对可能在面板关闭期间变化的特征覆盖度。</summary>
    public void OnPanelOpened()
    {
        if (_disposed || !IsPanelVisible) return;

        _anchorFile = _main.CurrentFile;
        _ = EvaluateUnindexedAsync(invalidate: true);
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
    /// 请求未提取统计:单飞(在途一轮复用)。<paramref name="invalidate"/> 只用于目录变化与提取落库,
    /// 面板正常打开复用已提交或正在跑的一轮,不触发无谓重扫。
    /// </summary>
    private Task EvaluateUnindexedAsync(bool invalidate = false)
    {
        if (_disposed) return Task.CompletedTask;
        if (invalidate) Interlocked.Increment(ref _unindexedVersion);

        lock (_unindexedGate)
        {
            if (_unindexedRun != null) return _unindexedRun.Task;
            if (_unindexedCommitted == Volatile.Read(ref _unindexedVersion)) return Task.CompletedTask;

            var run = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _unindexedRun = run;
            _ = RunUnindexedAsync(run);
            return run.Task;
        }
    }

    private async Task RunUnindexedAsync(TaskCompletionSource<bool> run)
    {
        var token = _lifetimeCts.Token;
        try
        {
            while (!_disposed)
            {
                var request = await UiDispatcher.InvokeAsync(() =>
                    (Version: Volatile.Read(ref _unindexedVersion), Files: _folder.AllFiles.ToArray()))
                    .ConfigureAwait(false);
                int count = await Task.Run(() => CountUnindexedAsync(request.Files, token), token)
                    .ConfigureAwait(false);
                bool committed = await UiDispatcher.InvokeAsync(() =>
                {
                    lock (_unindexedGate)
                    {
                        if (!ReferenceEquals(_unindexedRun, run)) return true;
                        if (_disposed || request.Version != Volatile.Read(ref _unindexedVersion)) return false;
                        UnindexedCount = count;
                        _unindexedCommitted = request.Version;
                        run.TrySetResult(true);
                        _unindexedRun = null;
                        return true;
                    }
                }).ConfigureAwait(false);
                if (committed) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SimilarityPanel] 未提取统计失败: " + ex.Message);
        }
        finally
        {
            lock (_unindexedGate)
            {
                run.TrySetResult(false);
                if (ReferenceEquals(_unindexedRun, run)) _unindexedRun = null;
            }
        }
    }

    /// <summary>
    /// 按指纹分组统计待提取张数:同次曝光的 RAW/HEIF/JPG 只计一张,三路齐备才算已提取。
    /// 纯后台执行(不触碰面板字段),取消按文件边界透传。
    /// </summary>
    private static async Task<int> CountUnindexedAsync(IReadOnlyList<ImageFile> files, CancellationToken ct)
    {
        var groups = await FolderFeatureIndexer.GroupByFingerprintAsync(files, ct).ConfigureAwait(false);

        int unindexed = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // 无拍摄时间或加载异常的孤立文件也算"未提取"以保持按钮可见
                if (string.IsNullOrEmpty(group.Fingerprint))
                {
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

        return unindexed;
    }

    // ── 批量提取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动一轮批量特征提取(入口在 UI 线程):入口快照 + 根存储租约;取消源链接应用活跃代际与本 VM 生命周期,
    /// 只有应用进入后台或宿主释放才终止,已入库部分保留(SIM-03)。
    /// </summary>
    private async Task RunIndexingAsync()
    {
        if (_disposed || IsStateIndexing) return;

        var files = _folder.AllFiles;
        if (files == null || files.Count == 0) return;

        // 应用在后台时系统会立刻取消本轮:入口直接拒绝,不留"提取中"假状态。
        if (!BatchAnalysisContext.IsApplicationActive) return;

        var snapshot = files.ToArray();

        IStorageAccessSession? lease;
        try
        {
            // 租约在 UI 线程同步建立,必须早于换目录释放旧会话。
            lease = AcquireRunLease(snapshot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SimilarityPanel] 存储访问租约建立失败: {ex.Message}");
            IndexError = ex.Message;
            return;
        }

        try
        {
            int batch = ++_indexBatch;
            ResetPendingProgress();
            IndexProgress = 0;
            IndexTotal = 0; // 分组总数尚未统计:先按不定态显示,由首个进度事件给出
            IndexError = null;
            IsStateIndexing = true;

            var indexer = new FolderFeatureIndexer();
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token, BatchAnalysisContext.ApplicationToken);
            var token = runCts.Token;

            void OnProgress(IndexProgress progress) => QueueIndexProgress(batch, progress);
            indexer.ProgressChanged += OnProgress;

            string? error = null;
            try
            {
                // Attach 是平台权限准入(binder 调用),放后台;未附加成功的文件不进本轮。
                var attached = await Task.Run(() => AttachAll(lease, snapshot), CancellationToken.None)
                    .ConfigureAwait(false);
                error = attached.Error;

                if (attached.Files.Count > 0)
                    await indexer.RunAsync(attached.Files, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 取消语境在下面按令牌来源判定短原因,不保留异常原文。
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SimilarityPanel] 批量提取异常: " + ex.Message);
                error = ex.Message;
            }
            finally
            {
                indexer.ProgressChanged -= OnProgress;
                error ??= token.IsCancellationRequested ? CancelReason() : indexer.LastError;
            }

            // 已入库特征先失效重扫覆盖度:等待最新计数提交到 UI 之后才结束"提取中"。
            await EvaluateUnindexedAsync(invalidate: true).ConfigureAwait(false);

            // 结束动作、抖动徽标与面板重算在同一次 UI 回合:后台不读面板字段、不改 _computeCts。
            try
            {
                IReadOnlyList<ImageFile>? shakeTarget = null;
                await UiDispatcher.InvokeAsync(() =>
                {
                    FlushIndexProgress();
                    if (_disposed) return;

                    shakeTarget = _folder.AllFiles;
                    if (batch != _indexBatch) return;

                    IsStateIndexing = false;
                    IndexError = error;
                    if (IsPanelVisible) _ = RecomputeAsync(_anchorFile);
                }).ConfigureAwait(false);

                if (shakeTarget != null)
                    await ShakeFlagService.EvaluateAsync(shakeTarget).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SimilarityPanel] 提取收尾失败: " + ex.Message);
            }
        }
        finally
        {
            // 根租约持到收尾之后:RunAsync、统计与 UI 投递任一步失败都在这里归还。
            lease?.Dispose();
        }
    }

    /// <summary>取消语境的短原因:系统后台限制与宿主释放都不是成功(SIM-03)。</summary>
    private string CancelReason()
        => _lifetimeCts.IsCancellationRequested ? "提取已中断（应用退出）" : "提取已中断（应用进入后台）";

    /// <summary>建立本轮根存储访问租约(根取当前文件夹,单图模式回退首张文件);只有平台不支持的会话类型按无租约处理。</summary>
    private IStorageAccessSession? AcquireRunLease(IReadOnlyList<ImageFile> files)
    {
        IStorageItemRef? root = _folder.CurrentFolder;
        if (root == null && files.Count > 0) root = files[0].File;
        if (root == null) return null;

        try
        {
            return _main.StorageAccess.Create(root);
        }
        catch (StorageException ex) when (ex.Error == StorageError.Unsupported)
        {
            return null;
        }
    }

    /// <summary>为本轮文件建立访问准入:只返回附加成功的项,首个失败原因一并返回,不在整批上假装通过。</summary>
    private static (IReadOnlyList<ImageFile> Files, string? Error) AttachAll(
        IStorageAccessSession? lease, IReadOnlyList<ImageFile> files)
    {
        if (lease == null) return (files, null);

        string? error = null;
        var attached = new List<ImageFile>(files.Count);

        foreach (var file in files)
        {
            try
            {
                lease.Attach(file.File);
                attached.Add(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimilarityPanel] Attach 失败 {file.Name}: {ex.Message}");
                error ??= ex.Message;
            }
        }

        return (attached, error);
    }

    /// <summary>清空上一轮遗留的待投递进度(新批次从 0 开始)。</summary>
    private void ResetPendingProgress()
    {
        lock (_progressGate)
        {
            _pendingProgress = null;
            _progressPostScheduled = false;
        }
    }

    /// <summary>登记最新进度并合并投递:一轮里 UI 线程最多排一次落字段,迟到批次直接丢弃。</summary>
    private void QueueIndexProgress(int batch, IndexProgress progress)
    {
        if (_disposed) return;

        lock (_progressGate)
        {
            if (_disposed || batch != Volatile.Read(ref _indexBatch)) return;

            _pendingProgress = progress;
            _pendingProgressBatch = batch;
            if (_progressPostScheduled) return;
            _progressPostScheduled = true;
        }

        UiDispatcher.Post(FlushIndexProgress);
    }

    /// <summary>把最新的待投递进度落到 UI 字段;不属于当前批次的迟到值丢弃。</summary>
    private void FlushIndexProgress()
    {
        IndexProgress? pending;
        int pendingBatch;
        lock (_progressGate)
        {
            pending = _pendingProgress;
            pendingBatch = _pendingProgressBatch;
            _pendingProgress = null;
            _progressPostScheduled = false;
        }

        if (pending == null || _disposed || pendingBatch != _indexBatch) return;

        IndexProgress = pending.Value.Completed;
        IndexTotal = pending.Value.Total;
    }

    // ── 相似度计算 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 重新计算当前图片的相似项(旧计算取消);只在面板可见、应用在前台且处于 UI 线程时发起(SCOPE-01/SIM-06)。
    /// </summary>
    private async Task RecomputeAsync(ImageFile? current)
    {
        if (_disposed) return;

        _computeCts?.Cancel();
        if (!IsPanelVisible || !BatchAnalysisContext.IsApplicationActive) return;

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
            if (_disposed || token.IsCancellationRequested) return;
            SimilarItems = results;

            foreach (var item in results)
            {
                _thumbnailList.QueueThumbnailLoad(item.File, priority: true);
            }
        });
    }
}
