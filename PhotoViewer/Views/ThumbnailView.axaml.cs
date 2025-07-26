using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

    public partial class ThumbnailView : UserControl
    {
        private readonly DispatcherTimer _scrollTimer = new DispatcherTimer();
        private ThumbnailViewModel? ViewModel => DataContext as ThumbnailViewModel;
        
        public ThumbnailView()
        {
            InitializeComponent();
            
            // 设置滚动计时器（300ms延迟）
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
            _scrollTimer.Tick += async (s, e) => {
                _scrollTimer.Stop();
                await LoadVisibleThumbnailsAsync();
            };
            
            // 滚动事件处理
            ThumbnailScrollViewer.ScrollChanged += OnScrollChanged;
            
            // 排序选项变化时刷新
            SortByComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            OrderComboBox.SelectionChanged += (s, e) => RefreshThumbnails();
            
            // 监听数据上下文变化
            this.WhenAnyValue(x => x.DataContext)
                .Where(dc => dc is ThumbnailViewModel)
                .Subscribe(_ => InitializeViewModel());
        }
        
        private void InitializeViewModel()
        {
            if (ViewModel != null)
            {
                // 初始加载可见缩略图
                Dispatcher.UIThread.Post(async () => 
                {
                    await Task.Delay(100); // 等待布局完成
                    await LoadVisibleThumbnailsAsync();
                });
            }
        }
        
        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // 使用计时器延迟加载，避免频繁滚动时重复加载
            _scrollTimer.Stop();
            _scrollTimer.Start();
        }
        
        private async Task LoadVisibleThumbnailsAsync()
        {
            if (ViewModel == null) return;
            
            try
            {
                var scrollViewer = ThumbnailScrollViewer;
                if (scrollViewer == null) return;
                
                // 计算可见区域
                double startX = scrollViewer.Offset.X;
                double endX = startX + scrollViewer.Viewport.Width;
                
                // 加载可见区域及附近区域的缩略图
                for (int i = 0; i < ViewModel.DisplayedFiles.Count; i++)
                {
                    var item = ViewModel.DisplayedFiles[i];
                    if (item.Thumbnail != null) continue; // 已经加载过
                    
                    // 获取该项在列表中的位置
                    var container = ThumbnailItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as Control;
                    if (container == null) continue;
                    
                    // 计算该项的位置
                    var position = container.TranslatePoint(new Point(), ThumbnailItemsControl) ?? new Point();
                    double itemStartX = position.X;
                    double itemEndX = itemStartX + container.Bounds.Width;
                    
                    // 检查是否在可见区域附近（左右各加200像素缓冲区）
                    if (itemEndX >= startX - 200 && itemStartX <= endX + 200)
                    {
                        await item.LoadThumbnailAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载可见缩略图失败: {ex.Message}");
            }
        }
        
        private void RefreshThumbnails()
        {
            // 排序逻辑现在在 ViewModel 中处理
            // 这里只需触发 ViewModel 的刷新
        }
        
        private void CenterButton_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (ViewModel?._mainViewModel?.CurrentFile != null)
            {
                var currentItem = ViewModel.DisplayedFiles.FirstOrDefault(
                    f => f.File == ViewModel._mainViewModel.CurrentFile);
                
                if (currentItem != null)
                {
                    ThumbnailItemsControl.ScrollIntoView(currentItem);
                }
            }
        }
    }

    // 转换器：当前项边框高亮
    public class BoolToCurrentBorderConverter : IValueConverter
    {
        public static readonly BoolToCurrentBorderConverter Instance = new();
        
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isCurrent && isCurrent)
            {
                return new SolidColorBrush(Colors.DodgerBlue);
            }
            return new SolidColorBrush(Colors.Transparent);
        }
        
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class ObjectToVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isVisible = value == null;
            
            // 如果参数为 "Invert"，则反转可见性
            if (parameter is string strParam && strParam.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            {
                isVisible = !isVisible;
            }
            
            return isVisible;
        }
        
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
