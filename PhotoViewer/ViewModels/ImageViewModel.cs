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
    
    private string _HintText = "点击打开文件 或 拖拽图片到此处";
    public string HintText
    {
        get => _HintText;
        set => this.RaiseAndSetIfChanged(ref _HintText, value);
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

        // 监听影响缩略图显示的属性变化
        this.WhenAnyValue(
            vm => vm.Main.Settings.ShowZoomIndicator,
            vm => vm.Fit,
            vm => vm.Scale,
            vm => vm.Translate,
            vm => vm.ViewSize,
            vm => vm.ImageSize,
            vm => vm.SourceBitmap)
            .Subscribe(_ => UpdateZoomIndicator());
    }

    /*
     * 打开图片
     */
    
    public async Task LoadImageAsync(IStorageFile file)
    {
        try
        {
            HintText = string.Empty;
            
            // 使用缓存服务加载图片
            var bitmap = await BitmapLoader.GetBitmapAsync(file);
            
            if (bitmap == null) 
            {
                Console.WriteLine($"Failed to load bitmap for file: {file.Name}");
                SourceBitmap = null;
                HintText = "无法打开该图片";
                return;
            }
            
            // 验证位图有效性
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap dimensions: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} for file: {file.Name}");
                bitmap.Dispose();
                SourceBitmap = null;
                HintText = "图片解码无效";
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

    /*
     * 缩放指示器相关属性
     */
    
    private bool _showZoomIndicator;
    public bool ShowZoomIndicator
    {
        get => _showZoomIndicator;
        private set => this.RaiseAndSetIfChanged(ref _showZoomIndicator, value);
    }

    private Vector _thumbnailDisplaySize;
    public Vector ThumbnailDisplaySize
    {
        get => _thumbnailDisplaySize;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailDisplaySize, value);
    }

    private Vector _thumbnailImageSize;
    public Vector ThumbnailImageSize
    {
        get => _thumbnailImageSize;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailImageSize, value);
    }

    private Vector _thumbnailImageOffset;
    public Vector ThumbnailImageOffset
    {
        get => _thumbnailImageOffset;
        private set => this.RaiseAndSetIfChanged(ref _thumbnailImageOffset, value);
    }

    private Vector _viewportFrameSize;
    public Vector ViewportFrameSize
    {
        get => _viewportFrameSize;
        private set => this.RaiseAndSetIfChanged(ref _viewportFrameSize, value);
    }

    private Vector _viewportFramePosition;
    public Vector ViewportFramePosition
    {
        get => _viewportFramePosition;
        private set => this.RaiseAndSetIfChanged(ref _viewportFramePosition, value);
    }

    private string _zoomPercentageText = "100%";
    public string ZoomPercentageText
    {
        get => _zoomPercentageText;
        private set => this.RaiseAndSetIfChanged(ref _zoomPercentageText, value);
    }

    /// <summary>
    /// 更新缩略图相关属性
    /// </summary>
    private void UpdateZoomIndicator()
    {
        // 只在非适配状态且有图片时显示
        ShowZoomIndicator = Main.Settings.ShowZoomIndicator && !Fit && SourceBitmap != null && ImageSize.X > 0 && ImageSize.Y > 0 && ViewSize.X > 0 && ViewSize.Y > 0;

        if (!ShowZoomIndicator) return;

        const double maxThumbnailSize = 150;

        // 计算缩略图显示尺寸（保持原图比例，长边为150）
        var imageAspect = ImageSize.X / ImageSize.Y;
        if (imageAspect > 1) // 横图
        {
            ThumbnailDisplaySize = new Vector(maxThumbnailSize, maxThumbnailSize / imageAspect);
        }
        else // 竖图
        {
            ThumbnailDisplaySize = new Vector(maxThumbnailSize * imageAspect, maxThumbnailSize);
        }

        // 缩略图内的图片尺寸和偏移（居中显示）
        ThumbnailImageSize = ThumbnailDisplaySize;
        ThumbnailImageOffset = new Vector(0, 0);

        // 计算可见区域白框
        UpdateZoomViewportFrame();

        // 计算缩放百分比（保留两位小数）
        ZoomPercentageText = $"{Scale * 100:0.##}%";
    }

    /// <summary>
    /// 计算可见区域白框的位置和大小
    /// </summary>
    private void UpdateZoomViewportFrame()
    {
        if (ImageSize.X <= 0 || ImageSize.Y <= 0 || ViewSize.X <= 0 || ViewSize.Y <= 0) return;

        // 白框尺寸
        // 显示大小 / 显示缩放 = 缩略大小 / 缩略缩放
        var thumbnailScale = ThumbnailDisplaySize.X / ImageSize.X;
        ViewportFrameSize = ViewSize / Scale * thumbnailScale;
        
        // 白框位置
        var frameCenter = Vector.Multiply(GetCenterUV(), ThumbnailDisplaySize) + ThumbnailDisplaySize * 0.5;
        ViewportFramePosition = frameCenter - ViewportFrameSize * 0.5;
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
        else ZoomTo(1, center);
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
    public void ZoomTo(double scale, Vector center)
    {
        var imageCenter = ImageSize * 0.5 + Translate;
        Translate += (Scale - scale) / Scale * (center - imageCenter);
        Scale = scale;
        ClampCenter();
        Fit = false;
    }

    public void ZoomTo(double scale)
    {
        ZoomTo(scale, ViewSize * 0.5);
    }

    /// <summary>
    /// 缩放至预设
    /// </summary>
    /// <param name="levelOffset">放大或缩小几挡</param>
    public void ZoomPreset(int levelOffset)
    {
        ZoomPreset(levelOffset, ViewSize * 0.5);
    }

    /// <summary>
    /// 缩放至预设（带中心）
    /// </summary>
    /// <param name="levelOffset">放大或缩小几挡</param>
    /// <param name="center">缩放中心（窗口坐标系）</param>
    public void ZoomPreset(int levelOffset, Vector center)
    {
        var presets = Main.Settings.ScalePresets;
        var offset = levelOffset;
        if (offset > 0)
        {
            for (int i = 0; i < presets.Count && offset != 0; i++)
            {
                if (presets[i].Value > Scale) offset--;
                if (offset == 0) ZoomTo(presets[i].Value, center);
            }
        }
        else if (offset < 0)
        {
            for (int i = presets.Count - 1; i >= 0 && offset != 0; i--)
            {
                if (presets[i].Value < Scale) offset++;
                if (offset == 0) ZoomTo(presets[i].Value, center);
            }
        }
    }

    /// <summary>
    /// 缩放等比
    /// </summary>
    public void ZoomScale(double scale, Vector center)
    {
        ZoomTo(Scale * scale, center);
    }
    
    public void ZoomScale(double scale)
    {
        ZoomTo(Scale * scale);
    }

    /// <summary>
    /// 移动图片
    /// </summary>
    /// <param name="delta">增量</param>
    public void Move(Vector delta)
    {
        if (Fit) return; // 适应屏幕时不响应移动
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

    /// <summary>
    /// 获取屏幕中间点在图片上的相对位置（左上角 -0.5,-0.5，右下角 0.5,0.5）
    /// </summary>
    /// <returns></returns>
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