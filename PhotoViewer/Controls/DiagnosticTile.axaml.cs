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
/// DINO 诊断工具页瓦片控件。外框是随父容器宽度变动的正方形;内部按 <see cref="AspectRatio"/> letterbox
/// 居中铺开图像内容(横图上下黑边、竖图左右黑边、不裁切不变形);标题文本放在正方形外的下方。
/// 内容层从下到上:dim 缩略图背景(<see cref="BackgroundSource"/>)→ 主图(<see cref="Source"/>)→
/// Overlay 槽位(<see cref="Overlay"/>,如 Canvas 矢量场)→ 持久十字准星。
/// hover 在内容区域上时鼠标变成反色十字光标;任何瓦片内点击都被吞掉,只有命中内容区域时才抛 <see cref="TileClicked"/>。
/// </summary>
public partial class DiagnosticTile : UserControl
{
    /// <summary>十字准星每臂长度(像素);主线段总长度为 2 × 此值。</summary>
    private const double CrosshairArmLength = 12.0;

    /// <summary>主源位图(锐度图 / PCA-RGB / cosine 等);可为 null,Overlay 控件单独渲染时使用。</summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<DiagnosticTile, Bitmap?>(nameof(Source));

    /// <summary>压暗的原图缩略图,作为主图背景层(锐度图 / 拖影矢量等需要"叠在原图上"的场景使用)。</summary>
    public static readonly StyledProperty<Bitmap?> BackgroundSourceProperty =
        AvaloniaProperty.Register<DiagnosticTile, Bitmap?>(nameof(BackgroundSource));

    /// <summary>主图层不透明度,用于让背景缩略图透出来;默认 1。</summary>
    public static readonly StyledProperty<double> ContentOpacityProperty =
        AvaloniaProperty.Register<DiagnosticTile, double>(nameof(ContentOpacity), 1.0);

    /// <summary>叠加内容(任意 Avalonia Control,如 ShakeFieldView)。会被同步铺满 ContentArea。</summary>
    public static readonly StyledProperty<Control?> OverlayProperty =
        AvaloniaProperty.Register<DiagnosticTile, Control?>(nameof(Overlay));

    /// <summary>标题文本。</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<DiagnosticTile, string?>(nameof(Label));

    /// <summary>源图长宽比(宽/高)。默认 1.0(方形)。</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<DiagnosticTile, double>(nameof(AspectRatio), 1.0);

    /// <summary>
    /// 十字准星归一化坐标([0,1]);null 表示不显示。
    /// 由父级在收到任意瓦片的 <see cref="TileClicked"/> 后统一下发给所有瓦片。
    /// </summary>
    public static readonly StyledProperty<Point?> CrosshairProperty =
        AvaloniaProperty.Register<DiagnosticTile, Point?>(nameof(Crosshair));

    /// <summary>主源位图。</summary>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>压暗的原图缩略图(背景层)。</summary>
    public Bitmap? BackgroundSource
    {
        get => GetValue(BackgroundSourceProperty);
        set => SetValue(BackgroundSourceProperty, value);
    }

    /// <summary>主图层不透明度。</summary>
    public double ContentOpacity
    {
        get => GetValue(ContentOpacityProperty);
        set => SetValue(ContentOpacityProperty, value);
    }

    /// <summary>叠加内容控件。</summary>
    public Control? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    /// <summary>标题文本。</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
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
    /// 用户点击内容区域时触发。事件已被标记 <c>Handled</c>,不会冒泡,父级可用"未处理"冒泡点击识别空闲区域。
    /// </summary>
    public event EventHandler<TileClickedEventArgs>? TileClicked;

    /// <summary>初始化 DINO 诊断瓦片。</summary>
    public DiagnosticTile()
    {
        InitializeComponent();
        ContentArea.Cursor = new Cursor(StandardCursorType.Cross);
        SquareFrame.AddHandler(PointerPressedEvent, OnFramePointerPressed, RoutingStrategies.Tunnel);
        // 监听外框宽度变化:正方形高度跟随宽度;再据此 + AspectRatio 计算 letterbox 后的内容区域尺寸。
        SquareFrame.GetObservable(BoundsProperty).Subscribe(_ => SyncSquareLayout());
        UpdateCrosshair();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AspectRatioProperty)
        {
            SyncSquareLayout();
        }
        else if (change.Property == CrosshairProperty)
        {
            UpdateCrosshair();
        }
    }

    /// <summary>
    /// 把 <see cref="SquareFrame"/> 锁成正方形(高 = 宽),再按 <see cref="AspectRatio"/> letterbox 出
    /// <see cref="ContentArea"/> 的尺寸:横图取满宽、竖图取满高,内部图像完全包含在正方形里、不裁切。
    /// </summary>
    private void SyncSquareLayout()
    {
        double side = SquareFrame.Bounds.Width;
        if (side <= 0 || double.IsNaN(side))
        {
            return;
        }

        SquareFrame.Height = side;

        double ratio = AspectRatio > 0 ? AspectRatio : 1.0;
        double cw, ch;
        if (ratio >= 1.0)
        {
            cw = side;
            ch = side / ratio;
        }
        else
        {
            ch = side;
            cw = side * ratio;
        }
        ContentArea.Width = cw;
        ContentArea.Height = ch;
        UpdateCrosshair();
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

        double w = ContentArea.Width;
        double h = ContentArea.Height;
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
    /// 外框点击处理:无论击中内容区域还是 letterbox 黑边,都标记 Handled(瓦片吞掉自身所有点击);
    /// 仅当点击落在内容区域内部时,才翻译成归一化坐标并触发 <see cref="TileClicked"/>。
    /// </summary>
    private void OnFramePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        var area = ContentArea;
        double w = area.Bounds.Width;
        double h = area.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var pt = e.GetPosition(area);
        if (pt.X < 0 || pt.Y < 0 || pt.X > w || pt.Y > h) return;

        double u = Math.Clamp(pt.X / w, 0, 1);
        double v = Math.Clamp(pt.Y / h, 0, 1);
        TileClicked?.Invoke(this, new TileClickedEventArgs(u, v));
    }
}
