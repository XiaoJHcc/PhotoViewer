using CoreAnimation;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 单轴胶片栏布局（UICollectionViewLayout）：几何全部取自共享 <see cref="FilmstripGeometry"/>——
/// 96×144 卡片、10/5/10 内边距、横向首项居中、文档主轴长度与可见项范围；索引顺序就是屏幕顺序。
/// 宿主只决定主轴方向与视口，本类不重复任何尺寸常量。
/// </summary>
internal sealed class iOSFilmstripLayout : UICollectionViewLayout
{
    /// <summary>行布局为横向单行，列布局为纵向单列。</summary>
    internal bool Horizontal { get; set; } = true;

    /// <summary>
    /// 项目数来源（业务筛选结果）。必须由宿主提供：UICollectionView 在 reloadData 之后、
    /// 下一次布局之前会返回过期的条目数（实测为 0），用它算文档长度会把文档压成视口大小，
    /// 随后的偏移约束就把刚发起的显式定位清零。
    /// </summary>
    internal Func<int>? CountProvider { get; set; }

    /// <summary>当前项目数（数据源与布局同源，保证一致）。</summary>
    internal int Count => CountProvider?.Invoke() ?? (int)(CollectionView?.NumberOfItemsInSection(0) ?? 0);
    /// <summary>滚动视口尺寸（iOS 上集合视图本身就是滚动视图）。</summary>
    private CGSize Viewport => CollectionView?.Bounds.Size ?? CGSize.Empty;

    /// <summary>文档尺寸：主轴按项目数延伸，空列表与短列表至少覆盖视口，避免多余空白与无法滚动。</summary>
    public override CGSize CollectionViewContentSize
    {
        get
        {
            var content = FilmstripGeometry.ContentSize(Horizontal, (double)Viewport.Width, (double)Viewport.Height, Count);
            return new CGSize(content.Width, content.Height);
        }
    }

    /// <summary>无流式换行计算；几何完全由共享函数给出。</summary>
    public override void PrepareLayout() { }

    /// <summary>
    /// 单个卡片的固定几何，索引顺序即屏幕顺序。
    /// UIKit 只在数据源合法范围内查询；越界（数据源刚替换、旧索引尚未失效的防御路径）返回零尺寸属性，
    /// 既不给 UIKit 空引用，也不让越界索引落进任何卡片位置。
    /// </summary>
    /// <param name="indexPath">项目索引路径。</param>
    /// <returns>布局属性。</returns>
    public override UICollectionViewLayoutAttributes LayoutAttributesForItem(NSIndexPath indexPath)
    {
        var attributes = UICollectionViewLayoutAttributes.CreateForCell(indexPath);
        if (indexPath.Section != 0 || indexPath.Item < 0 || indexPath.Item >= Count) return attributes;
        var card = FilmstripGeometry.ItemFrame(Horizontal, (int)indexPath.Item, Count, (double)CollectionViewContentSize.Width);
        attributes.Frame = new CGRect(card.X, card.Y, card.Width, card.Height);
        return attributes;
    }

    /// <summary>只创建与可见矩形相交的属性，保留 UICollectionView 的原生复用。</summary>
    /// <param name="rect">可见矩形（文档坐标）</param>
    /// <returns>相交项目的布局属性。</returns>
    public override UICollectionViewLayoutAttributes[] LayoutAttributesForElementsInRect(CGRect rect)
    {
        if (Count <= 0) return [];
        double contentWidth = (double)CollectionViewContentSize.Width;
        var range = FilmstripGeometry.VisibleRange(Count, FilmstripGeometry.ItemStart(Horizontal, contentWidth, Count),
            FilmstripGeometry.Step(Horizontal), Horizontal ? (double)rect.X : (double)rect.Y, Horizontal ? (double)rect.Width : (double)rect.Height);
        if (range.Last < range.First) return [];
        var attributes = new List<UICollectionViewLayoutAttributes>(range.Last - range.First + 1);
        for (int index = range.First; index <= range.Last; index++)
        {
            using var path = NSIndexPath.FromItemSection(index, 0);
            attributes.Add(LayoutAttributesForItem(path));
        }
        return attributes.ToArray();
    }

