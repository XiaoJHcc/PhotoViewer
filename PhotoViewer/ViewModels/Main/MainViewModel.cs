using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Main;

public class MainViewModel : ViewModelBase, IDisposable
{
    public FolderViewModel FolderVM { get; }
    public FileViewModel FileVM { get; }
    public ControlViewModel ControlVM { get; }
    public SettingsViewModel Settings { get; }
    public ToolsViewModel Tools { get; }
    public RatingSourcesViewModel RatingSources { get; }
    public OpenRequestChannel OpenRequests { get; }
    /// <summary>外部打开协调器：唯一消费者顺序处理 <see cref="OpenRequests"/>，重复激活当前目标只前置窗口。</summary>
    public OpenCoordinator OpenCoordinator { get; }
    public IStorageAccessSessionFactory StorageAccess { get; }
    public RatingService Rating { get; } = new();

    private bool _disposed;
    private readonly Action _recheckShakeFlags;
    private readonly object _ratingRefreshLock = new();
    private readonly Dictionary<StorageItemId, long> _ratingRefreshVersions = new();
    private long _ratingRefreshSequence;

    public SettingsRepository SettingsRepository => Settings.Repository
        ?? throw new InvalidOperationException("当前设置未绑定持久化仓库");

    /// <summary>
    /// 星级写回文件失败时触发(UI 线程);界面据此给出可见提示,不呈现虚假成功(FILM-06)。
    /// 参数为原照片与本次请求的星级。
    /// </summary>
    public event Action<ImageFile, int>? RatingWriteFailed;

    /// <summary>
    /// 星级写回失败的详细事件（UI 线程）：携带本次结果、原因与是否 Partial，
    /// 不依赖可被后发请求清空的 <see cref="LastRatingWriteError"/>。
    /// 旧 <see cref="RatingWriteFailed"/> 保留兼容，新界面应优先订阅本事件。
    /// </summary>
    public event Action<RatingWriteFailure>? RatingWriteFailedDetailed;

    /// <summary>
    /// 显式打开失败时触发(UI 线程);文案由共享层统一给出,界面据此显示 STATE-03 提示条(STATE-02)。
    /// 参数为完整的失败提示正文。
    /// </summary>
    public event Action<string>? OpenFailed;

    /// <summary>
    /// 显式打开（文件/文件夹/外部打开/拖入）开始时触发：主图回到适应态，不沿用上一批照片的倍率与对焦跟随（OPEN-05）。
    /// </summary>
    public event Action? FitToViewRequested;

    // 当前状态
    public ImageFile? LastFile;
    private ImageFile? _currentFile;

    /// <summary>
    /// 主缩略图列表的当前锚点(持有 <see cref="ImageFile.IsCurrent"/> 高亮的那一项)。
    /// 与 <see cref="CurrentFile"/> 通常一致;但 <see cref="SetCurrentImageKeepAnchor"/> 会让二者解耦,
    /// 这里独立记录,确保下一次正常切换能正确清掉旧锚点的高亮。
    /// </summary>
    private ImageFile? _currentAnchor;

    /// <summary>
    /// setter 内部抑制位:为 true 时,本次 <see cref="CurrentFile"/> 赋值仅更新主图状态(<see cref="ImageFile.IsCurrentImage"/>),
    /// 不更新主缩略图列表的锚点高亮(<see cref="ImageFile.IsCurrent"/>)。
    /// 由 <see cref="SetCurrentImageKeepAnchor"/> 一次性置位,用于相似聚类面板等"切主图但保留原锚点"的场景。
    /// </summary>
    private bool _suppressAnchorUpdate;

