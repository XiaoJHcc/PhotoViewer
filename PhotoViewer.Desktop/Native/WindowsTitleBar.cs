using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 隐藏式标题栏:标题文本 + 最小化/最大化/全屏/关闭四个自绘按钮,
/// 指针进入顶部热区才显示、移开或窗口失活即隐藏(契约 SHELL-02,对齐旧壳
/// <c>MainWindowForWindows.axaml.cs</c> 的 ShowTitleBar/HideTitleBar)。
/// <para>
/// 只负责"看得见的部分":整个覆盖层 <see cref="Root"/> 的背景为 null(不可命中),
/// 只有按钮吃掉指针;拖动区不在这里画元素 —— 由 <see cref="WindowsMainWindow"/> 把区域登记成
/// 系统 caption(<c>InputNonClientPointerSource</c>),隐藏时清空,所以标题栏不可见时顶部不可拖动。
/// </para>
/// </summary>
internal sealed class WindowsTitleBar
{
    private readonly TextBlock _title;
    private readonly StackPanel _buttons;
    private readonly Grid _root;
    private bool _visible;

    /// <summary>
    /// 构建标题栏覆盖层。
    /// </summary>
    /// <param name="title">标题文本</param>
    /// <param name="minimize">最小化</param>
    /// <param name="toggleMaximize">最大化 / 还原</param>
    /// <param name="toggleFullScreen">全屏 / 退出全屏</param>
    /// <param name="close">关闭</param>
    public WindowsTitleBar(string title, Action minimize, Action toggleMaximize, Action toggleFullScreen, Action close)
    {
        _title = new TextBlock
        {
            Text = title,
            FontSize = WindowsLayout.TitleTextFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.TitleTextForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(WindowsLayout.TitleTextLeftInset, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, WindowsLayout.CaptionButtonsRightInset, 0),
            Visibility = Visibility.Collapsed,
        };
        _buttons.Children.Add(CreateButton("\uE921", "最小化", null, minimize));
        _buttons.Children.Add(CreateButton("\uE922", "最大化", null, toggleMaximize));
        _buttons.Children.Add(CreateButton("\uE740", "全屏", null, toggleFullScreen));
        _buttons.Children.Add(CreateButton("\uE8BB", "关闭", WindowsLayout.CaptionCloseHover, close));

        // Background 保持 null:覆盖层本身不吃指针,只有按钮是可命中的目标,
        // 顶部其余位置交给系统 caption 区域处理拖动。
        _root = new Grid
        {
            Height = WindowsLayout.CaptionHeight,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_title);
        _root.Children.Add(_buttons);
    }

    /// <summary>覆盖层根元素(加到窗口内容最上层)。</summary>
    public UIElement Root => _root;

    /// <summary>标题栏当前是否可见(拖动区是否登记)。</summary>
    public bool IsVisible => _visible;

    /// <summary>标题栏按钮组占位宽(含右边距),用于计算拖动区右边界。</summary>
    public double ButtonsWidth =>
        (WindowsLayout.CaptionButtonWidth * _buttons.Children.Count) + (WindowsLayout.CaptionButtonsRightInset * 2);

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
        _buttons.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前布局摆放标题文本:左右布局下文件栏占住顶部,标题跟在文件栏之后;上下布局标题从窗口左边开始。
    /// </summary>
    /// <param name="leftInset">标题文本宿主左边界(DIP)</param>
    public void SetTitleInset(double leftInset) => _title.Margin = new Thickness(leftInset + WindowsLayout.TitleTextLeftInset, 0, 0, 0);

    /// <summary>创建标题栏按钮。</summary>
    /// <param name="glyph">字形码位</param>
    /// <param name="tooltip">悬停提示</param>
    /// <param name="hoverBackground">悬停底色;null 表示用默认浅色</param>
    /// <param name="action">点击动作</param>
    private static Button CreateButton(string glyph, string tooltip, Color? hoverBackground, Action action)
    {
        var button = new Button
        {
            Width = WindowsLayout.CaptionButtonWidth,
            Height = WindowsLayout.CaptionButtonHeight,
            Margin = new Thickness(
                WindowsLayout.CaptionButtonMarginX,
                WindowsLayout.CaptionButtonMarginY,
                WindowsLayout.CaptionButtonMarginX,
                WindowsLayout.CaptionButtonMarginY),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(WindowsLayout.CaptionButtonBackground),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
                FontSize = WindowsLayout.CaptionButtonFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.CaptionButtonForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTipService.SetToolTip(button, tooltip);

        // WinUI 的 Button 模板用主题画刷覆盖 Background,悬停/按下必须改写这几个键才不被默认亮色顶掉
        var hover = hoverBackground ?? WindowsLayout.CaptionButtonHover;
        button.Resources["ButtonBackground"] = new SolidColorBrush(WindowsLayout.CaptionButtonBackground);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(hover);
        button.Resources["ButtonBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        button.Click += (_, _) => action();
        return button;
    }
}
