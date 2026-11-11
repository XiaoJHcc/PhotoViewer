using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>
/// 星级弹窗中单个条目的排版结果；全部矩形相对条目左上角。
/// 单条目（非冲突）只用文件名/时间与星排，来源条目（冲突）只用文件名/时间之外的信息与星排。
/// </summary>
/// <param name="Width">条目宽（星排步长决定）。</param>
/// <param name="Height">条目总高（含上下内边距）。</param>
/// <param name="TwoLines">文件名是否独占首行（或竖排条目是否显示时间行）。</param>
/// <param name="Name">文件名矩形；宽度已让出同行右侧的时间或来源信息。</param>
/// <param name="Time">拍摄时间矩形；不显示时为全零。</param>
/// <param name="Badge">来源标签矩形；单条目为全零。</param>
/// <param name="Status">来源状态文本矩形；单条目为全零。</param>
/// <param name="Stars">星排矩形。</param>
/// <param name="StarsHorizontal">星排主轴是否为水平方向（竖排时 5 在上、0 在下）。</param>
public readonly record struct RatingPopupEntryLayout(
    double Width,
    double Height,
    bool TwoLines,
    LayoutRect Name,
    LayoutRect Time,
    LayoutRect Badge,
    LayoutRect Status,
    LayoutRect Stars,
    bool StarsHorizontal);

/// <summary>
/// 星级弹窗的平台中立排版（RATING-07）：星排步长按可用空间收窄、单条目横排/竖排、
/// 冲突来源条目沿 RATING-04 口径排文件名与信息行，以及面板尺寸、条目起点与分隔线位置。
/// 全部矩形相对条目（或面板）左上角，各端只负责落成原生视图。
/// </summary>
public static class RatingPopupLayout
{
    /// <summary>面板四周内边距（4 点）。</summary>
    public static double Padding => DesignTokens.PopupPadding;

    /// <summary>条目两侧内边距（8 点）。</summary>
    public static double EntryPadding => DesignTokens.PopupRatingSourcesEntryPadding;

    /// <summary>条目之间的间距（8 点，分隔线居中其间）。</summary>
    public static double EntrySpacing => DesignTokens.PopupRatingSourcesEntrySpacing;

    /// <summary>信息行与星排的间距（6 点）。</summary>
    public static double LabelGap => DesignTokens.PopupRatingSourcesLabelGap;

    /// <summary>长文件名换行后的信息行间距（4 点）。</summary>
    public static double MetaLineGap => DesignTokens.PopupRatingSourcesMetaLineGap;

    /// <summary>条目分隔线高度（1 点）。</summary>
    public static double DividerHeight => DesignTokens.PopupRatingSourcesDividerHeight;

    /// <summary>文件名/时间/来源信息共用的行带高（16 点）。</summary>
    public static double InfoHeight => RatingSourcesLayout.InfoHeight;

    /// <summary>弹窗星排每格边长上限（44 点）。</summary>
    public static double Slot => DesignTokens.RatingPopupSlot;

    /// <summary>弹窗星排每格边长下限（28 点）。</summary>
    public static double MinSlot => DesignTokens.RatingPopupMinSlot;

    /// <summary>竖排弹窗宽度（120 点）。</summary>
    public static double ColumnWidth => DesignTokens.RatingPopupColumnWidth;

    /// <summary>按可用空间求星排步长：六格均分后向下取整并夹在最短与标准步长之间。</summary>
    /// <param name="availableMain">弹窗沿星排主轴的可用长度。</param>
    /// <param name="chrome">星排之外占用的长度（面板与条目内边距、信息行等）。</param>
    /// <returns>星排步长。</returns>
    public static double Pitch(double availableMain, double chrome)
        => Math.Clamp(Math.Floor((availableMain - chrome) / RatingControlGeometry.SlotCount), MinSlot, Slot);

    /// <summary>横排弹窗的星排外固定占用：面板与条目两侧内边距。</summary>
    public static double HorizontalChrome => 2 * (Padding + EntryPadding);

    /// <summary>竖排弹窗的星排外交叉占用：内边距、文件名带与可选的时间行。</summary>
    /// <param name="hasTime">是否显示时间行。</param>
    /// <returns>星排之外占用的交叉轴长度。</returns>
    public static double ColumnChrome(bool hasTime)
        => 2 * Padding + 2 * EntryPadding + InfoHeight + (hasTime ? MetaLineGap + InfoHeight : 0) + LabelGap;

    /// <summary>条目宽：六格星排加两侧条目内边距。</summary>
    /// <param name="pitch">星排步长。</param>
    /// <returns>条目宽。</returns>
    public static double EntryWidth(double pitch)
        => RatingControlGeometry.SlotCount * pitch + 2 * EntryPadding;

    /// <summary>
    /// 非冲突单条目的横排版式：文件名与拍摄时间同行（放得下才显示时间），下方横排星排。
    /// </summary>
    /// <param name="nameWidth">文件名实测宽度。</param>
    /// <param name="timeWidth">拍摄时间实测宽度；0 表示没有时间。</param>
    /// <param name="pitch">星排步长。</param>
    /// <returns>条目排版结果。</returns>
    public static RatingPopupEntryLayout SingleRow(double nameWidth, double timeWidth, double pitch)
    {
        double content = RatingControlGeometry.SlotCount * pitch;
        bool showTime = timeWidth > 0 && nameWidth + LabelGap + timeWidth <= content + .5;
        var name = new LayoutRect(
            EntryPadding,
            EntryPadding,
            showTime ? content - LabelGap - timeWidth : content,
            InfoHeight);
        var time = showTime
            ? new LayoutRect(EntryPadding + content - timeWidth, EntryPadding, timeWidth, InfoHeight)
            : default;
        var stars = new LayoutRect(EntryPadding, EntryPadding + InfoHeight + LabelGap, content, pitch);
        return new RatingPopupEntryLayout(
            EntryWidth(pitch),
            stars.MaxY + EntryPadding,
            false,
            name,
            time,
            default,
            default,
            stars,
            true);
    }

