using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主屏小型控件的状态外观。
/// 主屏按钮仅校准常态填充，保留 WinUI 描边与系统状态（TOOLBAR-10、SHELL-12）；
/// 另承载透明方块按钮、动作按钮、内容禁用强调，
/// 以及星级槽位的槽内状态层（TOOLBAR-03：常态与禁用透明，悬停/按下只画内切正圆）。
/// </summary>
internal static class WindowsControlStyles
{
    /// <summary>WinUI 悬停状态层（胶囊内分段的悬停底色）。</summary>
    private const string HoverStateLayerKey = "SubtleFillColorSecondaryBrush";

    /// <summary>WinUI 按下状态层。</summary>
    private const string PressedStateLayerKey = "SubtleFillColorTertiaryBrush";

    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    /// <summary>
    /// 取 WinUI 主题画刷（深色主题随 <c>XamlControlsResources</c> 一并加载）。
    /// 资源缺失时退回应用自有语义色，避免整条控制栏因一个主题键缺失而不可见。
    /// </summary>
    /// <param name="key">主题资源键。</param>
    /// <param name="fallback">资源缺失时的回退语义色。</param>
    /// <returns>主题画刷或回退画刷。</returns>
    internal static Brush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current is { } application
            && application.Resources.TryGetValue(key, out var value)
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    internal static void ApplyMainButtonFill(ButtonBase button)
    {
        var fill = new SolidColorBrush(WindowsLayout.MainButtonFill);
        button.Resources[button is ToggleButton ? "ToggleButtonBackground" : "ButtonBackground"] = fill;
        button.Background = fill;
    }

    /// <summary>透明底方形图标按钮（评分星）：悬停 #333、按下 #3A3A3A，常态与禁用无底色。</summary>
    /// <param name="button">评分按钮。</param>
    public static void ApplyTransparentSquareButton(Button button) => ApplyButtonStates(
        button,
        Transparent,
        Transparent,
        WindowsLayout.RatingHoverBackground,
        Transparent,
        WindowsLayout.RatingPressedBackground,
        Transparent,
        WindowsLayout.ExifValueForeground,
        WindowsLayout.ExifValueForeground);

    /// <summary>文字动作按钮（相似聚类动作区）：常态 #2A2A2A/#555、悬停 #3A3A3A/#666、按下 #333/#555。</summary>
    /// <param name="button">动作按钮。</param>
    public static void ApplyActionButton(Button button) => ApplyButtonStates(
        button,
        WindowsLayout.SimilarityActionBackground,
        WindowsLayout.SimilarityActionBorder,
        WindowsLayout.SimilarityActionHover,
        WindowsLayout.CommandButtonHoverBorder,
        WindowsLayout.SimilarityActionPressed,
        WindowsLayout.SimilarityActionBorder,
        WindowsLayout.SimilarityActionForeground,
        WindowsLayout.ExifValueForeground);

    /// <summary>提示条内文字动作（STATE-05）：透明底、无描边，悬停/按下取评分槽位同一状态层色，文字保持提示条前景色。</summary>
    /// <param name="button">文字动作按钮。</param>
    public static void ApplyToastTextAction(Button button) => ApplyButtonStates(
        button,
        Transparent,
        Transparent,
        WindowsLayout.RatingHoverBackground,
        Transparent,
        WindowsLayout.RatingPressedBackground,
        Transparent,
        WindowsLayout.StatusToastForeground,
        WindowsLayout.StatusToastForeground);

    /// <summary>
    /// 星级槽位（TOOLBAR-03、RATING-07）：按钮自身在常态、悬停、按下与禁用下都透明且无描边，
    /// 悬停与按下只由槽位内的内切正圆状态层表达，禁用与常态都不画状态层。
    /// 不播放过渡动画，连续划过槽位时不闪。
    /// </summary>
    /// <param name="button">槽位按钮。</param>
    /// <param name="stateLayer">槽位内居中的正圆状态层（直径取槽位短边，不参与命中）。</param>
    public static void ApplyRatingSlot(Button button, Ellipse stateLayer)
    {
        // 只设 Background 不够：WinUI 模板在悬停/按下/禁用时回到主题键，四态都要显式透明。
        SetBrush(button, "ButtonBackground", Transparent);
        SetBrush(button, "ButtonBackgroundPointerOver", Transparent);
        SetBrush(button, "ButtonBackgroundPressed", Transparent);
        SetBrush(button, "ButtonBackgroundDisabled", Transparent);
        SetBrush(button, "ButtonBorderBrush", Transparent);
        SetBrush(button, "ButtonBorderBrushPointerOver", Transparent);
        SetBrush(button, "ButtonBorderBrushPressed", Transparent);
        SetBrush(button, "ButtonBorderBrushDisabled", Transparent);
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);

        var hover = ThemeBrush(HoverStateLayerKey, WindowsLayout.RatingHoverBackground);
        var pressed = ThemeBrush(PressedStateLayerKey, WindowsLayout.RatingPressedBackground);
        bool over = false;
        bool down = false;

        void Apply()
        {
            stateLayer.Fill = !button.IsEnabled ? null : down ? pressed : over ? hover : null;
        }

        // 槽位按钮的按压与移出都由原生控件与指针捕获驱动，这里只跟状态，不管手势语义。
        button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) =>
        {
            over = true;
            Apply();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) =>
        {
            over = false;
            down = false;
            Apply();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            down = true;
            Apply();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            down = false;
            Apply();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            down = false;
            Apply();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) =>
        {
            over = false;
            down = false;
            Apply();
        }), handledEventsToo: true);
        button.IsEnabledChanged += (_, _) =>
        {
            if (!button.IsEnabled)
            {
                over = false;
                down = false;
            }

            Apply();
        };
        Apply();
    }

    /// <summary>按可用状态调整控件内容：禁用只降内容不透明度，控件底色与几何不变。</summary>
    /// <param name="control">按钮。</param>
    /// <param name="enabled">是否可用。</param>
    public static void SetContentEnabled(ContentControl control, bool enabled)
    {
        control.IsEnabled = enabled;
        if (control.Content is UIElement content)
        {
            content.Opacity = enabled ? 1 : WindowsLayout.DisabledContentAlpha;
        }
    }

    private static void ApplyButtonStates(
        Button button,
        Color background,
        Color border,
        Color hoverBackground,
        Color hoverBorder,
        Color pressedBackground,
        Color pressedBorder,
        Color foreground,
        Color hoverForeground)
    {
        SetBrush(button, "ButtonBackground", background);
        SetBrush(button, "ButtonBackgroundPointerOver", hoverBackground);
        SetBrush(button, "ButtonBackgroundPressed", pressedBackground);
        // 禁用不换底色：系统默认的禁用灰底会让一排按钮在无照片时看起来是另一种控件。
        SetBrush(button, "ButtonBackgroundDisabled", background);

        SetBrush(button, "ButtonBorderBrush", border);
        SetBrush(button, "ButtonBorderBrushPointerOver", hoverBorder);
        SetBrush(button, "ButtonBorderBrushPressed", pressedBorder);
        SetBrush(button, "ButtonBorderBrushDisabled", border);

        SetBrush(button, "ButtonForeground", foreground);
        SetBrush(button, "ButtonForegroundPointerOver", hoverForeground);
        SetBrush(button, "ButtonForegroundPressed", hoverForeground);
        SetBrush(button, "ButtonForegroundDisabled", foreground);
    }

    private static void SetBrush(FrameworkElement target, string key, Color color)
        => target.Resources[key] = new SolidColorBrush(color);
}
