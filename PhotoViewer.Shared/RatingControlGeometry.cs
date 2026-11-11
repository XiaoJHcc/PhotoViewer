using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>星级控件的形态：槽位放得下固有长度时展开，否则压成紧凑胶囊（RATING-07）。</summary>
public enum RatingControlForm
{
    /// <summary>展开态：六格星排，沿槽位主轴居中。</summary>
    Expanded,

    /// <summary>紧凑态：五颗叠压星铺满槽位主轴。</summary>
    Compact,
}

/// <summary>星级弹窗相对入口的展开方向。</summary>
public enum RatingPopupDirection
{
    /// <summary>入口上方。</summary>
    Up,

    /// <summary>入口下方。</summary>
    Down,

    /// <summary>入口左侧。</summary>
    Left,

    /// <summary>入口右侧。</summary>
    Right,
}

/// <summary>星级弹窗的入口类型：控件入口与胶片卡片入口的方向表相反。</summary>
public enum RatingPopupAnchor
{
    /// <summary>星级控件（控制栏或手机形态槽位）。</summary>
    Control,

    /// <summary>胶片卡片。</summary>
    Card,
}

/// <summary>
/// 星级控件的平台中立几何（RATING-07）：形态判定、控件在槽位内的矩形、六格星排的槽位与
/// 「主轴坐标 → 值」映射、紧凑星簇偏移、弹窗方向表与落位夹取。各端只负责把结果换算成原生坐标。
/// </summary>
public static class RatingControlGeometry
{
    /// <summary>星排槽位数（0 号清零叉 + 1–5 星）。</summary>
    public const int SlotCount = 6;

    /// <summary>控件交叉轴长度，与玻璃按钮边长一致（44 点）。</summary>
    public static double Thickness => DesignTokens.ShellGlassButtonSize;

    /// <summary>展开态每格沿主轴的步长（36 点）。</summary>
    public static double Slot => DesignTokens.RatingControlSlot;

    /// <summary>展开态星排两端到胶囊端的留白（4 点）。</summary>
    public static double EndInset => DesignTokens.RatingControlEndInset;

    /// <summary>展开态固有主轴长度：六格步长加两端留白（224 点）。</summary>
    public static double ExpandedLength => SlotCount * Slot + 2 * EndInset;

    /// <summary>紧凑态胶囊最短主轴长度（88 点）。</summary>
    public static double CompactMinLength => DesignTokens.RatingCompactMinLength;

    /// <summary>紧凑态相邻叠压星的中心距（9 点）。</summary>
    public static double CompactStep => DesignTokens.RatingCompactStep;

    /// <summary>紧凑态叠压星字号（16 点）。</summary>
    public static double CompactGlyph => DesignTokens.RatingCompactGlyphFontSize;

    /// <summary>紧凑态/卡片长按起手后视为点按、不预览的位移阈值（8 点）。</summary>
    public static double TapSlop => DesignTokens.RatingGestureTapSlop;

    /// <summary>交叉轴越出星排/弹窗该距离即进入取消区（44 点）。</summary>
    public static double CancelSlop => DesignTokens.RatingGestureCancelSlop;

    /// <summary>按槽位主轴长度判定形态：达到固有长度即为展开态，阈值即固有长度本身。</summary>
    /// <param name="availableMain">槽位沿主轴的可用长度。</param>
    /// <returns>展开态或紧凑态。</returns>
    public static RatingControlForm ResolveForm(double availableMain)
        => availableMain >= ExpandedLength - 1e-6 ? RatingControlForm.Expanded : RatingControlForm.Compact;

