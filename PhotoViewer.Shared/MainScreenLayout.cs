namespace PhotoViewer.Shared;

/// <summary>平台中立的矩形（左上角原点、单位为逻辑点），两个原生壳的布局数学共用同一结果类型。</summary>
/// <param name="X">左边缘。</param>
/// <param name="Y">上边缘。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    /// <summary>右边缘。</summary>
    public double MaxX => X + Width;
    /// <summary>下边缘。</summary>
    public double MaxY => Y + Height;
}

/// <summary>
/// 主屏分区几何（平台中立）：文件栏/控制栏/分析栏的主轴长度、筛选条五个控件的位置与两向分区矩形。
/// 尺寸口径与旧壳主界面对拍一致（文件栏内容宽 116、卡片 96×144、筛选控件 90×32、
/// 相似聚类 toggle 90×27、间距 5、外边距 10）；Mac 与 iPad 原生壳只把结果落成各自的原生 frame，
/// 平台特有部分（macOS 隐藏式标题栏让位、iOS 安全区铺满边缘）留在各 head 内。
/// </summary>
public static class MainScreenLayout
{
    /// <summary>分区之间的 1 点分隔线。</summary>
    public const double Divider = 1;
    /// <summary>文件栏内容宽度（筛选条与胶片栏共用，旧壳 116 点）。</summary>
    public const double FileBarContentWidth = 116;
    /// <summary>筛选控件（三个下拉）宽。</summary>
    public const double FilterControlWidth = 90;
    /// <summary>筛选控件（三个下拉）高。</summary>
    public const double FilterControlHeight = 32;
    /// <summary>筛选条内容间距。</summary>
    public const double FilterGap = 5;
    /// <summary>相似聚类 toggle 高度。</summary>
    public const double FilterToggleHeight = 27;
    /// <summary>筛选条四周外边距（列布局为尾部外边距）。</summary>
    public const double FilterMargin = 10;
    /// <summary>计数文本槽高（旧壳 12 点字的量得值）。</summary>
    public const double FilterCountHeight = 17;

    /// <summary>胶片栏带宽：卡片高 + 顶内边距 + 底内边距。</summary>
    public static double FilmstripBand => FilmstripGeometry.CardHeight + FilmstripGeometry.TopPadding + FilmstripGeometry.BottomPadding;

    /// <summary>筛选条五个控件的矩形（相对文件栏内容原点，行布局为横向居中排布，列布局为 116 栏内纵向排布）。</summary>
    /// <param name="Sort">排序控件。</param>
    /// <param name="Order">升降序控件。</param>
    /// <param name="Rating">星级筛选控件。</param>
    /// <param name="Count">计数文本槽。</param>
    /// <param name="Toggle">相似聚类 toggle（不论是否参与居中都会给出位置）。</param>
    public readonly record struct FilterFrames(LayoutRect Sort, LayoutRect Order, LayoutRect Rating, LayoutRect Count, LayoutRect Toggle);

    /// <summary>分区矩形（含分隔线），坐标与宿主内容视图一致（左上角原点）。</summary>
    /// <param name="FileBar">文件栏背景分区。</param>
    /// <param name="FileDivider">文件栏与主图之间的分隔线。</param>
    /// <param name="Photo">主图分区。</param>
    /// <param name="Analysis">分析栏分区（交叉轴为 0 时表示未显示）。</param>
    /// <param name="ControlDivider">控制栏与主图之间的分隔线。</param>
    /// <param name="ControlBar">控制栏背景分区。</param>
    public readonly record struct PaneFrames(LayoutRect FileBar, LayoutRect FileDivider, LayoutRect Photo, LayoutRect Analysis,
        LayoutRect ControlDivider, LayoutRect ControlBar);

    /// <summary>筛选条自身的主轴长度：行布局为控件高 + 上下外边距，列布局为前导让位 + 三个控件 + 计数 + toggle 与尾部外边距。</summary>
    /// <param name="horizontal">是否行布局（文件栏在上下）。</param>
    /// <param name="leadingInset">列布局的前导让位（macOS 为隐藏标题栏交通灯让位，iOS 为安全区顶边 + 外边距）。</param>
    /// <returns>筛选条主轴长度。</returns>
    public static double FilterBarMainLength(bool horizontal, double leadingInset)
        => horizontal
            ? FilterControlHeight + FilterMargin * 2
            : leadingInset + FilterControlHeight * 3 + FilterGap * 3 + FilterCountHeight + FilterGap + FilterToggleHeight + FilterMargin;

    /// <summary>文件栏主轴长度（含分隔线）：行布局为筛选条 + 胶片栏带 + 分隔线，列布局为内容宽 + 分隔线 + 附加交叉轴长度。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="filterBarMain">筛选条主轴长度。</param>
    /// <param name="extraCross">列布局的附加交叉轴长度（macOS 为相似聚类面板宽度，iOS 为安全区前导内边距）。</param>
    /// <returns>文件栏主轴长度。</returns>
    public static double FileBarMainLength(bool horizontal, double filterBarMain, double extraCross)
        => horizontal ? filterBarMain + FilmstripBand + Divider : FileBarContentWidth + Divider + extraCross;

    /// <summary>控制栏主轴长度（含分隔线）。</summary>
    /// <param name="measuredMain">控制栏文档在主轴上的量得长度。</param>
    /// <returns>控制栏主轴长度。</returns>
    public static double ControlBarMainLength(double measuredMain) => measuredMain + Divider;