    public ImageFile? CurrentFile
    {
        get => _currentFile;
        set
        {
            // 切图取证(常驻,一行):与展示会话的加载日志对表可还原错位/丢显事故
            Console.WriteLine($"CurrentFile => {(value?.Name ?? "null")}");
            if (_currentFile != null) _currentFile.IsCurrentImage = false;
            if (_currentFile != value) LastFile = _currentFile;
            this.RaiseAndSetIfChanged(ref _currentFile, value);

            if (!_suppressAnchorUpdate)
            {
                if (_currentAnchor != null && !ReferenceEquals(_currentAnchor, value))
                {
                    _currentAnchor.IsCurrent = false;
                }
                if (value != null) value.IsCurrent = true;
                _currentAnchor = value;
            }

            if (value != null)
            {
                value.IsCurrentImage = true;
                FileVM.ThumbnailList.PreloadNearbyFiles();
            }
        }
    }

    /// <summary>
    /// 切换主图但保留主缩略图列表的锚点高亮不变。
    /// 用于相似聚类面板的点击:用户从相似列表选一张时只想看新图,不希望主列表里原锚点跟着漂。
    /// </summary>
    /// <param name="file">要切换到的图片</param>
    public void SetCurrentImageKeepAnchor(ImageFile? file)
    {
        _suppressAnchorUpdate = true;
        try { CurrentFile = file; }
        finally { _suppressAnchorUpdate = false; }
    }

    /// <summary>建立共享业务 VM；生产壳在调用前完成设置仓库初始化。</summary>
    public MainViewModel(
        SettingsRepository? settingsRepository = null,
        IViewStateStore? viewState = null,
        OpenRequestChannel? openRequests = null,
        IStorageAccessSessionFactory? storageAccess = null)
    {
        Settings = new SettingsViewModel(
            settingsRepository ?? new SettingsRepository(
                new InMemorySettingsDocumentStore(),
                new DefaultPerformanceBudget(),
                OperatingSystem.IsMacOS() || OperatingSystem.IsIOS(),
                BitmapLoader.AutomaticCacheLimit),
            viewState);
        OpenRequests = openRequests ?? new OpenRequestChannel();
        StorageAccess = storageAccess ?? new LocalStorageAccessSessionFactory();
        Tools = new ToolsViewModel(this);
        // 创建子 ViewModel
        FolderVM = new FolderViewModel(this);
        ControlVM = new ControlViewModel(this);
        // 文件栏容器(必须在 FolderVM 之后,因为 FileVM 内部会订阅 FolderVM 事件)
        FileVM = new FileViewModel(this);
        RatingSources = new RatingSourcesViewModel(this);

        // 外部打开：界面就绪前的请求已在通道内排队，消费者从这里开始顺序处理。
        OpenCoordinator = new OpenCoordinator(this);

        // 监听布局模式变化
        Settings.WhenAnyValue(s => s.LayoutMode)
            .Subscribe(_ => UpdateLayoutFromSettings());

        // 当前图片切换、或 ExifData / EXIF 加载完成时，同步刷新工具状态。
        // 无 EXIF 的照片加载完成时 ExifData 仍为 null（值未变化），必须同时监听 IsExifLoaded 才能区分“加载中”和“无 EXIF”。
        this.WhenAnyValue(m => m.CurrentFile)
            .Select(file => file == null
                ? Observable.Return<ImageFile?>(null)
                : file.WhenAnyValue(f => f.ExifData, f => f.IsExifLoaded).Select(_ => file))
            .Switch()
            .Subscribe(_ => Tools.SyncCurrentFile(CurrentFile));

        // 文件夹加载完成后,按指纹批量评估抖动判定 — 数据来自 photos.db 已有的 cv_grid + 尺寸,
        // 不触发任何图像解码或 ONNX 推理。未入库的文件 IsShake 保持 null(不挂徽标)。
        FolderVM.AllFilesChanged += () => _ = PhotoViewer.Core.AI.ShakeFlagService.EvaluateAsync(FolderVM.AllFiles);

        // AI 设置页清空特征数据库后,触发对当前文件列表的重新评估,徽标即时消失。
        _recheckShakeFlags = () => _ = PhotoViewer.Core.AI.ShakeFlagService.EvaluateAsync(FolderVM.AllFiles);
        PhotoViewer.Core.AI.ShakeFlagService.RecheckRequested += _recheckShakeFlags;
    }

