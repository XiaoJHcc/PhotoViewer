using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI;
using System.IO;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using System.Linq;

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
                    var exifThumbnail = await TryLoadExifThumbnailAsync();
                    if (exifThumbnail != null)
                    {
                        Console.WriteLine($"使用EXIF缩略图: {Name}");
                        return exifThumbnail;
                    }

                    // 如果没有EXIF缩略图，则解码原图生成缩略图
                    Console.WriteLine($"生成缩略图: {Name}");
                    return await GenerateThumbnailFromImageAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"解码缩略图失败 ({Name}): {ex.Message}");
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载缩略图失败 ({Name}): {ex.Message}");
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }

    /// <summary>
    /// 尝试从EXIF中加载嵌入式缩略图
    /// </summary>
    private async Task<Bitmap?> TryLoadExifThumbnailAsync()
    {/*
        try
        {
            await using var stream = await File.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);
            
            // 查找缩略图目录
            var exifThumbnailDirectory = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
            if (exifThumbnailDirectory?.HasThumbnailData == true)
            {
                var thumbnailData = exifThumbnailDirectory.GetThumbnailData();
                if (thumbnailData?.Length > 0)
                {
                    using var thumbnailStream = new MemoryStream(thumbnailData);
                    var thumbnail = new Bitmap(thumbnailStream);
                    
                    // 如果EXIF缩略图过大，则缩放到合适大小
                    if (thumbnail.PixelSize.Width > 120)
                    {
                        var scale = 120.0 / thumbnail.PixelSize.Width;
                        var newSize = new PixelSize(
                            (int)(thumbnail.PixelSize.Width * scale),
                            (int)(thumbnail.PixelSize.Height * scale)
                        );
                        var scaledThumbnail = thumbnail.CreateScaledBitmap(newSize);
                        thumbnail.Dispose();
                        return scaledThumbnail;
                    }
                    
                    return thumbnail;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"提取EXIF缩略图失败 ({Name}): {ex.Message}");
        }*/
        
        return null;
    }

    /// <summary>
    /// 从原图生成缩略图
    /// </summary>
    private async Task<Bitmap?> GenerateThumbnailFromImageAsync()
    {
        try
        {
            await using var stream = await File.OpenReadAsync();
            var originalBitmap = new Bitmap(stream);
            
            // 生成缩略图 (120px宽度)
            if (originalBitmap.PixelSize.Width > 120)
            {
                var scale = 120.0 / originalBitmap.PixelSize.Width;
                var newSize = new PixelSize(
                    (int)(originalBitmap.PixelSize.Width * scale),
                    (int)(originalBitmap.PixelSize.Height * scale)
                );
                var scaledBitmap = originalBitmap.CreateScaledBitmap(newSize);
                originalBitmap.Dispose();
                return scaledBitmap;
            }
            
            return originalBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"从原图生成缩略图失败 ({Name}): {ex.Message}");
            return null;
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
                    Console.WriteLine($"读取 EXIF 数据失败 ({Name}): {ex.Message}");
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
            Console.WriteLine($"加载 EXIF 数据失败 ({Name}): {ex.Message}");
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