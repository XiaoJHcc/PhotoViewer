using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;

namespace PhotoViewer.ViewModels.File;

/// <summary>
/// 文件栏顶部筛选/排序控件的视图模型。
/// 仅承载筛选、排序、计数等 UI 状态;实际的筛选执行由 <see cref="ThumbnailListViewModel"/> 订阅 <see cref="FilterChanged"/> 事件后完成。
/// </summary>
public class FilterBarViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly FolderViewModel _folder;
    private Func<int> _filteredCountProvider = () => 0;

    /// <summary>排序方式下拉选项</summary>
    public List<SortOption> SortModes { get; } =
    [
        new() { DisplayName = "名称", Value = SortMode.Name },
        new() { DisplayName = "拍摄时间", Value = SortMode.Date },
        new() { DisplayName = "文件大小", Value = SortMode.Size }
    ];

    /// <summary>排序方向下拉选项</summary>
    public List<SortOption> SortOrders { get; } =
    [
        new() { DisplayName = "升序 ↑", Value = SortOrder.Ascending },
        new() { DisplayName = "降序 ↓", Value = SortOrder.Descending }
    ];

    /// <summary>星级筛选选项</summary>
    public List<RatingFilterOption> RatingFilters { get; } =
    [
        new() { DisplayName = "全部", Key = "All" },
        new() { DisplayName = "无星级", Key = "None" },
        new() { DisplayName = "一星", Key = "Eq1" },
        new() { DisplayName = "二星", Key = "Eq2" },
        new() { DisplayName = "三星", Key = "Eq3" },
        new() { DisplayName = "四星", Key = "Eq4" },
        new() { DisplayName = "五星", Key = "Eq5" },
        new() { DisplayName = "一星以上", Key = "Gt1" },
        new() { DisplayName = "二星以上", Key = "Gt2" },
        new() { DisplayName = "三星以上", Key = "Gt3" },
        new() { DisplayName = "四星以上", Key = "Gt4" },
        new() { DisplayName = "星级冲突", Key = "Conflict" },
    ];

    private SortMode _sortMode = SortMode.Name;
    public SortMode SortMode
    {
        get => _sortMode;
        set => this.RaiseAndSetIfChanged(ref _sortMode, value);
    }

    private SortOrder _sortOrder = SortOrder.Ascending;
    public SortOrder SortOrder
    {
        get => _sortOrder;
        set => this.RaiseAndSetIfChanged(ref _sortOrder, value);
    }

    private string _selectedRatingFilter = "All";
    public string SelectedRatingFilter
    {
        get => _selectedRatingFilter;
        set => this.RaiseAndSetIfChanged(ref _selectedRatingFilter, value);
    }

    /// <summary>当前文件夹名称(只读,从 FolderViewModel 透传)</summary>
    public string FolderName => _folder.FolderName;

    /// <summary>筛选后文件计数(由 ThumbnailListViewModel 提供)</summary>
    public int FilteredCount => _filteredCountProvider();

    /// <summary>当前布局是否为竖向(决定筛选条横排/纵排)</summary>
    public bool IsVerticalLayout => _main.IsHorizontalLayout;

    /// <summary>设置引用,用于绑定显示星级开关</summary>
    public SettingsViewModel Settings => _main.Settings;

    /// <summary>排序模式或星级筛选发生变化时触发,由 ThumbnailListViewModel 订阅以重算筛选/排序</summary>
    public event Action? FilterChanged;

    /// <summary>仅排序变化时触发(可单独优化为不重算筛选)</summary>
    public event Action? SortChanged;

    /// <summary>
    /// 构造筛选条视图模型。
    /// </summary>
    /// <param name="main">主视图模型,用于读取布局/设置</param>
    /// <param name="folder">文件源视图模型,用于读取文件夹名</param>
    public FilterBarViewModel(MainViewModel main, FolderViewModel folder)
    {
        _main = main;
        _folder = folder;

        _main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));

        _folder.WhenAnyValue(x => x.FolderName)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FolderName)));

        this.WhenAnyValue(x => x.SortMode, x => x.SortOrder)
            .Skip(1)
            .Subscribe(_ => SortChanged?.Invoke());

        this.WhenAnyValue(x => x.SelectedRatingFilter)
            .Skip(1)
            .Subscribe(_ => FilterChanged?.Invoke());
    }

    /// <summary>
    /// 由 ThumbnailListViewModel 在初始化阶段提供计数源。
    /// </summary>
    /// <param name="provider">返回当前已筛选文件数量的委托</param>
    public void BindFilteredCountProvider(Func<int> provider)
    {
        _filteredCountProvider = provider;
    }

    /// <summary>
    /// 通知筛选计数已变化,触发 UI 更新。
    /// </summary>
    public void RaiseFilteredCountChanged() => this.RaisePropertyChanged(nameof(FilteredCount));
}

/// <summary>
/// 星级筛选选项,序列化键值由 <see cref="ThumbnailListViewModel.ApplyFilter"/> 解析。
/// </summary>
public class RatingFilterOption
{
    public string DisplayName { get; set; } = "";
    public string Key { get; set; } = "";
}
