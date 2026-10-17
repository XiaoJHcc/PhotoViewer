using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生胶片条:单行/单列虚拟化列表 + 卡片池 + 可见区上报 + 350ms 三次缓出定位
/// (契约 FILM-01/02/04/05)。业务索引即几何顺序,项距由 <see cref="WindowsLayout"/> 固定。
/// </summary>
internal sealed class WindowsFilmstripView : Grid
{
    /// <summary>定位动画时长(契约 FILM-02)。</summary>
    private const double LocateAnimationMilliseconds = 350;

    /// <summary>可见区外额外加载的卡片数。</summary>
    private const int VisibleRangeOverscan = 2;

    /// <summary>一次定位请求最多启动的动画次数(列表在动画期间重建会把偏移清回 0,需要补做)。</summary>
    private const int LocateMaxAttempts = 4;

    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _list;
    private readonly Action<ImageFile> _select;
    private readonly Func<ImageFile, int, Task> _rate;
    private readonly ScrollViewer _scroll;
    private readonly ItemsRepeater _repeater;
    private readonly StackLayout _stackLayout;
    private readonly Stack<WindowsThumbnailCard> _pool = new();

    private DispatcherQueueTimer? _animationTimer;
    private DateTime _animationStartedAt;
    private double _animationFrom;
    private double _animationTo;
    private bool _isRowLayout = true;
    private bool _isAnimating;
    private bool _locatePending;
    private int _locateAttempts;
    private int _reportedFirst = -1;
    private int _reportedLast = -1;

    /// <summary>
    /// 创建胶片条。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="select">选中回调</param>
    /// <param name="rate">评分回调</param>
    public WindowsFilmstripView(MainViewModel main, Action<ImageFile> select, Func<ImageFile, int, Task> rate)
    {
        _main = main;
        _list = main.FileVM.ThumbnailList;
        _select = select;
        _rate = rate;

        _stackLayout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 0 };
        _repeater = new ItemsRepeater
        {
            Layout = _stackLayout,
            ItemTemplate = new CardFactory(this),
            Margin = new Thickness(
                WindowsLayout.FilmstripPadLeft,
                WindowsLayout.FilmstripPadTop,
                WindowsLayout.FilmstripPadRight,
                WindowsLayout.FilmstripPadBottom),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            // ZoomMode 默认 Enabled,会让 ScrollViewer 用无限尺寸测量内容,虚拟化容器的测量因此失败
            ZoomMode = ZoomMode.Disabled,
            // 上下布局时文件栏行高由胶片条决定,旧壳 ScrollViewer 固定 MinHeight=148
            MinHeight = WindowsLayout.FilmstripMinHeight,
            Content = _repeater,
        };
        _scroll.ViewChanged += (_, _) => ReportVisibleRange();
        Children.Add(_scroll);

        // 定位请求可能先于布局到达(启动期/换文件夹),布局每轮结束补做一次
        LayoutUpdated += OnLayoutUpdated;
        _list.ScrollToCurrentRequested += ScrollToCurrentAnimated;
        _list.PropertyChanged += OnListChanged;
        _main.PropertyChanged += OnMainChanged;

