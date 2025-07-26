using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using PhotoViewer.Views;
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
    
    public ImageViewModel(MainViewModel mainViewModel)
    {
        _main = mainViewModel;
        
        Main.WhenAnyValue(vm => vm.CurrentFile)
            .Where(file => file != null)
            .Subscribe(currentFile => LoadImageAsync(currentFile.File));
    }

    public async Task LoadImageAsync(IStorageFile file)
    {
        // 使用缓存服务加载图片
        var bitmap = await BitmapCacheService.GetBitmapAsync(file);
        if (bitmap == null) return;
        SourceBitmap = bitmap;
    }
}