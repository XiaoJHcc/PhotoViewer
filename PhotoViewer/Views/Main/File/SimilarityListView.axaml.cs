using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoViewer.Core.Similarity;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Views.Main.File;

/// <summary>
/// 相似聚类面板视图。
/// 数据由 <see cref="SimilarityPanelViewModel"/> 提供;模板与主缩略图列表保持一致,
/// 仅将"拍摄时间"位置替换为相似度百分比。
/// </summary>
public partial class SimilarityListView : UserControl
{
    public SimilarityPanelViewModel? ViewModel => DataContext as SimilarityPanelViewModel;

    public SimilarityListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 星级按钮点击:从 Tag 解析星级,转交 MainViewModel.SetRatingAsync。
    /// </summary>
    private void OnSimStarClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (sender is Button btn &&
            btn.Tag is string s &&
            int.TryParse(s, out var rating) &&
            btn.DataContext is SimilarityItem item)
        {
            _ = ViewModel.Main.SetRatingAsync(item.File, rating);
        }
    }
}
