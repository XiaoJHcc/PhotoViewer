using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 设置页/工具页共用的 Win11 行原语:滚动页容器、页面/分组标题、设置卡片、开关/滑块/数值行与提示。
/// 颜色、字号、圆角与卡片材质全部取 WinUI 3 主题资源,不再维护第二套灰度。
/// </summary>
internal static class WindowsSettingsCard
{
    /// <summary>设置页/工具页共用的滚动页容器:页面标题 + 内容,MaxWidth 与页边距统一。</summary>
    /// <param name="title">页面标题</param>
    /// <param name="children">页面内容</param>
    /// <returns>自带滚动的页面</returns>
    public static ScrollViewer Page(string title, params UIElement[] children)
    {
        var stack = new StackPanel
        {
            MaxWidth = WindowsLayout.SettingsPageMaxWidth,
            Padding = new Thickness(
                WindowsLayout.SettingsPagePadding,
                WindowsLayout.SettingsPageTopPadding,
                WindowsLayout.SettingsPagePadding,
                WindowsLayout.SettingsPagePadding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        stack.Children.Add(PageTitle(title));
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush(Color.FromArgb(0, 0, 0, 0)),
        };
    }

    /// <summary>页面标题(Win11 Title 28/36)。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>标题控件</returns>
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        Style = Style("TitleTextBlockStyle"),
        Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>分组标题(Win11 BodyStrong 14/20)。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>分组标题控件</returns>
    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Style = Style("BodyStrongTextBlockStyle"),
        Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
        Margin = new Thickness(0, WindowsLayout.SettingsSectionGap, 0, 8),
    };

    /// <summary>Win11 设置卡片:左标题/描述 + 右控件。</summary>
    /// <param name="header">行标题</param>
    /// <param name="description">行描述;null 表示单行</param>
    /// <param name="control">右侧控件;null 表示纯文本行</param>
    /// <returns>设置卡片</returns>
    public static SettingsCard Row(string header, string? description, FrameworkElement? control)
    {
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
        }

        return new SettingsCard
        {
            Header = header,
            Description = description!,
            Content = control,
        };
    }

    /// <summary>整卡可点的开关行;点文字区域等同切换开关。</summary>
    /// <param name="header">行标题</param>
    /// <param name="description">行描述;null 表示单行</param>
    /// <param name="toggle">开关</param>
    /// <returns>设置卡片</returns>
    public static SettingsCard ToggleRow(string header, string? description, ToggleSwitch toggle)
    {
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
    /// <returns>设置卡片</returns>
    public static SettingsCard SliderRow(string header, Slider slider, TextBlock valueText)
    {
        valueText.Style = Style("BodyTextBlockStyle");
        valueText.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        valueText.Margin = new Thickness(0, 0, 0, 8);

        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        slider.MinWidth = 0;

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(valueText);
        Grid.SetRow(slider, 1);
        content.Children.Add(slider);

        return new SettingsCard
        {
            Header = header,
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
    }

    /// <summary>整块内容卡片(排序表、预设列表等);内容自己决定内边距。</summary>
    /// <param name="content">卡片内容</param>
    /// <param name="padding">内边距;null 用 0</param>
    /// <returns>设置卡片</returns>
    public static SettingsCard Card(UIElement content, Thickness? padding = null) => new()
    {
        ContentAlignment = ContentAlignment.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = content,
        Padding = padding ?? new Thickness(0),
    };

    /// <summary>提示文本;按语义级别取 WinUI 系统色。</summary>
    /// <param name="text">提示文本</param>
    /// <param name="level">语义级别</param>
    /// <returns>提示控件</returns>
    public static TextBlock Hint(string text, PerfInfoLevel level) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        TextWrapping = TextWrapping.Wrap,
        Foreground = LevelBrush(level),
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>行标题。</summary>
    /// <param name="text">标题文本</param>
    /// <returns>标题控件</returns>
    public static TextBlock RowTitle(string text) => new()
    {
        Text = text,
        Style = Style("BodyTextBlockStyle"),
        Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>行描述。</summary>
    /// <param name="text">描述文本</param>
    /// <returns>描述控件</returns>
    public static TextBlock RowDescription(string text) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, WindowsLayout.SettingsCardDescriptionGap, 0, 0),
    };

    /// <summary>行内分隔线。</summary>
    /// <returns>分隔线</returns>
    public static Border RowDivider() => new()
    {
        Height = 1,
        Background = ThemeBrush("DividerStrokeColorDefaultBrush"),
        Margin = new Thickness(0, 8, 0, 8),
    };

    /// <summary>列表行尾拖拽把手(WinUI 无原生把手,这里只作视觉锚点)。</summary>
    /// <returns>把手字形</returns>
    public static TextBlock DragHandle() => new()
    {
        Text = "\uE76F",
        FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
        FontSize = 12,
        Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
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
        Style = Style("BodyTextBlockStyle"),
        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
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
        var transparent = ThemeBrush("SubtleFillColorTransparentBrush");
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(WindowsLayout.SettingsCardCornerRadius),
            Background = transparent,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTipService.SetToolTip(button, tooltip);
        ApplySubtleButtonColors(button);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>把图标按钮的主题画刷键设为 WinUI Subtle 状态色。</summary>
    /// <param name="button">按钮</param>
    public static void ApplySubtleButtonColors(Button button)
    {
        var transparent = ThemeBrush("SubtleFillColorTransparentBrush");
        var hover = ThemeBrush("SubtleFillColorSecondaryBrush");
        var pressed = ThemeBrush("SubtleFillColorTertiaryBrush");
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonForeground"] = ThemeBrush("TextFillColorPrimaryBrush");
        button.Resources["ButtonForegroundPointerOver"] = ThemeBrush("TextFillColorPrimaryBrush");
        button.Resources["ButtonForegroundPressed"] = ThemeBrush("TextFillColorSecondaryBrush");
    }

    /// <summary>按语义级别取提示画刷。</summary>
    /// <param name="level">语义级别</param>
    /// <returns>画刷</returns>
    public static Brush LevelBrush(PerfInfoLevel level) => level switch
    {
        PerfInfoLevel.Warning => ThemeBrush("SystemFillColorCautionBrush"),
        PerfInfoLevel.Error => ThemeBrush("SystemFillColorCriticalBrush"),
        _ => ThemeBrush("TextFillColorSecondaryBrush"),
    };

    /// <summary>读取 WinUI 主题画刷。</summary>
    /// <param name="key">资源键</param>
    /// <returns>画刷</returns>
    public static Brush ThemeBrush(string key) => Resource<Brush>(key);

    /// <summary>读取 WinUI 文本样式(页面内容与卡片行共用同一套字号层级)。</summary>
    /// <param name="key">样式键</param>
    /// <returns>文本样式</returns>
    public static Style TextStyle(string key) => Style(key);

    /// <summary>把颜色包成纯色画刷(仅用于内容高亮等应用自有语义)。</summary>
    /// <param name="color">颜色</param>
    /// <returns>纯色画刷</returns>
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

    /// <summary>读取 WinUI 主题资源,缺失即报错(不允许静默回退到第二套颜色/样式)。</summary>
    /// <typeparam name="T">资源类型</typeparam>
    /// <param name="key">资源键</param>
    /// <returns>资源</returns>
    private static T Resource<T>(string key) where T : class
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"WinUI 主题资源缺失: {key}");
    }

    /// <summary>读取 WinUI 文本样式。</summary>
    /// <param name="key">样式键</param>
    /// <returns>样式</returns>
    private static Style Style(string key) => Resource<Style>(key);
}
