namespace PhotoViewer.Shared;

/// <summary>
/// 主屏浮层外壳的分区矩形（SHELL-01、SHELL-11）：栏位背景、栏位内容、分隔线、主图与分析栏槽位。
/// 背景铺到窗口边缘，内容收在布局安全区内，主图分区满幅；坐标与宿主内容视图一致（左上角原点）。
/// </summary>
/// <param name="FileBar">文件栏背景分区。</param>
/// <param name="FileContent">文件栏内容区（筛选控件与胶片栏的可用范围）。</param>
/// <param name="FileDivider">文件栏与主图之间的分隔线。</param>
/// <param name="Photo">主图分区（同时是适应区）。</param>
/// <param name="Analysis">分析栏分区（交叉轴 0 表示未显示）。</param>
/// <param name="ControlDivider">控制栏与主图之间的分隔线（与分析栏相邻时为零）。</param>
/// <param name="ControlBackground">控制栏背景分区（分析栏出现时与其合成工具区背景）。</param>
/// <param name="ControlContent">控制栏内容区（系统滚动容器的可用范围）。</param>
public readonly record struct ShellPaneFrames(LayoutRect FileBar, LayoutRect FileContent, LayoutRect FileDivider, LayoutRect Photo,
    LayoutRect Analysis, LayoutRect ControlDivider, LayoutRect ControlBackground, LayoutRect ControlContent);

/// <summary>
/// 宽形态主屏的分区几何（SHELL-01、SHELL-11）：在 <see cref="MainScreenLayout.Partition"/> 之上叠加浮层口径——
/// 栏位背景铺满窗口边缘，栏位内容在上下布局收在安全区内、左右布局纵贯窗口，主图分区保持满幅。
/// 平台只提供方向归一后的安全区与各栏主轴长度；手机形态走 <see cref="PhoneScreenLayout"/>。
/// </summary>
public static class ShellPaneLayout
{
    /// <summary>
    /// 布局安全区：宽大于高（横屏主屏，含宽形态行布局）时不为底部手势条保留安全区，其余与系统安全区一致；
    /// 刘海、屏幕圆角与窗口控件由各元素带的探针单独避让，不在这里扣除。
    /// </summary>
    /// <param name="safe">方向归一后的系统安全区。</param>
    /// <param name="width">舞台宽。</param>
    /// <param name="height">舞台高。</param>
    /// <returns>布局用安全区。</returns>
    public static EdgeInsets SafeInsets(EdgeInsets safe, double width, double height)
        => width > height ? safe with { Bottom = 0 } : safe;

    /// <summary>
    /// 计算两向分区：文件栏与控制栏贴主轴两端，分析栏夹在主图与控制栏之间；传入长度为 0 的栏位被压成零并把空间让给主图。
    /// 文件栏内容的交叉轴范围可由调用方给出（列布局的胶片条从安全区前导之后起排），未给出时按安全区与固定内容宽推导。
    /// </summary>
    /// <param name="horizontal">是否行布局（文件栏在上、控制栏在下）。</param>
    /// <param name="width">舞台宽。</param>
    /// <param name="height">舞台高。</param>
    /// <param name="safe">方向归一后的安全区。</param>
    /// <param name="fileMain">文件栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="controlMain">控制栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="analysisCross">分析栏交叉轴长度（0 表示隐藏）。</param>
    /// <param name="fileContentX">列布局文件栏内容的起点（未给出时取安全区前导）。</param>
    /// <param name="fileContentWidth">列布局文件栏内容的宽度（未给出时取栏内剩余宽度与固定内容宽中的较小者）。</param>
    /// <returns>八个分区矩形。</returns>
    public static ShellPaneFrames Partition(bool horizontal, double width, double height, EdgeInsets safe,
        double fileMain, double controlMain, double analysisCross, double? fileContentX = null, double? fileContentWidth = null)
    {
        bool joined = controlMain > 0 && analysisCross > 0;
        double controlExtent = Math.Max(0, controlMain - (joined ? MainScreenLayout.Divider : 0));
        if (horizontal)
        {
            double boxHeight = Math.Max(1, height - safe.Top - safe.Bottom);
            var panes = MainScreenLayout.Partition(true, width, boxHeight, fileMain, controlExtent, analysisCross);
            double contentWidth = Math.Max(1, width - safe.Left - safe.Right);
            double regionTop = analysisCross > 0 ? safe.Top + panes.Analysis.Y : safe.Top + panes.ControlDivider.Y;
            return new ShellPaneFrames(
                new LayoutRect(0, 0, width, safe.Top + panes.FileBar.Height),
                new LayoutRect(safe.Left, safe.Top, contentWidth, panes.FileBar.Height),
                Shift(panes.FileDivider, safe.Top),
                Shift(panes.Photo, safe.Top),
                analysisCross > 0 ? Shift(panes.Analysis, safe.Top) : default,
                controlMain > 0 && !joined ? Shift(panes.ControlDivider, safe.Top) : default,
                analysisCross > 0 || controlMain > 0
                    ? new LayoutRect(0, regionTop, width, Math.Max(0, height - regionTop)) : default,
                controlMain > 0
                    ? new LayoutRect(safe.Left, safe.Top + (joined ? panes.ControlDivider.Y : panes.ControlBar.Y), contentWidth,
                        panes.ControlBar.Height + (joined ? MainScreenLayout.Divider : 0))
                    : default);
        }
        var column = MainScreenLayout.Partition(false, width, height, fileMain, controlExtent, analysisCross);
        double columnContentX = fileContentX ?? safe.Left;
        double columnContentWidth = fileContentWidth
            ?? Math.Min(MainScreenLayout.FileBarContentWidth, Math.Max(0, column.FileBar.Width - columnContentX));
        double controlX = joined ? column.ControlDivider.X : column.ControlBar.X;
        double regionLeft = analysisCross > 0 ? column.Analysis.X : column.ControlDivider.X;
        return new ShellPaneFrames(
            column.FileBar,
            new LayoutRect(columnContentX, 0, Math.Max(0, columnContentWidth), height),
            column.FileDivider,
            column.Photo,
            analysisCross > 0 ? column.Analysis : default,
            controlMain > 0 && !joined ? column.ControlDivider : default,
            analysisCross > 0 || controlMain > 0 ? new LayoutRect(regionLeft, 0, width - regionLeft, height) : default,
            controlMain > 0
                ? new LayoutRect(controlX, 0, column.ControlBar.Width + (joined ? MainScreenLayout.Divider : 0), height)
                : default);
    }

    /// <summary>沿主轴平移矩形（行布局把安全区顶边让位补回主轴坐标）。</summary>
    /// <param name="rect">原始矩形。</param>
    /// <param name="offset">主轴偏移。</param>
    /// <returns>平移后的矩形。</returns>
    private static LayoutRect Shift(LayoutRect rect, double offset) => new(rect.X, rect.Y + offset, rect.Width, rect.Height);
}
