using System;
using Avalonia.Controls;
using PhotoViewer.ViewModels.Main.File;
using ReactiveUI;

namespace PhotoViewer.Views.Main.File;

public partial class FileView : UserControl
{
    public FileView()
    {
        InitializeComponent();

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
    /// 根据布局方向切换内容区列宽。
    /// IsRowLayout=true（按行布局，控件横向排列）= 文件栏顶部：两列各 *，聚类面板最多占一半。
    /// IsRowLayout=false（按列布局，控件纵向堆叠）= 文件栏侧边：主列固定 116px，聚类列 Auto。
    /// </summary>
    private void ApplyColumnLayout(bool isRowLayout)
    {
        if (ContentGrid is null) return;
        ContentGrid.ColumnDefinitions.Clear();
        if (isRowLayout)
        {
            // 文件栏顶部：主列和聚类列各占一半
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }
        else
        {
            // 文件栏侧边：主列固定宽，聚类列随内容
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(116, GridUnitType.Pixel));
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
    }
}
