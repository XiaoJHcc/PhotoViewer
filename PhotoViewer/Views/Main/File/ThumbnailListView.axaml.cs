using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.ViewModels.File;

namespace PhotoViewer.Views.Main.File;

/// <summary>
/// 主缩略图列表视图。
/// 负责:可见区域估算与上报、滚动到当前项的动画、星级点击转发到 MainViewModel.SetRatingAsync。
/// 数据由 <see cref="ThumbnailListViewModel"/> 提供。
/// </summary>
public partial class ThumbnailListView : UserControl
{
    private readonly DispatcherTimer _scrollTimer = new();
    private readonly DispatcherTimer _scrollingTimer = new();

    private Animation? _scrollAnimation;
    private CancellationTokenSource? _animationCancellationTokenSource;
    private Task? _currentAnimationTask;

    private bool _isScrolling;
    private Vector _lastScrollOffset = Vector.Zero;

    private ScrollViewer? _scroll;
    private DispatcherTimer? _debounceTimer;
    private double _lastHorizontalOffset;
    private double _lastVerticalOffset;
    private ThumbnailListViewModel? _attachedViewModel;

    public ThumbnailListViewModel? ViewModel => DataContext as ThumbnailListViewModel;

    public ThumbnailListView()
    {
        InitializeComponent();

        InitializeScrollAnimation();

        _scrollTimer.Interval = TimeSpan.FromMilliseconds(300);
        _scrollTimer.Tick += async (s, e) =>
        {
            _scrollTimer.Stop();
            _isScrolling = false;
            await LoadVisibleThumbnailsAsync();
        };

        _scrollingTimer.Interval = TimeSpan.FromMilliseconds(100);
        _scrollingTimer.Tick += async (s, e) =>
        {
            if (_isScrolling)
            {
                await LoadVisibleThumbnailsAsync();
            }
        };

        ThumbnailScrollViewer.ScrollChanged += OnScrollChangedThumbnail;

        this.WhenAnyValue(x => x.DataContext)
            .Subscribe(OnDataContextChanged);

        this.Loaded += async (s, e) =>
        {
            await Task.Delay(100);
            await LoadVisibleThumbnailsAsync();
        };

        this.AttachedToVisualTree += OnAttached;
        this.DetachedFromVisualTree += OnDetached;
    }

    /// <summary>
    /// DataContext 变化后重新订阅 VM 事件。
    /// </summary>
    private void OnDataContextChanged(object? dataContext)
    {
        UpdateViewModelSubscription(dataContext as ThumbnailListViewModel);
    }

    /// <summary>
    /// 在 VM 切换时安全地解绑/重绑 ScrollToCurrentRequested 订阅。
    /// </summary>
    private void UpdateViewModelSubscription(ThumbnailListViewModel? viewModel)
    {
        if (ReferenceEquals(_attachedViewModel, viewModel))
        {
            return;
        }

        if (_attachedViewModel != null)
        {
            _attachedViewModel.ScrollToCurrentRequested -= ScrollToCurrentImage;
        }

        _attachedViewModel = viewModel;

        if (_attachedViewModel != null)
        {
            _attachedViewModel.ScrollToCurrentRequested += ScrollToCurrentImage;
        }
    }

    /// <summary>
    /// 滚动事件:启动滚动中实时加载与滚动结束防抖。
    /// </summary>
    private void OnScrollChangedThumbnail(object? sender, ScrollChangedEventArgs e)
    {
        var currentOffset = ThumbnailScrollViewer?.Offset ?? Vector.Zero;

        if (Math.Abs(currentOffset.X - _lastScrollOffset.X) > 1 ||
            Math.Abs(currentOffset.Y - _lastScrollOffset.Y) > 1)
        {
            _lastScrollOffset = currentOffset;

            if (!_isScrolling)
            {
                _isScrolling = true;
                _scrollingTimer.Start();
            }

            _scrollTimer.Stop();
            _scrollTimer.Start();
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateViewModelSubscription(DataContext as ThumbnailListViewModel);

        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScrollChangedBitmapPrefetch;
        }

        _scroll = this.FindControl<ScrollViewer>("ThumbnailScrollViewer");
        if (_scroll != null)
        {
            _scroll.ScrollChanged += OnScrollChangedBitmapPrefetch;
        }
    }

