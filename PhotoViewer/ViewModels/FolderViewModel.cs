using ReactiveUI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

// 下拉选框类
public class SortOption
{
    public string DisplayName { get; set; } // 中文显示
    public object Value { get; set; }       // 实际值
}

// 排序方式
public enum SortMode { Name, Date, Star, Size }
public enum SortOrder { Ascending, Descending }

public class FolderViewModel : ReactiveObject
{
    public MainViewModel Main { get; }
    
    // 布局方向（从主视图模型获取实际布局状态）
    public bool IsVerticalLayout => Main.IsHorizontalLayout;
    
    // 排序方式
    private SortMode _sortMode = SortMode.Name;
    private SortOrder _sortOrder = SortOrder.Ascending;
    
    // 当前文件夹和文件集合
    private IStorageFolder? _currentFolder;
    private readonly ObservableCollection<ImageFile> _allFiles = new();
    private readonly ObservableCollection<ImageFile> _filteredFiles = new();
    public ReadOnlyObservableCollection<ImageFile> AllFiles { get; }
    public ReadOnlyObservableCollection<ImageFile> FilteredFiles { get; }
    
    // 缩略图加载队列和并发控制
    private readonly ConcurrentQueue<ImageFile> _thumbnailLoadQueue = new();
    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(3); // 最多同时加载3个缩略图
    private readonly CancellationTokenSource _thumbnailCancellationTokenSource = new();
    private bool _isThumbnailLoadingActive = false;
    
    // 滚动到当前图片的事件
    public event Action? ScrollToCurrentRequested;
    
    public List<SortOption> SortModes { get; } =
    [
        new() { DisplayName = "名称", Value = SortMode.Name },
        new() { DisplayName = "修改日期", Value = SortMode.Date },
        new() { DisplayName = "文件大小", Value = SortMode.Size }
    ];

    public List<SortOption> SortOrders { get; } =
    [
        new() { DisplayName = "升序 ↑", Value = SortOrder.Ascending },
        new() { DisplayName = "降序 ↓", Value = SortOrder.Descending }
    ];
        
    public SortMode SortMode
    {
        get => _sortMode;
        set => this.RaiseAndSetIfChanged(ref _sortMode, value);
    }
        
    public SortOrder SortOrder
    {
        get => _sortOrder;
        set => this.RaiseAndSetIfChanged(ref _sortOrder, value);
    }
        
    private readonly BackgroundBitmapPrefetcher _bitmapPrefetcher; // 新增

