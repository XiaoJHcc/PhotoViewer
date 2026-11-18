using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 文件栏顶部筛选/排序控件的视图模型。
/// 仅承载筛选、排序、计数等 UI 状态；实际的筛选执行由 <see cref="ThumbnailListViewModel"/> 订阅 <see cref="FilterChanged"/> 事件后完成。
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

    /// <summary>星级筛选选项：首项「全部」表示不限条件，其余条件可多选并按并集命中。</summary>
    public List<RatingFilterOption> RatingFilters { get; } =
    [
        new() { DisplayName = "全部", Key = PhotoListProjection.AllRatings },
        new() { DisplayName = "无星级", Key = "None" },
        new() { DisplayName = "一星", Key = "Eq1" },
        new() { DisplayName = "二星", Key = "Eq2" },
        new() { DisplayName = "三星", Key = "Eq3" },
        new() { DisplayName = "四星", Key = "Eq4" },
        new() { DisplayName = "五星", Key = "Eq5" },
        new() { DisplayName = "已拒绝", Key = "Rejected" },
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

    private IReadOnlySet<string> _selectedRatingFilters = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>已选星级条件键；空集表示「全部」，集合内容不变时不发通知。</summary>
    public IReadOnlySet<string> SelectedRatingFilters
    {
        get => _selectedRatingFilters;
        set
        {
            var next = new HashSet<string>(
                value.Where(key => key != PhotoListProjection.AllRatings
                    && RatingFilters.Exists(option => option.Key == key)),
                StringComparer.Ordinal);
            if (next.SetEquals(_selectedRatingFilters)) return;
            _selectedRatingFilters = next;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(SelectedRatingFilter));
            this.RaisePropertyChanged(nameof(IsRatingFilterActive));
        }
    }

    /// <summary>兼容旧平台单选入口；多选状态下返回「全部」或唯一条件。</summary>
    public string SelectedRatingFilter
    {
        get => _selectedRatingFilters.Count == 1 ? _selectedRatingFilters.Single() : PhotoListProjection.AllRatings;
        set => SelectOnlyRatingFilter(value);
    }

    /// <summary>是否有生效的星级条件。</summary>
    public bool IsRatingFilterActive => _selectedRatingFilters.Count > 0;

    /// <summary>读取某个星级条件的勾选态；「全部」仅在空集合时选中。</summary>
    public bool IsRatingFilterSelected(string key)
        => key == PhotoListProjection.AllRatings ? !IsRatingFilterActive : _selectedRatingFilters.Contains(key);

    /// <summary>切换星级条件；点「全部」清空所有条件。</summary>
    public void ToggleRatingFilter(string key)
    {
        if (key == PhotoListProjection.AllRatings)
        {
            SelectedRatingFilters = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        var next = new HashSet<string>(_selectedRatingFilters, StringComparer.Ordinal);
        if (!next.Remove(key)) next.Add(key);
        SelectedRatingFilters = next;
    }

    /// <summary>兼容旧的单选写回入口。</summary>
    public void SelectOnlyRatingFilter(string key)
        => SelectedRatingFilters = key == PhotoListProjection.AllRatings
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>([key], StringComparer.Ordinal);

    /// <summary>相似聚类面板是否展开；初值从 Settings 读取，变更时写回 Settings 并触发 <see cref="SimilarityPanelToggled"/>。</summary>
    public bool IsSimilarityPanelOpen
    {
#if PHOTOVIEWER_DINO
        get => _main.Settings.SimilarityPanelExpanded;
#else
        get => false;
#endif
        set
        {
#if PHOTOVIEWER_DINO
            if (_main.Settings.SimilarityPanelExpanded == value) return;
            _main.Settings.SimilarityPanelExpanded = value;
            this.RaisePropertyChanged();
            SimilarityPanelToggled?.Invoke(value);
#endif
        }
    }

    /// <summary>当前文件夹名称（只读，从 FolderViewModel 透传）</summary>
    public string FolderName => _folder.FolderName;

    /// <summary>筛选后文件计数（由 ThumbnailListViewModel 提供）</summary>
    public int FilteredCount => _filteredCountProvider();

    /// <summary>当前布局是否为行布局（分栏位于上下，决定筛选条横排/纵排）</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>设置引用，用于绑定显示星级开关</summary>
    public SettingsViewModel Settings => _main.Settings;

    /// <summary>排序模式或星级筛选发生变化时触发，由 ThumbnailListViewModel 订阅以重算筛选/排序</summary>
    public event Action? FilterChanged;

    /// <summary>仅排序变化时触发（可单独优化为不重算筛选）</summary>
    public event Action? SortChanged;

#if PHOTOVIEWER_DINO
    /// <summary>相似聚类面板展开/关闭时触发，参数为新的展开状态。</summary>
    public event Action<bool>? SimilarityPanelToggled;
#endif

    /// <summary>
    /// 构造筛选条视图模型。
    /// </summary>
    /// <param name="main">主视图模型，用于读取布局/设置</param>
    /// <param name="folder">文件源视图模型，用于读取文件夹名</param>
    public FilterBarViewModel(MainViewModel main, FolderViewModel folder)
    {
        _main = main;
        _folder = folder;

        _main.WhenAnyValue(x => x.IsRowLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRowLayout)));

        _folder.WhenAnyValue(x => x.FolderName)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FolderName)));

        this.WhenAnyValue(x => x.SortMode, x => x.SortOrder)
            .Skip(1)
            .Subscribe(_ => SortChanged?.Invoke());

        this.WhenAnyValue(x => x.SelectedRatingFilters)
            .Skip(1)
            .Subscribe(_ => FilterChanged?.Invoke());

        // 当 Settings 里的值被外部（如 ApplyModel）改变时，同步通知 UI
        _main.Settings.WhenAnyValue(x => x.SimilarityPanelExpanded)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsSimilarityPanelOpen)));
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
    /// 通知筛选计数已变化，触发 UI 更新。
    /// </summary>
    public void RaiseFilteredCountChanged() => this.RaisePropertyChanged(nameof(FilteredCount));
}

/// <summary>
/// 星级筛选选项，序列化键值由 <see cref="ThumbnailListViewModel.ApplyFilter"/> 解析。
/// </summary>
public class RatingFilterOption
{
    public string DisplayName { get; set; } = "";
    public string Key { get; set; } = "";
}
