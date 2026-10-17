namespace PhotoViewer.Shared;

/// <summary>
/// 相似聚类面板的平台中立几何（与旧壳 <c>SimilarityListView</c> 同口径，Mac 与 iPad 原生壳共用）：
/// 面板最小 106×154，列布局宽度 = 卡片 96 + 列表左右内边距 8 + 两侧 1 点描边，
/// 行布局从胶片栏右侧切出一段——宽度取"卡片数 × 96 + 内边距 + 描边"与"可用宽度一半"的较小者，
/// 再补 4 点外边距。宿主只把结果落成各自原生 frame，控件内部排布留在各 head。
/// </summary>
public static class SimilarityPanelGeometry
{
    /// <summary>列布局下面板最小宽度（旧壳 MinWidth）。</summary>
    public const double MinWidth = 106;

    /// <summary>行布局下面板最小高度（旧壳 MinHeight）。</summary>
    public const double MinHeight = 154;

    /// <summary>面板与主图/胶片栏之间的外边距（旧壳右侧 4 点）。</summary>
    public const double Margin = 4;

    /// <summary>面板四周 1 点描边。</summary>
    public const double BorderWidth = 1;

    /// <summary>行布局列表两侧内边距合计（旧壳 ItemsPanel 左右各 4 点）。</summary>
    public const double ListPadding = 8;

    /// <summary>列布局固定面板宽度：卡片 96 + 列表左右内边距 8 + 两侧 1 点描边。</summary>
    public const double PanelWidth = FilmstripGeometry.CardWidth + 4 + 8 + BorderWidth * 2;

    /// <summary>行布局列表交叉轴长度：卡片高 + 上下内边距 + 两侧描边。</summary>
    public static double ListCross
        => FilmstripGeometry.CardHeight + FilmstripGeometry.TopPadding + FilmstripGeometry.BottomPadding + BorderWidth * 2;

    /// <summary>
    /// 行布局从胶片栏右侧切出的总宽度（含 4 点外边距）：内容宽度 = 卡片数 × 卡宽 + 列表内边距 + 描边，
    /// 不小于 MinWidth 且不超过可用宽度一半。
    /// </summary>
    /// <param name="hasItems">是否有相似项（空态按 MinWidth 占位）。</param>
    /// <param name="itemCount">相似项数量。</param>
    /// <param name="paneWidth">文件栏/窗口的可用宽度。</param>
    /// <returns>胶片栏需要让出的宽度。</returns>
    public static double RowWidth(bool hasItems, int itemCount, double paneWidth)
    {
        double content = hasItems
            ? itemCount * FilmstripGeometry.CardWidth + ListPadding + BorderWidth * 2
            : MinWidth;
        return Math.Min(Math.Max(MinWidth, content), Math.Max(1, paneWidth) / 2) + Margin;
    }
}
