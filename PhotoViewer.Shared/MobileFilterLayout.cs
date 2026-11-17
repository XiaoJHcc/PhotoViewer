using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>
/// 宽形态筛选控件的落位结果（FILTER-07，移动端宽形态）：两段合体胶囊、筛选/更多两段、相似聚类开关与控件主轴长度。
/// </summary>
/// <param name="Control">合体胶囊矩形（文件栏内容区坐标）。</param>
/// <param name="Filter">筛选段（相对胶囊左上角）。</param>
/// <param name="More">更多段（相对胶囊左上角）。</param>
/// <param name="Toggle">相似聚类开关矩形（文件栏内容区坐标；标准版为零矩形）。</param>
/// <param name="MainLength">筛选控件自身的主轴长度（含外边距）。</param>
public readonly record struct MobileFilterFrames(
    LayoutRect Control,
    LayoutRect Filter,
    LayoutRect More,
    LayoutRect Toggle,
    double MainLength);

/// <summary>
/// 宽形态筛选控件的纯几何（FILTER-07，移动端宽形态）：筛选/更多两段合体宽 96（2 × 48），与大档卡片同宽，列布局按卡片外框的内容末端对齐，
/// 行布局把合体与相似聚类开关当成一组按自然宽居中；列布局开关 96 宽落在合体下方隔 8 点。
/// 全部以点为单位，只做算术，不依赖平台 UI 框架。
/// </summary>
public static class MobileFilterLayout
{
    /// <summary>控件高。</summary>
    public static double Segment => DesignTokens.ShellGlassButtonSize;

    /// <summary>两段合体宽：与大档胶片卡片外框同宽。</summary>
    public static double GroupWidth => FilmCardGeometry.Width(FilmCardTier.Large);

    /// <summary>单段宽：筛选与更多等分合体。</summary>
    public static double SegmentWidth => GroupWidth / 2;

    /// <summary>控件与相似聚类开关之间的间距。</summary>
    public static double Gap => DesignTokens.ShellGlassGroupGap;

    /// <summary>行布局的外边距（同时也是控件到内容区边缘的最小距离）。</summary>
    public static double Margin => MainScreenLayout.FilterMargin;

    /// <summary>控件自身的主轴长度：行布局为高加两侧外边距，列布局为前导让位、合体、开关与尾部外边距之和。</summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="leadingInset">列布局的前导让位（行布局忽略）。</param>
    /// <param name="includeToggle">是否显示相似聚类开关（标准版为 false）。</param>
    /// <returns>主轴长度。</returns>
    public static double MainLength(bool row, double leadingInset, bool includeToggle)
        => row
            ? Segment + Margin * 2
            : leadingInset + Segment + (includeToggle ? Gap + Segment : 0) + Margin;

    /// <summary>
    /// 计算合体胶囊、两段与相似聚类开关的矩形：行布局整组（含开关）自然宽居中，
    /// 列布局的合体与开关共用卡片外框的内容末端，开关在合体下方隔 <see cref="Gap"/>。
    /// </summary>
    /// <param name="row">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度（列布局含实际遮挡后的占用范围）。</param>
    /// <param name="leadingInset">列布局的前导让位。</param>
    /// <param name="toggleWidth">相似聚类开关宽度（行布局为自然宽，列布局为卡片列宽）；0 表示没有开关。</param>
    /// <returns>各矩形与主轴长度。</returns>
    public static MobileFilterFrames Compute(bool row, double contentWidth, double leadingInset, double toggleWidth,
        double? contentTrailing = null)
    {
        bool includeToggle = toggleWidth > 0;
        if (row)
        {
            double total = GroupWidth + (includeToggle ? Gap + toggleWidth : 0);
            double start = Math.Max(Margin, (contentWidth - total) / 2);
            var control = new LayoutRect(start, Margin, GroupWidth, Segment);
            var toggle = includeToggle
                ? new LayoutRect(control.MaxX + Gap, Margin, toggleWidth, Segment)
                : default;
            return new MobileFilterFrames(control, SegmentRect(0), SegmentRect(1), toggle, Segment + Margin * 2);
        }

        double trailing = contentTrailing ?? contentWidth - Margin;
        var column = FilmSideGeometry.AlignTrailing(trailing, leadingInset, GroupWidth, Segment);
        var columnToggle = includeToggle
            ? FilmSideGeometry.AlignTrailing(trailing, column.MaxY + Gap, toggleWidth, Segment)
            : default;
        return new MobileFilterFrames(column, SegmentRect(0), SegmentRect(1), columnToggle,
            leadingInset + Segment + (includeToggle ? Gap + Segment : 0) + Margin);
    }

    /// <summary>第几段的局部矩形：两段沿主轴首尾相接，高占满控件。</summary>
    /// <param name="index">段序（0 = 筛选，1 = 更多）。</param>
    /// <returns>相对胶囊左上角的矩形。</returns>
    private static LayoutRect SegmentRect(int index) => new(index * SegmentWidth, 0, SegmentWidth, Segment);
}
