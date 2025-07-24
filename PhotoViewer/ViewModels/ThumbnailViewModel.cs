using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Core;

namespace PhotoViewer.ViewModels
{
    public class ThumbnailViewModel : ReactiveObject
    {
        internal readonly AppState _state;
        private readonly ObservableCollection<ImageFile> _displayedFiles = new();
        private readonly object _loadLock = new();
        
        public ReadOnlyObservableCollection<ImageFile> DisplayedFiles { get; }
        
        public ThumbnailViewModel(AppState state)
        {
            _state = state;
            DisplayedFiles = new ReadOnlyObservableCollection<ImageFile>(_displayedFiles);
            
            // 当过滤后的文件变化时更新显示
            // _state.FilteredFiles.CollectionChanged += (s, e) => UpdateDisplayedFiles();
            // Deepseek BUG
            
            // 当排序方式变化时更新显示
            _state.WhenAnyValue(s => s.SortMode, s => s.SortOrder)
                .Subscribe(_ => UpdateDisplayedFiles());
            
            // 初始更新
            UpdateDisplayedFiles();
        }

        private void UpdateDisplayedFiles()
        {
            lock (_loadLock)
            {
                _displayedFiles.Clear();
                foreach (var file in _state.FilteredFiles)
                {
                    _displayedFiles.Add(file);
                }
            }
        }
        
        public async Task PreloadThumbnailsAsync()
        {
            // 只加载前10个缩略图（可见区域）
            var tasks = _displayedFiles
                .Take(10)
                .Where(f => f.Thumbnail == null)
                .Select(f => f.LoadThumbnailAsync())
                .ToArray();
            
            await Task.WhenAll(tasks);
        }
    }
}