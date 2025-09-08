using System;
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
    /// EXIF 数据
    /// </summary>
    public ExifData? ExifData
    {
        get => _exifData;
        private set
        {
            var oldValue = _exifData;
            this.RaiseAndSetIfChanged(ref _exifData, value);
            
            // 只有当值真正发生变化时才通知拍摄日期变化
            if (!ReferenceEquals(oldValue, value))
            {
                this.RaisePropertyChanged(nameof(PhotoDate));
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
        
    public ImageFile(IStorageFile file)
    {
        File = file;
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
                    // 首先尝试从EXIF中提取嵌入式缩略图
                    var exifThumbnail = await ExifLoader.TryLoadExifThumbnailAsync(File);
                    if (exifThumbnail != null)
                    {
                        return exifThumbnail;
                    }

                    // 如果没有EXIF缩略图，则解码原图生成缩略图
                    return await ExifLoader.GenerateThumbnailFromImageAsync(File);
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
}