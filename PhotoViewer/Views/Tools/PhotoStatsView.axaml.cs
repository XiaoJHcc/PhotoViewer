using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Views.Tools;

public partial class PhotoStatsView : UserControl
{
    /// <summary>初始化照片数据统计视图。</summary>
    public PhotoStatsView()
    {
        InitializeComponent();
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = true
        });

        var vm = DataContext as PhotoStatsViewModel;
        foreach (var folder in result)
            vm?.AddFolder(folder.Path.LocalPath);
    }

    private void OnRemoveFolderClick(object? sender, RoutedEventArgs e)
    {
        var selected = FolderListBox.SelectedItem as string;
        if (selected != null)
            (DataContext as PhotoStatsViewModel)?.RemoveFolder(selected);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as PhotoStatsViewModel;
        if (vm == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存统计结果",
            SuggestedFileName = "photo_stats.csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }
            }
        });

        if (file == null) return;
        await vm.ExportAsync(file.Path.LocalPath);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => (DataContext as PhotoStatsViewModel)?.CancelExport();
}
