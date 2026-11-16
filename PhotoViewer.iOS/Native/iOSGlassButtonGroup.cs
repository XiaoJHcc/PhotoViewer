using CoreGraphics;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 合体玻璃按钮：一个交互式玻璃胶囊承载各段，段内指针高亮由系统效果提供。
/// 段主轴长度默认取 <see cref="DesignTokens.ShellGlassButtonSize"/>，宿主可按区域几何覆盖。
/// </summary>
internal sealed class iOSGlassButtonGroup : UIVisualEffectView
{
    private readonly List<UIButton> _buttons = [];
    private bool _vertical;
    private bool _mirrored;

    internal iOSGlassButtonGroup() : base(new UIGlassEffect { Interactive = true })
    {
        CornerConfiguration = UICornerConfiguration.CreateCapsule();
        BackgroundColor = UIColor.Clear;
    }

    /// <summary>各段的按钮实例（顺序即排布顺序，隐藏段跳过排布）。</summary>
    internal IReadOnlyList<UIButton> Buttons => _buttons;

    /// <summary>合体组使用单一系统玻璃背景。</summary>
    internal bool UsesSharedGlass => true;

    /// <summary>单段沿排布轴的长度；默认 44，宽形态筛选段为 48。</summary>
    internal double SegmentLength { get; set; } = DesignTokens.ShellGlassButtonSize;

    /// <summary>是否纵向排布（横屏控制列用纵排，其余横排）。</summary>
    internal bool Vertical
    {
        get => _vertical;
        set
        {
            if (_vertical == value) return;
            _vertical = value;
            SetNeedsLayout();
        }
    }

    /// <summary>段序是否从末端起排（横屏刘海在右时整栏镜像，筛选段落在合体右侧）。</summary>
    internal bool Mirrored
    {
        get => _mirrored;
        set
        {
            if (_mirrored == value) return;
            _mirrored = value;
            SetNeedsLayout();
        }
    }

    /// <summary>追加一个按钮段。</summary>
    internal UIButton Add(string symbolName, string label, Action? action)
    {
        var button = iOSGlassButtons.Segment(symbolName, label, action);
        ContentView.AddSubview(button);
        _buttons.Add(button);
        SetNeedsLayout();
        return button;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        double length = SegmentLength;
        int visible = _buttons.Count(button => !button.Hidden);
        int index = 0;
        foreach (var button in _buttons)
        {
            if (button.Hidden) continue;
            int slot = Mirrored ? visible - 1 - index : index;
            iOSViewGeometry.SetRectIfNeeded(button, Vertical
                ? new CGRect(0, slot * length, Bounds.Width, length)
                : new CGRect(slot * length, 0, length, Bounds.Height));
            index++;
        }
    }
}

/// <summary>
/// 原生视图落位的稳定写法：交互式玻璃的按压缩放、松手回弹由系统在按压期间驱动自身几何，
/// 重排只按**未变换**的 bounds/center 比较，几何未变就完全不写视图。
/// 不用 <c>Frame</c> 直接赋值：带变换的视图写 Frame 会由 frame 反解 bounds，
/// 把正在进行的按压缩放/回弹改成永久尺寸变化（星排格子的按压放大就是这样被冲掉的）。
/// </summary>
internal static class iOSViewGeometry
{
    /// <summary>按目标矩形设置 bounds 与 center；两者都已一致时什么都不写（保持系统驱动的变换与动画）。</summary>
    /// <param name="view">目标视图。</param>
    /// <param name="rect">目标矩形（父视图坐标，bounds 原点取 0）。</param>
    internal static void SetRectIfNeeded(UIView view, CGRect rect)
    {
        var bounds = new CGRect(0, 0, rect.Width, rect.Height);
        var center = new CGPoint(rect.GetMidX(), rect.GetMidY());
        if (view.Bounds == bounds && view.Center == center) return;
        view.Bounds = bounds;
        view.Center = center;
    }
}

