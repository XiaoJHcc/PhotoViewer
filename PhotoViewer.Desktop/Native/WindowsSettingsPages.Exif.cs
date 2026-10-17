using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Desktop.Native;

internal sealed partial class WindowsSettingsPages
{
    /// <summary>EXIF 页:显示项排序、星级显示与安全写回。</summary>
    /// <returns>页面</returns>
    private UIElement BuildExifPage()
    {
        var items = new WindowsSettingsList(
            _settings.ExifDisplayItems,
            row => BuildExifRow((SettingsViewModel.ExifDisplayItem)row),
            _settings.MoveExifDisplay,
            WindowsLayout.SettingsListRowHeight);
        _detach.Add(items.Detach);

        var showRating = NewToggle(_settings.ShowRating, value => _settings.ShowRating = value);
        OnSetting(nameof(SettingsViewModel.ShowRating), () => showRating.IsOn = _settings.ShowRating);
        var safeSetRating = NewToggle(_settings.SafeSetRating, value => _settings.SafeSetRating = value);
        OnSetting(nameof(SettingsViewModel.SafeSetRating), () => safeSetRating.IsOn = _settings.SafeSetRating);

        var ratingGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        ratingGroup.Children.Add(WindowsSettingsCard.ToggleRow("显示星级评分", null, showRating));
        ratingGroup.Children.Add(WindowsSettingsCard.ToggleRow(
            "安全修改文件星级",
            "检验文件字节变化再写回，降低损坏原文件概率",
            safeSetRating));

        return Page(
            "EXIF",
            WindowsSettingsCard.SectionHeader("EXIF 显示/收纳"),
            WindowsSettingsCard.Card(items, new Thickness(4)),
            WindowsSettingsCard.SectionHeader("评分与写回"),
            ratingGroup);
    }

    /// <summary>EXIF 显示行:勾选 + 字段名 + 把手。</summary>
    /// <param name="item">显示项</param>
    /// <returns>行元素</returns>
    private WindowsSettingsRowElement BuildExifRow(SettingsViewModel.ExifDisplayItem item)
    {
        var checkbox = new CheckBox
        {
            IsChecked = item.IsEnabled,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var updating = false;
        checkbox.Checked += (_, _) =>
        {
            if (!updating)
            {
                item.IsEnabled = true;
            }
        };
        checkbox.Unchecked += (_, _) =>
        {
            if (!updating)
            {
                item.IsEnabled = false;
            }
        };

        var name = WindowsSettingsCard.RowTitle(item.DisplayName);
        name.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(checkbox);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var handle = WindowsSettingsCard.DragHandle();
        Grid.SetColumn(handle, 2);
        grid.Children.Add(handle);

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(SettingsViewModel.ExifDisplayItem.IsEnabled))
            {
                return;
            }

            updating = true;
            checkbox.IsChecked = item.IsEnabled;
            updating = false;
        };
        item.PropertyChanged += handler;

        var row = new WindowsSettingsRowElement(() => item.PropertyChanged -= handler);
        row.Children.Add(grid);
        return row;
    }
}
