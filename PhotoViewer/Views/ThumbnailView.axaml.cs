using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ThumbnailView : UserControl
{
    private readonly DispatcherTimer _scrollTimer = new DispatcherTimer();
    public ThumbnailViewModel? ViewModel => DataContext as ThumbnailViewModel;

    public ThumbnailView()
    {
        InitializeComponent();

        // 设置滚动计时器（300ms延迟）
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
        _scrollTimer.Tick += async (s, e) =>
        {
            _scrollTimer.Stop();
            await LoadVisibleThumbnailsAsync();
        };

        // 滚动事件处理
        ThumbnailScrollViewer.ScrollChanged += OnScrollChanged;

        // 排序选项变化时刷新
        SortByComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();
        OrderComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();
        
        // 监听DataContext变化
        this.WhenAnyValue(x => x.DataContext)
            .Subscribe(OnDataContextChanged);
    }
    
    private void OnDataContextChanged(object? dataContext)
    {
        if (dataContext is ThumbnailViewModel viewModel)
        {
            // 订阅滚动到当前图片的事件
            viewModel.ScrollToCurrentRequested += ScrollToCurrentImage;
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
            for (int i = 0; i < ViewModel.Main.FilteredFiles.Count; i++)
            {
                var item = ViewModel.Main.FilteredFiles[i];
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

    /// <summary>
    /// 滚动到当前选中的图片
    /// </summary>
    private async void ScrollToCurrentImage()
    {
        if (ViewModel?.Main.CurrentFile == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var currentFile = ViewModel.Main.CurrentFile;
                var index = ViewModel.Main.FilteredFiles.IndexOf(currentFile);
                
                if (index >= 0)
                {
                    ScrollToIndex(index);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"滚动到当前图片失败: {ex.Message}");
            }
        });
    }
    
    /// <summary>
    /// 滚动到指定索引的图片
    /// </summary>
    /// <param name="index">图片在列表中的索引</param>
    private void ScrollToIndex(int index)
    {
        if (index < 0 || index >= ViewModel?.Main.FilteredFiles.Count) return;

        try
        {
            // 获取ItemsControl中的容器
            var container = ThumbnailItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as Control;
            if (container == null)
            {
                // 如果容器未创建，等待一下再试
                Dispatcher.UIThread.Post(() =>
                {
                    var retryContainer = ThumbnailItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as Control;
                    if (retryContainer != null)
                    {
                        ScrollToContainer(retryContainer);
                    }
                }, DispatcherPriority.Background);
                return;
            }

            ScrollToContainer(container);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"滚动到索引 {index} 失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 滚动到指定的容器控件
    /// </summary>
    /// <param name="container">要滚动到的容器控件</param>
    private void ScrollToContainer(Control container)
    {
        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            if (scrollViewer == null) return;

            // 获取容器相对于ItemsControl的位置
            var containerPosition = container.TranslatePoint(new Point(), ThumbnailItemsControl);
            if (containerPosition == null) return;

            var isVertical = ViewModel?.IsVerticalLayout ?? false;
            
            if (isVertical)
            {
                // 垂直布局：滚动Y轴
                var targetY = containerPosition.Value.Y;
                var viewportHeight = scrollViewer.Viewport.Height;
                var containerHeight = container.Bounds.Height;
                
                // 计算居中位置
                var centerY = targetY - (viewportHeight - containerHeight) / 2;
                centerY = Math.Max(0, Math.Min(centerY, scrollViewer.Extent.Height - viewportHeight));
                
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, centerY);
            }
            else
            {
                // 水平布局：滚动X轴
                var targetX = containerPosition.Value.X;
                var viewportWidth = scrollViewer.Viewport.Width;
                var containerWidth = container.Bounds.Width;
                
                // 计算居中位置
                var centerX = targetX - (viewportWidth - containerWidth) / 2;
                centerX = Math.Max(0, Math.Min(centerX, scrollViewer.Extent.Width - viewportWidth));
                
                scrollViewer.Offset = new Vector(centerX, scrollViewer.Offset.Y);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"滚动到容器失败: {ex.Message}");
        }
    }
}

// 布尔到边框颜色转换器：当前项边框高亮
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

// 文件大小转换器
public class FileSizeConverter : IValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ulong bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 日期格式转换器
public class DateTimeConverter : IValueConverter
{
    public static readonly DateTimeConverter Instance = new();
        
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dateTime)
        {
            return dateTime.ToString("MM-dd HH:mm");
        }
        return string.Empty;
    }
        
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 布局方向转换器
public class LayoutOrientationConverter : IValueConverter
{
    public static readonly LayoutOrientationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? Orientation.Vertical : Orientation.Horizontal;
        }
        return Orientation.Horizontal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Orientation orientation)
        {
            return orientation == Orientation.Vertical;
        }
        return false;
    }
}

// 滚动条可见性转换器
public class ScrollBarVisibilityConverter : IValueConverter
{
    public static readonly ScrollBarVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            bool reverse = parameter?.ToString() == "Reverse";
            
            if (reverse)
            {
                return isVertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
            else
            {
                return isVertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
        }
        return ScrollBarVisibility.Auto;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下的水平对齐转换器
public class VerticalLayoutAlignmentConverter : IValueConverter
{
    public static readonly VerticalLayoutAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }
        return HorizontalAlignment.Stretch;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下的垂直对齐转换器
public class VerticalLayoutVerticalAlignmentConverter : IValueConverter
{
    public static readonly VerticalLayoutVerticalAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }
        return VerticalAlignment.Stretch;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
