using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ComboBoxItem = PhotoViewer.Core.ComboBoxItem;

namespace PhotoViewer.ViewModels;

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
    
    // 滚动到当前图片的事件
    public event Action? ScrollToCurrentRequested;
    
    public List<ComboBoxItem> SortModes { get; } =
    [
        new() { DisplayName = "名称", Value = SortMode.Name },
        new() { DisplayName = "修改日期", Value = SortMode.Date },
        new() { DisplayName = "文件大小", Value = SortMode.Size }
    ];

    public List<ComboBoxItem> SortOrders { get; } =
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
        
    public FolderViewModel(MainViewModel main)
    {
        Main = main;
        
        // 初始化集合
        AllFiles = new ReadOnlyObservableCollection<ImageFile>(_allFiles);
        FilteredFiles = new ReadOnlyObservableCollection<ImageFile>(_filteredFiles);
        
        // 监听布局变化
        Main.WhenAnyValue(x => x.IsHorizontalLayout)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVerticalLayout)));
            
        // 监听设置变化
        Main.Settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
            
        this.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
            .Subscribe(_ => ApplySort());
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
            
                    Console.WriteLine(item.Name);
            
                    if (IsImageFile(item.Name))
                    {
                        _allFiles.Add(new ImageFile(item));
                    }
                }
            
                ApplyFilter();
        
                Main.CurrentFile = _filteredFiles.First();
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
    /// 缩略图和筛选
    ////////////////

    #region LoadFolder
    
    // 图片加载完成后 调用其他逻辑
    public async Task LoadNewImageFolder(IStorageFile file)
    {
        var folder = await file.GetParentAsync();
        if (folder == null || folder == _currentFolder) return;

        Main.CurrentFile = new ImageFile(file);
        
        // 优先加载当前图片的 EXIF 数据
        _ = Task.Run(async () => await Main.CurrentFile.LoadExifDataAsync());
        
        // 加载图片所在文件夹
        _currentFolder = folder;
        _allFiles.Clear();
        _filteredFiles.Clear();
        _allFiles.Add(Main.CurrentFile);
            
        // 加载文件夹内容
        var items = folder.GetItemsAsync();

        await foreach (var storageItem in items)
        {
            var item = (IStorageFile)storageItem;
            if (IsImageFile(item.Name) && item.Name != Main.CurrentFile.Name)
            {
                _allFiles.Add(new ImageFile(item));
            }
        }
            
        ApplyFilter();
        
        // 异步加载其他图片的 EXIF 数据（排除当前图片）
        _ = Task.Run(async () =>
        {
            var otherFiles = _filteredFiles.Where(f => f != Main.CurrentFile);
            await ExifLoader.LoadFolderExifDataAsync(otherFiles);
        });
    }
    
    public bool IsImageFile(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension != null && Main.Settings.SelectedFormats.Contains(extension);
    }
    
    // 筛选文件夹内图片
    private void ApplyFilter()
    {
        var filtered = _allFiles.Where(f => 
            Main.Settings.SelectedFormats.Contains(
                System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant()
            )
        ).ToList();
        
        _filteredFiles.Clear();
        foreach (var file in filtered)
        {
            _filteredFiles.Add(file);
        }
        
        ApplySort();
        
        // 筛选后重新加载 EXIF 数据（排除当前图片，因为已经优先加载了）
        _ = Task.Run(async () =>
        {
            var otherFiles = _filteredFiles.Where(f => f != Main.CurrentFile);
            await ExifLoader.LoadFolderExifDataAsync(otherFiles);
        });
    }
    
    // 排序筛选后的图片
    private void ApplySort()
    {
        var sortedFiles = SortMode switch
        {
            SortMode.Name => SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.Name)
                : _filteredFiles.OrderByDescending(f => f.Name),
                
            SortMode.Date => SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.ModifiedDate)
                : _filteredFiles.OrderByDescending(f => f.ModifiedDate),
                
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
    
    public void PreloadNearbyFiles()
    {
        if (Main.CurrentFile == null || _filteredFiles.Count == 0) return;
            
        var index = _filteredFiles.IndexOf(Main.CurrentFile);
        var start = Math.Max(0, index - Main.Settings.PreloadCount);
        var end = Math.Min(_filteredFiles.Count - 1, index + Main.Settings.PreloadCount);
            
        for (int i = start; i <= end; i++)
        {
            if (i != index && _filteredFiles[i].Thumbnail == null)
            {
                _ = _filteredFiles[i].LoadThumbnailAsync();
            }
        }
    }
    
    #endregion
}