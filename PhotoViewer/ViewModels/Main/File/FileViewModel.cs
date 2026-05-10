using ReactiveUI;
using System;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 文件栏（File）容器视图模型，组合筛选条 + 主缩略图列表 + 相似聚类面板三块。
/// 自身不持有业务状态，仅协调三个子 VM 的生命周期。
/// </summary>
public class FileViewModel : ReactiveObject
{
    public MainViewModel Main { get; }

    public FilterBarViewModel FilterBar { get; }
    public ThumbnailListViewModel ThumbnailList { get; }
    public SimilarityPanelViewModel SimilarityPanel { get; }

    /// <summary>当前布局是否为行布局（分栏位于上下，内部横向排列）</summary>
    public bool IsRowLayout => Main.IsRowLayout;

    /// <summary>相似聚类面板是否展开（绑定到 FileView 的 IsVisible）</summary>
    public bool IsSimilarityPanelOpen => FilterBar.IsSimilarityPanelOpen;

    /// <summary>
    /// 构造文件栏容器视图模型，创建并连接三个子 VM。
    /// </summary>
    /// <param name="main">主视图模型</param>
    public FileViewModel(MainViewModel main)
    {
        Main = main;

        FilterBar = new FilterBarViewModel(main, main.FolderVM);
        ThumbnailList = new ThumbnailListViewModel(main, main.FolderVM, FilterBar);
        SimilarityPanel = new SimilarityPanelViewModel(main, ThumbnailList, main.FolderVM);

        main.WhenAnyValue(x => x.IsRowLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRowLayout)));

        // 面板展开时触发三态判定；关闭时无需操作
        FilterBar.SimilarityPanelToggled += isOpen =>
        {
            this.RaisePropertyChanged(nameof(IsSimilarityPanelOpen));
            if (isOpen)
                SimilarityPanel.OnPanelOpened();
        };
    }
}
