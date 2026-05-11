using System;
using Avalonia.Controls;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.Views.Main.File;
using ReactiveUI;
using Avalonia.LogicalTree;
using System.Linq;

namespace PhotoViewer.Views.Main.File;

public partial class FileView : UserControl
{
    public FileView()
    {
        InitializeComponent();

        OuterGrid.SizeChanged += (_, _) => UpdateSimilarityMaxWidth();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is FileViewModel vm)
            {
                vm.WhenAnyValue(x => x.IsRowLayout)
                    .Subscribe(ApplyColumnLayout);
            }
        };
    }

    /// <summary>
    /// 根据布局方向切换文件栏外层列定义与筛选条跨列。
    /// IsRowLayout=true(按行布局,文件栏在顶部):主列 *,聚类列 Auto;筛选条横跨两列。
    /// IsRowLayout=false(按列布局,文件栏在侧边):主列固定 116px,聚类列 Auto;筛选条仅占主列保证左右对称。
    /// </summary>
    private void ApplyColumnLayout(bool isRowLayout)
    {
        if (OuterGrid is null || FilterHost is null) return;
        OuterGrid.ColumnDefinitions.Clear();
        if (isRowLayout)
        {
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumnSpan(FilterHost, 2);
        }
        else
        {
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(116, GridUnitType.Pixel));
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumnSpan(FilterHost, 1);
        }
        UpdateSimilarityMaxWidth();
    }

    /// <summary>
    /// 行布局下给相似聚类面板的 Border 设定 MaxWidth=容器一半,
    /// 使 ScrollViewer Viewport 受限,从而触发横向滚动;列布局下不约束。
    /// 直接设在 Border 上(而非 ColumnDefinition.MaxWidth)以确保 Viewport 准确传递。
    /// </summary>
    private void UpdateSimilarityMaxWidth()
    {
        if (OuterGrid is null) return;
        var isRowLayout = (DataContext as FileViewModel)?.IsRowLayout ?? false;
        var simHost = this.GetLogicalDescendants()
            .OfType<SimilarityListView>()
            .FirstOrDefault();
        if (simHost == null) return;
        simHost.MaxWidth = isRowLayout ? OuterGrid.Bounds.Width / 2 : double.PositiveInfinity;
    }
}

