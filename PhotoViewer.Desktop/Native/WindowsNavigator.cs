using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using SkiaSharp;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 主图缩放指示器(导航图):右下角缩略图 + 白色可见框 + 缩放百分比
/// (旧壳 <c>ImageView.axaml:74-107</c>,几何见 <see cref="WindowsLayout"/>)。
///
/// 缩略图由当前主图会话的线性 RGBA16F 代理帧经 Skia 直接缩到导航尺寸并转 sRGB8
/// (与 macOS 的 <c>MetalPhotoView.CaptureLinearFrame(navigation: true)</c> 同口径:
/// 只处理导航尺寸的像素,不做全分辨率 CPU 转换),白框与百分比由原生控件绘制。
/// 旧壳的导航图不可交互,这里同样不吃指针(<see cref="UIElement.IsHitTestVisible"/> 为 false)。
/// </summary>
internal sealed class WindowsNavigator : Grid
{
    private readonly Canvas _canvas;
    private readonly Image _image;
    private readonly Rectangle _frame;
    private readonly TextBlock _percent;
    private DecodedImage? _source;
    private WriteableBitmap? _bitmap;
    private int _pixelWidth;
    private int _pixelHeight;

    /// <summary>
    /// 创建导航图覆盖层。
    /// </summary>
    public WindowsNavigator()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(WindowsLayout.NavigatorMargin);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _frame = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = WindowsLayout.NavigatorFrameThickness,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _canvas = new Canvas();
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_frame);

        _percent = new TextBlock
        {
            FontSize = WindowsLayout.NavigatorPercentFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, WindowsLayout.NavigatorPercentTopMargin, 0, WindowsLayout.NavigatorPercentBottomMargin),
        };

        var stack = new StackPanel();
        stack.Children.Add(_canvas);
        stack.Children.Add(_percent);

        // Border 在 WinUI 3 里是 sealed(不能继承),外框用组合而不是派生。
        var frameBorder = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.NavigatorBackground),
            CornerRadius = new CornerRadius(WindowsLayout.NavigatorCornerRadius),
            Padding = new Thickness(WindowsLayout.NavigatorPadding),
            Child = stack,
        };
        Children.Add(frameBorder);
    }

    /// <summary>
    /// 当前显示的缩略图对应的会话帧;用于识别是否需要重建缩略图(换图或代理→Full 热替换)。
    /// </summary>
    public DecodedImage? Source => _source;

    /// <summary>导航图像素尺寸;未显示时为 <c>(0, 0)</c>,供定向验收断言。</summary>
    public (int Width, int Height) PixelSize => (_pixelWidth, _pixelHeight);

    /// <summary>可见框在导航图逻辑坐标内的位置与尺寸(逻辑单位);隐藏时为空。</summary>
    public Rect FrameBounds => new(Canvas.GetLeft(_frame), Canvas.GetTop(_frame), _frame.Width, _frame.Height);

    /// <summary>百分比文本(含 % 号)。</summary>
    public string PercentageText => _percent.Text;

    /// <summary>
    /// 读取当前导航图缩略图的 BGRA8 像素(定向验收用:核对 EXIF 方向在缩略图上的实际朝向)。
    /// </summary>
    /// <returns>紧排像素;无缩略图时为 null。</returns>
    internal byte[]? ReadThumbnailPixels()
    {
        if (_bitmap == null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            return null;
        }

        var bytes = new byte[_pixelWidth * _pixelHeight * 4];
        using var stream = _bitmap.PixelBuffer.AsStream();
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>缩略图像素宽(PBGRA 行宽按宽 × 4)。</summary>
    internal int ThumbnailPixelWidth => _pixelWidth;

    /// <summary>
    /// 更新导航图内容与几何:
    /// 会话帧变化时重建缩略图,其余情况只移动白框、刷新百分比(拖动平移时每帧调用)。
    /// </summary>
    /// <param name="image">当前主图会话帧(线性 RGBA16F);null 表示无图。</param>
    /// <param name="navigationSize">导航图逻辑尺寸(长边 150,按显示朝向比例)。</param>
    /// <param name="viewport">可见框在导航图逻辑坐标内的位置与尺寸。</param>
    /// <param name="ratio">当前物理缩放(1.0 = 100%)。</param>
    public void Update(DecodedImage? image, (double Width, double Height) navigationSize, Rect viewport, double ratio)
    {
        if (image == null || navigationSize.Width <= 0 || navigationSize.Height <= 0)
        {
            Reset();
            return;
        }

        _canvas.Width = navigationSize.Width;
        _canvas.Height = navigationSize.Height;
        _image.Width = navigationSize.Width;
        _image.Height = navigationSize.Height;

        if (!ReferenceEquals(_source, image) || _bitmap == null)
        {
            SetThumbnail(image, navigationSize);
        }

        _frame.Width = viewport.Width;
        _frame.Height = viewport.Height;
        Canvas.SetLeft(_frame, viewport.X);
        Canvas.SetTop(_frame, viewport.Y);
        _frame.Visibility = Visibility.Visible;
        _percent.Text = $"{ratio * 100:0.##}%";
    }

    /// <summary>清空并隐藏(无图、适应态或设置关闭时)。</summary>
    public void Reset()
    {
        _source = null;
        _bitmap = null;
        _image.Source = null;
        _percent.Text = string.Empty;
        _frame.Visibility = Visibility.Collapsed;
        _pixelWidth = 0;
        _pixelHeight = 0;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 把会话帧缩到导航图尺寸并转 sRGB8:线性 F16 → 目标 sRGB 位图,EXIF 方向在绘制矩阵里应用。
    /// 只对导航尺寸的像素做色彩转换,开销与照片分辨率无关。
    /// </summary>
    /// <param name="image">会话帧(线性 RGBA16F、未旋转)。</param>
    /// <param name="navigationSize">导航图逻辑尺寸。</param>
    private void SetThumbnail(DecodedImage image, (double Width, double Height) navigationSize)
    {
        double raster = XamlRoot?.RasterizationScale ?? 1;
        var pixelWidth = Math.Max(1, (int)Math.Round(navigationSize.Width * raster));
        var pixelHeight = Math.Max(1, (int)Math.Round(navigationSize.Height * raster));

        _source = image;
        if (!image.TryAcquire())
        {
            Reset();
            return;
        }

        try
        {
            using var source = CreateSkiaImage(image);
            using var target = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Opaque, SKColorSpace.CreateSrgb()));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.Black);
                var matrix = BuildOrientationMatrix(image, pixelWidth, pixelHeight);
                canvas.Concat(in matrix);
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(source,
                    SKRect.Create(0, 0, image.Width, image.Height),
                    SKRect.Create(0, 0, image.Width, image.Height),
                    sampling, new SKPaint { IsAntialias = true });
            }

            _bitmap = WindowsImageAdapter.ToSource(pixelWidth, pixelHeight, target.GetPixelSpan());
            _image.Source = _bitmap;
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            Visibility = Visibility.Visible;
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>把线性 F16 像素包成 Skia 图像(不复制像素,只在调用期间有效)。</summary>
    /// <param name="image">会话帧。</param>
    /// <returns>线性 sRGB 色彩的 Skia 图像。</returns>
    private static unsafe SKImage CreateSkiaImage(DecodedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.RgbaF16, SKAlphaType.Opaque, SKColorSpace.CreateSrgbLinear());
        fixed (byte* pixels = image.Pixels)
        {
            // FromPixels 不接管内存;像素所有权由 DecodedImage 的租约保证,绘制完成后立即释放。
            return SKImage.FromPixels(info, (IntPtr)pixels, image.Stride)
                ?? throw new InvalidOperationException("导航图源帧无法包装为 Skia 图像");
        }
    }

    /// <summary>
    /// EXIF 方向的绘制矩阵:把源(未旋转)像素坐标映射到显示朝向的目标像素坐标,
    /// 与主图顶点着色器的 UV 置换口径一致(2..8 依次为镜像/翻转/转置/90°…)。
    /// </summary>
    /// <param name="image">会话帧(携带源尺寸与方向)。</param>
    /// <param name="targetWidth">目标像素宽。</param>
    /// <param name="targetHeight">目标像素高。</param>
    private static SKMatrix BuildOrientationMatrix(DecodedImage image, int targetWidth, int targetHeight)
    {
        // 先按显示朝向铺满目标,再把源像素坐标映射进去:目标宽高已按方向交换。
        int sourceWidth = image.Width;
        int sourceHeight = image.Height;
        float scale = Math.Min(targetWidth / (float)(image.Orientation >= 5 ? sourceHeight : sourceWidth),
            targetHeight / (float)(image.Orientation >= 5 ? sourceWidth : sourceHeight));
        float drawnWidth = (image.Orientation >= 5 ? sourceHeight : sourceWidth) * scale;
        float drawnHeight = (image.Orientation >= 5 ? sourceWidth : sourceHeight) * scale;
        float offsetX = (targetWidth - drawnWidth) / 2;
        float offsetY = (targetHeight - drawnHeight) / 2;

        float width = drawnWidth;
        float height = drawnHeight;
        var orientation = image.Orientation switch
        {
            2 => new SKMatrix(-scale, 0, offsetX + width, 0, scale, offsetY, 0, 0, 1),
            3 => new SKMatrix(-scale, 0, offsetX + width, 0, -scale, offsetY + height, 0, 0, 1),
            4 => new SKMatrix(scale, 0, offsetX, 0, -scale, offsetY + height, 0, 0, 1),
            5 => new SKMatrix(0, scale, offsetX, scale, 0, offsetY, 0, 0, 1),
            6 => new SKMatrix(0, -scale, offsetX + width, scale, 0, offsetY, 0, 0, 1),
            7 => new SKMatrix(0, -scale, offsetX + width, -scale, 0, offsetY + height, 0, 0, 1),
            8 => new SKMatrix(0, scale, offsetX, -scale, 0, offsetY + height, 0, 0, 1),
            _ => new SKMatrix(scale, 0, offsetX, 0, scale, offsetY, 0, 0, 1),
        };
        return orientation;
    }
}
