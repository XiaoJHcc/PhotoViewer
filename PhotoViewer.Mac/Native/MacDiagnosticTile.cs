using AppKit;
using CoreGraphics;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生诊断瓦片:正方形外框跟随父容器宽度定高,内容按 <see cref="AspectRatio"/> letterbox 居中铺开
/// (横图取满宽、竖图取满高,不裁切不变形),说明文字排在正方形下方、仍属瓦片自身范围。
/// 绘制层自下而上:外框与内层圆角裁剪 → 压暗背景图(<see cref="BackgroundSource"/>,alpha 0.30)→ 主图(<see cref="Source"/>,不做插值)
/// → 叠加视图 <see cref="Overlay"/> → 装饰层(占位文本、十字准星、左上角药丸标签)。
/// 瓦片吞掉自身范围内的所有左键按下,内容矩形内交给 <see cref="TileClicked"/>,其余位置（letterbox/说明区/角标）交给 <see cref="BlankClicked"/>。
/// 收到的 <see cref="NSImage"/> 由调用方持有,本类只引用、从不释放。
/// </summary>
public sealed class MacDiagnosticTile : NSView
{
    /// <summary>外框圆角半径。</summary>
    private const double OuterCornerRadius = 6;
    /// <summary>内层内容裁剪圆角半径。</summary>
    private const double InnerCornerRadius = 5;
    /// <summary>外框描边宽度。</summary>
    private const double BorderWidth = 1;
    /// <summary>十字准星单臂长度上限(点);主线总长为 2 × 此值。</summary>
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
    /// <summary>角标药丸圆角半径(只圆外侧两角)。</summary>
    private const double CornerPillRadius = 5;
    /// <summary>背景缩略图压暗后的不透明度。</summary>
    private const double BackgroundAlpha = 0.30;
    /// <summary>占位文本左右各留的空白,避免长文本顶到边框。</summary>
    private const double PlaceholderInset = 8;
    /// <summary>说明文字量高时给出的高度上限,仅用于取换行后的实际高度。</summary>
    private const double CaptionMeasureLimit = 10000;

    /// <summary>外框填色 #222(同时是 letterbox 底色)。</summary>
    private static readonly NSColor FrameFill = NSColor.FromRgb((byte)0x22, (byte)0x22, (byte)0x22);
    /// <summary>外框描边 #444。</summary>
    private static readonly NSColor FrameStroke = NSColor.FromRgb((byte)0x44, (byte)0x44, (byte)0x44);
    /// <summary>占位文本颜色 #777。</summary>
    private static readonly NSColor PlaceholderColor = NSColor.FromRgb((byte)0x77, (byte)0x77, (byte)0x77);
    /// <summary>说明文字颜色 #888。</summary>
    private static readonly NSColor CaptionColor = NSColor.FromRgb((byte)0x88, (byte)0x88, (byte)0x88);
    /// <summary>角标药丸底色 #88000000。</summary>
    private static readonly NSColor CornerPillFill = NSColor.FromRgba((byte)0x00, (byte)0x00, (byte)0x00, (byte)0x88);
    /// <summary>角标药丸文字颜色 #aaffffff。</summary>
    private static readonly NSColor CornerPillTextColor = NSColor.FromRgba((byte)0xff, (byte)0xff, (byte)0xff, (byte)0xaa);
    /// <summary>十字准星颜色(纯白 1pt 线)。</summary>
    private static readonly NSColor CrosshairColor = NSColor.White;
    /// <summary>瓦片内所有文字共用的系统字体。</summary>
    private static readonly NSFont TextFont = NSFont.SystemFontOfSize((nfloat)TextFontSize) ?? throw new InvalidOperationException("系统字体不可用");

    private readonly ChromeView _chrome;
    private readonly NSTextField _placeholder;
    private readonly NSTextField _cornerText;
    private readonly NSTextField _caption;
    private NSImage? _source;
    private NSImage? _backgroundSource;
    private NSView? _overlay;
    private double _aspectRatio = 1.0;
    private string? _cornerLabel;
    private string? _label;
    private string? _placeholderText;
    private double? _crosshairU;
    private double? _crosshairV;
    /// <summary>letterbox 后的内容矩形(瓦片坐标,已扣除 1pt 边框)。</summary>
    private CGRect _contentRect = CGRect.Empty;