    /// <summary>
    /// 筛选条五个控件的位置：行布局默认整组横向居中（macOS 隐藏标题栏的交通灯让位），
    /// <paramref name="alignLeading"/> 时从内容前导边（外边距 10）起排（iPad：没有交通灯，不留左侧空带）；
    /// 列布局固定在 116 栏内纵向排列。计数与 toggle 与控件垂直居中。
    /// </summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容区宽度（行布局用其居中，列布局固定 116）。</param>
    /// <param name="leadingInset">列布局首控件的前导让位。</param>
    /// <param name="countWidth">计数文本的自然宽度。</param>
    /// <param name="includeSimilarityToggle">相似聚类 toggle 是否参与整组居中（隐藏时不占位，但位置照常给出）。</param>
    /// <param name="alignLeading">行布局是否从内容前导边起排而不是整组居中。</param>
    /// <param name="controlWidth">下拉控件宽度（默认 90 点；手机窄屏可传紧凑值，不改变其他平台默认口径）。</param>
    /// <param name="toggleWidth">相似聚类 toggle 宽度（默认 90 点）。</param>
    /// <returns>五个控件的矩形。</returns>
    public static FilterFrames FilterBarFrames(bool horizontal, double contentWidth, double leadingInset, double countWidth,
        bool includeSimilarityToggle, bool alignLeading = false, double controlWidth = FilterControlWidth,
        double toggleWidth = FilterControlWidth)
    {
        double toggleHeight = FilterToggleHeight;
        double controlGap = FilterGap;
        if (horizontal)
        {
            double controlsWidth = controlWidth * 3 + controlGap * 2 + controlGap + countWidth;
            double contentWidthUsed = includeSimilarityToggle ? controlsWidth + controlGap + toggleWidth : controlsWidth;
            double start = alignLeading ? FilterMargin : (contentWidth - contentWidthUsed) / 2;
            double controlY = FilterMargin;
            double countX = start + (controlWidth + controlGap) * 3;
            double toggleX = countX + countWidth + controlGap;
            return new FilterFrames(
                new LayoutRect(start, controlY, controlWidth, FilterControlHeight),
                new LayoutRect(start + controlWidth + controlGap, controlY, controlWidth, FilterControlHeight),
                new LayoutRect(start + (controlWidth + controlGap) * 2, controlY, controlWidth, FilterControlHeight),
                new LayoutRect(countX, controlY + (FilterControlHeight - FilterCountHeight) / 2, countWidth, FilterCountHeight),
                new LayoutRect(toggleX, controlY + (FilterControlHeight - toggleHeight) / 2, toggleWidth, toggleHeight));
        }
        // 列布局的 toggle 始终紧随计数下方，与是否参与整组居中无关。
        double left = (FileBarContentWidth - controlWidth) / 2;
        double top = leadingInset;
        double countY = top + (FilterControlHeight + controlGap) * 3;
        return new FilterFrames(
            new LayoutRect(left, top, controlWidth, FilterControlHeight),
            new LayoutRect(left, top + FilterControlHeight + controlGap, controlWidth, FilterControlHeight),
            new LayoutRect(left, top + (FilterControlHeight + controlGap) * 2, controlWidth, FilterControlHeight),
            new LayoutRect((FileBarContentWidth - countWidth) / 2, countY, countWidth, FilterCountHeight),
            new LayoutRect(left, countY + FilterCountHeight + controlGap, toggleWidth, toggleHeight));
    }

    /// <summary>
    /// 两向分区矩形：文件栏与控制栏贴主轴两端，主图取剩余空间，分析栏夹在主图与控制栏之间。
    /// 传入长度为 0 表示该栏隐藏（其分区被压成零并让空间回主图），与分析栏交叉轴为 0 时同理。
    /// </summary>
    /// <param name="horizontal">是否行布局（文件栏在上、控制栏在下）。</param>
    /// <param name="width">可用宽度。</param>
    /// <param name="height">可用高度。</param>
    /// <param name="fileMain">文件栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="controlMain">控制栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="analysisCross">分析栏交叉轴长度（0 表示隐藏）。</param>
    /// <returns>六个分区矩形。</returns>
    public static PaneFrames Partition(bool horizontal, double width, double height, double fileMain, double controlMain, double analysisCross)
    {
        double file = Math.Max(0, fileMain);
        double control = Math.Max(0, controlMain);
        double analysis = Math.Max(0, analysisCross);
        if (horizontal)
        {
            double photoHeight = Math.Max(1, height - file - analysis - control);
            return new PaneFrames(
                new LayoutRect(0, 0, width, Math.Max(0, file - Divider)),
                new LayoutRect(0, file - Divider, width, Divider),
                new LayoutRect(0, file, width, photoHeight),
                new LayoutRect(0, file + photoHeight, width, analysis),
                new LayoutRect(0, height - control, width, Divider),
                new LayoutRect(0, height - control + Divider, width, Math.Max(0, control - Divider)));
        }
        double photoWidth = Math.Max(1, width - file - analysis - control);
        return new PaneFrames(
            new LayoutRect(0, 0, Math.Max(0, file - Divider), height),
            new LayoutRect(file - Divider, 0, Divider, height),
            new LayoutRect(file, 0, photoWidth, height),
            new LayoutRect(file + photoWidth, 0, analysis, height),
            new LayoutRect(width - control, 0, Divider, height),
            new LayoutRect(width - control + Divider, 0, Math.Max(0, control - Divider), height));
    }
}
