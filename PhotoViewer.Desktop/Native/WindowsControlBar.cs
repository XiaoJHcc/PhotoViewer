using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生控制栏:EXIF 信息块、评分、命令三个整体分组,按可用空间整组换行/换列并居中
/// (契约 TOOLBAR-01)。分组内顺序、显隐与快捷键绑定全部来自共享设置,不在壳里硬编码。
/// </summary>
internal sealed class WindowsControlBar : Grid
{
    private readonly MainViewModel _main;
    private readonly Action<string> _execute;
    private readonly Border _frame;
    private readonly WindowsWrapPanel _groups;
    private readonly Border _exifGroup;
    private readonly Border _ratingGroup;
    private readonly StackPanel _exifStack;
    private readonly StackPanel _ratingStack;
    private readonly StackPanel _commandStack;
    private readonly List<Button> _starButtons = new();
    private bool _refreshPending;
    private bool _isRowLayout = true;

    /// <summary>
    /// 创建控制栏。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="execute">命令执行入口(原生壳的命令分发)</param>
    public WindowsControlBar(MainViewModel main, Action<string> execute)
    {
        _main = main;
        _execute = execute;

        _exifStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        _exifGroup = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BlockBackground),
            CornerRadius = new CornerRadius(6),
            Child = _exifStack,
        };

        _ratingStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        _ratingGroup = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BlockBackground),
            CornerRadius = new CornerRadius(6),
            Child = _ratingStack,
        };
        for (var star = 0; star <= 5; star++)
        {
            var value = star;
            var button = CreateGlyphButton(WindowsLayout.RatingButtonSize, WindowsLayout.RatingGlyphFontSize, null);
            button.Click += (_, _) => RateCurrent(value);
            _starButtons.Add(button);
            _ratingStack.Children.Add(button);
        }

        _commandStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = WindowsLayout.CommandSpacing };
        _groups = new WindowsWrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = WindowsLayout.ControlBarSpacing,
            LineSpacing = WindowsLayout.ControlBarSpacing,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _exifGroup, _ratingGroup, _commandStack },
        };

        _frame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.Divider),
            BorderThickness = new Thickness(0, WindowsLayout.DividerThickness, 0, 0),
            Padding = new Thickness(WindowsLayout.ControlBarPadding),
            Child = _groups,
        };
        Children.Add(_frame);

        _main.PropertyChanged += OnMainChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.Settings.PropertyChanged += OnControlChanged;
        Refresh();
    }

    /// <summary>
    /// 切换主轴方向(上下布局三组横排、控制栏带上分隔线;左右布局三组竖排、控制栏带左分隔线)。
    /// </summary>
    /// <param name="isRowLayout">是否为上下布局</param>
    public void SetOrientation(bool isRowLayout)
    {
        _frame.BorderThickness = isRowLayout
            ? new Thickness(0, WindowsLayout.DividerThickness, 0, 0)
            : new Thickness(WindowsLayout.DividerThickness, 0, 0, 0);

        if (_isRowLayout == isRowLayout)
        {
            return;
        }

        _isRowLayout = isRowLayout;
        _groups.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _exifStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _commandStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _ratingStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        ApplyRatingOrder();
        ApplyGroupMargins();
    }

    /// <summary>
    /// 释放订阅。
    /// </summary>
    public void Detach()
    {
        _main.PropertyChanged -= OnMainChanged;
        _main.ControlVM.PropertyChanged -= OnControlChanged;
        _main.Settings.PropertyChanged -= OnControlChanged;
    }

    /// <summary>
    /// 重建控制栏内容(EXIF 项与命令按钮)。
    /// </summary>
    private void Refresh()
    {
        RefreshExif();
        RefreshCommands();
        RefreshRating();
        ApplyRatingOrder();
        ApplyGroupMargins();
    }

    /// <summary>重建 EXIF 信息块(保留标签与强调值的信息层级与完整内容 tooltip)。</summary>
    private void RefreshExif()
    {
        _exifStack.Children.Clear();
        foreach (var entry in _main.ControlVM.EnabledExifItems)
        {
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                FontSize = WindowsLayout.ExifLabelFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.ExifLabelForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = entry.DisplayValue,
                FontSize = WindowsLayout.ExifValueFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WindowsLayout.ExifValueForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var block = new Border
            {
                Width = WindowsLayout.ExifBlockWidth,
                Height = WindowsLayout.ExifBlockHeight,
                Background = new SolidColorBrush(WindowsLayout.BlockBackground),
                CornerRadius = new CornerRadius(6),
                Child = stack,
            };
            ToolTipService.SetToolTip(block, $"{entry.DisplayName}:{entry.DisplayValue}");
            _exifStack.Children.Add(block);
        }

        _exifGroup.Visibility = _exifStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>重建命令按钮:显隐与顺序由 Hotkeys.IsDisplay 与用户排序驱动。</summary>
    private void RefreshCommands()
    {
        _commandStack.Children.Clear();
        foreach (var item in _main.ControlVM.EnabledControls)
        {
            var glyph = WindowsIcons.ForCommand(item.Command);
            if (glyph == null)
            {
                continue;
            }

            _commandStack.Children.Add(CreateCommandButton(item, glyph));
        }

        _commandStack.Visibility = _commandStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>创建一个命令按钮;评分命令按星数绘制星形(TOOLBAR-01)。</summary>
    private Button CreateCommandButton(SettingsViewModel.HotkeyItem item, string glyph)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (WindowsIcons.TryGetRatingStars(item.Command, out var stars))
        {
            if (stars == 0)
            {
                content.Children.Add(CreateGlyph(WindowsIcons.ClearGlyph, 12, WindowsLayout.StarOff));
            }
            else
            {
                for (var i = 0; i < stars; i++)
                {
                    content.Children.Add(CreateGlyph(WindowsIcons.StarGlyph, 10, WindowsLayout.StarOn));
                }
            }
        }
        else
        {
            content.Children.Add(CreateGlyph(glyph, WindowsLayout.CommandGlyphFontSize, null));
        }

        var button = new Button
        {
            Width = WindowsLayout.CommandButtonSize,
            Height = WindowsLayout.CommandButtonSize,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(WindowsLayout.CommandButtonBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.CommandButtonBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Content = content,
        };
        ToolTipService.SetToolTip(button, BuildTooltip(item));
        button.Click += (_, _) => _execute(item.Command);
        return button;
    }

    /// <summary>拼装 tooltip:命令名 + 主/次绑定实际按键。</summary>
    private static string BuildTooltip(SettingsViewModel.HotkeyItem item)
    {
        var parts = new List<string> { item.Name };
        if (item.PrimaryHotkey != null)
        {
            parts.Add(item.PrimaryHotkey.ToDisplayString());
        }

        if (item.SecondaryHotkey != null)
        {
            parts.Add(item.SecondaryHotkey.ToDisplayString());
        }

        return string.Join(" · ", parts);
    }

    /// <summary>按当前照片评分刷新星色。</summary>
    private void RefreshRating()
    {
        var rating = _main.CurrentFile?.Rating ?? 0;
        for (var star = 0; star <= 5; star++)
        {
            var button = _starButtons[star];
            var on = star > 0 && rating >= star;
            var tint = star > 0 && on ? WindowsLayout.StarOn : WindowsLayout.StarOff;
            var glyph = star == 0 ? WindowsIcons.ClearGlyph : on ? WindowsIcons.StarGlyph : "\uE734";
            var size = star == 0 ? 12 : WindowsLayout.RatingGlyphFontSize;
            button.Opacity = _main.ControlVM.StarOpacity;
            button.Content = CreateGlyph(glyph, size, tint);
        }

        _ratingGroup.Visibility = _main.ControlVM.ShowRating ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>评分按钮的排布方向:上下布局自下而上 0→5。</summary>
    private void ApplyRatingOrder()
    {
        _ratingStack.Children.Clear();
        if (_isRowLayout)
        {
            foreach (var button in _starButtons)
            {
                _ratingStack.Children.Add(button);
            }

            return;
        }

        for (var index = _starButtons.Count - 1; index >= 0; index--)
        {
            _ratingStack.Children.Add(_starButtons[index]);
        }
    }

    /// <summary>分组在主轴两端的留白(EXIF 4、评分 8、命令 8)。</summary>
    private void ApplyGroupMargins()
    {
        _exifGroup.Margin = _isRowLayout
            ? new Thickness(WindowsLayout.ExifBlockMargin / 2, 0, WindowsLayout.ExifBlockMargin / 2, 0)
            : new Thickness(0, WindowsLayout.ExifBlockMargin / 2, 0, WindowsLayout.ExifBlockMargin / 2);
        _ratingGroup.Margin = _isRowLayout
            ? new Thickness(0, 0, WindowsLayout.RatingGroupMargin, 0)
            : new Thickness(0, WindowsLayout.RatingGroupMargin, 0, 0);
        _commandStack.Margin = _isRowLayout
            ? new Thickness(0, 0, WindowsLayout.CommandSpacing, 0)
            : new Thickness(0, WindowsLayout.CommandSpacing, 0, 0);
    }

    /// <summary>创建设置字体的字形文本。</summary>
    private static TextBlock CreateGlyph(string glyph, double fontSize, Color? tint) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
        FontSize = fontSize,
        Foreground = new SolidColorBrush(tint ?? WindowsLayout.ExifValueForeground),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>创建方形字形按钮。</summary>
    private static Button CreateGlyphButton(double size, double fontSize, Color? tint) => new()
    {
        Width = size,
        Height = size,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
        BorderThickness = new Thickness(0),
        Content = CreateGlyph(WindowsIcons.StarGlyph, fontSize, tint),
    };

    /// <summary>按评分设置当前照片。</summary>
    private async void RateCurrent(int rating)
    {
        try
        {
            await _main.SetRatingAsync(_main.CurrentFile, rating);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail($"设置评分失败(rating={rating})", ex);
        }
    }

    /// <summary>主视图模型变化:切换照片时刷新星色。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            ScheduleRefresh();
        }
    }

    /// <summary>控制栏相关设置变化:合并到一次 dispatcher turn 内重建。</summary>
    private void OnControlChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.EnabledExifItems)
            or nameof(SettingsViewModel.Hotkeys)
            or nameof(SettingsViewModel.ShowRating)
            or nameof(ControlViewModel.EnabledExifItems)
            or nameof(ControlViewModel.EnabledControls)
            or nameof(ControlViewModel.StarOpacity))
        {
            ScheduleRefresh();
        }
    }

    /// <summary>合并刷新:一轮内多次变化只重建一次。</summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        UiDispatcher.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, UiPriority.Background);
    }
}
