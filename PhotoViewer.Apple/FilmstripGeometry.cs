namespace PhotoViewer.Apple;

/// <summary>
/// 胶片栏的平台中立几何：单轴列表的卡片尺寸、内边距、跨轴居中、文档长度与可见项范围。
/// 索引顺序就是屏幕顺序；宿主视图负责把结果落到原生布局属性与滚动容器上。
/// </summary>
public static class FilmstripGeometry
{
    /// <summary>卡片外框宽度（缩略图 80 + 四周 3 点占位与 10 点边框口径）。</summary>
    public const double CardWidth = 96;
    /// <summary>卡片外框高度。</summary>
    public const double CardHeight = 144;
    /// <summary>卡片间距（卡片自带 3 点占位，步长即卡片尺寸）。</summary>
    public const double Spacing = 0;
    /// <summary>横向列表左右内边距。</summary>
    public const double HorizontalPadding = 10;
    /// <summary>纵向列表顶部内边距，也是横向列表的顶边起点。</summary>
    public const double TopPadding = 5;
    /// <summary>纵向列表底部内边距，也是横向列表的交叉轴尾距。</summary>
    public const double BottomPadding = 10;

    /// <summary>主轴步长：横向为卡片宽，纵向为卡片高。</summary>
    /// <param name="horizontal">是否横向列表。</param>
    /// <returns>相邻卡片的中心距。</returns>
    public static double Step(bool horizontal) => (horizontal ? CardWidth : CardHeight) + Spacing;

    /// <summary>首项沿主轴的起点：横向在内容宽度内居中（至少保留内边距），纵向固定顶边内边距。</summary>
    /// <param name="horizontal">是否横向列表。</param>
    /// <param name="contentWidth">文档宽度。</param>
    /// <param name="count">项目数。</param>
    /// <returns>第一项的主轴起点。</returns>
    public static double ItemStart(bool horizontal, double contentWidth, int count)
        => horizontal ? Math.Max(HorizontalPadding, (contentWidth - count * Step(true) + Spacing) / 2) : TopPadding;

    /// <summary>文档尺寸：只在主轴延伸，空列表与短列表仍至少覆盖原生裁剪视口。</summary>
    /// <param name="horizontal">是否横向列表。</param>
    /// <param name="viewportWidth">裁剪视口宽度。</param>
    /// <param name="viewportHeight">裁剪视口高度。</param>
    /// <param name="count">项目数。</param>
    /// <returns>文档尺寸。</returns>
    public static (double Width, double Height) ContentSize(bool horizontal, double viewportWidth, double viewportHeight, int count)
    {
        double length = count == 0 ? 0 : count * Step(horizontal) - Spacing
            + (horizontal ? HorizontalPadding * 2 : TopPadding + BottomPadding);
        return horizontal ? (Math.Max(viewportWidth, length), viewportHeight) : (viewportWidth, Math.Max(viewportHeight, length));
    }

    /// <summary>单个卡片在文档中的矩形。</summary>
    /// <param name="horizontal">是否横向列表。</param>
    /// <param name="index">项目索引。</param>
    /// <param name="count">项目数。</param>
    /// <param name="contentWidth">文档宽度（横向项起点与纵向跨轴居中都要用）。</param>
    /// <returns>卡片矩形。</returns>
    public static (double X, double Y, double Width, double Height) ItemFrame(bool horizontal, int index, int count, double contentWidth)
        => horizontal
            ? (ItemStart(true, contentWidth, count) + index * Step(true), TopPadding, CardWidth, CardHeight)
            : (Math.Max(0, (contentWidth - CardWidth) / 2), TopPadding + index * Step(false), CardWidth, CardHeight);

    /// <summary>仅与可见矩形相交的项目范围，保留原生虚拟化。</summary>
    /// <param name="count">项目数。</param>
    /// <param name="itemStart">首项主轴起点。</param>
    /// <param name="step">主轴步长。</param>
    /// <param name="rectStart">可见矩形沿主轴的起点。</param>
    /// <param name="rectLength">可见矩形沿主轴的长度。</param>
    /// <returns>首末索引；列表为空或不相交时 <c>Last</c> 小于 <c>First</c>。</returns>
    public static (int First, int Last) VisibleRange(int count, double itemStart, double step, double rectStart, double rectLength)
    {
        if (count <= 0) return (0, -1);
        double end = rectStart + rectLength;
        int first = Math.Max(0, (int)Math.Floor((rectStart - itemStart) / step));
        int last = Math.Min(count - 1, (int)Math.Floor((end - itemStart) / step));
        return (first, last);
    }

    /// <summary>
    /// 把某一项的中心移到视口中心所需的主轴滚动偏移（FILM-02）：目标 = 卡片起点 + (卡片长度 − 视口长度) / 2，
    /// 限制在 <c>[0, max(0, 文档长度 − 视口长度)]</c>，故首尾允许偏离中心、短列表不制造空白。
    /// </summary>
    /// <param name="itemStart">卡片沿主轴的起点。</param>
    /// <param name="itemLength">卡片沿主轴的长度。</param>
    /// <param name="viewportLength">视口沿主轴的长度。</param>
    /// <param name="documentLength">文档沿主轴的长度。</param>
    /// <returns>合法的滚动偏移。</returns>
    public static double CenterOffset(double itemStart, double itemLength, double viewportLength, double documentLength)
        => Math.Clamp(itemStart + (itemLength - viewportLength) / 2, 0, Math.Max(0, documentLength - viewportLength));
}

/// <summary>胶片栏定位动画的平台中立曲线：350ms 三次缓出。</summary>
public static class FilmstripAnimation
{
    /// <summary>动画时长（秒）。</summary>
    public const double DurationSeconds = .35;

    /// <summary>由起止时刻求进度，超时钳制在 1。</summary>
    /// <param name="startedAt">动画起始的单调时钟秒数。</param>
    /// <param name="now">当前单调时钟秒数。</param>
    /// <returns>0–1 的线性进度。</returns>
    public static double Progress(double startedAt, double now) => Math.Clamp((now - startedAt) / DurationSeconds, 0, 1);

    /// <summary>三次缓出曲线。</summary>
    /// <param name="progress">线性进度。</param>
    /// <returns>缓出后的进度。</returns>
    public static double EaseOutCubic(double progress) => 1 - Math.Pow(1 - progress, 3);

    /// <summary>按线性进度在起点与终点之间缓出插值。</summary>
    /// <param name="start">起点坐标。</param>
    /// <param name="end">终点坐标。</param>
    /// <param name="progress">线性进度。</param>
    /// <returns>当前坐标。</returns>
    public static double Interpolate(double start, double end, double progress) => start + (end - start) * EaseOutCubic(progress);

    /// <summary>位移是否已小于 1 点（不需要播放动画，直接定位）。</summary>
    /// <param name="startX">起点横坐标。</param>
    /// <param name="startY">起点纵坐标。</param>
    /// <param name="endX">终点横坐标。</param>
    /// <param name="endY">终点纵坐标。</param>
    /// <returns>横纵位移都小于 1 点时为 true。</returns>
    public static bool IsNegligible(double startX, double startY, double endX, double endY)
        => Math.Abs(endX - startX) < 1 && Math.Abs(endY - startY) < 1;
}
