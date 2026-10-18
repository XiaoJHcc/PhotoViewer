using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生控制栏:EXIF 信息块、评分、命令三个整体分组,按可用空间整组换行/换列并居中
/// (契约 TOOLBAR-01)。分组内顺序、显隐与快捷键绑定全部来自共享设置,不在壳里硬编码。
/// </summary>
internal sealed class WindowsControlBar : Grid
{
    private readonly MainViewModel _main;
    private readonly Action<string> _execute;
    private readonly Action? _openTools;
    private readonly Border _frame;
    private readonly ScrollViewer _scroll;
    private readonly WindowsWrapPanel _groups;
    private readonly Border _exifGroup;
    private readonly Border _ratingGroup;
    private readonly StackPanel _exifStack;
    private readonly StackPanel _ratingStack;
    private readonly StackPanel _commandStack;
    private readonly List<Border> _exifBlocks = new();
    private readonly List<Button> _starButtons = new();
    private bool _refreshPending;
    private bool _fileRefreshPending;
    private bool _ratingRefreshPending;
    private bool _commandsRefreshPending;
    private bool _isRowLayout = true;
    private bool _restoreScrollPending;
    private double _savedHorizontalOffset;
    private double _savedVerticalOffset;

    /// <summary>
    /// 创建控制栏。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="execute">命令执行入口(原生壳的命令分发)</param>
    /// <param name="openTools">EXIF 信息块点击时打开工具窗口;null 表示不接线</param>
    public WindowsControlBar(MainViewModel main, Action<string> execute, Action? openTools = null)
    {
        _main = main;
        _execute = execute;
        _openTools = openTools;

        var exifMetrics = ControlBarLayout.GetMetrics(ControlBarGroupKind.Exif);
        var ratingMetrics = ControlBarLayout.GetMetrics(ControlBarGroupKind.Rating);
        var commandMetrics = ControlBarLayout.GetMetrics(ControlBarGroupKind.Command);

        _exifStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = exifMetrics.Spacing,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 旧壳的 EXIF 分组边框没有底色,只有每个信息块自己是 #222;
        // 给分组加底色会把块间 12 的留白填成一片,看起来像一整块长条。
        _exifGroup = new Border
        {
            Child = _exifStack,
        };
        if (_openTools != null)
        {
            _exifGroup.Tapped += (_, _) => _openTools();
            ToolTipService.SetToolTip(_exifGroup, "工具");
        }

        _ratingStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = ratingMetrics.Spacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            // 旧壳把这 8 加在星条(DockPanel)上而不是分组边框上,分组边框本身没有外边距
            Margin = new Thickness(0, ratingMetrics.Inset, 0, ratingMetrics.Inset),
        };
        _ratingGroup = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BlockBackground),
            CornerRadius = new CornerRadius(DesignTokens.ToolbarCornerRadius),
            Child = _ratingStack,
        };
        for (var star = 0; star <= 5; star++)
        {
            var value = star;
            var button = CreateGlyphButton(ratingMetrics.Width, DesignTokens.ToolbarRatingGlyphFontSize, null);
            button.Click += (_, _) => RateCurrent(value);
            _starButtons.Add(button);
            _ratingStack.Children.Add(button);
        }

        _commandStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = commandMetrics.Spacing,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
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
        // TOOLBAR-08：单组超长时沿主轴滚动；内容不超出时不显示滚动条。
        _scroll = new ScrollViewer
        {
            Content = _frame,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _scroll.LayoutUpdated += (_, _) => ApplyPendingScroll();
        Children.Add(_scroll);

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

        SaveScrollOffset();
        _isRowLayout = isRowLayout;
        _groups.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _exifStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _commandStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _ratingStack.Orientation = isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _scroll.HorizontalScrollMode = isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.VerticalScrollMode = isRowLayout ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = isRowLayout ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = isRowLayout ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ApplyRatingOrder();
        ApplyOrientationMargins();
        RestoreScrollOffset();
    }

    /// <summary>换向前记住用户滚动位置(TOOLBAR-08)。</summary>
    private void SaveScrollOffset()
    {
        _savedHorizontalOffset = _scroll.HorizontalOffset;
        _savedVerticalOffset = _scroll.VerticalOffset;
    }

    /// <summary>布局稳定后把偏移约束到新几何的合法范围;不主动回到起点。</summary>
    private void RestoreScrollOffset()
    {
        _restoreScrollPending = true;
    }

    /// <summary>新几何就绪后应用保存的偏移;内容尚未测量时等下一轮布局。</summary>
    private void ApplyPendingScroll()
    {
        if (!_restoreScrollPending)
        {
            return;
        }

        double maxHorizontal = _scroll.ScrollableWidth;
        double maxVertical = _scroll.ScrollableHeight;
        // 只恢复新方向的主轴偏移；旧方向的偏移在新几何里没有意义。
        double wantedHorizontal = _isRowLayout ? _savedHorizontalOffset : 0;
        double wantedVertical = _isRowLayout ? 0 : _savedVerticalOffset;
        if (wantedHorizontal > maxHorizontal + 0.5 || wantedVertical > maxVertical + 0.5)
        {
            // 内容仍在逐轮测量,此时夹取会把远端偏移永久压回 0。
            return;
        }

        _restoreScrollPending = false;
        _scroll.ChangeView(
            Math.Clamp(wantedHorizontal, 0, maxHorizontal),
            Math.Clamp(wantedVertical, 0, maxVertical),
            null,
            disableAnimation: true);
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
    /// 重建控制栏全部内容(EXIF 项、命令按钮与评分)。
    /// </summary>
    private void Refresh()
    {
        RefreshExif();
        RefreshCommands();
        RefreshRating();
        ApplyRatingOrder();
        ApplyOrientationMargins();
    }

    /// <summary>
    /// 重建文件相关内容(EXIF 项、评分星色、评分顺序与方向留白);命令按钮只依赖设置,不在此重建。
    /// </summary>
    private void RefreshFileParts()
    {
        RefreshExif();
        RefreshRating();
        UpdateCommandAvailability();
        ApplyRatingOrder();
        ApplyOrientationMargins();
    }

    /// <summary>无照片时除“打开文件”外的命令按钮走系统禁用态(TOOLBAR-04)。</summary>
    private void UpdateCommandAvailability()
    {
        bool hasFile = _main.CurrentFile != null;
        foreach (var child in _commandStack.Children)
        {
            if (child is Button { Tag: string command } button)
            {
                button.IsEnabled = hasFile || command == "Open";
            }
        }
    }

    /// <summary>重建 EXIF 信息块(保留标签与强调值的信息层级与完整内容 tooltip)。</summary>
    private void RefreshExif()
    {
        _exifStack.Children.Clear();
        _exifBlocks.Clear();
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
                CornerRadius = new CornerRadius(DesignTokens.ToolbarCornerRadius),
                Child = stack,
            };
            ToolTipService.SetToolTip(block, $"{entry.DisplayName}:{entry.DisplayValue}");
            _exifBlocks.Add(block);
            _exifStack.Children.Add(block);
        }

        ApplyOrientationMargins();
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

        UpdateCommandAvailability();
        _commandStack.Visibility = _commandStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>创建一个命令按钮;评分命令画数字(0 为叉,TOOLBAR-03)。</summary>
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
            // TOOLBAR-03：评分快捷命令统一画数字（0 显示为叉），不用星形替代。
            if (stars == 0)
            {
                content.Children.Add(CreateGlyph(WindowsIcons.ClearGlyph, DesignTokens.ToolbarRatingClearGlyphFontSize, WindowsLayout.StarClear));
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = stars.ToString(),
                    FontSize = WindowsLayout.CommandGlyphFontSize,
                    Foreground = new SolidColorBrush(WindowsLayout.ExifValueForeground),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
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
            CornerRadius = new CornerRadius(DesignTokens.ToolbarCornerRadius),
            Content = content,
            Tag = item.Command,
            IsEnabled = _main.CurrentFile != null || item.Command == "Open",
        };
        var tooltip = BuildTooltip(item);
        ToolTipService.SetToolTip(button, tooltip);
        // TOOLBAR-04：读屏名称与悬停提示同源，含命令名与当前主/次绑定。
        AutomationProperties.SetName(button, tooltip);
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

    /// <summary>按当前照片评分刷新星色；无照片时整组走系统禁用态，不用透明度近似隐藏。</summary>
    private void RefreshRating()
    {
        var rating = _main.CurrentFile?.Rating ?? 0;
        bool hasFile = _main.CurrentFile != null;
        for (var star = 0; star <= 5; star++)
        {
            var button = _starButtons[star];
            var on = star > 0 && rating >= star;
            var tint = star == 0
                ? WindowsLayout.StarClear
                : on ? WindowsLayout.StarOn : WindowsLayout.StarOff;
            var glyph = star == 0 ? WindowsIcons.ClearGlyph : on ? WindowsIcons.StarGlyph : "\uE734";
            var size = star == 0 ? DesignTokens.ToolbarRatingClearGlyphFontSize : WindowsLayout.RatingGlyphFontSize;
            button.IsEnabled = hasFile;
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

    /// <summary>
    /// 主轴方向相关的留白:
    /// EXIF 块 = 旧壳 WebMarginConverter '0,4|4,0'(横排 4,0,4,0 / 竖排 0,4,0,4);
    /// 星条 = '0,8|8,0';命令组无额外留白。三个分组边框本身都没有外边距。
    /// </summary>
    private void ApplyOrientationMargins()
    {
        var exifMargin = _isRowLayout
            ? new Thickness(WindowsLayout.ExifBlockMargin, 0, WindowsLayout.ExifBlockMargin, 0)
            : new Thickness(0, WindowsLayout.ExifBlockMargin, 0, WindowsLayout.ExifBlockMargin);
        foreach (var block in _exifBlocks)
        {
            block.Margin = exifMargin;
        }

        _exifGroup.Margin = new Thickness(0);
        _ratingStack.Margin = _isRowLayout
            ? new Thickness(WindowsLayout.RatingGroupMargin, 0, WindowsLayout.RatingGroupMargin, 0)
            : new Thickness(0, WindowsLayout.RatingGroupMargin, 0, WindowsLayout.RatingGroupMargin);
        _commandStack.Margin = new Thickness(0);
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

    /// <summary>主视图模型变化:切换照片时只刷新文件相关内容(命令按钮只依赖设置)。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            ScheduleFileRefresh();
        }
    }

    /// <summary>控制栏相关设置变化:按影响范围合并到一次 dispatcher turn 内重建。</summary>
    private void OnControlChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.EnabledExifItems)
            or nameof(ControlViewModel.EnabledExifItems))
        {
            // EXIF 信息块随当前文件的 EXIF 与显示设置变化,归入文件相关刷新。
            ScheduleFileRefresh();
        }
        else if (e.PropertyName is nameof(SettingsViewModel.Hotkeys)
            or nameof(ControlViewModel.EnabledControls))
        {
            // 命令按钮只由显隐、快捷键绑定与排序决定。
            ScheduleCommandsRefresh();
        }
        else if (e.PropertyName is nameof(SettingsViewModel.ShowRating)
            or nameof(ControlViewModel.StarOpacity))
        {
            ScheduleRatingRefresh();
        }
    }

    /// <summary>安排文件相关刷新:EXIF、评分、评分顺序与方向留白。</summary>
    private void ScheduleFileRefresh()
    {
        _fileRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>安排评分刷新:显示开关、透明度或当前文件评分变化。</summary>
    private void ScheduleRatingRefresh()
    {
        _ratingRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>安排命令按钮重建:显隐、快捷键绑定或排序变化。</summary>
    private void ScheduleCommandsRefresh()
    {
        _commandsRefreshPending = true;
        ScheduleRefresh();
    }

    /// <summary>合并刷新:一轮内多次变化只投递一次回调,并按待办范围只执行对应部分。</summary>
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
            var file = _fileRefreshPending;
            var rating = _ratingRefreshPending;
            var commands = _commandsRefreshPending;
            _fileRefreshPending = false;
            _ratingRefreshPending = false;
            _commandsRefreshPending = false;

            if (file)
            {
                // 文件相关刷新已包含评分;命令同时待办时按原全量顺序重建一次。
                if (commands)
                {
                    Refresh();
                }
                else
                {
                    RefreshFileParts();
                }

                return;
            }

            if (rating)
            {
                RefreshRating();
            }

            if (commands)
            {
                RefreshCommands();
            }
        }, UiPriority.Background);
    }
}
