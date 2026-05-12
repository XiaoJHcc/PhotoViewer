using Avalonia.Controls;

namespace PhotoViewer.Views.Main.File;

/// <summary>
/// 相似聚类面板视图:纯宿主 UserControl,具体数据与交互由 XAML 绑定到
/// <see cref="PhotoViewer.ViewModels.Main.File.SimilarityPanelViewModel"/> 完成;
/// 卡片/星级点击统一交给 <see cref="PhotoViewer.Controls.ThumbnailCard"/> 处理。
/// </summary>
public partial class SimilarityListView : UserControl
{
    public SimilarityListView()
    {
        InitializeComponent();
    }
}
