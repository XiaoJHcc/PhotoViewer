namespace PhotoViewer.Shared;

/// <summary>控制栏业务分组类型；决定分组内子控件尺寸、内边距与间距口径。</summary>
public enum ControlBarGroupKind
{
    /// <summary>EXIF 信息块分组（50×40 小块，每块自带内边距）。</summary>
    Exif = 0,
    /// <summary>评分分组（30×30，纵向布局中反序）。</summary>
    Rating = 1,
    /// <summary>命令按钮分组（40×40）。</summary>
    Command = 2,
}

/// <summary>
/// 控制栏的平台中立布局数学：分组内子控件尺寸与内边距、整组主轴长度、按可用空间整组换行/换列、
/// 行内居中与文档尺寸。宿主只负责把结果落到原生视图 frame。
/// </summary>
public static class ControlBarLayout
{
    /// <summary>文档四周外边距。</summary>
    public const double Padding = 10;
    /// <summary>分组间距。</summary>
    public const double GroupSpacing = 10;

    /// <summary>一个分组的布局输入：类型与子控件数。子控件数决定分组主轴长度。</summary>
    /// <param name="Kind">分组类型。</param>
    /// <param name="Count">子控件数。</param>
    public readonly record struct GroupSpec(ControlBarGroupKind Kind, int Count);

    /// <summary>分组内单个子控件的尺寸、子控件间距与分组内边距。</summary>
    /// <param name="Inset">分组内边距（EXIF 每块各留一次）。</param>
    /// <param name="Spacing">子控件间距。</param>
    /// <param name="Width">子控件宽。</param>
    /// <param name="Height">子控件高。</param>
    public readonly record struct Metrics(double Inset, double Spacing, double Width, double Height);

    /// <summary>按分组类型取子控件指标。</summary>
    /// <param name="kind">分组类型。</param>
    /// <returns>该类型的子控件尺寸、间距与内边距。</returns>
    public static Metrics GetMetrics(ControlBarGroupKind kind) => kind switch
    {
        ControlBarGroupKind.Exif => new Metrics(4, 4, 50, 40),
        ControlBarGroupKind.Rating => new Metrics(8, 0, 30, 30),
        _ => new Metrics(0, 8, 40, 40),
    };

    /// <summary>分组在主轴上的长度，含分组自带内边距。</summary>
    /// <param name="group">分组输入。</param>
    /// <param name="horizontal">是否横向（控制栏整行排布）。</param>
    /// <returns>主轴长度。</returns>
    public static double MainLength(GroupSpec group, bool horizontal)
    {
        var metrics = GetMetrics(group.Kind);
        double childMain = horizontal ? metrics.Width : metrics.Height;
        int count = Math.Max(0, group.Count);
        return count * childMain + Math.Max(0, count - 1) * metrics.Spacing
            + metrics.Inset * 2 * (group.Kind == ControlBarGroupKind.Exif ? count : 1);
    }

    /// <summary>分组自身在交叉轴上的长度（未考虑行内拉伸）。</summary>
    /// <param name="group">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <returns>交叉轴长度。</returns>
    public static double CrossLength(GroupSpec group, bool horizontal)
    {
        var metrics = GetMetrics(group.Kind);
        return horizontal ? metrics.Height : metrics.Width;
    }

    /// <summary>整组沿主轴排布，空间不足时换行/换列；单个超长分组保持整体不被拆开。</summary>
    /// <param name="groups">按顺序的分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="available">主轴可用长度。</param>
    /// <returns>每一行/列的分组索引。</returns>
    public static List<List<int>> PackLines(IReadOnlyList<GroupSpec> groups, bool horizontal, double available)
    {
        var lines = new List<List<int>>();
        for (int index = 0; index < groups.Count; index++)
        {
            if (lines.Count == 0
                || lines[^1].Count > 0 && LineLength(groups, horizontal, lines[^1]) + GroupSpacing + MainLength(groups[index], horizontal) > available)
                lines.Add([]);
            lines[^1].Add(index);
        }
        return lines;
    }

    /// <summary>一行/一列的主轴总长度，组间空隙不计入最后一项。</summary>
    /// <param name="groups">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="line">该行的分组索引。</param>
    /// <returns>主轴总长度。</returns>
    public static double LineLength(IReadOnlyList<GroupSpec> groups, bool horizontal, IReadOnlyList<int> line)
    {
        double total = 0;
        for (int index = 0; index < line.Count; index++) total += MainLength(groups[line[index]], horizontal);
        return total + Math.Max(0, line.Count - 1) * GroupSpacing;
    }

    /// <summary>一行内最高的分组交叉轴长度。</summary>
    /// <param name="groups">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="line">该行的分组索引。</param>
    /// <returns>行交叉轴长度。</returns>
    public static double LineCross(IReadOnlyList<GroupSpec> groups, bool horizontal, IReadOnlyList<int> line)
    {
        double cross = 0;
        foreach (int index in line) cross = Math.Max(cross, CrossLength(groups[index], horizontal));
        return cross;
    }

    /// <summary>各行交叉轴长度之和加行间距（不含四周外边距）。</summary>
    /// <param name="groups">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="lines">分行结果。</param>
    /// <returns>交叉轴总长度。</returns>
    public static double CrossTotal(IReadOnlyList<GroupSpec> groups, bool horizontal, IReadOnlyList<List<int>> lines)
    {
        double total = 0;
        foreach (var line in lines) total += LineCross(groups, horizontal, line);
        return total + Math.Max(0, lines.Count - 1) * GroupSpacing;
    }

    /// <summary>包含四周外边距的文档尺寸；空文档保留纯外边距。</summary>
    /// <param name="groups">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="lines">分行结果。</param>
    /// <returns>文档尺寸（宽、高）。</returns>
    public static (double Width, double Height) DocumentSize(IReadOnlyList<GroupSpec> groups, bool horizontal, IReadOnlyList<List<int>> lines)
    {
        double main = 0;
        foreach (var line in lines) main = Math.Max(main, LineLength(groups, horizontal, line));
        double cross = CrossTotal(groups, horizontal, lines);
        return horizontal ? (main + Padding * 2, cross + Padding * 2) : (cross + Padding * 2, main + Padding * 2);
    }

    /// <summary>分组内每个子控件相对分组原点的位置（主轴与交叉轴分量，调用方按方向落位）。</summary>
    /// <param name="group">分组输入。</param>
    /// <param name="horizontal">是否横向。</param>
    /// <param name="crossSize">所在行的交叉轴长度（用于子控件居中）。</param>
    /// <returns>与子控件顺序一致的偏移数组。</returns>
    public static (double Main, double Cross)[] ChildOffsets(GroupSpec group, bool horizontal, double crossSize)
    {
        var metrics = GetMetrics(group.Kind);
        double childMain = horizontal ? metrics.Width : metrics.Height;
        double childCross = horizontal ? metrics.Height : metrics.Width;
        double cross = Math.Max(childCross, crossSize);
        var offsets = new (double Main, double Cross)[Math.Max(0, group.Count)];
        for (int index = 0; index < offsets.Length; index++)
        {
            int position = group.Kind == ControlBarGroupKind.Rating && !horizontal ? group.Count - index - 1 : index;
            double main = metrics.Inset + position * (childMain + metrics.Spacing + (group.Kind == ControlBarGroupKind.Exif ? metrics.Inset * 2 : 0));
            offsets[index] = (main, (cross - childCross) / 2);
        }
        return offsets;
    }
}
