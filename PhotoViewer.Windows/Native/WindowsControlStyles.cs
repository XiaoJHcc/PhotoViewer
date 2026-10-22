using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 主屏小型控件的统一状态外观（TOOLBAR-10）。
/// 悬停/按下只改底色、描边与前景；禁用不改底色与描边，只把内容降到
/// <see cref="WindowsLayout.DisabledContentAlpha"/>，避免 WinUI 默认模板的禁用灰底与悬停闪白。
/// </summary>
internal static class WindowsControlStyles
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    /// <summary>命令按钮：常态 #333/#555、悬停 #3D3D3D/#666、按下 #2A2A2A/#555。</summary>
    /// <param name="button">命令按钮。</param>
    public static void ApplyCommandButton(Button button) => ApplyButtonStates(
        button,
        WindowsLayout.CommandButtonBackground,
        WindowsLayout.CommandButtonBorder,
        WindowsLayout.CommandButtonHoverBackground,
        WindowsLayout.CommandButtonHoverBorder,
        WindowsLayout.CommandButtonPressedBackground,
        WindowsLayout.CommandButtonBorder,
        WindowsLayout.ExifValueForeground,
        WindowsLayout.ExifValueForeground);

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

    /// <summary>
    /// 相似聚类开关：四种勾选态 × 悬停/按下各自的前景与底色都显式给出，
    /// 避免模板在悬停/按下时回落到系统主题色（文字发黑）或系统蓝底。
    /// </summary>
    /// <param name="toggle">相似聚类开关。</param>
    public static void ApplySimilarityToggle(ToggleButton toggle)
    {
        SetBrush(toggle, "ToggleButtonBackground", Transparent);
        SetBrush(toggle, "ToggleButtonBackgroundPointerOver", WindowsLayout.SimilarityToggleHover);
        SetBrush(toggle, "ToggleButtonBackgroundPressed", WindowsLayout.CommandButtonPressedBackground);
        SetBrush(toggle, "ToggleButtonBackgroundDisabled", Transparent);
        SetBrush(toggle, "ToggleButtonBackgroundChecked", WindowsLayout.SimilarityToggleCheckedBackground);
        SetBrush(toggle, "ToggleButtonBackgroundCheckedPointerOver", WindowsLayout.SimilarityToggleCheckedHover);
        SetBrush(toggle, "ToggleButtonBackgroundCheckedPressed", WindowsLayout.SimilarityToggleCheckedHover);
        SetBrush(toggle, "ToggleButtonBackgroundCheckedDisabled", WindowsLayout.SimilarityToggleCheckedBackground);

        SetBrush(toggle, "ToggleButtonBorderBrush", WindowsLayout.SimilarityToggleBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushPointerOver", WindowsLayout.CommandButtonHoverBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushPressed", WindowsLayout.SimilarityToggleBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushDisabled", WindowsLayout.SimilarityToggleBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushChecked", WindowsLayout.SimilarityToggleCheckedBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushCheckedPointerOver", WindowsLayout.SimilarityToggleCheckedBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushCheckedPressed", WindowsLayout.SimilarityToggleCheckedBorder);
        SetBrush(toggle, "ToggleButtonBorderBrushCheckedDisabled", WindowsLayout.SimilarityToggleCheckedBorder);

        SetBrush(toggle, "ToggleButtonForeground", WindowsLayout.SimilarityToggleForeground);
        SetBrush(toggle, "ToggleButtonForegroundPointerOver", WindowsLayout.SimilarityToggleHoverForeground);
        SetBrush(toggle, "ToggleButtonForegroundPressed", WindowsLayout.SimilarityToggleHoverForeground);
        SetBrush(toggle, "ToggleButtonForegroundDisabled", WindowsLayout.SimilarityToggleForeground);
        SetBrush(toggle, "ToggleButtonForegroundChecked", WindowsLayout.SimilarityToggleCheckedForeground);
        SetBrush(toggle, "ToggleButtonForegroundCheckedPointerOver", WindowsLayout.SimilarityToggleCheckedHoverForeground);
        SetBrush(toggle, "ToggleButtonForegroundCheckedPressed", WindowsLayout.SimilarityToggleCheckedHoverForeground);
        SetBrush(toggle, "ToggleButtonForegroundCheckedDisabled", WindowsLayout.SimilarityToggleCheckedForeground);
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
