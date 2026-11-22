using System;
using System.Collections.Generic;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>Android 布局安全区（dp）：系统栏与刘海并集，横屏不含手势导航条底部。</summary>
/// <param name="Left">左内边距。</param>
/// <param name="Top">上内边距。</param>
/// <param name="Right">右内边距。</param>
/// <param name="Bottom">下内边距。</param>
internal readonly record struct AndroidInsets(double Left, double Top, double Right, double Bottom);

/// <summary>窗口内的显示挖孔矩形（dp，窗口坐标）；API 29+ 才有内容，低版本为空。</summary>
/// <param name="Left">左缘。</param>
/// <param name="Top">上缘。</param>
/// <param name="Right">右缘。</param>
/// <param name="Bottom">下缘。</param>
internal readonly record struct AndroidCutoutRect(double Left, double Top, double Right, double Bottom)
{
    /// <summary>矩形中心横坐标。</summary>
    internal double CenterX => (Left + Right) / 2;
    /// <summary>矩形宽。</summary>
    internal double Width => Right - Left;
    /// <summary>矩形高。</summary>
    internal double Height => Bottom - Top;
}

/// <summary>
/// 屏幕圆角读数（dp）：<see cref="Android.Views.WindowInsets"/> 的 <c>RoundedCorner</c>（API 31+）给出圆心与半径，
/// 这里只保留四角半径；低版本与无圆角的窗口（如多数模拟器）全部为 0，几何因此不产生额外避让。
/// </summary>
/// <param name="TopLeft">左上角半径。</param>
/// <param name="TopRight">右上角半径。</param>
/// <param name="BottomRight">右下角半径。</param>
/// <param name="BottomLeft">左下角半径。</param>
internal readonly record struct AndroidCornerRadii(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
{
    /// <summary>是否读到任何圆角。</summary>
    internal bool Any => TopLeft > .5 || TopRight > .5 || BottomRight > .5 || BottomLeft > .5;
}

/// <summary>
/// 窗口 Insets 原始读数（dp）：系统栏、显示挖孔、导航栏与可点元素的底部读数分开记录，
/// 另存公开 <c>DisplayCutout</c> 的矩形与 <c>RoundedCorner</c> 半径。布局安全区每次都按当前方向
/// 从这里重新派生，旋转后不沿用旧值。
/// </summary>
/// <param name="SystemBars">系统状态栏与导航栏 Insets。</param>
/// <param name="Cutout">显示挖孔 Insets。</param>
/// <param name="NavigationBottom">导航栏底部 Insets。</param>
/// <param name="TappableBottom">可点元素底部 Insets（诊断读取）。</param>
/// <param name="IsGestureNavigation">当前系统是否使用手势导航，与导航条当前可见性无关。</param>
/// <param name="NonNavigationBottom">导航栏以外的系统栏底部 Insets。</param>
/// <param name="Cutouts">显示挖孔矩形（dp，窗口坐标）；API 29 以下为空。</param>
/// <param name="Corners">屏幕四角半径（dp）；API 31 以下或窗口无圆角时为 0。</param>
internal readonly record struct AndroidWindowInsets(AndroidInsets SystemBars, AndroidInsets Cutout,
    double NavigationBottom, double TappableBottom, bool IsGestureNavigation, double NonNavigationBottom = 0,
    IReadOnlyList<AndroidCutoutRect>? Cutouts = null, AndroidCornerRadii Corners = default)
{

    /// <summary>
    /// 派生布局安全区：与系统栏、刘海取并集；横屏只忽略手势导航条底部，
    /// 三键导航遮挡与刘海照常扣除；屏幕圆角不进布局安全区，由贴角元素带单独避让。
    /// </summary>
    /// <param name="landscape">是否横屏（宽大于高）。</param>
    /// <returns>布局安全区。</returns>
    internal AndroidInsets DeriveSafe(bool landscape)
    {
        double bottom = Math.Max(landscape && IsGestureNavigation ? NonNavigationBottom : SystemBars.Bottom,
            Cutout.Bottom);
        return new AndroidInsets(
            Math.Max(SystemBars.Left, Cutout.Left),
            Math.Max(SystemBars.Top, Cutout.Top),
            Math.Max(SystemBars.Right, Cutout.Right),
            bottom);
    }

    /// <summary>
    /// 贴角带的圆角避让线（dp，SHELL-10）：元素外形的端头是半径 ρ 的圆（44 高胶囊与圆按钮 ρ = 22，矩形 ρ = 0），
    /// 近缘距相邻窗口缘 y 时，求端头圆恰好内切屏幕圆弧（圆心距窗口角 r、半径 r）时元素近端到另一窗口缘的距离：
    /// <c>(r − ρ) − √((r − ρ)² − (r − ρ − y)²)</c>；端头圆心已离开圆角方块（y ≥ r − ρ）时为 0。
    /// 半径取公开 <c>RoundedCorner</c> 读数，圆角为 0 的平台不产生位移。这条线与系统安全区并列取较大者，
    /// 该带的边距再从较大者量起，两者不相加。
    /// </summary>
    /// <param name="radius">屏幕圆角半径（dp）。</param>
    /// <param name="edgeDistance">元素近缘到相邻窗口缘的实际距离（dp，负值按 0）。</param>
    /// <param name="shapeRadius">元素端头圆半径（dp）。</param>
    /// <returns>沿带方向元素近端不得越过的位置（距窗口缘，dp）。</returns>
    internal static double CornerInset(double radius, double edgeDistance, double shapeRadius)
    {
        double span = radius - Math.Max(0, shapeRadius);
        if (radius <= .5 || span <= 0) return 0;
        double offset = span - Math.Max(0, edgeDistance);
        if (offset <= 0) return 0;
        return Math.Max(0, span - Math.Sqrt(Math.Max(0, span * span - offset * offset)));
    }

    /// <summary>
    /// 窗口最左侧/最右侧的挖孔矩形中心：手机横屏据此判定刘海在左还是镜像（API 29+ 真实矩形）。
    /// 读不到矩形时返回 null。
    /// </summary>
    /// <returns>挖孔中心横坐标（dp）；无挖孔矩形时为 null。</returns>
    internal double? CutoutCenterX => Cutouts is { Count: > 0 } list ? list[0].CenterX : null;

    /// <summary>所有挖孔矩形的可读文本（验收报告用）。</summary>
    /// <returns>形如 <c>(0,0)-(148,32)</c> 的逗号分隔串；无挖孔时为 <c>none</c>。</returns>
    internal string DescribeCutouts()
    {
        if (Cutouts is not { Count: > 0 } list) return "none";
        var parts = new List<string>(list.Count);
        foreach (var rect in list)
            parts.Add(FormattableString.Invariant($"[{rect.Left:0.#},{rect.Top:0.#} {rect.Width:0.#}x{rect.Height:0.#}]"));
        return string.Join(",", parts);
    }
}

/// <summary>
/// Android 宽形态主屏分区：安全区口径与共享 <see cref="ShellPaneLayout"/> 完全一致——由
/// <see cref="AndroidWindowInsets.DeriveSafe"/> 派生真实安全区后直接交给共享分区函数，
/// 栏位背景铺满屏幕边缘、内容收在安全区内；主图视图满窗口，适应区由共享分区给出。
/// </summary>
/// <param name="FileBar">文件栏背景分区。</param>
/// <param name="FileContent">文件栏内容区（筛选条与胶片栏可用范围）。</param>
/// <param name="FileDivider">文件栏与主图之间的分隔线。</param>
/// <param name="Photo">主图分区（同时是适应区）。</param>
/// <param name="Analysis">分析栏分区（交叉轴 0 表示未显示）。</param>
/// <param name="ControlDivider">控制栏与主图之间的分隔线。</param>
/// <param name="ControlBackground">控制栏背景分区（铺到屏幕边缘）。</param>
/// <param name="ControlContent">控制栏内容区。</param>
internal readonly record struct AndroidPaneFrames(LayoutRect FileBar, LayoutRect FileContent, LayoutRect FileDivider, LayoutRect Photo,
    LayoutRect Analysis, LayoutRect ControlDivider, LayoutRect ControlBackground, LayoutRect ControlContent);

/// <summary>Android 宽形态分区入口：把真实安全区交给共享 <see cref="ShellPaneLayout.Partition"/>。</summary>
internal static class AndroidMainScreenLayout
{
    /// <summary>计算两向分区：行布局栏位在上下、列布局在左右，隐藏的栏位压成零并把空间让给主图。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="width">屏幕宽度（dp）。</param>
    /// <param name="height">屏幕高度（dp）。</param>
    /// <param name="safe">布局安全区（dp，横屏已去掉手势导航条底部）。</param>
    /// <param name="fileMain">文件栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="controlMain">控制栏主轴长度（含分隔线；0 表示隐藏）。</param>
    /// <param name="analysisCross">分析栏交叉轴长度（0 表示未显示）。</param>
    /// <param name="fileContentX">列布局文件栏内容起点（dp，默认安全区前导）。</param>
    /// <param name="fileContentWidth">列布局文件栏内容宽度（dp，默认卡片带厚）。</param>
    /// <returns>八个分区矩形。</returns>
    internal static AndroidPaneFrames Compute(bool horizontal, double width, double height, AndroidInsets safe,
        double fileMain, double controlMain, double analysisCross, double? fileContentX = null,
        double? fileContentWidth = null)
    {
        var panes = ShellPaneLayout.Partition(horizontal, width, height,
            new EdgeInsets(safe.Left, safe.Top, safe.Right, safe.Bottom),
            fileMain, controlMain, analysisCross, fileContentX, fileContentWidth);
        return new AndroidPaneFrames(panes.FileBar, panes.FileContent, panes.FileDivider, panes.Photo,
            panes.Analysis, panes.ControlDivider, panes.ControlBackground, panes.ControlContent);
    }
}
