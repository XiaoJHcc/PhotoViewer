using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using Windows.UI.ViewManagement;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生胶片条:单行/单列虚拟化列表 + 卡片池 + 可见区上报 + 350ms 三次缓出定位
/// (契约 FILM-01/02/04/05)。业务索引即几何顺序,项距由 <see cref="WindowsLayout"/> 固定。
/// </summary>
internal sealed class WindowsFilmstripView : Grid
{
    /// <summary>定位动画时长(契约 FILM-02)。</summary>
    private const double LocateAnimationMilliseconds = DesignTokens.MotionFilmScrollDuration;

    /// <summary>可见区外额外加载的卡片数。</summary>
    private const int VisibleRangeOverscan = 2;

    /// <summary>一次定位请求最多启动的动画次数(列表在动画期间重建会把偏移清回 0,需要补做)。</summary>
    private const int LocateMaxAttempts = 4;

    private readonly MainViewModel _main;
    private readonly ThumbnailListViewModel _list;
    private readonly Action<ImageFile> _select;
    private readonly Func<ImageFile, int, Task> _rate;
    private readonly ScrollViewer _scroll;
    private readonly Stack<WindowsThumbnailCard> _pool = new();
    private readonly UISettings _uiSettings = new();

    /// <summary>当前列表容器;换数据源时整体换新(见 <see cref="ReloadItems"/>)。</summary>
    private ItemsRepeater? _repeater;

    /// <summary>当前容器的排布;每个容器各配一份,虚拟化算法把已实现区间存在它自己身上。</summary>
    private StackLayout _stackLayout = new();