    private async void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateViewModelSubscription(null);

        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScrollChangedBitmapPrefetch;
            _scroll = null;
        }

        _scrollTimer.Stop();
        _scrollingTimer.Stop();
        _debounceTimer?.Stop();
        _isScrolling = false;
        await CancelCurrentAnimationAsync();
    }

    /// <summary>
    /// 滚动事件触发位图预取防抖。
    /// </summary>
    private void OnScrollChangedBitmapPrefetch(object? s, ScrollChangedEventArgs e)
    {
        if (Math.Abs(_lastHorizontalOffset - _scroll!.Offset.X) < 0.1 &&
            Math.Abs(_lastVerticalOffset - _scroll.Offset.Y) < 0.1)
            return;

        _lastHorizontalOffset = _scroll.Offset.X;
        _lastVerticalOffset = _scroll.Offset.Y;

        RestartDebounce();
    }

    private void RestartDebounce()
    {
        var vm = ViewModel;
        if (vm == null) return;
        var delayMs = vm.Main.Settings.VisibleCenterDelayMs;

        _debounceTimer?.Stop();
        _debounceTimer ??= new DispatcherTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            TryReportVisibleRange();
        };
        _debounceTimer.Start();
    }

    /// <summary>
    /// 估算可见范围并上报给 VM 触发位图中心预取。
    /// </summary>
    private void TryReportVisibleRange()
    {
        var vm = ViewModel;
        if (vm == null || _scroll == null) return;

        bool vertical = vm.IsVerticalLayout;
        double itemExtent = vertical ? 138 + 6 : 90 + 6;

        double offset = vertical ? _scroll.Offset.Y : _scroll.Offset.X;
        double viewport = vertical ? _scroll.Viewport.Height : _scroll.Viewport.Width;

        if (itemExtent < 1) return;

        int firstIndex = (int)Math.Floor(offset / itemExtent);
        if (firstIndex < 0) firstIndex = 0;

        int visibleCount = (int)Math.Ceiling(viewport / itemExtent) + 2;
        int lastIndex = firstIndex + visibleCount - 1;

        var total = vm.FilteredFiles.Count;
        if (total == 0) return;

        if (lastIndex >= total) lastIndex = total - 1;
        if (firstIndex >= total) firstIndex = total - 1;

        vm.ReportVisibleRange(firstIndex, lastIndex);
    }

    /// <summary>
    /// 计算当前可见区域的文件并交给 VM 调度缩略图加载。
    /// </summary>
    private Task LoadVisibleThumbnailsAsync()
    {
        if (ViewModel == null) return Task.CompletedTask;

        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            var itemsControl = ThumbnailItemsControl;
            if (scrollViewer == null || itemsControl == null) return Task.CompletedTask;

            var isVertical = ViewModel.IsVerticalLayout;
            var viewport = scrollViewer.Viewport;
            var offset = scrollViewer.Offset;

            double bufferSize = _isScrolling ? 300 : 200;

            var visibleFiles = new List<Core.ImageFile>();
            var itemCount = ViewModel.FilteredFiles.Count;

            var estimatedItemSize = isVertical ? 144.0 : 96.0;
            var viewportSize = isVertical ? viewport.Height : viewport.Width;
            var scrollPosition = isVertical ? offset.Y : offset.X;

            var startIndex = Math.Max(0, (int)((scrollPosition - bufferSize) / estimatedItemSize));
            var endIndex = Math.Min(itemCount - 1, (int)((scrollPosition + viewportSize + bufferSize) / estimatedItemSize));

            startIndex = Math.Max(0, startIndex - 2);
            endIndex = Math.Min(itemCount - 1, endIndex + 2);

            for (int i = startIndex; i <= endIndex; i++)
            {
                if (i < itemCount)
                {
                    var item = ViewModel.FilteredFiles[i];

                    var container = itemsControl.ContainerFromIndex(i) as Control;
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

                                if (itemEnd >= viewportStart - bufferSize && itemStart <= viewportEnd + bufferSize)
                                {
                                    visibleFiles.Add(item);
                                }
                                continue;
                            }
                        }
                        catch
                        {
                            // 精确检测失败,回退估算
                        }
                    }

                    visibleFiles.Add(item);
                }
            }

            ViewModel.LoadVisibleThumbnails(visibleFiles);
        }
        catch (Exception ex)
        {
            Console.WriteLine("加载可见缩略图失败: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 滚动到当前选中的图片(由 VM 事件触发)。
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
    /// 滚动到指定索引:容器已实例化则直接定位,否则按估算位置滚动。
    /// </summary>
    private void ScrollToIndex(int index)
    {
        if (ViewModel == null || index < 0 || index >= ViewModel.FilteredFiles.Count) return;

        try
        {
            var container = ThumbnailItemsControl.ContainerFromIndex(index) as Control;
            if (container != null)
            {
                ScrollToContainer(container);
                return;
            }

            var scrollViewer = ThumbnailScrollViewer;
            if (scrollViewer == null) return;

            bool isVertical = ViewModel.IsVerticalLayout;
            double itemExtent = isVertical ? (138 + 6) : (90 + 6);
            double targetPos = index * itemExtent;
            double viewport = isVertical ? scrollViewer.Viewport.Height : scrollViewer.Viewport.Width;
            double centered = Math.Max(0, targetPos - (viewport - itemExtent) / 2);

            var targetOffset = isVertical
                ? new Vector(scrollViewer.Offset.X, centered)
                : new Vector(centered, scrollViewer.Offset.Y);

            _ = AnimateScrollToAsync(targetOffset);
        }
        catch (Exception ex)
        {
            Console.WriteLine("滚动到索引 " + index + " 失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 初始化滚动动画。
    /// </summary>
    private void InitializeScrollAnimation()
    {
        _scrollAnimation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(350),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward
        };
    }

    /// <summary>
    /// 带缓动效果滚动到指定偏移量。
    /// </summary>
    private async Task AnimateScrollToAsync(Vector targetOffset)
    {
        var scrollViewer = ThumbnailScrollViewer;
        if (scrollViewer == null) return;

        await CancelCurrentAnimationAsync();

        _animationCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _animationCancellationTokenSource.Token;

        try
        {
            var currentOffset = scrollViewer.Offset;

            if (Math.Abs(targetOffset.X - currentOffset.X) < 1 &&
                Math.Abs(targetOffset.Y - currentOffset.Y) < 1)
            {
                return;
            }

            _isScrolling = true;
            _scrollingTimer.Start();

            cancellationToken.ThrowIfCancellationRequested();

            var keyFrame = new KeyFrame
            {
                Cue = new Cue(1.0)
            };

            var isVertical = ViewModel?.IsVerticalLayout ?? false;

            if (isVertical)
            {
                keyFrame.Setters.Add(new Setter(ScrollViewer.OffsetProperty,
                    new Vector(currentOffset.X, targetOffset.Y)));
            }
            else
            {
                keyFrame.Setters.Add(new Setter(ScrollViewer.OffsetProperty,
                    new Vector(targetOffset.X, currentOffset.Y)));
            }

            _scrollAnimation!.Children.Clear();
            _scrollAnimation.Children.Add(keyFrame);

            cancellationToken.ThrowIfCancellationRequested();

            _currentAnimationTask = _scrollAnimation.RunAsync(scrollViewer, cancellationToken);
            await _currentAnimationTask;

            _isScrolling = false;
            _scrollingTimer.Stop();

            await Task.Delay(50, cancellationToken);
            await LoadVisibleThumbnailsAsync();
        }
        catch (OperationCanceledException)
        {
            // 动画被取消
        }
        catch (Exception ex)
        {
            Console.WriteLine("滚动动画失败: " + ex.Message);
            if (!cancellationToken.IsCancellationRequested)
            {
                scrollViewer.Offset = targetOffset;
                _isScrolling = false;
                _scrollingTimer.Stop();
                await LoadVisibleThumbnailsAsync();
            }
        }
        finally
        {
            if (_animationCancellationTokenSource?.Token == cancellationToken)
            {
                _animationCancellationTokenSource?.Dispose();
                _animationCancellationTokenSource = null;
                _currentAnimationTask = null;
            }
        }
    }

    /// <summary>
    /// 取消当前正在执行的动画。
    /// </summary>
    private async Task CancelCurrentAnimationAsync()
    {
        if (_animationCancellationTokenSource != null && !_animationCancellationTokenSource.Token.IsCancellationRequested)
        {
            _animationCancellationTokenSource.Cancel();

            _scrollingTimer.Stop();
            _isScrolling = false;

            if (_currentAnimationTask != null)
            {
                try
                {
                    await _currentAnimationTask;
                }
                catch (OperationCanceledException)
                {
                    // 预期的取消异常,忽略
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
    /// 滚动到指定容器控件。
    /// </summary>
    private async void ScrollToContainer(Control container)
    {
        try
        {
            var scrollViewer = ThumbnailScrollViewer;
            var itemsControl = ThumbnailItemsControl;
            if (scrollViewer == null || itemsControl == null) return;

            if (!container.IsMeasureValid || !container.IsArrangeValid)
            {
                await Task.Delay(50);
                if (!container.IsMeasureValid || !container.IsArrangeValid)
                {
                    return;
                }
            }

            var containerPosition = container.TranslatePoint(new Point(0, 0), itemsControl);
            if (containerPosition == null) return;

            var isVertical = ViewModel?.IsVerticalLayout ?? false;
            Vector targetOffset;

            if (isVertical)
            {
                var targetY = containerPosition.Value.Y;
                var viewportHeight = scrollViewer.Viewport.Height;
                var containerHeight = container.Bounds.Height;

                var centerY = targetY - (viewportHeight - containerHeight) / 2;
                centerY = Math.Max(0, Math.Min(centerY, scrollViewer.Extent.Height - viewportHeight));

                targetOffset = new Vector(scrollViewer.Offset.X, centerY);
            }
            else
            {
                var targetX = containerPosition.Value.X;
                var viewportWidth = scrollViewer.Viewport.Width;
                var containerWidth = container.Bounds.Width;

                var centerX = targetX - (viewportWidth - containerWidth) / 2;
                centerX = Math.Max(0, Math.Min(centerX, scrollViewer.Extent.Width - viewportWidth));

                targetOffset = new Vector(centerX, scrollViewer.Offset.Y);
            }

            await AnimateScrollToAsync(targetOffset);
        }
        catch (Exception ex)
        {
            Console.WriteLine("滚动到容器失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 星级按钮点击:从 Tag 解析星级,转交 MainViewModel.SetRatingAsync。
    /// </summary>
    private void OnThumbStarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (sender is Button btn &&
            btn.Tag is string s &&
            int.TryParse(s, out var rating) &&
            btn.DataContext is Core.ImageFile file)
        {
            _ = ViewModel.Main.SetRatingAsync(file, rating);
        }
    }
}
