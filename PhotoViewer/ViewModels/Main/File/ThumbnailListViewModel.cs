using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.ViewModels.Main.File;

/// <summary>
/// 主缩略图列表的视图模型。
/// 职责:筛选/排序文件源、提供 <see cref="FilteredFiles"/> 给 UI、调度可见区域的缩略图加载与位图预取。
/// </summary>
public class ThumbnailListViewModel : ReactiveObject
{
    private readonly MainViewModel _main;
    private readonly FolderViewModel _folder;
    private readonly FilterBarViewModel _filter;

    private List<ImageFile> _filteredFiles = new();
    /// <summary>
    /// 筛选后的文件列表,绑定到主缩略图列表的 ItemsSource。
    /// 每次重新赋值时通知 UI 整体刷新(原子替换,避免逐条 Add 的 N 次 CollectionChanged)。
    /// </summary>
    public List<ImageFile> FilteredFiles
    {
        get => _filteredFiles;
        private set => this.RaiseAndSetIfChanged(ref _filteredFiles, value);
    }

    /// <summary>当前布局是否为行布局（分栏位于上下，影响列表方向、滚动条朝向、对齐方式）</summary>
    public bool IsRowLayout => _main.IsRowLayout;

    /// <summary>设置引用,用于绑定 ShowRating 等</summary>
    public SettingsViewModel Settings => _main.Settings;

    /// <summary>主视图模型引用,模板内绑定 IsCurrent 高亮等</summary>
    public MainViewModel Main => _main;

    /// <summary>滚动到当前图片的事件,由 View 订阅</summary>
    public event Action? ScrollToCurrentRequested;

    private readonly BitmapPrefetcher _bitmapPrefetcher;

    /// <summary>
    /// 最近浏览过的路径 LRU(新→旧),容量 = 预取前后窗口之和,用于位图淘汰保护集。
    /// </summary>
    private readonly List<string> _recentViewedPaths = new();

    /// <summary>
    /// 被保留的文件:改星级导致不再符合筛选条件时,暂时保留在列表中,
    /// 待用户切走后再移除,避免丢失滚动进度。
    /// </summary>
    private ImageFile? _retainedFile;

    /// <summary>
    /// 当前照片仍在新筛选结果中时的旧列表快照与索引，供当前照片被筛掉后定位上一张/下一张。
    /// 连续改筛选时保留最近一次仍包含当前照片的列表，锚点语义始终指向它被排除前的位置。
    /// </summary>
    private List<ImageFile>? _anchorSnapshot;
    private int _anchorIndex = -1;

