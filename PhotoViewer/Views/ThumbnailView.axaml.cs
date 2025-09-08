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
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using System.Threading;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ThumbnailView : UserControl
{
    private readonly DispatcherTimer _scrollTimer = new DispatcherTimer();
    
    // 添加动画相关字段
    private Animation? _scrollAnimation;
    private CancellationTokenSource? _animationCancellationTokenSource;
    private Task? _currentAnimationTask;
    
    public FolderViewModel? ViewModel => DataContext as FolderViewModel;

    public ThumbnailView()
    {
        InitializeComponent();

        // 初始化滚动动画
        InitializeScrollAnimation();

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
        if (dataContext is FolderViewModel viewModel)
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
            for (int i = 0; i < ViewModel.Main.FolderVM.FilteredFiles.Count; i++)
            {
                var item = ViewModel.Main.FolderVM.FilteredFiles[i];
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
                var index = ViewModel.Main.FolderVM.FilteredFiles.IndexOf(currentFile);
                
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
        if (index < 0 || index >= ViewModel?.Main.FolderVM.FilteredFiles.Count) return;

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
    /// 初始化滚动动画
    /// </summary>
    private void InitializeScrollAnimation()
    {
        _scrollAnimation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(350), // 动画持续时间
            Easing = new CubicEaseOut(), // 缓动函数
            FillMode = FillMode.Forward
        };
    }
    
    /// <summary>
    /// 带缓动效果的滚动到指定位置
    /// </summary>
    /// <param name="targetOffset">目标偏移量</param>
    private async Task AnimateScrollToAsync(Vector targetOffset)
    {
        var scrollViewer = ThumbnailScrollViewer;
        if (scrollViewer == null) return;

        // 取消之前的动画
        await CancelCurrentAnimationAsync();
        
        // 创建新的取消令牌
        _animationCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _animationCancellationTokenSource.Token;
        
        try
        {
            var currentOffset = scrollViewer.Offset;
            
            // 如果目标位置和当前位置相同，直接返回
            if (Math.Abs(targetOffset.X - currentOffset.X) < 1 && 
                Math.Abs(targetOffset.Y - currentOffset.Y) < 1)
            {
                return;
            }

            // 检查是否已被取消
            cancellationToken.ThrowIfCancellationRequested();

            // 创建关键帧动画
            var keyFrame = new KeyFrame
            {
                Cue = new Cue(1.0)
            };
            
            // 根据布局方向选择动画属性
            var isVertical = ViewModel?.IsVerticalLayout ?? false;
            
            if (isVertical)
            {
                // 垂直滚动动画
                keyFrame.Setters.Add(new Setter(ScrollViewer.OffsetProperty, 
                    new Vector(currentOffset.X, targetOffset.Y)));
            }
            else
            {
                // 水平滚动动画
                keyFrame.Setters.Add(new Setter(ScrollViewer.OffsetProperty, 
                    new Vector(targetOffset.X, currentOffset.Y)));
            }

            _scrollAnimation.Children.Clear();
            _scrollAnimation.Children.Add(keyFrame);

            // 检查是否已被取消
            cancellationToken.ThrowIfCancellationRequested();

            // 执行动画并保存任务引用
            _currentAnimationTask = _scrollAnimation.RunAsync(scrollViewer, cancellationToken);
            await _currentAnimationTask;
        }
        catch (OperationCanceledException)
        {
            // 动画被取消，不需要处理
            Console.WriteLine("滚动动画被取消");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"滚动动画失败: {ex.Message}");
            // 如果动画失败，直接设置位置
            if (!cancellationToken.IsCancellationRequested)
            {
                scrollViewer.Offset = targetOffset;
            }
        }
        finally
        {
            // 清理资源
            if (_animationCancellationTokenSource?.Token == cancellationToken)
            {
                _animationCancellationTokenSource?.Dispose();
                _animationCancellationTokenSource = null;
                _currentAnimationTask = null;
            }
        }
    }
    
    /// <summary>
    /// 取消当前正在执行的动画
    /// </summary>
    private async Task CancelCurrentAnimationAsync()
    {
        if (_animationCancellationTokenSource != null && !_animationCancellationTokenSource.Token.IsCancellationRequested)
        {
            _animationCancellationTokenSource.Cancel();
            
            // 等待当前动画完成取消
            if (_currentAnimationTask != null)
            {
                try
                {
                    await _currentAnimationTask;
                }
                catch (OperationCanceledException)
                {
                    // 预期的取消异常，忽略
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"等待动画取消时发生错误: {ex.Message}");
                }
            }
            
            _animationCancellationTokenSource?.Dispose();
            _animationCancellationTokenSource = null;
            _currentAnimationTask = null;
        }
    }
    
    /// <summary>
    /// 滚动到指定的容器控件
    /// </summary>
    /// <param name="container">要滚动到的容器控件</param>
    private async void ScrollToContainer(Control container)
    {
        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            if (scrollViewer == null) return;

            // 获取容器相对于ItemsControl的位置
            var containerPosition = container.TranslatePoint(new Point(), ThumbnailItemsControl);
            if (containerPosition == null) return;

            var isVertical = ViewModel?.IsVerticalLayout ?? false;
            Vector targetOffset;
            
            if (isVertical)
            {
                // 垂直布局：滚动Y轴
                var targetY = containerPosition.Value.Y;
                var viewportHeight = scrollViewer.Viewport.Height;
                var containerHeight = container.Bounds.Height;
                
                // 计算居中位置
                var centerY = targetY - (viewportHeight - containerHeight) / 2;
                centerY = Math.Max(0, Math.Min(centerY, scrollViewer.Extent.Height - viewportHeight));
                
                targetOffset = new Vector(scrollViewer.Offset.X, centerY);
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
                
                targetOffset = new Vector(centerX, scrollViewer.Offset.Y);
            }

            // 使用缓动动画滚动到目标位置（会自动取消之前的动画）
            await AnimateScrollToAsync(targetOffset);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"滚动到容器失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 公共方法：平滑滚动到指定偏移量
    /// </summary>
    /// <param name="offset">目标偏移量</param>
    public async Task SmoothScrollToAsync(Vector offset)
    {
        await AnimateScrollToAsync(offset);
    }

    /// <summary>
    /// 立即停止所有滚动动画
    /// </summary>
    public async Task StopAnimationAsync()
    {
        await CancelCurrentAnimationAsync();
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

