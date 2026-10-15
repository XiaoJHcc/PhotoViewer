using AppKit;
using CoreGraphics;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>单轴胶片栏布局；内容长度由项目数决定，跨轴始终只有一行或一列。</summary>
internal sealed class MacFilmstripLayout : NSCollectionViewFlowLayout
{
    private int Count => (int)(CollectionView?.GetNumberOfItems(0) ?? 0);
    internal const double CardWidth = 96;
    internal const double CardHeight = 144;
    internal const double Spacing = 0;
    internal const double HorizontalPadding = 10;
    internal const double TopPadding = 5;
    internal const double BottomPadding = 10;
    private double LeadingPadding => Horizontal ? HorizontalPadding : TopPadding;
    private double TrailingPadding => Horizontal ? HorizontalPadding : BottomPadding;
    /// <summary>上下布局使用横向胶片栏，左右布局使用纵向胶片栏。</summary>
    internal bool Horizontal
    {
        get => ScrollDirection == NSCollectionViewScrollDirection.Horizontal;
        set => ScrollDirection = value ? NSCollectionViewScrollDirection.Horizontal : NSCollectionViewScrollDirection.Vertical;
    }
    private double Step => (Horizontal ? CardWidth : CardHeight) + Spacing;
    private double ItemStart => Horizontal ? Math.Max(HorizontalPadding, ((double)CollectionViewContentSize.Width - Count * Step + Spacing) / 2) : TopPadding;

    /// <summary>内容区只在主轴延伸，空列表与短列表仍至少覆盖原生裁剪视口。</summary>
    public override CGSize CollectionViewContentSize
    {
        get
        {
            var viewport = CollectionView?.EnclosingScrollView?.ContentView.Bounds.Size ?? CGSize.Empty;
            double length = Count == 0 ? 0 : Count * Step - Spacing + LeadingPadding + TrailingPadding;
            return Horizontal ? new CGSize(Math.Max((double)viewport.Width, length), viewport.Height)
                : new CGSize(viewport.Width, Math.Max((double)viewport.Height, length));
        }
    }

    /// <summary>无需流式换行计算；保留 FlowLayout 的滚动方向契约供 AppKit 配置原生滚动轴。</summary>
    public override void PrepareLayout() { }

    /// <summary>为单一项目提供固定方形图片卡片的几何，索引顺序就是屏幕顺序。</summary>
    public override NSCollectionViewLayoutAttributes? GetLayoutAttributesForItem(NSIndexPath indexPath)
    {
        if (indexPath.Section != 0 || indexPath.Item < 0 || indexPath.Item >= Count) return null;
        var attributes = NSCollectionViewLayoutAttributes.CreateForItem(indexPath);
        var size = CollectionViewContentSize;
        attributes.Frame = Horizontal
            ? new CGRect(ItemStart + indexPath.Item * Step, TopPadding, CardWidth, CardHeight)
            : new CGRect(Math.Max(0, ((double)size.Width - CardWidth) / 2), LeadingPadding + indexPath.Item * Step, CardWidth, CardHeight);
        return attributes;
    }

    /// <summary>仅创建与可见矩形相交的布局属性，保留 NSCollectionView 的原生虚拟化。</summary>
    public override NSCollectionViewLayoutAttributes[] GetLayoutAttributesForElements(CGRect rect)
    {
        if (Count <= 0) return [];
        double start = Horizontal ? (double)rect.X : (double)rect.Y;
        double end = start + (Horizontal ? (double)rect.Width : (double)rect.Height);
        int first = Math.Max(0, (int)Math.Floor((start - ItemStart) / Step));
        int last = Math.Min(Count - 1, (int)Math.Floor((end - ItemStart) / Step));
        if (last < first) return [];
        var attributes = new List<NSCollectionViewLayoutAttributes>(last - first + 1);
        for (int index = first; index <= last; index++)
        {
            var path = NSIndexPath.FromItemSection(index, 0);
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
        if (NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion ||
            Math.Abs((double)(target.X - start.X)) < 1 && Math.Abs((double)(target.Y - start.Y)) < 1)
        {
            SetScrollPosition(target);
            return;
        }
        double started = NSProcessInfo.ProcessInfo.SystemUptime;
        _animation = NSTimer.CreateRepeatingTimer(1.0 / 60, _ =>
        {
            double progress = Math.Clamp((NSProcessInfo.ProcessInfo.SystemUptime - started) / .35, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            SetScrollPosition(new CGPoint(start.X + (target.X - start.X) * eased, start.Y + (target.Y - start.Y) * eased));
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

    /// <summary>释放滚动视图时终止计时器，避免已关闭窗口继续收到动画回调。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelAnimation();
        base.Dispose(disposing);
    }
}
