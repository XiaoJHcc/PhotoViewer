using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生诊断瓦片（UIKit 版，分析栏与 DINO 诊断页共用）：正方形外框跟随宽度定高，内容按
/// <see cref="AspectRatio"/> letterbox 居中铺开（横图取满宽、竖图取满高，不裁切不变形），
/// 说明文字排在正方形下方、仍属瓦片自身范围。
/// 绘制层自下而上：外框与内层圆角裁剪 → 压暗背景图（<see cref="BackgroundSource"/>，alpha 0.30，平滑插值）
/// → 主图（<see cref="Source"/>，图层放大/缩小过滤器均为最近邻，32×32 热力图保持像素级）
/// → 叠加视图 <see cref="Overlay"/> → 装饰层（占位文本、十字准星、左上角药丸标签）。
/// 与 Mac 原生 <c>MacDiagnosticTile</c> 同一视觉口径（#222/#444 1 点/r6、内层 r5、药丸 #88000000 圆角 5,0,5,0）。
/// 收到的 <see cref="UIImage"/> 由调用方持有，本类只引用、从不释放。
/// </summary>
internal sealed class iOSDiagnosticTile : UIView
{
    /// <summary>外框圆角半径。</summary>
    private const double OuterCornerRadius = 6;
    /// <summary>内层内容裁剪圆角半径。</summary>
    private const double InnerCornerRadius = 5;
    /// <summary>外框描边宽度。</summary>
    private const double BorderWidth = 1;
    /// <summary>十字准星单臂长度上限（点）；主线总长为 2 × 此值。</summary>
    private const double CrosshairArmLength = 12;
    /// <summary>瓦片内全部文字的字号。</summary>
    private const double TextFontSize = 12;
    private const double CaptionMarginLeft = 4;
    private const double CaptionMarginTop = 6;
    private const double CaptionMarginRight = 4;
    private const double CaptionMarginBottom = 4;
    /// <summary>角标药丸左右内边距。</summary>
    private const double CornerPillPaddingX = 8;
    /// <summary>角标药丸上下内边距。</summary>
    private const double CornerPillPaddingY = 4;
    /// <summary>角标药丸圆角半径（只圆外侧两角）。</summary>
    private const double CornerPillRadius = 5;
    /// <summary>背景缩略图压暗后的不透明度。</summary>
    private const double BackgroundAlpha = 0.30;
    /// <summary>占位文本左右各留的空白，避免长文本顶到边框。</summary>
    private const double PlaceholderInset = 8;
    /// <summary>换行文字测量的高度上限。</summary>
    private const double MeasureLimit = 10000;

    /// <summary>外框填色 #222（同时是 letterbox 底色）。</summary>
    private static readonly UIColor FrameFill = UIColor.FromRGB(0x22, 0x22, 0x22);
    /// <summary>外框描边 #444。</summary>
    private static readonly UIColor FrameStroke = UIColor.FromRGB(0x44, 0x44, 0x44);
    /// <summary>占位文本颜色 #777。</summary>
    private static readonly UIColor PlaceholderColor = UIColor.FromRGB(0x77, 0x77, 0x77);
    /// <summary>说明文字颜色 #888。</summary>
    private static readonly UIColor CaptionColor = UIColor.FromRGB(0x88, 0x88, 0x88);
    /// <summary>角标药丸底色 #88000000。</summary>
    private static readonly UIColor CornerPillFill = UIColor.FromRGBA(0x00, 0x00, 0x00, 0x88);
    /// <summary>角标药丸文字颜色 #aaffffff。</summary>
    private static readonly UIColor CornerPillTextColor = UIColor.FromRGBA(0xff, 0xff, 0xff, 0xaa);
    /// <summary>十字准星颜色（纯白 1 点线）。</summary>
    private static readonly UIColor CrosshairColor = UIColor.White;
    /// <summary>瓦片内所有文字共用的系统字体。</summary>
    private static readonly UIFont TextFont = UIFont.SystemFontOfSize((nfloat)TextFontSize);

