using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 工具窗口的照片数据统计页:文件夹列表、通配符筛选与 CSV 导出。
/// 业务状态全部在共享 <see cref="PhotoStatsViewModel"/>,本页只做原生控件绑定与工具窗口自有的文件选择器。
/// </summary>
internal sealed class WindowsStatsToolPage : Grid
{
    private readonly PhotoStatsViewModel _stats;
    private readonly Func<IntPtr> _windowHandle;
    private readonly ListView _folders;
    private readonly List<UIElement> _folderRows = [];
    private readonly TextBlock _emptyFolders;
    private readonly Button _addButton;
    private readonly Button _removeButton;
    private readonly TextBox _patternBox;
    private readonly Button _exportButton;
    private readonly Button _cancelButton;
    private readonly ProgressRing _progress;
    private readonly TextBlock _status;
    private bool _picking;
    private bool _disposed;

    /// <summary>创建照片数据统计页。</summary>
    /// <param name="stats">共享统计视图模型</param>
    /// <param name="windowHandle">工具窗口句柄提供者(文件选择器必须绑定窗口)</param>
    public WindowsStatsToolPage(PhotoStatsViewModel stats, Func<IntPtr> windowHandle)
    {
        _stats = stats;
        _windowHandle = windowHandle;

        _folders = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            MinHeight = 80,
            MaxHeight = WindowsLayout.ToolsFolderListHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_folders, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_folders, ScrollBarVisibility.Disabled);
        _folders.SelectionChanged += (_, _) => UpdateButtons();

        _emptyFolders = new TextBlock
        {
            Text = "未添加文件夹",
            Style = WindowsSettingsCard.TextStyle("BodyTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorTertiaryBrush"),
            Padding = new Thickness(8, 18, 0, 8),
        };

        _addButton = new Button
        {
            Content = "添加文件夹",
            IsEnabled = !_stats.IsRunning,
        };
        _addButton.Click += async (_, _) => await AddFolderAsync();

        _removeButton = new Button
        {
            Content = "移除选中",
            IsEnabled = false,
        };
        _removeButton.Click += (_, _) => RemoveSelectedFolder();

