using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

internal sealed partial class WindowsSettingsPages
{
    /// <summary>控制页:布局模式、控制栏功能/快捷键。</summary>
    /// <returns>页面</returns>
    private UIElement BuildControlPage()
    {
        var combo = new ComboBox
        {
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var mode in _settings.LayoutModes)
        {
            combo.Items.Add(new ComboBoxItem { Content = mode.DisplayName, Tag = mode.Value });
        }

        var initial = FindLayoutIndex();
        combo.SelectedIndex = initial;
        var description = WindowsSettingsCard.RowDescription(
            initial >= 0 ? _settings.LayoutModes[initial].Description : string.Empty);

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is LayoutMode mode
                && combo.SelectedIndex >= 0)
            {
                _settings.LayoutMode = mode;
                description.Text = _settings.LayoutModes[combo.SelectedIndex].Description;
            }
        };
        OnSetting(nameof(SettingsViewModel.LayoutMode), () =>
        {
            var index = FindLayoutIndex();
            combo.SelectedIndex = index;
            if (index >= 0)
            {
                description.Text = _settings.LayoutModes[index].Description;
            }
        });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(WindowsSettingsCard.RowTitle("布局模式"));
        left.Children.Add(description);
        var layoutGrid = new Grid();
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layoutGrid.Children.Add(left);
        Grid.SetColumn(combo, 1);
        layoutGrid.Children.Add(combo);

        var hotkeys = new WindowsSettingsList(
            _settings.Hotkeys,
            row => BuildHotkeyRow((SettingsViewModel.HotkeyItem)row),
            _settings.MoveHotkey,
            WindowsLayout.SettingsListRowHeight);
        _detach.Add(hotkeys.Detach);

        return Page(
            "控制",
            WindowsSettingsCard.SectionHeader("控制栏布局位置"),
            WindowsSettingsCard.Card(layoutGrid),
            WindowsSettingsCard.SectionHeader("控制栏功能与快捷键"),
            WindowsSettingsCard.Card(hotkeys, new Thickness(8)));
    }

    /// <summary>当前布局模式在选项里的索引。</summary>
    /// <returns>索引;未找到返回 -1</returns>
    private int FindLayoutIndex()
    {
        for (var index = 0; index < _settings.LayoutModes.Count; index++)
        {
            if (_settings.LayoutModes[index].Value == _settings.LayoutMode)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>快捷键行:显隐 + 名称 + 主/次录制按钮 + 把手。</summary>
    /// <param name="hotkey">快捷键项</param>
    /// <returns>行元素</returns>
    private WindowsSettingsRowElement BuildHotkeyRow(SettingsViewModel.HotkeyItem hotkey)
    {
        var checkbox = new CheckBox
        {
            IsChecked = hotkey.IsDisplay,
            Width = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var updating = false;
        checkbox.Checked += (_, _) =>
        {
            if (!updating)
            {
                hotkey.IsDisplay = true;
            }
        };
        checkbox.Unchecked += (_, _) =>
        {
            if (!updating)
            {
                hotkey.IsDisplay = false;
            }
        };

        var primary = BuildHotkeyButton();
        var secondary = BuildHotkeyButton();
        primary.Click += (_, _) => ToggleRecording(hotkey, true);
        secondary.Click += (_, _) => ToggleRecording(hotkey, false);
        var primaryText = (TextBlock)primary.Content;
        var secondaryText = (TextBlock)secondary.Content;

        void Refresh()
        {
            RefreshHotkeyButton(primary, primaryText, hotkey, true, _recorder);
            RefreshHotkeyButton(secondary, secondaryText, hotkey, false, _recorder);
        }

        Refresh();

        var name = WindowsSettingsCard.RowTitle(hotkey.Name);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = 220;

        var grid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(checkbox);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(primary, 2);
        grid.Children.Add(primary);
        Grid.SetColumn(secondary, 3);
        grid.Children.Add(secondary);
        var handle = WindowsSettingsCard.DragHandle();
        Grid.SetColumn(handle, 4);
        grid.Children.Add(handle);

        PropertyChangedEventHandler propertyHandler = (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(SettingsViewModel.HotkeyItem.PrimaryHotkey):
                case nameof(SettingsViewModel.HotkeyItem.SecondaryHotkey):
                case nameof(SettingsViewModel.HotkeyItem.HasPrimaryConflict):
                case nameof(SettingsViewModel.HotkeyItem.HasSecondaryConflict):
                    Refresh();
                    break;
                case nameof(SettingsViewModel.HotkeyItem.IsDisplay):
                    updating = true;
                    checkbox.IsChecked = hotkey.IsDisplay;
                    updating = false;
                    break;
            }
        };
        hotkey.PropertyChanged += propertyHandler;
        _recorder.StateChanged += Refresh;

        var row = new WindowsSettingsRowElement(() =>
        {
            hotkey.PropertyChanged -= propertyHandler;
            _recorder.StateChanged -= Refresh;
        });
        row.Children.Add(grid);
        return row;
    }

    /// <summary>起始/结束录制;点击正在录制的那一侧视为取消。</summary>
    /// <param name="hotkey">快捷键项</param>
    /// <param name="primary">是否主绑定</param>
    private void ToggleRecording(SettingsViewModel.HotkeyItem hotkey, bool primary)
    {
        if (_recorder.IsRecordingBinding(hotkey, primary))
        {
            _recorder.Cancel();
        }
        else
        {
            _recorder.Begin(hotkey, primary);
        }
    }

    /// <summary>未绑定显示文本。</summary>
    /// <param name="gesture">手势</param>
    /// <returns>文本</returns>
    private static string GestureText(AppGesture? gesture) => WindowsInput.FormatGesture(gesture);

    /// <summary>创建行内录制按钮。</summary>
    /// <returns>按钮</returns>
    private static Button BuildHotkeyButton()
    {
        var text = new TextBlock
        {
            FontSize = 11,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.CardNameForeground),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var button = new Button
        {
            Content = text,
            Width = WindowsLayout.SettingsHotkeyButtonWidth,
            Height = WindowsLayout.SettingsHotkeyButtonHeight,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
        };
        ToolTipService.SetToolTip(button, "点击录制；Delete 清空，Esc 取消");
        ApplyHotkeyButtonColors(button, Color.FromArgb(0xFF, 0x33, 0x33, 0x33));
        return button;
    }

    /// <summary>刷新按钮文本/描边(录制态、冲突、未设置)。</summary>
    /// <param name="button">按钮</param>
    /// <param name="text">文本控件</param>
    /// <param name="hotkey">快捷键项</param>
    /// <param name="primary">是否主绑定</param>
    /// <param name="recorder">录制器</param>
    private static void RefreshHotkeyButton(Button button, TextBlock text,
        SettingsViewModel.HotkeyItem hotkey, bool primary, WindowsHotkeyRecorder recorder)
    {
        var recording = recorder.IsRecordingBinding(hotkey, primary);
        var conflict = primary ? hotkey.HasPrimaryConflict : hotkey.HasSecondaryConflict;
        var gesture = primary ? hotkey.PrimaryHotkey : hotkey.SecondaryHotkey;
        text.Text = recording ? "按下按键…" : GestureText(gesture);

        if (recording)
        {
            text.Foreground = WindowsSettingsCard.Brush(WindowsLayout.CardNameForeground);
            ApplyHotkeyButtonColors(button, WindowsLayout.CurrentBorder);
            return;
        }

        if (conflict)
        {
            text.Foreground = WindowsSettingsCard.Brush(WindowsLayout.SettingsConflictForeground);
            ApplyHotkeyButtonColors(button, WindowsLayout.SettingsConflictForeground);
            return;
        }

        text.Foreground = WindowsSettingsCard.Brush(WindowsLayout.CardNameForeground);
        ApplyHotkeyButtonColors(button, WindowsLayout.Divider);
    }

    /// <summary>按钮模板用主题画刷覆盖背景/描边,必须同时改写资源键。</summary>
    /// <param name="button">按钮</param>
    /// <param name="border">描边色</param>
    private static void ApplyHotkeyButtonColors(Button button, Color border)
    {
        var background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
        var hover = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33));
        var borderBrush = new SolidColorBrush(border);
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = hover;
        button.Resources["ButtonBorderBrush"] = borderBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = borderBrush;
        button.Resources["ButtonBorderBrushPressed"] = borderBrush;
        button.Resources["ButtonForeground"] = WindowsSettingsCard.Brush(WindowsLayout.CardNameForeground);
    }
}