    public FolderViewModel(MainViewModel main)
    {
        Main = main;
        
        // 初始化集合
        AllFiles = new ReadOnlyObservableCollection<ImageFile>(_allFiles);
        FilteredFiles = new ReadOnlyObservableCollection<ImageFile>(_filteredFiles);
        
        // 启动缩略图加载后台任务
        StartThumbnailLoadingTask();
        
        // 监听布局变化
        Main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));
            
        // 监听设置变化
        Main.Settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
        
        // 监听同名视为一张图片设置
        Main.Settings.WhenAnyValue(s => s.SameNameAsOnePhoto)
            .Subscribe(_ => ApplyFilter());
            
        this.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
            .Subscribe(_ => ApplySort());

        // 注册缓存状态变化事件
        BitmapLoader.CacheStatusChanged += OnCacheStatusChanged;

        _bitmapPrefetcher = new BackgroundBitmapPrefetcher(this);

        // 当前图片变化 -> 触发前后预取
        Main.WhenAnyValue(m => m.CurrentFile)
            .Subscribe(_ => _bitmapPrefetcher.PrefetchAroundCurrent());
    }

    /// <summary>
    /// 处理缓存状态变化
    /// </summary>
    private void OnCacheStatusChanged(string filePath, bool isInCache)
    {
        // 在UI线程中更新对应文件的缓存状态
        Dispatcher.UIThread.Post(() =>
        {
            var imageFile = AllFiles.FirstOrDefault(f => f.File.Path.LocalPath == filePath);
            if (imageFile != null)
            {
                imageFile.IsInCache = isInCache;
            }
        });
    }

    public void SelectImageCommand(ImageFile file)
    {
        Main.CurrentFile = file;
    }
    
    public void ScrollToCurrent()
    {
        ScrollToCurrentRequested?.Invoke();
    }
    
    ////////////////
    /// 打开文件
    ////////////////

    #region OpenFile
    
    /// <summary>
    /// 打开文件选择器
    /// </summary>
    public async Task OpenFilePickerAsync()
    {
        TopLevel? topLevel = null;
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            // 移动端：通过 MainView 获取 TopLevel
            if (App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
            {
                topLevel = TopLevel.GetTopLevel(singleView.MainView);
            }
        }
        else
        {
            // 桌面端：通过 MainWindow 获取 TopLevel
            if (App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            }
        }

        if (topLevel?.StorageProvider == null) return;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            // 移动平台：选择文件夹
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择图片文件夹",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                // 加载文件夹内第一个文件
                _currentFolder = folders[0];
                _allFiles.Clear();
                _filteredFiles.Clear();

                Console.WriteLine("OpenFolder: " + folders[0].Path);

                // 加载文件夹内容
                var items = folders[0].GetItemsAsync();
                await foreach (var storageItem in items)
                {
                    var item = (IStorageFile)storageItem;
    
                    if (IsImageFile(item.Name))
                    {
                        _allFiles.Add(new ImageFile(item));
                    }
                }

                ApplyFilter();

                if (_filteredFiles.Count > 0)
                {
                    Main.CurrentFile = _filteredFiles.First();
                    
                    // 立即为当前图片加载 EXIF 数据
                    _ = Task.Run(async () =>
                    {
                        await Main.CurrentFile.LoadExifDataAsync();
                        
                        // EXIF 加载完成后，在 UI 线程中触发属性更新
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Main.CurrentFile.RaisePropertyChanged(nameof(Main.CurrentFile.PhotoDate));
                            Main.CurrentFile.RaisePropertyChanged(nameof(Main.CurrentFile.RotationAngle));
                            Main.CurrentFile.RaisePropertyChanged(nameof(Main.CurrentFile.NeedsHorizontalFlip));
                            
                            // 触发 ControlViewModel 的 EXIF 数据更新（包括星级）
                            Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
                            Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
                        });
                    });
            
                    // 只为当前图片加载缩略图，其他的由可见性检测触发
                    QueueThumbnailLoad(Main.CurrentFile, priority: true);
                }
            }
        }
        else
        {
            // 桌面平台：选择图片文件
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                FileTypeFilter = GetFilePickerFileTypes(),
                AllowMultiple = false
            });

            if (files.Count > 0 && files[0] is IStorageFile file)
            {
                // 新打开文件时始终适配显示
                Main.ImageVM.Fit = true;
        
                // 加载图片所在文件夹
                await LoadNewImageFolder(file);
        
                // 加载文件夹后滚动至当前图片
                ScrollToCurrent();
            }
        }
    }

    // 打开文件选择器中的类型过滤器
    private List<FilePickerFileType> GetFilePickerFileTypes()
    {
        var fileTypes = new List<FilePickerFileType>();
        
        // 添加"所有支持的图片格式"选项
        var allSupportedType = new FilePickerFileType("所有图片")
        {
            AppleUniformTypeIdentifiers = new[] { "public.image" },
            MimeTypes = new[] { "image/*" },
            Patterns = Main.Settings.SelectedFormats.Select(format => $"*{format}").ToArray()
        };
        fileTypes.Add(allSupportedType);
        
        // 为每个已勾选的格式创建单独的文件类型
        foreach (var formatItem in Main.Settings.FileFormats.Where(f => f.IsEnabled))
        {
            var fileType = new FilePickerFileType($"{formatItem.DisplayName} 文件")
            {
                AppleUniformTypeIdentifiers = GetAppleTypeIdentifiers(formatItem.Extensions),
                MimeTypes = GetMimeTypes(formatItem.Extensions),
                Patterns = formatItem.Extensions.Select(ext => $"*{ext}").ToArray()
            };
            fileTypes.Add(fileType);
        }
        
        return fileTypes;
    }
    
    // 根据扩展名获取对应的 Apple 类型标识符
    private string[] GetAppleTypeIdentifiers(string[] extensions)
    {
        var identifiers = new List<string>();
        
        foreach (var ext in extensions)
        {
            var identifier = ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "public.jpeg",
                ".png" => "public.png",
                ".tiff" or ".tif" => "public.tiff",
                ".webp" => "public.webp",
                ".bmp" => "com.microsoft.bmp",
                ".gif" => "com.compuserve.gif",
                _ => "public.image"
            };
            
            if (!identifiers.Contains(identifier))
            {
                identifiers.Add(identifier);
            }
        }
        
        return identifiers.ToArray();
    }
    
    // 根据扩展名获取对应的 MIME 类型
    private string[] GetMimeTypes(string[] extensions)
    {
        var mimeTypes = new List<string>();
        
        foreach (var ext in extensions)
        {
            var mimeType = ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".tiff" or ".tif" => "image/tiff",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "image/*"
            };
            
            if (!mimeTypes.Contains(mimeType))
            {
                mimeTypes.Add(mimeType);
            }
        }
        
        return mimeTypes.ToArray();
    }
    
    #endregion
    
    
    ////////////////
    /// 加载文件夹 筛选
    ////////////////

    #region LoadFolder
    
    // 图片加载完成后 调用其他逻辑
    public async Task LoadNewImageFolder(IStorageFile file)
    {
        var folder = await file.GetParentAsync();
        if (folder == null || folder == _currentFolder) return;

        Main.CurrentFile = new ImageFile(file);
        
        // 优先加载当前图片的 EXIF 数据，暂不加载缩略图（由可见性检测触发）
        _ = Task.Run(async () => await Main.CurrentFile.LoadExifDataAsync());
        
        // 加载图片所在文件夹
        _currentFolder = folder;
        _allFiles.Clear();
        _filteredFiles.Clear();
        _allFiles.Add(Main.CurrentFile);
            
        // 加载文件夹内容（不自动加载缩略图）
        var items = folder.GetItemsAsync();

        await foreach (var storageItem in items)
        {
            var item = (IStorageFile)storageItem;
            if (IsImageFile(item.Name) && item.Name != Main.CurrentFile.Name)
            {
                var imageFile = new ImageFile(item);
                _allFiles.Add(imageFile);
                // 移除自动加载缩略图的逻辑
            }
        }
            
        ApplyFilter();

        // 加载完成后更新所有文件的缓存状态
        foreach (var imageFile in AllFiles)
        {
            imageFile.UpdateCacheStatus();
        }
        
        // 异步加载其他图片的 EXIF 数据（排除当前图片）
        _ = Task.Run(async () =>
        {
            var otherFiles = _filteredFiles.Where(f => f != Main.CurrentFile);
            await ExifLoader.LoadFolderExifDataAsync(otherFiles);
            
            // EXIF 加载完成后，触发UI更新以显示拍摄日期和旋转信息
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var file in otherFiles)
                {
                    file.RaisePropertyChanged(nameof(file.PhotoDate));
                    file.RaisePropertyChanged(nameof(file.RotationAngle));
                    file.RaisePropertyChanged(nameof(file.NeedsHorizontalFlip));
                }
            });
        });
    }
    
    public bool IsImageFile(string fileName)
    {
        if (IsHiddenFile(fileName)) return false;
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension != null && Main.Settings.SelectedFormats.Contains(extension);
    }

    public static bool IsHiddenFile(string fileName)
    {
        return fileName.StartsWith('.');
    }
    
    // 筛选文件夹内图片
    private void ApplyFilter()
    {
        var filtered = _allFiles.Where(f => 
            Main.Settings.SelectedFormats.Contains(
                System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant()
            )
        ).ToList();

        // 同名文件合并逻辑
        if (Main.Settings.SameNameAsOnePhoto)
        {
            var order = Main.Settings.SelectedFormats;
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

                // 选择代表文件：按 SelectedFormats 中扩展名索引最小优先
                ImageFile representative = g
                    .OrderBy(f =>
                    {
                        var ext = System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant() ?? "";
                        var idx = order.IndexOf(ext);
                        return idx < 0 ? int.MaxValue : idx;
                    })
                    .First();

                representative.HiddenFiles.Clear();
                foreach (var other in g)
                {
                    if (other != representative)
                    {
                        representative.HiddenFiles.Add(other.File);
                        // 非代表文件恢复原始显示名（避免切换设置后残留）
                        other.ResetGrouping();
                    }
                }

                if (representative.HiddenFiles.Count > 0)
                {
                    var hiddenGroups = representative.HiddenFiles
                        .Select(f => Main.Settings.GetFormatDisplayNameByExtension(
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
            // 未合并模式重置所有
            foreach (var f in filtered)
            {
                f.ResetGrouping();
            }
        }

        // 星级筛选附加阶段
        filtered = filtered.Where(f =>
        {
            var r = f.Rating;
            return SelectedRatingFilter switch
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
                _ => true
            };
        }).ToList();

        _filteredFiles.Clear();
        foreach (var file in filtered) _filteredFiles.Add(file);

        ApplySort();

        this.RaisePropertyChanged(nameof(FilteredCount)); // 更新计数
        
    }
    
    // 星级筛选项
    public class RatingFilterOption
    {
        public string DisplayName { get; set; } = "";
        public string Key { get; set; } = "";
    }
    
    // 评分筛选
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
    ];

    private string _selectedRatingFilter = "All";
    public string SelectedRatingFilter
    {
        get => _selectedRatingFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRatingFilter, value);
            ApplyFilter();
        }
    }

    public int FilteredCount => _filteredFiles.Count;

    // 对外刷新（评分变化后调用）
    public void RefreshFilters() => ApplyFilter();
    
    // 排序筛选后的图片
    private void ApplySort()
    {
        var sortedFiles = SortMode switch
        {
            SortMode.Name => SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.Name)
                : _filteredFiles.OrderByDescending(f => f.Name),
                
            SortMode.Date => SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue)
                : _filteredFiles.OrderByDescending(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue),
                
            SortMode.Size => SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.FileSize)
                : _filteredFiles.OrderByDescending(f => f.FileSize),
                
            _ => _filteredFiles.OrderBy(f => f.Name)
        };
        
        ObservableCollection<ImageFile> tempFiles = new();
        foreach (var file in sortedFiles)
        {
            tempFiles.Add(file);
        }
        
        _filteredFiles.Clear();
        foreach (var file in tempFiles)
        {
            _filteredFiles.Add(file);
        }
    }
    
    public bool HasPreviousFile()
    {
        if (Main.CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(Main.CurrentFile) > 0;
    }
        
    public bool HasNextFile()
    {
        if (Main.CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(Main.CurrentFile) < _filteredFiles.Count - 1;
    }
    
    // 优化预载逻辑，只预载少量附近的缩略图
    public void PreloadNearbyFiles()
    {
        if (Main.CurrentFile == null || _filteredFiles.Count == 0) return;
            
        var index = _filteredFiles.IndexOf(Main.CurrentFile);
        // 减少预载范围，只预载相邻的1-2个文件
        var start = Math.Max(0, index - 1);
        var end = Math.Min(_filteredFiles.Count - 1, index + 1);
            
        for (int i = start; i <= end; i++)
        {
            var file = _filteredFiles[i];
            
            // 优先加载当前图片，其次是相邻图片
            QueueThumbnailLoad(file, priority: i == index);
        }
    }
    
    #endregion
    
    
    ////////////////
    /// 缩略图异步加载
    ////////////////
    
    #region ThumbnailLoading
    
    /// <summary>
    /// 启动缩略图加载后台任务
    /// </summary>
    private void StartThumbnailLoadingTask()
    {
        _isThumbnailLoadingActive = true;
        _ = Task.Run(async () =>
        {
            while (_isThumbnailLoadingActive && !_thumbnailCancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_thumbnailLoadQueue.TryDequeue(out var imageFile))
                    {
                        await _thumbnailLoadSemaphore.WaitAsync(_thumbnailCancellationTokenSource.Token);
                        
                        try
                        {
                            // 检查是否已经加载过缩略图
                            if (imageFile.Thumbnail == null && !imageFile.IsThumbnailLoading)
                            {
                                await imageFile.LoadThumbnailAsync();
                            }
                        }
                        finally
                        {
                            _thumbnailLoadSemaphore.Release();
                        }
                    }
                    else
                    {
                        // 队列为空时等待一段时间
                        await Task.Delay(50, _thumbnailCancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("缩略图加载任务异常: " + ex.Message);
                }
            }
        });
    }
    
    /// <summary>
    /// 将图片文件加入缩略图加载队列
    /// </summary>
    /// <param name="imageFile">要加载缩略图的图片文件</param>
    /// <param name="priority">是否优先加载</param>
    public void QueueThumbnailLoad(ImageFile imageFile, bool priority = false)
    {
        if (imageFile.Thumbnail != null || imageFile.IsThumbnailLoading) return;
        
        if (priority)
        {
            // 优先加载：清空队列并重新排序
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
    /// 批量加载可见区域的缩略图
    /// </summary>
    /// <param name="visibleFiles">可见的图片文件列表</param>
    public void LoadVisibleThumbnails(IEnumerable<ImageFile> visibleFiles)
    {
        // 获取当前队列中的文件
        var queuedFiles = new HashSet<ImageFile>();
        var tempQueue = new List<ImageFile>();
        
        // 保存当前队列中未加载的文件
        while (_thumbnailLoadQueue.TryDequeue(out var existingFile))
        {
            if (existingFile.Thumbnail == null && !existingFile.IsThumbnailLoading)
            {
                queuedFiles.Add(existingFile);
                tempQueue.Add(existingFile);
            }
        }
        
        // 按优先级重新组织队列：可见文件优先
        var priorityFiles = new List<ImageFile>();
        var normalFiles = new List<ImageFile>();
        
        foreach (var file in visibleFiles)
        {
            if (file.Thumbnail == null && !file.IsThumbnailLoading)
            {
                priorityFiles.Add(file);
                queuedFiles.Remove(file); // 从普通队列中移除，避免重复
            }
        }
        
        // 限制普通文件数量，避免队列过长影响滚动性能
        var limitedNormalFiles = tempQueue.Where(f => queuedFiles.Contains(f)).Take(10).ToList();
        
        // 重新构建队列：优先文件在前
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
    /// 清空缩略图加载队列
    /// </summary>
    public void ClearThumbnailQueue()
    {
        while (_thumbnailLoadQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 停止缩略图加载
    /// </summary>
    public void StopThumbnailLoading()
    {
        _isThumbnailLoadingActive = false;
        _thumbnailCancellationTokenSource.Cancel();
    }
    
    #endregion
    
    
    ////////////////
    /// 位图预加载
    ////////////////

    #region BitmapPrefetching
    
    // 供缩略图视图滚动停止后调用，触发可见范围中心预取
    public void ReportVisibleRange(int firstIndex, int lastIndex)
    {
        if (firstIndex < 0 || lastIndex < firstIndex) return;
        if (_filteredFiles.Count == 0) return;
        if (firstIndex >= _filteredFiles.Count) return;
        if (lastIndex >= _filteredFiles.Count) lastIndex = _filteredFiles.Count - 1;
        _bitmapPrefetcher.PrefetchVisibleCenter(firstIndex, lastIndex);
    }
    
    // 供后台 Bitmap 预取降低优先级：判断缩略图是否仍然繁忙
    internal bool IsThumbnailLoadingBusy()
    {
        // 队列中还有待处理 或 仍有项处于加载中
        if (!_thumbnailLoadQueue.IsEmpty) return true;
        // 快速检测（数量大时可加短路）
        return _filteredFiles.Any(f => f.IsThumbnailLoading);
    }
    
    #endregion
    
    
}