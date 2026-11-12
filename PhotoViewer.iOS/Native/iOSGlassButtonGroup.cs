using CoreGraphics;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 手机形态的合体玻璃按钮：一个系统玻璃材质容器内按横轴或纵轴排布若干方形按钮段，
/// 每段边长取 <see cref="DesignTokens.ShellGlassButtonSize"/>（命中区不小于 44 点）。
/// 段数、显隐与方向由宿主决定，本类只把可见段依次铺进内容区；胶囊外形交给系统
/// <see cref="UIGlassEffect"/> 与 <see cref="UICornerConfiguration"/>，不自绘圆角或阴影。
/// </summary>
internal sealed class iOSGlassButtonGroup : UIVisualEffectView
{
    private readonly List<UIButton> _buttons = [];
    private bool _vertical;
    private bool _mirrored;

    /// <summary>各段的按钮实例（顺序即排布顺序，隐藏段跳过排布）。</summary>
    internal IReadOnlyList<UIButton> Buttons => _buttons;

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

    /// <summary>建立玻璃容器：交互式玻璃效果 + 系统胶囊外形。</summary>
    internal iOSGlassButtonGroup() : base(new UIGlassEffect { Interactive = true })
    {
        CornerConfiguration = UICornerConfiguration.CreateCapsule();
        BackgroundColor = UIColor.Clear;
    }

    /// <summary>追加一个按钮段。</summary>
    /// <param name="symbolName">SF Symbols 名称。</param>
    /// <param name="label">读屏名称。</param>
    /// <param name="action">点按回调。</param>
    /// <returns>该段按钮，供宿主设显隐与强调色。</returns>
    internal UIButton Add(string symbolName, string label, Action? action)
    {
        var button = iOSGlassButtons.Segment(symbolName, label, action);
        ContentView.AddSubview(button);
        _buttons.Add(button);
        SetNeedsLayout();
        return button;
    }

    /// <summary>按方向把可见段依次铺满内容区，每段保持设计令牌的方形边长。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        double size = DesignTokens.ShellGlassButtonSize;
        int visible = _buttons.Count(button => !button.Hidden);
        int index = 0;
        foreach (var button in _buttons)
        {
            if (button.Hidden) continue;
            int slot = Mirrored ? visible - 1 - index : index;
            button.Frame = Vertical
                ? new CGRect(0, slot * size, Bounds.Width, size)
                : new CGRect(slot * size, 0, size, Bounds.Height);
            index++;
        }
    }
}

/// <summary>玻璃按钮工厂：单圆按钮取系统玻璃按钮配置，合体按钮的段取无底色配置（玻璃由所在容器承担）。</summary>
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
            AccessibilityLabel = label,
        };
        if (action != null) button.TouchUpInside += (_, _) => action();
        return button;
    }

    /// <summary>合体按钮的一段：无底色配置，背景与外形由玻璃容器统一承担。</summary>
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
            AccessibilityLabel = label,
        };
        if (action != null) button.TouchUpInside += (_, _) => action();
        return button;
    }

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
