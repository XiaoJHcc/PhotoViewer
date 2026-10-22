using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.ViewModels.Main;

/// <summary>排序下拉选项(显示名 + 枚举值)</summary>
public class SortOption
{
    public string DisplayName { get; set; } = null!;
    public object Value { get; set; } = null!;
}

/// <summary>排序方式</summary>
public enum SortMode { Name, Date, Rating, Size }
/// <summary>排序方向</summary>
public enum SortOrder { Ascending, Descending }

/// <summary>
/// 文件源视图模型:仅负责打开/加载文件夹与单文件,维护 <see cref="AllFiles"/> 原始集合。
/// 筛选、排序、缩略图队列、位图预取等由 <see cref="File.ThumbnailListViewModel"/> 订阅本 VM 的事件后完成。
/// </summary>
public class FolderViewModel : ReactiveObject
{
    public MainViewModel Main { get; }

    private readonly List<ImageFile> _allFiles = new();
    /// <summary>原始文件集合(未筛选、未排序),写入星级后同步刷新</summary>
    public IReadOnlyList<ImageFile> AllFiles => _allFiles;

    private IFolderRef? _currentFolder;
    /// <summary>打开代际:每次重新加载列表/单图时自增,旧代际的枚举与补齐结果据此丢弃。</summary>
    private int _openGeneration;

    private string _folderName = "";
    /// <summary>当前文件夹名称(若为单图模式则为文件名)</summary>
    public string FolderName
    {
        get => _folderName;
        set => this.RaiseAndSetIfChanged(ref _folderName, value);
    }

    /// <summary>原始文件集合发生变化时触发(新增/清空/重载),由 ThumbnailListViewModel 订阅以重算筛选</summary>
    public event Action? AllFilesChanged;

    /// <summary>请求 View 滚动到当前文件(事件转发给 ThumbnailListView)</summary>
    public event Action? ScrollToCurrentRequested;

    /// <summary>请求优先加载某张图的缩略图(由 ThumbnailListViewModel 订阅)</summary>
    public event Action<ImageFile>? PriorityThumbnailRequested;

    /// <summary>
    /// 构造文件源视图模型。
    /// </summary>
    /// <param name="main">主视图模型</param>
    public FolderViewModel(MainViewModel main)
    {
        Main = main;
    }

    /// <summary>
    /// 触发滚动到当前文件事件。
    /// </summary>
    public void ScrollToCurrent() => ScrollToCurrentRequested?.Invoke();

    /// <summary>
    /// 开启新的打开代际。
    /// </summary>
    /// <returns>本次打开代际编号。</returns>
    private int BeginOpenGeneration() => Interlocked.Increment(ref _openGeneration);

    /// <summary>
    /// 判断给定代际是否仍是当前代际;旧代际的后台结果不得再写入列表或当前照片。
    /// </summary>
    /// <param name="generation">要校验的代际编号</param>
    /// <returns>代际仍有效时为 true</returns>
    private bool IsCurrentOpenGeneration(int generation) => generation == Volatile.Read(ref _openGeneration);

    ////////////////
    // 打开文件
    ////////////////

    #region OpenFile

