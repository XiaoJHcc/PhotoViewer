using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
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
    private readonly DispatcherTimer _scrollingTimer = new DispatcherTimer(); // 新增：滚动中计时器
    
    // 添加动画相关字段
    private Animation? _scrollAnimation;
    private CancellationTokenSource? _animationCancellationTokenSource;
    private Task? _currentAnimationTask;
    
    // 滚动状态跟踪
    private bool _isScrolling = false;
    private Vector _lastScrollOffset = Vector.Zero;
    
    public FolderViewModel? ViewModel => DataContext as FolderViewModel;

    public ThumbnailView()
    {
        InitializeComponent();

        // 初始化滚动动画
        InitializeScrollAnimation();

        // 设置滚动结束计时器（300ms延迟确认滚动结束）
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
        _scrollTimer.Tick += async (s, e) =>
        {
            _scrollTimer.Stop();
            _isScrolling = false;
            await LoadVisibleThumbnailsAsync();
        };

        // 设置滚动中计时器（100ms间隔实时加载）
        _scrollingTimer.Interval = TimeSpan.FromMilliseconds(100);
        _scrollingTimer.Tick += async (s, e) =>
        {
            if (_isScrolling)
            {
                await LoadVisibleThumbnailsAsync();
            }
        };

        // 滚动事件处理
        ThumbnailScrollViewer.ScrollChanged += OnScrollChanged;

        // 排序选项变化时刷新
        SortByComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();
        OrderComboBox.SelectionChanged += (s, e) => LoadVisibleThumbnailsAsync();
        
        // 监听DataContext变化
        this.WhenAnyValue(x => x.DataContext)
            .Subscribe(OnDataContextChanged);
            
        // 组件加载完成后初始加载可见缩略图
        this.Loaded += async (s, e) =>
        {
            await Task.Delay(100); // 等待布局完成
            await LoadVisibleThumbnailsAsync();
        };
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
        var currentOffset = ThumbnailScrollViewer?.Offset ?? Vector.Zero;
        
        // 检测是否真的在滚动（偏移量发生变化）
        if (Math.Abs(currentOffset.X - _lastScrollOffset.X) > 1 || 
            Math.Abs(currentOffset.Y - _lastScrollOffset.Y) > 1)
        {
            _lastScrollOffset = currentOffset;
            
            if (!_isScrolling)
            {
                // 开始滚动
                _isScrolling = true;
                _scrollingTimer.Start(); // 启动滚动中的实时加载
            }
            
            // 重置滚动结束计时器
            _scrollTimer.Stop();
            _scrollTimer.Start();
        }
    }

    private async Task LoadVisibleThumbnailsAsync()
    {
        if (ViewModel == null) return;

        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            var itemsControl = ThumbnailItemsControl;
            if (scrollViewer == null || itemsControl == null) return;

            var isVertical = ViewModel.IsVerticalLayout;
            var viewport = scrollViewer.Viewport;
            var offset = scrollViewer.Offset;
            
            // 根据滚动状态调整缓冲区大小
            double bufferSize = _isScrolling ? 300 : 200; // 滚动中增大缓冲区
            
            var visibleFiles = new List<Core.ImageFile>();
            var itemCount = ViewModel.FilteredFiles.Count;
            
            // 简化可见性检测：基于项目索引估算位置
            var estimatedItemSize = isVertical ? 150.0 : 100.0; // 估算的项目尺寸
            var viewportSize = isVertical ? viewport.Height : viewport.Width;
            var scrollPosition = isVertical ? offset.Y : offset.X;
            
            // 计算可见范围的索引
            var startIndex = Math.Max(0, (int)((scrollPosition - bufferSize) / estimatedItemSize));
            var endIndex = Math.Min(itemCount - 1, (int)((scrollPosition + viewportSize + bufferSize) / estimatedItemSize));
            
            // 扩展范围以确保覆盖
            startIndex = Math.Max(0, startIndex - 2);
            endIndex = Math.Min(itemCount - 1, endIndex + 2);
            
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (i < itemCount)
                {
                    var item = ViewModel.FilteredFiles[i];
                    
                    // 尝试获取实际容器进行精确检测
                    var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as Control;
                    if (container != null && container.IsMeasureValid && container.IsArrangeValid)
                    {
                        try
                        {
                            var containerPosition = container.TranslatePoint(new Point(0, 0), itemsControl);
                            if (containerPosition.HasValue)
                            {
                                var containerBounds = container.Bounds;
                                double itemStart, itemEnd;
                                double viewportStart, viewportEnd;
                                
                                if (isVertical)
                                {
                                    itemStart = containerPosition.Value.Y;
                                    itemEnd = itemStart + containerBounds.Height;
                                    viewportStart = offset.Y;
                                    viewportEnd = offset.Y + viewport.Height;
                                }
                                else
                                {
                                    itemStart = containerPosition.Value.X;
                                    itemEnd = itemStart + containerBounds.Width;
                                    viewportStart = offset.X;
                                    viewportEnd = offset.X + viewport.Width;
                                }

                                // 精确的可见性检测
                                if (itemEnd >= viewportStart - bufferSize && itemStart <= viewportEnd + bufferSize)
                                {
                                    visibleFiles.Add(item);
                                }
                                continue;
                            }
                        }
                        catch
                        {
                            // 如果精确检测失败，回退到估算方式
                        }
                    }
                    
                    // 回退到估算方式
                    visibleFiles.Add(item);
                }
            }
            
            // 使用FolderViewModel的批量加载方法
            ViewModel.LoadVisibleThumbnails(visibleFiles);
        }
        catch (Exception ex)
        {
            Console.WriteLine("加载可见缩略图失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 滚动到当前选中的图片
    /// </summary>
    private async void ScrollToCurrentImage()
    {
        if (ViewModel?.Main.CurrentFile == null) return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var currentFile = ViewModel.Main.CurrentFile;
                var index = ViewModel.FilteredFiles.IndexOf(currentFile);
                
                if (index >= 0)
                {
                    ScrollToIndex(index);
                    
                    // 滚动完成后加载可见区域缩略图
                    await Task.Delay(100);
                    await LoadVisibleThumbnailsAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("滚动到当前图片失败: " + ex.Message);
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
            Console.WriteLine("滚动到索引 " + index + " 失败: " + ex.Message);
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

            // 标记为动画滚动状态
            _isScrolling = true;
            _scrollingTimer.Start();

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
            
            // 动画完成后的处理
            _isScrolling = false;
            _scrollingTimer.Stop();
            
            // 动画完成后加载可见区域缩略图
            await Task.Delay(50, cancellationToken);
            await LoadVisibleThumbnailsAsync();
        }
        catch (OperationCanceledException)
        {
            // 动画被取消，不需要处理
        }
        catch (Exception ex)
        {
            Console.WriteLine("滚动动画失败: " + ex.Message);
            // 如果动画失败，直接设置位置
            if (!cancellationToken.IsCancellationRequested)
            {
                scrollViewer.Offset = targetOffset;
                _isScrolling = false;
                _scrollingTimer.Stop();
                // 设置位置后加载可见区域缩略图
                await LoadVisibleThumbnailsAsync();
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
            
            // 停止滚动中的计时器
            _scrollingTimer.Stop();
            _isScrolling = false;
            
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
                    Console.WriteLine("等待动画取消时发生错误: " + ex.Message);
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
            var itemsControl = ThumbnailItemsControl;
            if (scrollViewer == null || itemsControl == null) return;

            // 确保容器已经测量和排列
            if (!container.IsMeasureValid || !container.IsArrangeValid)
            {
                // 等待布局完成
                await Task.Delay(50);
                if (!container.IsMeasureValid || !container.IsArrangeValid)
                {
                    return;
                }
            }

            // 获取容器相对于ItemsControl的位置
            var containerPosition = container.TranslatePoint(new Point(0, 0), itemsControl);
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
            Console.WriteLine("滚动到容器失败: " + ex.Message);
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
        _scrollingTimer.Stop();
        _isScrolling = false;
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

// 拍摄日期格式转换器
public class PhotoDateConverter : IValueConverter
{
    public static readonly PhotoDateConverter Instance = new();
        
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
