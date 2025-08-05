using System;
using System.Threading.Tasks;
using Avalonia;
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
    
    private bool _hintTextVisible = true;
    public bool HintTextVisible
    {
        get => _hintTextVisible;
        set => this.RaiseAndSetIfChanged(ref _hintTextVisible, value);
    }

    private Bitmap? _sourceBitmap;
    public Bitmap? SourceBitmap
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
        
        // 图片加载完成时
        this.WhenAnyValue(vm => vm.SourceBitmap)
            .Subscribe(_ =>
            {
                OnBitmapChanged();
            });
    }

    /*
     * 打开图片
     */
    
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
    
    /*
     * 预览缩放
     */

    private double _scale;
    public double Scale
    {
        get => _scale;
        set => this.RaiseAndSetIfChanged(ref _scale, value);
    }

    private Vector _translate;
    public Vector Translate
    {
        get => _translate;
        set => this.RaiseAndSetIfChanged(ref _translate, value);
    }
    
    private Vector _imageSize;  // 图片原始大小
    public Vector ImageSize
    {
        get => _imageSize;
        set => this.RaiseAndSetIfChanged(ref _imageSize, value);
    }
    
    private Vector _viewSize;   // 显示区域大小
    public Vector ViewSize
    {
        get => _viewSize;
        set => this.RaiseAndSetIfChanged(ref _viewSize, value);
    }
    
    private bool _fit = true; // 适配屏幕
    public bool Fit
    {
        get => _fit;
        set => this.RaiseAndSetIfChanged(ref _fit, value);
    }

    private double _fitScale;   // 适配缩放值

    public double FitScale
    {
        get => _fitScale;
        set => this.RaiseAndSetIfChanged(ref _fitScale, value);
    }
    
    /// <summary>
    /// 窗口大小变更时 更新图片框视图
    /// </summary> 
    public void UpdateView(Vector viewSize)
    {
        var deltaSize = viewSize - ViewSize;
        ViewSize = viewSize;
        Console.WriteLine("ImageView.UpdateView() " + ViewSize.X + ", " + ViewSize.Y);
        if (ViewSize.X <= 0 || ViewSize.Y <= 0) return;
        if (Fit) FitToScreen();
        else Translate += deltaSize * 0.5;
    }
    
    /// <summary>
    /// 适应屏幕
    /// </summary>
    public void FitToScreen()
    {
        if (ImageSize == default) return;
        FitScale = Math.Min(ViewSize.X / ImageSize.X, ViewSize.Y / ImageSize.Y);
        Scale = FitScale;
        var moveToLeft = - ((1.0 - FitScale) * 0.5 * ImageSize);
        var moveLeftToCenter = (ViewSize - FitScale * ImageSize) * 0.5;
        Translate = moveToLeft + moveLeftToCenter;
        Fit = true;
    }

    public void ToggleFit(Vector center)
    {
        if (!Fit) FitToScreen();
        else Zoom(1, center);
    }

    /// <summary>
    /// 缩放图片：以窗口指定点为中心，缩放至指定倍率
    /// </summary>
    /// <param name="scale">目标倍率</param>
    /// <param name="center">缩放中心（窗口坐标系）</param>
    public void Zoom(double scale, Vector center)
    {
        var imageCenter = ImageSize * 0.5 + Translate;
        Translate += (Scale - scale) / Scale * (center - imageCenter);
        Scale = scale;
        Fit = false;
    }

    public void Zoom(double scale)
    {
        Zoom(scale, ViewSize * 0.5);
    }

    /// <summary>
    /// 移动图片
    /// </summary>
    /// <param name="delta">增量</param>
    public void Move(Vector delta)
    {
        Translate += delta;
    }

    /// <summary>
    /// 图片切换时保持缩放位置一致
    /// </summary>
    private void OnBitmapChanged()
    {
        if (SourceBitmap == null) return;
        var newImageSize = new Vector(SourceBitmap.PixelSize.Width, SourceBitmap.PixelSize.Height);
        if (ImageSize == newImageSize) return;
        if (Fit)
        {
            ImageSize = newImageSize;
            FitToScreen();
        }
        else
        {
            var uv = GetCenterUV();
            ImageSize = newImageSize;
            SetUVToCenter(uv);
        }
    }
    
    private Vector GetCenterUV()
    {
        var imageCenterPoint = ImageSize * 0.5 + Translate;
        var viewCenterOffset = ViewSize * 0.5 - imageCenterPoint;
        return Vector.Divide(viewCenterOffset, Scale * ImageSize);
    }
    
    private void SetUVToCenter(Vector uv)
    {
        var viewCenterOffset = Vector.Multiply(uv, ImageSize * Scale);
        var imageCenterPoint = ViewSize * 0.5 - viewCenterOffset;
        Translate = imageCenterPoint - 0.5 * ImageSize;
    }

}