        _isRowLayout = _main.IsRowLayout;
        ApplyOrientation();
        ReloadItems(scrollToCurrent: false);
    }

    /// <summary>
    /// 释放订阅并停止动画。
    /// </summary>
    public void Detach()
    {
        LayoutUpdated -= OnLayoutUpdated;
        _list.ScrollToCurrentRequested -= ScrollToCurrentAnimated;
        _list.PropertyChanged -= OnListChanged;
        _main.PropertyChanged -= OnMainChanged;
        _animationTimer?.Stop();
    }

    /// <summary>
    /// 切换单行/单列布局(FILM-04)。
    /// </summary>
    /// <param name="isRowLayout">是否为上下布局(胶片条横排)</param>
    public void SetOrientation(bool isRowLayout)
    {
        if (_isRowLayout == isRowLayout)
        {
            return;
        }

        _isRowLayout = isRowLayout;
        ApplyOrientation();
        // 项距随方向变化(96 ↔ 144),旧偏移已无意义;按当前项重新定位,而不是把列表甩回顶部
        ScrollToCurrentAnimated();
        ReportVisibleRange();
    }

    /// <summary>
    /// 按当前照片定位(350ms 三次缓出;仅显式翻页/打开调用,FILM-01)。
    /// 请求可能早于布局到达(启动期/换文件夹时 extent 与 viewport 还是 0),也可能被紧随其后的
    /// 列表重建把偏移清回 0(`ReloadItems` 会重置 ItemsSource)。因此请求记账后由布局回调收敛:
    /// 偏移真正落到目标才销账,否则下一次布局继续补做,最多 <see cref="LocateMaxAttempts"/> 次。
    /// </summary>
    public void ScrollToCurrentAnimated()
    {
        _locatePending = true;
        _locateAttempts = 0;
        TryApplyLocate();
    }

    /// <summary>尝试兑现未完成的定位请求;几何未就绪或动画在途就等下一轮。</summary>
    private void TryApplyLocate()
    {
        if (!_locatePending || _isAnimating)
        {
            return;
        }

        var files = _list.FilteredFiles;
        var index = _main.CurrentFile == null ? -1 : files.IndexOf(_main.CurrentFile);
        if (index < 0)
        {
            _locatePending = false;
            return;
        }

        var pitch = _isRowLayout ? WindowsLayout.CardPitchHorizontal : WindowsLayout.CardPitchVertical;
        var viewport = _isRowLayout ? _scroll.ViewportWidth : _scroll.ViewportHeight;
        var extent = _isRowLayout ? _scroll.ExtentWidth : _scroll.ExtentHeight;
        if (viewport <= 0 || extent <= 0)
        {
            return;
        }

        // 虚拟化列表的 extent 随项实现逐轮增长:extent 还没盖住目标项时算出来的目标一定是被夹短的,
        // 这时候不能销账(否则远端定位会静默停在列表头部),等下一轮布局再算。
        var covered = extent + 1 >= ((index + 1) * pitch);
        var current = _isRowLayout ? _scroll.HorizontalOffset : _scroll.VerticalOffset;
        var target = Math.Clamp(
            (index * pitch) - ((viewport - pitch) / 2),
            0,
            Math.Max(0, extent - viewport));
        if (Math.Abs(current - target) < 1)
        {
            if (covered)
            {
                _locatePending = false;
            }

            return;
        }

        if (_locateAttempts >= LocateMaxAttempts)
        {
            _locatePending = false;
            NativeStartupLog.Write($"胶片条定位未生效: index={index} target={target} current={current} extent={extent} viewport={viewport}");
            return;
        }

        _locateAttempts++;
        StartAnimation(current, target);
    }

    /// <summary>布局每轮结束补做挂起的定位请求(无事可做时开销为一次布尔判断)。</summary>
    private void OnLayoutUpdated(object? sender, object e) => TryApplyLocate();

    /// <summary>应用当前方向的排布与滚动轴。</summary>
    private void ApplyOrientation()
    {
        _stackLayout.Orientation = _isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _scroll.HorizontalScrollMode = _isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.VerticalScrollMode = _isRowLayout ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
        _repeater.VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>重建列表(筛选结果或排序变化)。</summary>
    /// <param name="scrollToCurrent">重建后是否定位当前项</param>
    private void ReloadItems(bool scrollToCurrent)
    {
        StopAnimation();
        _repeater.ItemsSource = null;
        _pool.Clear();
        _reportedFirst = -1;
        _reportedLast = -1;
        _repeater.ItemsSource = _list.FilteredFiles;
        ReportVisibleRange();
        if (scrollToCurrent)
        {
            ScrollToCurrentAnimated();
        }
    }

    /// <summary>取一张卡片:回收池优先,否则新建。</summary>
    private WindowsThumbnailCard RentCard(ImageFile file)
    {
        var card = _pool.Count > 0 ? _pool.Pop() : new WindowsThumbnailCard(_select, _rate);
        card.Bind(file, _main.Settings.ShowRating);
        _list.QueueThumbnailLoad(file, true);
        return card;
    }

    /// <summary>归还卡片。</summary>
    private void ReturnCard(WindowsThumbnailCard card)
    {
        card.Unbind();
        _pool.Push(card);
    }

    /// <summary>按滚动位置上报可见区,驱动缩略图加载优先级与 LRU 保护窗口。</summary>
    private void ReportVisibleRange()
    {
        var files = _list.FilteredFiles;
        if (files.Count == 0)
        {
            return;
        }

        var pitch = _isRowLayout ? WindowsLayout.CardPitchHorizontal : WindowsLayout.CardPitchVertical;
        var offset = _isRowLayout ? _scroll.HorizontalOffset : _scroll.VerticalOffset;
        var viewport = _isRowLayout ? _scroll.ViewportWidth : _scroll.ViewportHeight;

        var first = Math.Max(0, (int)Math.Floor(offset / pitch) - VisibleRangeOverscan);
        var last = Math.Min(files.Count - 1, (int)Math.Ceiling((offset + viewport) / pitch) + VisibleRangeOverscan);
        if (first == _reportedFirst && last == _reportedLast)
        {
            return;
        }

        _reportedFirst = first;
        _reportedLast = last;
        _list.ReportVisibleRange(first, last);
        _list.LoadVisibleThumbnails(files.Skip(first).Take(last - first + 1));
    }

    /// <summary>启动定位动画(三次缓出,latest-wins)。</summary>
    private void StartAnimation(double from, double to)
    {
        if (Math.Abs(to - from) < 1)
        {
            return;
        }

        StopAnimation();
        _animationFrom = from;
        _animationTo = to;
        _animationStartedAt = DateTime.UtcNow;
        _isAnimating = true;

        _animationTimer ??= DispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick -= OnAnimationTick;
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Start();
    }

    /// <summary>停止定位动画。</summary>
    private void StopAnimation()
    {
        _isAnimating = false;
        if (_animationTimer == null)
        {
            return;
        }

        _animationTimer.Stop();
        _animationTimer.Tick -= OnAnimationTick;
    }

    /// <summary>定位动画每帧推进。</summary>
    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = (DateTime.UtcNow - _animationStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / LocateAnimationMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var offset = _animationFrom + ((_animationTo - _animationFrom) * eased);

        if (_isRowLayout)
        {
            _scroll.ChangeView(offset, null, null, true);
        }
        else
        {
            _scroll.ChangeView(null, offset, null, true);
        }

        if (progress < 1)
        {
            return;
        }

        StopAnimation();

        // 动画期间列表可能被重建(ItemsSource 归零会把偏移清回 0),收尾时复核一次
        TryApplyLocate();
    }

    /// <summary>列表属性变化:筛选结果变化时重建。</summary>
    private void OnListChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThumbnailListViewModel.FilteredFiles))
        {
            ReloadItems(scrollToCurrent: false);
        }
    }

    /// <summary>主视图模型变化:布局方向变化时换向。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRowLayout))
        {
            SetOrientation(_main.IsRowLayout);
        }
    }

    /// <summary>
    /// 卡片工厂:虚拟化容器由 ItemsRepeater 负责,这里只做取用/回收与绑定。
    /// </summary>
    private sealed class CardFactory : IElementFactory
    {
        private readonly WindowsFilmstripView _owner;

        /// <summary>
        /// 创建工厂。
        /// </summary>
        /// <param name="owner">所属胶片条</param>
        public CardFactory(WindowsFilmstripView owner) => _owner = owner;

        /// <inheritdoc/>
        public UIElement GetElement(ElementFactoryGetArgs args)
            => _owner.RentCard((ImageFile)args.Data);

        /// <inheritdoc/>
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is WindowsThumbnailCard card)
            {
                _owner.ReturnCard(card);
            }
        }
    }
}
