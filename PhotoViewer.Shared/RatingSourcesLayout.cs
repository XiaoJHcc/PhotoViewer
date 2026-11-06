using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>来源浮窗的入口类型：卡片入口按可用空间选择上下方向，控制栏入口按排布方向避开主体（RATING-04）。</summary>
public enum RatingSourcesAnchorKind
{
    /// <summary>胶片栏/相似面板卡片。</summary>
    Card,
    /// <summary>控制栏评分组。</summary>
    Control,
}

/// <summary>来源浮窗相对入口的展开方向。</summary>
public enum RatingSourcesDirection
{
    /// <summary>入口下方。</summary>
    Down,
    /// <summary>入口上方。</summary>
    Up,
    /// <summary>入口左侧（竖排控制栏）。</summary>
    Left,
}

/// <summary>
/// 单个来源条目的布局结果（坐标为条目左上角原点的逻辑点；条目宽恒为
/// <see cref="RatingSourcesLayout.EntryWidth"/>）。文本行高由各端原生控件决定，
/// <see cref="MetaY"/> 是信息行带顶边，宿主再按自己的行高在带内垂直居中。
/// </summary>
/// <param name="TwoLines">文件名是否独占首行。</param>
/// <param name="Height">条目总高（含上下内边距与星排）。</param>
/// <param name="NameWidth">文件名可用宽度。</param>
/// <param name="MetaY">信息行带顶边。</param>
/// <param name="BadgeX">来源标签左缘。</param>
/// <param name="BadgeWidth">来源标签宽（含两侧内边距）。</param>
/// <param name="StatusX">状态文本左缘。</param>
/// <param name="StatusWidth">状态文本宽。</param>
/// <param name="StarsY">星排顶边。</param>
public readonly record struct RatingSourcesEntryLayout(
    bool TwoLines,
    double Height,
    double NameWidth,
    double MetaY,
    double BadgeX,
    double BadgeWidth,
    double StatusX,
    double StatusWidth,
    double StarsY);

/// <summary>
/// RATING-04 来源浮窗的平台中立几何：宽度由六格星排与条目内边距决定，条目按实测文本宽
/// 选择单行/双行并把来源标签与状态靠右，浮窗整体高度为上下内边距加条目与间距。
/// 展开方向与落地位置同样在这里计算；各端只负责把结果换算成原生坐标与控件。
/// </summary>
public static class RatingSourcesLayout
{
    /// <summary>条目内槽位数（0 号清零叉 + 1–5 星）。</summary>
    public const int SlotCount = 6;
    /// <summary>三块信息共用的行带高；纯排版值，不对应设计令牌。</summary>
    public const double InfoHeight = 16;
    /// <summary>槽位间距与配置表口径一致（评分组为 0）。</summary>
    public const double SlotSpacing = 0;

    /// <summary>星槽边长取控制栏评分组指标（与评分按钮同尺寸）。</summary>
    public static readonly double SlotSize = ControlBarLayout.GetMetrics(ControlBarGroupKind.Rating).Width;
    /// <summary>条目宽 = 六格星排 + 两侧条目内边距（196 点）。</summary>
    public static readonly double EntryWidth = SlotCount * SlotSize + DesignTokens.PopupRatingSourcesEntryPadding * 2;
    /// <summary>条目内可用信息宽度（180 点）。</summary>
    public static readonly double EntryContentWidth = EntryWidth - DesignTokens.PopupRatingSourcesEntryPadding * 2;
    /// <summary>浮窗内容宽 = 条目宽 + 两侧浮窗内边距（204 点，描边另计）。</summary>
    public static readonly double ContentWidth = EntryWidth + DesignTokens.PopupPadding * 2;

    /// <summary>单个条目内星排总宽（六格星槽）。</summary>
    public static double StarsWidth => SlotCount * SlotSize + (SlotCount - 1) * SlotSpacing;

