using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class ImageViewModel : ReactiveObject
{
    private readonly AppState _state;
    private Bitmap? _currentImage;
    
    public Bitmap? CurrentImage
    {
        get => _currentImage;
        private set => this.RaiseAndSetIfChanged(ref _currentImage, value);
    }
    
    public ImageViewModel(AppState state)
    {
        _state = state;
    }
    
    public async Task LoadImage(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            CurrentImage = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载图片失败: {ex.Message}");
            CurrentImage = null;
        }
    }
}