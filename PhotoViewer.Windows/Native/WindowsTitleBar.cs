using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 桌面标题栏(SHELL-02):标题文字 + 全屏/最小化/最大化/关闭四个自绘按钮。
/// <para>
/// 只负责"看得见的部分":默认是悬停覆盖层，窄宽度时由主窗口让出布局高度并常态显示；
/// <see cref="IsHitTestVisible"/> 保持默认(空处不命中任何元素),
/// 背景是一块独立的不可命中元素,按钮才是唯一可命中的目标。拖动区不在这里画元素 ——
/// 由 <see cref="WindowsMainWindow"/> 把区域登记成系统 caption,所以标题栏不可见时顶部不可拖动。
/// </para>
/// </summary>
internal sealed class WindowsTitleBar
{
    /// <summary>WinUI 应用内亚克力主题画刷(深色主题),列布局与文件栏隐藏时铺在标题栏后面。</summary>
    private const string AcrylicBackgroundBrushKey = "AcrylicInAppFillColorDefaultBrush";

    /// <summary>WinUI 悬停底色主题画刷。</summary>
    private const string HoverBackgroundBrushKey = "SubtleFillColorSecondaryBrush";

    /// <summary>WinUI 按下底色主题画刷。</summary>
    private const string PressedBackgroundBrushKey = "SubtleFillColorTertiaryBrush";

    /// <summary>WinUI 标题文字样式(字号/字体/颜色跟随系统标题栏)。</summary>
    private const string CaptionTextStyleKey = "CaptionTextBlockStyle";

    /// <summary>WinUI 主文字色,窗口按钮字形常态与悬停取它。</summary>
    private const string PrimaryForegroundBrushKey = "TextFillColorPrimaryBrush";

    /// <summary>WinUI 次文字色,窗口按钮字形按下态取它。</summary>
    private const string SecondaryForegroundBrushKey = "TextFillColorSecondaryBrush";

    private readonly Grid _root;

    // 背景拆成两段:主图渲染器在交换链内画了亚克力的区段不铺 XAML 背景(见 SetSpan)。
    private readonly Border _backgroundLeading;
    private readonly Border _backgroundTrailing;
    private readonly AcrylicBackdropRecipe _backdropRecipe;

    // 接缝渐变:左段右缘 / 右段左缘紧贴主图区段时,从接缝值(不透明)渐变到透明,与主图区段一侧在交界处取同一个值。
    private readonly Border _seamLeading;
    private readonly Border _seamTrailing;
    private IReadOnlyList<Rect> _backgroundSegments = Array.Empty<Rect>();
    private readonly TextBlock _title;
    private readonly StackPanel _buttons;
    private readonly Button _fullScreenButton;
    private readonly Button _minimizeButton;
    private readonly Button _maximizeButton;
    private readonly Button _closeButton;
    private readonly FontIcon _fullScreenGlyph;
    private readonly FontIcon _maximizeGlyph;
    private bool _visible;

    /// <summary>
    /// 构建标题栏覆盖层。
    /// </summary>
    /// <param name="title">标题文本</param>
    /// <param name="toggleFullScreen">全屏 / 退出全屏</param>
    /// <param name="minimize">最小化</param>
    /// <param name="toggleMaximize">最大化 / 还原</param>
    /// <param name="close">关闭</param>
    public WindowsTitleBar(string title, Action toggleFullScreen, Action minimize, Action toggleMaximize, Action close)
    {
        BackgroundBrush = Resource<Brush>(AcrylicBackgroundBrushKey);
        _backdropRecipe = BuildBackdropRecipe(BackgroundBrush as AcrylicBrush);
        _seamLeading = CreateSeam(towardRight: true);
        _seamTrailing = CreateSeam(towardRight: false);
        _backgroundLeading = CreateBackgroundSegment(_seamLeading);
        _backgroundTrailing = CreateBackgroundSegment(_seamTrailing);

        // CaptionTextBlockStyle 自带字号/字体/颜色:标题与系统标题栏文字同源,不自调字号。
        _title = new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(WindowsLayout.TitleBarTitleInset, 0, 0, 0),
            IsHitTestVisible = false,
        };
        if (Resource<Style>(CaptionTextStyleKey) is { } style)
        {
            _title.Style = style;
        }
        else
        {
            NativeStartupLog.Write($"标题栏:缺少 WinUI 样式 {CaptionTextStyleKey},标题文字回落到默认字号");
        }