/// <summary>玻璃按钮工厂：单圆按钮使用系统玻璃配置，合体段使用系统无底色配置，背景由所在玻璃容器统一承担。</summary>
internal static class iOSGlassButtons
{
    /// <summary>单圆按钮：系统玻璃按钮配置 + 系统符号，方形边长即系统胶囊形。</summary>
    /// <param name="symbolName">SF Symbols 名称。</param>
    /// <param name="label">读屏名称。</param>
    /// <param name="action">点按回调。</param>
    /// <returns>配置完成的按钮。</returns>
    internal static UIButton Round(string symbolName, string label, Action? action)
    {
        var configuration = UIButtonConfiguration.GlassButtonConfiguration;
        configuration.Image = iOSSymbols.Symbol(symbolName, DesignTokens.ToolbarCommandGlyphFontSize);
        configuration.BaseForegroundColor = UIColor.Label;
        var button = new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            PointerInteractionEnabled = true,
            AccessibilityLabel = label,
        };
        if (action != null) button.TouchUpInside += (_, _) => action();
        EnableHighlight(button);
        return button;
    }

    /// <summary>合体按钮的一段：系统无底色按钮，连续胶囊背景由所在的单一玻璃容器承担。</summary>
    /// <param name="symbolName">SF Symbols 名称。</param>
    /// <param name="label">读屏名称。</param>
    /// <param name="action">点按回调。</param>
    /// <returns>配置完成的按钮。</returns>
    internal static UIButton Segment(string symbolName, string label, Action? action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = iOSSymbols.Symbol(symbolName, DesignTokens.ToolbarCommandGlyphFontSize);
        configuration.BaseForegroundColor = UIColor.Label;
        var button = new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            PointerInteractionEnabled = true,
            AccessibilityLabel = label,
        };
        if (action != null) button.TouchUpInside += (_, _) => action();
        EnableHighlight(button);
        return button;
    }

    /// <summary>使用系统提供的按钮高亮指针效果，形状继续由按钮与系统建议决定。</summary>
    internal static void EnableHighlight(UIButton button)
    {
        button.PointerInteractionEnabled = true;
        button.PointerStyleProvider = (sender, _, _) =>
        {
            var preview = new UITargetedPreview(sender);
            var rect = PointerRect(sender, preview);
            var shape = UIPointerShape.CreateRounded(rect, (nfloat)(Math.Min(rect.Width, rect.Height) / 2));
            return UIPointerStyle.Create(UIPointerHighlightEffect.Create(preview), shape);
        };
    }

    /// <summary>指针形状使用预览容器的坐标系，并在按钮实际矩形内缩 4 点。</summary>
    internal static CGRect PointerRect(UIButton button, UITargetedPreview preview)
        => button.ConvertRectToView(button.Bounds, preview.Target.Container).Inset(4, 4);

    /// <summary>改写按钮的前景强调态（雾天增强开关），不改变几何与命中区。</summary>
    /// <param name="button">目标按钮。</param>
    /// <param name="emphasized">是否用系统强调色。</param>
    /// <param name="accent">系统强调色（取自视图层继承的 tint，绑定未暴露 <c>UIColor.tintColor</c>）。</param>
    internal static void SetEmphasized(UIButton button, bool emphasized, UIColor accent)
    {
        var configuration = button.Configuration;
        if (configuration == null) return;
        configuration.BaseForegroundColor = emphasized ? accent : UIColor.Label;
        button.Configuration = configuration;
    }
}

/// <summary>
/// 一组相邻玻璃控件的共同容器：系统 <see cref="UIGlassContainerEffect"/> 让内容区里的玻璃元素在同一取样区域内渲染，
/// 按压放大或彼此靠近到融合距离以内时按系统液态效果相连，静止时各自独立。容器铺满宿主但自身不拦截触摸，
/// 只把命中交给内容区里的控件。
/// </summary>
internal sealed class iOSGlassContainer : UIVisualEffectView
{
    /// <summary>建立玻璃容器。</summary>
    /// <param name="spacing">相邻玻璃元素开始融合的距离。</param>
    internal iOSGlassContainer(double spacing) : base(new UIGlassContainerEffect { Spacing = (nfloat)spacing })
    {
        BackgroundColor = UIColor.Clear;
    }

    /// <summary>只命中内容区里的控件；落在控件之外时穿透到下层（主图、胶片条）。</summary>
    /// <param name="point">本视图坐标中的触点。</param>
    /// <param name="uievent">触摸事件。</param>
    /// <returns>命中的控件，或 null。</returns>
    public override UIView? HitTest(CGPoint point, UIEvent? uievent)
    {
        if (Hidden || !UserInteractionEnabled || Alpha < 0.01f) return null;
        var children = ContentView.Subviews;
        for (int index = children.Length - 1; index >= 0; index--)
        {
            var child = children[index];
            var hit = child.HitTest(child.ConvertPointFromView(point, this), uievent);
            if (hit != null) return hit;
        }
        return null;
    }
}
