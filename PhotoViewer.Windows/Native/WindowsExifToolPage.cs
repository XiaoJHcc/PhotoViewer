using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 工具窗口的 EXIF 详情页:顶部文件名与格式切换,下方按元数据分组渲染 7 列表格。
/// 数据全部来自共享 <see cref="ExifDetailViewModel"/>(含惰性加载、当前格式状态),本页只读不写业务状态。
/// </summary>
internal sealed class WindowsExifToolPage : Grid
{
    private readonly ToolsViewModel _tools;
    private readonly TextBlock _fileName;
    private readonly SelectorBar _formatSelector;
    private readonly TextBlock _formatLabel;
    private readonly StackPanel _loading;
    private readonly TextBlock _emptyState;
    private readonly StackPanel _groupsHost;
    private ExifDetailViewModel? _detail;
    private List<string> _formatLabels = [];
    private bool _updatingFormat;
    private bool _rebuildPending;
    private bool _rebuildWhenVisible;
    private bool _disposed;

    /// <summary>创建 EXIF 详情页并绑定共享工具壳。</summary>
    /// <param name="tools">共享工具壳视图模型</param>
    public WindowsExifToolPage(ToolsViewModel tools)
    {
        _tools = tools;

        _fileName = new TextBlock
        {
            Style = WindowsSettingsCard.TextStyle("BodyTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _formatLabel = new TextBlock
        {
            Style = WindowsSettingsCard.TextStyle("CaptionTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _formatSelector = new SelectorBar
        {
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _formatSelector.SelectionChanged += OnFormatSelectionChanged;

        var formatHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        formatHost.Children.Add(_formatLabel);
        formatHost.Children.Add(_formatSelector);

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 4),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_fileName);
        Grid.SetColumn(formatHost, 1);
        header.Children.Add(formatHost);

        _loading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        _loading.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _loading.Children.Add(new TextBlock
        {
            Text = "加载中…",
            Style = WindowsSettingsCard.TextStyle("CaptionTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        _emptyState = new TextBlock
        {
            Style = WindowsSettingsCard.TextStyle("BodyTextBlockStyle"),
            Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 32, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        _groupsHost = new StackPanel();

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_loading);
        body.Children.Add(_emptyState);
        body.Children.Add(_groupsHost);

        Children.Add(WindowsSettingsCard.Page("EXIF 详情", body));
        _tools.PropertyChanged += OnToolsChanged;
        Rebind();
    }

    /// <summary>当前渲染的分组数(验收用)。</summary>
    internal int GroupCount { get; private set; }

    /// <summary>当前渲染的标签行数(验收用)。</summary>
    internal int TagCount { get; private set; }

    /// <summary>格式切换项数(验收用)。</summary>
    internal int FormatCount => _detail?.Formats.Count ?? 0;

    /// <summary>是否显示格式切换控件(多格式时)。</summary>
    internal bool FormatSelectorVisible => _formatSelector.Visibility == Visibility.Visible;

    /// <summary>文件名文本(验收用)。</summary>
    internal string FileNameText => _fileName.Text;

    /// <summary>退订共享状态,窗口关闭时调用。</summary>
    internal void Detach()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tools.PropertyChanged -= OnToolsChanged;
        BindDetail(null);
    }

    /// <summary>共享工具状态变化:EXIF VM 被替换或可用性变化时重新绑定。</summary>
    private void OnToolsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ToolsViewModel.ExifDetail) or nameof(ToolsViewModel.IsExifDetailEnabled))
        {
            Rebind();
        }
    }

    /// <summary>切换到当前 EXIF VM,并在绑定变化时合并重建。</summary>
    private void Rebind()
    {
        if (_disposed)
        {
            return;
        }

        BindDetail(_tools.ExifDetail);
        ScheduleRebuild();
    }

    /// <summary>切换 VM 订阅;同一实例不重复订阅。</summary>
    /// <param name="detail">目标 VM;null 解绑</param>
    private void BindDetail(ExifDetailViewModel? detail)
    {
        if (ReferenceEquals(_detail, detail))
        {
            return;
        }

        if (_detail != null)
        {
            _detail.PropertyChanged -= OnDetailChanged;
        }

        _detail = detail;
        // VM 实例变化时强制重建格式切换项,避免继续引用上一个 VM 的 FormatEntry。
        _formatLabels = [];
        if (_detail != null)
        {
            _detail.PropertyChanged += OnDetailChanged;
        }
    }

    /// <summary>EXIF 数据/格式/加载状态变化:合并到一次 dispatcher turn 内重建。</summary>
    private void OnDetailChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _detail))
        {
            return;
        }

