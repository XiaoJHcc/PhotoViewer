using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>
/// EXIF 块的平台中立几何（TOOLBAR-05）：一个深色圆角块内的项目尺寸、内边距、整块尺寸与逐项矩形。
/// 横排时块高取玻璃按钮边长、项目沿水平方向排布；竖排时块宽为项目宽加两侧内边距、项目沿竖直方向排布。
/// 宿主只负责把逐项矩形落到原生文本控件上。
/// </summary>
public static class ExifBlockGeometry
{
    /// <summary>每项宽（标签在上、数值在下，居中尾部截断）。</summary>
    public static double ItemWidth => DesignTokens.ToolbarExifItemWidth;

    /// <summary>竖排时每项高。</summary>
    public static double ItemHeight => DesignTokens.ToolbarExifItemHeight;

    /// <summary>相邻项的间距。</summary>
    public static double ItemSpacing => DesignTokens.ToolbarExifItemSpacing;

    /// <summary>整块四周内边距。</summary>
    public static double Padding => DesignTokens.ToolbarExifPadding;

    /// <summary>横排块的交叉轴长度（与玻璃按钮边长一致）。</summary>
    public static double Thickness => DesignTokens.ShellGlassButtonSize;

    /// <summary>整块圆角。</summary>
    public static double CornerRadius => DesignTokens.ToolbarExifCornerRadius;

    /// <summary>
    /// 整块的固有尺寸：横排为「两侧内边距 + n 项宽 + (n−1) 项距 × 厚度」，竖排为「项宽 + 两侧内边距 ×
    /// 两侧内边距 + n 项高 + (n−1) 项距」；无项时为零。
    /// </summary>
    /// <param name="count">EXIF 项数。</param>
    /// <param name="horizontal">项目是否横排。</param>
    /// <returns>整块宽高。</returns>
    public static (double Width, double Height) Size(int count, bool horizontal)
    {
        int items = Math.Max(0, count);
        if (items == 0) return (0, 0);
        double run = items * (horizontal ? ItemWidth : ItemHeight) + (items - 1) * ItemSpacing;
        return horizontal
            ? (Padding * 2 + run, Thickness)
            : (ItemWidth + Padding * 2, Padding * 2 + run);
    }

    /// <summary>
    /// 单项矩形（相对整块左上角）：项目沿主轴从内边距起排、彼此相距 <see cref="ItemSpacing"/>。
    /// 整块主轴实际长度 <paramref name="blockMain"/> 可能短于固有长度（槽位挤压），此时各项等量收窄，
    /// 收窄量按平均分摊且不小于 0；长于固有长度时各项保持固有尺寸、不拉伸。索引越界返回零矩形。
    /// </summary>
    /// <param name="index">项目索引。</param>
    /// <param name="count">EXIF 项数。</param>
    /// <param name="horizontal">项目是否横排（横排为主轴 x，竖排为主轴 y）。</param>
    /// <param name="blockMain">整块沿主轴的实际长度（横排为宽，竖排为高）。</param>
    /// <returns>项目矩形。</returns>
    public static LayoutRect ItemRect(int index, int count, bool horizontal, double blockMain)
    {
        int items = Math.Max(0, count);
        if (index < 0 || index >= items) return default;

        double intrinsic = horizontal ? ItemWidth : ItemHeight;
        double available = (blockMain - Padding * 2 - (items - 1) * ItemSpacing) / items;
        double main = Math.Clamp(available, 0, intrinsic);
        double start = Padding + index * (main + ItemSpacing);
        return horizontal
            ? new LayoutRect(start, 0, main, Thickness)
            : new LayoutRect(Padding, start, ItemWidth, main);
    }
}
