using System;
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
        ControlViewModel = new ControlViewModel(this);
        ImageViewModel = new ImageViewModel(this);
        Settings = new SettingsViewModel();
            
        // 监听设置变化
        Settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
            
        ThumbnailViewModel.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
            .Subscribe(_ => ApplySort());
        
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

    private bool _isModalVisible = false;
        
    public bool IsModalVisible
    {
        get => _isModalVisible;
        set => this.RaiseAndSetIfChanged(ref _isModalVisible, value);
    }
        
    /// <summary>
    /// 打开设置弹窗
    /// </summary>
    public void OpenSettingModal()
    {
        IsModalVisible = true;
    }
    public void HideModal()
    {
        IsModalVisible = false;
    }
    
    // 优先加载图片 加载完成后调用其他逻辑
    public async void LoadNewImageFolder(IStorageFile file)
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
    
    private bool IsImageFile(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return Settings.SelectedFormats.Contains(extension);
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
        
        //DEBUG
        Console.WriteLine("_filteredFiles.Count = " + _filteredFiles.Count);
        Console.WriteLine("IndexOf(CurrentFile) = " + _filteredFiles.IndexOf(CurrentFile));
        
        ControlViewModel.Update();
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
}