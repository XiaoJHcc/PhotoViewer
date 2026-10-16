namespace PhotoViewer.Apple;

/// <summary>
/// 分析栏的平台中立几何（与旧壳 <c>AnalysisView</c> 同口径）：
/// 正方形瓦片边长 = <c>floor((可用主轴长度 − 20 − 8×(n−1)) / n)</c>，从主轴内边距 10 起排、间距 8，
/// 交叉轴尺寸 = 瓦片边长 + 20。宿主视图负责把结果落到原生瓦片 frame。
/// </summary>
public static class AnalysisPanelGeometry
{
    /// <summary>瓦片四周内边距（旧壳 Border Padding）。</summary>
    public const double Padding = 10;
    /// <summary>瓦片间距（旧壳 StackPanel Spacing）。</summary>
    public const double Spacing = 8;

    /// <summary>按可用主轴长度算正方形瓦片边长。</summary>
    /// <param name="availableMain">主轴可用长度（行布局为面板宽，列布局为面板高）。</param>
    /// <param name="count">瓦片数。</param>
    /// <returns>瓦片边长，至少 1。</returns>
    public static double TileSize(double availableMain, int count)
    {
        int n = Math.Max(1, count);
        double usable = Math.Max(0, availableMain - Padding * 2 - Spacing * (n - 1));
        return Math.Max(1, Math.Floor(usable / n));
    }

    /// <summary>交叉轴尺寸（瓦片边长 + 两侧内边距）。</summary>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <param name="count">瓦片数。</param>
    /// <returns>需为面板分配的交叉轴长度。</returns>
    public static double CrossSize(double availableMain, int count) => TileSize(availableMain, count) + Padding * 2;

    /// <summary>第 index 张瓦片沿主轴的起点；从主轴内边距起排，剩余不足一枚的零头留在末端。</summary>
    /// <param name="index">瓦片顺序索引。</param>
    /// <param name="tileSize">瓦片边长。</param>
    /// <returns>主轴起点。</returns>
    public static double TileOffset(int index, double tileSize) => Padding + index * (tileSize + Spacing);
}
