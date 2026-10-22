using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;

namespace PhotoViewer.Mac.Native;

/// <summary>单轴胶片栏布局；内容长度由项目数决定，跨轴始终只有一行或一列。</summary>
internal sealed class MacFilmstripLayout : NSCollectionViewFlowLayout
{
    private int Count => (int)(CollectionView?.GetNumberOfItems(0) ?? 0);

    /// <summary>上下布局使用横向胶片栏，左右布局使用纵向胶片栏。</summary>
    internal bool Horizontal
    {
        get => ScrollDirection == NSCollectionViewScrollDirection.Horizontal;
        set => ScrollDirection = value ? NSCollectionViewScrollDirection.Horizontal : NSCollectionViewScrollDirection.Vertical;
    }

    /// <summary>内容区只在主轴延伸，空列表与短列表仍至少覆盖原生裁剪视口。</summary>
    public override CGSize CollectionViewContentSize
    {
        get
        {
            var viewport = CollectionView?.EnclosingScrollView?.ContentView.Bounds.Size ?? CGSize.Empty;
            var content = FilmstripGeometry.ContentSize(Horizontal, (double)viewport.Width, (double)viewport.Height, Count);
            return new CGSize(content.Width, content.Height);
        }
    }

    /// <summary>无需流式换行计算；保留 FlowLayout 的滚动方向契约供 AppKit 配置原生滚动轴。</summary>
    public override void PrepareLayout() { }

    /// <summary>为单一项目提供固定方形图片卡片的几何，索引顺序就是屏幕顺序。</summary>
    public override NSCollectionViewLayoutAttributes? GetLayoutAttributesForItem(NSIndexPath indexPath)
    {
        if (indexPath.Section != 0 || indexPath.Item < 0 || indexPath.Item >= Count) return null;
        var attributes = NSCollectionViewLayoutAttributes.CreateForItem(indexPath);
        var card = FilmstripGeometry.ItemFrame(Horizontal, (int)indexPath.Item, Count, (double)CollectionViewContentSize.Width);
        attributes.Frame = new CGRect(card.X, card.Y, card.Width, card.Height);
        return attributes;
    }

    /// <summary>仅创建与可见矩形相交的布局属性，保留 NSCollectionView 的原生虚拟化。</summary>
    public override NSCollectionViewLayoutAttributes[] GetLayoutAttributesForElements(CGRect rect)
    {
        if (Count <= 0) return [];
        double contentWidth = (double)CollectionViewContentSize.Width;
        var range = FilmstripGeometry.VisibleRange(Count, FilmstripGeometry.ItemStart(Horizontal, contentWidth, Count),
            FilmstripGeometry.Step(Horizontal), Horizontal ? (double)rect.X : (double)rect.Y, Horizontal ? (double)rect.Width : (double)rect.Height);
        if (range.Last < range.First) return [];
        var attributes = new List<NSCollectionViewLayoutAttributes>(range.Last - range.First + 1);
        for (int index = range.First; index <= range.Last; index++)
        {
            using var path = NSIndexPath.FromItemSection(index, 0);
            var item = GetLayoutAttributesForItem(path);
            if (item != null) attributes.Add(item);
        }
        return attributes.ToArray();
    }

    /// <summary>视口尺寸变化时重新计算跨轴居中，滚动位置改变不重复布局。</summary>
    public override bool ShouldInvalidateLayout(CGRect newBounds)
        => CollectionView != null && newBounds.Size != CollectionView.Bounds.Size;
}

/// <summary>胶片栏原生滚动容器，横向模式也接受普通鼠标的纵向滚轮。</summary>
internal sealed class MacFilmstripScrollView : NSScrollView
{
    private NSTimer? _animation;
    /// <summary>真实动画是否仍在执行，供交互验收区分自动滚动和系统滚轮缓动。</summary>
    internal bool IsAnimating => _animation != null;

