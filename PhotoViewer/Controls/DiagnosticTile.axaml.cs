using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace PhotoViewer.Controls;

/// <summary>
/// 描述 <see cref="DiagnosticTile.TileClicked"/> 的点击位置(归一化到 [0,1] 的图像坐标)。
/// </summary>
public sealed class TileClickedEventArgs : EventArgs
{
    /// <summary>归一化横坐标(0 左,1 右)。</summary>
    public double U { get; }

    /// <summary>归一化纵坐标(0 上,1 下)。</summary>
    public double V { get; }

    /// <summary>构造事件参数。</summary>
    public TileClickedEventArgs(double u, double v)
    {
        U = u;
        V = v;
    }
}

/// <summary>
/// DINO 诊断工具页瓦片控件。统一外框 + 图像区域(保持源图长宽比)+ 持久十字准星叠加 + 点击归一化坐标回调。
/// 调用方负责把 <see cref="AspectRatio"/> 设成源图 宽/高;<see cref="Crosshair"/> 传 <see cref="Point"/>(归一化坐标)显示,传 null 隐藏。
/// hover 在图像区域上时,鼠标自动变成反色十字光标。
/// </summary>
public partial class DiagnosticTile : UserControl
{
    /// <summary>十字准星每臂长度(像素);主线段总长度为 2 × 此值。</summary>
    private const double CrosshairArmLength = 12.0;

    /// <summary>源位图。</summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<DiagnosticTile, Bitmap?>(nameof(Source));

    /// <summary>标题文本。</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<DiagnosticTile, string?>(nameof(Label));

    /// <summary>图像区域宽度(像素);高度由 <see cref="AspectRatio"/> 推出。默认 220。</summary>
    public static readonly StyledProperty<double> ImageWidthProperty =
        AvaloniaProperty.Register<DiagnosticTile, double>(nameof(ImageWidth), 220.0);

    /// <summary>源图长宽比(宽/高)。默认 1.0(方形)。</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<DiagnosticTile, double>(nameof(AspectRatio), 1.0);

    /// <summary>
    /// 十字准星归一化坐标([0,1]);null 表示不显示。
    /// 由父级在收到任意瓦片的 <see cref="TileClicked"/> 后统一下发给所有瓦片。
    /// </summary>
    public static readonly StyledProperty<Point?> CrosshairProperty =
        AvaloniaProperty.Register<DiagnosticTile, Point?>(nameof(Crosshair));

    /// <summary>源位图。</summary>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>标题文本。</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>图像区域宽度(像素)。</summary>
    public double ImageWidth
    {
        get => GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    /// <summary>源图长宽比(宽/高)。</summary>
    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>十字准星归一化坐标(null 隐藏)。</summary>
    public Point? Crosshair
    {
        get => GetValue(CrosshairProperty);
        set => SetValue(CrosshairProperty, value);
    }

    /// <summary>
    /// 用户点击图像区域时触发。事件已被标记 <c>Handled</c>,不会冒泡,父级可用"未处理"冒泡点击识别空闲区域。
    /// </summary>
    public event EventHandler<TileClickedEventArgs>? TileClicked;

    /// <summary>初始化 DINO 诊断瓦片。</summary>
    public DiagnosticTile()
    {
        InitializeComponent();
        ImageHost.Cursor = new Cursor(StandardCursorType.Cross);
        UpdateImageSize();
        UpdateCrosshair();
        OuterBorder.AddHandler(PointerPressedEvent, OnOuterPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageWidthProperty || change.Property == AspectRatioProperty)
        {
            UpdateImageSize();
            UpdateCrosshair();
        }
        else if (change.Property == CrosshairProperty)
        {
            UpdateCrosshair();
        }
    }

    /// <summary>根据 <see cref="ImageWidth"/> 与 <see cref="AspectRatio"/> 同步 ImageHost 的物理尺寸。</summary>
    private void UpdateImageSize()
    {
        double w = Math.Max(1, ImageWidth);
        double ratio = AspectRatio > 0 ? AspectRatio : 1.0;
        double h = Math.Max(1, w / ratio);
        ImageHost.Width = w;
        ImageHost.Height = h;
    }

    /// <summary>
    /// 把 <see cref="Crosshair"/> 的归一化坐标翻译成 Canvas 上两条 Rectangle 的位置。
    /// 主线条两端各留 <see cref="CrosshairArmLength"/> 像素,不拉到图像边缘,避免信息噪声。
    /// </summary>
    private void UpdateCrosshair()
    {
        var pt = Crosshair;
        if (pt == null)
        {
            CrosshairCanvas.IsVisible = false;
            return;
        }

        double w = ImageHost.Width;
        double h = ImageHost.Height;
        if (w <= 0 || h <= 0 || double.IsNaN(w) || double.IsNaN(h))
        {
            CrosshairCanvas.IsVisible = false;
            return;
        }

        CrosshairCanvas.IsVisible = true;
        CrosshairCanvas.Width = w;
        CrosshairCanvas.Height = h;

        double cx = Math.Round(Math.Clamp(pt.Value.X, 0, 1) * w);
        double cy = Math.Round(Math.Clamp(pt.Value.Y, 0, 1) * h);

        double armV = Math.Min(CrosshairArmLength, h / 2);
        double armH = Math.Min(CrosshairArmLength, w / 2);

        double vTop = Math.Max(0, cy - armV);
        double vLen = Math.Min(h - vTop, armV * 2);
        double hLeft = Math.Max(0, cx - armH);
        double hLen = Math.Min(w - hLeft, armH * 2);

        VMain.Height = vLen;
        HMain.Width = hLen;

        Canvas.SetLeft(VMain, cx);
        Canvas.SetTop(VMain, vTop);
        Canvas.SetLeft(HMain, hLeft);
        Canvas.SetTop(HMain, cy);
    }

    /// <summary>
    /// 外框点击处理:无论击中内部图像还是外围 padding,都标记 Handled(瓦片吞掉自身所有点击);
    /// 仅当点击落在图像区域内部时,才翻译成归一化坐标并触发 <see cref="TileClicked"/>。
    /// </summary>
    private void OnOuterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        var host = ImageHost;
        double w = host.Bounds.Width;
        double h = host.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var pt = e.GetPosition(host);
        if (pt.X < 0 || pt.Y < 0 || pt.X > w || pt.Y > h) return;

        double u = Math.Clamp(pt.X / w, 0, 1);
        double v = Math.Clamp(pt.Y / h, 0, 1);
        TileClicked?.Invoke(this, new TileClickedEventArgs(u, v));
    }
}
