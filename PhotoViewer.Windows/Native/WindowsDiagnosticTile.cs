using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using PhotoViewer.Core.AI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生诊断瓦片:正方形外框 + 按照片纵横比 letterbox 的内容区 + 可选底部说明。
///
/// 分析栏与 DINO 诊断工具页共用(与 macOS 的 <c>MacDiagnosticTile</c> 同一职责):
/// 内容层自下而上为压暗背景图 / 主图 / 叠加层(抖动矢量场) / 准星,再叠左上角药丸标签。
/// 瓦片吞掉自身范围内的点击,只有命中内容矩形时才把归一化坐标交给 <see cref="TileClicked"/>;
/// letterbox 留边与说明文字区的点击交给 <see cref="BackgroundClicked"/>。
/// 收到的位图由调用方持有,本类只引用、从不释放。
/// </summary>
internal sealed class WindowsDiagnosticTile : Panel
{
    /// <summary>内层内容裁剪圆角(与外框 1px 描边配合,避免内容压住圆角)。</summary>
    private const double InnerCornerRadius = 5;

    /// <summary>说明文字上间距。</summary>
    private const double CaptionTopMargin = 6;

    /// <summary>说明文字左右留白。</summary>
    private const double CaptionSideMargin = 4;

    /// <summary>说明文字下间距。</summary>
    private const double CaptionBottomMargin = 4;

    /// <summary>背景缩略图压暗后的不透明度。</summary>
    private const double BackgroundAlpha = 0.30;

    private readonly Border _frame;
    private readonly Grid _square;
    private readonly ContentHost _contentHost;
    private readonly Grid _content;
    private readonly Image _backgroundImage;
    private readonly Image _image;
    private readonly Canvas _crosshair;
    private readonly Line _crosshairHorizontal;
    private readonly Line _crosshairVertical;
    private readonly Border _cornerHost;
    private readonly TextBlock _cornerText;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _caption;

    private FrameworkElement? _overlay;
    private double? _crosshairU;
    private double? _crosshairV;

