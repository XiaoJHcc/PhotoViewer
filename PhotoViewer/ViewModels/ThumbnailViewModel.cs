using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
        internal readonly MainViewModel _mainViewModel;
        private readonly ObservableCollection<ImageFile> _displayedFiles = new();
        private readonly object _loadLock = new();
        
        public ReadOnlyObservableCollection<ImageFile> DisplayedFiles { get; }
        
        public ThumbnailViewModel(MainViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
            DisplayedFiles = new ReadOnlyObservableCollection<ImageFile>(_displayedFiles);
            
            // 监听过滤文件集合变化
            // Observable.FromEventPattern<NotifyCollectionChangedEventArgs>(
            //     h => _mainViewModel.FilteredFiles.CollectionChanged += h,
            //     h => _mainViewModel.FilteredFiles.CollectionChanged -= h
            // ).Subscribe(_ => UpdateDisplayedFiles());
            // Deepseek BUG
            
            // 初始更新
            UpdateDisplayedFiles();
        }

        private void UpdateDisplayedFiles()
        {
            lock (_loadLock)
            {
                _displayedFiles.Clear();
                foreach (var file in _mainViewModel.FilteredFiles)
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
        
        public void SetCurrentFile(ImageFile file)
        {
            _mainViewModel.CurrentFile = file;
        }
    }
}