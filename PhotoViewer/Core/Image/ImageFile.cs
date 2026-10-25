using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;
using PhotoViewer.Core.Platform;
using ReactiveUI;

namespace PhotoViewer.Core.Image;

public class ImageFile : ReactiveObject
{
    /// <summary>列表用缩略图的目标短边像素值。</summary>
    private const int ListThumbnailShortSide = 120;

    private bool _isCurrent;
    private bool _isCurrentImage;
    private ExifData? _exifData;
    private Task<ExifData?>? _exifLoadTask;
    private readonly object _exifLoadLock = new();
    private bool _isExifLoading;
    private bool _isExifLoaded;
    private bool _isThumbnailLoading;
    private bool _isInCache;
    private PhotoGroup? _group;
    private DateTimeOffset? _modifiedDate;
    private ulong? _fileSize;
    private bool _basicPropsLoaded;
    private bool? _isShake;
    private XmpRatingSnapshot _rating = XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);
    private IReadOnlyList<RatingSourceState> _ratingSources = Array.Empty<RatingSourceState>();

    /// <summary>
    /// 元数据世代：刷新开始、外部评分写入与清除 EXIF 时递增。
    /// 在途读取开始时记下世代，回写前校验；不一致说明结果已被更新的一次刷新/写入取代，整批丢弃。
    /// </summary>
    private int _metadataVersion;

    /// <summary>在途元数据读取数（EXIF 首读 + 显式刷新），用于正确收起 <see cref="IsExifLoading"/>。</summary>
    private int _activeMetadataReads;

    public IFileRef File { get; }
    public string Name => File.Name;

    /// <summary>
    /// 文件修改日期（异步初始化后可用，初始化前返回 null）。
    /// </summary>
    public DateTimeOffset? ModifiedDate
    {
        get => _modifiedDate;
        private set => this.RaiseAndSetIfChanged(ref _modifiedDate, value);
    }

    /// <summary>
    /// 文件大小（异步初始化后可用，初始化前返回 null）。
    /// </summary>
    public ulong? FileSize
    {
        get => _fileSize;
        private set => this.RaiseAndSetIfChanged(ref _fileSize, value);
    }
    
    /// <summary>
    /// 拍摄日期（优先使用EXIF中的拍摄时间，否则使用修改时间）
    /// </summary>
    public DateTimeOffset? PhotoDate
    {
        get
        {
            if (ExifData?.DateTimeOriginal.HasValue == true)
            {
                return new DateTimeOffset(ExifData.DateTimeOriginal.Value);
            }
            return ModifiedDate;
        }
    }
        
    /// <summary>
    /// 列表缩略图。存储于 <see cref="BitmapLoader"/> 统一缓存(Thumb 档),本属性为查询视图:
    /// getter 直查缓存,setter 写入/移除缓存项(淘汰与释放归缓存所有)。
    /// 缓存淘汰该档时经 <see cref="NotifyThumbnailChanged"/> 驱动 UI 回到占位符。
    /// </summary>
    public DecodedImage? Thumbnail
    {
        get => BitmapLoader.TryGetCached(File.StorageId.Value, ImageSizeClass.Thumb);
        private set
        {
            if (value != null)
                BitmapLoader.StoreThumbnail(File.StorageId.Value, value);
            else
                BitmapLoader.RemoveCached(File.StorageId.Value, ImageSizeClass.Thumb);
            this.RaisePropertyChanged(nameof(Thumbnail));
            this.RaisePropertyChanged(nameof(ShowThumbnailPlaceholder));
        }
    }

    /// <summary>缓存侧缩略图就绪/被淘汰时,驱动绑定重新读取 <see cref="Thumbnail"/> 视图。</summary>
    public void NotifyThumbnailChanged()
    {
        this.RaisePropertyChanged(nameof(Thumbnail));
        this.RaisePropertyChanged(nameof(ShowThumbnailPlaceholder));
    }
        
    public bool IsCurrent
    {
        get => _isCurrent;
        set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }

    /// <summary>
    /// 是否为主图视图当前显示的图片(独立于主缩略图列表的锚点高亮)。
    /// 由 <see cref="ViewModels.MainViewModel.CurrentFile"/> setter 维护;相似聚类面板的边框高亮绑定此属性,
    /// 这样点击相似项切换主图时,主缩略图列表的 <see cref="IsCurrent"/> 锚点保持不变。
    /// </summary>
    public bool IsCurrentImage
    {
        get => _isCurrentImage;
        set => this.RaiseAndSetIfChanged(ref _isCurrentImage, value);
    }

    /// <summary>
    /// 图片是否在缓存中
    /// </summary>
    public bool IsInCache
    {
        get => _isInCache;
        set => this.RaiseAndSetIfChanged(ref _isInCache, value);
    }

    /// <summary>
    /// EXIF 数据
    /// </summary>
    public ExifData? ExifData
    {
        get => _exifData;
        private set
        {
            var oldValue = _exifData;
            this.RaiseAndSetIfChanged(ref _exifData, value);
            if (!ReferenceEquals(oldValue, value))
            {
                if (value != null) AdoptRating(value);
                this.RaisePropertyChanged(nameof(PhotoDate));
                this.RaisePropertyChanged(nameof(Rating));
                this.RaisePropertyChanged(nameof(RatingState));
                this.RaisePropertyChanged(nameof(RatingSources));
                this.RaisePropertyChanged(nameof(HasRatingConflict));
            }
        }
    }

    /// <summary>
    /// EXIF 是否正在加载
    /// </summary>
    public bool IsExifLoading
    {
        get => _isExifLoading;
        private set => this.RaiseAndSetIfChanged(ref _isExifLoading, value);
    }

    /// <summary>
    /// EXIF 是否已加载
    /// </summary>
    public bool IsExifLoaded
    {
        get => _isExifLoaded;
        private set => this.RaiseAndSetIfChanged(ref _isExifLoaded, value);
    }
    
    /// <summary>
    /// 缩略图是否正在加载
    /// </summary>
    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isThumbnailLoading, value);
            this.RaisePropertyChanged(nameof(ShowThumbnailPlaceholder));
        }
    }

    /// <summary>
    /// 是否显示无缩略图占位符。
    /// </summary>
    public bool ShowThumbnailPlaceholder => Thumbnail == null && !IsThumbnailLoading;

    /// <summary>
    /// 所属同名合并组（<see cref="PhotoGroupMap"/> 构建时回填，未参与分组时为 null）。
    /// 组信息以该对象为唯一真源：显示名与星级冲突都从组计算。
    /// </summary>
    public PhotoGroup? Group
    {
        get => _group;
        internal set
        {
            if (ReferenceEquals(_group, value)) return;
            _group = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(DisplayName));
            this.RaisePropertyChanged(nameof(HasRatingConflict));
        }
    }

    public string DisplayName => Group?.DisplayName ?? Name;

    /// <summary>
    /// 是否存在星级冲突：代表文件与任意同伴文件（如 RAW）的星级不一致。
    /// 组内只有一个文件或不属于任何组时为 false。
    /// </summary>
    public bool HasRatingConflict => Group?.HasRatingConflict ?? false;

    public ImageFile(IFileRef file)
    {
        File = file;

        // 延迟初始化缓存状态，避免在静态类还未完全初始化时调用
        UiDispatcher.Post(() => UpdateCacheStatus());
    }

    /// <summary>
    /// 异步加载文件基本属性（大小、修改日期），避免在属性 getter 中同步阻塞。
    /// </summary>
    public async Task LoadBasicPropertiesAsync(CancellationToken cancellationToken = default)
    {
        if (_basicPropsLoaded) return;
        var version = Volatile.Read(ref _metadataVersion);
        try
        {
            var props = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // 期间已有刷新/评分写入接管：旧的属性读取不得覆盖新值。
            if (!IsCurrentMetadataVersion(version)) return;
            // 直接写入字段（不触发 RaisePropertyChanged），避免 1000+ 个 UiDispatcher.InvokeAsync。
            // 调用方在全部加载完成后统一在 UI 线程批量通知。
            _modifiedDate = props.DateModified;
            _fileSize = props.Size;
            _basicPropsLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load basic properties for {Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新磁盘元数据：基本属性、完整 EXIF、内嵌评分与已存在的 sidecar。
    /// 不依赖 <see cref="LoadBasicPropertiesAsync"/> 的 <c>_basicPropsLoaded</c> 与 <see cref="IsExifLoaded"/> 旧缓存短路；
    /// 读取期间旧 EXIF 继续展示，新数据就绪后一次替换，不会因刷新瞬时变成 0 星。
    /// 结果回写前校验元数据世代：期间被更新的一次刷新或评分写入取代时整批丢弃。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次结果已应用时为 true；被更新数据取代时为 false（不是失败）。</returns>
    /// <exception cref="StorageException">文件已不存在或元数据读取失败；旧数据保持不变。</exception>
    public async Task<bool> RefreshMetadataAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _metadataVersion);
        BeginMetadataRead();
        try
        {
            var (properties, exifData) = await Task.Run(async () =>
            {
                var basic = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!basic.Exists)
                    throw new StorageException(StorageError.ObjectGone, $"{Name} 已不存在");
                var exif = await ExifLoader.LoadExifDataAsync(File, RatingSidecarIndex).ConfigureAwait(false);
                return (basic, exif);
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var applied = false;
            await UiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentMetadataVersion(version)) return;
                applied = true;

                _modifiedDate = properties.DateModified;
                _fileSize = properties.Size;
                _basicPropsLoaded = true;
                this.RaisePropertyChanged(nameof(ModifiedDate));
                this.RaisePropertyChanged(nameof(FileSize));
                this.RaisePropertyChanged(nameof(PhotoDate));

                if (exifData != null)
                {
                    ExifData = exifData;
                    IsExifLoaded = true;
                }
            });

            if (applied && exifData == null)
                throw new StorageException(StorageError.IoError, $"无法读取 {Name} 的 EXIF 数据");

            return applied;
        }
        finally
        {
            EndMetadataRead();
        }
    }

    /// <summary>记录一次元数据读取在途；多个读取重叠时最后结束的负责收起加载态。</summary>
    private void BeginMetadataRead()
    {
        Interlocked.Increment(ref _activeMetadataReads);
        IsExifLoading = true;
    }

    private void EndMetadataRead()
    {
        if (Interlocked.Decrement(ref _activeMetadataReads) <= 0) IsExifLoading = false;
    }

    private bool IsCurrentMetadataVersion(int version) => Volatile.Read(ref _metadataVersion) == version;

    /// <summary>
    /// 更新缓存状态
    /// </summary>
    public void UpdateCacheStatus()
    {
        try
        {
            var filePath = File.StorageId.Value;
            IsInCache = BitmapLoader.IsInCache(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update cache status for {Name}: {ex.Message}");
            IsInCache = false;
        }
    }
        
    public async Task LoadThumbnailAsync()
    {
        if (Thumbnail != null || IsThumbnailLoading) return;

        IsThumbnailLoading = true;
        try
        {
            // 在后台线程中生成缩略图：约 120 短边的列表用缩略图。
            var bitmap = await Task.Run(() => ThumbnailService.GetThumbnailAsync(File, ListThumbnailShortSide));
            
            // 在UI线程中设置结果
            await UiDispatcher.InvokeAsync(() =>
            {
                if (bitmap != null)
                {
                    Thumbnail = bitmap;
                }
            });

            // 缩略图已就绪后加载 EXIF 数据（用于旋转角度等），消费者限流保证并发可控。
            _ = LoadExifDataAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load thumbnail (" + Name + "): " + ex.Message);
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }


    /// <summary>
    /// 异步加载 EXIF 数据。并发调用共享同一个加载 Task,避免后到的调用方拿到半成品的 null。
    /// </summary>
    public Task<ExifData?> LoadExifDataAsync()
    {
        if (IsExifLoaded) return Task.FromResult(ExifData);

        lock (_exifLoadLock)
        {
            if (IsExifLoaded) return Task.FromResult(ExifData);
            if (_exifLoadTask != null) return _exifLoadTask;
            _exifLoadTask = LoadExifDataCoreAsync();
            return _exifLoadTask;
        }
    }

    private async Task<ExifData?> LoadExifDataCoreAsync()
    {
        var version = Volatile.Read(ref _metadataVersion);
        BeginMetadataRead();
        try
        {
            // 在后台线程中读取EXIF数据
            var exifData = await Task.Run(async () =>
            {
                try
                {
                    return await ExifLoader.LoadExifDataAsync(File, RatingSidecarIndex);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read EXIF data (" + Name + "): " + ex.Message);
                    return null;
                }
            });

            // 在UI线程中设置结果；期间已刷新或被评分写入取代时整批丢弃，绝不让旧来源复活。
            await UiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentMetadataVersion(version)) return;
                if (exifData != null) ExifData = exifData;
                IsExifLoaded = true;
            });

            return IsCurrentMetadataVersion(version) ? ExifData : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load EXIF data (" + Name + "): " + ex.Message);
            return null;
        }
        finally
        {
            EndMetadataRead();
            lock (_exifLoadLock)
            {
                _exifLoadTask = null;
            }
        }
    }

    /// <summary>
    /// 仅加载星级信息（用于快速筛选，不加载其他 EXIF 数据）。
    /// 内嵌与已存在的 sidecar 各自作为独立来源读取；sidecar 不遮蔽内嵌。
    /// </summary>
    /// <param name="cancellationToken">取消令牌：打开被后发请求取代时停止读取，且不写入旧结果。</param>
    /// <param name="sidecarIndex">文件夹枚举得到的 sidecar 索引；避免逐个文件做目录查找。</param>
    public async Task LoadRatingOnlyAsync(
        CancellationToken cancellationToken = default,
        RatingSidecarIndex? sidecarIndex = null)
    {
        // 如果已经有完整的EXIF数据，直接返回
        if (IsExifLoaded && ExifData != null) return;

        // 如果正在加载完整EXIF数据，等待完成
        if (IsExifLoading) return;

        var version = Volatile.Read(ref _metadataVersion);
        try
        {
            var index = sidecarIndex ?? RatingSidecarIndex;
            var (embedded, sources) = await Task.Run(
                () => ExifLoader.LoadRatingSourcesAsync(File, index, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // 读取期间已被取消：不写入本次会话的旧结果。
            cancellationToken.ThrowIfCancellationRequested();

            // 期间已有刷新或评分写入接管：旧的来源结果（含已删除的 sidecar）不得写回。
            if (!IsCurrentMetadataVersion(version)) return;

            // 直接写入字段（不触发 RaisePropertyChanged），避免 1000+ 个 UiDispatcher.InvokeAsync。
            // 调用方 LoadRatingsAsync 在全部加载完成后统一在 UI 线程批量通知。
            ApplyRatingCore(embedded, sources);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load rating (" + Name + "): " + ex.Message);
        }
    }

    /// <summary>
    /// 强制重新加载 EXIF 数据
    /// </summary>
    public async Task ForceReloadExifDataAsync() => await RefreshMetadataAsync();

    /// <summary>
    /// 写入或补偿后用磁盘真值更新本文件的全部评分来源。
    /// 同时推进元数据世代：更早开始的在途 EXIF/来源读取完成后一律丢弃，不得覆盖或复活旧 sidecar。
    /// </summary>
    public void ApplyRatingSources(IReadOnlyList<RatingSourceState> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Interlocked.Increment(ref _metadataVersion);
        var embedded = sources.FirstOrDefault(source => source.Source == XmpRatingSource.Embedded)?.Snapshot
            ?? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);
        ApplyRatingCore(embedded, sources);
        this.RaisePropertyChanged(nameof(Rating));
        this.RaisePropertyChanged(nameof(RatingState));
        this.RaisePropertyChanged(nameof(RatingSources));
        this.RaisePropertyChanged(nameof(HasRatingConflict));
    }

    /// <summary>
    /// 仅更新当前对象的内存星级缓存（内嵌来源），避免在文件已写入后仍使用旧的运行期星级参与筛选。
    /// </summary>
    /// <param name="rating">新的星级：0~5；-1 表示拒绝。</param>
    public void UpdateCachedRating(int rating)
    {
        Interlocked.Increment(ref _metadataVersion);
        var snapshot = rating == -1
            ? new XmpRatingSnapshot(XmpRatingState.Rejected, -1, XmpRatingSource.Embedded)
            : new XmpRatingSnapshot(XmpRatingState.Rated, rating, XmpRatingSource.Embedded);
        ApplyRatingCore(snapshot, [new RatingSourceState(XmpRatingSource.Embedded, File, snapshot)]);
        this.RaisePropertyChanged(nameof(Rating));
        this.RaisePropertyChanged(nameof(RatingState));
        this.RaisePropertyChanged(nameof(RatingSources));
        this.RaisePropertyChanged(nameof(HasRatingConflict));
    }

    /// <summary>
    /// 清除 EXIF 数据；推进元数据世代，在途读取完成后不再写回。
    /// </summary>
    public void ClearExifData()
    {
        Interlocked.Increment(ref _metadataVersion);
        ExifData = null;
        IsExifLoaded = false;
        IsExifLoading = false;
        lock (_exifLoadLock)
        {
            _exifLoadTask = null;
        }
    }

    /// <summary>
    /// 星级（0~5，来自本文件内嵌 XMP；-1 表示已拒绝；缺失/无效/冲突为 0）。
    /// 代表文件的该值就是胶片栏数字筛选的依据；sidecar 是独立来源，不参与此值。
    /// </summary>
    public int Rating => _rating.NumericValue;

    /// <summary>内嵌来源的完整状态；Missing 与 Rated 0 由此区分，不与数值混同。</summary>
    public XmpRatingState RatingState => _rating.State;

    /// <summary>全部实际评分来源（内嵌 + 已存在的 sidecar），供详情按来源展示。</summary>
    public IReadOnlyList<RatingSourceState> RatingSources => _ratingSources;

    /// <summary>
    /// 文件夹枚举得到的 sidecar 索引；由 <see cref="ViewModels.Main.FolderLoadSession"/> 设置。
    /// 为 null 时读取才回退到逐文件查找同名 sidecar。
    /// </summary>
    public RatingSidecarIndex? RatingSidecarIndex { get; set; }

    private void ApplyRatingCore(XmpRatingSnapshot embedded, IReadOnlyList<RatingSourceState> sources)
    {
        _rating = embedded;
        _ratingSources = sources;
        if (_exifData == null)
        {
            _exifData = new ExifData
            {
                FilePath = File.StorageId.Value,
            };
        }

        _exifData.EmbeddedRating = embedded;
        _exifData.RatingState = embedded.State;
        _exifData.Rating = embedded.ValueOrNull;
        _exifData.RatingSources = sources.ToList();
    }

    private void AdoptRating(ExifData data)
    {
        if (data.EmbeddedRating is { } embedded)
        {
            _rating = embedded;
            _ratingSources = data.RatingSources.Count > 0
                ? data.RatingSources
                : [new RatingSourceState(XmpRatingSource.Embedded, File, embedded)];
            return;
        }

        // 兼容只写了数值的旧路径：null 视为缺失，-1 视为拒绝。
        var snapshot = data.Rating switch
        {
            null => XmpRatingSnapshot.Missing(XmpRatingSource.Embedded),
            -1 => new XmpRatingSnapshot(XmpRatingState.Rejected, -1, XmpRatingSource.Embedded),
            var value => new XmpRatingSnapshot(XmpRatingState.Rated, value, XmpRatingSource.Embedded),
        };
        _rating = snapshot;
        _ratingSources = data.RatingSources.Count > 0
            ? data.RatingSources
            : [new RatingSourceState(XmpRatingSource.Embedded, File, snapshot)];
        data.EmbeddedRating = snapshot;
        data.RatingState = snapshot.State;
    }

    /// <summary>
    /// 抖动判定:null = 未判定(无 CV 缓存或解码失败,不挂徽标);true = 平移/旋转/强旋转抖动;false = 静止/纹理/混乱/弱信号。
    /// 由 <see cref="PhotoViewer.Core.AI.ShakeFlagService"/> 在文件夹加载完成与批量索引完成后回填。
    /// </summary>
    public bool? IsShake
    {
        get => _isShake;
        set => this.RaiseAndSetIfChanged(ref _isShake, value);
    }
}