        var folderButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 0, 8, 8),
        };
        folderButtons.Children.Add(_addButton);
        folderButtons.Children.Add(_removeButton);

        var folderContent = new StackPanel { Spacing = 8 };
        folderContent.Children.Add(_folders);
        folderContent.Children.Add(_emptyFolders);
        folderContent.Children.Add(folderButtons);
        var folderCard = WindowsSettingsCard.Card(folderContent, new Thickness(8));

        _patternBox = new TextBox
        {
            Text = _stats.Patterns,
            Height = WindowsLayout.ToolsPatternBoxHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !_stats.IsRunning,
        };
        _patternBox.TextChanged += (_, _) =>
        {
            if (!string.Equals(_stats.Patterns, _patternBox.Text, StringComparison.Ordinal))
            {
                _stats.Patterns = _patternBox.Text;
            }
        };
        var patternCard = new SettingsCard
        {
            Header = "通配符",
            Description = "逗号分隔，如 *.HIF,*.JPG",
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _patternBox,
        };

        _exportButton = new Button
        {
            Content = "导出 CSV",
            IsEnabled = _stats.CanExport,
        };
        _exportButton.Click += async (_, _) => await ExportAsync();

        _cancelButton = new Button
        {
            Content = "取消",
            Visibility = Visibility.Collapsed,
        };
        _cancelButton.Click += (_, _) => _stats.CancelExport();

        _progress = new ProgressRing
        {
            IsActive = false,
            Width = 16,
            Height = 16,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(_exportButton);
        actions.Children.Add(_cancelButton);
        actions.Children.Add(_progress);

        _status = new TextBlock
        {
            Text = _stats.StatusText,
            Style = WindowsSettingsCard.TextStyle("BodyTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var exportContent = new StackPanel();
        exportContent.Children.Add(actions);
        exportContent.Children.Add(_status);
        var exportCard = WindowsSettingsCard.Card(exportContent, new Thickness(16, 12, 16, 16));

        var body = new StackPanel();
        body.Children.Add(WindowsSettingsCard.SectionHeader("文件夹"));
        body.Children.Add(folderCard);
        body.Children.Add(WindowsSettingsCard.SectionHeader("文件筛选"));
        body.Children.Add(patternCard);
        body.Children.Add(WindowsSettingsCard.SectionHeader("导出"));
        body.Children.Add(exportCard);

        Children.Add(WindowsSettingsCard.Page("照片数据统计", body));
        _stats.PropertyChanged += OnStatsChanged;
        _stats.Folders.CollectionChanged += OnFoldersChanged;
        RebuildFolders();
        UpdateButtons();
        UpdateStatus();
    }

    /// <summary>文件夹行数(验收用)。</summary>
    internal int FolderRowCount => _folderRows.Count;

    /// <summary>状态文本(验收用)。</summary>
    internal string StatusText => _status.Text;

    /// <summary>通配符输入框(验收用)。</summary>
    internal TextBox PatternBox => _patternBox;

    /// <summary>选择文件夹行(验收用;与真实点击选择同一路径)。</summary>
    /// <param name="index">行索引</param>
    internal void SelectFolderForAcceptance(int index) => _folders.SelectedIndex = index;

    /// <summary>移除当前选中行(按钮与验收共用)。</summary>
    internal void RemoveSelectedFolder()
    {
        if (_folders.SelectedItem is WindowsSettingsRowElement { Item: string path })
        {
            _stats.RemoveFolder(path);
        }
    }

    /// <summary>退订共享状态,窗口关闭时调用。</summary>
    internal void Detach()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stats.PropertyChanged -= OnStatsChanged;
        _stats.Folders.CollectionChanged -= OnFoldersChanged;
    }

    /// <summary>共享统计状态变化:进度、按钮与状态文本。</summary>
    private void OnStatsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        switch (args.PropertyName)
        {
            case nameof(PhotoStatsViewModel.IsRunning):
            case nameof(PhotoStatsViewModel.CanExport):
                UpdateButtons();
                break;
            case nameof(PhotoStatsViewModel.StatusText):
                UpdateStatus();
                break;
            case nameof(PhotoStatsViewModel.Patterns):
                if (!string.Equals(_patternBox.Text, _stats.Patterns, StringComparison.Ordinal))
                {
                    _patternBox.Text = _stats.Patterns;
                }

                break;
        }
    }

    /// <summary>文件夹集合变化:重建列表行。</summary>
    private void OnFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => RebuildFolders();

    /// <summary>重建文件夹列表行(列表项直接放 UIElement,与设置页排序表同一做法)。</summary>
    private void RebuildFolders()
    {
        if (_disposed)
        {
            return;
        }

        string? selected = (_folders.SelectedItem as WindowsSettingsRowElement)?.Item as string;
        _folderRows.Clear();
        foreach (var path in _stats.Folders)
        {
            var row = new WindowsSettingsRowElement(() => { })
            {
                Item = path,
                MinHeight = 40,
                Margin = new Thickness(8, 0, 8, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = path,
                Style = WindowsSettingsCard.TextStyle("BodyTextBlockStyle"),
                Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _folderRows.Add(row);
        }

        _folders.ItemsSource = null;
        _folders.ItemsSource = _folderRows;
        if (selected != null)
        {
            foreach (var row in _folderRows)
            {
                if (row is WindowsSettingsRowElement element
                    && string.Equals(element.Item as string, selected, StringComparison.OrdinalIgnoreCase))
                {
                    _folders.SelectedItem = row;
                    break;
                }
            }
        }

        bool empty = _folderRows.Count == 0;
        _folders.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _emptyFolders.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
        {
            _folders.Height = Math.Clamp(_folderRows.Count * 41.0, 80, WindowsLayout.ToolsFolderListHeight);
        }

        UpdateButtons();
    }

    /// <summary>按运行/选择/选择器状态刷新按钮可用性与进度指示。</summary>
    private void UpdateButtons()
    {
        bool busy = _stats.IsRunning || _picking;
        _addButton.IsEnabled = !busy;
        _removeButton.IsEnabled = !busy && _folders.SelectedItem != null;
        _patternBox.IsEnabled = !busy;
        _exportButton.IsEnabled = _stats.CanExport && !_picking;
        _cancelButton.Visibility = _stats.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsActive = _stats.IsRunning;
        _progress.Visibility = _stats.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>刷新状态文本。</summary>
    private void UpdateStatus() => _status.Text = _stats.StatusText;

    /// <summary>选择并添加文件夹;选择期间禁用相关按钮。</summary>
    private async Task AddFolderAsync()
    {
        if (_picking || _stats.IsRunning)
        {
            return;
        }

        _picking = true;
        UpdateButtons();
        try
        {
            var path = await WindowsToolsDialogs.PickFolderAsync(_windowHandle);
            if (!_disposed && path != null)
            {
                _stats.AddFolder(path);
            }
        }
        finally
        {
            _picking = false;
            if (!_disposed)
            {
                UpdateButtons();
            }
        }
    }

    /// <summary>选择 CSV 输出位置并启动导出;取消选择不执行。</summary>
    private async Task ExportAsync()
    {
        if (_picking || !_stats.CanExport)
        {
            return;
        }

        _picking = true;
        UpdateButtons();
        try
        {
            var path = await WindowsToolsDialogs.PickCsvAsync(_windowHandle);
            if (!_disposed && path != null)
            {
                await _stats.ExportAsync(path);
            }
        }
        finally
        {
            _picking = false;
            if (!_disposed)
            {
                UpdateButtons();
            }
        }
    }
}