    /// <summary>
    /// 控件在槽位内的视觉矩形：展开态取固有长度并沿主轴居中，紧凑态铺满槽位主轴
    /// （槽位短于最短长度时仍保持槽位长度，不越出槽位）；交叉轴恒为控件厚度并居中。
    /// </summary>
    /// <param name="slot">宿主给出的槽位矩形。</param>
    /// <param name="horizontal">槽位主轴是否为水平方向。</param>
    /// <returns>控件的视觉矩形。</returns>
    public static LayoutRect ControlFrame(LayoutRect slot, bool horizontal)
    {
        double slotMain = horizontal ? slot.Width : slot.Height;
        double slotCross = horizontal ? slot.Height : slot.Width;
        double main = ResolveForm(slotMain) == RatingControlForm.Expanded ? ExpandedLength : slotMain;
        double mainStart = (slotMain - main) / 2;
        double crossStart = (slotCross - Thickness) / 2;
        return horizontal
            ? new LayoutRect(slot.X + mainStart, slot.Y + crossStart, main, Thickness)
            : new LayoutRect(slot.X + crossStart, slot.Y + mainStart, Thickness, main);
    }

    /// <summary>六格星排的起点：星排整体在行主轴长度内居中。</summary>
    /// <param name="rowLength">行沿主轴的可用长度。</param>
    /// <param name="pitch">每格步长。</param>
    /// <returns>0 号格（主轴上第一格）的起点坐标。</returns>
    public static double RowStart(double rowLength, double pitch) => (rowLength - SlotCount * pitch) / 2;

    /// <summary>
    /// 单格在行局部坐标中的矩形：横排时光标值 0 在最左、5 在最右；竖排时 5 在最上、0 在最下。
    /// 格子在交叉轴上占满整行。
    /// </summary>
    /// <param name="value">星级值（0–5，越界夹取）。</param>
    /// <param name="rowLength">行沿主轴的可用长度。</param>
    /// <param name="cross">行沿交叉轴的长度。</param>
    /// <param name="horizontal">星排主轴是否为水平方向。</param>
    /// <param name="pitch">每格步长。</param>
    /// <returns>格子的行局部矩形。</returns>
    public static LayoutRect SlotRect(int value, double rowLength, double cross, bool horizontal, double pitch)
    {
        int clamped = Math.Clamp(value, 0, SlotCount - 1);
        double start = RowStart(rowLength, pitch);
        return horizontal
            ? new LayoutRect(start + clamped * pitch, 0, pitch, cross)
            : new LayoutRect(0, start + (SlotCount - 1 - clamped) * pitch, cross, pitch);
    }

    /// <summary>
    /// 把行局部的主轴坐标映射回星级值（<see cref="SlotRect"/> 的逆映射）；
    /// 越过星排两端时夹到 0 / 5。
    /// </summary>
    /// <param name="main">行局部的主轴坐标（横排为 x，竖排为 y）。</param>
    /// <param name="rowLength">行沿主轴的可用长度。</param>
    /// <param name="horizontal">星排主轴是否为水平方向。</param>
    /// <param name="pitch">每格步长。</param>
    /// <returns>星级值（0–5）。</returns>
    public static int ValueAt(double main, double rowLength, bool horizontal, double pitch)
    {
        double start = RowStart(rowLength, pitch);
        int index = (int)Math.Floor((main - start) / pitch);
        int clamped = Math.Clamp(index, 0, SlotCount - 1);
        return horizontal ? clamped : SlotCount - 1 - clamped;
    }

    /// <summary>交叉轴坐标是否仍落在取消区内：星排/弹窗远端到近端一带各外扩取消阈值。</summary>
    /// <param name="cross">交叉轴坐标。</param>
    /// <param name="bandStart">取消带近端。</param>
    /// <param name="bandEnd">取消带远端。</param>
    /// <returns>落在带内（含边界）返回 true，越出即取消。</returns>
    public static bool InBand(double cross, double bandStart, double bandEnd)
        => cross >= bandStart - CancelSlop && cross <= bandEnd + CancelSlop;

