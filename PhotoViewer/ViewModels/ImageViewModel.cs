using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class ImageViewModel : ReactiveObject
{
    private readonly MainViewModel _mainViewModel;
    
    private string _hintText = "点击或拖入图片";
    public string HintText
    {
        get => _hintText;
        set => this.RaiseAndSetIfChanged(ref _hintText, value);
    }
    
    private Bitmap? _currentImage;
        
    public Bitmap? CurrentImage
    {
        get => _currentImage;
        set => this.RaiseAndSetIfChanged(ref _currentImage, value);
    }
        
    public ImageViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
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