using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>宽形态筛选控件的形态（FILTER-07）：三段合体胶囊，或放不下时的单胶囊。</summary>
public enum FilterControlMode
{
    /// <summary>三合体：排序 / 方向 / 星级三段原生弹出按钮同处一个交互式玻璃胶囊内。</summary>
    Segmented,

    /// <summary>单胶囊：一个系统玻璃按钮弹出同一套分组菜单。</summary>
    Capsule,
}

/// <summary>
/// 宽形态筛选控件的落位结果：形态、整块在文件栏内容区内的矩形、三段相对整块的局部矩形
/// （单胶囊时为空矩形）、相似聚类开关矩形与筛选控件自身的主轴长度。
/// </summary>
/// <param name="Mode">控件形态。</param>
/// <param name="Control">控件矩形（文件栏内容区坐标）。</param>
/// <param name="Sort">排序段（相对控件左上角；单胶囊时为零矩形）。</param>
/// <param name="Order">升降序段（相对控件左上角；单胶囊时为零矩形）。</param>
/// <param name="Rating">星级筛选段（相对控件左上角；单胶囊时为零矩形）。</param>
/// <param name="Toggle">相似聚类开关矩形（文件栏内容区坐标；不含开关时为零矩形）。</param>
/// <param name="MainLength">筛选控件自身的主轴长度（含外边距）。</param>
public readonly record struct WideFilterFrames(
    FilterControlMode Mode,
    LayoutRect Control,
    LayoutRect Sort,
    LayoutRect Order,
    LayoutRect Rating,
    LayoutRect Toggle,
    double MainLength);

/// <summary>
/// 宽形态筛选控件的平台中立几何（FILTER-07）：行布局整组在文件栏内容区内居中，三段放不下时改单胶囊；
/// 列布局把控件固定为胶片卡片列宽，三段放得下就均分余量、放不下则单胶囊，相似聚类开关在下方同宽。
/// </summary>
public static class WideFilterLayout
{
    /// <summary>控件高度（玻璃按钮边长）。</summary>
    public static double Height => DesignTokens.ShellGlassButtonSize;

    /// <summary>控件与相似聚类开关之间的间距。</summary>
    public static double Gap => DesignTokens.ShellGlassGroupGap;

    /// <summary>行布局的外边距（同时也是控件到内容区边缘的最小距离）。</summary>
    public static double Margin => MainScreenLayout.FilterMargin;

    /// <summary>
    /// 计算筛选控件与相似聚类开关的矩形。
    /// </summary>
    /// <param name="row">是否行布局（文件栏在上下）。</param>
    /// <param name="contentWidth">文件栏内容区宽度。</param>
    /// <param name="cardWidth">列布局的胶片卡片列宽（大档 96 / 小档 64）。</param>
    /// <param name="leadingInset">列布局的前导让位（标题栏让位或安全区顶边）。</param>
    /// <param name="sortWidth">排序段自然宽度。</param>
    /// <param name="orderWidth">升降序段自然宽度。</param>
    /// <param name="ratingWidth">星级筛选段自然宽度。</param>
    /// <param name="capsuleWidth">单胶囊形态的宽度（行布局由调用方给出）。</param>
    /// <param name="includeToggle">是否显示相似聚类开关（标准版为 false）。</param>
    /// <param name="toggleWidth">相似聚类开关宽度。</param>
    /// <returns>形态、各矩形与筛选控件主轴长度。</returns>
    public static WideFilterFrames Compute(bool row, double contentWidth, double cardWidth, double leadingInset,
        double sortWidth, double orderWidth, double ratingWidth, double capsuleWidth, bool includeToggle,
        double toggleWidth)
    {
        double segmented = sortWidth + orderWidth + ratingWidth;
        double toggleRun = includeToggle ? Gap + toggleWidth : 0;
        if (row)
        {
            bool fits = segmented + toggleRun <= contentWidth - Margin * 2;
            double controlWidth = fits ? segmented : capsuleWidth;
            double total = controlWidth + toggleRun;
            double start = Math.Max(Margin, (contentWidth - total) / 2);
            var control = new LayoutRect(start, Margin, controlWidth, Height);
            var toggle = includeToggle
                ? new LayoutRect(control.MaxX + Gap, Margin, toggleWidth, Height)
                : default;
            var (sort, order, rating) = fits
                ? Segments(sortWidth, orderWidth, ratingWidth)
                : (default, default, default);
            return new WideFilterFrames(
                fits ? FilterControlMode.Segmented : FilterControlMode.Capsule,
                control, sort, order, rating, toggle, Height + Margin * 2);
        }

        var columnControl = new LayoutRect((contentWidth - cardWidth) / 2, leadingInset, cardWidth, Height);
        bool columnFits = segmented <= cardWidth;
        var columnToggle = includeToggle
            ? new LayoutRect(columnControl.X, columnControl.MaxY + Gap, cardWidth, Height)
            : default;
        var (columnSort, columnOrder, columnRating) = columnFits
            ? Segments(sortWidth + (cardWidth - segmented) / 3, orderWidth + (cardWidth - segmented) / 3,
                ratingWidth + (cardWidth - segmented) / 3)
            : (default, default, default);
        double mainLength = leadingInset + Height + (includeToggle ? Gap + Height : 0) + Margin;
        return new WideFilterFrames(
            columnFits ? FilterControlMode.Segmented : FilterControlMode.Capsule,
            columnControl, columnSort, columnOrder, columnRating, columnToggle, mainLength);
    }

    /// <summary>三段沿控件主轴从左到右首尾相接，每段高度占满控件。</summary>
    private static (LayoutRect Sort, LayoutRect Order, LayoutRect Rating) Segments(
        double sortWidth, double orderWidth, double ratingWidth)
        => (new LayoutRect(0, 0, sortWidth, Height),
            new LayoutRect(sortWidth, 0, orderWidth, Height),
            new LayoutRect(sortWidth + orderWidth, 0, ratingWidth, Height));
}
