using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ReactiveUI;

namespace PhotoViewer.Core
{
    public enum SortMode { Name, Date, Size }
    public enum SortOrder { Ascending, Descending }
    
    public class FileFilterSettings : ReactiveObject
    {
        private ObservableCollection<string> _includedFormats = new ObservableCollection<string>
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
        };
        
        public ObservableCollection<string> IncludedFormats
        {
            get => _includedFormats;
            set => this.RaiseAndSetIfChanged(ref _includedFormats, value);
        }
    }
    
    public class CacheSettings : ReactiveObject
    {
        private int _preloadCount = 3;
        private int _maxCacheSizeMB = 5000;
        
        public int PreloadCount
        {
            get => _preloadCount;
            set => this.RaiseAndSetIfChanged(ref _preloadCount, value);
        }
        
        public int MaxCacheSizeMB
        {
            get => _maxCacheSizeMB;
            set => this.RaiseAndSetIfChanged(ref _maxCacheSizeMB, value);
        }
    }
    
    public class AppState : ReactiveObject
    {
        private IStorageFolder? _currentFolder;
        private readonly ObservableCollection<ImageFile> _allFiles = new();
        private readonly ObservableCollection<ImageFile> _filteredFiles = new();
        private ImageFile? _currentFile;
        private SortMode _sortMode = SortMode.Name;
        private SortOrder _sortOrder = SortOrder.Ascending;
        private readonly FileFilterSettings _filterSettings = new();
        private readonly CacheSettings _cacheSettings = new();
        
        public IStorageFolder? CurrentFolder
        {
            get => _currentFolder;
            private set => this.RaiseAndSetIfChanged(ref _currentFolder, value);
        }
        
        public ReadOnlyObservableCollection<ImageFile> AllFiles { get; }
        public ReadOnlyObservableCollection<ImageFile> FilteredFiles { get; }
        
        public ImageFile? CurrentFile
        {
            get => _currentFile;
            set
            {
                if (_currentFile != null) _currentFile.IsCurrent = false;
                this.RaiseAndSetIfChanged(ref _currentFile, value);
                if (value != null) value.IsCurrent = true;
            }
        }
        
        public SortMode SortMode
        {
            get => _sortMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _sortMode, value);
                ApplySort();
            }
        }
        
        public SortOrder SortOrder
        {
            get => _sortOrder;
            set
            {
                this.RaiseAndSetIfChanged(ref _sortOrder, value);
                ApplySort();
            }
        }
        
        public FileFilterSettings FilterSettings => _filterSettings;
        public CacheSettings CacheSettings => _cacheSettings;
        
        public AppState()
        {
            AllFiles = new ReadOnlyObservableCollection<ImageFile>(_allFiles);
            FilteredFiles = new ReadOnlyObservableCollection<ImageFile>(_filteredFiles);
            
            // 当过滤设置变化时自动重新过滤
            _filterSettings.WhenAnyValue(x => x.IncludedFormats)
                .Subscribe(_ => ApplyFilter());
        }
        
        public async Task LoadFolder(IStorageFolder folder)
        {
            CurrentFolder = folder;
            _allFiles.Clear();
            
            // var items = await folder.GetItemsAsync();
            // foreach (var item in items.OfType<IStorageFile>())
            // {
            //     if (IsImageFile(item.Name))
            //     {
            //         _allFiles.Add(new ImageFile(item));
            //     }
            // }
            // Deepseek BUG
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
        }
        
        private bool IsImageFile(string fileName)
        {
            var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
            return _filterSettings.IncludedFormats.Contains(extension);
        }
        
        private void ApplyFilter()
        {
            var filtered = _allFiles.Where(f => 
                _filterSettings.IncludedFormats.Contains(
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
            var sorted = _sortMode switch
            {
                SortMode.Name => _sortOrder == SortOrder.Ascending 
                    ? _filteredFiles.OrderBy(f => f.Name)
                    : _filteredFiles.OrderByDescending(f => f.Name),
                
                SortMode.Date => _sortOrder == SortOrder.Ascending 
                    ? _filteredFiles.OrderBy(f => f.ModifiedDate)
                    : _filteredFiles.OrderByDescending(f => f.ModifiedDate),
                
                SortMode.Size => _sortOrder == SortOrder.Ascending 
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
    }
    
    public class ImageFile : ReactiveObject
    {
        private Bitmap? _thumbnail;
        private bool _isCurrent;
        
        public IStorageFile File { get; }
        public string Name => File.Name;
        public DateTimeOffset? ModifiedDate => File.GetBasicPropertiesAsync().Result.DateModified;
        public ulong? FileSize => File.GetBasicPropertiesAsync().Result.Size;
        
        public Bitmap? Thumbnail
        {
            get => _thumbnail;
            private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
        }
        
        public bool IsCurrent
        {
            get => _isCurrent;
            set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
        }
        
        public ImageFile(IStorageFile file)
        {
            File = file;
        }
        
        public async Task LoadThumbnailAsync()
        {
            try
            {
                await using var stream = await File.OpenReadAsync();
                var bitmap = new Bitmap(stream);
                
                // 生成缩略图 (120px宽度)
                if (bitmap.PixelSize.Width > 120)
                {
                    var scale = 120.0 / bitmap.PixelSize.Width;
                    var newSize = new PixelSize(
                        (int)(bitmap.PixelSize.Width * scale),
                        (int)(bitmap.PixelSize.Height * scale)
                    );
                    bitmap = bitmap.CreateScaledBitmap(newSize);
                }
                
                // Thumbnail = new Bitmap(bitmap.PlatformImpl);
                // Deepseek BUG
                Thumbnail = bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载缩略图失败: {ex.Message}");
            }
        }
    }
}