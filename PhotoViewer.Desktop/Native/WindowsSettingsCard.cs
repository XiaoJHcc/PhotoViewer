using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 设置页行原语:页面标题、分组标题、卡片、开关行、滑块行、提示与图标按钮。
/// 统一走 <see cref="WindowsLayout"/> 的设置 token,避免每页各写一套边距/字号。
/// </summary>
internal static class WindowsSettingsCard
{
    /// <summary>页面标题(Win11 Title 28/36)。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>标题控件</returns>
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsPageTitleFontSize,
        LineHeight = WindowsLayout.SettingsPageTitleLineHeight,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush(WindowsLayout.CardNameForeground),
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>分组标题(Win11 BodyStrong 14/20)。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>分组标题控件</returns>
    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsSectionHeaderFontSize,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush(WindowsLayout.CardNameForeground),
        Margin = new Thickness(0, WindowsLayout.SettingsSectionGap, 0, 8),
    };

    /// <summary>卡片外壳:深色底 + 1px 描边 + 4px 圆角。</summary>
    /// <param name="content">卡片内容</param>
    /// <param name="padding">内边距;null 用默认</param>
    /// <returns>卡片边框</returns>
    public static Border Card(UIElement content, Thickness? padding = null) => new()
    {
        Background = Brush(WindowsLayout.BlockBackground),
        BorderBrush = Brush(WindowsLayout.Divider),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(WindowsLayout.SettingsCardCornerRadius),
        MinHeight = WindowsLayout.SettingsCardMinHeight,
        Padding = padding ?? new Thickness(
            WindowsLayout.SettingsCardPaddingX,
            WindowsLayout.SettingsCardPaddingY,
            WindowsLayout.SettingsCardPaddingX,
            WindowsLayout.SettingsCardPaddingY),
        Child = content,
    };

    /// <summary>左标题/描述 + 右控件的设置行。</summary>
    /// <param name="header">行标题</param>
    /// <param name="description">行描述;null 表示单行</param>
    /// <param name="control">右侧控件;null 表示纯文本行</param>
    /// <returns>卡片</returns>
    public static Border Row(string header, string? description, FrameworkElement? control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(RowTitle(header));
        if (!string.IsNullOrEmpty(description))
        {
            text.Children.Add(RowDescription(description));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);

        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(WindowsLayout.SettingsControlGap, 0, 0, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        return Card(grid);
    }

    /// <summary>整卡可点的开关行;点文字区域等同切换开关。</summary>
    /// <param name="header">行标题</param>
    /// <param name="description">行描述;null 表示单行</param>
    /// <param name="toggle">开关</param>
    /// <returns>卡片</returns>
    public static Border ToggleRow(string header, string? description, ToggleSwitch toggle)    {
        toggle.OnContent = string.Empty;
        toggle.OffContent = string.Empty;
        toggle.MinWidth = 0;
        AutomationProperties.SetName(toggle, header);

        var card = Row(header, description, toggle);
        card.Tapped += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && IsInside(source, toggle))
            {
                return;
            }

            toggle.IsOn = !toggle.IsOn;
            args.Handled = true;
        };
        return card;
    }

    /// <summary>标题 + 右侧数值 + 下方滑块的设置行。</summary>
    /// <param name="header">行标题</param>
    /// <param name="slider">滑块</param>
    /// <param name="valueText">右侧数值文本</param>
    /// <returns>卡片</returns>
    public static Border SliderRow(string header, Slider slider, TextBlock valueText)
    {
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(RowTitle(header));

        valueText.Foreground = Brush(WindowsLayout.ExifLabelForeground);
        valueText.VerticalAlignment = VerticalAlignment.Center;
        valueText.MinWidth = WindowsLayout.SettingsSliderValueWidth;
        valueText.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(valueText, 1);
        headerRow.Children.Add(valueText);

        slider.Margin = new Thickness(0, 8, 0, 0);
        var content = new StackPanel();
        content.Children.Add(headerRow);
        content.Children.Add(slider);
        return Card(content);
    }

    /// <summary>提示文本;按语义级别取色。</summary>
    /// <param name="text">提示文本</param>
    /// <param name="level">语义级别</param>
    /// <returns>提示控件</returns>
    public static TextBlock Hint(string text, PerfInfoLevel level) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsRowDescriptionFontSize,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(LevelColor(level)),
        Margin = new Thickness(WindowsLayout.SettingsCardPaddingX, 4, WindowsLayout.SettingsCardPaddingX, 0),
    };

    /// <summary>行标题。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>标题控件</returns>
    public static TextBlock RowTitle(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsRowTitleFontSize,
        Foreground = Brush(WindowsLayout.CardNameForeground),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>行描述。</summary>
    /// <param name="text">描述文本</param>
    /// <returns>描述控件</returns>
    public static TextBlock RowDescription(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsRowDescriptionFontSize,
        Foreground = Brush(WindowsLayout.ExifLabelForeground),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, WindowsLayout.SettingsCardDescriptionGap, 0, 0),
    };

    /// <summary>行内分隔线。</summary>
    /// <returns>分隔线</returns>
    public static Border RowDivider() => new()
    {
        Height = 1,
        Background = Brush(WindowsLayout.Divider),
        Margin = new Thickness(0, 8, 0, 8),
    };

    /// <summary>列表行尾拖拽把手(WinUI 无原生把手,这里只作视觉锚点)。</summary>
    /// <returns>把手字形</returns>
    public static TextBlock DragHandle() => new()
    {
        Text = "\uE76F",
        FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
        FontSize = 12,
        Foreground = Brush(WindowsLayout.SimilarityCaptionForeground),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(8, 0, 0, 0),
        IsHitTestVisible = false,
    };

    /// <summary>右侧等宽数值文本。</summary>
    /// <param name="text">数值文本</param>
    /// <returns>数值控件</returns>
    public static TextBlock ValueText(string text) => new()
    {
        Text = text,
        FontSize = WindowsLayout.SettingsRowDescriptionFontSize,
        Foreground = Brush(WindowsLayout.ExifLabelForeground),
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        MinWidth = WindowsLayout.SettingsSliderValueWidth,
    };

    /// <summary>透明底的图标按钮(行内删除/添加用)。</summary>
    /// <param name="glyph">Segoe Fluent Icons 字形</param>
    /// <param name="tooltip">悬停提示</param>
    /// <param name="action">点击动作</param>
    /// <returns>按钮</returns>
    public static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = 12,
                Foreground = Brush(WindowsLayout.ExifLabelForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTipService.SetToolTip(button, tooltip);

        var transparent = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        var hover = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = hover;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>按语义级别取提示颜色。</summary>
    /// <param name="level">语义级别</param>
    /// <returns>颜色</returns>
    public static Color LevelColor(PerfInfoLevel level) => level switch
    {
        PerfInfoLevel.Warning => WindowsLayout.SettingsConflictForeground,
        PerfInfoLevel.Error => WindowsLayout.SimilarityErrorForeground,
        _ => WindowsLayout.ExifLabelForeground,
    };

    /// <summary>把颜色包成纯色画刷。</summary>
    /// <param name="color">颜色</param>
    /// <returns>画刷</returns>
    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>判断元素是否位于指定祖先之内(用于整卡点击时避开开关自身)。</summary>
    /// <param name="source">事件源</param>
    /// <param name="ancestor">祖先</param>
    /// <returns>是否位于祖先内</returns>
    private static bool IsInside(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
