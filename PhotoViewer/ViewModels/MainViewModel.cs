using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class MainViewModel : ReactiveObject
{
    private readonly SettingsViewModel _settings;
    // private readonly AppState _appState = new();
        
    public ThumbnailViewModel ThumbnailViewModel { get; }
    public ControlViewModel ControlViewModel { get; }
    public ImageViewModel ImageViewModel { get; }
    
    // 当前状态
    private IStorageFolder? _currentFolder;
    private ImageFile? _currentFile;
    public ImageFile? CurrentFile
    {
        get => _currentFile;
        set
        {
            
            Console.WriteLine("CurrentFile => " + value.Name);
            
            if (_currentFile != null) _currentFile.IsCurrent = false;
            this.RaiseAndSetIfChanged(ref _currentFile, value);
            if (value != null) 
            {
                value.IsCurrent = true;
                PreloadNearbyFiles();
            }
        }
    }
    private readonly ObservableCollection<ImageFile> _allFiles = new();
    private readonly ObservableCollection<ImageFile> _filteredFiles = new();
    public ReadOnlyObservableCollection<ImageFile> AllFiles { get; }
    public ReadOnlyObservableCollection<ImageFile> FilteredFiles { get; }
        
    public MainViewModel(SettingsViewModel settings)
    {
        _settings = settings;
            
        // 初始化集合
        AllFiles = new ReadOnlyObservableCollection<ImageFile>(_allFiles);
        FilteredFiles = new ReadOnlyObservableCollection<ImageFile>(_filteredFiles);
            
        // 创建子 ViewModel
        ThumbnailViewModel = new ThumbnailViewModel(this);
        ControlViewModel = new ControlViewModel(this);
        ImageViewModel = new ImageViewModel(this);
            
        // 监听设置变化
        _settings.WhenAnyValue(s => s.SelectedFormats)
            .Subscribe(_ => ApplyFilter());
            
        _settings.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
            .Subscribe(_ => ApplySort());
        
    }
    
    // 优先加载图片 加载完成后调用其他逻辑
    public async void OnNewImageLoaded(IStorageFile file)
    {
        var folder = await file.GetParentAsync();
        if (folder == null || folder == _currentFolder) return;

        CurrentFile = new ImageFile(file);
        
        await LoadFolder(folder);
    }
    
    // 读取当前图片文件夹内其他图片
    public async Task LoadFolder(IStorageFolder folder)
    {
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
        return _settings.SelectedFormats.Contains(extension);
    }
    
    // 筛选文件夹内图片
    private void ApplyFilter()
    {
        var filtered = _allFiles.Where(f => 
            _settings.SelectedFormats.Contains(
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
        var sortedFiles = _settings.SortMode switch
        {
            SortMode.Name => _settings.SortOrder == SortOrder.Ascending 
                ? _filteredFiles.OrderBy(f => f.Name)
                : _filteredFiles.OrderByDescending(f => f.Name),
                
            SortMode.Date => _settings.SortOrder == SortOrder.Ascending 
                ? _filteredFiles.OrderBy(f => f.ModifiedDate)
                : _filteredFiles.OrderByDescending(f => f.ModifiedDate),
                
            SortMode.Size => _settings.SortOrder == SortOrder.Ascending 
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
        Console.WriteLine("_filteredFiles = ");
        foreach (var file in _filteredFiles)
        {
            Console.WriteLine(file.Name);
        }
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
        
    // public ImageFile? GetPreviousFile()
    // {
    //     if (!HasPreviousFile()) return null;
    //     var index = _filteredFiles.IndexOf(CurrentFile);
    //     return _filteredFiles[index - 1];
    // }
    //     
    // public ImageFile? GetNextFile()
    // {
    //     if (!HasNextFile()) return null;
    //     var index = _filteredFiles.IndexOf(CurrentFile);
    //     return _filteredFiles[index + 1];
    // }
        
    private void PreloadNearbyFiles()
    {
        if (CurrentFile == null || _filteredFiles.Count == 0) return;
            
        var index = _filteredFiles.IndexOf(CurrentFile);
        var start = Math.Max(0, index - _settings.PreloadCount);
        var end = Math.Min(_filteredFiles.Count - 1, index + _settings.PreloadCount);
            
        for (int i = start; i <= end; i++)
        {
            if (i != index && _filteredFiles[i].Thumbnail == null)
            {
                _ = _filteredFiles[i].LoadThumbnailAsync();
            }
        }
    }
}