using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class ImageViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    public MainViewModel Main => _main;
    
    private string _hintText = "点击或拖入图片";
    public string HintText
    {
        get => _hintText;
        set => this.RaiseAndSetIfChanged(ref _hintText, value);
    }

    private Bitmap _sourceBitmap;
    public Bitmap SourceBitmap
    {
        get => _sourceBitmap;
        set => this.RaiseAndSetIfChanged(ref _sourceBitmap, value);
    }
    
    public event EventHandler<IStorageFile>? ImageLoaded;
    
    public ImageViewModel(MainViewModel mainViewModel)
    {
        _main = mainViewModel;
        
        Main.WhenAnyValue(vm => vm.CurrentFile)
            .Subscribe(currentFile =>
            {
                if (currentFile == null) ClearImage();
                else LoadImageAsync(currentFile.File);
            });
    }

    public async Task LoadImageAsync(IStorageFile file)
    {
        // 使用缓存服务加载图片
        var bitmap = await BitmapCacheService.GetBitmapAsync(file);
        if (bitmap == null) return;
        SourceBitmap = bitmap;
        ImageLoaded?.Invoke(this, file);
    }
    
    public void ClearImage()
    {
        SourceBitmap = null;
        ImageLoaded?.Invoke(this, null);
    }
}