    /// <summary>
    /// 非冲突单条目的竖排版式：文件名一行，放得下时时间独占一行，下方竖排星列（5 在上、0 在下）居中。
    /// </summary>
    /// <param name="nameWidth">文件名实测宽度。</param>
    /// <param name="timeWidth">拍摄时间实测宽度；0 表示没有时间。</param>
    /// <param name="pitch">星排步长。</param>
    /// <returns>条目排版结果。</returns>
    public static RatingPopupEntryLayout SingleColumn(double nameWidth, double timeWidth, double pitch)
    {
        double width = ColumnWidth - 2 * Padding;
        double content = width - 2 * EntryPadding;
        var name = new LayoutRect(EntryPadding, EntryPadding, content, InfoHeight);
        bool showTime = timeWidth > 0 && timeWidth <= content + .5;
        var time = showTime
            ? new LayoutRect(EntryPadding, name.MaxY + MetaLineGap, content, InfoHeight)
            : default;
        var stars = new LayoutRect(
            (width - pitch) / 2,
            (showTime ? time.MaxY : name.MaxY) + LabelGap,
            pitch,
            RatingControlGeometry.SlotCount * pitch);
        return new RatingPopupEntryLayout(
            width,
            stars.MaxY + EntryPadding,
            showTime,
            name,
            time,
            default,
            default,
            stars,
            false);
    }

    /// <summary>
    /// 冲突来源条目的排版：状态先夹进六格星排宽度，文件名放不下时独占首行；
    /// 来源标签与状态靠右，星排横排。
    /// </summary>
    /// <param name="nameWidth">文件名实测宽度。</param>
    /// <param name="badgeWidth">来源标签总宽（已含两侧内边距）。</param>
    /// <param name="statusWidth">状态文本实测宽度。</param>
    /// <param name="pitch">星排步长。</param>
    /// <returns>条目排版结果。</returns>
    public static RatingPopupEntryLayout Source(double nameWidth, double badgeWidth, double statusWidth, double pitch)
    {
        double available = RatingControlGeometry.SlotCount * pitch;
        double width = EntryWidth(pitch);
        statusWidth = Math.Max(0, statusWidth);
        double metaWidth = badgeWidth + LabelGap + statusWidth;
        if (metaWidth > available)
        {
            statusWidth = Math.Max(0, available - badgeWidth - LabelGap);
            metaWidth = badgeWidth + LabelGap + statusWidth;
        }
        bool twoLines = nameWidth + LabelGap + metaWidth > available + .5;
        double nameFit = twoLines ? available : Math.Max(0, available - LabelGap - metaWidth);
        double metaY = twoLines ? EntryPadding + InfoHeight + MetaLineGap : EntryPadding;
        double statusX = width - EntryPadding - statusWidth;
        double badgeX = statusX - LabelGap - badgeWidth;
        var name = new LayoutRect(EntryPadding, EntryPadding, nameFit, InfoHeight);
        var badge = new LayoutRect(badgeX, metaY, badgeWidth, InfoHeight);
        var status = new LayoutRect(statusX, metaY, statusWidth, InfoHeight);
        var stars = new LayoutRect(EntryPadding, metaY + InfoHeight + LabelGap, available, pitch);
        return new RatingPopupEntryLayout(
            width,
            stars.MaxY + EntryPadding,
            twoLines,
            name,
            default,
            badge,
            status,
            stars,
            true);
    }

    /// <summary>面板尺寸：宽取最宽条目加两侧内边距，高取上下内边距加条目与间距。</summary>
    /// <param name="entries">按顺序的条目排版。</param>
    /// <returns>面板宽高；空列表为纯内边距。</returns>
    public static (double Width, double Height) PanelSize(IReadOnlyList<RatingPopupEntryLayout> entries)
    {
        double width = 0;
        double height = 2 * Padding;
        foreach (var entry in entries)
        {
            width = Math.Max(width, entry.Width);
            height += entry.Height;
        }
        height += Math.Max(0, entries.Count - 1) * EntrySpacing;
        return (width + 2 * Padding, height);
    }

    /// <summary>条目在面板内的顶边：上内边距加之前所有条目与间距。</summary>
    /// <param name="entries">按顺序的条目排版。</param>
    /// <param name="index">条目序号（越界时夹到有效范围）。</param>
    /// <returns>条目顶边坐标。</returns>
    public static double EntryTop(IReadOnlyList<RatingPopupEntryLayout> entries, int index)
    {
        int limit = Math.Clamp(index, 0, entries.Count);
        double top = Padding;
        for (int i = 0; i < limit; i++) top += entries[i].Height + EntrySpacing;
        return top;
    }

    /// <summary>第 index 条与下一条之间分隔线的纵坐标（居中于条目间距）。</summary>
    /// <param name="entries">按顺序的条目排版。</param>
    /// <param name="index">分隔线上方的条目序号（越界时夹到有效范围）。</param>
    /// <returns>分隔线顶边坐标；空列表为面板内边距。</returns>
    public static double DividerY(IReadOnlyList<RatingPopupEntryLayout> entries, int index)
    {
        if (entries.Count == 0) return Padding;
        int clamped = Math.Clamp(index, 0, entries.Count - 1);
        return EntryTop(entries, clamped) + entries[clamped].Height + (EntrySpacing - DividerHeight) / 2;
    }
}
