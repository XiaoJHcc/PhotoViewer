using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>控制栏流式排布中的一个分组：业务类型与显式主轴/交叉轴长度（由分组内容决定）。</summary>
/// <param name="Kind">分组类型（EXIF / 评分 / 命令），供宿主选择子控件样式。</param>
/// <param name="Main">分组沿主轴的长度。</param>
/// <param name="Cross">分组沿交叉轴的长度。</param>
public readonly record struct ToolbarGroupSize(ControlBarGroupKind Kind, double Main, double Cross);

/// <summary>流式排布结果：文档宽高与按输入顺序的分组矩形（文档坐标，左上角原点）。</summary>
/// <param name="Width">文档宽。</param>
/// <param name="Height">文档高。</param>
/// <param name="Groups">每个分组在文档坐标中的矩形。</param>
public readonly record struct ToolbarFlowDocument(double Width, double Height, IReadOnlyList<LayoutRect> Groups);

/// <summary>
/// 宽形态控制栏的流式排布（TOOLBAR-01）：分组尺寸由内容显式给出（命令圆按钮、星级控件、
/// EXIF 块），<see cref="Arrange"/> 一次给出文档宽高与每个分组矩形；整组换行/换列、
/// 行内居中与文档尺寸与 <see cref="ControlBarLayout"/> 同口径。
/// </summary>
public static class ToolbarFlowLayout
{
    /// <summary>文档默认外边距（10 点）；共同工具区的交叉轴前导内边距可由调用方省略。</summary>
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
    /// <param name="bare">是否取无衬底变体（直接放在栏背景上）。</param>
    /// <returns>EXIF 分组尺寸。</returns>
    public static ToolbarGroupSize Exif(int count, bool horizontal, bool bare = false)
    {
        var (width, height) = ExifBlockGeometry.Size(count, horizontal, bare);
        double cross = horizontal || count <= 0
            ? height
            : bare ? CommandSize : width;
        return new ToolbarGroupSize(ControlBarGroupKind.Exif, horizontal ? width : height, cross);
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

    /// <summary>
    /// 包含四周外边距的文档尺寸；空文档保留纯外边距。交叉轴的前导内边距可覆盖（与相邻工具区共边时传 0），
    /// 尾随内边距恒为一份 <see cref="Padding"/>，主轴不变。
    /// </summary>
    /// <param name="groups">分组尺寸。</param>
    /// <param name="horizontal">文档主轴是否为水平方向。</param>
    /// <param name="lines">分行结果。</param>
    /// <param name="crossLeadingPadding">交叉轴前导内边距；为空时与尾随同取 <see cref="Padding"/>。</param>
    /// <returns>文档尺寸（宽、高）。</returns>
    public static (double Width, double Height) DocumentSize(
        IReadOnlyList<ToolbarGroupSize> groups, bool horizontal, IReadOnlyList<List<int>> lines,
        double? crossLeadingPadding = null)
    {
        double main = 0;
        double cross = 0;
        foreach (var line in lines) main = Math.Max(main, LineLength(groups, line));
        foreach (var line in lines) cross += LineCross(groups, line);
        cross += Math.Max(0, lines.Count - 1) * GroupSpacing;
        double crossTotal = cross + (crossLeadingPadding ?? Padding) + Padding;
        return horizontal
            ? (main + Padding * 2, crossTotal)
            : (crossTotal, main + Padding * 2);
    }

    /// <summary>行在文档主轴上的起点：整行居中，空间不足时退回外边距。</summary>
    /// <param name="availableMain">文档主轴可用长度。</param>
    /// <param name="lineLength">该行主轴长度。</param>
    /// <returns>行起点坐标。</returns>
    public static double LineStart(double availableMain, double lineLength)
        => Math.Max(Padding, (availableMain - lineLength) / 2);

    /// <summary>扣掉主轴两端外边距后的内容主轴长度；分组尺寸（星级展开/紧凑判定）与整组换行以此为准。</summary>
    /// <param name="availableMain">控制栏沿主轴的可用长度（已扣平台内边距）。</param>
    /// <returns>内容主轴长度，至少 1。</returns>
    public static double ContentMain(double availableMain)
        => Math.Max(1, Math.Max(1, availableMain) - Padding * 2);

    /// <summary>
    /// 完整流式排布：整组换行/换列、每行/列居中、组间与行间距、分组交叉轴拉伸到行高，
    /// 一次给出文档尺寸与每个分组在文档坐标中的矩形。分组尺寸由调用方按 <see cref="ContentMain"/> 预先求出。
    /// </summary>
    /// <param name="groups">按顺序的分组尺寸。</param>
    /// <param name="horizontal">文档主轴是否为水平方向。</param>
    /// <param name="availableMain">文档主轴可用长度（已扣平台内边距）。</param>
    /// <param name="crossLeadingPadding">交叉轴前导内边距；为空时取 <see cref="Padding"/>，与相邻分析栏共边时传 0。</param>
    /// <returns>文档宽高与每个分组的矩形。</returns>
    public static ToolbarFlowDocument Arrange(IReadOnlyList<ToolbarGroupSize> groups, bool horizontal,
        double availableMain, double? crossLeadingPadding = null)
    {
        double available = Math.Max(1, availableMain);
        var lines = PackLines(groups, ContentMain(availableMain));
        var frames = new LayoutRect[groups.Count];
        double cross = crossLeadingPadding ?? Padding;
        foreach (var line in lines)
        {
            double lineCross = LineCross(groups, line);
            double start = LineStart(available, LineLength(groups, line));
            foreach (int index in line)
            {
                var size = groups[index];
                frames[index] = horizontal
                    ? new LayoutRect(start, cross, size.Main, lineCross)
                    : new LayoutRect(cross, start, lineCross, size.Main);
                start += size.Main + GroupSpacing;
            }
            cross += lineCross + GroupSpacing;
        }
        var document = DocumentSize(groups, horizontal, lines, crossLeadingPadding);
        return new ToolbarFlowDocument(document.Width, document.Height, frames);
    }
}
