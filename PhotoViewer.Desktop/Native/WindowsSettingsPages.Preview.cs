using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Desktop.Native;

internal sealed partial class WindowsSettingsPages
{
    private StackPanel? _presetHost;
    private INotifyCollectionChanged? _presetCollection;
    private NotifyCollectionChangedEventHandler? _presetCollectionHandler;

    /// <summary>预览页:缩放指示器与缩放比例预设增删。</summary>
    /// <returns>页面</returns>
    private UIElement BuildPreviewPage()
    {
        var showZoom = NewToggle(_settings.ShowZoomIndicator, value => _settings.ShowZoomIndicator = value);
        OnSetting(nameof(SettingsViewModel.ShowZoomIndicator), () => showZoom.IsOn = _settings.ShowZoomIndicator);

        _presetHost = new StackPanel { Spacing = 6 };
        _presetCollectionHandler = (_, _) => RebuildPresets();
        RebuildPresets();
        OnSetting(nameof(SettingsViewModel.ScalePresets), RebuildPresets);
        _detach.Add(() =>
        {
            if (_presetCollection != null && _presetCollectionHandler != null)
            {
                _presetCollection.CollectionChanged -= _presetCollectionHandler;
            }
        });

        var toggleGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        toggleGroup.Children.Add(WindowsSettingsCard.ToggleRow("缩放时显示指示器", null, showZoom));

        return Page(
            "预览",
            WindowsSettingsCard.SectionHeader("缩放位置显示"),
            toggleGroup,
            WindowsSettingsCard.SectionHeader("缩放比例预设"),
            WindowsSettingsCard.Card(_presetHost, new Thickness(8)));
    }

    /// <summary>重建预设行:集合被排序替换时也要更换订阅。</summary>
    private void RebuildPresets()
    {
        if (_presetHost == null)
        {
            return;
        }

        if (_presetCollection != null && _presetCollectionHandler != null)
        {
            _presetCollection.CollectionChanged -= _presetCollectionHandler;
        }

        _presetCollection = _settings.ScalePresets;
        if (_presetCollectionHandler != null)
        {
            _presetCollection.CollectionChanged += _presetCollectionHandler;
        }

        _presetHost.Children.Clear();
        foreach (var preset in _settings.ScalePresets)
        {
            _presetHost.Children.Add(BuildPresetRow(preset));
        }

        var add = NewButton("添加预设", _settings.AddScalePreset);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        add.Margin = new Thickness(0, 4, 0, 0);
        _presetHost.Children.Add(add);
    }

    /// <summary>单个缩放预设行:数值框 + % + 派生比例 + 删除。</summary>
    /// <param name="preset">预设</param>
    /// <returns>行元素</returns>
    private UIElement BuildPresetRow(SettingsViewModel.ScalePreset preset)
    {
        var value = double.TryParse(preset.Text, out var parsed) ? Math.Clamp(parsed, 1, 1600) : 100;
        var slider = NewSlider(0, 1, 0.001, ToExp(value, 1, 1600), newValue =>
        {
            preset.Text = FormatNumber(FromExp(newValue, 1, 1600));
        });
        slider.Width = 220;
        slider.Margin = new Thickness(0, 0, 8, 0);

        var display = new TextBlock
        {
            Text = preset.Display,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.ExifLabelForeground),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 64,
        };

        var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(slider);
        Grid.SetColumn(display, 1);
        grid.Children.Add(display);
        var delete = WindowsSettingsCard.IconButton("\uE74D", "移除预设", () => _settings.RemoveScalePreset(preset));
        Grid.SetColumn(delete, 2);
        grid.Children.Add(delete);

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.ScalePreset.Display))
            {
                display.Text = preset.Display;
            }
        };
        preset.PropertyChanged += handler;

        var row = new WindowsSettingsRowElement(() => preset.PropertyChanged -= handler);
        row.Children.Add(grid);
        return row;
    }
}