    /// <summary>
    /// 按三块信息的实测宽度排出条目：状态先夹进可用宽度，文件名放不下时改为双行独占首行。
    /// </summary>
    /// <param name="nameWidth">文件名单行实测宽度。</param>
    /// <param name="badgeWidth">来源标签总宽（已含两侧 <see cref="DesignTokens.PopupRatingSourcesBadgePaddingX"/>）。</param>
    /// <param name="statusWidth">状态文本实测宽度。</param>
    /// <returns>条目布局结果。</returns>
    public static RatingSourcesEntryLayout MeasureEntry(double nameWidth, double badgeWidth, double statusWidth)
    {
        double padding = DesignTokens.PopupRatingSourcesEntryPadding;
        double labelGap = DesignTokens.PopupRatingSourcesLabelGap;
        double available = EntryContentWidth;
        statusWidth = Math.Max(0, statusWidth);
        double metaWidth = badgeWidth + labelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - labelGap);
            metaWidth = badgeWidth + labelGap + statusWidth;
        }
        bool twoLines = nameWidth + labelGap + metaWidth > available + .5;
        double nameFit = twoLines ? available : Math.Max(0, available - labelGap - metaWidth);
        double metaBandY = twoLines
            ? padding + InfoHeight + DesignTokens.PopupRatingSourcesMetaLineGap
            : padding;
        double statusX = EntryWidth - padding - statusWidth;
        double badgeX = statusX - labelGap - badgeWidth;
        double starsY = metaBandY + InfoHeight + labelGap;
        double height = starsY + SlotSize + padding;
        return new RatingSourcesEntryLayout(twoLines, height, nameFit, metaBandY, badgeX, badgeWidth,
            statusX, statusWidth, starsY);
    }

    /// <summary>浮窗内容高度（不含描边）：上下内边距 + 各条目高 + 条目间距。</summary>
    /// <param name="entries">按顺序的条目布局。</param>
    /// <returns>内容高度；空列表为纯上下内边距。</returns>
    public static double ContentHeight(IReadOnlyList<RatingSourcesEntryLayout> entries)
    {
        double total = DesignTokens.PopupPadding * 2;
        foreach (var entry in entries) total += entry.Height;
        return total + Math.Max(0, entries.Count - 1) * DesignTokens.PopupRatingSourcesEntrySpacing;
    }

    /// <summary>
    /// 按入口类型与上下可用空间决定展开方向：控制栏入口横排向上、竖排向左；
    /// 卡片入口默认向下，下方放不下且上方够放时向上。
    /// </summary>
    /// <param name="kind">入口类型。</param>
    /// <param name="controlHorizontal">控制栏入口是否横排。</param>
    /// <param name="spaceAbove">入口上方的可用空间。</param>
    /// <param name="spaceBelow">入口下方的可用空间。</param>
    /// <param name="contentHeight">浮窗高度（含描边与内边距的实际展示高度）。</param>
    /// <returns>展开方向。</returns>
    public static RatingSourcesDirection ResolveDirection(
        RatingSourcesAnchorKind kind, bool controlHorizontal, double spaceAbove, double spaceBelow, double contentHeight)
    {
        if (kind == RatingSourcesAnchorKind.Control)
            return controlHorizontal ? RatingSourcesDirection.Up : RatingSourcesDirection.Left;
        double required = contentHeight + DesignTokens.PopupRatingSourcesArrowMargin;
        return spaceBelow < required && spaceAbove >= required ? RatingSourcesDirection.Up : RatingSourcesDirection.Down;
    }

    /// <summary>
    /// 以入口矩形中心对齐浮窗并留出箭头余量，再整体夹进边界；边界容不下时贴住边界起点。
    /// </summary>
    /// <param name="anchor">入口矩形（与 <paramref name="bounds"/> 同一坐标空间）。</param>
    /// <param name="bounds">可放置边界。</param>
    /// <param name="width">浮窗宽。</param>
    /// <param name="height">浮窗高。</param>
    /// <param name="direction">展开方向。</param>
    /// <param name="gap">入口与浮窗之间的箭头余量；null 取 <see cref="DesignTokens.PopupRatingSourcesArrowMargin"/>。</param>
    /// <returns>浮窗左上角坐标。</returns>
    public static (double X, double Y) Place(LayoutRect anchor, LayoutRect bounds, double width, double height,
        RatingSourcesDirection direction, double? gap = null)
    {
        double margin = gap ?? DesignTokens.PopupRatingSourcesArrowMargin;
        double x, y;
        switch (direction)
        {
            case RatingSourcesDirection.Up:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.Y - margin - height;
                break;
            case RatingSourcesDirection.Left:
                x = anchor.X - margin - width;
                y = anchor.Y + anchor.Height / 2 - height / 2;
                break;
            default:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.Y + anchor.Height + margin;
                break;
        }
        return (Clamp(x, bounds.X, bounds.MaxX - width), Clamp(y, bounds.Y, bounds.MaxY - height));
    }

    /// <summary>
    /// 中部省略：保留首尾，避免丢掉扩展名与关键区分部分；二分查找能放下的最大保留字数。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="maxWidth">可用宽度。</param>
    /// <param name="measure">按文本测宽的函数（同一字体/字号）。</param>
    /// <returns>放得下时原样返回，否则返回带省略号的保留首尾文本。</returns>
    public static string EllipsizeMiddle(string text, double maxWidth, Func<string, double> measure)
    {
        if (string.IsNullOrEmpty(text) || measure(text) <= maxWidth) return text;
        string Candidate(int kept)
        {
            int head = (kept + 1) / 2;
            int tail = kept - head;
            return head <= 0
                ? "…"
                : string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
        }
        int low = 0;
        int high = text.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (measure(Candidate(middle)) <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return Candidate(low);
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Min(Math.Max(value, min), max);
}
