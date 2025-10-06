using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI;

namespace PhotoViewer.Core;

public class ImageFile : ReactiveObject
{
    private Bitmap? _thumbnail;
    private bool _isCurrent;
    private ExifData? _exifData;
    private bool _isExifLoading;
    private bool _isExifLoaded;
    private bool _isThumbnailLoading;
    private bool _isInCache;
    private string? _displayName;

    public IStorageFile File { get; }
    public string Name => File.Name;
    public DateTimeOffset? ModifiedDate => File.GetBasicPropertiesAsync().Result.DateModified;
    public ulong? FileSize => File.GetBasicPropertiesAsync().Result.Size;
    
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
        
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
    }
        
    public bool IsCurrent
    {
        get => _isCurrent;
        set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
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
    /// 图片旋转角度（基于EXIF Orientation）
    /// </summary>
    public double RotationAngle
    {
        get
        {
            if (ExifData?.OrientationValue != null)
            {
                return ExifLoader.GetRotationAngle(ExifData.OrientationValue);
            }
            return 0;
        }
    }

    /// <summary>
    /// 是否需要水平翻转（基于EXIF Orientation）
    /// </summary>
    public bool NeedsHorizontalFlip
    {
        get
        {
            if (ExifData?.OrientationValue != null)
            {
                return ExifLoader.NeedsHorizontalFlip(ExifData.OrientationValue);
            }
            return false;
        }
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
                this.RaisePropertyChanged(nameof(RotationAngle));
                this.RaisePropertyChanged(nameof(NeedsHorizontalFlip));
                this.RaisePropertyChanged(nameof(Rating)); // 新增：星级同步更新
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
        private set => this.RaiseAndSetIfChanged(ref _isThumbnailLoading, value);
    }

    public string DisplayName
    {
        get => _displayName ?? Name;
        set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    // 被同名合并隐藏的其它格式文件
    public List<IStorageFile> HiddenFiles { get; } = new();

    // 工具：清空隐藏文件并重置显示名
    public void ResetGrouping()
    {
        HiddenFiles.Clear();
        DisplayName = Name;
    }

    public ImageFile(IStorageFile file)
    {
        File = file;

        // 延迟初始化缓存状态，避免在静态类还未完全初始化时调用
        Dispatcher.UIThread.Post(() => UpdateCacheStatus());
    }

    /// <summary>
    /// 更新缓存状态
    /// </summary>
    public void UpdateCacheStatus()
    {
        try
        {
            var filePath = File.Path.LocalPath;
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
            // 在后台线程中生成缩略图
            var bitmap = await Task.Run(async () =>
            {
                try
                {
                    // 检查是否为 HEIF 格式
                    if (HeifLoader.IsHeifFile(File))
                    {
                        // 先尝试 HEIF 缩略图
                        var heifThumbnail = await HeifLoader.LoadHeifThumbnailAsync(File);
                        if (heifThumbnail != null)
                        {
                            return heifThumbnail;
                        }
                    }
                    else
                    {
                        // 首先尝试从EXIF中提取嵌入式缩略图
                        var exifThumbnail = await ExifLoader.TryLoadExifThumbnailAsync(File);
                        if (exifThumbnail != null)
                        {
                            return exifThumbnail;
                        }

                        // 如果没有EXIF缩略图，则解码原图生成缩略图
                        return await ExifLoader.GenerateThumbnailFromImageAsync(File);
                    }
                    
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to decode thumbnail (" + Name + "): " + ex.Message);
                    return null;
                }
            });
            
            // 在UI线程中设置结果
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (bitmap != null)
                {
                    Thumbnail = bitmap;
                }
            });

            // 加载缩略图的同时异步加载 EXIF 数据（用于旋转和其他信息）
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
    /// 异步加载 EXIF 数据
    /// </summary>
    public async Task<ExifData?> LoadExifDataAsync()
    {
        if (IsExifLoaded || IsExifLoading) return ExifData;

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
            await Dispatcher.UIThread.InvokeAsync(() =>
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
        }
    }

    /// <summary>
    /// 仅加载星级信息（用于快速筛选，不加载其他EXIF数据）
    /// </summary>
    public async Task LoadRatingOnlyAsync()
    {
        // 如果已经有完整的EXIF数据，直接返回
        if (IsExifLoaded && ExifData != null) return;
        
        // 如果正在加载完整EXIF数据，等待完成
        if (IsExifLoading) return;

        try
        {
            // 在后台线程中只读取星级信息
            var rating = await Task.Run(async () =>
            {
                try
                {
                    return await ExifLoader.LoadRatingOnlyAsync(File);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read rating (" + Name + "): " + ex.Message);
                    return 0;
                }
            });
            
            // 在UI线程中更新星级信息
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 如果还没有EXIF数据，创建一个只包含星级的ExifData
                if (ExifData == null)
                {
                    ExifData = new ExifData 
                    { 
                        FilePath = File.Path.LocalPath,
                        Rating = rating 
                    };
                }
                else
                {
                    // 如果已有EXIF数据，只更新星级
                    ExifData.Rating = rating;
                }
            });
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
    /// 清除 EXIF 数据
    /// </summary>
    public void ClearExifData()
    {
        ExifData = null;
        IsExifLoaded = false;
        IsExifLoading = false;
    }
    
    /// <summary>
    /// 清除缩略图
    /// </summary>
    public void ClearThumbnail()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        IsThumbnailLoading = false;
    }
    
    /// <summary>
    /// 星级（0~5，来自 EXIF/XMP；若无则为0）
    /// </summary>
    public int Rating => ExifData?.Rating ?? 0;
}