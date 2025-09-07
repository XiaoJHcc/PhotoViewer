using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class MainViewModel : ViewModelBase
{
    public ThumbnailViewModel ThumbnailViewModel { get; }
    public ControlViewModel ControlViewModel { get; }
    public ImageViewModel ImageViewModel { get; }
    public SettingsViewModel Settings { get; }
    
    // 实际使用的布局方向（考虑智能模式）
    private bool _isHorizontalLayout = false;
    public bool IsHorizontalLayout
    {
        get => _isHorizontalLayout;
        private set => this.RaiseAndSetIfChanged(ref _isHorizontalLayout, value);
    }

    // 屏幕方向状态
    private bool _isScreenLandscape = true;
    public bool IsScreenLandscape
    {
        get => _isScreenLandscape;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _isScreenLandscape, value))
            {
                UpdateLayoutFromSettings();
            }
        }
    }

    // 当前状态
    private IStorageFolder? _currentFolder;
    private ImageFile? _currentFile;
    public ImageFile? CurrentFile
    {
        get => _currentFile;
        set
        {
            if (_currentFile != null) _currentFile.IsCurrent = false;
            this.RaiseAndSetIfChanged(ref _currentFile, value);
            if (value != null) 
            {
                Console.WriteLine("CurrentFile => " + value.Name);
                value.IsCurrent = true;
                PreloadNearbyFiles();
            }
            else
            {
                Console.WriteLine("CurrentFile => null");
            }
        }
    }
    private readonly ObservableCollection<ImageFile> _allFiles = new();
    private readonly ObservableCollection<ImageFile> _filteredFiles = new();
    public ReadOnlyObservableCollection<ImageFile> AllFiles { get; }
    public ReadOnlyObservableCollection<ImageFile> FilteredFiles { get; }
        
    public MainViewModel()
    {
        // 初始化集合
        AllFiles = new ReadOnlyObservableCollection<ImageFile>(_allFiles);
        FilteredFiles = new ReadOnlyObservableCollection<ImageFile>(_filteredFiles);
            
        // 创建子 ViewModel
        ThumbnailViewModel = new ThumbnailViewModel(this);
        ImageViewModel = new ImageViewModel(this);
        Settings = new SettingsViewModel();
            
        // 监听设置变化
        Settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
            
        ThumbnailViewModel.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
            .Subscribe(_ => ApplySort());

        // 监听布局模式变化
        Settings.WhenAnyValue(s => s.LayoutMode)
            .Subscribe(_ => UpdateLayoutFromSettings());
        
        // 确保所有必要的属性在第55行之前已初始化
        ControlViewModel = new ControlViewModel(this);
    }
    
    /// <summary>
    /// 打开设置窗口
    /// </summary>
    public void OpenSettingWindow(Window parentWindow)
    {
        var settingsWindow = new SettingsWindow
        {
            DataContext = Settings
        };
        settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        settingsWindow.ShowDialog(parentWindow);
    }

    // 模态显示
    private bool _isModalVisible = false;
    public bool IsModalVisible
    {
        get => _isModalVisible;
        set => this.RaiseAndSetIfChanged(ref _isModalVisible, value);
    }
    // 模态显示时 遮罩不透明度
    private double _modalMaskOpacity = 0.5;
    public double ModalMaskOpacity
    {
        get => _modalMaskOpacity;
        set => this.RaiseAndSetIfChanged(ref _modalMaskOpacity, value);
    }
    // 模态显示时 模态弹出
    private double _modalMarginTop = 2000;
    public double ModalMarginTop
    {
        get => _modalMarginTop;
        set => this.RaiseAndSetIfChanged(ref _modalMarginTop, value);
    }
    
        
    /// <summary>
    /// 打开设置弹窗
    /// </summary>
    public void OpenSettingModal()
    {
        ShowModal();
    }

    public void ShowModal()
    {
        IsModalVisible = true;
        
        ModalMaskOpacity = 0.5;
        ModalMarginTop = 80;
    }
    public async void HideModal()
    {
        ModalMaskOpacity = 0;
        ModalMarginTop = 2000;
        
        await Task.Delay(400);
        IsModalVisible = false;
    }
    
    // 图片加载完成后 调用其他逻辑
    public async Task LoadNewImageFolder(IStorageFile file)
    {
        var folder = await file.GetParentAsync();
        if (folder == null || folder == _currentFolder) return;

        CurrentFile = new ImageFile(file);
        
        // 加载图片所在文件夹
        _currentFolder = folder;
        _allFiles.Clear();
        _filteredFiles.Clear();
        _allFiles.Add(CurrentFile);
            
        // 加载文件夹内容
        var items = folder.GetItemsAsync();

        await foreach (var storageItem in items)
        {
            var item = (IStorageFile)storageItem;
            if (IsImageFile(item.Name) && item.Name != CurrentFile.Name)
            {
                _allFiles.Add(new ImageFile(item));
            }
        }
            
        ApplyFilter();
    }
    
    public bool IsImageFile(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension != null && Settings.SelectedFormats.Contains(extension);
    }
    
    // Android 从文件夹加载第一个文件
    public async Task OpenAndroidFolder(IStorageFolder folder)
    {
        _currentFolder = folder;
        _allFiles.Clear();
        _filteredFiles.Clear();
    
        Console.WriteLine("OpenFolder: " + folder.Path);
        
        // 加载文件夹内容
        var items = folder.GetItemsAsync();
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
        
        CurrentFile = _filteredFiles.First();
    }
    
    // 筛选文件夹内图片
    private void ApplyFilter()
    {
        var filtered = _allFiles.Where(f => 
            Settings.SelectedFormats.Contains(
                System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant()
            )
        ).ToList();
        
        _filteredFiles.Clear();
        foreach (var file in filtered)
        {
            _filteredFiles.Add(file);
        }
        
        ApplySort();
    }
    
    // 排序筛选后的图片
    private void ApplySort()
    {
        var sortedFiles = ThumbnailViewModel.SortMode switch
        {
            SortMode.Name => ThumbnailViewModel.SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.Name)
                : _filteredFiles.OrderByDescending(f => f.Name),
                
            SortMode.Date => ThumbnailViewModel.SortOrder == SortOrder.Ascending
                ? _filteredFiles.OrderBy(f => f.ModifiedDate)
                : _filteredFiles.OrderByDescending(f => f.ModifiedDate),
                
            SortMode.Size => ThumbnailViewModel.SortOrder == SortOrder.Ascending
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
        if (CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(CurrentFile) > 0;
    }
        
    public bool HasNextFile()
    {
        if (CurrentFile == null || _filteredFiles.Count == 0) return false;
        return _filteredFiles.IndexOf(CurrentFile) < _filteredFiles.Count - 1;
    }
        
    private void PreloadNearbyFiles()
    {
        if (CurrentFile == null || _filteredFiles.Count == 0) return;
            
        var index = _filteredFiles.IndexOf(CurrentFile);
        var start = Math.Max(0, index - Settings.PreloadCount);
        var end = Math.Min(_filteredFiles.Count - 1, index + Settings.PreloadCount);
            
        for (int i = start; i <= end; i++)
        {
            if (i != index && _filteredFiles[i].Thumbnail == null)
            {
                _ = _filteredFiles[i].LoadThumbnailAsync();
            }
        }
    }
    
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
                await OpenAndroidFolder(folders[0]);
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
                await LoadNewImageFromFile(file);
            }
        }
    }

    /// <summary>
    /// 获取文件选择器的文件类型过滤器
    /// </summary>
    private List<FilePickerFileType> GetFilePickerFileTypes()
    {
        var fileTypes = new List<FilePickerFileType>();
        
        // 添加"所有支持的图片格式"选项
        var allSupportedType = new FilePickerFileType("所有图片")
        {
            AppleUniformTypeIdentifiers = new[] { "public.image" },
            MimeTypes = new[] { "image/*" },
            Patterns = Settings.SelectedFormats.Select(format => $"*{format}").ToArray()
        };
        fileTypes.Add(allSupportedType);
        
        // 为每个已勾选的格式创建单独的文件类型
        foreach (var formatItem in Settings.FileFormats.Where(f => f.IsEnabled))
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

    /// <summary>
    /// 从文件加载新图片（适配显示）
    /// </summary>
    private async Task LoadNewImageFromFile(IStorageFile file)
    {
        // 新打开文件时始终适配显示
        ImageViewModel.Fit = true;
        
        // 加载图片所在文件夹
        await LoadNewImageFolder(file);
        
        // 加载文件夹后滚动至当前图片
        ThumbnailViewModel.ScrollToCurrent();
    }
    
    /// <summary>
    /// 根据设置和屏幕方向更新布局
    /// </summary>
    private void UpdateLayoutFromSettings()
    {
        bool newLayout = Settings.LayoutMode switch
        {
            LayoutMode.Horizontal => true,
            LayoutMode.Vertical => false,
            LayoutMode.Auto => IsScreenLandscape,
            _ => false
        };

        if (IsHorizontalLayout != newLayout)
        {
            IsHorizontalLayout = newLayout;
            
            // 通知相关视图模型布局已变化
            ThumbnailViewModel?.RaisePropertyChanged(nameof(ThumbnailViewModel.IsVerticalLayout));
            ControlViewModel?.RaisePropertyChanged(nameof(ControlViewModel.IsVerticalLayout));
        }
    }

    /// <summary>
    /// 更新屏幕方向（由视图调用）
    /// </summary>
    public void UpdateScreenOrientation(double width, double height)
    {
        bool newIsLandscape = width > height;
        
        if (IsScreenLandscape != newIsLandscape)
        {
            IsScreenLandscape = newIsLandscape;
            
            // 如果是智能模式，屏幕方向变化会触发布局更新
            if (Settings.LayoutMode == LayoutMode.Auto)
            {
                UpdateLayoutFromSettings();
            }
        }
    }
}
