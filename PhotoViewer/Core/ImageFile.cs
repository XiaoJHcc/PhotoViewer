using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ReactiveUI;

namespace PhotoViewer.Core;

public class ImageFile : ReactiveObject
{
    private Bitmap? _thumbnail;
    private bool _isCurrent;
        
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
                
            // Thumbnail = new Bitmap(bitmap.PlatformImpl);
            // Deepseek BUG
            Thumbnail = bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载缩略图失败: {ex.Message}");
        }
    }
}