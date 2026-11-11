using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>控制栏流式排布中的一个分组：业务类型与显式主轴/交叉轴长度（由分组内容决定）。</summary>
/// <param name="Kind">分组类型（EXIF / 评分 / 命令），供宿主选择子控件样式。</param>
/// <param name="Main">分组沿主轴的长度。</param>
/// <param name="Cross">分组沿交叉轴的长度。</param>
public readonly record struct ToolbarGroupSize(ControlBarGroupKind Kind, double Main, double Cross);

/// <summary>
/// 宽形态控制栏的流式排布（TOOLBAR-01）：分组尺寸由内容显式给出（命令圆按钮、星级控件、
/// EXIF 块），整组换行/换列、行内居中与文档尺寸与旧 <see cref="ControlBarLayout"/> 同口径。
/// Mac / Windows / Android 继续使用旧类型。
/// </summary>
public static class ToolbarFlowLayout
{
    /// <summary>文档四周外边距（10 点）。</summary>
    public static double Padding => DesignTokens.ToolbarPadding;

    /// <summary>分组间距，换行后同时作为行距（10 点）。</summary>
    public static double GroupSpacing => DesignTokens.ToolbarGroupSpacing;

    /// <summary>命令圆按钮边长（44 点）。</summary>
    public static double CommandSize => DesignTokens.ShellGlassButtonSize;

    /// <summary>命令按钮之间的间距（8 点）。</summary>
    public static double CommandSpacing => DesignTokens.ToolbarCommandSpacing;

    /// <summary>命令分组：按按钮数与间距求主轴长度，交叉轴为按钮边长。</summary>
    /// <param name="count">命令按钮数。</param>
    /// <returns>命令分组尺寸。</returns>
    public static ToolbarGroupSize Commands(int count)
    {
        int buttons = Math.Max(0, count);
        return new ToolbarGroupSize(
            ControlBarGroupKind.Command,
            buttons * CommandSize + Math.Max(0, buttons - 1) * CommandSpacing,
            CommandSize);
    }

    /// <summary>命令分组内第 index 个按钮相对分组原点的主轴偏移。</summary>
    /// <param name="index">按钮序号。</param>
    /// <returns>主轴偏移。</returns>
    public static double CommandOffset(int index) => index * (CommandSize + CommandSpacing);

    /// <summary>星级控件分组：可用主轴放得下固有长度时按展开态占位，否则按紧凑态最短长度占位。</summary>
    /// <param name="availableMain">控制栏沿主轴的可用长度。</param>
    /// <returns>星级分组尺寸。</returns>
    public static ToolbarGroupSize Rating(double availableMain)
    {
        double main = RatingControlGeometry.ResolveForm(availableMain) == RatingControlForm.Expanded
            ? RatingControlGeometry.ExpandedLength
            : RatingControlGeometry.CompactMinLength;
        return new ToolbarGroupSize(ControlBarGroupKind.Rating, main, RatingControlGeometry.Thickness);
    }

    /// <summary>EXIF 分组：整块尺寸取自 <see cref="ExifBlockGeometry"/>（横排为主轴取块宽，竖排取块高）。</summary>
    /// <param name="count">EXIF 项数。</param>
    /// <param name="horizontal">EXIF 块是否横排。</param>
    /// <returns>EXIF 分组尺寸。</returns>
    public static ToolbarGroupSize Exif(int count, bool horizontal)
    {
        var (width, height) = ExifBlockGeometry.Size(count, horizontal);
        return new ToolbarGroupSize(
            ControlBarGroupKind.Exif,
            horizontal ? width : height,
            horizontal ? height : width);
    }

    /// <summary>整组沿主轴排布，空间不足时换行/换列；单个超长分组保持整体不被拆开。</summary>
    /// <param name="groups">按顺序的分组尺寸。</param>
    /// <param name="available">主轴可用长度。</param>
    /// <returns>每一行/列的分组索引。</returns>
    public static List<List<int>> PackLines(IReadOnlyList<ToolbarGroupSize> groups, double available)
    {
        var lines = new List<List<int>>();
        for (int index = 0; index < groups.Count; index++)
        {
            if (lines.Count == 0
                || lines[^1].Count > 0 && LineLength(groups, lines[^1]) + GroupSpacing + groups[index].Main > available)
                lines.Add([]);
            lines[^1].Add(index);
        }
        return lines;
    }

    /// <summary>一行/一列的主轴总长度，组间空隙不计入最后一项。</summary>
    /// <param name="groups">分组尺寸。</param>
    /// <param name="line">该行的分组索引。</param>
    /// <returns>主轴总长度。</returns>
    public static double LineLength(IReadOnlyList<ToolbarGroupSize> groups, IReadOnlyList<int> line)
    {
        double total = 0;
        foreach (int index in line) total += groups[index].Main;
        return total + Math.Max(0, line.Count - 1) * GroupSpacing;
    }

    /// <summary>一行内最高的分组交叉轴长度。</summary>
    /// <param name="groups">分组尺寸。</param>
    /// <param name="line">该行的分组索引。</param>
    /// <returns>行交叉轴长度。</returns>
    public static double LineCross(IReadOnlyList<ToolbarGroupSize> groups, IReadOnlyList<int> line)
    {
        double cross = 0;
        foreach (int index in line) cross = Math.Max(cross, groups[index].Cross);
        return cross;
    }

    /// <summary>包含四周外边距的文档尺寸；空文档保留纯外边距。</summary>
    /// <param name="groups">分组尺寸。</param>
    /// <param name="horizontal">文档主轴是否为水平方向。</param>
    /// <param name="lines">分行结果。</param>
    /// <returns>文档尺寸（宽、高）。</returns>
    public static (double Width, double Height) DocumentSize(
        IReadOnlyList<ToolbarGroupSize> groups, bool horizontal, IReadOnlyList<List<int>> lines)
    {
        double main = 0;
        double cross = 0;
        foreach (var line in lines) main = Math.Max(main, LineLength(groups, line));
        foreach (var line in lines) cross += LineCross(groups, line);
        cross += Math.Max(0, lines.Count - 1) * GroupSpacing;
        return horizontal
            ? (main + Padding * 2, cross + Padding * 2)
            : (cross + Padding * 2, main + Padding * 2);
    }

    /// <summary>行在文档主轴上的起点：整行居中，空间不足时退回外边距。</summary>
    /// <param name="availableMain">文档主轴可用长度。</param>
    /// <param name="lineLength">该行主轴长度。</param>
    /// <returns>行起点坐标。</returns>
    public static double LineStart(double availableMain, double lineLength)
        => Math.Max(Padding, (availableMain - lineLength) / 2);
}