    /// <summary>
    /// 打开文件选择器(桌面选文件,移动端选文件夹)。
    /// </summary>
    public async Task OpenFilePickerAsync()
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            var folder = await FilePicker.PickFolderAsync("选择图片文件夹");
            if (folder != null)
            {
                await OpenFolderAsync(folder);
            }
        }
        else
        {
            var file = await FilePicker.PickFileAsync("选择图片", GetFilePickFilters());
            if (file != null)
            {
                await OpenImageAsync(file);
            }
        }
    }

    /// <summary>
    /// 打开外部传入的一组文件,优先使用首个可识别图片。
    /// </summary>
    /// <param name="files">外部传入的文件集合</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenExternalFilesAsync(IEnumerable<IFileRef> files, bool scrollToCurrent = true)
    {
        var targetFile = files.FirstOrDefault(file => IsImageFile(file.Name));
        if (targetFile == null)
        {
            return;
        }
        await OpenImageAsync(targetFile, scrollToCurrent);
    }

    /// <summary>
    /// 打开指定图片,并尽量进入其所在文件夹;若平台权限不支持,则回退为单图模式。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenImageAsync(IFileRef file, bool scrollToCurrent = true)
    {
        if (!IsImageFile(file.Name))
        {
            return;
        }

        // OPEN-08：这次请求捕获当前代际作为护栏;若期间有新的列表加载开始,父目录结果直接作废。
        // 同目录内选图不开启新代际,避免误伤该目录仍在进行的补齐任务。
        var generation = Volatile.Read(ref _openGeneration);

        StorageAccessManager.Retain(file);
        // OPEN-05：显式打开开始新的观片，主图回到适应态。
        Main.RequestFitToView();

        generation = await OpenImageWithFolderFallbackAsync(file, generation);

        if (scrollToCurrent && IsCurrentOpenGeneration(generation))
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 打开指定文件夹,并定位到筛选后的第一张图片。
    /// </summary>
    /// <param name="folder">要打开的文件夹</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenFolderAsync(IFolderRef folder, bool scrollToCurrent = true)
    {
        StorageAccessManager.Retain(folder);
        // OPEN-05：显式打开开始新的观片，主图回到适应态。
        Main.RequestFitToView();

        // OPEN-08：同目录重开视为刷新,以当前照片为首选目标,且保留缩略图缓存。
        var isSameFolder = IsSameStorageItem(folder, _currentFolder);
        var preferredFile = isSameFolder && Main.CurrentFile != null && _allFiles.Contains(Main.CurrentFile)
            ? Main.CurrentFile.File
            : null;
        if (!isSameFolder)
        {
            // 仅切换文件夹时整批释放旧缩略图(统一缓存 Thumb 档清空,不依赖淘汰)。
            BitmapLoader.ClearThumbnailClass();
        }

        var generation = await LoadFolderAsync(folder, preferredFile);

        if (scrollToCurrent && IsCurrentOpenGeneration(generation))
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 兼容旧调用路径:打开新图片所在文件夹。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    public async Task LoadNewImageFolder(IFileRef file)
    {
        await OpenImageAsync(file, scrollToCurrent: false);
    }

    /// <summary>
    /// 构建文件选择器的类型过滤器(显示名 + 扩展名模式;UTI/MIME 推导在选择器实现内)。
    /// </summary>
    private List<FilePickFilter> GetFilePickFilters()
    {
        var filters = new List<FilePickFilter>
        {
            new("所有图片", Main.Settings.SelectedFormats.Select(format => $"*{format}").ToArray())
            {
                IsGenericImage = true
            }
        };

        foreach (var formatItem in Main.Settings.FileFormats.Where(f => f.IsEnabled))
        {
            filters.Add(new FilePickFilter($"{formatItem.DisplayName} 文件",
                formatItem.Extensions.Select(ext => $"*{ext}").ToArray()));
        }

        return filters;
    }

    #endregion


    ////////////////
    /// 加载文件夹
    ////////////////

    #region LoadFolder

    /// <summary>
    /// 优先尝试以"文件夹上下文"打开图片;若无法获取父目录,则回退为单图模式。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="generation">发起本次打开时捕获的代际</param>
    /// <returns>本次打开实际生效的代际</returns>
    private async Task<int> OpenImageWithFolderFallbackAsync(IFileRef file, int generation)
    {
        var folder = await file.GetParentAsync();

        if (!IsCurrentOpenGeneration(generation))
        {
            return generation;
        }

        if (folder == null)
        {
            return await OpenSingleImageAsync(file);
        }

        if (IsSameStorageItem(folder, _currentFolder) && _allFiles.Count > 0)
        {
            StorageAccessManager.Retain(folder);
            FolderName = folder.Name;

            var existingFile = FindLoadedFile(file);
            if (existingFile == null)
            {
                existingFile = new ImageFile(file);
                _allFiles.Insert(0, existingFile);
                await existingFile.LoadRatingOnlyAsync();
                if (!IsCurrentOpenGeneration(generation))
                {
                    return generation;
                }

                existingFile.UpdateCacheStatus();
                AllFilesChanged?.Invoke();
            }

            await SetCurrentFileAsync(existingFile, generation);
            return generation;
        }

        return await LoadFolderAsync(folder, file);
    }

    /// <summary>
    /// 以单图模式打开图片。当平台只授予单文件访问权限时,保证用户至少能看到当前图片。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <returns>本次打开代际</returns>
    private async Task<int> OpenSingleImageAsync(IFileRef file)
    {
        var generation = BeginOpenGeneration();

        StorageAccessManager.Retain(file);

        _currentFolder = null;
        FolderName = file.Name;
        _allFiles.Clear();

        var imageFile = new ImageFile(file);
        _allFiles.Add(imageFile);

        await imageFile.LoadRatingOnlyAsync();
        if (!IsCurrentOpenGeneration(generation))
        {
            return generation;
        }

        AllFilesChanged?.Invoke();

        imageFile.UpdateCacheStatus();
        await SetCurrentFileAsync(imageFile, generation);
        return generation;
    }

    /// <summary>
    /// 加载文件夹内容,并可选定位到指定图片。
    /// </summary>
    /// <param name="folder">要加载的文件夹</param>
    /// <param name="preferredFile">优先定位的图片;为空时打开第一张</param>
    /// <returns>本次打开代际</returns>
    private async Task<int> LoadFolderAsync(IFolderRef folder, IFileRef? preferredFile = null)
    {
        var generation = BeginOpenGeneration();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("[Folder] === BEGIN OpenFolder: " + folder.Path);

        StorageAccessManager.Retain(folder);
        StorageAccessManager.Retain(preferredFile);

        _currentFolder = folder;
        FolderName = folder.Name;
        _allFiles.Clear();

        // ── 阶段 1:优先显示目标图片 ──
        ImageFile? targetFile = null;
        if (preferredFile != null && IsImageFile(preferredFile.Name))
        {
            targetFile = new ImageFile(preferredFile);
            _allFiles.Add(targetFile);
            AllFilesChanged?.Invoke();
            targetFile.UpdateCacheStatus();
            await SetCurrentFileAsync(targetFile, generation);
            if (!IsCurrentOpenGeneration(generation))
            {
                return generation;
            }

            Console.WriteLine($"[Folder] Phase1 (first image): {sw.ElapsedMilliseconds}ms");
        }

        // ── 阶段 2:枚举文件夹所有文件 ──
        var allNewFiles = new List<ImageFile>();
        try
        {
            await foreach (var storageItem in folder.GetItemsAsync())
            {
                if (!IsCurrentOpenGeneration(generation))
                {
                    return generation;
                }

                if (storageItem is not IFileRef item) continue;
                if (!IsImageFile(item.Name)) continue;
                if (preferredFile != null && IsSameStorageItem(item, preferredFile)) continue;
                allNewFiles.Add(new ImageFile(item));
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrentOpenGeneration(generation))
            {
                return generation;
            }

            Console.Error.WriteLine($"[Folder] 枚举文件夹失败: {folder.Path} — {ex.Message}");
            // STATE-02：打开失败必须让用户看见对象与原因，不能只写日志。
            Main.ReportOpenFailure(StatusMessages.FolderOpenFailed(folder.Name, ex.Message));
            if (preferredFile != null)
            {
                // OPEN-03：目录枚举被拒/失败时回退为单图模式,至少让用户看到目标图片。
                return await OpenSingleImageAsync(preferredFile);
            }

            // OPEN-02/OPEN-08：没有可回退的单张文件时保持空态,不残留半张列表。
            _allFiles.Clear();
            Main.CurrentFile = null;
            AllFilesChanged?.Invoke();
            return generation;
        }

        if (!IsCurrentOpenGeneration(generation))
        {
            return generation;
        }

        Console.WriteLine($"[Folder] Phase2 (enumerate {allNewFiles.Count} files): {sw.ElapsedMilliseconds}ms");

        foreach (var file in allNewFiles)
        {
            _allFiles.Add(file);
        }
        AllFilesChanged?.Invoke();

        if (targetFile == null)
        {
            if (!IsCurrentOpenGeneration(generation))
            {
                return generation;
            }

            // AllFilesChanged 已同步重建筛选列表:首张取筛选后列表的首项,同名合并时即为代表文件
            // (如 HIF+ARW 同名时取 HIF),不能取枚举顺序首项——那可能是被合并隐藏的伴侣文件。
            // OPEN-04：筛选结果为空(如星级筛选排除全部)时,回退到枚举首项所属合并组的代表文件。
            var firstFile = Main.FileVM.ThumbnailList.FilteredFiles.FirstOrDefault()
                ?? FindFirstGroupRepresentative();
            if (firstFile != null)
            {
                firstFile.UpdateCacheStatus();
                await SetCurrentFileAsync(firstFile, generation);
            }
        }

        if (!IsCurrentOpenGeneration(generation))
        {
            return generation;
        }

        // 列表已完整(第二次 AllFilesChanged 已同步重建 FilteredFiles)且当前图已定,
        // 显式补触发一次邻图预取 — 阶段1 触发时列表只有 1 项会空跑,CurrentFile 不再变则不会重触发
        Main.FileVM.ThumbnailList.TriggerPrefetchAroundCurrent();

        Console.WriteLine($"[Folder] Phase2 (UI updated): {sw.ElapsedMilliseconds}ms");

        // ── 阶段 3:后台加载基本属性和星级(只作用于本代际的快照) ──
        var filesSnapshot = _allFiles.ToList();
        foreach (var imageFile in filesSnapshot) imageFile.UpdateCacheStatus();

        _ = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(8);
            var propTasks = filesSnapshot.Select(async f =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (IsCurrentOpenGeneration(generation))
                    {
                        await f.LoadBasicPropertiesAsync();
                    }
                }
                finally { semaphore.Release(); }
            }).ToList();
            await Task.WhenAll(propTasks);

            if (!IsCurrentOpenGeneration(generation))
            {
                return;
            }

            Console.WriteLine($"[Folder] Phase3 (basic props loaded): {sw.ElapsedMilliseconds}ms");
        });

        _ = Task.Run(async () =>
        {
            await LoadAllExifRatingsAsync(filesSnapshot, generation);
            if (!IsCurrentOpenGeneration(generation))
            {
                return;
            }

            await UiDispatcher.InvokeAsync(() =>
            {
                if (IsCurrentOpenGeneration(generation))
                {
                    AllFilesChanged?.Invoke();
                }
            });
            Console.WriteLine($"[Folder] Phase3 (ratings loaded): {sw.ElapsedMilliseconds}ms");
        });

        Console.WriteLine($"[Folder] === END OpenFolder (sync portion): {sw.ElapsedMilliseconds}ms ===");
        return generation;
    }

    /// <summary>
    /// 切换当前图片,并触发当前图的 EXIF 加载与缩略图优先加载。
    /// </summary>
    /// <param name="imageFile">要设为当前的图片</param>
    /// <param name="generation">当前打开代际;旧代际的结果不再写入当前照片</param>
    private Task SetCurrentFileAsync(ImageFile? imageFile, int generation)
    {
        if (!IsCurrentOpenGeneration(generation))
        {
            return Task.CompletedTask;
        }

        Main.CurrentFile = imageFile;
        if (imageFile == null)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            await imageFile.LoadExifDataAsync();

            if (!IsCurrentOpenGeneration(generation) || !ReferenceEquals(Main.CurrentFile, imageFile))
            {
                return;
            }

            await UiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentOpenGeneration(generation) || !ReferenceEquals(Main.CurrentFile, imageFile))
                {
                    return;
                }

                imageFile.RaisePropertyChanged(nameof(imageFile.PhotoDate));

                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
            });
        });

        if (IsCurrentOpenGeneration(generation))
        {
            PriorityThumbnailRequested?.Invoke(imageFile);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 在已加载集合中查找同一个存储文件。
    /// </summary>
    /// <param name="file">要查找的存储文件</param>
    private ImageFile? FindLoadedFile(IFileRef file)
    {
        return _allFiles.FirstOrDefault(imageFile => IsSameStorageItem(imageFile.File, file));
    }

    /// <summary>
    /// 取枚举首项所属同名合并组的代表文件(按设置中的格式优先级)。
    /// </summary>
    /// <returns>代表文件;列表为空时为 null</returns>
    private ImageFile? FindFirstGroupRepresentative()
    {
        var first = _allFiles.FirstOrDefault();
        if (first == null)
        {
            return null;
        }

        if (!Main.Settings.SameNameAsOnePhoto)
        {
            return first;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(first.Name);
        var order = Main.Settings.SelectedFormats;
        return _allFiles
            .Where(file => string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(file.Name),
                baseName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(file =>
            {
                var extension = System.IO.Path.GetExtension(file.Name)?.ToLowerInvariant() ?? string.Empty;
                var index = order.IndexOf(extension);
                return index < 0 ? int.MaxValue : index;
            })
            .FirstOrDefault() ?? first;
    }

    /// <summary>
    /// 判断两个存储项是否指向同一个底层对象。
    /// </summary>
    private static bool IsSameStorageItem(IStorageItemRef? left, IStorageItemRef? right)
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
    /// 加载给定文件快照的 EXIF 星级数据,用于准确的星级筛选。
    /// </summary>
    /// <param name="files">本轮代际的文件快照</param>
    /// <param name="generation">打开代际;旧代际的补齐结果不再写入</param>
    private async Task LoadAllExifRatingsAsync(IReadOnlyList<ImageFile> files, int generation)
    {
        using var semaphore = new SemaphoreSlim(8);
        var tasks = files.Select(async file =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!IsCurrentOpenGeneration(generation))
                {
                    return;
                }

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

        if (!IsCurrentOpenGeneration(generation))
        {
            return;
        }

        await UiDispatcher.InvokeAsync(() =>
        {
            if (!IsCurrentOpenGeneration(generation))
            {
                return;
            }

            foreach (var file in files)
            {
                file.RaisePropertyChanged(nameof(file.Rating));
            }
        });
    }

    /// <summary>
    /// 根据设置中已启用的格式判断给定文件名是否为图片。
    /// </summary>
    /// <param name="fileName">文件名(含扩展名)</param>
    public bool IsImageFile(string fileName)
    {
        if (IsHiddenFile(fileName)) return false;
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension != null && Main.Settings.SelectedFormats.Contains(extension);
    }

    /// <summary>
    /// 是否为隐藏文件(以点号开头)。
    /// </summary>
    public static bool IsHiddenFile(string fileName)
    {
        return fileName.StartsWith('.');
    }

    #endregion
}