    /// <summary>视口尺寸变化时重算跨轴居中；纯滚动不重复布局。</summary>
    /// <param name="newBounds">新的边界。</param>
    /// <returns>是否需要重新布局。</returns>
    public override bool ShouldInvalidateLayoutForBoundsChange(CGRect newBounds)
        => CollectionView != null && newBounds.Size != CollectionView.Bounds.Size;
}

/// <summary>
/// 胶片栏（[FILM-01–05]）：UICollectionView + 共享几何布局、可中断的 350ms 三次缓出定位（CADisplayLink 驱动）、
/// 可见范围上报共享缩略图调度器。点选只改当前照片不居中；上一张/下一张、打开文件、方向变化与重新显示走显式定位。
/// </summary>
internal sealed class iOSFilmstripView : UIView
{
    private readonly MainViewModel _main;
    private readonly iOSFilmstripLayout _layout = new();
    private readonly UICollectionView _collection;
    private readonly Source _source;
    /// <summary>显示链接回调目标必须由本类强引用：CADisplayLink 不 retain 自己的 target，被回收后动画静默停止。</summary>
    private readonly DisplayLinkTarget _animationTarget;
    private CADisplayLink? _animation;
    private double _animationStartedAt;
    private CGPoint _animationFrom;
    private CGPoint _animationTo;
    private int _scrollRequestVersion;
    private bool _axisInitialized;
    private bool _centerPending;

    /// <summary>星级点击回调（写回交给宿主统一走共享评分与错误提示）。</summary>
    internal event Action<ImageFile, int>? RateRequested;

    /// <summary>真实集合视图（验收读取可见卡片与偏移）。</summary>
    internal UICollectionView Collection => _collection;
    /// <summary>当前主轴是否为横向单行。</summary>
    internal bool Horizontal => _layout.Horizontal;
    /// <summary>定位动画是否仍在播放（验收区分自动定位与用户滚动）。</summary>
    internal bool IsAnimating => _animation != null;
    /// <summary>系统减少动态效果；测试缝允许注入固定值（验收核对直接到终点路径）。</summary>
    internal Func<bool> ReduceMotionProvider { get; set; } = () => UIAccessibility.IsReduceMotionEnabled;
    /// <summary>当前滚动偏移（主轴分量由调用方按方向取用）。</summary>
    internal CGPoint ContentOffset => _collection.ContentOffset;
    /// <summary>文档尺寸（帧检查与边界断言用）。</summary>
    internal CGSize DocumentSize => _layout.CollectionViewContentSize;

    /// <summary>构建集合视图、卡片复用注册与滚动/拖拽接管。</summary>
    /// <param name="main">共享主 VM（筛选结果与缩略图调度都在共享层）。</param>
    public iOSFilmstripView(MainViewModel main) : base(CGRect.Empty)
    {
        _main = main;
        _layout.CountProvider = () => main.FileVM.ThumbnailList.FilteredFiles.Count;
        _animationTarget = new DisplayLinkTarget(StepAnimation);
        BackgroundColor = UIColor.FromWhiteAlpha(.07f, 1);
        _collection = new UICollectionView(CGRect.Empty, _layout)
        {
            BackgroundColor = UIColor.FromWhiteAlpha(.07f, 1),
            AllowsSelection = false,
            ShowsHorizontalScrollIndicator = true,
            ShowsVerticalScrollIndicator = false,
            AlwaysBounceHorizontal = false,
            AlwaysBounceVertical = false,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
        };
        _collection.RegisterClassForCell(typeof(iOSThumbnailCard), Source.CellId);
        _source = new Source(this);
        _collection.DataSource = _source;
        _collection.Delegate = new ScrollObserver(this);
        AddSubview(_collection);
    }

