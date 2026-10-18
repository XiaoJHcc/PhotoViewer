using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using Windows.Foundation;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生筛选条:排序方式、升降序、星级筛选与筛选后计数,名称与取值语义与旧壳一致(契约 FILTER-01)。
/// 控件位置直接消费共享 <see cref="MainScreenLayout.FilterBarFrames"/>:行布局整组居中、
/// 列布局在 116 栏内纵排,相似聚类 toggle 高 27,不再维护第二套间距。
/// </summary>
internal sealed class WindowsFilterBar : Panel
{
    private readonly FilterBarViewModel _filter;
    private readonly ComboBox _sortMode;
    private readonly ComboBox _sortOrder;
    private readonly ComboBox _ratingFilter;
    private readonly TextBlock _count;
    private readonly ToggleButton _similarityToggle;
    private bool _syncing;
    private bool _isRowLayout = true;
    private MainScreenLayout.FilterFrames _frames;

    /// <summary>
    /// 创建筛选条。
    /// </summary>
    /// <param name="filter">筛选视图模型</param>
    public WindowsFilterBar(FilterBarViewModel filter)
    {
        _filter = filter;

        _sortMode = CreateComboBox();
        _sortMode.ItemsSource = _filter.SortModes.Select(option => option.DisplayName).ToList();
        _sortMode.SelectionChanged += OnSortModeChanged;

        _sortOrder = CreateComboBox();
        _sortOrder.ItemsSource = _filter.SortOrders.Select(option => option.DisplayName).ToList();
        _sortOrder.SelectionChanged += OnSortOrderChanged;

        _ratingFilter = CreateComboBox();
        _ratingFilter.ItemsSource = _filter.RatingFilters.Select(option => option.DisplayName).ToList();
        _ratingFilter.SelectionChanged += OnRatingFilterChanged;

        _similarityToggle = CreateSimilarityToggle();
        _count = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        Children.Add(_sortMode);
        Children.Add(_sortOrder);
        Children.Add(_ratingFilter);
        Children.Add(_count);
        Children.Add(_similarityToggle);

        _filter.PropertyChanged += OnFilterChanged;
        SyncFromViewModel();
    }

    /// <summary>
    /// 释放订阅。
    /// </summary>
    public void Detach() => _filter.PropertyChanged -= OnFilterChanged;