    /// <summary>换容器后要恢复的主轴偏移(FILM-06:筛选/评分刷新只更新列表,不抢用户位置)。</summary>
    private double? _pendingOffsetRestore;

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
        };
        _scroll.ViewChanged += (_, _) => ReportVisibleRange();

        // FILM-03 用户接管:点选卡片、拖动滚动条、滚轮或触摸滚动都立刻撤销在途定位与排队请求。
        // 卡片与滚动条各自的 PointerPressed 都会冒泡到这里,因此用 handledEventsToo 抓全。
        _scroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnScrollPointerPressed), true);
        _scroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnScrollPointerMoved), true);
        _scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnScrollPointerWheelChanged), true);
        _scroll.ManipulationStarted += (_, _) => CancelLocate();

        Children.Add(_scroll);

        // 定位请求可能先于布局到达(启动期/换文件夹),布局每轮结束补做一次
        LayoutUpdated += OnLayoutUpdated;
        _list.ScrollToCurrentRequested += ScrollToCurrentAnimated;
        _list.PropertyChanged += OnListChanged;
        _main.PropertyChanged += OnMainChanged;

        _isRowLayout = _main.IsRowLayout;
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
    /// 系统启用"减少动态效果"时直接落到终点,触发条件不变(FILM-03)。
    /// </summary>
    public void ScrollToCurrentAnimated()
    {
        _locatePending = true;
        _locateAttempts = 0;
        // 显式定位优先于换容器后的偏移恢复(否则定位到位后又被拉回旧位置)。
        _pendingOffsetRestore = null;
        TryApplyLocate();
    }

    /// <summary>
    /// 验收专用:"减少动态效果"判定覆盖(<c>true</c>/<c>false</c>);生产路径始终为 null,
    /// 由 <see cref="ReduceMotionEnabled"/> 读系统设置。仅定向验收使用。
    /// </summary>
    internal static bool? ReduceMotionOverrideForAcceptance;

    /// <summary>当前是否有定位动画在播放(供定向验收断言接管效果)。</summary>
    internal bool IsLocateAnimating => _isAnimating;

    /// <summary>当前系统是否要求减少动态效果(Windows 的"动画效果"开关)。</summary>
    internal bool ReduceMotionEnabled => ReduceMotionOverrideForAcceptance ?? !_uiSettings.AnimationsEnabled;

    /// <summary>列表主轴(横向布局取 X、纵向布局取 Y)的当前滚动偏移,供定向验收断言接管与终点。</summary>
    internal double MainAxisOffset => _isRowLayout ? _scroll.HorizontalOffset : _scroll.VerticalOffset;

    /// <summary>
    /// 滚动视口(剪辑可见区的元素),供定向验收区分"用户看得见的卡片"与
    /// "虚拟化列表保留的已回收/已滚出视口的卡片"(FILM-05)。
    /// </summary>
    internal FrameworkElement VisibleViewport => _scroll;

    /// <summary>
    /// 用户接管列表(FILM-03):撤销正在播放的动画与排队未执行的定位请求,位置停在用户手动到达处。
    /// 点选卡片、拖动滚动条、滚轮与触摸滚动都走这里;下一次显式请求按最新几何重新计算。
    /// 已在途的一帧 <c>ChangeView</c> 无法取消,所以撤销后用同位置的无动画请求把它压掉。
    /// </summary>
    internal void CancelLocate()
    {
        bool interrupt = _isAnimating || _locatePending;
        _locatePending = false;
        // 用户接管:排队的偏移恢复也一并撤销,位置停在用户手动到达处。
        _pendingOffsetRestore = null;
        StopAnimation();
        if (interrupt)
        {
            _scroll.ChangeView(_isRowLayout ? _scroll.HorizontalOffset : null,
                _isRowLayout ? null : _scroll.VerticalOffset, null, true);
        }
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
        // ItemsRepeater 的 Margin 会把整块内容内缩:第一张卡片从 pad 而不是 0 开始,
        // 居中目标和"extent 是否盖住目标项"都必须带上这段前导内边距。
        var pad = _isRowLayout ? WindowsLayout.FilmstripPadLeft : WindowsLayout.FilmstripPadTop;
        var viewport = _isRowLayout ? _scroll.ViewportWidth : _scroll.ViewportHeight;
        var extent = _isRowLayout ? _scroll.ExtentWidth : _scroll.ExtentHeight;
        if (viewport <= 0 || extent <= 0)
        {
            return;
        }

        // 虚拟化列表的 extent 随项实现逐轮增长:extent 还没盖住目标项时算出来的目标一定是被夹短的,
        // 这时候不能销账(否则远端定位会静默停在列表头部),等下一轮布局再算。
        var covered = extent + 1 >= pad + ((index + 1) * pitch);
        var current = _isRowLayout ? _scroll.HorizontalOffset : _scroll.VerticalOffset;
        var target = Math.Clamp(
            pad + (index * pitch) - ((viewport - pitch) / 2),
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
        if (ReduceMotionEnabled)
        {
            // 减少动态效果:同一触发条件,直接到终点;到位后由下一轮布局复核销账。
            _scroll.ChangeView(_isRowLayout ? target : null, _isRowLayout ? null : target, null, true);
            if (covered)
            {
                _locatePending = false;
            }

            return;
        }

        StartAnimation(current, target);
    }

    /// <summary>布局每轮结束补做挂起的定位请求与偏移恢复(无事可做时开销只是两次布尔判断)。</summary>
    private void OnLayoutUpdated(object? sender, object e)
    {
        TryRestoreOffset();
        TryApplyLocate();
    }

    /// <summary>应用当前方向的排布与滚动轴。</summary>
    private void ApplyOrientation()
    {
        _stackLayout.Orientation = _isRowLayout ? Orientation.Horizontal : Orientation.Vertical;
        _scroll.HorizontalScrollMode = _isRowLayout ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.VerticalScrollMode = _isRowLayout ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = _isRowLayout ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    /// <summary>
    /// 新建列表容器。换数据源必须换新容器:ItemsRepeater 替换 ItemsSource 只重置自己的实现索引,
    /// 已实现的卡片不会随之撤下,旧卡片会一直留在容器里(见 <see cref="ReloadItems"/>)。
    /// </summary>
    private ItemsRepeater CreateRepeater()
    {
        // 每个容器各配一份 StackLayout:虚拟化算法把已实现区间存在 Layout 自己身上,
        // 新旧容器共用一份会把上一个容器的实现状态当成当前状态。
        _stackLayout = new StackLayout { Spacing = 0 };
        ApplyOrientation();
        return new ItemsRepeater
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
    }

    /// <summary>
    /// 重建列表(筛选结果或排序变化)。在途动画立即停止;排队的定位请求保留 ——
    /// 外部打开/翻页的请求常常先于这次重建到达,清掉会让"打开非首张"静默停在列表头部;
    /// 用户接管与栏位隐藏才真正撤销请求(FILM-03)。
    /// 数据源整体换一份时容器也整体换新:ItemsRepeater 换 ItemsSource 不回收已实现的元素,
    /// 旧卡片会积在视觉树里(换文件夹越多积越多,卡片还继续持有缩略图位图与文件属性订阅)。
    /// 旧容器连同它的卡片一起脱离视觉树,卡片逐个解绑;栏位偏移在重建后恢复,刷新不抢用户位置(FILM-06)。
    /// </summary>
    /// <param name="scrollToCurrent">重建后是否定位当前项</param>
    private void ReloadItems(bool scrollToCurrent)
    {
        StopAnimation();
        var files = _list.FilteredFiles;
        var previousOffset = MainAxisOffset;
        if (_repeater is { } previous)
        {
            _scroll.Content = null;
            UnbindRealizedCards(previous);
        }

        _pool.Clear();
        _reportedFirst = -1;
        _reportedLast = -1;

        var repeater = CreateRepeater();
        repeater.ItemsSource = files;
        _scroll.Content = repeater;
        _repeater = repeater;
        // 换容器会把偏移夹回 0;列表刷新只更新内容,不抢用户位置(FILM-06)。
        // 显式定位请求优先,有定位在排队时不恢复旧偏移。
        _pendingOffsetRestore = !_locatePending && files.Count > 0 && previousOffset > 0 ? previousOffset : null;
        TryRestoreOffset();
        ReportVisibleRange();
        if (scrollToCurrent)
        {
            ScrollToCurrentAnimated();
        }
    }

    /// <summary>把换容器前的主轴偏移恢复回去;几何未就绪就等下一轮布局。</summary>
    private void TryRestoreOffset()
    {
        if (_pendingOffsetRestore is not { } target || _locatePending)
        {
            return;
        }

        var extent = _isRowLayout ? _scroll.ExtentWidth : _scroll.ExtentHeight;
        var viewport = _isRowLayout ? _scroll.ViewportWidth : _scroll.ViewportHeight;
        if (extent <= 0 || viewport <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(target, 0, Math.Max(0, extent - viewport));
        if (Math.Abs(MainAxisOffset - clamped) < 1)
        {
            _pendingOffsetRestore = null;
            return;
        }

        _scroll.ChangeView(_isRowLayout ? clamped : null, _isRowLayout ? null : clamped, null, true);
    }

    /// <summary>解绑被丢弃容器上挂着的卡片,避免残留缩略图位图与文件属性订阅。</summary>
    private static void UnbindRealizedCards(DependencyObject container)
    {
        if (container is WindowsThumbnailCard card)
        {
            card.Unbind();
            return;
        }

        int children = VisualTreeHelper.GetChildrenCount(container);
        for (int index = 0; index < children; index++)
        {
            UnbindRealizedCards(VisualTreeHelper.GetChild(container, index));
        }
    }

    /// <summary>取一张卡片:回收池优先,否则新建。</summary>
    private WindowsThumbnailCard RentCard(ImageFile file)
    {
        WindowsThumbnailCard? card = null;
        while (_pool.Count > 0)
        {
            var pooled = _pool.Pop();
            // 池里的卡片必须已脱离视觉树;仍挂着父容器的那张是没摘干净的残留,丢弃而不是硬塞回去。
            if (pooled.Parent == null)
            {
                card = pooled;
                break;
            }
        }

        card ??= new WindowsThumbnailCard(_select, _rate);
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

    /// <summary>滚轮滚动列表:用户接管,撤销在途定位(FILM-03)。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">滚轮参数。</param>
    private void OnScrollPointerWheelChanged(object sender, PointerRoutedEventArgs e) => CancelLocate();

    /// <summary>指针按下(点选卡片或拖动滚动条):用户接管列表(FILM-03)。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针参数。</param>
    private void OnScrollPointerPressed(object sender, PointerRoutedEventArgs e) => CancelLocate();

    /// <summary>按住指针拖动(拖动滚动条或触摸滚动):用户接管列表(FILM-03)。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针参数。</param>
    private void OnScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed)
        {
            CancelLocate();
        }
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
        _animationTimer.Interval = TimeSpan.FromMilliseconds(DesignTokens.MotionFilmScrollTick);
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
