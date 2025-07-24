using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoViewer.Service;

namespace PhotoViewer.ViewModels
{
    public class ThumbnailViewModel : ViewModelBase
    {
        private IStorageFile? _currentFile;
        private bool _isLoading;
        
        public ObservableCollection<ThumbnailItem> ThumbnailItems { get; } 
            = new ObservableCollection<ThumbnailItem>();

        public IStorageFile? CurrentFile;
        
        public async Task SetFiles(IEnumerable<IStorageFile> files, IStorageFile? currentFile = null)
        {
            CurrentFile = currentFile;
            ThumbnailItems.Clear();
            
            // 先添加占位符
            foreach (var file in files)
            {
                ThumbnailItems.Add(new ThumbnailItem 
                { 
                    File = file,
                    IsCurrent = file.Path.AbsolutePath == currentFile?.Path.AbsolutePath,
                    Image = null // 初始为null，显示文件名
                });
            }
            
            // 初始加载可见区域的缩略图
            await LoadVisibleThumbnails();
        }
        
        public async Task LoadVisibleThumbnails()
        {
            if (_isLoading) return;
            _isLoading = true;
            
            try
            {
                // 加载前10个缩略图（可见区域）
                for (int i = 0; i < Math.Min(10, ThumbnailItems.Count); i++)
                {
                    var item = ThumbnailItems[i];
                    if (item.Image == null)
                    {
                        item.Image = await BitmapCacheService.GetBitmapAsync(item.File, 120);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载可见缩略图失败: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }
        
        public void RefreshThumbnails(int sortBy, int order)
        {
            if (ThumbnailItems.Count == 0) return;
            
            var sorted = sortBy switch
            {
                0 => ThumbnailItems.OrderBy(t => t.File.Name),
                _ => ThumbnailItems.OrderBy(t => t.File.GetBasicPropertiesAsync().Result.DateModified)
            };
            
            if (order == 1) sorted = sorted.Reverse() as IOrderedEnumerable<ThumbnailItem>;
            
            var sortedList = sorted.ToList();
            ThumbnailItems.Clear();
            
            foreach (var item in sortedList)
            {
                ThumbnailItems.Add(item);
            }
        }
        
        public void SetCurrentFile(IStorageFile file)
        {
            CurrentFile = file;
            
            // 高亮当前项
            foreach (var item in ThumbnailItems)
            {
                item.IsCurrent = item.File.Path.AbsolutePath == file.Path.AbsolutePath;
            }
        }
    }
    
    // ThumbnailItem 类定义
    public class ThumbnailItem
    {
        public IStorageFile File { get; set; }
        public Bitmap? Image { get; set; }
        public bool IsCurrent { get; set; }
    }
}