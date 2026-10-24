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
                this.RaisePropertyChanged(nameof(PhotoDate));
                this.RaisePropertyChanged(nameof(Rating));
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
        try
        {
            var props = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
        IsExifLoading = true;
        try
        {
            // 在后台线程中读取EXIF数据
            var exifData = await Task.Run(async () =>
            {
                try
                {
                    return await ExifLoader.LoadExifDataAsync(File);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read EXIF data (" + Name + "): " + ex.Message);
                    return null;
                }
            });

            // 在UI线程中设置结果
            await UiDispatcher.InvokeAsync(() =>
            {
                ExifData = exifData;
                IsExifLoaded = true;
            });

            return ExifData;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load EXIF data (" + Name + "): " + ex.Message);
            return null;
        }
        finally
        {
            IsExifLoading = false;
            lock (_exifLoadLock)
            {
                _exifLoadTask = null;
            }
        }
    }

    /// <summary>
    /// 仅加载星级信息（用于快速筛选，不加载其他EXIF数据）
    /// </summary>
    /// <param name="cancellationToken">取消令牌：打开被后发请求取代时停止读取，且不写入旧结果。</param>
    public async Task LoadRatingOnlyAsync(CancellationToken cancellationToken = default)
    {
        // 如果已经有完整的EXIF数据，直接返回
        if (IsExifLoaded && ExifData != null) return;
        
        // 如果正在加载完整EXIF数据，等待完成
        if (IsExifLoading) return;

        try
        {
            var rating = await Task.Run(
                () => ExifLoader.LoadRatingOnlyAsync(File, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // 读取期间已被取消：不写入本次会话的旧结果。
            cancellationToken.ThrowIfCancellationRequested();

            // 直接写入字段（不触发 RaisePropertyChanged），避免 1000+ 个 UiDispatcher.InvokeAsync。
            // 调用方 LoadRatingsAsync 在全部加载完成后统一在 UI 线程批量通知。
            if (_exifData == null)
            {
                _exifData = new ExifData 
                { 
                    FilePath = File.StorageId.Value,
                    Rating = rating 
                };
            }
            else
            {
                _exifData.Rating = rating;
            }
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
    public async Task ForceReloadExifDataAsync()
    {
        ClearExifData();
        await LoadExifDataAsync();
    }

    /// <summary>
    /// 仅更新当前对象的内存星级缓存，避免在文件已写入后仍使用旧的运行期星级参与筛选。
    /// </summary>
    /// <param name="rating">新的星级（0~5）</param>
    public void UpdateCachedRating(int rating)
    {
        // 若当前尚未加载 EXIF，则创建一个仅包含星级的最小缓存，保证筛选与界面立即使用新值。
        if (ExifData == null)
        {
            ExifData = new ExifData
            {
                FilePath = File.StorageId.Value,
                Rating = rating
            };
        }
        else
        {
            ExifData.Rating = rating;
            this.RaisePropertyChanged(nameof(Rating));
        }

        this.RaisePropertyChanged(nameof(HasRatingConflict));
    }

    /// <summary>
    /// 清除 EXIF 数据
    /// </summary>
    public void ClearExifData()
    {
        ExifData = null;
        IsExifLoaded = false;
        IsExifLoading = false;
        lock (_exifLoadLock)
        {
            _exifLoadTask = null;
        }
    }
    
    /// <summary>
    /// 星级（0~5，来自 EXIF/XMP；若无则为0）
    /// </summary>
    public int Rating => ExifData?.Rating ?? 0;

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