    /// <summary>
    /// 构造主缩略图列表视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="folder">文件源视图模型</param>
    /// <param name="filter">筛选条视图模型</param>
    public ThumbnailListViewModel(MainViewModel main, FolderViewModel folder, FilterBarViewModel filter)
    {
        _main = main;
        _folder = folder;
        _filter = filter;

        _filter.BindFilteredCountProvider(() => _filteredFiles.Count);

        _main.WhenAnyValue(x => x.IsRowLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRowLayout)));

        _main.Settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
        _main.Settings.WhenAnyValue(s => s.SameNameAsOnePhoto)
            .Subscribe(_ => ApplyFilter());

        _filter.FilterChanged += OnFilterChanged;
        _filter.SortChanged += ApplySort;

        _folder.AllFilesChanged += ApplyFilter;
        _folder.ScrollToCurrentRequested += () => ScrollToCurrentRequested?.Invoke();
        _folder.PriorityThumbnailRequested += file => QueueThumbnailLoad(file, priority: true);

        BitmapLoader.CacheStatusChanged += OnCacheStatusChanged;

        _bitmapPrefetcher = new BitmapPrefetcher(_main, this);

        _main.WhenAnyValue(m => m.CurrentFile)
            .Subscribe(file =>
            {
                // FILTER-05：切图后锚点失效，翻页重新以当前照片的列表索引为准。
                _anchorSnapshot = null;
                _anchorIndex = -1;

                _bitmapPrefetcher.PrefetchAroundCurrent();
                UpdateProtectedPaths(file);
                if (_retainedFile != null && !ReferenceEquals(file, _retainedFile))
                {
                    _retainedFile = null;
                    ApplyFilter();
                }
            });
    }

    /// <summary>
    /// 当前图变更时刷新位图淘汰保护集:FilteredFiles 中当前 ±(PreloadBackward+PreloadForward)
    /// 范围的路径 + 最近浏览过的 M 张(M = 前后窗口之和)。
    /// </summary>
    private void UpdateProtectedPaths(ImageFile? currentFile)
    {
        var settings = _main.Settings;
        int window = Math.Max(0, settings.PreloadBackwardCount) + Math.Max(0, settings.PreloadForwardCount);
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (currentFile != null)
        {
            // 最近浏览 LRU:新路径置顶,容量 = 前后窗口之和(至少 1)
            var currentPath = currentFile.File.Path.LocalPath;
            _recentViewedPaths.Remove(currentPath);
            _recentViewedPaths.Insert(0, currentPath);
            int m = Math.Max(1, window);
            if (_recentViewedPaths.Count > m)
                _recentViewedPaths.RemoveRange(m, _recentViewedPaths.Count - m);

            var files = _filteredFiles;
            var idx = files.IndexOf(currentFile);
            if (idx >= 0)
            {
                int start = Math.Max(0, idx - window);
                int end = Math.Min(files.Count - 1, idx + window);
                for (int i = start; i <= end; i++)
                    protectedPaths.Add(files[i].File.Path.LocalPath);
            }
        }

        foreach (var p in _recentViewedPaths)
            protectedPaths.Add(p);

        BitmapLoader.SetProtectedPaths(protectedPaths);
    }

    /// <summary>
    /// 选中点击的图片(命令绑定)。
    /// </summary>
    /// <param name="file">被点击的缩略图项</param>
    public void SelectImageCommand(ImageFile file)
    {
        _main.CurrentFile = file;
    }

    /// <summary>
    /// 显式补触发一次当前图邻图预取(由 <see cref="FolderViewModel"/> 在列表补全后调用,
    /// 避免打开单个文件时阶段1 列表仅 1 项导致预取空跑)。
    /// </summary>
    public void TriggerPrefetchAroundCurrent() => _bitmapPrefetcher.PrefetchAroundCurrent();

    /// <summary>
    /// 当 BitmapLoader 缓存状态变化时,在 UI 线程同步刷新对应文件的缓存边框。
    /// </summary>
    /// <summary>
    /// 当 BitmapLoader 缓存状态变化时,在 UI 线程同步刷新对应文件:Full 档 → 缓存边框;Thumb 档 → 重读缩略图视图(淘汰回占位符,就绪上屏)。
    /// </summary>
    private void OnCacheStatusChanged(string filePath, ImageSizeClass sizeClass, bool isInCache)
    {
        UiDispatcher.Post(() =>
        {
            var imageFile = _folder.AllFiles.FirstOrDefault(f => f.File.Path.LocalPath == filePath);
            if (imageFile == null) return;

            if (sizeClass == ImageSizeClass.Thumb)
            {
                imageFile.NotifyThumbnailChanged();
                return;
            }

            // 仅 Full 档驱动"已解码"边框;Proxy 档不计入
            if (sizeClass != ImageSizeClass.Full) return;

            imageFile.IsInCache = isInCache;
        });
    }

    private void OnFilterChanged()
    {
        ApplyFilter();
        LoadVisibleThumbnailsAfterFilter();
    }

    /// <summary>
    /// 根据筛选条状态与设置重算筛选后的文件列表,并触发计数变更通知。
    /// </summary>
    public void ApplyFilter()
    {
        // FILTER-05：记录重算前当前照片在旧列表中的索引；只有它仍在旧列表中时才更新锚点，
        // 这样连续改筛选时锚点始终指向当前照片被排除前的位置。
        if (_main.CurrentFile is { } anchorFile)
        {
            var oldIndex = _filteredFiles.IndexOf(anchorFile);
            if (oldIndex >= 0)
            {
                _anchorSnapshot = _filteredFiles;
                _anchorIndex = oldIndex;
            }
        }

        var allFiles = _folder.AllFiles;

        var filtered = allFiles.Where(f =>
            _main.Settings.SelectedFormats.Contains(
                System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant() ?? string.Empty
            )
        ).ToList();

        if (_main.Settings.SameNameAsOnePhoto)
        {
            var order = _main.Settings.SelectedFormats;
            var grouped = filtered
                .GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<ImageFile> merged = new();
            foreach (var g in grouped)
            {
                if (g.Count() == 1)
                {
                    var single = g.First();
                    single.ResetGrouping();
                    merged.Add(single);
                    continue;
                }

                ImageFile representative = g
                    .OrderBy(f =>
                    {
                        var ext = System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant() ?? "";
                        var idx = order.IndexOf(ext);
                        return idx < 0 ? int.MaxValue : idx;
                    })
                    .First();

                representative.HiddenFiles.Clear();
                representative.HiddenFileRatings.Clear();
                foreach (var other in g)
                {
                    if (other != representative)
                    {
                        representative.HiddenFiles.Add(other.File);
                        representative.HiddenFileRatings[other.File.Name] = other.Rating;
                        other.ResetGrouping();
                    }
                }

                if (representative.HiddenFiles.Count > 0)
                {
                    var hiddenGroups = representative.HiddenFiles
                        .Select(f => _main.Settings.GetFormatDisplayNameByExtension(
                            System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant() ?? ""))
                        .Distinct()
                        .ToList();

                    representative.DisplayName = $"{representative.Name}({string.Join('/', hiddenGroups)})";
                }
                else
                {
                    representative.DisplayName = representative.Name;
                }

                merged.Add(representative);
            }

            filtered = merged;
        }
        else
        {
            foreach (var f in filtered)
            {
                f.ResetGrouping();
            }
        }

        var ratingKey = _filter.SelectedRatingFilter;
        filtered = filtered.Where(f =>
        {
            if (ReferenceEquals(f, _retainedFile)) return true;
            var r = f.Rating;
            return ratingKey switch
            {
                "All" => true,
                "None" => r == 0,
                "Eq1" => r == 1,
                "Eq2" => r == 2,
                "Eq3" => r == 3,
                "Eq4" => r == 4,
                "Eq5" => r == 5,
                "Gt1" => r >= 1,
                "Gt2" => r >= 2,
                "Gt3" => r >= 3,
                "Gt4" => r >= 4,
                "Conflict" => f.HasRatingConflict,
                _ => true
            };
        }).ToList();

        filtered = SortFileList(filtered);
        // FILTER-04：先提交列表再通知计数，保证同一帧内列表与文本一致。
        FilteredFiles = filtered;

        // 同名合并后，文件夹打开时可能把“被隐藏”的 ARW/HIF 设为 CurrentFile；
        // 主图与卡片会因此不是同一张。把当前文件映射回它所在组的代表文件。
        // FILTER-04：映射后仍要发出计数通知，列表与文本在同一次刷新中一起提交。
        if (_main.CurrentFile is { } current && !filtered.Contains(current))
        {
            var representative = filtered.FirstOrDefault(f =>
                f.HiddenFiles.Any(hidden => string.Equals(hidden.PathKey, current.File.PathKey, StringComparison.Ordinal)));
            if (representative != null)
            {
                _main.CurrentFile = representative;
            }
        }

        _filter.RaiseFilteredCountChanged();
    }

    /// <summary>
    /// 仅排序变化时调用,基于现有 FilteredFiles 重新排序。
    /// </summary>
    private void ApplySort()
    {
        FilteredFiles = SortFileList(_filteredFiles.ToList());
    }

    private List<ImageFile> SortFileList(List<ImageFile> files)
    {
        // FILTER-02：名称比较固定用序数忽略大小写，四端同序且不随系统区域设置变化。
        IEnumerable<ImageFile> sorted = _filter.SortMode switch
        {
            SortMode.Name => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),

            SortMode.Date => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue)
                : files.OrderByDescending(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue),

            SortMode.Size => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),

            _ => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        return sorted.ToList();
    }

    /// <summary>
    /// 对外刷新筛选(评分写入后调用)。
    /// <paramref name="retainFile"/> 非 null 时,即使该文件不再符合筛选条件也暂时保留,
    /// 待用户切走后再移除,避免丢失滚动进度。
    /// </summary>
    public void RefreshFilters(ImageFile? retainFile = null)
    {
        _retainedFile = retainFile;
        ApplyFilter();
    }

    /// <summary>
    /// 当前文件之前是否还存在可移动到的项。
    /// </summary>
    public bool HasPreviousFile() => ResolveMoveTarget(-1) != null;

    /// <summary>
    /// 当前文件之后是否还存在可移动到的项。
    /// </summary>
    public bool HasNextFile() => ResolveMoveTarget(1) != null;

    /// <summary>
    /// 在当前筛选结果中移动当前照片（上一张/下一张）。
    /// 当前照片已被筛选排除时，以它在重算前列表中的索引为参照，落到新列表中最近的前/后项，
    /// 不从列表首尾重新开始；端点不环绕。成功后设置 <see cref="MainViewModel.CurrentFile"/> 并滚动到该项。
    /// </summary>
    /// <param name="offset">方向偏移（-1 上一张，+1 下一张）。</param>
    /// <returns>是否发生了移动。</returns>
    public bool MoveCurrent(int offset)
    {
        var target = ResolveMoveTarget(offset);
        if (target == null) return false;

        _main.CurrentFile = target;
        ScrollToCurrent();
        return true;
    }

    /// <summary>
    /// 解析移动目标：当前照片仍在列表时取其相邻项；已不在列表时从锚点快照向 offset 方向
    /// 找第一个仍在新列表中的项。端点返回 null（不环绕）。
    /// </summary>
    private ImageFile? ResolveMoveTarget(int offset)
    {
        if (offset == 0) return null;

        var current = _main.CurrentFile;
        if (current == null || _filteredFiles.Count == 0) return null;

        int index = _filteredFiles.IndexOf(current);
        if (index >= 0)
        {
            int target = index + offset;
            return target >= 0 && target < _filteredFiles.Count ? _filteredFiles[target] : null;
        }

        // FILTER-05：锚点是当前照片被排除前的列表与索引；锚点自身已不在新列表，
        // 因此从相邻位置开始向目标方向查找。
        if (_anchorSnapshot == null || _anchorIndex < 0) return null;

        int step = Math.Sign(offset);
        for (int i = _anchorIndex + step; i >= 0 && i < _anchorSnapshot.Count; i += step)
        {
            var candidate = _anchorSnapshot[i];
            if (_filteredFiles.Contains(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// 触发滚动到当前文件的事件。
    /// </summary>
    public void ScrollToCurrent() => ScrollToCurrentRequested?.Invoke();

    /// <summary>
    /// 当前图片切换后预载附近少量文件的缩略图。
    /// </summary>
    public void PreloadNearbyFiles()
    {
        if (_main.CurrentFile == null || _filteredFiles.Count == 0) return;

        var index = _filteredFiles.IndexOf(_main.CurrentFile);
        if (index < 0) return;

        var start = Math.Max(0, index - 1);
        var end = Math.Min(_filteredFiles.Count - 1, index + 1);

        for (int i = start; i <= end; i++)
        {
            var file = _filteredFiles[i];
            QueueThumbnailLoad(file, priority: i == index);
        }
    }

    /// <summary>
    /// 筛选变化后加载首屏可见缩略图。
    /// </summary>
    private void LoadVisibleThumbnailsAfterFilter()
    {
        ClearThumbnailQueue();

        var visibleRange = _filteredFiles.Take(20).ToList();
        foreach (var file in visibleRange)
        {
            if (file.Thumbnail == null && !file.IsThumbnailLoading)
            {
                QueueThumbnailLoad(file);
            }
        }
    }

    #region ThumbnailLoading

    /// <summary>
    /// 将图片的缩略图加载投递到 <see cref="DecodeScheduler"/>。
    /// 同 key 已在队/在途时由调度器去重;高优先级请求可提升队列中该项的优先级。
    /// </summary>
    /// <param name="imageFile">目标图片</param>
    /// <param name="priority">是否优先加载(P1 可见/当前;否则 P2 普通排队)</param>
    public void QueueThumbnailLoad(ImageFile imageFile, bool priority = false)
    {
        if (imageFile.Thumbnail != null || imageFile.IsThumbnailLoading) return;

        DecodeScheduler.Submit(
            imageFile.File.Path.LocalPath + "#thumb",
            priority ? DecodePriority.P1_HighValue : DecodePriority.P2_Prefetch,
            _ => imageFile.LoadThumbnailAsync());
    }

    /// <summary>
    /// 批量加载可见区域的缩略图(全部按 P1 投递;滚走的普通排队项由调度器去重兜底,不再手动限流)。
    /// </summary>
    /// <param name="visibleFiles">View 上报的可见文件列表</param>
    public void LoadVisibleThumbnails(IEnumerable<ImageFile> visibleFiles)
    {
        foreach (var file in visibleFiles)
        {
            QueueThumbnailLoad(file, priority: true);
        }
    }

    /// <summary>
    /// 清空缩略图加载队列(调度器中全部待执行的 "#thumb" 工作项;在途项不受影响)。
    /// </summary>
    public void ClearThumbnailQueue()
    {
        DecodeScheduler.ClearQueuedByKeySuffix("#thumb");
    }

    /// <summary>
    /// 停止缩略图加载后台任务(全局停止调度器,取消所有在途工作项)。
    /// </summary>
    public void StopThumbnailLoading()
    {
        DecodeScheduler.Shutdown();
    }

    #endregion

    #region BitmapPrefetching

    /// <summary>
    /// View 滚动停止后上报可见范围,触发中心区域的位图预取。
    /// </summary>
    /// <param name="firstIndex">可见首项索引</param>
    /// <param name="lastIndex">可见末项索引</param>
    public void ReportVisibleRange(int firstIndex, int lastIndex)
    {
        if (firstIndex < 0 || lastIndex < firstIndex) return;
        if (_filteredFiles.Count == 0) return;
        if (firstIndex >= _filteredFiles.Count) return;
        if (lastIndex >= _filteredFiles.Count) lastIndex = _filteredFiles.Count - 1;
        _bitmapPrefetcher.PrefetchVisibleCenter(firstIndex, lastIndex);
    }

    /// <summary>
    /// 后台位图预取检查缩略图通道是否仍在繁忙,以让位于高优先级加载;原生分析栏的未提取回退同样需要它。
    /// </summary>
    public bool IsThumbnailLoadingBusy()
    {
        if (DecodeScheduler.GetQueuedCount(DecodePriority.P1_HighValue) > 0) return true;
        return _filteredFiles.Any(f => f.IsThumbnailLoading);
    }

    #endregion
}
