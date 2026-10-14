using AppKit;
using CoreGraphics;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>单轴胶片条布局；内容长度由项目数决定，跨轴始终只有一行或一列。</summary>
internal sealed class MacFilmstripLayout(Func<int> count) : NSCollectionViewFlowLayout
{
    internal const double CardWidth = 96;
    internal const double CardHeight = 148;
    internal const double Spacing = 5;
    internal const double Padding = 4;
    /// <summary>上下布局使用横向胶片条，左右布局使用纵向胶片条。</summary>
    internal bool Horizontal
    {
        get => ScrollDirection == NSCollectionViewScrollDirection.Horizontal;
        set => ScrollDirection = value ? NSCollectionViewScrollDirection.Horizontal : NSCollectionViewScrollDirection.Vertical;
    }
    private double Step => (Horizontal ? CardWidth : CardHeight) + Spacing;

    /// <summary>内容区只在主轴延伸，空列表与短列表仍至少覆盖原生裁剪视口。</summary>
    public override CGSize CollectionViewContentSize
    {
        get
        {
            var viewport = CollectionView?.EnclosingScrollView?.ContentView.Bounds.Size ?? CGSize.Empty;
            double length = count() == 0 ? 0 : count() * Step - Spacing + Padding * 2;
            return Horizontal ? new CGSize(Math.Max((double)viewport.Width, length), viewport.Height)
                : new CGSize(viewport.Width, Math.Max((double)viewport.Height, length));
        }
    }

    /// <summary>无需流式换行计算；保留 FlowLayout 的滚动方向契约供 AppKit 配置原生滚动轴。</summary>
    public override void PrepareLayout() { }

    /// <summary>为单一项目提供固定方形图片卡片的几何，索引顺序就是屏幕顺序。</summary>
    public override NSCollectionViewLayoutAttributes? GetLayoutAttributesForItem(NSIndexPath indexPath)
    {
        if (indexPath.Section != 0 || indexPath.Item < 0 || indexPath.Item >= count()) return null;
        var attributes = NSCollectionViewLayoutAttributes.CreateForItem(indexPath);
        var size = CollectionViewContentSize;
        attributes.Frame = Horizontal
            ? new CGRect(Padding + indexPath.Item * Step, Math.Max(0, ((double)size.Height - CardHeight) / 2), CardWidth, CardHeight)
            : new CGRect(Math.Max(0, ((double)size.Width - CardWidth) / 2), Padding + indexPath.Item * Step, CardWidth, CardHeight);
        return attributes;
    }

    /// <summary>仅创建与可见矩形相交的布局属性，保留 NSCollectionView 的原生虚拟化。</summary>
    public override NSCollectionViewLayoutAttributes[] GetLayoutAttributesForElements(CGRect rect)
    {
        double start = Horizontal ? (double)rect.X : (double)rect.Y;
        double end = start + (Horizontal ? (double)rect.Width : (double)rect.Height);
        int first = Math.Max(0, (int)Math.Floor((start - Padding) / Step));
        int last = Math.Min(count() - 1, (int)Math.Floor((end - Padding) / Step));
        var attributes = new List<NSCollectionViewLayoutAttributes>();
        for (int index = first; index <= last; index++)
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

/// <summary>胶片条原生滚动容器，横向模式也接受普通鼠标的纵向滚轮。</summary>
internal sealed class MacFilmstripScrollView : NSScrollView
{
    /// <summary>是否只沿横轴滚动。</summary>
    internal bool Horizontal { get; set; }

    /// <summary>触控板保留系统横向/惯性；纵向滚轮在横向胶片条中映射到主轴。</summary>
    public override void ScrollWheel(NSEvent theEvent)
    {
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
}
