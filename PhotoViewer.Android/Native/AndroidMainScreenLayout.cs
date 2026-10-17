using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>Android 安全区（dp）：状态栏、导航栏/手势条与横屏刘海。</summary>
/// <param name="Left">左内边距。</param>
/// <param name="Top">上内边距。</param>
/// <param name="Right">右内边距。</param>
/// <param name="Bottom">下内边距。</param>
internal readonly record struct AndroidInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Android 主屏分区（A3）：与 iOS <c>iOSMainScreenLayout</c> 同一套安全区口径——
/// 栏位背景铺满屏幕边缘，内容收在安全区内，主图满幅；行布局栏位在上下、列布局在左右。
/// </summary>
/// <param name="FileBar">文件栏背景分区。</param>
/// <param name="FileContent">文件栏内容区（筛选条与胶片栏可用范围）。</param>
/// <param name="FileDivider">文件栏与主图之间的分隔线。</param>
/// <param name="Photo">主图分区。</param>
/// <param name="Analysis">分析栏分区（交叉轴 0 表示未显示）。</param>
/// <param name="ControlDivider">控制栏与主图之间的分隔线。</param>
/// <param name="ControlBackground">控制栏背景分区（铺到屏幕边缘）。</param>
/// <param name="ControlContent">控制栏内容区。</param>
internal readonly record struct AndroidPaneFrames(LayoutRect FileBar, LayoutRect FileContent, LayoutRect FileDivider, LayoutRect Photo,
    LayoutRect Analysis, LayoutRect ControlDivider, LayoutRect ControlBackground, LayoutRect ControlContent);

/// <summary>Android 主屏分区计算（纯函数，便于程序化对拍）。</summary>
internal static class AndroidMainScreenLayout
{
    /// <summary>计算两向分区：行布局栏位在上下、列布局在左右，隐藏的栏位压成零并把空间让给主图。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="width">屏幕宽度（dp）。</param>
    /// <param name="height">屏幕高度（dp）。</param>
    /// <param name="safe">安全区（dp）。</param>
    /// <param name="fileMain">文件栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="controlMain">控制栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="analysisCross">分析栏交叉轴长度（0 表示未显示）。</param>
    /// <returns>八个分区矩形。</returns>
    internal static AndroidPaneFrames Compute(bool horizontal, double width, double height, AndroidInsets safe,
        double fileMain, double controlMain, double analysisCross)
    {
        double left = safe.Left, right = safe.Right, top = safe.Top, bottom = safe.Bottom;
        if (horizontal)
        {
            double boxHeight = System.Math.Max(1, height - top - bottom);
            var panes = MainScreenLayout.Partition(true, width, boxHeight, fileMain, controlMain, analysisCross);
            var fileDivider = Shift(panes.FileDivider, top);
            var controlDivider = Shift(panes.ControlDivider, top);
            var controlContent = Shift(panes.ControlBar, top);
            double contentWidth = System.Math.Max(1, width - left - right);
            return new AndroidPaneFrames(
                new LayoutRect(0, 0, width, top + panes.FileBar.Height),
                new LayoutRect(left, top, contentWidth, panes.FileBar.Height),
                fileDivider,
                Shift(panes.Photo, top),
                Shift(panes.Analysis, top),
                controlDivider,
                new LayoutRect(0, controlDivider.Y, width, System.Math.Max(0, height - controlDivider.Y)),
                new LayoutRect(left, controlContent.Y, contentWidth, controlContent.Height));
        }
        var column = MainScreenLayout.Partition(false, width, height, fileMain, controlMain, analysisCross);
        double contentHeight = System.Math.Max(1, height - top - bottom);
        return new AndroidPaneFrames(
            column.FileBar,
            new LayoutRect(left, top, System.Math.Min(MainScreenLayout.FileBarContentWidth, System.Math.Max(0, column.FileBar.Width - left)), contentHeight),
            column.FileDivider,
            column.Photo,
            column.Analysis,
            column.ControlDivider,
            new LayoutRect(column.ControlBar.X, 0, System.Math.Max(0, width - column.ControlBar.X), height),
            new LayoutRect(column.ControlBar.X, top, column.ControlBar.Width, contentHeight));
    }

    /// <summary>沿主轴平移矩形。</summary>
    /// <param name="rect">原始矩形。</param>
    /// <param name="offset">主轴偏移。</param>
    /// <returns>平移后的矩形。</returns>
    private static LayoutRect Shift(LayoutRect rect, double offset) => new(rect.X, rect.Y + offset, rect.Width, rect.Height);
}
