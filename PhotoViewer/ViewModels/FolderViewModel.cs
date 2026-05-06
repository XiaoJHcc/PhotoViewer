using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels;

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

    private IStorageFolder? _currentFolder;
    private bool _isExifLoadingInProgress;

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

    ////////////////
    // 打开文件
    ////////////////

    #region OpenFile

    /// <summary>
    /// 打开文件选择器(桌面选文件,移动端选文件夹)。
    /// </summary>
    public async Task OpenFilePickerAsync()
    {
        var topLevel = GetCurrentTopLevel();
        if (topLevel?.StorageProvider == null) return;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
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
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                FileTypeFilter = GetFilePickerFileTypes(),
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                await OpenImageAsync(files[0]);
            }
        }
    }

    /// <summary>
    /// 打开外部传入的一组文件,优先使用首个可识别图片。
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
    /// 打开指定图片,并尽量进入其所在文件夹;若平台权限不支持,则回退为单图模式。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenImageAsync(IStorageFile file, bool scrollToCurrent = true)
    {
        if (!IsImageFile(file.Name))
        {
            return;
        }

        StorageAccessManager.Retain(file);
        Main.ImageVM.Fit = true;

        await OpenImageWithFolderFallbackAsync(file);

        if (scrollToCurrent)
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 打开指定文件夹,并定位到筛选后的第一张图片。
    /// </summary>
    /// <param name="folder">要打开的文件夹</param>
    /// <param name="scrollToCurrent">打开后是否滚动到当前图片</param>
    public async Task OpenFolderAsync(IStorageFolder folder, bool scrollToCurrent = true)
    {
        StorageAccessManager.Retain(folder);
        Main.ImageVM.Fit = true;

        await LoadFolderAsync(folder);

        if (scrollToCurrent)
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 兼容旧调用路径:打开新图片所在文件夹。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    public async Task LoadNewImageFolder(IStorageFile file)
    {
        await OpenImageAsync(file, scrollToCurrent: false);
    }

    /// <summary>
    /// 获取当前主界面的 TopLevel。
    /// </summary>
    private static TopLevel? GetCurrentTopLevel()
    {
        return App.GetCurrentTopLevel();
    }

    /// <summary>
    /// 打开文件选择器中的类型过滤器。
    /// </summary>
    private List<FilePickerFileType> GetFilePickerFileTypes()
    {
        var fileTypes = new List<FilePickerFileType>();

        var allSupportedType = new FilePickerFileType("所有图片")
        {
            AppleUniformTypeIdentifiers = new[] { "public.image" },
            MimeTypes = new[] { "image/*" },
            Patterns = Main.Settings.SelectedFormats.Select(format => $"*{format}").ToArray()
        };
        fileTypes.Add(allSupportedType);

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

    /// <summary>
    /// 根据扩展名获取对应的 Apple UTI。
    /// </summary>
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

    /// <summary>
    /// 根据扩展名获取对应的 MIME 类型。
    /// </summary>
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
    /// 加载文件夹
    ////////////////

    #region LoadFolder

    /// <summary>
    /// 优先尝试以"文件夹上下文"打开图片;若无法获取父目录,则回退为单图模式。
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
            StorageAccessManager.Retain(folder);
            FolderName = folder.Name;

            var existingFile = FindLoadedFile(file);
            if (existingFile == null)
            {
                existingFile = new ImageFile(file);
                _allFiles.Insert(0, existingFile);
                await existingFile.LoadRatingOnlyAsync();
                existingFile.UpdateCacheStatus();
                AllFilesChanged?.Invoke();
            }

            await SetCurrentFileAsync(existingFile);
            return;
        }

        await LoadFolderAsync(folder, file);
    }

    /// <summary>
    /// 以单图模式打开图片。当平台只授予单文件访问权限时,保证用户至少能看到当前图片。
    /// </summary>
    /// <param name="file">要打开的图片文件</param>
    private async Task OpenSingleImageAsync(IStorageFile file)
    {
        StorageAccessManager.Retain(file);

        _currentFolder = null;
        FolderName = file.Name;
        _allFiles.Clear();

        var imageFile = new ImageFile(file);
        _allFiles.Add(imageFile);

        await imageFile.LoadRatingOnlyAsync();
        AllFilesChanged?.Invoke();

        imageFile.UpdateCacheStatus();
        await SetCurrentFileAsync(imageFile);
    }

    /// <summary>
    /// 加载文件夹内容,并可选定位到指定图片。
    /// </summary>
    /// <param name="folder">要加载的文件夹</param>
    /// <param name="preferredFile">优先定位的图片;为空时打开第一张</param>
    private async Task LoadFolderAsync(IStorageFolder folder, IStorageFile? preferredFile = null)
    {
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
            await SetCurrentFileAsync(targetFile);
            Console.WriteLine($"[Folder] Phase1 (first image): {sw.ElapsedMilliseconds}ms");
        }

        // ── 阶段 2:枚举文件夹所有文件 ──
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

        foreach (var file in allNewFiles)
        {
            _allFiles.Add(file);
        }
        AllFilesChanged?.Invoke();

        if (targetFile == null)
        {
            var firstFile = _allFiles.FirstOrDefault();
            if (firstFile != null)
            {
                firstFile.UpdateCacheStatus();
                await SetCurrentFileAsync(firstFile);
            }
        }

        Console.WriteLine($"[Folder] Phase2 (UI updated): {sw.ElapsedMilliseconds}ms");

        // ── 阶段 3:后台加载基本属性和星级 ──
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
            await Dispatcher.UIThread.InvokeAsync(() => AllFilesChanged?.Invoke());
            Console.WriteLine($"[Folder] Phase3 (ratings loaded): {sw.ElapsedMilliseconds}ms");
        });

        Console.WriteLine($"[Folder] === END OpenFolder (sync portion): {sw.ElapsedMilliseconds}ms ===");
    }

    /// <summary>
    /// 切换当前图片,并触发当前图的 EXIF 加载与缩略图优先加载。
    /// </summary>
    /// <param name="imageFile">要设为当前的图片</param>
    private Task SetCurrentFileAsync(ImageFile? imageFile)
    {
        Main.CurrentFile = imageFile;
        if (imageFile == null)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            await imageFile.LoadExifDataAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                imageFile.RaisePropertyChanged(nameof(imageFile.PhotoDate));
                imageFile.RaisePropertyChanged(nameof(imageFile.RotationAngle));
                imageFile.RaisePropertyChanged(nameof(imageFile.NeedsHorizontalFlip));

                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.CurrentExifData));
                Main.ControlVM.RaisePropertyChanged(nameof(Main.ControlVM.StarOpacity));
            });
        });

        PriorityThumbnailRequested?.Invoke(imageFile);

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
    /// 加载所有文件的 EXIF 星级数据,用于准确的星级筛选。
    /// </summary>
    private async Task LoadAllExifRatingsAsync()
    {
        if (_isExifLoadingInProgress) return;

        _isExifLoadingInProgress = true;

        try
        {
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
