using ReactiveUI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public string DisplayName { get; set; } = null!; // 中文显示
    public object Value { get; set; } = null!;       // 实际值
}

// 排序方式
public enum SortMode { Name, Date, Rating, Size }
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
    private readonly List<ImageFile> _allFiles = new();
    private List<ImageFile> _filteredFiles = new();
    public IReadOnlyList<ImageFile> AllFiles => _allFiles;

    /// <summary>
    /// 筛选后的文件列表，绑定到 ThumbnailView 的 ItemsSource。
    /// 每次重新赋值时通知 UI 整体刷新（原子替换，避免逐条 Add 的 N 次 CollectionChanged）。
    /// </summary>
    public List<ImageFile> FilteredFiles
    {
        get => _filteredFiles;
        private set => this.RaiseAndSetIfChanged(ref _filteredFiles, value);
    }
    
    // 缩略图加载队列和并发控制
    private readonly ConcurrentQueue<ImageFile> _thumbnailLoadQueue = new();
    private readonly CancellationTokenSource _thumbnailCancellationTokenSource = new();
    private bool _isThumbnailLoadingActive = false;
    
    // 添加EXIF加载状态跟踪
    private bool _isExifLoadingInProgress = false;
    
    // 滚动到当前图片的事件
    public event Action? ScrollToCurrentRequested;
    
    public List<SortOption> SortModes { get; } =
    [
        new() { DisplayName = "名称", Value = SortMode.Name },
        new() { DisplayName = "拍摄时间", Value = SortMode.Date },
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
        
    private readonly BitmapPrefetcher _bitmapPrefetcher; // 新增

    public FolderViewModel(MainViewModel main)
    {
        Main = main;
        
        // 初始化集合（_allFiles 和 _filteredFiles 已在字段初始化时创建）
        
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

        _bitmapPrefetcher = new BitmapPrefetcher(this);

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
    // 打开文件
    ////////////////

    #region OpenFile
    
    /// <summary>
    /// 打开文件选择器
    /// </summary>
    public async Task OpenFilePickerAsync()
    {
        var topLevel = GetCurrentTopLevel();

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
                await OpenFolderAsync(folders[0]);
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

            if (files.Count > 0)
            {
                var file = files[0];
                await OpenImageAsync(file);
            }
        }
    }

    /// <summary>
    /// 打开外部传入的一组文件，并优先使用首个可识别图片。
    /// </summary>
    /// <param name="files">外部传入的文件集合</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenExternalFilesAsync(IEnumerable<IStorageFile> files, bool scrollToCurrent = true)
    {
        var targetFile = files.FirstOrDefault(file => IsImageFile(file.Name));
        if (targetFile == null)
        {
            return;
        }

        await OpenImageAsync(targetFile, scrollToCurrent);
    }

    /// <summary>
    /// 打开指定图片，并尽量进入其所在文件夹；若平台权限不支持，则回退为单图模式。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenImageAsync(IStorageFile file, bool scrollToCurrent = true)
    {
        if (!IsImageFile(file.Name))
        {
            return;
        }

        // 新打开文件时始终适配显示
        Main.ImageVM.Fit = true;

        await OpenImageWithFolderFallbackAsync(file);

        if (scrollToCurrent)
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 打开指定文件夹，并定位到筛选后的第一张图片。
    /// </summary>
    /// <param name="folder">要打开的文件夹</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenFolderAsync(IStorageFolder folder, bool scrollToCurrent = true)
    {
        // 新打开目录时也恢复为适配显示，避免沿用上一次缩放状态。
        Main.ImageVM.Fit = true;

        await LoadFolderAsync(folder);

        if (scrollToCurrent)
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 获取当前主界面的 TopLevel。
    /// </summary>
    private static TopLevel? GetCurrentTopLevel()
    {
        return App.GetCurrentTopLevel();
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
    
    private string _folderName = "";
    public string FolderName
    {
        get => _folderName;
        set => this.RaiseAndSetIfChanged(ref _folderName, value);
    }
    
    /// <summary>
    /// 兼容旧调用路径：打开新图片所在文件夹。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    public async Task LoadNewImageFolder(IStorageFile file)
    {
        await OpenImageAsync(file, scrollToCurrent: false);
    }

    /// <summary>
    /// 优先尝试以“文件夹上下文”打开图片；若无法获取父目录，则回退为单图模式。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    private async Task OpenImageWithFolderFallbackAsync(IStorageFile file)
    {
        var folder = await file.GetParentAsync();

        if (folder == null)
        {
            await OpenSingleImageAsync(file);
            return;
        }

        if (IsSameStorageItem(folder, _currentFolder) && _allFiles.Count > 0)
        {
            FolderName = folder.Name;

            var existingFile = FindLoadedFile(file);
            if (existingFile == null)
            {
                existingFile = new ImageFile(file);
                _allFiles.Insert(0, existingFile);
                await existingFile.LoadRatingOnlyAsync();
                existingFile.UpdateCacheStatus();
                ApplyFilter();
            }

            await SetCurrentFileAsync(existingFile);
            return;
        }

        await LoadFolderAsync(folder, file);
    }

    /// <summary>
    /// 以单图模式打开图片。
    /// 当平台只授予单文件访问权限时，仍可保证用户至少能看到当前图片。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    private async Task OpenSingleImageAsync(IStorageFile file)
    {
        _currentFolder = null;
        FolderName = file.Name;
        _allFiles.Clear();
        _filteredFiles.Clear();

        var imageFile = new ImageFile(file);
        _allFiles.Add(imageFile);

        await imageFile.LoadRatingOnlyAsync();
        ApplyFilter();

        imageFile.UpdateCacheStatus();
        await SetCurrentFileAsync(imageFile);
    }

    /// <summary>
    /// 加载文件夹内容，并可选定位到指定图片。
    /// </summary>
    /// <param name="folder">要加载的文件夹</param>
    /// <param name="preferredFile">优先定位的图片；为空时打开第一张</param>
    private async Task LoadFolderAsync(IStorageFolder folder, IStorageFile? preferredFile = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("[Folder] === BEGIN OpenFolder: " + folder.Path);

        _currentFolder = folder;
        FolderName = folder.Name;
        _allFiles.Clear();
        _filteredFiles.Clear();

        // ── 阶段 1：优先显示目标图片 ──
        // 立即把用户点击/打开的图片加入列表并设为当前，使 UI 在毫秒级内有内容。
        ImageFile? targetFile = null;
        if (preferredFile != null && IsImageFile(preferredFile.Name))
        {
            targetFile = new ImageFile(preferredFile);
            _allFiles.Add(targetFile);
            ApplyFilter();
            targetFile.UpdateCacheStatus();
            await SetCurrentFileAsync(targetFile);
            Console.WriteLine($"[Folder] Phase1 (first image): {sw.ElapsedMilliseconds}ms");
        }

        // ── 阶段 2：枚举文件夹所有文件 ──
        // 枚举需在 UI 线程完成（iOS 的 security-scoped URL 可能有线程亲和性），
        // 但只做轻量的名称过滤，不做任何文件 I/O。
        var allNewFiles = new List<ImageFile>();
        var items = folder.GetItemsAsync();
        await foreach (var storageItem in items)
        {
            if (storageItem is not IStorageFile item) continue;
            if (!IsImageFile(item.Name)) continue;
            if (preferredFile != null && IsSameStorageItem(item, preferredFile)) continue;
            allNewFiles.Add(new ImageFile(item));
        }

        Console.WriteLine($"[Folder] Phase2 (enumerate {allNewFiles.Count} files): {sw.ElapsedMilliseconds}ms");

        // 一次性添加后执行筛选，ApplyFilter 内部原子替换 FilteredFiles 引用。
        foreach (var file in allNewFiles)
        {
            _allFiles.Add(file);
        }
        ApplyFilter();

        // 若阶段 1 未设置当前文件（例如直接打开文件夹而非打开某张图片），
        // 则定位到筛选后的第一张。
        if (targetFile == null)
        {
            var firstFile = _filteredFiles.FirstOrDefault();
            if (firstFile != null)
            {
                firstFile.UpdateCacheStatus();
                await SetCurrentFileAsync(firstFile);
            }
        }

        Console.WriteLine($"[Folder] Phase2 (UI updated): {sw.ElapsedMilliseconds}ms");

        // ── 阶段 3：后台加载基本属性和星级 ──
        foreach (var imageFile in _allFiles) imageFile.UpdateCacheStatus();

        _ = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(8);
            var propTasks = _allFiles.Select(async f =>
            {
                await semaphore.WaitAsync();
                try { await f.LoadBasicPropertiesAsync(); }
                finally { semaphore.Release(); }
            }).ToList();
            await Task.WhenAll(propTasks);
            Console.WriteLine($"[Folder] Phase3 (basic props loaded): {sw.ElapsedMilliseconds}ms");
        });

        _ = Task.Run(async () =>
        {
            await LoadAllExifRatingsAsync();
            await Dispatcher.UIThread.InvokeAsync(() => ApplyFilter());
            Console.WriteLine($"[Folder] Phase3 (ratings loaded): {sw.ElapsedMilliseconds}ms");
        });

        Console.WriteLine($"[Folder] === END OpenFolder (sync portion): {sw.ElapsedMilliseconds}ms ===");
    }

    /// <summary>
    /// 切换当前图片，并触发当前图与同目录其它图片的 EXIF 预加载。
    /// </summary>
    /// <param name="imageFile">要设为当前的图片</param>
    private Task SetCurrentFileAsync(ImageFile? imageFile)
    {
        Main.CurrentFile = imageFile;
        if (imageFile == null)
        {
            return Task.CompletedTask;
        }

        // 立即为当前图片加载完整 EXIF 数据。
        _ = Task.Run(async () =>
        {
            await imageFile.LoadExifDataAsync();

            // EXIF 加载完成后，在 UI 线程中触发属性更新。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                imageFile.RaisePropertyChanged(nameof(imageFile.PhotoDate));
                imageFile.RaisePropertyChanged(nameof(imageFile.RotationAngle));
                imageFile.RaisePropertyChanged(nameof(imageFile.NeedsHorizontalFlip));

                // 触发 ControlViewModel 的 EXIF 数据更新（包括星级）。
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
            });
        });

        QueueThumbnailLoad(imageFile, priority: true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 在已加载集合中查找同一个存储文件。
    /// </summary>
    /// <param name="file">要查找的存储文件</param>
    private ImageFile? FindLoadedFile(IStorageFile file)
    {
        return _allFiles.FirstOrDefault(imageFile => IsSameStorageItem(imageFile.File, file));
    }

    /// <summary>
    /// 判断两个存储项是否指向同一个底层对象。
    /// </summary>
    /// <param name="left">左侧存储项</param>
    /// <param name="right">右侧存储项</param>
    private static bool IsSameStorageItem(IStorageItem? left, IStorageItem? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        if (left.Path == right.Path)
        {
            return true;
        }

        var leftLocalPath = left.TryGetLocalPath();
        var rightLocalPath = right.TryGetLocalPath();
        if (!string.IsNullOrEmpty(leftLocalPath) && !string.IsNullOrEmpty(rightLocalPath))
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(leftLocalPath, rightLocalPath, comparison);
        }

        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// 加载所有文件的EXIF星级数据，用于准确的星级筛选
    /// </summary>
    private async Task LoadAllExifRatingsAsync()
    {
        if (_isExifLoadingInProgress) return;
        
        _isExifLoadingInProgress = true;
        
        try
        {
            // 使用信号量限制并发 I/O 数量，避免在 iOS 上同时打开过多文件描述符。
            using var semaphore = new SemaphoreSlim(8);
            var tasks = _allFiles.Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await file.LoadRatingOnlyAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"加载文件 {file.Name} 星级失败: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            
            // 在UI线程中更新星级显示
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var file in _allFiles)
                {
                    file.RaisePropertyChanged(nameof(file.Rating));
                }
            });
        }
        finally
        {
            _isExifLoadingInProgress = false;
        }
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
                System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant() ?? string.Empty
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
                representative.HiddenFileRatings.Clear();
                foreach (var other in g)
                {
                    if (other != representative)
                    {
                        representative.HiddenFiles.Add(other.File);
                        // 缓存隐藏伴侣文件的星级，用于"星级冲突"筛选
                        representative.HiddenFileRatings[other.File.Name] = other.Rating;
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
                // 筛选出同名 HEIF/JPG/RAW 中星级不一致的照片
                "Conflict" => f.HasRatingConflict,
                _ => true
            };
        }).ToList();

        // 先排序再一次性替换 FilteredFiles 引用，通知 UI 整体刷新（单次绑定更新，非逐条 Add）
        filtered = SortFileList(filtered);
        FilteredFiles = filtered;

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
        new() { DisplayName = "星级冲突", Key = "Conflict" },
    ];

    private string _selectedRatingFilter = "All";
    public string SelectedRatingFilter
    {
        get => _selectedRatingFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRatingFilter, value);
            // 筛选变化时重新加载可见区域的缩略图
            ApplyFilter();
            LoadVisibleThumbnailsAfterFilter();
        }
    }
    
    /// <summary>
    /// 筛选变化后加载可见区域的缩略图
    /// </summary>
    private void LoadVisibleThumbnailsAfterFilter()
    {
        // 清空当前缩略图队列，重新安排加载顺序
        ClearThumbnailQueue();
        
        // 为筛选后的前几个文件（通常是可见的）加载缩略图
        var visibleRange = _filteredFiles.Take(20).ToList(); // 取前20个作为初始可见范围
        
        foreach (var file in visibleRange)
        {
            if (file.Thumbnail == null && !file.IsThumbnailLoading)
            {
                QueueThumbnailLoad(file);
            }
        }
    }

    public int FilteredCount => _filteredFiles.Count;

    // 对外刷新（评分变化后调用）
    public void RefreshFilters() => ApplyFilter();
    
    // 排序筛选后的图片
    /// <summary>
    /// 按当前排序模式和排序方向对文件列表排序，返回排序后的新列表。
    /// </summary>
    private List<ImageFile> SortFileList(List<ImageFile> files)
    {
        IEnumerable<ImageFile> sorted = SortMode switch
        {
            SortMode.Name => SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.Name)
                : files.OrderByDescending(f => f.Name),
                
            SortMode.Date => SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue)
                : files.OrderByDescending(f => f.PhotoDate ?? f.ModifiedDate ?? DateTimeOffset.MinValue),
                
            SortMode.Size => SortOrder == SortOrder.Ascending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),
                
            _ => files.OrderBy(f => f.Name)
        };
        return sorted.ToList();
    }

    /// <summary>
    /// 重新排序当前已筛选的文件列表（仅在排序模式/方向变化时独立调用）
    /// </summary>
    private void ApplySort()
    {
        FilteredFiles = SortFileList(_filteredFiles.ToList());
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
    /// 启动缩略图加载后台任务（多个并发消费者）
    /// </summary>
    private void StartThumbnailLoadingTask()
    {
        _isThumbnailLoadingActive = true;
        // 启动多个并发消费者以充分利用 I/O 带宽
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