    /// <summary>是否只沿横轴滚动。</summary>
    internal bool Horizontal { get; set; }
    /// <summary>用户滚动开始时撤销窗口队列中的自动定位请求。</summary>
    internal Action? UserScrollStarted { get; set; }

    /// <summary>取消居中动画并保留当前帧的位置，供用户操作与布局变化接管。</summary>
    internal void CancelAnimation()
    {
        _animation?.Invalidate();
        _animation?.Dispose();
        _animation = null;
    }

    /// <summary>从当前可见位置沿主轴执行 350ms 三次缓出，减少动态效果时直接定位。</summary>
    internal void AnimateTo(CGPoint target)
    {
        CancelAnimation();
        var start = ContentView.Bounds.Location;
        target = Horizontal ? new CGPoint(target.X, start.Y) : new CGPoint(start.X, target.Y);
        if (NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion
            || FilmstripAnimation.IsNegligible((double)start.X, (double)start.Y, (double)target.X, (double)target.Y))
        {
            SetScrollPosition(target);
            return;
        }
        double started = NSProcessInfo.ProcessInfo.SystemUptime;
        _animation = NSTimer.CreateRepeatingTimer(1.0 / 60, _ =>
        {
            double progress = FilmstripAnimation.Progress(started, NSProcessInfo.ProcessInfo.SystemUptime);
            SetScrollPosition(new CGPoint(FilmstripAnimation.Interpolate((double)start.X, (double)target.X, progress),
                FilmstripAnimation.Interpolate((double)start.Y, (double)target.Y, progress)));
            if (progress >= 1) CancelAnimation();
        });
        NSRunLoop.Main.AddTimer(_animation, NSRunLoopMode.Common);
    }

    /// <summary>更新实际裁剪位置及系统滚动条，可见范围观察者随每帧同步加载。</summary>
    private void SetScrollPosition(CGPoint position)
    {
        ContentView.ScrollToPoint(position);
        ReflectScrolledClipView(ContentView);
    }

    /// <summary>原生滚动条开始跟踪前取消自动滚动，保留用户拖动的控制权。</summary>
    public override void MouseDown(NSEvent theEvent)
    {
        UserScrollStarted?.Invoke();
        CancelAnimation();
        base.MouseDown(theEvent);
    }

    /// <summary>触控板保留系统横向/惯性；纵向滚轮在横向胶片栏中映射到主轴。</summary>
    public override void ScrollWheel(NSEvent theEvent)
    {
        UserScrollStarted?.Invoke();
        CancelAnimation();
        if (!Horizontal || Math.Abs((double)theEvent.ScrollingDeltaX) >= Math.Abs((double)theEvent.ScrollingDeltaY))
        {
            base.ScrollWheel(theEvent);
            return;
        }
        double delta = (double)theEvent.ScrollingDeltaY * (theEvent.HasPreciseScrollingDeltas ? 1 : 16);
        double maximum = Math.Max(0, (double)(DocumentView?.Frame.Width ?? 0) - (double)ContentView.Bounds.Width);
        double position = Math.Clamp((double)ContentView.Bounds.X - delta, 0, maximum);
        ContentView.ScrollToPoint(new CGPoint(position, 0));
        ReflectScrolledClipView(ContentView);
    }

    /// <summary>
    /// FILM-05：接入鼠标时系统会把滚动条切回占位的 legacy 样式并挤窄裁剪区，卡片跨轴偏离居中；
    /// 每次重新排布前都锁回悬浮样式，裁剪区始终铺满文件栏。
    /// </summary>
    public override void Tile()
    {
        if (ScrollerStyle != NSScrollerStyle.Overlay) ScrollerStyle = NSScrollerStyle.Overlay;
        base.Tile();
    }

    /// <summary>释放滚动视图时终止计时器，避免已关闭窗口继续收到动画回调。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelAnimation();
        base.Dispose(disposing);
    }
}
