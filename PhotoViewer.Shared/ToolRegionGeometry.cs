using PhotoViewer.Core.Design;

namespace PhotoViewer.Shared;

/// <summary>
/// 手机横屏与 iOS 宽形态的左右工具区几何：控制区与相邻分析栏共用一列内边距，相邻侧的间距由分析栏末端持有。
/// 控制区恒定由内容宽、尾随内边距与（独立时的）前导内边距组成；分析区由瓦片宽、内边距与接缝一侧的间距组成。
/// </summary>
public static class ToolRegionGeometry
{
    /// <summary>工具区四周内边距（10 点）。</summary>
    public static double Padding => DesignTokens.ToolbarPadding;

    /// <summary>分析内容与控制内容之间的接缝间距。</summary>
    public static double Spacing => DesignTokens.ShellToolRegionSpacing;

    /// <summary>相邻分组之间的间距（10 点）。</summary>
    public static double GroupSpacing => DesignTokens.ToolbarGroupSpacing;

    /// <summary>控制内容的前导内边距：与相邻分析栏共边时为 0，独立时是一份工具区内边距。</summary>
    /// <param name="joinedToAnalysis">控制区是否紧邻分析栏。</param>
    /// <returns>前导内边距。</returns>
    public static double ControlLeadingInset(bool joinedToAnalysis) => joinedToAnalysis ? 0 : Padding;

    /// <summary>控制区沿交叉轴的宽度：内容宽 + 尾随内边距 + 前导内边距（相邻分析栏时为 0）。</summary>
    /// <param name="contentWidth">控制内容的固有宽（玻璃按钮边长）。</param>
    /// <param name="joinedToAnalysis">控制区是否紧邻分析栏。</param>
    /// <returns>控制区宽度（相邻 54 / 独立 64，按钮边长 44 时）。</returns>
    public static double ControlWidth(double contentWidth, bool joinedToAnalysis)
        => contentWidth + Padding + ControlLeadingInset(joinedToAnalysis);

    /// <summary>分析区沿交叉轴的宽度：瓦片宽 + 内边距 + 接缝一侧的间距（相邻控制区时 8，否则 10）。</summary>
    /// <param name="tileWidth">瓦片边长。</param>
    /// <param name="joinedToControls">分析区是否紧邻控制区。</param>
    /// <returns>分析区宽度。</returns>
    public static double AnalysisWidth(double tileWidth, bool joinedToControls)
        => tileWidth + Padding + (joinedToControls ? Spacing : Padding);

    /// <summary>内容在可用区间内的居中起点：空间不足时停在区间起点。</summary>
    /// <param name="start">可用区间起点。</param>
    /// <param name="length">可用区间长度。</param>
    /// <param name="contentLength">内容长度。</param>
    /// <returns>内容起点。</returns>
    public static double CenteredStart(double start, double length, double contentLength)
        => start + Math.Max(0, (length - contentLength) / 2);
}
