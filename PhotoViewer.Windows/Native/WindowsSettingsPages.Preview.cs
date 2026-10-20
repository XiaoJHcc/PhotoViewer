using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Settings;
using Windows.System;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsSettingsPages
{
    private const string PresetErrorText = "请输入大于 0 且不超过 1600% 的数字";

    private StackPanel? _presetHost;
    private InfoBar? _presetError;
    private Button? _presetAddButton;
    private INotifyCollectionChanged? _presetCollection;
    private NotifyCollectionChangedEventHandler? _presetCollectionHandler;
    private TextBox? _focusedPresetEditor;
    private bool _committingPreset;
    private bool _closing;
    private readonly Dictionary<SettingsViewModel.ScalePreset, PresetRow> _presetRows = [];

    private sealed class PresetRow
    {
        public required SettingsViewModel.ScalePreset Preset { get; init; }
        public required WindowsSettingsRowElement Element { get; init; }
        public required TextBox Editor { get; init; }
        public required Button Delete { get; init; }
        public required Action Detach { get; init; }
    }

    /// <summary>预览页:缩放指示器与缩放比例预设增删。</summary>
    /// <returns>页面</returns>
    private UIElement BuildPreviewPage()
    {
        var showZoom = NewToggle(_settings.ShowZoomIndicator, value => _settings.ShowZoomIndicator = value);
        OnSetting(nameof(SettingsViewModel.ShowZoomIndicator), () => showZoom.IsOn = _settings.ShowZoomIndicator);

        _presetHost = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        _presetError = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false,
            Message = PresetErrorText,
        };
        _presetAddButton = NewButton("添加预设", () =>
        {
            CommitPendingEdits();
            _settings.AddScalePreset();
        });
        _presetAddButton.HorizontalAlignment = HorizontalAlignment.Right;
        _presetAddButton.Margin = new Thickness(8, 4, 8, 8);

        _presetCollectionHandler = (_, _) => RebuildPresets();
        RebuildPresets();
        OnSetting(nameof(SettingsViewModel.ScalePresets), RebuildPresets);
        _detach.Add(() =>
        {
            if (_presetCollection != null && _presetCollectionHandler != null)
            {
                _presetCollection.CollectionChanged -= _presetCollectionHandler;
            }

            foreach (var row in _presetRows.Values)
            {
                row.Detach();
            }
        });

        var toggleGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        toggleGroup.Children.Add(WindowsSettingsCard.ToggleRow("缩放时显示指示器", null, showZoom));

        var page = Page(
            "预览",
            WindowsSettingsCard.SectionHeader("缩放位置显示"),
            toggleGroup,
            WindowsSettingsCard.SectionHeader("缩放比例预设"),
            _presetError,
            WindowsSettingsCard.Card(_presetHost, new Thickness(4)));
        page.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        page.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPreviewPagePressed), true);
        return page;
    }

    /// <summary>提交尚未失焦的预设编辑;切换分类与关闭设置前调用。</summary>
    public void CommitPendingEdits()
    {
        if (_focusedPresetEditor == null)
        {
            return;
        }

        var row = _presetRows.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Editor, _focusedPresetEditor));
        if (row != null)
        {
            CommitPreset(row.Editor, row.Preset, updateUi: true);
        }
    }

    /// <summary>关闭前只写工作副本,不再触发布局或焦点操作;排序在源设置提交时完成。</summary>
    public void CommitForClose()
    {
        _closing = true;
        if (_focusedPresetEditor == null)
        {
            return;
        }

        var row = _presetRows.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Editor, _focusedPresetEditor));
        if (row != null)
        {
            CommitPreset(row.Editor, row.Preset, updateUi: false);
        }
    }

    private void OnPreviewPagePressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || sender is not DependencyObject page)
        {
            return;
        }

        for (var current = source; current != null && !ReferenceEquals(current, page); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Control)
            {
                return;
            }
        }

        (sender as Control)?.Focus(FocusState.Programmatic);
    }

    private void RebuildPresets()
    {
        if (_presetHost == null || _presetAddButton == null)
        {
            return;
        }

        if (!ReferenceEquals(_presetCollection, _settings.ScalePresets))
        {
            if (_presetCollection != null && _presetCollectionHandler != null)
            {
                _presetCollection.CollectionChanged -= _presetCollectionHandler;
            }

            _presetCollection = _settings.ScalePresets;
            if (_presetCollectionHandler != null)
            {
                _presetCollection.CollectionChanged += _presetCollectionHandler;
            }
        }

        foreach (var stale in _presetRows.Keys.Where(preset => !_settings.ScalePresets.Contains(preset)).ToArray())
        {
            var row = _presetRows[stale];
            _presetHost.Children.Remove(row.Element);
            row.Detach();
            _presetRows.Remove(stale);
        }

        for (var index = 0; index < _settings.ScalePresets.Count; index++)
        {
            var preset = _settings.ScalePresets[index];
            if (!_presetRows.TryGetValue(preset, out var row))
            {
                row = BuildPresetRow(preset);
                _presetRows[preset] = row;
                _presetHost.Children.Insert(Math.Min(index, _presetHost.Children.Count), row.Element);
            }
            else
            {
                var current = _presetHost.Children.IndexOf(row.Element);
                if (current < 0)
                {
                    _presetHost.Children.Insert(Math.Min(index, _presetHost.Children.Count), row.Element);
                }
                else if (current != index)
                {
                    _presetHost.Children.RemoveAt(current);
                    _presetHost.Children.Insert(Math.Min(index, _presetHost.Children.Count), row.Element);
                }
            }

            row.Delete.IsEnabled = _settings.CanRemoveScalePreset;
        }

        if (!_presetHost.Children.Contains(_presetAddButton))
        {
            _presetHost.Children.Add(_presetAddButton);
        }
    }

    private PresetRow BuildPresetRow(SettingsViewModel.ScalePreset preset)
    {
        var editor = new TextBox
        {
            Text = preset.Text,
            Width = WindowsLayout.SettingsNumberBoxWidth,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(editor, "缩放比例预设");
        AutomationProperties.SetHelpText(editor, "输入百分比,Enter 或失焦提交");

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
        grid.Children.Add(editor);
        Grid.SetColumn(percent, 1);
        grid.Children.Add(percent);
        var delete = WindowsSettingsCard.IconButton("\uE74D", "移除预设", () => _settings.RemoveScalePreset(preset));
        delete.IsEnabled = _settings.CanRemoveScalePreset;
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        void OnGotFocus(object sender, RoutedEventArgs e) => _focusedPresetEditor = editor;
        void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(_focusedPresetEditor, editor))
            {
                _focusedPresetEditor = null;
            }

            CommitPreset(editor, preset);
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            CommitPreset(editor, preset);
            e.Handled = true;
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (_closing)
            {
                return;
            }

            if (args.PropertyName == nameof(SettingsViewModel.ScalePreset.Text) && editor.Text != preset.Text)
            {
                editor.Text = preset.Text;
            }
        };

        editor.GotFocus += OnGotFocus;
        editor.LostFocus += OnLostFocus;
        editor.KeyDown += OnKeyDown;
        preset.PropertyChanged += handler;

        var row = new WindowsSettingsRowElement(Detach);
        row.Children.Add(grid);
        return new PresetRow { Preset = preset, Element = row, Editor = editor, Delete = delete, Detach = Detach };

        void Detach()
        {
            if (ReferenceEquals(_focusedPresetEditor, editor))
            {
                _focusedPresetEditor = null;
            }

            editor.GotFocus -= OnGotFocus;
            editor.LostFocus -= OnLostFocus;
            editor.KeyDown -= OnKeyDown;
            preset.PropertyChanged -= handler;
        }
    }

    private void CommitPreset(TextBox editor, SettingsViewModel.ScalePreset preset, bool updateUi = true)
    {
        if (_committingPreset)
        {
            return;
        }

        _committingPreset = true;
        try
        {
            CommitPresetCore(editor, preset, updateUi);
        }
        finally
        {
            _committingPreset = false;
        }
    }

    private void CommitPresetCore(TextBox editor, SettingsViewModel.ScalePreset preset, bool updateUi)
    {
        bool valid = (double.TryParse(editor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || double.TryParse(editor.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
            && SettingsViewModel.IsValidScalePresetPercent(percent);

        if (!valid)
        {
            if (updateUi)
            {
                _presetError!.IsOpen = true;
                editor.Text = preset.Text;
            }

            return;
        }

        if (updateUi)
        {
            _presetError!.IsOpen = false;
        }

        percent = Math.Round(percent, 3);
        var text = FormatNumber(percent);
        bool hadFocus = updateUi && editor.FocusState != FocusState.Unfocused;
        int caret = editor.SelectionStart;
        if (text != preset.Text)
        {
            preset.Text = text;
            if (updateUi)
            {
                _settings.ApplyScalePreset();
            }
        }

        if (!updateUi)
        {
            return;
        }

        editor.Text = preset.Text;
        if (hadFocus)
        {
            editor.Focus(FocusState.Programmatic);
            editor.SelectionStart = Math.Min(editor.Text.Length, caret);
        }
    }
}