    /// <summary>
    /// 紧凑星簇中单个字形中心相对胶囊中心的主轴偏移。1–5 为星，0 为冲突警示三角
    /// （仅在冲突时有效）；无冲突时星 k 中心为 (k−3)×步长，有冲突时三角在最前，
    /// 之后空一格再接 1–5 星。横排向右为正（星 1 在左），竖排取反（星 1 在下，三角在最下）。
    /// </summary>
    /// <param name="item">簇内条目：1–5 星，0 为冲突三角。</param>
    /// <param name="conflict">是否为冲突态（决定三角与空格是否参与）。</param>
    /// <param name="horizontal">胶囊主轴是否为水平方向。</param>
    /// <returns>沿主轴的中心偏移；无冲突时的 0 号条目返回 0。</returns>
    public static double CompactOffset(int item, bool conflict, bool horizontal)
    {
        double offset;
        if (item <= 0)
            offset = conflict ? -3 : 0;
        else
            offset = conflict ? item - 2 : item - 3;
        offset *= CompactStep;
        return horizontal ? offset : -offset;
    }

    /// <summary>紧凑星簇沿主轴的内容长度：字形宽加星间跨度（冲突时多出三角与一格空格）。</summary>
    /// <param name="conflict">是否为冲突态。</param>
    /// <returns>簇内容的主轴长度。</returns>
    public static double CompactContentLength(bool conflict) => CompactGlyph + (conflict ? 6 : 4) * CompactStep;

    /// <summary>
    /// 星级弹窗的展开方向表：一律朝主图内侧。控件入口竖屏向上、横屏向刘海对侧、宽形态行布局向上列布局向左；
    /// 卡片入口与控件入口左右相反，宽形态行布局向下、列布局向右。
    /// </summary>
    /// <param name="form">主屏形态。</param>
    /// <param name="rowLayout">宽形态是否为行布局。</param>
    /// <param name="notchOnLeft">手机横屏时刘海是否在左。</param>
    /// <param name="anchor">入口类型。</param>
    /// <returns>弹窗展开方向。</returns>
    public static RatingPopupDirection PopupDirection(
        ScreenForm form, bool rowLayout, bool notchOnLeft, RatingPopupAnchor anchor)
        => form switch
        {
            ScreenForm.PhonePortrait => RatingPopupDirection.Up,
            ScreenForm.PhoneLandscape => (anchor == RatingPopupAnchor.Control) == notchOnLeft
                ? RatingPopupDirection.Left
                : RatingPopupDirection.Right,
            _ => anchor == RatingPopupAnchor.Control
                ? (rowLayout ? RatingPopupDirection.Up : RatingPopupDirection.Left)
                : (rowLayout ? RatingPopupDirection.Down : RatingPopupDirection.Right),
        };

    /// <summary>
    /// 以入口矩形对齐弹窗并留出间距，再整体夹进边界；边界容不下时贴住边界起点。
    /// </summary>
    /// <param name="anchor">入口矩形（与 <paramref name="bounds"/> 同一坐标空间）。</param>
    /// <param name="bounds">可放置边界。</param>
    /// <param name="width">弹窗宽。</param>
    /// <param name="height">弹窗高。</param>
    /// <param name="direction">展开方向。</param>
    /// <param name="gap">入口与弹窗的间距。</param>
    /// <returns>弹窗矩形（左上角坐标与尺寸）。</returns>
    public static LayoutRect PlacePopup(LayoutRect anchor, LayoutRect bounds, double width, double height,
        RatingPopupDirection direction, double gap)
    {
        double x;
        double y;
        switch (direction)
        {
            case RatingPopupDirection.Up:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.Y - gap - height;
                break;
            case RatingPopupDirection.Left:
                x = anchor.X - gap - width;
                y = anchor.Y + anchor.Height / 2 - height / 2;
                break;
            case RatingPopupDirection.Right:
                x = anchor.MaxX + gap;
                y = anchor.Y + anchor.Height / 2 - height / 2;
                break;
            default:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.MaxY + gap;
                break;
        }
        return new LayoutRect(
            Clamp(x, bounds.X, bounds.MaxX - width),
            Clamp(y, bounds.Y, bounds.MaxY - height),
            width,
            height);
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Min(Math.Max(value, min), max);
}