    /// <summary>设置主轴方向（行布局横向单行、列布局纵向单列）并让几何立即生效。</summary>
    /// <param name="horizontal">是否横向单行。</param>
    internal void SetAxis(bool horizontal)
    {
        if (_axisInitialized && _layout.Horizontal == horizontal) return;
        _axisInitialized = true;
        _layout.Horizontal = horizontal;
        _collection.ShowsHorizontalScrollIndicator = horizontal;
        _collection.ShowsVerticalScrollIndicator = !horizontal;
        _layout.InvalidateLayout();
        SetNeedsLayout();
    }

    /// <summary>业务筛选结果原子替换后重载列表（不主动居中，[FILM-01]）。</summary>
    internal void ReloadData()
    {
        _collection.ReloadData();
        _layout.InvalidateLayout();
        SetNeedsLayout();
    }

    /// <summary>取消尚未执行的定位请求与正在播放的动画，保留当前可见位置（[FILM-03]）。</summary>
    internal void CancelScroll()
    {
        _scrollRequestVersion++;
        CancelAnimation();
    }

    /// <summary>
    /// 当前照片变化时的取消：只撤销"此刻之前"登记的请求与在途动画，不波及此后新登记的显式请求。
    /// **必须在切图的同步通知里调用**（内部会把取消动作投递到主队列）：上一张/下一张在切图返回后立刻发起定位
    /// （[FILM-01/02]），若等投递到主队列才捕获版本号，就会把紧随其后的显式定位一并作废，定位看起来像"没反应"。
    /// </summary>
    internal void CancelScrollUpToNow()
    {
        int version = _scrollRequestVersion;
        UiDispatcher.Post(() =>
        {
            if (_scrollRequestVersion == version) CancelScroll();
        });
    }

    /// <summary>
    /// 显式定位当前项：沿主轴把卡片中心移到视口中心，用共享 350ms 三次缓出（[FILM-02]）。
    /// 视口尚未成形（尺寸 &lt; 1）时登记待办，等首次有效布局再执行 —— 打开文件可能早于主屏布局完成。
    /// </summary>
    internal void ScrollToCurrent()
    {
        int request = ++_scrollRequestVersion;
        UiDispatcher.Post(() =>
        {
            if (request != _scrollRequestVersion) return;
            if (_collection.Bounds.Width < 1 || _collection.Bounds.Height < 1)
            {
                _centerPending = true;
                return;
            }
            CenterOnCurrent();
        });
    }