        // 无右边距、无按钮间距:按钮组紧贴窗口顶边与右缘,组内每个 46×32。
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _fullScreenButton = CreateButton(WindowsIcons.FullScreenGlyph, "全屏", null, null, null, toggleFullScreen,
            out _fullScreenGlyph);
        _minimizeButton = CreateButton(WindowsIcons.MinimizeGlyph, "最小化", null, null, null, minimize, out _);
        _maximizeButton = CreateButton(WindowsIcons.MaximizeGlyph, "最大化", null, null, null, toggleMaximize, out _maximizeGlyph);
        _closeButton = CreateButton(WindowsIcons.CloseGlyph, "关闭", WindowsLayout.TitleBarCloseHover,
            WindowsLayout.TitleBarClosePressed, new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), close, out _);
        Buttons = new[] { _fullScreenButton, _minimizeButton, _maximizeButton, _closeButton };
        foreach (var button in Buttons)
        {
            _buttons.Children.Add(button);
        }

        // 覆盖层本身没有背景:空处不命中任何元素,指针与点击直接落到下层(全屏无拖动区时留给主图)。
        _root = new Grid
        {
            Height = WindowsLayout.TitleBarHeight,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_backgroundLeading);
        _root.Children.Add(_backgroundTrailing);
        _root.Children.Add(_title);
        _root.Children.Add(_buttons);
    }

    /// <summary>覆盖层根元素(加到窗口内容最上层)。</summary>
    public UIElement Root => _root;

    /// <summary>标题栏当前是否可见(拖动区是否登记)。</summary>
    public bool IsVisible => _visible;

    /// <summary>背景是否可见(行布局文件栏可见时无背景)。</summary>
    public bool IsBackgroundVisible
        => _backgroundLeading.Visibility == Visibility.Visible || _backgroundTrailing.Visibility == Visibility.Visible;

    /// <summary>背景画刷(验收核对它确实是 WinUI 应用内亚克力)。</summary>
    public Brush? BackgroundBrush { get; }

    /// <summary>
    /// XAML 背景实际铺出的分段(覆盖层坐标,已扣除主图渲染器画亚克力的区段)。
    /// </summary>
    public IReadOnlyList<Rect> BackgroundSegments => _backgroundSegments;

    /// <summary>
    /// 主图渲染器在交换链内复刻亚克力用的配方:主题画刷的运行时取值,不在代码里硬编码颜色。
    /// </summary>
    public AcrylicBackdropRecipe BackdropRecipe => _backdropRecipe;

    /// <summary>
    /// 主题画刷能否在主图交换链里复刻(必须是 <see cref="AcrylicBrush"/>);
    /// 不能时整条背景留 XAML,交换链区段不开。
    /// </summary>
    public bool HasBackdropRecipe => BackgroundBrush is AcrylicBrush;

    /// <summary>标题文字当前内容。</summary>
    public string TitleText => _title.Text;

    /// <summary>标题文字元素(验收核对样式、截断与可用宽)。</summary>
    internal TextBlock TitleElement => _title;

    /// <summary>覆盖层左缘在窗口内容内的偏移(列布局 = 文件栏右缘)。</summary>
    public double Left => _root.Margin.Left;

    /// <summary>窗口按钮,从左到右为全屏、最小化、最大化、关闭。</summary>
    public IReadOnlyList<Button> Buttons { get; }

    /// <summary>
    /// 按钮组占位宽(外框宽 × 数量,无右边距与按钮间距),用于布局尚未完成时的兜底。
    /// </summary>
    public double ButtonsWidth => WindowsLayout.TitleBarButtonWidth * Buttons.Count;

    /// <summary>
    /// 显示或隐藏标题栏。隐藏时按钮不参与命中测试。
    /// </summary>
    /// <param name="visible">是否显示</param>
    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        _root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前布局设置跨度与背景:列布局从文件栏右缘起,行布局悬停形态整宽无背景,
    /// 窄宽度常驻栏与文件栏隐藏时整宽带亚克力。
    /// <paramref name="photoBand"/> 是主图渲染器已经画好亚克力的区段(覆盖层坐标),该处 XAML 背景让空:
    /// SwapChainPanel 不能给 <c>AcrylicBrush</c> 取样,只有交换链内的那一份才有真实的模糊。
    /// </summary>
    /// <param name="left">覆盖层左缘在窗口内容内的偏移</param>
    /// <param name="showBackground">是否铺亚克力背景</param>
    /// <param name="photoBand">主图已画亚克力的区段(覆盖层坐标);null 表示整条都由 XAML 铺</param>
    /// <param name="width">覆盖层宽度(窗口内容宽减去左偏移)</param>
    public void SetSpan(double left, bool showBackground, Rect? photoBand, double width)
    {
        _root.Margin = new Thickness(left, 0, 0, 0);
        if (!showBackground || width <= 0)
        {
            HideSegment(_backgroundLeading);
            HideSegment(_backgroundTrailing);
            _backgroundSegments = Array.Empty<Rect>();
            return;
        }

        var bandStart = photoBand is { } band ? Math.Clamp(band.X, 0, width) : width;
        var bandEnd = photoBand is { } gap ? Math.Clamp(gap.X + gap.Width, bandStart, width) : width;
        var segments = new List<Rect>(2);
        if (bandStart > 0.5)
        {
            segments.Add(new Rect(0, 0, bandStart, WindowsLayout.TitleBarHeight));
            ShowLeadingSegment(bandStart);
            SetSeam(_seamLeading, photoBand != null, bandStart);
        }
        else
        {
            HideSegment(_backgroundLeading);
        }

        if (width - bandEnd > 0.5)
        {
            segments.Add(new Rect(bandEnd, 0, width - bandEnd, WindowsLayout.TitleBarHeight));
            ShowTrailingSegment(bandEnd);
            SetSeam(_seamTrailing, photoBand != null, width - bandEnd);
        }
        else
        {
            HideSegment(_backgroundTrailing);
        }

        _backgroundSegments = segments;
    }

    /// <summary>同步标题文字(与窗口标题同一字符串)。</summary>
    /// <param name="text">标题文案</param>
    public void SetTitle(string text)
    {
        if (_title.Text != text)
        {
            _title.Text = text;
        }
    }

    /// <summary>
    /// 设置标题文字的可用宽:尾部截断;宽度不足时整体隐藏(单行、不参与命中)。
    /// </summary>
    /// <param name="maxWidth">标题文字可占的宽(不含前导间距)</param>
    public void SetTitleMaxWidth(double maxWidth)
    {
        if (maxWidth < WindowsLayout.TitleBarTitleMinWidth)
        {
            _title.Visibility = Visibility.Collapsed;
            return;
        }

        _title.Visibility = Visibility.Visible;
        _title.MaxWidth = maxWidth;
    }

    /// <summary>
    /// 按窗口态同步字形、悬停提示与读屏名:最大化 ↔ 还原、全屏 ↔ 退出全屏(SHELL-06)。
    /// </summary>
    /// <param name="isFullScreen">是否全屏</param>
    /// <param name="isMaximized">呈现器是否最大化</param>
    public void SetWindowState(bool isFullScreen, bool isMaximized)
    {
        if (isFullScreen)
        {
            Apply(_fullScreenButton, _fullScreenGlyph, WindowsIcons.ExitFullScreenGlyph, "退出全屏");
        }
        else
        {
            Apply(_fullScreenButton, _fullScreenGlyph, WindowsIcons.FullScreenGlyph, "全屏");
        }

        // 全屏不是最大化:全屏态下最大化按钮保持"最大化",与系统 caption 口径一致。
        if (!isFullScreen && isMaximized)
        {
            Apply(_maximizeButton, _maximizeGlyph, WindowsIcons.RestoreGlyph, "还原");
        }
        else
        {
            Apply(_maximizeButton, _maximizeGlyph, WindowsIcons.MaximizeGlyph, "最大化");
        }
    }

    /// <summary>
    /// 按钮组左边界(DIP):取真实布局出的位置;标题栏刚从隐藏切到显示、布局尚未跑完(或几何已过期)时
    /// 按常量兜底 —— 按钮组始终紧贴窗口右缘、彼此无间距。
    /// </summary>
    /// <param name="relativeTo">参考元素</param>
    /// <returns>按钮组在参考元素坐标系内的左边界</returns>
    internal double GetButtonsLeft(UIElement relativeTo)
    {
        var width = ((FrameworkElement)relativeTo).ActualWidth;
        double left = _buttons.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0)).X;
        if (!double.IsNaN(left) && Math.Abs(left + ButtonsWidth - width) < 1)
        {
            return left;
        }

        return Math.Max(0, width - ButtonsWidth);
    }

    /// <summary>取窗口按钮当前的 Segoe Fluent Icons 字形。</summary>
    /// <param name="button">窗口按钮</param>
    /// <returns>字形码位;非本类创建的按钮返回空串</returns>
    public static string GlyphOf(Button button) => button.Content is FontIcon { Glyph: { } glyph } ? glyph : string.Empty;

    /// <summary>按字形与文案更新按钮的显示、悬停提示与读屏名(保持 Button 实例与焦点不变)。</summary>
    /// <param name="button">窗口按钮</param>
    /// <param name="glyph">按钮字形元素</param>
    /// <param name="code">字形码位</param>
    /// <param name="text">悬停提示与读屏名</param>
    private static void Apply(Button button, FontIcon glyph, string code, string text)
    {
        if (glyph.Glyph != code)
        {
            glyph.Glyph = code;
        }

        if (!Equals(ToolTipService.GetToolTip(button), text))
        {
            ToolTipService.SetToolTip(button, text);
        }

        // SHELL-06：自绘窗口按钮必须向辅助技术暴露名称。
        if (AutomationProperties.GetName(button) != text)
        {
            AutomationProperties.SetName(button, text);
        }
    }

    /// <summary>创建窗口按钮。</summary>
    /// <param name="glyph">字形码位</param>
    /// <param name="tooltip">悬停提示与读屏名</param>
    /// <param name="hover">悬停底色;null 表示用 WinUI 标准悬停底色</param>
    /// <param name="pressed">按下底色;null 表示用 WinUI 标准按下底色</param>
    /// <param name="foreground">固定字形色;null 表示跟主题文字色</param>
    /// <param name="action">点击动作</param>
    /// <param name="icon">返回的字形元素</param>
    /// <returns>窗口按钮</returns>
    private static Button CreateButton(string glyph, string tooltip, Color? hover, Color? pressed, Brush? foreground,
        Action action, out FontIcon icon)
    {
        icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
            FontSize = WindowsLayout.TitleBarButtonGlyphSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 显式赋 null 会把字形画成透明;只有固定色的关闭按钮才覆盖,其余继承按钮模板的前景色。
        if (foreground != null)
        {
            icon.Foreground = foreground;
        }

        var button = new Button
        {
            Width = WindowsLayout.TitleBarButtonWidth,
            Height = WindowsLayout.TitleBarHeight,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Content = icon,
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);

        // WinUI 的 Button 模板按主题键取底色/描边/字形色;只设 Background 会被模板覆盖,
        // 因此常态、悬停、按下三态都显式写入,避免回落到默认亮色。
        Override(button, "ButtonBackground", new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)));
        Override(button, "ButtonBackgroundPointerOver", hover is { } hoverColor
            ? new SolidColorBrush(hoverColor)
            : Resource<Brush>(HoverBackgroundBrushKey));
        Override(button, "ButtonBackgroundPressed", pressed is { } pressedColor
            ? new SolidColorBrush(pressedColor)
            : Resource<Brush>(PressedBackgroundBrushKey));
        Override(button, "ButtonBorderBrush", new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)));
        Override(button, "ButtonBorderBrushPointerOver", new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)));
        Override(button, "ButtonBorderBrushPressed", new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)));
        if (foreground == null)
        {
            var primary = Resource<Brush>(PrimaryForegroundBrushKey);
            Override(button, "ButtonForeground", primary);
            Override(button, "ButtonForegroundPointerOver", primary);
            Override(button, "ButtonForegroundPressed", Resource<Brush>(SecondaryForegroundBrushKey));
        }

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>写入模板状态键;资源缺失时保持模板默认值,只记一条诊断。</summary>
    /// <param name="button">窗口按钮</param>
    /// <param name="key">模板资源键</param>
    /// <param name="value">画刷</param>
    private static void Override(Button button, string key, Brush? value)
    {
        if (value == null)
        {
            NativeStartupLog.Write($"标题栏:WinUI 主题画刷缺失,按钮保持模板默认值(key={key})");
            return;
        }

        button.Resources[key] = value;
    }

    /// <summary>建一段背景元素:不参与命中,常态隐藏,位置由 <see cref="SetSpan"/> 指定。</summary>
    /// <param name="seam">贴在该段与主图区段交界一侧的接缝渐变</param>
    /// <returns>背景分段元素。</returns>
    private Border CreateBackgroundSegment(Border seam) => new()
    {
        Background = BackgroundBrush,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch,
        Child = seam,
    };

    /// <summary>
    /// 建接缝渐变:接缝值(配方在纯 FallbackColor 背板上的结果)按 smoothstep 从交界处的不透明渐变到透明,
    /// 宽度 <see cref="WindowsLayout.TitleBarBackdropSeamWidth"/>,与主图区段一侧的渐变对称。
    /// </summary>
    /// <param name="towardRight">交界在右侧(左段)</param>
    /// <returns>渐变元素(常态隐藏)。</returns>
    private Border CreateSeam(bool towardRight)
    {
        var (r, g, b) = _backdropRecipe.SeamColor();
        var color = Color.FromArgb(0xFF, ToByte(r), ToByte(g), ToByte(b));
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(towardRight ? 1 : 0, 0.5),
            EndPoint = new Point(towardRight ? 0 : 1, 0.5),
        };
        // smoothstep(0, 1, t) 的补:t = 0 处完全是接缝值,t = 1 处完全是 XAML 亚克力。
        foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var weight = t * t * (3 - 2 * t);
            brush.GradientStops.Add(new GradientStop
            {
                Offset = t,
                Color = Color.FromArgb((byte)Math.Round((1 - weight) * 255), color.R, color.G, color.B),
            });
        }

        return new Border
        {
            Background = brush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = towardRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>显示或隐藏接缝渐变;段比渐变宽度窄时按段宽截断。</summary>
    /// <param name="seam">接缝渐变</param>
    /// <param name="visible">该段是否紧贴主图区段</param>
    /// <param name="segmentWidth">所在段的宽度</param>
    private static void SetSeam(Border seam, bool visible, double segmentWidth)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (seam.Visibility != target)
        {
            seam.Visibility = target;
        }

        var width = Math.Min(WindowsLayout.TitleBarBackdropSeamWidth, Math.Max(0, segmentWidth));
        if (Math.Abs(seam.Width - width) > 0.01 || double.IsNaN(seam.Width))
        {
            seam.Width = width;
        }
    }

    /// <summary>0–1 分量 → 8 位。</summary>
    /// <param name="value">分量</param>
    /// <returns>字节</returns>
    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255);

    /// <summary>接缝渐变是否在显示(验收用):左段右缘 / 右段左缘。</summary>
    internal (bool Leading, bool Trailing) SeamVisibility
        => (_backgroundLeading.Visibility == Visibility.Visible && _seamLeading.Visibility == Visibility.Visible,
            _backgroundTrailing.Visibility == Visibility.Visible && _seamTrailing.Visibility == Visibility.Visible);

    /// <summary>左段:从覆盖层左缘到照片区段左缘(按宽度定位)。</summary>
    /// <param name="width">段宽。</param>
    private void ShowLeadingSegment(double width)
    {
        if (_backgroundLeading.Visibility == Visibility.Visible && Math.Abs(_backgroundLeading.Width - width) < 0.01)
        {
            return;
        }

        _backgroundLeading.Width = width;
        _backgroundLeading.Margin = new Thickness(0);
        _backgroundLeading.Visibility = Visibility.Visible;
    }

    /// <summary>右段:从照片区段右缘到覆盖层右缘(拉伸 + 左外边距)。</summary>
    /// <param name="offset">段左缘在覆盖层内的偏移。</param>
    private void ShowTrailingSegment(double offset)
    {
        _backgroundTrailing.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (_backgroundTrailing.Visibility == Visibility.Visible
            && Math.Abs(_backgroundTrailing.Margin.Left - offset) < 0.01)
        {
            return;
        }

        _backgroundTrailing.Margin = new Thickness(offset, 0, 0, 0);
        _backgroundTrailing.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏分段。</summary>
    /// <param name="segment">分段元素。</param>
    private static void HideSegment(Border segment)
    {
        if (segment.Visibility != Visibility.Collapsed)
        {
            segment.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 从 WinUI 亚克力主题画刷读出配方(TintColor / TintOpacity / TintLuminosityOpacity / FallbackColor)。
    /// 亮度层缺失时不猜数值,改用 FallbackColor 纯色带并记一条诊断。
    /// </summary>
    /// <param name="brush">主题画刷;不是 <see cref="AcrylicBrush"/> 时返回默认配方。</param>
    /// <returns>配方。</returns>
    private static AcrylicBackdropRecipe BuildBackdropRecipe(AcrylicBrush? brush)
    {
        if (brush == null)
        {
            NativeStartupLog.Write($"标题栏:缺少亚克力主题画刷 {AcrylicBackgroundBrushKey},主图区段不画背板");
            return default;
        }

        var tint = brush.TintColor;
        var fallback = brush.FallbackColor;
        double? luminosity = brush.TintLuminosityOpacity;
        var hasLuminosity = luminosity is { } value && !double.IsNaN(value);
        NativeStartupLog.Write($"title bar backdrop: 配方 tint=#{tint.R:X2}{tint.G:X2}{tint.B:X2}"
            + $" tintOpacity={brush.TintOpacity:0.###}"
            + $" luminosityOpacity={(hasLuminosity ? luminosity!.Value.ToString("0.###") : "未设置")}"
            + $" fallback=#{fallback.R:X2}{fallback.G:X2}{fallback.B:X2}"
            + (hasLuminosity ? "" : ",缺亮度层,主图区段改用 FallbackColor 纯色带"));
        // 与 AcrylicBrush.GetEffectiveTintColor 同口径:设置了亮度层时色调层不透明度 = TintColor.A × TintOpacity。
        return new AcrylicBackdropRecipe(
            (tint.R / 255f, tint.G / 255f, tint.B / 255f),
            (float)Math.Clamp(tint.A / 255.0 * brush.TintOpacity, 0, 1),
            hasLuminosity ? (float)Math.Clamp(luminosity!.Value, 0, 1) : -1f,
            (LinearChannel(fallback.R), LinearChannel(fallback.G), LinearChannel(fallback.B)));
    }

    /// <summary>8 位 sRGB 分量 → 线性 scRGB(0–1)。</summary>
    /// <param name="value">sRGB 分量。</param>
    /// <returns>线性分量。</returns>
    private static float LinearChannel(byte value)
    {
        double srgb = value / 255.0;
        return (float)(srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4));
    }

    /// <summary>读取 WinUI 主题资源(应用固定深色主题,直接取应用资源表)。</summary>
    /// <typeparam name="T">资源类型</typeparam>
    /// <param name="key">资源键</param>
    /// <returns>资源;缺失返回 null</returns>
    private static T? Resource<T>(string key) where T : class
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;
}