    private readonly UIView _clip;
    private readonly UIImageView _background;
    private readonly UIImageView _source;
    private readonly UILabel _placeholder;
    private readonly ChromeView _chrome;
    private readonly UILabel _cornerText;
    private readonly UILabel _caption;
    private readonly UITapGestureRecognizer _tap;
    private UIImage? _sourceImage;
    private UIImage? _backgroundSource;
    private UIView? _overlay;
    private double _aspectRatio = 1.0;
    private string? _cornerLabel;
    private string? _label;
    private string? _placeholderText;
    private double? _crosshairU;
    private double? _crosshairV;
    /// <summary>letterbox 后的内容矩形（瓦片坐标，已扣除 1 点边框）。</summary>
    private CGRect _contentRect = CGRect.Empty;

    /// <summary>内容矩形内点击回调，参数为归一化图像坐标（0 左/上，1 右/下）。</summary>
    internal Action<double, double>? TileClicked { get; set; }

    /// <summary>建立外框、内层裁剪、装饰层与底部说明文字；几何在尺寸变化时由 <see cref="SyncLayout"/> 推出。</summary>
    public iOSDiagnosticTile()
    {
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        // 自绘瓦片：尺寸变化时必须重绘（默认 ScaleToFill 会把首次 1×1 的内容拉伸，边框/药丸/准星全部丢失）。
        ContentMode = UIViewContentMode.Redraw;

        _clip = new UIView { ClipsToBounds = true, BackgroundColor = FrameFill };
        _clip.Layer.CornerRadius = (nfloat)InnerCornerRadius;
        _background = ImageView();
        _source = ImageView();
        _background.Alpha = (nfloat)BackgroundAlpha;
        // 热力图是 32×32 放大的像素艺术：图层放大/缩小过滤器都取最近邻（等价 Mac 的 ImageInterpolation.None）。
        _source.Layer.MagnificationFilter = "nearest";
        _source.Layer.MinificationFilter = "nearest";
        _placeholder = new UILabel
        {
            Font = TextFont,
            TextColor = PlaceholderColor,
            TextAlignment = UITextAlignment.Center,
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };
        _clip.AddSubview(_background);
        _clip.AddSubview(_source);
        _clip.AddSubview(_placeholder);

        // 装饰层只画准星与药丸，必须透明且不参与不透明层合成（否则会以黑色盖住图像）。
        _chrome = new ChromeView(this)
        {
            ContentMode = UIViewContentMode.Redraw,
            Opaque = false,
            BackgroundColor = UIColor.Clear,
        };
        _cornerText = new UILabel
        {
            Font = TextFont,
            TextColor = CornerPillTextColor,
            TextAlignment = UITextAlignment.Center,
            Lines = 1,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };
        _chrome.AddSubview(_cornerText);
        _caption = new UILabel
        {
            Font = TextFont,
            TextColor = CaptionColor,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            LineBreakMode = UILineBreakMode.WordWrap,
            BackgroundColor = UIColor.Clear,
        };
        AddSubview(_clip);
        AddSubview(_chrome);
        AddSubview(_caption);

        // 点击只在本瓦片内生效；与细节预览的 hover/点按识别器允许同时识别，互不抢占。
        _tap = new UITapGestureRecognizer(OnTapped) { CancelsTouchesInView = true };
        _tap.Delegate = new SimultaneousDelegate();
        AddGestureRecognizer(_tap);
    }

    /// <summary>主源位图（锐度图 / PCA-RGB / cosine 等）；为 null 时显示占位文本或空底。</summary>
    internal UIImage? Source
    {
        get => _sourceImage;
        set
        {
            if (ReferenceEquals(_sourceImage, value)) return;
            _sourceImage = value;
            _source.Image = value;
            _source.Hidden = value == null;
            SyncPlaceholder();
        }
    }

    /// <summary>压暗的原图缩略图，作为主图背景层（alpha 0.30，平滑插值）。</summary>
    internal UIImage? BackgroundSource
    {
        get => _backgroundSource;
        set
        {
            if (ReferenceEquals(_backgroundSource, value)) return;
            _backgroundSource = value;
            _background.Image = value;
            _background.Hidden = value == null;
        }
    }

