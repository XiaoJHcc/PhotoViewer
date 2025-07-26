using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class MainViewModel : ReactiveObject
{
    private readonly SettingsViewModel _settings;
    // private readonly AppState _appState = new();
        
    public ThumbnailViewModel ThumbnailViewModel { get; }
    public ControlViewModel ControlViewModel { get; }
    public ImageViewModel ImageViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    
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
                value.IsCurrent = true;
                PreloadNearbyFiles();
            }
        }
    }
    private readonly ObservableCollection<ImageFile> _allFiles = new();
    private readonly ObservableCollection<ImageFile> _filteredFiles = new();
    public ReadOnlyObservableCollection<ImageFile> AllFiles { get; }
    public ReadOnlyObservableCollection<ImageFile> FilteredFiles { get; }
        
    // public MainViewModel(SettingsViewModel settings) //Deepseek
    public MainViewModel()
    {
        // ThumbnailViewModel = new ThumbnailViewModel(_appState);
        // ControlViewModel = new ControlViewModel(_appState);
        // ImageViewModel = new ImageViewModel(_appState);
        // SettingsViewModel = new SettingsViewModel(_appState);
        
        // _settings = settings; //Deepseek
        _settings = SettingsViewModel;
            
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
    
    public async Task OpenFolder(IStorageFolder folder)
    {
        _currentFolder = folder;
        _allFiles.Clear();
        _filteredFiles.Clear();
        CurrentFile = null;
            
        // 加载文件夹内容
        var items = folder.GetItemsAsync();

        await foreach (var storageItem in items)
        {
            var item = (IStorageFile)storageItem;
            if (IsImageFile(item.Name))
            {
                _allFiles.Add(new ImageFile(item));
            }
        }
            
        ApplyFilter();
            
        // 如果有文件，选择第一个
        if (_filteredFiles.Count > 0)
        {
            CurrentFile = _filteredFiles[0];
        }
    }
    
    private bool IsImageFile(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return _settings.SelectedFormats.Contains(extension);
    }
        
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
    private void ApplySort()
        {
            var sorted = _settings.SortMode switch
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
            
            // 保持当前文件选中状态
            var current = CurrentFile;
            
            _filteredFiles.Clear();
            foreach (var file in sorted)
            {
                _filteredFiles.Add(file);
            }
            
            CurrentFile = current;
        }
        
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
        
        public ImageFile? GetPreviousFile()
        {
            if (!HasPreviousFile()) return null;
            
            var index = _filteredFiles.IndexOf(CurrentFile);
            return _filteredFiles[index - 1];
        }
        
        public ImageFile? GetNextFile()
        {
            if (!HasNextFile()) return null;
            
            var index = _filteredFiles.IndexOf(CurrentFile);
            return _filteredFiles[index + 1];
        }
}