namespace PhotoViewer.Shared;

/// <summary>
/// EXIF 详情页的 7 列行几何（平台中立，Mac 与 iPad 工具页共用旧壳口径）：
/// 首列 Tag ID 60 点，其余 2:2:3 分配给 名称 / 中文名 / 值，列间 1 点分隔线；
/// 单元格左右内边距 10、上下 8，值列最多 3 行（尾部截断）。宿主只负责落 frame 与文本绘制。
/// </summary>
public static class ExifTableGeometry
{
    /// <summary>Tag ID 列宽（旧壳 ColumnDefinitions 首列 60）。</summary>
    public const double IdColumnWidth = 60;

    /// <summary>列分隔线与行分隔线厚度。</summary>
    public const double DividerThickness = 1;

    /// <summary>名称/中文名/值单元格的左右内边距（旧壳 Padding 10,8）。</summary>
    public const double CellPaddingX = 10;

    /// <summary>名称/中文名/值单元格的上下内边距。</summary>
    public const double CellPaddingY = 8;

    /// <summary>值列最多显示行数（旧壳 MaxLines=3）。</summary>
    public const int ValueMaxLines = 3;

    /// <summary>7 列分配结果（文本列宽度已扣除单元格内边距）。</summary>
    /// <param name="NameColumn">名称列总宽（含内边距）。</param>
    /// <param name="ChineseColumn">中文名列总宽（含内边距）。</param>
    /// <param name="ValueColumn">值列总宽（含内边距）。</param>
    /// <param name="NameTextWidth">名称可用文本宽。</param>
    /// <param name="ChineseTextWidth">中文名可用文本宽。</param>
    /// <param name="ValueTextWidth">值可用文本宽。</param>
    public readonly record struct Columns(double NameColumn, double ChineseColumn, double ValueColumn,
        double NameTextWidth, double ChineseTextWidth, double ValueTextWidth);

    /// <summary>按卡片宽度计算 2:2:3 的三列宽度与各自可用文本宽。</summary>
    /// <param name="cardWidth">卡片（行容器）宽度。</param>
    /// <returns>列宽与文本宽。</returns>
    public static Columns Compute(double cardWidth)
    {
        double columns = Math.Max(0, cardWidth - IdColumnWidth - DividerThickness * 3);
        double unit = columns / 7;
        double nameColumn = unit * 2;
        double chineseColumn = unit * 2;
        double valueColumn = Math.Max(0, columns - nameColumn - chineseColumn);
        return new Columns(nameColumn, chineseColumn, valueColumn,
            Math.Max(1, nameColumn - CellPaddingX * 2),
            Math.Max(1, chineseColumn - CellPaddingX * 2),
            Math.Max(1, valueColumn - CellPaddingX * 2));
    }
}