    /// <summary>内容图宽高比（宽/高）；&lt;= 0 视为 1（正方形）。变化时重算 letterbox。</summary>
    internal double AspectRatio
    {
        get => _aspectRatio;
        set
        {
            if (value == _aspectRatio) return;
            _aspectRatio = value;
            SyncLayout();
        }
    }

    /// <summary>左上角短标签（识别瓦片身份）；null/空白时隐藏整个药丸。</summary>
    internal string? CornerLabel
    {
        get => _cornerLabel;
        set
        {
            if (string.Equals(_cornerLabel, value, StringComparison.Ordinal)) return;
            _cornerLabel = value;
            SyncCornerPill();
        }
    }

    /// <summary>底部说明文字；null/空白时不显示，瓦片高度退回正方形。</summary>
    internal string? Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
            SyncLayout();
        }
    }

    /// <summary>占位文本；仅当 <see cref="Source"/> 为 null 且本属性非空白时在正方形内居中显示。</summary>
    internal string? PlaceholderText
    {
        get => _placeholderText;
        set
        {
            if (string.Equals(_placeholderText, value, StringComparison.Ordinal)) return;
            _placeholderText = value;
            SyncPlaceholder();
        }
    }

    /// <summary>内容区叠加视图（如矢量场），随内容矩形同步尺寸；替换时把旧视图从父视图摘除。</summary>
    internal UIView? Overlay
    {
        get => _overlay;
        set
        {
            if (ReferenceEquals(_overlay, value)) return;
            _overlay?.RemoveFromSuperview();
            _overlay = value;
            if (_overlay != null)
            {
                // 插到装饰层之下：叠加视图须在图像之上、准星与药丸之下。
                InsertSubviewBelow(_overlay, _chrome);
                _overlay.Frame = _contentRect;
                _overlay.SetNeedsDisplay();
            }
        }
    }

    /// <summary>当前是否已贴上主图（细节预览按代理期留空的口径读取）。</summary>
    internal bool HasContent => _sourceImage != null;

    /// <summary>主图图层是否使用最近邻放大/缩小（32×32 热力图保持像素级，验收核对）。</summary>
    internal bool UsesNearestNeighbour
        => string.Equals(_source.Layer.MagnificationFilter, "nearest", StringComparison.Ordinal)
        && string.Equals(_source.Layer.MinificationFilter, "nearest", StringComparison.Ordinal);

    /// <summary>背景层是否按旧壳口径压暗到 alpha 0.30（验收核对）。</summary>
    internal bool BackgroundDimmed => Math.Abs((double)_background.Alpha - BackgroundAlpha) < .001;

    /// <summary>设置归一化准星位置；任一坐标为 null 即隐藏，取值不变时不重绘。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    internal void SetCrosshair(double? u, double? v)
    {
        if (Nullable.Equals(_crosshairU, u) && Nullable.Equals(_crosshairV, v)) return;
        _crosshairU = u;
        _crosshairV = v;
        _chrome.SetNeedsDisplay();
    }

    /// <summary>
    /// 给定外宽时瓦片所需总高：没有说明文字时就是正方形高度，有说明文字时再加上下边距与换行后的文字高度。
    /// </summary>
    /// <param name="width">瓦片外宽。</param>
    /// <returns>瓦片总高。</returns>
    internal double RequiredHeight(double width)
    {
        if (string.IsNullOrWhiteSpace(_label)) return width;
        return width + CaptionMarginTop + MeasureCaption(width - CaptionMarginLeft - CaptionMarginRight) + CaptionMarginBottom;
    }

    /// <summary>尺寸变化时重算正方形、letterbox 内容矩形与所有文字位置。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        SyncLayout();
    }

    /// <summary>绘制外框填色与 1 点描边；描边内缩半个线宽，保证完整可见。</summary>
    /// <param name="rect">脏矩形。</param>
    public override void Draw(CGRect rect)
    {
        double side = (double)Bounds.Width;
        if (side <= 0) return;
        var square = new CGRect(0, 0, side, side);
        using (var frame = UIBezierPath.FromRoundedRect(square, (nfloat)OuterCornerRadius))
        {
            FrameFill.SetFill();
            frame.Fill();
        }
        var borderRect = Inset(square, BorderWidth / 2);
        using var border = UIBezierPath.FromRoundedRect(borderRect, (nfloat)(OuterCornerRadius - BorderWidth / 2));
        border.LineWidth = (nfloat)BorderWidth;
        FrameStroke.SetStroke();
        border.Stroke();
    }

    /// <summary>命中测试只在正方形（不含说明文字以外的透明区）内吞掉触摸，具体判定交给点击识别器。</summary>
    /// <param name="point">触点（父视图坐标）。</param>
    /// <param name="evt">触摸事件。</param>
    /// <returns>命中视图。</returns>
    public override UIView? HitTest(CGPoint point, UIEvent? evt)
    {
        var local = ConvertPointFromView(point, Superview);
        return Bounds.Contains(local) ? base.HitTest(point, evt) : null;
    }

    /// <summary>把正方形、内容矩形、装饰子视图、叠加视图与说明文字按当前尺寸和数据同步。</summary>
    private void SyncLayout()
    {
        double side = Math.Max(0, (double)Bounds.Width);
        _contentRect = ContentRect(side);
        _clip.Frame = Inset(new CGRect(0, 0, side, side), BorderWidth);
        _clip.Layer.CornerRadius = (nfloat)InnerCornerRadius;
        _background.Frame = OffsetContent();
        _source.Frame = OffsetContent();
        _chrome.Frame = new CGRect(0, 0, side, side);
        if (_overlay != null) _overlay.Frame = _contentRect;
        SyncPlaceholder();
        SyncCornerPill();
        SyncCaption(side);
        SetNeedsDisplay();
        _chrome.SetNeedsDisplay();
    }

    /// <summary>内容矩形相对内层裁剪容器的坐标（裁剪容器已内缩 1 点）。</summary>
    /// <returns>图像视图 frame。</returns>
    private CGRect OffsetContent()
        => new(_contentRect.X - BorderWidth, _contentRect.Y - BorderWidth, _contentRect.Width, _contentRect.Height);

    /// <summary>按宽高比算出内缩外框后的居中内容矩形：横图取满宽、竖图取满高，不裁切不变形。</summary>
    /// <param name="side">正方形边长。</param>
    /// <returns>内容矩形。</returns>
    private CGRect ContentRect(double side)
    {
        double inner = Math.Max(0, side - BorderWidth * 2);
        double ratio = _aspectRatio > 0 ? _aspectRatio : 1.0;
        double contentWidth, contentHeight;
        if (ratio >= 1.0)
        {
            contentWidth = inner;
            contentHeight = inner / ratio;
        }
        else
        {
            contentHeight = inner;
            contentWidth = inner * ratio;
        }
        return new CGRect(BorderWidth + (inner - contentWidth) / 2, BorderWidth + (inner - contentHeight) / 2, contentWidth, contentHeight);
    }

    /// <summary>占位文本在正方形内居中（不限于内容矩形）；<see cref="Source"/> 一旦赋值即隐藏。</summary>
    private void SyncPlaceholder()
    {
        string? text = _placeholderText;
        if (_sourceImage != null || string.IsNullOrWhiteSpace(text))
        {
            _placeholder.Hidden = true;
            return;
        }
        _placeholder.Hidden = false;
        _placeholder.Text = text;
        _placeholder.SizeToFit();
        double side = Math.Max(0, (double)Bounds.Width);
        double width = Math.Min((double)_placeholder.Frame.Width, Math.Max(1, side - PlaceholderInset * 2));
        double height = (double)_placeholder.Frame.Height;
        _placeholder.Frame = new CGRect((side - width) / 2, (side - height) / 2, width, height);
    }

    /// <summary>左上角药丸贴正方形左上角、无外边距，尺寸 = 文字尺寸 + 8/4 内边距，并按正方形宽度截断。</summary>
    private void SyncCornerPill()
    {
        string? text = _cornerLabel;
        if (string.IsNullOrWhiteSpace(text))
        {
            _cornerText.Hidden = true;
            _chrome.PillRect = CGRect.Empty;
            return;
        }
        _cornerText.Hidden = false;
        _cornerText.Text = text;
        _cornerText.SizeToFit();
        double side = Math.Max(0, (double)Bounds.Width);
        double width = Math.Min((double)_cornerText.Frame.Width + CornerPillPaddingX * 2, side);
        double height = (double)_cornerText.Frame.Height + CornerPillPaddingY * 2;
        _chrome.PillRect = new CGRect(0, 0, width, height);
        _cornerText.Frame = new CGRect(CornerPillPaddingX, CornerPillPaddingY,
            Math.Max(0, width - CornerPillPaddingX * 2), (double)_cornerText.Frame.Height);
        _chrome.SetNeedsDisplay();
    }

    /// <summary>说明文字排在正方形下方，外边距 4/6/4/4，按瓦片宽度换行居中。</summary>
    /// <param name="side">正方形边长。</param>
    private void SyncCaption(double side)
    {
        string? text = _label;
        if (string.IsNullOrWhiteSpace(text))
        {
            _caption.Hidden = true;
            return;
        }
        _caption.Hidden = false;
        _caption.Text = text;
        double width = Math.Max(1, (double)Bounds.Width - CaptionMarginLeft - CaptionMarginRight);
        _caption.Frame = new CGRect(CaptionMarginLeft, side + CaptionMarginTop, width, MeasureCaption(width));
    }

    /// <summary>量出说明文字在给定宽度下换行后的高度；没有说明文字时返回 0。</summary>
    /// <param name="width">可用宽度。</param>
    /// <returns>文字高度（点）。</returns>
    private double MeasureCaption(double width)
    {
        string? text = _label;
        if (string.IsNullOrWhiteSpace(text) || width <= 0) return 0;
        _caption.Text = text;
        return (double)_caption.SizeThatFits(new CGSize(width, MeasureLimit)).Height;
    }

    /// <summary>点击：只有落在内容矩形内才换算成归一化坐标抛出，letterbox 黑边与说明文字区的点击只被消费。</summary>
    /// <param name="recognizer">点击识别器。</param>
    private void OnTapped(UITapGestureRecognizer recognizer)
    {
        var content = _contentRect;
        if (content.Width <= 0 || content.Height <= 0) return;
        var point = recognizer.LocationInView(this);
        if (!content.Contains(point)) return;
        double u = Math.Clamp(((double)point.X - (double)content.X) / (double)content.Width, 0, 1);
        double v = Math.Clamp(((double)point.Y - (double)content.Y) / (double)content.Height, 0, 1);
        TileClicked?.Invoke(u, v);
    }

    /// <summary>建立固定内容模式的图像视图。</summary>
    /// <returns>图像视图。</returns>
    private static UIImageView ImageView() => new()
    {
        ContentMode = UIViewContentMode.ScaleToFill,
        BackgroundColor = UIColor.Clear,
        ClipsToBounds = true,
    };

    /// <summary>把矩形四周各内缩一段距离，避免出现负尺寸。</summary>
    /// <param name="rect">原始矩形。</param>
    /// <param name="amount">内缩量。</param>
    /// <returns>内缩后的矩形。</returns>
    private static CGRect Inset(CGRect rect, double amount)
        => new(rect.X + amount, rect.Y + amount,
            Math.Max(0, (double)rect.Width - amount * 2), Math.Max(0, (double)rect.Height - amount * 2));

    /// <summary>与瓦片点击识别器并存的委托：细节预览的 hover/点按需要同时识别。</summary>
    private sealed class SimultaneousDelegate : UIGestureRecognizerDelegate
    {
        /// <summary>允许与其他识别器同时识别。</summary>
        /// <param name="gestureRecognizer">本识别器。</param>
        /// <param name="otherGestureRecognizer">其他识别器。</param>
        /// <returns>始终允许。</returns>
        public override bool ShouldRecognizeSimultaneously(UIGestureRecognizer gestureRecognizer, UIGestureRecognizer otherGestureRecognizer) => true;
    }

    /// <summary>
    /// 正方形范围内的装饰层：绘制左上角药丸底与十字准星，并承载药丸文字标签。
    /// 始终叠在叠加视图之上；命中判定在瓦片层完成，这里不接收触摸。
    /// </summary>
    private sealed class ChromeView(iOSDiagnosticTile owner) : UIView
    {
        /// <summary>药丸矩形（以正方形左上角为原点，无外边距）；空矩形表示不显示。</summary>
        internal CGRect PillRect { get; set; } = CGRect.Empty;

        /// <summary>装饰层纯绘制，不参与交互。</summary>
        public override bool UserInteractionEnabled
        {
            get => false;
            set { }
        }

        /// <summary>先画准星再画药丸底，与 Mac/旧壳版的内容层顺序一致。</summary>
        /// <param name="rect">脏矩形。</param>
        public override void Draw(CGRect rect)
        {
            DrawCrosshair();
            DrawCornerPill();
        }

        /// <summary>十字准星：两条 1 点白线，单臂不超过 12 点，并夹在内容矩形内不越界。</summary>
        private void DrawCrosshair()
        {
            if (owner._crosshairU is not { } u || owner._crosshairV is not { } v) return;
            var content = owner._contentRect;
            double width = (double)content.Width;
            double height = (double)content.Height;
            if (width <= 0 || height <= 0) return;

            double centerX = Math.Round(Math.Clamp(u, 0, 1) * width);
            double centerY = Math.Round(Math.Clamp(v, 0, 1) * height);
            double armV = Math.Min(CrosshairArmLength, height / 2);
            double armH = Math.Min(CrosshairArmLength, width / 2);
            double top = Math.Max(0, centerY - armV);
            double verticalLength = Math.Min(height - top, armV * 2);
            double left = Math.Max(0, centerX - armH);
            double horizontalLength = Math.Min(width - left, armH * 2);

            var context = UIGraphics.GetCurrentContext();
            if (context == null) return;
            CrosshairColor.SetFill();
            context.FillRect(new CGRect(content.X + centerX, content.Y + top, 1, verticalLength));
            context.FillRect(new CGRect(content.X + left, content.Y + centerY, horizontalLength, 1));
        }

        /// <summary>左上角药丸底：#88000000，只圆左上与右下两个角（半径 5），其余两角保持直角。</summary>
        private void DrawCornerPill()
        {
            var pill = PillRect;
            if (pill.Width <= 0 || pill.Height <= 0) return;
            var context = UIGraphics.GetCurrentContext();
            if (context == null) return;
            using var path = PillPath(pill);
            if (path.CGPath is not { } cgPath) return;
            CornerPillFill.SetFill();
            context.AddPath(cgPath);
            context.FillPath();
        }

        /// <summary>构造 5,0,5,0 圆角路径：两处 90° 转角用三次贝塞尔圆弧逼近，半径按矩形短边收敛。</summary>
        /// <param name="rect">药丸矩形。</param>
        /// <returns>路径。</returns>
        private static UIBezierPath PillPath(CGRect rect)
        {
            const double kappa = 0.5523;
            double radius = Math.Min(CornerPillRadius, Math.Min((double)rect.Width, (double)rect.Height) / 2);
            double control = radius * kappa;
            double left = rect.X;
            double top = rect.Y;
            double right = rect.X + rect.Width;
            double bottom = rect.Y + rect.Height;

            var path = new UIBezierPath();
            path.MoveTo(new CGPoint(left + radius, top));
            path.AddLineTo(new CGPoint(right, top));
            path.AddLineTo(new CGPoint(right, bottom - radius));
            path.AddCurveToPoint(new CGPoint(right - radius, bottom),
                new CGPoint(right, bottom - radius + control), new CGPoint(right - radius + control, bottom));
            path.AddLineTo(new CGPoint(left, bottom));
            path.AddLineTo(new CGPoint(left, top + radius));
            path.AddCurveToPoint(new CGPoint(left + radius, top),
                new CGPoint(left, top + radius - control), new CGPoint(left + radius - control, top));
            path.ClosePath();
            return path;
        }
    }
}
