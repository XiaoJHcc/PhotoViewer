using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ReactiveUI;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Core;

public class ImageFile : ReactiveObject
{
    private Bitmap? _thumbnail;
    private bool _isCurrent;
    private ExifData? _exifData;
    private bool _isExifLoading;
    private bool _isExifLoaded;
        
    public IStorageFile File { get; }
    public string Name => File.Name;
    public DateTimeOffset? ModifiedDate => File.GetBasicPropertiesAsync().Result.DateModified;
    public ulong? FileSize => File.GetBasicPropertiesAsync().Result.Size;
        
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
        private set => this.RaiseAndSetIfChanged(ref _exifData, value);
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
        
    public ImageFile(IStorageFile file)
    {
        File = file;
    }
        
    public async Task LoadThumbnailAsync()
    {
        try
        {
            await using var stream = await File.OpenReadAsync();
            var bitmap = new Bitmap(stream);
                
            // 生成缩略图 (120px宽度)
            if (bitmap.PixelSize.Width > 120)
            {
                var scale = 120.0 / bitmap.PixelSize.Width;
                var newSize = new PixelSize(
                    (int)(bitmap.PixelSize.Width * scale),
                    (int)(bitmap.PixelSize.Height * scale)
                );
                bitmap = bitmap.CreateScaledBitmap(newSize);
            }
                
            Thumbnail = bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载缩略图失败: {ex.Message}");
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
            ExifData = await ExifLoader.LoadExifDataAsync(File);
            IsExifLoaded = true;
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
}