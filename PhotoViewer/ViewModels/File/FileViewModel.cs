using ReactiveUI;
using System;

namespace PhotoViewer.ViewModels.File;

/// <summary>
/// 文件栏(File)容器视图模型,组合筛选条 + 主缩略图列表 + 相似聚类面板三块。
/// 自身不持有业务状态,仅协调三个子 VM 的生命周期。
/// </summary>
public class FileViewModel : ReactiveObject
{
    public MainViewModel Main { get; }

    public FilterBarViewModel FilterBar { get; }
    public ThumbnailListViewModel ThumbnailList { get; }
    public SimilarityPanelViewModel SimilarityPanel { get; }

    /// <summary>当前布局是否为竖向(决定 FileView 内三分区是横排还是纵排)</summary>
    public bool IsVerticalLayout => Main.IsHorizontalLayout;

    /// <summary>
    /// 构造文件栏容器视图模型,创建并连接三个子 VM。
    /// </summary>
    /// <param name="main">主视图模型</param>
    public FileViewModel(MainViewModel main)
    {
        Main = main;

        FilterBar = new FilterBarViewModel(main, main.FolderVM);
        ThumbnailList = new ThumbnailListViewModel(main, main.FolderVM, FilterBar);
        SimilarityPanel = new SimilarityPanelViewModel();

        main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));
    }
}
