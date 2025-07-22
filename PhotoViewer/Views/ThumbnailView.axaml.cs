using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using PhotoViewer.Service;

namespace PhotoViewer.Views;

    public partial class ThumbnailView : UserControl
    {
        public event EventHandler<IStorageFile>? ThumbnailSelected;
        
        // 使用 ObservableCollection 并绑定到 ItemsSource
        public ObservableCollection<ThumbnailItem> ThumbnailItems { get; } 
            = new ObservableCollection<ThumbnailItem>();
        
        private IStorageFile? _currentFile;
        
        public ThumbnailView()
        {
            InitializeComponent();
            
            // 排序选项变化时刷新
            SortByComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            OrderComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            
            // 复位按钮点击
            CenterButton.Click += (s, e) => ScrollToCurrent();
            
            // 缩略图点击事件
            ThumbnailItemsControl.AddHandler(
                PointerPressedEvent, 
                Thumbnail_PointerPressed, 
                RoutingStrategies.Tunnel);
        }
        
        public async Task SetFiles(IEnumerable<IStorageFile> files, IStorageFile? currentFile = null)
        {
            _currentFile = currentFile;
            ThumbnailItems.Clear();
            
            foreach (var file in files)
            {
                ThumbnailItems.Add(new ThumbnailItem 
                { 
                    File = file,
                    IsCurrent = file.Path.AbsolutePath == currentFile?.Path.AbsolutePath
                });
            }
            
            // 异步加载缩略图
            _ = LoadThumbnailsAsync();
            
            // 滚动到当前图片
            await Task.Delay(100); // 等待布局完成
            ScrollToCurrent();
        }
        
        private async Task LoadThumbnailsAsync()
        {
            foreach (var item in ThumbnailItems)
            {
                item.Image = await BitmapCacheService.GetBitmapAsync(item.File, 120);
            }
        }
        
        private void RefreshThumbnails()
        {
            if (ThumbnailItems.Count == 0) return;
            
            var sortBy = SortByComboBox.SelectedIndex;
            var order = OrderComboBox.SelectedIndex;
            
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
            
            ScrollToCurrent();
        }
        
        private void ScrollToCurrent()
        {
            if (_currentFile == null) return;
            
            var currentItem = ThumbnailItems.FirstOrDefault(t => 
                t.File.Path.AbsolutePath == _currentFile.Path.AbsolutePath);
            
            if (currentItem != null)
            {
                // 高亮当前项
                foreach (var item in ThumbnailItems)
                {
                    item.IsCurrent = item == currentItem;
                }
                
                // 滚动到可见区域
                ThumbnailItemsControl.ScrollIntoView(currentItem);
            }
        }
        
        private void Thumbnail_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Source is Image image && image.Tag is IStorageFile file)
            {
                ThumbnailSelected?.Invoke(this, file);
                e.Handled = true;
            }
            else if (e.Source is Border border && border.Child is Image childImage && childImage.Tag is IStorageFile borderFile)
            {
                ThumbnailSelected?.Invoke(this, borderFile);
                e.Handled = true;
            }
        }
        
        private void CenterButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToCurrent();
        }
        
        // 设置当前文件（从外部调用）
        public void SetCurrentFile(IStorageFile file)
        {
            _currentFile = file;
            ScrollToCurrent();
        }
        
    }

    // 转换器：当前项边框高亮
    public class BoolToCurrentBorderConverter : IValueConverter
    {
        public static readonly BoolToCurrentBorderConverter Instance = new();
            
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is true ? Brushes.DodgerBlue : Brushes.Transparent;
        }
            
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

// ThumbnailItem 类定义
    public class ThumbnailItem
    {
        public IStorageFile File { get; set; }
        public Bitmap? Image { get; set; }
        public bool IsCurrent { get; set; }
    }