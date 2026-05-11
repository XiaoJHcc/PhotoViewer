using System;
using Avalonia.Controls;
using PhotoViewer.ViewModels.Main.File;
using ReactiveUI;

namespace PhotoViewer.Views.Main.File;

public partial class FileView : UserControl
{
    private ColumnDefinition? _col1;

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
    /// IsRowLayout=true(按行布局,文件栏在顶部):主列 *,聚类列 Auto(最大不超过容器一半);筛选条横跨两列。
    /// IsRowLayout=false(按列布局,文件栏在侧边):主列固定 116px,聚类列 Auto;筛选条仅占主列保证左右对称。
    /// </summary>
    private void ApplyColumnLayout(bool isRowLayout)
    {
        if (OuterGrid is null || FilterHost is null) return;
        OuterGrid.ColumnDefinitions.Clear();
        if (isRowLayout)
        {
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            _col1 = new ColumnDefinition(GridLength.Auto);
            OuterGrid.ColumnDefinitions.Add(_col1);
            Grid.SetColumnSpan(FilterHost, 2);
        }
        else
        {
            OuterGrid.ColumnDefinitions.Add(new ColumnDefinition(116, GridUnitType.Pixel));
            _col1 = new ColumnDefinition(GridLength.Auto);
            OuterGrid.ColumnDefinitions.Add(_col1);
            Grid.SetColumnSpan(FilterHost, 1);
        }
        UpdateSimilarityMaxWidth();
    }

    /// <summary>
    /// 行布局下限制相似聚类列最大宽度为容器的一半,避免相似面板霸占整条文件栏;
    /// 列布局下不约束(Auto 会按内容自适应,主列已固定 116px)。
    /// </summary>
    private void UpdateSimilarityMaxWidth()
    {
        if (_col1 is null || OuterGrid is null) return;
        var isRowLayout = (DataContext as FileViewModel)?.IsRowLayout ?? false;
        _col1.MaxWidth = isRowLayout ? OuterGrid.Bounds.Width / 2 : double.PositiveInfinity;
    }
}