    /// <summary>
    /// 结束主视图模型的生命周期：取消外部打开消费者、在途打开会话与后台补齐，归还存储访问租约并释放设置。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_ratingRefreshLock) _ratingRefreshVersions.Clear();
        PhotoViewer.Core.AI.ShakeFlagService.RecheckRequested -= _recheckShakeFlags;
        OpenCoordinator.Dispose();
        FolderVM.Dispose();
        RatingSources.Dispose();
        Settings.Dispose();
    }
    
    ////////////////
    /// 窗口与主图
    ////////////////
    
    #region Windows

    /// <summary>
    /// 打开设置窗口（桌面端）。窗口构建由平台窗口展示器完成。
    /// </summary>
    public void OpenSettingWindow() => NativeWindowPresenter.ShowSettingsWindow(Settings);

    /// <summary>
    /// 打开工具窗口（桌面端单例）。未打开时回到工具首页；已打开时只前置并保留当前页（TOOLS-01）。
    /// </summary>
    public void OpenToolsWindow()
    {
        if (!Tools.IsPresented)
            Tools.ShowList();
        NativeWindowPresenter.ShowOrActivateToolsWindow(Tools);
    }

    /// <summary>
    /// 请求主图回到适应态（OPEN-05）。由平台壳订阅 <see cref="FitToViewRequested"/> 后执行。
    /// </summary>
    public void RequestFitToView() => FitToViewRequested?.Invoke();

    /// <summary>
    /// 上报显式打开失败（STATE-02），由平台壳订阅 <see cref="OpenFailed"/> 后以 STATE-03 提示条呈现。
    /// </summary>
    /// <param name="message">完整的失败提示正文。</param>
    public void ReportOpenFailure(string message)
    {
        if (UiDispatcher.CheckAccess()) OpenFailed?.Invoke(message);
        else UiDispatcher.Post(() => OpenFailed?.Invoke(message));
    }

    #endregion
    
    
    ////////////////
    /// 整体布局
    ////////////////

    #region Layout
    
    // 实际使用的布局方向（考虑智能模式）
    private bool _isRowLayout = true;
    public bool IsRowLayout
    {
        get => _isRowLayout;
        private set => this.RaiseAndSetIfChanged(ref _isRowLayout, value);
    }

    // 屏幕方向状态
    private bool _isScreenLandscape = true;
    public bool IsScreenLandscape
    {
        get => _isScreenLandscape;
        set
        {
            var oldValue = _isScreenLandscape;
            this.RaiseAndSetIfChanged(ref _isScreenLandscape, value);
            
            // 只有当值真正发生变化时才更新布局
            if (oldValue != value)
            {
                UpdateLayoutFromSettings();
            }
        }
    }
    
    /// <summary>
    /// 根据设置和屏幕方向更新布局
    /// </summary>
    private void UpdateLayoutFromSettings()
    {
        bool newLayout = Settings.LayoutMode switch
        {
            LayoutMode.Col => false,
            LayoutMode.Row => true,
            LayoutMode.Auto => !IsScreenLandscape,
            _ => true
        };

        if (IsRowLayout != newLayout)
        {
            IsRowLayout = newLayout;

            // 通知相关视图模型布局已变化
            ControlVM?.RaisePropertyChanged(nameof(ControlVM.IsRowLayout));
        }
    }

    /// <summary>
    /// 更新屏幕方向（由视图调用）
    /// </summary>
    public void UpdateScreenOrientation(double width, double height)
    {
        bool newIsLandscape = width > height;
        
        // 直接比较值是否变化，而不是依赖 RaiseAndSetIfChanged 的返回值
        if (IsScreenLandscape != newIsLandscape)
        {
            IsScreenLandscape = newIsLandscape;
            
            // 如果是智能模式，屏幕方向变化会触发布局更新
            if (Settings.LayoutMode == LayoutMode.Auto)
            {
                UpdateLayoutFromSettings();
            }
        }
    }
    
    private bool _isAnalysisViewVisible = false;
    /// <summary>分析栏(细节预览 + DINO/CV 诊断合并)是否可见。菜单/快捷键(显示为"分析栏")控制此项。</summary>
    public bool IsAnalysisViewVisible
    {
        get => _isAnalysisViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isAnalysisViewVisible, value);
    }

    private bool _isThumbnailViewVisible = true;
    public bool IsThumbnailViewVisible
    {
        get => _isThumbnailViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isThumbnailViewVisible, value);
    }

    private bool _isControlViewVisible = true;
    public bool IsControlViewVisible
    {
        get => _isControlViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isControlViewVisible, value);
    }

    /// <summary>切换分析栏可见性。绑定到菜单项与快捷键。</summary>
    public void ToggleAnalysisView()
    {
        IsAnalysisViewVisible = !IsAnalysisViewVisible;
    }

    public void ToggleThumbnailView()
    {
        IsThumbnailViewVisible = !IsThumbnailViewVisible;
    }

    public void ToggleControlView()
    {
        IsControlViewVisible = !IsControlViewVisible;
    }
    
    #endregion

    
    ////////////////
    /// 星级管理
    ////////////////
    
    #region Rating
    
    public string? LastRatingWriteError { get; private set; }
    public RatingWriteOutcome? LastRatingWriteOutcome { get; private set; }

    internal PhotoGroup GetRatingGroup(ImageFile file)
    {
        if (file.Group != null) return file.Group;
        var formats = Settings.FileFormats
            .Select(item => new PhotoFormatRule(item.Id, item.DisplayName, item.Extensions, item.IsEnabled))
            .ToArray();
        return PhotoGroupMap.Build(FolderVM.AllFiles, Settings.SameNameAsOnePhoto, formats).GroupFor(file)
            ?? PhotoGroupMap.Build([file], false, formats).GroupFor(file)!;
    }

    public void ShowRatingSources(ImageFile file) => RatingSources.Show(file, GetRatingGroup(file));

    /// <summary>
    /// 设置图片星级（兼容旧签名）。统一写入组内全部实际来源（各文件内嵌 + 已存在的同名 sidecar）。
    /// 只有整组写入/验证成功（或同值无需写入）才同步数据库；成功后不清空 EXIF。
    /// </summary>
    public Task SetRatingAsync(ImageFile? file, int rating) => SetRatingCoreAsync(file, rating);

    /// <summary>
    /// 设置图片星级并返回本次结果（供来源浮窗等调用方决定关闭/保留，避免共享 LastOutcome 竞态）。
    /// </summary>
    public Task<RatingWriteOutcome?> SetRatingWithResultAsync(ImageFile? file, int rating) =>
        SetRatingCoreAsync(file, rating);

    private async Task<RatingWriteOutcome?> SetRatingCoreAsync(ImageFile? file, int rating)
    {
        if (file == null) return null;
        var ratingSw = Stopwatch.StartNew();
        Console.WriteLine($"[Rating] === BEGIN SetRating({file.Name}, {rating}) ===");
        LastRatingWriteError = null;
        LastRatingWriteOutcome = null;
        var persisted = false;
        try
        {
            var group = GetRatingGroup(file);
            var safeMode = Settings.SafeSetRating;
            var sidecarIndex = group.Files
                .Select(item => item.RatingSidecarIndex)
                .FirstOrDefault(item => item != null);
            var result = await Task.Run(
                () => Rating.SetRatingAsync(group, rating, safeMode, sidecarIndex));
            LastRatingWriteOutcome = result.Outcome;
            if (result.Outcome is RatingWriteOutcome.Superseded or RatingWriteOutcome.Canceled)
            {
                // 即使没有物理修改也按磁盘真值刷新，避免内存与外部状态长期分叉。
                await RefreshRatingsFromDiskAsync(group);
                return result.Outcome;
            }

            if (result.IsApplied)
            {
                persisted = true;
                // 成功后按磁盘真值逐文件重读全部来源（共享 sidecar 会回填到每个文件，
                // 内嵌 Conflict/多来源状态也不会被单份 canonical 的 After 抹掉）。
                await RefreshRatingsFromDiskAsync(group);
                _ = SyncRatingToDatabaseAsync(group.Representative, rating);
                return result.Outcome;
            }

            LastRatingWriteError = result.FailureReason;
            // 所有非 Applied 结果都按磁盘真值重读，不只 Partial。
            await RefreshRatingsFromDiskAsync(group);
            var failure = new RatingWriteFailure(
                file,
                rating,
                result.Outcome,
                result.FailureReason,
                result.Outcome == RatingWriteOutcome.Partial);
            await UiDispatcher.InvokeAsync(() =>
            {
                RatingWriteFailed?.Invoke(file, rating);
                RatingWriteFailedDetailed?.Invoke(failure);
            });
            return result.Outcome;
        }
        catch (Exception ex)
        {
            LastRatingWriteError = StatusMessages.ReasonFor(ex);
            Console.WriteLine("Error setting rating for " + file.Name + ": " + ex);
            if (!persisted)
            {
                var failure = new RatingWriteFailure(file, rating, null, LastRatingWriteError, IsPartial: false);
                await UiDispatcher.InvokeAsync(() =>
                {
                    RatingWriteFailed?.Invoke(file, rating);
                    RatingWriteFailedDetailed?.Invoke(failure);
                });
            }

            return null;
        }
        finally
        {
            Console.WriteLine($"[Rating] === END SetRating({file.Name}): Total {ratingSw.ElapsedMilliseconds}ms ===");
        }
    }

    private async Task RefreshRatingsFromDiskAsync(PhotoGroup group)
    {
        long sequence;
        lock (_ratingRefreshLock)
        {
            if (_disposed) return;
            sequence = ++_ratingRefreshSequence;
            foreach (var file in group.Files) _ratingRefreshVersions[file.File.StorageId] = sequence;
        }

        try
        {
            var readings = await Task.Run(async () =>
            {
                var values = new List<(ImageFile File, IReadOnlyList<RatingSourceState> Sources)>();
                foreach (var file in group.Files)
                {
                    var (_, sources) = await RatingSourceReader.ReadAsync(
                        file.File, sidecarIndex: null).ConfigureAwait(false);
                    values.Add((file, sources));
                }
                return values;
            });
            await UiDispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                var changed = false;
                foreach (var (file, sources) in readings)
                {
                    lock (_ratingRefreshLock)
                    {
                        if (!_ratingRefreshVersions.TryGetValue(file.File.StorageId, out var current) || current != sequence)
                            continue;
                        file.RatingSidecarIndex = null;
                        file.ApplyRatingSources(sources);
                        changed = true;
                    }
                }
                if (changed) RefreshRatingDependentState();
            });
        }
        finally
        {
            lock (_ratingRefreshLock)
            {
                foreach (var file in group.Files)
                    if (_ratingRefreshVersions.TryGetValue(file.File.StorageId, out var current) && current == sequence)
                        _ratingRefreshVersions.Remove(file.File.StorageId);
            }
        }
    }

    private void RefreshRatingDependentState()
    {
        ControlVM.RaisePropertyChanged(nameof(ControlVM.CurrentExifData));
        if (FileVM.FilterBar.SelectedRatingFilter != "All")
            FileVM.ThumbnailList.RefreshFilters(retainFile: CurrentFile);
    }

    /// <summary>
    /// 用户写星级后顺手同步 photos.rating 副本(Plan-2-4)。
    /// 走代表文件的指纹,失败仅打日志,不影响主流程 — DB 缺写不影响产品体验,等下一次 indexer 跑也会补上。
    /// </summary>
    private static async Task SyncRatingToDatabaseAsync(ImageFile file, int rating)
    {
        try
        {
            var exif = await file.LoadExifDataAsync().ConfigureAwait(false);
            var input = PhotoFingerprint.BuildInput(file.Name, exif, file.ModifiedDate?.UtcDateTime);
            if (!input.CaptureTime.HasValue) return;
            var fp = PhotoFingerprint.Compute(input);
            await PhotoDatabase.UpdateRatingAsync(fp, rating).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Rating] DB sync failed for {file.Name}: {ex.Message}");
        }
    }

    #endregion
}
