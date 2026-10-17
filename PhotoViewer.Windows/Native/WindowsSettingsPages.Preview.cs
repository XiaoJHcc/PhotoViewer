using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

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

        _presetHost = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
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
            WindowsSettingsCard.Card(_presetHost, new Thickness(4)));
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
        add.Margin = new Thickness(8, 4, 8, 8);
        _presetHost.Children.Add(add);
    }

    /// <summary>单个缩放预设行:数值框 + % + 派生比例 + 删除。</summary>
    /// <param name="preset">预设</param>
    /// <returns>行元素</returns>
    private UIElement BuildPresetRow(SettingsViewModel.ScalePreset preset)
    {
        var value = double.TryParse(preset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Round(Math.Clamp(parsed, 1, 1600), 1)
            : 100;
        var box = NewNumberBox(value, 1, 1600, 1, newValue =>
        {
            if (!double.IsNaN(newValue))
            {
                preset.Text = FormatNumber(Math.Round(newValue, 1));
            }
        });

        var percent = new TextBlock
        {
            Text = "%",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            Margin = new Thickness(4, 0, 12, 0),
        };

        var grid = new Grid
        {
            Height = WindowsLayout.SettingsListRowHeight,
            Margin = new Thickness(8, 0, 8, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(percent, 1);
        grid.Children.Add(percent);
        var delete = WindowsSettingsCard.IconButton("\uE74D", "移除预设", () => _settings.RemoveScalePreset(preset));
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        var updating = false;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(SettingsViewModel.ScalePreset.Text) || updating)
            {
                return;
            }

            if (double.TryParse(preset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var textValue))
            {
                updating = true;
                box.Value = Math.Round(Math.Clamp(textValue, 1, 1600), 1);
                updating = false;
            }
        };
        preset.PropertyChanged += handler;

        var row = new WindowsSettingsRowElement(() => preset.PropertyChanged -= handler);
        row.Children.Add(grid);
        return row;
    }
}
