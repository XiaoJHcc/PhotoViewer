using PhotoViewer.Apple;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 主屏分区几何：在共享 <see cref="MainScreenLayout.Partition"/> 之上叠加 UIKit 的安全区口径——
/// 栏位**背景**铺满屏幕边缘（状态栏、Home 指示条与横屏刘海区都盖住），栏位**内容**（筛选条、胶片栏、控制栏）
/// 在上下布局收在安全区内，左右布局纵贯窗口；主图区保持满幅。
/// 分析栏的交叉轴长度由调用方传入，未启用时恒为 0（槽位保留，空间归主图）。
/// </summary>
/// <param name="FileBar">文件栏背景分区。</param>
/// <param name="FileContent">文件栏内容区（筛选条与胶片栏的可用范围）。</param>
/// <param name="FileDivider">文件栏与主图之间的分隔线。</param>
/// <param name="Photo">主图分区。</param>
/// <param name="Analysis">分析栏分区（交叉轴 0 表示未显示）。</param>
/// <param name="ControlDivider">控制栏与主图之间的分隔线。</param>
/// <param name="ControlBackground">控制栏背景分区（铺到屏幕边缘）。</param>
/// <param name="ControlContent">控制栏内容区（系统滚动容器的可用范围）。</param>
internal readonly record struct iOSPaneFrames(LayoutRect FileBar, LayoutRect FileContent, LayoutRect FileDivider, LayoutRect Photo,
    LayoutRect Analysis, LayoutRect ControlDivider, LayoutRect ControlBackground, LayoutRect ControlContent);

/// <summary>iPad 主屏分区计算（纯函数，便于程序化对拍）。</summary>
internal static class iOSMainScreenLayout
{
    /// <summary>
    /// 布局实际使用的安全区：宽大于高（横屏主屏，含 iPad 行布局）时不为底部 Home 指示条保留安全区，
    /// 其余与系统安全区一致；刘海、屏幕圆角与窗口控件由各元素带的探针单独避让，不在这里扣除。
    /// </summary>
    /// <param name="safe">系统安全区。</param>
    /// <param name="width">舞台宽。</param>
    /// <param name="height">舞台高。</param>
    /// <returns>布局用安全区。</returns>
    internal static UIEdgeInsets LayoutSafeInsets(UIEdgeInsets safe, double width, double height)
        => width > height ? new UIEdgeInsets(safe.Top, safe.Left, 0, safe.Right) : safe;

    /// <summary>计算两向分区：行布局栏位在上下、列布局在左右，隐藏的栏位压成零并把空间让给主图。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="width">屏幕宽度。</param>
    /// <param name="height">屏幕高度。</param>
    /// <param name="safe">安全区（真实 <c>SafeAreaInsets</c>）。</param>
    /// <param name="fileMain">文件栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="controlMain">控制栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="analysisCross">分析栏交叉轴长度（未启用时为 0）。</param>
    /// <returns>八个分区矩形。</returns>
    internal static iOSPaneFrames Compute(bool horizontal, double width, double height, UIEdgeInsets safe,
        double fileMain, double controlMain, double analysisCross)
    {
        double left = (double)safe.Left, right = (double)safe.Right, top = (double)safe.Top, bottom = (double)safe.Bottom;
        if (horizontal)
        {
            // 行布局：主轴竖直，安全区上下让位从主轴扣除；栏位背景向上/向下补回屏幕边缘。
            double boxHeight = Math.Max(1, height - top - bottom);
            var panes = MainScreenLayout.Partition(true, width, boxHeight, fileMain, controlMain, analysisCross);
            var fileDivider = Shift(panes.FileDivider, top);
            var controlDivider = Shift(panes.ControlDivider, top);
            var controlContent = Shift(panes.ControlBar, top);
            double contentWidth = Math.Max(1, width - left - right);
            return new iOSPaneFrames(
                new LayoutRect(0, 0, width, top + panes.FileBar.Height),
                new LayoutRect(left, top, contentWidth, panes.FileBar.Height),
                fileDivider,
                Shift(panes.Photo, top),
                Shift(panes.Analysis, top),
                controlDivider,
                new LayoutRect(0, controlDivider.Y, width, Math.Max(0, height - controlDivider.Y)),
                new LayoutRect(left, controlContent.Y, contentWidth, controlContent.Height));
        }
        // 列布局：主轴水平，内容纵贯窗口上下缘（不扣上下安全区）；左右让位只计入文件栏前导，栏位背景补回屏幕边缘。
        var column = MainScreenLayout.Partition(false, width, height, fileMain, controlMain, analysisCross);
        double contentHeight = Math.Max(1, height);
        return new iOSPaneFrames(
            column.FileBar,
            new LayoutRect(left, 0, Math.Min(MainScreenLayout.FileBarContentWidth, Math.Max(0, column.FileBar.Width - left)), contentHeight),
            column.FileDivider,
            column.Photo,
            column.Analysis,
            column.ControlDivider,
            new LayoutRect(column.ControlBar.X, 0, Math.Max(0, width - column.ControlBar.X), height),
            new LayoutRect(column.ControlBar.X, 0, column.ControlBar.Width, contentHeight));
    }

    /// <summary>沿主轴平移矩形（行布局把安全区顶边让位补回主轴坐标）。</summary>
    /// <param name="rect">原始矩形。</param>
    /// <param name="offset">主轴偏移。</param>
    /// <returns>平移后的矩形。</returns>
    private static LayoutRect Shift(LayoutRect rect, double offset) => new(rect.X, rect.Y + offset, rect.Width, rect.Height);
}
