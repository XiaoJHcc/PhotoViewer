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
        try
        {
            // 使用缓存服务加载图片
            var bitmap = await BitmapLoader.GetBitmapAsync(file);
            if (bitmap == null) 
            {
                Console.WriteLine($"Failed to load bitmap for file: {file.Name}");
                return;
            }
            
            // 验证位图有效性
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap dimensions: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} for file: {file.Name}");
                bitmap.Dispose();
                return;
            }
            
            SourceBitmap = bitmap;
            ImageLoaded?.Invoke(this, file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load image in ImageViewModel ({file.Name}): {ex.Message}");
        }
    }
    
    public void ClearImage()
    {
        try
        {
            SourceBitmap = null;
            ImageLoaded?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to clear image: {ex.Message}");
        }
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
    
    public void ToggleFit()
    {
        ToggleFit(ViewSize * 0.5);
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
        ClampCenter();
        Fit = false;
    }

    public void Zoom(double scale)
    {
        Zoom(scale, ViewSize * 0.5);
    }

    /// <summary>
    /// 缩放图片至预设百分比
    /// </summary>
    /// <param name="levelOffset">放大或缩小几挡</param>
    public void ZoomPreset(int levelOffset)
    {
        var presets = Main.Settings.ScalePresets;
        var offset = levelOffset;
        if (offset > 0)
        {
            for (int i = 0; i < presets.Count && offset != 0; i++)
            {
                if (presets[i].Value > Scale) offset--;
                if (offset == 0) Zoom(presets[i].Value);
            }
        }
        else if (offset < 0)
        {
            for (int i = presets.Count - 1; i >= 0 && offset != 0; i--)
            {
                if (presets[i].Value < Scale) offset++;
                if (offset == 0) Zoom(presets[i].Value);
            }
        }
    }

    /// <summary>
    /// 移动图片
    /// </summary>
    /// <param name="delta">增量</param>
    public void Move(Vector delta)
    {
        Translate += delta;
        ClampCenter();
    }

    /// <summary>
    /// 图片切换时保持缩放位置一致
    /// </summary>
    private void OnBitmapChanged()
    {
        try
        {
            if (SourceBitmap == null) return;
            
            // 验证位图尺寸
            if (SourceBitmap.PixelSize.Width <= 0 || SourceBitmap.PixelSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap size in OnBitmapChanged: {SourceBitmap.PixelSize.Width}x{SourceBitmap.PixelSize.Height}");
                return;
            }
            
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
        catch (Exception ex)
        {
            Console.WriteLine($"Failed in OnBitmapChanged: {ex.Message}");
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

    private void ClampCenter()
    {
        var clamp1 = ViewSize * 0.5 - ImageSize * 0.5;
        var clamp2 = ImageSize * Scale * 0.5;
        Translate = Vector.Clamp(Translate, clamp1 - clamp2, clamp1 + clamp2);
    }

}