        ScheduleRebuild();
    }

    /// <summary>页面切到前台时补做隐藏期间的挂起重建;避免主图翻页时后台重建整张 EXIF 表。</summary>
    internal void SyncWhenVisible()
    {
        if (_disposed || Visibility != Visibility.Visible || !_rebuildWhenVisible)
        {
            return;
        }

        _rebuildWhenVisible = false;
        ScheduleRebuild();
    }

    /// <summary>安排一次异步重建,避免属性 setter 内同步改 UI 触发重入。</summary>
    private void ScheduleRebuild()
    {
        if (_rebuildPending || _disposed)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            _rebuildWhenVisible = true;
            return;
        }

        _rebuildPending = true;
        UiDispatcher.Post(() =>
        {
            _rebuildPending = false;
            if (_disposed)
            {
                return;
            }

            if (Visibility != Visibility.Visible)
            {
                _rebuildWhenVisible = true;
                return;
            }

            Rebuild();
        });
    }

    /// <summary>按当前 VM 重建文件名、格式切换与全部分组表格。</summary>
    private void Rebuild()
    {
        var detail = _detail;
        if (detail == null)
        {
            _fileName.Text = string.Empty;
            _formatSelector.Visibility = Visibility.Collapsed;
            _formatLabel.Visibility = Visibility.Collapsed;
            _loading.Visibility = Visibility.Collapsed;
            _groupsHost.Children.Clear();
            GroupCount = 0;
            TagCount = 0;
            SetEmpty("当前照片没有可显示的 EXIF 数据");
            return;
        }

        _fileName.Text = detail.BaseFileName;
        SyncFormatSelector(detail);
        _formatLabel.Text = detail.HasMultipleFormats ? string.Empty : detail.SelectedFormat.Label;
        _formatLabel.Visibility = detail.HasMultipleFormats ? Visibility.Collapsed : Visibility.Visible;
        _loading.Visibility = detail.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        var groups = detail.Groups;
        _groupsHost.Children.Clear();
        GroupCount = groups?.Count ?? 0;
        TagCount = 0;
        if (groups != null)
        {
            foreach (var group in groups)
            {
                TagCount += group.Tags.Count;
                _groupsHost.Children.Add(WindowsSettingsCard.SectionHeader(group.Name));
                _groupsHost.Children.Add(BuildGroupCard(group));
            }
        }

        if (detail.IsLoading)
        {
            SetEmpty(null);
        }
        else if (GroupCount == 0)
        {
            SetEmpty("没有可显示的 EXIF 数据");
        }
        else
        {
            SetEmpty(null);
        }
    }

    /// <summary>重建/同步格式切换项:格式集合变化时才重建条目,仅选中变化时只改选中项。</summary>
    /// <param name="detail">当前 EXIF VM</param>
    private void SyncFormatSelector(ExifDetailViewModel detail)
    {
        var labels = detail.Formats.Select(entry => entry.Label).ToList();
        bool labelsChanged = labels.Count != _formatLabels.Count
            || !labels.SequenceEqual(_formatLabels, StringComparer.Ordinal);
        _updatingFormat = true;
        try
        {
            if (labelsChanged)
            {
                _formatLabels = labels;
                _formatSelector.Items.Clear();
                foreach (var entry in detail.Formats)
                {
                    _formatSelector.Items.Add(new SelectorBarItem
                    {
                        Text = entry.Label,
                        Tag = entry,
                    });
                }
            }

            SelectorBarItem? selected = null;
            foreach (var item in _formatSelector.Items.OfType<SelectorBarItem>())
            {
                if (ReferenceEquals(item.Tag, detail.SelectedFormat))
                {
                    selected = item;
                    break;
                }
            }

            _formatSelector.SelectedItem = selected;
        }
        finally
        {
            _updatingFormat = false;
        }

        _formatSelector.Visibility = detail.HasMultipleFormats ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>构建一个元数据分组:分组标题下方的卡片,内部是 7 列行与行分隔线。</summary>
    /// <param name="group">元数据分组</param>
    /// <returns>设置卡片</returns>
    private SettingsCard BuildGroupCard(MetadataGroup group)
    {
        var rows = new StackPanel();
        foreach (var tag in group.Tags)
        {
            rows.Children.Add(BuildRow(tag));
        }

        return WindowsSettingsCard.Card(rows, new Thickness(0));
    }

    /// <summary>构建单条元数据的 7 列行:ID / 名称 / 中文名 / 值 + 三条列分隔线与行底分隔线。</summary>
    /// <param name="tag">元数据标签</param>
    /// <returns>行容器</returns>
    private static Grid BuildRow(MetadataTag tag)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowsLayout.ToolsTableIdWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

        var idCell = new Border
        {
            Background = WindowsSettingsCard.ThemeBrush("ControlFillColorDefaultBrush"),
            Child = new TextBlock
            {
                Text = tag.TagIdHex,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = WindowsSettingsCard.ThemeBrush("TextFillColorTertiaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(idCell);

        AddDivider(row, 1);
        AddCell(row, 2, tag.Name, "TextFillColorSecondaryBrush", 0);
        AddDivider(row, 3);
        AddCell(row, 4, tag.ChineseName ?? string.Empty, "TextFillColorTertiaryBrush", 0);
        AddDivider(row, 5);
        AddCell(row, 6, tag.Value, "TextFillColorPrimaryBrush", WindowsLayout.ToolsValueMaxLines);

        var rowDivider = new Border
        {
            Background = WindowsSettingsCard.ThemeBrush("DividerStrokeColorDefaultBrush"),
        };
        Grid.SetRow(rowDivider, 1);
        Grid.SetColumnSpan(rowDivider, 7);
        row.Children.Add(rowDivider);
        return row;
    }

    /// <summary>在行内放一条列分隔线。</summary>
    /// <param name="row">目标行</param>
    /// <param name="column">列索引</param>
    private static void AddDivider(Grid row, int column)
    {
        var divider = new Border
        {
            Width = 1,
            Background = WindowsSettingsCard.ThemeBrush("DividerStrokeColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(divider, column);
        row.Children.Add(divider);
    }

    /// <summary>在行内放一个可选中文本单元格。</summary>
    /// <param name="row">目标行</param>
    /// <param name="column">列索引</param>
    /// <param name="text">文本</param>
    /// <param name="brushKey">文字色主题键</param>
    /// <param name="maxLines">最多行数;0 表示不限</param>
    private static void AddCell(Grid row, int column, string text, string brushKey, int maxLines)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = WindowsSettingsCard.ThemeBrush(brushKey),
            Padding = new Thickness(
                WindowsLayout.ToolsTableCellPaddingX,
                WindowsLayout.ToolsTableCellPaddingY,
                WindowsLayout.ToolsTableCellPaddingX,
                WindowsLayout.ToolsTableCellPaddingY),
            TextWrapping = maxLines > 0 ? TextWrapping.Wrap : TextWrapping.WrapWholeWords,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (maxLines > 0)
        {
            block.MaxLines = maxLines;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        Grid.SetColumn(block, column);
        row.Children.Add(block);
    }

    /// <summary>显示/隐藏空状态文本。</summary>
    /// <param name="text">空状态文本;null 隐藏</param>
    private void SetEmpty(string? text)
    {
        bool visible = !string.IsNullOrEmpty(text);
        _emptyState.Text = visible ? text : string.Empty;
        _emptyState.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>格式切换:写回共享 VM,由其惰性加载并触发后续重建。</summary>
    private void OnFormatSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updatingFormat || _detail == null)
        {
            return;
        }

        if (sender.SelectedItem is SelectorBarItem { Tag: FormatEntry entry }
            && !ReferenceEquals(_detail.SelectedFormat, entry))
        {
            _detail.SelectedFormat = entry;
        }
    }
}