    /// <summary>
    /// 切换排布方向:上下布局横排居中,左右布局在文件栏单列内纵排。
    /// 位置统一由 <see cref="MainScreenLayout.FilterBarFrames"/> 给出。
    /// </summary>
    /// <param name="isRowLayout">是否为上下布局</param>
    public void SetOrientation(bool isRowLayout)
    {
        if (_isRowLayout == isRowLayout)
        {
            return;
        }

        _isRowLayout = isRowLayout;
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>刷新计数文本。</summary>
    public void RefreshCount() => _count.Text = $"共 {_filter.FilteredCount} 张";

    /// <summary>取得实际筛选控件的边界，供标题栏避让；不包含横向拉伸的外层网格。</summary>
    /// <param name="relativeTo">用于表示边界坐标的参考元素。</param>
    /// <returns>实际控件组合在参考元素内的逻辑坐标边界。</returns>
    internal Rect GetInteractiveBounds(UIElement relativeTo)
    {
        var sort = _frames.Sort;
        var order = _frames.Order;
        var rating = _frames.Rating;
        var count = _frames.Count;
        var toggle = _frames.Toggle;
        double left = Math.Min(Math.Min(sort.X, order.X), Math.Min(rating.X, Math.Min(count.X, toggle.X)));
        double top = Math.Min(Math.Min(sort.Y, order.Y), Math.Min(rating.Y, Math.Min(count.Y, toggle.Y)));
        double right = Math.Max(Math.Max(sort.MaxX, order.MaxX), Math.Max(rating.MaxX, Math.Max(count.MaxX, toggle.MaxX)));
        double bottom = Math.Max(Math.Max(sort.MaxY, order.MaxY), Math.Max(rating.MaxY, Math.Max(count.MaxY, toggle.MaxY)));
        return TransformToVisual(relativeTo).TransformBounds(new Rect(left, top, right - left, bottom - top));
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        double countWidth = Math.Max(1, _count.DesiredSize.Width);
        if (_isRowLayout)
        {
            double width = double.IsInfinity(availableSize.Width) ? DesiredRowWidth(countWidth) : availableSize.Width;
            _frames = MainScreenLayout.FilterBarFrames(true, width, 0, countWidth, true);
            return new Size(width, MainScreenLayout.FilterControlHeight + MainScreenLayout.FilterMargin * 2);
        }

        _frames = MainScreenLayout.FilterBarFrames(
            false, MainScreenLayout.FileBarContentWidth, MainScreenLayout.FilterMargin, countWidth, true);
        return new Size(
            MainScreenLayout.FileBarContentWidth,
            MainScreenLayout.FilterBarMainLength(false, MainScreenLayout.FilterMargin));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double countWidth = Math.Max(1, _count.DesiredSize.Width);
        _frames = _isRowLayout
            ? MainScreenLayout.FilterBarFrames(true, finalSize.Width, 0, countWidth, true)
            : MainScreenLayout.FilterBarFrames(
                false, MainScreenLayout.FileBarContentWidth, MainScreenLayout.FilterMargin, countWidth, true);

        ArrangeChild(_sortMode, _frames.Sort);
        ArrangeChild(_sortOrder, _frames.Order);
        ArrangeChild(_ratingFilter, _frames.Rating);
        ArrangeChild(_count, _frames.Count);
        ArrangeChild(_similarityToggle, _frames.Toggle);
        return finalSize;
    }

    /// <summary>行布局在未知宽度下的自然宽:三个控件 + 计数 + toggle + 间距与两侧外边距。</summary>
    /// <param name="countWidth">计数文本自然宽。</param>
    /// <returns>自然宽。</returns>
    private static double DesiredRowWidth(double countWidth)
        => MainScreenLayout.FilterMargin * 2
            + MainScreenLayout.FilterControlWidth * 3
            + MainScreenLayout.FilterGap * 4
            + countWidth
            + WindowsLayout.FilterToggleWidth;

    /// <summary>把子控件落到共享几何给出的矩形。</summary>
    /// <param name="child">子控件。</param>
    /// <param name="rect">目标矩形。</param>
    private static void ArrangeChild(UIElement child, LayoutRect rect)
        => child.Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));

    /// <summary>创建统一外观的下拉框(占位与旧壳同为 90×32)。</summary>
    private static ComboBox CreateComboBox() => new()
    {
        Width = WindowsLayout.FilterItemWidth,
        Height = WindowsLayout.FilterItemHeight,
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Center,
        CornerRadius = new CornerRadius(DesignTokens.FilterControlCornerRadius),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x44, 0x44, 0x44)),
    };

    /// <summary>
    /// 相似聚类开关(旧壳 FilterBarView 的 ToggleButton):90×27、选中态走蓝底蓝框,
    /// 直接写回 <see cref="FilterBarViewModel.IsSimilarityPanelOpen"/>(持久化在设置里)。
    /// WinUI 的 ToggleButton 模板用主题键画背景,必须按元素改键而不是只设 Background。
    /// </summary>
    private ToggleButton CreateSimilarityToggle()
    {
        var toggle = new ToggleButton
        {
            Content = "相似聚类",
            Width = WindowsLayout.FilterToggleWidth,
            Height = WindowsLayout.FilterToggleHeight,
            MinWidth = 0,
            MinHeight = 0,
            FontSize = WindowsLayout.SimilarityToggleFontSize,
            CornerRadius = new CornerRadius(DesignTokens.FilterToggleCornerRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(toggle, "展开/关闭相似聚类面板");
        toggle.Resources["ToggleButtonBackground"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        toggle.Resources["ToggleButtonBackgroundPointerOver"] = new SolidColorBrush(WindowsLayout.SimilarityToggleHover);
        toggle.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(WindowsLayout.SimilarityToggleCheckedBackground);
        toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(WindowsLayout.SimilarityToggleCheckedHover);
        toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(WindowsLayout.SimilarityToggleCheckedHover);
        toggle.Resources["ToggleButtonBorderBrush"] = new SolidColorBrush(WindowsLayout.SimilarityToggleBorder);
        toggle.Resources["ToggleButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0x66, 0x66));
        toggle.Resources["ToggleButtonBorderBrushChecked"] = new SolidColorBrush(WindowsLayout.SimilarityToggleCheckedBorder);
        toggle.Resources["ToggleButtonForeground"] = new SolidColorBrush(WindowsLayout.SimilarityToggleForeground);
        toggle.Resources["ToggleButtonForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));
        toggle.Resources["ToggleButtonForegroundChecked"] = new SolidColorBrush(WindowsLayout.SimilarityToggleCheckedForeground);
        toggle.Checked += (_, _) =>
        {
            if (!_syncing) _filter.IsSimilarityPanelOpen = true;
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!_syncing) _filter.IsSimilarityPanelOpen = false;
        };
        return toggle;
    }

    /// <summary>排序方式变化。</summary>
    private void OnSortModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _sortMode.SelectedIndex < 0)
        {
            return;
        }

        _filter.SortMode = (SortMode)_filter.SortModes[_sortMode.SelectedIndex].Value;
    }

    /// <summary>升降序变化。</summary>
    private void OnSortOrderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _sortOrder.SelectedIndex < 0)
        {
            return;
        }

        _filter.SortOrder = (SortOrder)_filter.SortOrders[_sortOrder.SelectedIndex].Value;
    }

    /// <summary>星级筛选变化。</summary>
    private void OnRatingFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _ratingFilter.SelectedIndex < 0)
        {
            return;
        }

        _filter.SelectedRatingFilter = _filter.RatingFilters[_ratingFilter.SelectedIndex].Key;
    }

    /// <summary>视图模型 → 控件(单向,不回写避免循环)。</summary>
    private void OnFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FilterBarViewModel.SortMode):
            case nameof(FilterBarViewModel.SortOrder):
            case nameof(FilterBarViewModel.SelectedRatingFilter):
            case nameof(FilterBarViewModel.IsSimilarityPanelOpen):
                SyncFromViewModel();
                break;
            case nameof(FilterBarViewModel.FilteredCount):
                RefreshCount();
                break;
        }
    }

    /// <summary>按视图模型选中当前值。</summary>
    private void SyncFromViewModel()
    {
        _syncing = true;
        try
        {
            _sortMode.SelectedIndex = IndexOf(_filter.SortModes.Select(option => (object)option.Value).ToList(), _filter.SortMode);
            _sortOrder.SelectedIndex = IndexOf(_filter.SortOrders.Select(option => (object)option.Value).ToList(), _filter.SortOrder);
            _ratingFilter.SelectedIndex = IndexOf(_filter.RatingFilters.Select(option => (object)option.Key).ToList(), _filter.SelectedRatingFilter);
            _similarityToggle.IsChecked = _filter.IsSimilarityPanelOpen;
            RefreshCount();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>查找取值对应的索引;找不到记录日志并返回 -1,避免配置漂移在 UI 回调里抛异常。</summary>
    private static int IndexOf(IReadOnlyList<object> values, object? target)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (Equals(values[index], target))
            {
                return index;
            }
        }

        NativeStartupLog.Write($"筛选选项缺少取值 {target};视图模型与控件选项不一致,已清空选择。");
        return -1;
    }
}
