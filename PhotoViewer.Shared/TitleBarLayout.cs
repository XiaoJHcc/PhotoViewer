namespace PhotoViewer.Shared;

/// <summary>
/// SHELL-02 标题栏的平台中立判定：行布局中筛选挖空与窗口按钮相交时常驻通栏，
/// 其余行布局挖空筛选控件，列布局与文件栏隐藏时铺背景。
/// 各端只负责给出自己坐标空间里的挖空与按钮区间，标题最小宽等平台差异留在 head。
/// </summary>
public static class TitleBarLayout
{
    /// <summary>筛选挖空与窗口按钮的水平区间是否真实相交（贴边不算）。</summary>
    /// <param name="rowLayout">是否行布局（栏位上下排布）。</param>
    /// <param name="fileBarVisible">文件栏（胶片栏）是否可见。</param>
    /// <param name="fullScreen">是否系统全屏（全屏没有自绘标题栏）。</param>
    /// <param name="cutoutLeft">筛选挖空左缘。</param>
    /// <param name="cutoutRight">筛选挖空右缘。</param>
    /// <param name="controlsLeft">窗口按钮组左缘。</param>
    /// <param name="controlsRight">窗口按钮组右缘。</param>
    /// <returns>应改为文件栏上方常驻通栏时返回 true。</returns>
    public static bool IsPersistent(bool rowLayout, bool fileBarVisible, bool fullScreen,
        double cutoutLeft, double cutoutRight, double controlsLeft, double controlsRight)
        => rowLayout && fileBarVisible && !fullScreen
            && controlsRight > cutoutLeft && controlsLeft < cutoutRight;

    /// <summary>行布局且非常驻时标题栏为筛选控件让开挖空。</summary>
    /// <param name="rowLayout">是否行布局。</param>
    /// <param name="fileBarVisible">文件栏是否可见。</param>
    /// <param name="persistent">是否常驻通栏。</param>
    /// <returns>存在挖空时返回 true。</returns>
    public static bool HasCutout(bool rowLayout, bool fileBarVisible, bool persistent)
        => rowLayout && fileBarVisible && !persistent;

    /// <summary>标题栏是否绘制背景：常驻通栏、列布局或文件栏隐藏时为通栏带。</summary>
    /// <param name="rowLayout">是否行布局。</param>
    /// <param name="fileBarVisible">文件栏是否可见。</param>
    /// <param name="persistent">是否常驻通栏。</param>
    /// <returns>应显示标题栏背景时返回 true。</returns>
    public static bool HasBackdrop(bool rowLayout, bool fileBarVisible, bool persistent)
        => persistent || !(rowLayout && fileBarVisible);
}
