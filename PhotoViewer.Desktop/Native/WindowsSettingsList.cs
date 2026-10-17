using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 设置页可排序列表:ListView 固定行高、内嵌卡片、不出现内部滚动。
/// 行是真实的 UIElement(本工程 ListView 的容器内容注入不生效,改为直接放行元素),
/// 拖拽重排后把新顺序回写到共享集合。
/// </summary>
internal sealed class WindowsSettingsList : Grid
{
    private readonly ListView _list;
    private readonly ObservableCollection<UIElement> _rows = [];
    private readonly IList _source;
    private readonly Action<int, int> _move;
    private readonly double _rowHeight;
    private bool _applyQueued;

    /// <summary>创建可排序列表。</summary>
    /// <param name="source">共享集合(重排结果回写目标)</param>
    /// <param name="rowFactory">行工厂;行必须设置 Item</param>
    /// <param name="move">共享集合的 Move(from,to)</param>
    /// <param name="rowHeight">行高</param>
    public WindowsSettingsList(IList source, Func<object, WindowsSettingsRowElement> rowFactory,
        Action<int, int> move, double rowHeight)
    {
        _source = source;
        _move = move;
        _rowHeight = rowHeight;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        foreach (var item in source)
        {
            var row = rowFactory(item);
            row.Item = item;
            row.Height = rowHeight;
            _rows.Add(row);
        }

        _list = new ListView
        {
            ItemsSource = _rows,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            CanDragItems = true,
            CanReorderItems = true,
            AllowDrop = true,
            ReorderMode = ListViewReorderMode.Enabled,
            Background = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollMode(_list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Hidden);
        _list.ContainerContentChanging += OnContainerContentChanging;

        Children.Add(_list);
        UpdateHeight();
        _rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>列表根元素。</summary>
    public UIElement Root => this;

    /// <summary>退订集合并释放行内订阅。</summary>
    public void Detach()
    {
        _rows.CollectionChanged -= OnRowsChanged;
        _list.ContainerContentChanging -= OnContainerContentChanging;
        foreach (var row in _rows)
        {
            (row as IDisposable)?.Dispose();
        }

        _rows.Clear();
    }

    /// <summary>行容器就绪时去掉默认内边距(不改 Content)。</summary>
    /// <param name="sender">列表</param>
    /// <param name="args">容器参数</param>
    private static void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        args.ItemContainer.Padding = new Thickness(0);
        args.ItemContainer.MinHeight = 0;
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        args.ItemContainer.VerticalContentAlignment = VerticalAlignment.Center;
    }

    /// <summary>集合变化(含拖拽重排)后同步高度并排队回写。</summary>
    /// <param name="sender">集合</param>
    /// <param name="args">变化参数</param>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        UpdateHeight();
        if (_applyQueued)
        {
            return;
        }

        _applyQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _applyQueued = false;
            ApplyOrder();
        });
    }

    /// <summary>把当前行顺序回写到共享集合(逐项 Move,保持单一数据源)。</summary>
    private void ApplyOrder()
    {
        var order = new List<object>(_rows.Count);
        foreach (var row in _rows)
        {
            if (row is WindowsSettingsRowElement { Item: { } item })
            {
                order.Add(item);
            }
        }

        for (var target = 0; target < order.Count; target++)
        {
            var current = _source.IndexOf(order[target]);
            if (current >= 0 && current != target)
            {
                _move(current, target);
            }
        }
    }

    /// <summary>按行数同步列表高度;滚动交给外层页面。</summary>
    private void UpdateHeight()
    {
        var count = Math.Max(1, _rows.Count);
        Height = count * _rowHeight;
        _list.Height = Height;
    }
}