    /// <summary>可见卡片交共享缩略图调度器按 P1 加载，并上报可见范围（预取/淘汰保护）。</summary>
    internal void ReportVisibleRange()
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        var indexes = _collection.IndexPathsForVisibleItems.Select(path => (int)path.Item).Order().ToArray();
        var visible = indexes.Where(index => index >= 0 && index < files.Count).Select(index => files[index]).ToArray();
        if (visible.Length > 0) _main.FileVM.ThumbnailList.LoadVisibleThumbnails(visible);
        if (indexes.Length > 0) _main.FileVM.ThumbnailList.ReportVisibleRange(indexes[0], indexes[^1]);
    }

    /// <summary>取某一项的卡片视图（未创建或超出可见区时为 null）。</summary>
    /// <param name="index">业务索引。</param>
    /// <returns>已创建的卡片；没有则返回 null。</returns>
    internal iOSThumbnailCard? CardAt(int index)
    {
        using var path = NSIndexPath.FromItemSection(index, 0);
        return _collection.CellForItem(path) as iOSThumbnailCard;
    }

    /// <summary>取某一项的布局矩形（文档坐标）。</summary>
    /// <param name="index">业务索引。</param>
    /// <returns>卡片矩形；越界返回 null。</returns>
    internal CGRect? FrameAt(int index)
    {
        if (index < 0 || index >= _main.FileVM.ThumbnailList.FilteredFiles.Count) return null;
        using var path = NSIndexPath.FromItemSection(index, 0);
        var frame = _layout.LayoutAttributesForItem(path).Frame;
        return frame.Width > 0 && frame.Height > 0 ? frame : null;
    }

    /// <summary>用户触摸/拖动接管列表（[FILM-03]）：撤销定位请求与在途动画。</summary>
    internal void UserTookOver() => CancelScroll();

    /// <summary>集合视图填满胶片栏分区，并在尺寸变化后重算文档、约束偏移与上报可见范围。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _collection.Frame = Bounds;
        _layout.InvalidateLayout();
        _collection.LayoutIfNeeded();
        ClampOffset();
        // 首次有效视口前登记的定位请求在这里补执行（打开文件可能早于主屏布局完成）。
        if (_centerPending && _collection.Bounds.Width >= 1 && _collection.Bounds.Height >= 1)
        {
            _centerPending = false;
            CenterOnCurrent();
        }
        ReportVisibleRange();
    }

    /// <summary>释放显示链接与集合视图。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelAnimation();
        base.Dispose(disposing);
    }

    /// <summary>把某一项居中（不含动画决策）：目标偏移由共享 <see cref="FilmstripGeometry.CenterOffset"/> 给出。</summary>
    private void CenterOnCurrent()
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        int index = _main.CurrentFile == null ? -1 : files.IndexOf(_main.CurrentFile);
        if (index < 0) return;
        if (FrameAt(index) is not { } frame) return;
        var document = _layout.CollectionViewContentSize;
        var viewport = _collection.Bounds.Size;
        bool horizontal = _layout.Horizontal;
        double main = FilmstripGeometry.CenterOffset(
            horizontal ? (double)frame.X : (double)frame.Y,
            horizontal ? (double)frame.Width : (double)frame.Height,
            horizontal ? (double)viewport.Width : (double)viewport.Height,
            horizontal ? (double)document.Width : (double)document.Height);
        var start = _collection.ContentOffset;
        AnimateTo(horizontal ? new CGPoint(main, start.Y) : new CGPoint(start.X, main));
    }

    /// <summary>从当前显示位置沿主轴播 350ms 三次缓出；减少动态效果或位移不足 1 点时直接定位。</summary>
    /// <param name="target">目标偏移。</param>
    private void AnimateTo(CGPoint target)
    {
        CancelAnimation();
        var start = _collection.ContentOffset;
        target = _layout.Horizontal ? new CGPoint(target.X, start.Y) : new CGPoint(start.X, target.Y);
        if (ReduceMotionProvider() || FilmstripAnimation.IsNegligible((double)start.X, (double)start.Y, (double)target.X, (double)target.Y))
        {
            SetOffset(target);
            return;
        }
        _animationFrom = start;
        _animationTo = target;
        _animationStartedAt = NSProcessInfo.ProcessInfo.SystemUptime;
        _animation = CADisplayLink.Create(_animationTarget, new ObjCRuntime.Selector("handleDisplayLink:"));
        _animation.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    /// <summary>每帧按共享缓出曲线推进偏移。</summary>
    private void StepAnimation()
    {
        double progress = FilmstripAnimation.Progress(_animationStartedAt, NSProcessInfo.ProcessInfo.SystemUptime);
        SetOffset(new CGPoint(
            FilmstripAnimation.Interpolate((double)_animationFrom.X, (double)_animationTo.X, progress),
            FilmstripAnimation.Interpolate((double)_animationFrom.Y, (double)_animationTo.Y, progress)));
        if (progress >= 1) CancelAnimation();
    }

    /// <summary>终止动画并保留当前帧位置。</summary>
    private void CancelAnimation()
    {
        var animation = _animation;
        _animation = null;
        if (animation == null) return;
        animation.Invalidate();
        animation.Dispose();
    }

    /// <summary>写入滚动偏移（不触发系统缓动）。</summary>
    /// <param name="offset">目标偏移。</param>
    private void SetOffset(CGPoint offset)
    {
        _collection.SetContentOffset(offset, false);
    }

    /// <summary>把偏移约束到合法范围（列表变短或视口变化时只做边界限制）。</summary>
    private void ClampOffset()
    {
        var document = _layout.CollectionViewContentSize;
        var viewport = _collection.Bounds.Size;
        var current = _collection.ContentOffset;
        var clamped = new CGPoint(
            Math.Clamp((double)current.X, 0, Math.Max(0, (double)document.Width - (double)viewport.Width)),
            Math.Clamp((double)current.Y, 0, Math.Max(0, (double)document.Height - (double)viewport.Height)));
        if (clamped != current) SetOffset(clamped);
    }

    /// <summary>显示链接回调目标（CADisplayLink 需要 ObjC 选择器入口，且由宿主强引用保活）。</summary>
    private sealed class DisplayLinkTarget : NSObject
    {
        private readonly Action _tick;

        /// <summary>保存每帧回调。</summary>
        /// <param name="tick">每帧动作。</param>
        public DisplayLinkTarget(Action tick) => _tick = tick;

        /// <summary>CADisplayLink 每帧入口。</summary>
        /// <param name="link">显示链接。</param>
        [Export("handleDisplayLink:")]
        public void Tick(CADisplayLink link) => _tick();
    }

    /// <summary>数据源：按需建卡、安排缩略图加载（复用由 UICollectionView 原生负责）。</summary>
    private sealed class Source : UICollectionViewDataSource
    {
        /// <summary>卡片复用标识。</summary>
        internal const string CellId = "PhotoCard";
        private readonly iOSFilmstripView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">胶片栏视图。</param>
        public Source(iOSFilmstripView owner) => _owner = owner;

        /// <summary>胶片栏始终是单个分区。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <returns>分区数。</returns>
        public override nint NumberOfSections(UICollectionView collectionView) => 1;

        /// <summary>筛选后的项目数。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <param name="section">分区索引。</param>
        /// <returns>项目数。</returns>
        public override nint GetItemsCount(UICollectionView collectionView, nint section) => _owner._main.FileVM.ThumbnailList.FilteredFiles.Count;

        /// <summary>复用卡片并按业务身份绑定（含缩略图 P1 排队）。</summary>
        /// <param name="collectionView">集合视图。</param>
        /// <param name="indexPath">项目索引路径。</param>
        /// <returns>已绑定的卡片。</returns>
        public override UICollectionViewCell GetCell(UICollectionView collectionView, NSIndexPath indexPath)
        {
            var card = (iOSThumbnailCard)collectionView.DequeueReusableCell(CellId, indexPath);
            var file = _owner._main.FileVM.ThumbnailList.FilteredFiles[(int)indexPath.Item];
            card.Bind(file, _owner._main.Settings.ShowRating,
                selected => _owner.OnCardSelected(selected),
                (selected, rating) => _owner.RateRequested?.Invoke(selected, rating));
            _owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(file, true);
            return card;
        }
    }

    /// <summary>滚动委托：上报可见范围；用户触摸/拖动即接管列表（[FILM-03]）。</summary>
    private sealed class ScrollObserver : UICollectionViewDelegate
    {
        private readonly iOSFilmstripView _owner;

        /// <summary>绑定宿主。</summary>
        /// <param name="owner">胶片栏视图。</param>
        public ScrollObserver(iOSFilmstripView owner) => _owner = owner;

        /// <summary>滚动（拖动、惯性、程序化定位）后上报可见范围。</summary>
        /// <param name="scrollView">集合视图。</param>
        public override void Scrolled(UIScrollView scrollView) => _owner.ReportVisibleRange();

        /// <summary>用户按下手指准备拖动：撤销自动定位与在途动画（[FILM-03]）。</summary>
        /// <param name="scrollView">集合视图。</param>
        public override void DraggingStarted(UIScrollView scrollView) => _owner.UserTookOver();

        /// <summary>拖动结束后若进入惯性减速，继续保持接管（动画不回跳）。</summary>
        /// <param name="scrollView">集合视图。</param>
        public override void DecelerationStarted(UIScrollView scrollView) => _owner.UserTookOver();
    }

    /// <summary>卡片点选：走共享选择命令（只改当前照片，不主动居中，[FILM-01]）。</summary>
    /// <param name="file">被点选的照片。</param>
    private void OnCardSelected(ImageFile file)
    {
        CancelScroll();
        _main.FileVM.ThumbnailList.SelectImageCommand(file);
    }
}
