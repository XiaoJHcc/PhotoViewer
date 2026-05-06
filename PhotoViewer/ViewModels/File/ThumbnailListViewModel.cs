using ReactiveUI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels.File;

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

    /// <summary>当前布局是否为竖向(影响列表方向、滚动条朝向、对齐方式)</summary>
    public bool IsVerticalLayout => _main.IsHorizontalLayout;

    /// <summary>设置引用,用于绑定 ShowRating 等</summary>
    public SettingsViewModel Settings => _main.Settings;

    /// <summary>主视图模型引用,模板内绑定 IsCurrent 高亮等</summary>
    public MainViewModel Main => _main;

    /// <summary>滚动到当前图片的事件,由 View 订阅</summary>
    public event Action? ScrollToCurrentRequested;

    private readonly ConcurrentQueue<ImageFile> _thumbnailLoadQueue = new();
    private readonly CancellationTokenSource _thumbnailCancellationTokenSource = new();
    private bool _isThumbnailLoadingActive;

    private readonly BitmapPrefetcher _bitmapPrefetcher;

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

        StartThumbnailLoadingTask();

        _main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));

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
            .Subscribe(_ => _bitmapPrefetcher.PrefetchAroundCurrent());
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
    /// 当 BitmapLoader 缓存状态变化时,在 UI 线程同步刷新对应文件的缓存边框。
    /// </summary>
    private void OnCacheStatusChanged(string filePath, bool isInCache)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var imageFile = _folder.AllFiles.FirstOrDefault(f => f.File.Path.LocalPath == filePath);
            if (imageFile != null)
            {
                imageFile.IsInCache = isInCache;
            }
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
        FilteredFiles = filtered;

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
        IEnumerable<ImageFile> sorted = _filter.SortMode switch
        {
            SortMode.Name => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.Name)
                : files.OrderByDescending(f => f.Name),

            SortMode.Date => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue)
                : files.OrderByDescending(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue),

            SortMode.Size => _filter.SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),

            _ => files.OrderBy(f => f.Name)
        };
        return sorted.ToList();
    }

    /// <summary>
    /// 对外刷新筛选(评分写入后调用)。
    /// </summary>
    public void RefreshFilters() => ApplyFilter();

    /// <summary>
    /// 当前文件之前是否还存在已筛选项。
    /// </summary>
    public bool HasPreviousFile()
    {
        if (_main.CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(_main.CurrentFile) > 0;
    }

    /// <summary>
    /// 当前文件之后是否还存在已筛选项。
    /// </summary>
    public bool HasNextFile()
    {
        if (_main.CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(_main.CurrentFile) < _filteredFiles.Count - 1;
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
    /// 启动缩略图加载后台任务(多个并发消费者)。
    /// </summary>
    private void StartThumbnailLoadingTask()
    {
        _isThumbnailLoadingActive = true;
        for (int i = 0; i < 3; i++)
        {
            _ = Task.Run(async () =>
            {
                while (_isThumbnailLoadingActive && !_thumbnailCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (_thumbnailLoadQueue.TryDequeue(out var imageFile))
                        {
                            try
                            {
                                if (imageFile.Thumbnail == null && !imageFile.IsThumbnailLoading)
                                {
                                    await imageFile.LoadThumbnailAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("缩略图加载异常: " + ex.Message);
                            }
                        }
                        else
                        {
                            await Task.Delay(50, _thumbnailCancellationTokenSource.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }
    }

    /// <summary>
    /// 将图片加入缩略图加载队列。
    /// </summary>
    /// <param name="imageFile">目标图片</param>
    /// <param name="priority">是否优先加载</param>
    public void QueueThumbnailLoad(ImageFile imageFile, bool priority = false)
    {
        if (imageFile.Thumbnail != null || imageFile.IsThumbnailLoading) return;

        if (priority)
        {
            var tempQueue = new List<ImageFile> { imageFile };
            while (_thumbnailLoadQueue.TryDequeue(out var existingFile))
            {
                if (existingFile != imageFile && existingFile.Thumbnail == null && !existingFile.IsThumbnailLoading)
                {
                    tempQueue.Add(existingFile);
                }
            }
            foreach (var file in tempQueue)
            {
                _thumbnailLoadQueue.Enqueue(file);
            }
        }
        else
        {
            _thumbnailLoadQueue.Enqueue(imageFile);
        }
    }

    /// <summary>
    /// 批量加载可见区域的缩略图。
    /// </summary>
    /// <param name="visibleFiles">View 上报的可见文件列表</param>
    public void LoadVisibleThumbnails(IEnumerable<ImageFile> visibleFiles)
    {
        var queuedFiles = new HashSet<ImageFile>();
        var tempQueue = new List<ImageFile>();

        while (_thumbnailLoadQueue.TryDequeue(out var existingFile))
        {
            if (existingFile.Thumbnail == null && !existingFile.IsThumbnailLoading)
            {
                queuedFiles.Add(existingFile);
                tempQueue.Add(existingFile);
            }
        }

        var priorityFiles = new List<ImageFile>();
        foreach (var file in visibleFiles)
        {
            if (file.Thumbnail == null && !file.IsThumbnailLoading)
            {
                priorityFiles.Add(file);
                queuedFiles.Remove(file);
            }
        }

        var limitedNormalFiles = tempQueue.Where(f => queuedFiles.Contains(f)).Take(10).ToList();

        foreach (var file in priorityFiles)
        {
            _thumbnailLoadQueue.Enqueue(file);
        }
        foreach (var file in limitedNormalFiles)
        {
            _thumbnailLoadQueue.Enqueue(file);
        }
    }

    /// <summary>
    /// 清空缩略图加载队列。
    /// </summary>
    public void ClearThumbnailQueue()
    {
        while (_thumbnailLoadQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 停止缩略图加载后台任务。
    /// </summary>
    public void StopThumbnailLoading()
    {
        _isThumbnailLoadingActive = false;
        _thumbnailCancellationTokenSource.Cancel();
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
    /// 后台位图预取检查缩略图通道是否仍在繁忙,以让位于高优先级加载。
    /// </summary>
    internal bool IsThumbnailLoadingBusy()
    {
        if (!_thumbnailLoadQueue.IsEmpty) return true;
        return _filteredFiles.Any(f => f.IsThumbnailLoading);
    }

    #endregion
}