    /// <summary>建立外框、装饰层与底部说明文字;具体几何在宽度变化时由 <see cref="SyncLayout"/> 推出。</summary>
    public MacDiagnosticTile()
    {
        _chrome = new ChromeView(this);
        _placeholder = TextLabel(PlaceholderColor, NSLineBreakMode.TruncatingTail);
        _cornerText = TextLabel(CornerPillTextColor, NSLineBreakMode.TruncatingTail);
        _caption = TextLabel(CaptionColor, NSLineBreakMode.ByWordWrapping);
        EnableWrapping(_caption);
        _chrome.AddSubview(_placeholder);
        _chrome.AddSubview(_cornerText);
        AddSubview(_chrome);
        AddSubview(_caption);
    }

    /// <summary>原生坐标使用从上到下的逻辑点,正方形自 (0,0) 起算。</summary>
    public override bool IsFlipped => true;

    /// <summary>主源位图(锐度图 / PCA-RGB / cosine 等);为 null 时显示占位文本或空底。</summary>
    public NSImage? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            SyncPlaceholder();
            NeedsDisplay = true;
        }
    }

    /// <summary>压暗的原图缩略图,作为主图背景层(alpha 0.30,高质量插值)。</summary>
    public NSImage? BackgroundSource
    {
        get => _backgroundSource;
        set
        {
            if (ReferenceEquals(_backgroundSource, value)) return;
            _backgroundSource = value;
            NeedsDisplay = true;
        }
    }

    /// <summary>内容图宽高比(宽/高);&lt;= 0 视为 1(正方形)。变化时重算 letterbox。</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        set
        {
            if (value == _aspectRatio) return;
            _aspectRatio = value;
            SyncLayout();
            NeedsDisplay = true;
        }
    }

    /// <summary>左上角短标签(识别瓦片身份);null/空白 时隐藏整个药丸。</summary>
    public string? CornerLabel
    {
        get => _cornerLabel;
        set
        {
            if (string.Equals(_cornerLabel, value, StringComparison.Ordinal)) return;
            _cornerLabel = value;
            SyncCornerPill();
            _chrome.NeedsDisplay = true;
        }
    }

    /// <summary>底部说明文字;null/空白 时不显示,瓦片高度退回正方形。</summary>
    public string? Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
            SyncLayout();
            NeedsDisplay = true;
        }
    }

    /// <summary>占位文本;仅当 <see cref="Source"/> 为 null 且本属性非空白时在正方形内居中显示。</summary>
    public string? PlaceholderText
    {
        get => _placeholderText;
        set
        {
            if (string.Equals(_placeholderText, value, StringComparison.Ordinal)) return;
            _placeholderText = value;
            SyncPlaceholder();
            NeedsDisplay = true;
        }
    }

    /// <summary>内容区叠加视图(如矢量场),随内容矩形同步尺寸;替换时把旧视图从父视图摘除。</summary>
    public NSView? Overlay
    {
        get => _overlay;
        set
        {
            if (ReferenceEquals(_overlay, value)) return;
            _overlay?.RemoveFromSuperview();
            _overlay = value;
            if (_overlay != null)
            {
                // 插到装饰层之下:叠加视图须在图像之上、准星与药丸之下。
                AddSubview(_overlay, NSWindowOrderingMode.Below, _chrome);
                _overlay.Frame = _contentRect;
                _overlay.NeedsDisplay = true;
            }
        }
    }

    /// <summary>内容矩形内点击回调,参数为归一化图像坐标(0 左/上,1 右/下)。</summary>
    public Action<double, double>? TileClicked { get; set; }
    /// <summary>点击内容矩形之外（letterbox、说明区或角标）时触发，等同面板空白（ANALYSIS-08）。</summary>
    public Action? BlankClicked { get; set; }

    /// <summary>设置归一化准星位置;任一坐标为 null 即隐藏,取值不变时不重绘。</summary>
    public void SetCrosshair(double? u, double? v)
    {
        if (Nullable.Equals(_crosshairU, u) && Nullable.Equals(_crosshairV, v)) return;
        _crosshairU = u;
        _crosshairV = v;
        _chrome.NeedsDisplay = true;
    }

    /// <summary>
    /// 给定外宽时瓦片所需总高:没有说明文字时就是正方形高度,有说明文字时再加上下边距与换行后的文字高度。
    /// </summary>
    public double RequiredHeight(double width)
    {
        if (string.IsNullOrWhiteSpace(_label)) return width;
        return width + CaptionMarginTop
            + MeasureCaption(width - CaptionMarginLeft - CaptionMarginRight)
            + CaptionMarginBottom;
    }

    /// <summary>宽度变化时重算正方形、letterbox 内容矩形与所有文字位置。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        SyncLayout();
    }

    /// <summary>
    /// 绘制外框、内层圆角裁剪与两层图像。描边内缩半个线宽,保证 1pt 边框完整可见;
    /// 主图用不做插值的 CoreGraphics 绘制,让 32×32 热力图保持像素级清晰。
    /// </summary>
    public override void DrawRect(CGRect dirtyRect)
    {
        double side = (double)Bounds.Width;
        if (side <= 0) return;
        var context = NSGraphicsContext.CurrentContext;
        if (context == null) return;

        var square = new CGRect(0, 0, side, side);
        using (var frame = NSBezierPath.FromRoundedRect(square, (nfloat)OuterCornerRadius, (nfloat)OuterCornerRadius))
        {
            FrameFill.SetFill();
            frame.Fill();
        }
        using (var border = NSBezierPath.FromRoundedRect(Inset(square, BorderWidth / 2),
            (nfloat)(OuterCornerRadius - BorderWidth / 2), (nfloat)(OuterCornerRadius - BorderWidth / 2)))
        {
            border.LineWidth = (nfloat)BorderWidth;
            FrameStroke.SetStroke();
            border.Stroke();
        }

        // 内层圆角 5 的裁剪:内容铺满整个内方形时四角仍保持圆角。
        using var clip = NSBezierPath.FromRoundedRect(Inset(square, BorderWidth), (nfloat)InnerCornerRadius, (nfloat)InnerCornerRadius);
        context.SaveGraphicsState();
        try
        {
            clip.AddClip();
            DrawImage(_backgroundSource, _contentRect, BackgroundAlpha, NSImageInterpolation.High);
            DrawImage(_source, _contentRect, 1.0, NSImageInterpolation.None);
        }
        finally
        {
            context.RestoreGraphicsState();
        }
    }

    /// <summary>瓦片吞掉自身范围内的所有命中,点击统一由 <see cref="MouseDown"/> 判定。</summary>
    public override NSView? HitTest(CGPoint point) => Frame.Contains(point) ? this : null;

    /// <summary>
    /// 左键按下时吞掉事件;只有落在内容矩形内（且不在角标药丸上）才换算成归一化坐标抛出 <see cref="TileClicked"/>,
    /// letterbox 黑边、说明文字区与角标按下按面板空白处理，抛出 <see cref="BlankClicked"/>（ANALYSIS-08）。
    /// </summary>
    public override void MouseDown(NSEvent theEvent)
    {
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        var content = _contentRect;
        bool onPill = _chrome.PillRect.Width > 0 && _chrome.PillRect.Height > 0 && _chrome.PillRect.Contains(point);
        if (content.Width <= 0 || content.Height <= 0 || !content.Contains(point) || onPill)
        {
            BlankClicked?.Invoke();
            return;
        }
        double u = Math.Clamp(((double)point.X - (double)content.X) / (double)content.Width, 0, 1);
        double v = Math.Clamp(((double)point.Y - (double)content.Y) / (double)content.Height, 0, 1);
        TileClicked?.Invoke(u, v);
    }

    /// <summary>把正方形、内容矩形、装饰子视图、叠加视图与说明文字按当前尺寸和数据同步。</summary>
    private void SyncLayout()
    {
        double side = Math.Max(0, (double)Bounds.Width);
        _contentRect = ContentRect(side);
        _chrome.Frame = new CGRect(0, 0, side, side);
        if (_overlay != null) _overlay.Frame = _contentRect;
        SyncPlaceholder();
        SyncCornerPill();
        SyncCaption(side);
    }

    /// <summary>按宽高比算出内缩外框后的居中内容矩形:横图取满宽、竖图取满高,不裁切不变形。</summary>
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
        return new CGRect((side - contentWidth) / 2, (side - contentHeight) / 2, contentWidth, contentHeight);
    }

    /// <summary>占位文本在正方形内居中(不限于内容矩形);<see cref="Source"/> 一旦赋值即隐藏。</summary>
    private void SyncPlaceholder()
    {
        string? text = _placeholderText;
        if (_source != null || string.IsNullOrWhiteSpace(text))
        {
            _placeholder.Hidden = true;
            return;
        }

        _placeholder.Hidden = false;
        _placeholder.StringValue = text;
        double side = Math.Max(0, (double)Bounds.Width);
        var measured = _placeholder.Cell?.CellSize ?? CGSize.Empty;
        double width = Math.Min((double)measured.Width, Math.Max(1, side - PlaceholderInset * 2));
        double height = (double)measured.Height;
        _placeholder.Frame = new CGRect((side - width) / 2, (side - height) / 2, width, height);
    }

    /// <summary>左上角药丸贴正方形左上角、无外边距,尺寸 = 文字尺寸 + 8/4 内边距,并按正方形宽度截断。</summary>
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
        _cornerText.StringValue = text;
        double side = Math.Max(0, (double)Bounds.Width);
        var measured = _cornerText.Cell?.CellSize ?? CGSize.Empty;
        double width = Math.Min((double)measured.Width + CornerPillPaddingX * 2, side);
        double height = (double)measured.Height + CornerPillPaddingY * 2;
        _chrome.PillRect = new CGRect(0, 0, width, height);
        _cornerText.Frame = new CGRect(CornerPillPaddingX, CornerPillPaddingY,
            Math.Max(0, width - CornerPillPaddingX * 2), (double)measured.Height);
    }

    /// <summary>说明文字排在正方形下方,外边距 4/6/4/4,按瓦片宽度换行居中。</summary>
    private void SyncCaption(double side)
    {
        string? text = _label;
        if (string.IsNullOrWhiteSpace(text))
        {
            _caption.Hidden = true;
            return;
        }

        _caption.Hidden = false;
        _caption.StringValue = text;
        double width = Math.Max(1, (double)Bounds.Width - CaptionMarginLeft - CaptionMarginRight);
        _caption.Frame = new CGRect(CaptionMarginLeft, side + CaptionMarginTop, width, MeasureCaption(width));
    }

    /// <summary>量出说明文字在给定宽度下换行后的高度;没有说明文字时返回 0。</summary>
    private double MeasureCaption(double width)
    {
        string? text = _label;
        if (string.IsNullOrWhiteSpace(text) || width <= 0) return 0;
        _caption.StringValue = text;
        var cell = _caption.Cell;
        if (cell == null) return 0;
        return (double)cell.CellSizeForBounds(new CGRect(0, 0, width, CaptionMeasureLimit)).Height;
    }

    /// <summary>生成 12pt 只读标签:透明背景、居中、不接收鼠标。</summary>
    private static NSTextField TextLabel(NSColor color, NSLineBreakMode lineBreakMode)
        => new(CGRect.Empty)
        {
            Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
            Alignment = NSTextAlignment.Center, Font = TextFont, TextColor = color, LineBreakMode = lineBreakMode,
        };

    /// <summary>说明文字依赖单元格的换行标志才能量出多行高度。</summary>
    private static void EnableWrapping(NSTextField field)
    {
        var cell = field.Cell;
        if (cell != null) cell.Wraps = true;
    }

    /// <summary>把矩形四周各内缩一段距离,避免出现负尺寸。</summary>
    private static CGRect Inset(CGRect rect, double amount)
        => new(rect.X + amount, rect.Y + amount,
            Math.Max(0, (double)rect.Width - amount * 2), Math.Max(0, (double)rect.Height - amount * 2));

    /// <summary>
    /// 在指定矩形内铺满绘制位图(不裁切、不变形)。瓦片坐标自上而下,而 CoreGraphics 图像原点在左下,
    /// 故通过翻转 CTM 摆正图像,再按调用方要求设置插值与整体透明度。
    /// </summary>
    private static void DrawImage(NSImage? image, CGRect rect, double alpha, NSImageInterpolation interpolation)
    {
        var context = NSGraphicsContext.CurrentContext;
        if (context == null || image == null || rect.Width <= 0 || rect.Height <= 0) return;
        var cgImage = image.CGImage;
        if (cgImage == null) return;

        var previous = context.ImageInterpolation;
        context.ImageInterpolation = interpolation;
        var cg = context.CGContext;
        cg.SaveState();
        try
        {
            cg.SetAlpha((nfloat)alpha);
            cg.TranslateCTM(rect.X, rect.Y + rect.Height);
            cg.ScaleCTM(1, -1);
            cg.DrawImage(new CGRect(0, 0, rect.Width, rect.Height), cgImage);
        }
        finally
        {
            cg.RestoreState();
            context.ImageInterpolation = previous;
        }
    }

    /// <summary>
    /// 正方形范围内的装饰层:绘制左上角药丸底与十字准星,并承载占位文本与药丸文字两个只读标签。
    /// 作为瓦片的子视图,它始终叠在叠加视图之上;命中的判定在瓦片层完成,这里不再单独处理鼠标。
    /// </summary>
    private sealed class ChromeView(MacDiagnosticTile owner) : NSView
    {
        /// <summary>药丸矩形(以正方形左上角为原点,无外边距);空矩形表示不显示。</summary>
        internal CGRect PillRect { get; set; } = CGRect.Empty;

        /// <summary>装饰层与瓦片共用自上而下的坐标。</summary>
        public override bool IsFlipped => true;

        /// <summary>先画准星再画药丸底,与 Avalonia 版的内容层顺序一致。</summary>
        public override void DrawRect(CGRect dirtyRect)
        {
            DrawCrosshair();
            DrawCornerPill();
        }

        /// <summary>十字准星:两条 1pt 白线,单臂不超过 12pt,并夹在内容矩形内不越界。</summary>
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

            CrosshairColor.SetFill();
            using var vertical = NSBezierPath.FromRect(new CGRect(content.X + centerX, content.Y + top, 1, verticalLength));
            using var horizontal = NSBezierPath.FromRect(new CGRect(content.X + left, content.Y + centerY, horizontalLength, 1));
            vertical.Fill();
            horizontal.Fill();
        }

        /// <summary>左上角药丸底:#88000000,只圆左上与右下两个角(半径 5),其余两角保持直角。</summary>
        private void DrawCornerPill()
        {
            var pill = PillRect;
            if (pill.Width <= 0 || pill.Height <= 0) return;
            using var path = PillPath(pill);
            CornerPillFill.SetFill();
            path.Fill();
        }

        /// <summary>构造 5,0,5,0 圆角路径:两处 90° 转角用三次贝塞尔圆弧逼近,半径按矩形短边收敛。</summary>
        private static NSBezierPath PillPath(CGRect rect)
        {
            const double kappa = 0.5523;
            double radius = Math.Min(CornerPillRadius, Math.Min((double)rect.Width, (double)rect.Height) / 2);
            double control = radius * kappa;
            double left = rect.X;
            double top = rect.Y;
            double right = rect.X + rect.Width;
            double bottom = rect.Y + rect.Height;

            var path = new NSBezierPath();
            path.MoveTo(new CGPoint(left + radius, top));
            path.LineTo(new CGPoint(right, top));
            path.LineTo(new CGPoint(right, bottom - radius));
            path.CurveTo(new CGPoint(right, bottom - radius + control),
                new CGPoint(right - radius + control, bottom), new CGPoint(right - radius, bottom));
            path.LineTo(new CGPoint(left, bottom));
            path.LineTo(new CGPoint(left, top + radius));
            path.CurveTo(new CGPoint(left, top + radius - control),
                new CGPoint(left + radius - control, top), new CGPoint(left + radius, top));
            path.ClosePath();
            return path;
        }
    }
}