    /// <summary>创建空瓦片;内容与标签随后由 presenter 状态填充。</summary>
    public WindowsDiagnosticTile()
    {
        _backgroundImage = new Image
        {
            Stretch = Stretch.Fill,
            Opacity = BackgroundAlpha,
            IsHitTestVisible = false,
        };
        _image = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        _crosshairHorizontal = new Line
        {
            Stroke = new SolidColorBrush(WindowsLayout.AnalysisCrosshairStroke),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
        };
        _crosshairVertical = new Line
        {
            Stroke = new SolidColorBrush(WindowsLayout.AnalysisCrosshairStroke),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
        };
        _crosshair = new Canvas { IsHitTestVisible = false };
        _crosshair.Children.Add(_crosshairHorizontal);
        _crosshair.Children.Add(_crosshairVertical);

        _content = new Grid { IsHitTestVisible = false };
        _content.Children.Add(_backgroundImage);
        _content.Children.Add(_image);
        _content.Children.Add(_crosshair);

        _contentHost = new ContentHost();
        _contentHost.Children.Add(_content);

        _cornerText = new TextBlock
        {
            FontSize = WindowsLayout.AnalysisLabelFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.AnalysisLabelForeground),
        };
        _cornerHost = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.AnalysisLabelBackground),
            CornerRadius = new CornerRadius(5, 0, 5, 0),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _cornerText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _placeholder = new TextBlock
        {
            FontSize = WindowsLayout.AnalysisPlaceholderFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.AnalysisPlaceholderForeground),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _square = new Grid();
        _square.Children.Add(_contentHost);
        _square.Children.Add(_placeholder);
        _square.Children.Add(_cornerHost);

        _frame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.AnalysisTileBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.AnalysisTileBorder),
            BorderThickness = new Thickness(WindowsLayout.AnalysisTileBorderThickness),
            CornerRadius = new CornerRadius(WindowsLayout.AnalysisTileCornerRadius),
            Child = _square,
        };

        _caption = new TextBlock
        {
            FontSize = WindowsLayout.AnalysisCaptionFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.AnalysisCaptionForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(
                CaptionSideMargin,
                CaptionTopMargin,
                CaptionSideMargin,
                CaptionBottomMargin),
            Visibility = Visibility.Collapsed,
        };

        Children.Add(_frame);
        Children.Add(_caption);

        _content.SizeChanged += (_, _) => RenderCrosshair();
        PointerPressed += OnPressed;
    }

    /// <summary>内容区点击回调,参数为归一化图像坐标(0 左/上,1 右/下)。</summary>
    public Action<double, double>? TileClicked { get; set; }

    /// <summary>内容区之外的空白点击回调(如清空准星)。</summary>
    public Action? BackgroundClicked { get; set; }

    /// <summary>是否已有主图位图。</summary>
    public bool HasBitmap => _image.Source != null;

    /// <summary>是否已有可显示内容(主图或带数据的叠加层)。</summary>
    public bool HasContent => HasBitmap || _overlay is WindowsShakeFieldView { HasField: true };

    /// <summary>当前正方形边长(逻辑单位);布局完成前为 0。</summary>
    public double Side => _frame.ActualWidth;

    /// <summary>底部说明文字(验收用)。</summary>
    public string CaptionText => _caption.Text;

    /// <summary>占位文字是否可见(验收用)。</summary>
    public bool PlaceholderVisible => _placeholder.Visibility == Visibility.Visible;

    /// <summary>左上角药丸标签文本(验收用)。</summary>
    public string LabelText => _cornerText.Text;

    /// <summary>设置主图位图与占位文字。</summary>
    /// <param name="source">位图;null 清空</param>
    /// <param name="placeholder">主图为空时要显示的占位文字</param>
    public void SetBitmap(WriteableBitmap? source, string? placeholder)
    {
        _image.Source = source;
        SetPlaceholder(placeholder);
    }

    /// <summary>只更新占位文字(位图引用未变时用,避免重复上传)。</summary>
    /// <param name="placeholder">占位文字;null 隐藏</param>
    public void SetPlaceholder(string? placeholder)
    {
        bool visible = _image.Source == null && !string.IsNullOrWhiteSpace(placeholder);
        _placeholder.Text = visible ? placeholder : string.Empty;
        _placeholder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>设置压暗的背景缩略图。</summary>
    /// <param name="background">背景位图;null 清空</param>
    public void SetBackground(WriteableBitmap? background) => _backgroundImage.Source = background;

    /// <summary>设置内容区叠加层(如抖动矢量场),替换时摘除旧元素。</summary>
    /// <param name="overlay">叠加元素;null 清空</param>
    public void SetOverlay(FrameworkElement? overlay)
    {
        if (ReferenceEquals(_overlay, overlay))
        {
            return;
        }

        if (_overlay != null)
        {
            _content.Children.Remove(_overlay);
        }

        _overlay = overlay;
        if (_overlay != null)
        {
            _overlay.IsHitTestVisible = false;
            _content.Children.Insert(_content.Children.IndexOf(_crosshair), _overlay);
        }
    }

    /// <summary>设置内容纵横比(宽/高),用于内容矩形与准星换算。</summary>
    /// <param name="aspect">照片纵横比</param>
    public void SetAspect(double aspect)
    {
        _contentHost.AspectRatio = aspect > 0 ? aspect : 1;
        RenderCrosshair();
    }

    /// <summary>设置左上角药丸标签;null/空白时隐藏整个药丸。</summary>
    /// <param name="label">短标签</param>
    public void SetLabel(string? label)
    {
        bool visible = !string.IsNullOrWhiteSpace(label);
        _cornerHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _cornerText.Text = visible ? label : string.Empty;
    }

    /// <summary>设置正方形下方的说明文字;null/空白时不占高度。</summary>
    /// <param name="caption">说明文字</param>
    public void SetCaption(string? caption)
    {
        bool visible = !string.IsNullOrWhiteSpace(caption);
        _caption.Text = visible ? caption : string.Empty;
        _caption.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
    }

    /// <summary>设置全局准星位置(归一化;null 隐藏)。</summary>
    /// <param name="u">横向 0-1</param>
    /// <param name="v">纵向 0-1</param>
    public void SetCrosshair(double? u, double? v)
    {
        _crosshairU = u;
        _crosshairV = v;
        RenderCrosshair();
    }

    /// <summary>释放位图引用(位图本身由 presenter 所有)。</summary>
    public void Dispose()
    {
        _image.Source = null;
        _backgroundImage.Source = null;
        _crosshair.Children.Clear();
        Children.Clear();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        if (!IsFinite(width) || width <= 0)
        {
            width = IsFinite(availableSize.Height) && availableSize.Height > 0 ? availableSize.Height : 0;
        }

        if (width <= 0)
        {
            return new Size(0, 0);
        }

        _frame.Measure(new Size(width, width));
        double captionHeight = 0;
        if (_caption.Visibility == Visibility.Visible)
        {
            _caption.Measure(new Size(width, double.PositiveInfinity));
            captionHeight = _caption.DesiredSize.Height;
        }

        return new Size(width, width + captionHeight);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = finalSize.Width;
        _frame.Arrange(new Rect(0, 0, width, width));
        if (_caption.Visibility == Visibility.Visible)
        {
            _caption.Arrange(new Rect(0, width, width, _caption.DesiredSize.Height));
            return new Size(width, width + _caption.DesiredSize.Height);
        }

        return new Size(width, width);
    }

    /// <summary>按 uv 绘制准星两臂(坐标换算到内容矩形)。</summary>
    private void RenderCrosshair()
    {
        double width = _content.ActualWidth;
        double height = _content.ActualHeight;
        if (_crosshairU is not { } u || _crosshairV is not { } v || width <= 0 || height <= 0)
        {
            _crosshairHorizontal.Visibility = Visibility.Collapsed;
            _crosshairVertical.Visibility = Visibility.Collapsed;
            return;
        }

        double centerX = Math.Round(Math.Clamp(u, 0, 1) * width);
        double centerY = Math.Round(Math.Clamp(v, 0, 1) * height);
        double arm = WindowsLayout.AnalysisCrosshairArm;
        _crosshairHorizontal.X1 = Math.Max(0, centerX - arm);
        _crosshairHorizontal.X2 = Math.Min(width, centerX + arm);
        _crosshairHorizontal.Y1 = centerY;
        _crosshairHorizontal.Y2 = centerY;
        _crosshairVertical.X1 = centerX;
        _crosshairVertical.X2 = centerX;
        _crosshairVertical.Y1 = Math.Max(0, centerY - arm);
        _crosshairVertical.Y2 = Math.Min(height, centerY + arm);
        _crosshairHorizontal.Visibility = Visibility.Visible;
        _crosshairVertical.Visibility = Visibility.Visible;
    }

    /// <summary>瓦片一律吞掉点击:内容矩形内换算 uv,其余区域交给清空回调。</summary>
    /// <param name="sender">瓦片</param>
    /// <param name="args">指针参数</param>
    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        var point = args.GetCurrentPoint(_contentHost).Position;
        var rect = _contentHost.ContentRect;
        bool inside = rect.Width > 0 && rect.Height > 0
            && point.X >= rect.X && point.X <= rect.X + rect.Width
            && point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
        if (inside && TileClicked != null)
        {
            double u = Math.Clamp((point.X - rect.X) / rect.Width, 0, 1);
            double v = Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1);
            TileClicked(u, v);
            return;
        }

        BackgroundClicked?.Invoke();
    }

    /// <summary>判断尺寸是否有限且非 NaN。</summary>
    /// <param name="value">待判断值</param>
    /// <returns>有限时为 true</returns>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// 内容区容器:按照片纵横比在正方形内居中排布单个内容元素(横图取满宽、竖图取满高),
    /// 并在 Measure/Arrange 里直接给出子元素矩形,不在布局回调里回写子元素尺寸。
    /// </summary>
    private sealed class ContentHost : Panel
    {
        private double _aspectRatio = 1;

        /// <summary>内容纵横比(宽/高);变化时重新测量以更新 letterbox 矩形。</summary>
        public double AspectRatio
        {
            get => _aspectRatio;
            set
            {
                if (Math.Abs(_aspectRatio - value) < 1e-9)
                {
                    return;
                }

                _aspectRatio = value;
                InvalidateMeasure();
            }
        }

        /// <summary>最近一次布局算出的内容矩形(本容器坐标)。</summary>
        public Rect ContentRect { get; private set; }

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 0;
            double height = IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : width;
            if (width <= 0 || height <= 0)
            {
                ContentRect = default;
                return new Size(width, height);
            }

            var rect = Compute(width, height);
            ContentRect = rect;
            foreach (var child in Children)
            {
                child.Measure(new Size(rect.Width, rect.Height));
            }

            return new Size(width, height);
        }

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = Math.Max(0, finalSize.Width);
            double height = Math.Max(0, finalSize.Height);
            if (width <= 0 || height <= 0)
            {
                ContentRect = default;
                return finalSize;
            }

            var rect = Compute(width, height);
            ContentRect = rect;
            foreach (var child in Children)
            {
                child.Arrange(rect);
            }

            return finalSize;
        }

        /// <summary>按纵横比求居中的内容矩形。</summary>
        /// <param name="width">可用宽</param>
        /// <param name="height">可用高</param>
        /// <returns>内容矩形</returns>
        private Rect Compute(double width, double height)
        {
            double ratio = AspectRatio > 0 ? AspectRatio : 1;
            double contentWidth;
            double contentHeight;
            if (ratio >= 1)
            {
                contentWidth = width;
                contentHeight = width / ratio;
            }
            else
            {
                contentHeight = height;
                contentWidth = height * ratio;
            }

            return new Rect((width - contentWidth) / 2, (height - contentHeight) / 2, contentWidth, contentHeight);
        }
    }
}
