using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Views.Tools;

public partial class ToolsView : UserControl
{
    /// <summary>初始化工具页视图。</summary>
    public ToolsView()
    {
        InitializeComponent();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
        => (DataContext as ToolsViewModel)?.ShowList();

    private void OnOpenExifDetailClick(object? sender, RoutedEventArgs e)
        => (DataContext as ToolsViewModel)?.OpenExifDetail();

    private void OnOpenPhotoStatsClick(object? sender, RoutedEventArgs e)
        => (DataContext as ToolsViewModel)?.OpenPhotoStats();
}
