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
    public void CommitPendingEdits() => CommitPresetRows(updateUi: true);

    /// <summary>关闭前只写工作副本,不再触发布局或焦点操作;排序在源设置提交时完成。</summary>
    public void CommitForClose()
    {
        _closing = true;
        CommitPresetRows(updateUi: false);
    }

    /// <summary>
    /// 提交文本尚未落定的预设行。焦点事件可能还没派发(刚取得焦点就关闭时 <see cref="Control.GotFocus"/> 尚未到达),
    /// 因此除了记录中的焦点框,还直接向 <see cref="FocusManager"/> 取当前焦点,并把文本与预设不一致的行一并提交。
    /// 两条路径都不把焦点收回输入框:切换分类与关闭时用户已经离开发那个输入框。
    /// </summary>
    /// <param name="updateUi">是否提示非法输入并排序(关闭时为 false)</param>
    private void CommitPresetRows(bool updateUi)
    {
        var focused = FocusedPresetEditor();
        foreach (var row in _presetRows.Values.ToArray())
        {
            if (!ReferenceEquals(row.Editor, focused) && row.Editor.Text == row.Preset.Text)
            {
                continue;
            }

            CommitPreset(row.Editor, row.Preset, updateUi, keepFocus: false);
        }
    }

    /// <summary>当前正在编辑预设的输入框:优先取焦点事件记录的,记录缺失时直接问焦点管理器。</summary>
    /// <returns>焦点所在的预设输入框;没有返回 null</returns>
    private TextBox? FocusedPresetEditor()
    {
        if (_focusedPresetEditor != null)
        {
            return _focusedPresetEditor;
        }

        if (_presetHost?.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not TextBox focused)
        {
            return null;
        }

        return _presetRows.Values.Any(row => ReferenceEquals(row.Editor, focused)) ? focused : null;
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

            // 失焦本身就是用户移焦的结果,提交后不得把焦点收回输入框。
            CommitPreset(editor, preset, keepFocus: false);
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            CommitPreset(editor, preset, keepFocus: true);
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

    private void CommitPreset(
        TextBox editor, SettingsViewModel.ScalePreset preset, bool updateUi = true, bool keepFocus = false)
    {
        if (_committingPreset)
        {
            return;
        }

        _committingPreset = true;
        try
        {
            CommitPresetCore(editor, preset, updateUi, keepFocus);
        }
        finally
        {
            _committingPreset = false;
        }
    }

    private void CommitPresetCore(TextBox editor, SettingsViewModel.ScalePreset preset, bool updateUi, bool keepFocus)
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
        if (keepFocus && editor.FocusState == FocusState.Unfocused)
        {
            // 排序会把行从面板上摘掉再插回,输入框随之失焦;只有用户仍在编辑(Enter 提交)时才把焦点还回去。
            editor.Focus(FocusState.Programmatic);
            editor.SelectionStart = Math.Min(editor.Text.Length, caret);
        }
    }
}
