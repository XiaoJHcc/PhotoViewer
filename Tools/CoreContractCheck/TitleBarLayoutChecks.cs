using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// SHELL-02：行布局筛选挖空与窗口按钮相交时常驻通栏、挖空与背景的三态规则。
/// </summary>
internal static class TitleBarLayoutChecks
{
    public static void Run(Checks c)
    {
        c.Section("标题栏常驻与挖空");
        c.True("按钮与筛选挖空相交时常驻通栏",
            TitleBarLayout.IsPersistent(rowLayout: true, fileBarVisible: true, fullScreen: false,
                cutoutLeft: 100, cutoutRight: 240, controlsLeft: 50, controlsRight: 150));
        c.True("只贴边不算相交",
            !TitleBarLayout.IsPersistent(rowLayout: true, fileBarVisible: true, fullScreen: false,
                cutoutLeft: 150, cutoutRight: 240, controlsLeft: 50, controlsRight: 150));
        c.True("按钮完全在挖空左侧不影响",
            !TitleBarLayout.IsPersistent(rowLayout: true, fileBarVisible: true, fullScreen: false,
                cutoutLeft: 300, cutoutRight: 400, controlsLeft: 0, controlsRight: 200));
        c.True("系统全屏不建自绘标题栏",
            !TitleBarLayout.IsPersistent(rowLayout: true, fileBarVisible: true, fullScreen: true,
                cutoutLeft: 100, cutoutRight: 240, controlsLeft: 50, controlsRight: 150));
        c.True("列布局不常驻",
            !TitleBarLayout.IsPersistent(rowLayout: false, fileBarVisible: true, fullScreen: false,
                cutoutLeft: 100, cutoutRight: 240, controlsLeft: 50, controlsRight: 150));
        c.True("文件栏隐藏不常驻",
            !TitleBarLayout.IsPersistent(rowLayout: true, fileBarVisible: false, fullScreen: false,
                cutoutLeft: 100, cutoutRight: 240, controlsLeft: 50, controlsRight: 150));

        c.True("行布局非常驻时挖空筛选控件",
            TitleBarLayout.HasCutout(rowLayout: true, fileBarVisible: true, persistent: false));
        c.True("常驻通栏不挖空",
            !TitleBarLayout.HasCutout(rowLayout: true, fileBarVisible: true, persistent: true));
        c.True("列布局不挖空",
            !TitleBarLayout.HasCutout(rowLayout: false, fileBarVisible: true, persistent: false));

        c.True("常驻通栏铺背景",
            TitleBarLayout.HasBackdrop(rowLayout: true, fileBarVisible: true, persistent: true));
        c.True("行布局挖空时不铺背景",
            !TitleBarLayout.HasBackdrop(rowLayout: true, fileBarVisible: true, persistent: false));
        c.True("列布局铺背景",
            TitleBarLayout.HasBackdrop(rowLayout: false, fileBarVisible: true, persistent: false));
        c.True("文件栏隐藏铺背景",
            TitleBarLayout.HasBackdrop(rowLayout: true, fileBarVisible: false, persistent: false));

        double aiFilterHeight = MainScreenLayout.FilterBarMainLength(false, 42, includeSimilarityToggle: true);
        double standardFilterHeight = MainScreenLayout.FilterBarMainLength(false, 42, includeSimilarityToggle: false);
        c.Equal("标准版列布局移除相似开关高度", MainScreenLayout.FilterGap + MainScreenLayout.FilterToggleHeight,
            aiFilterHeight - standardFilterHeight);
        c.Equal("行布局筛选高度不随版本变化",
            MainScreenLayout.FilterBarMainLength(true, 42, includeSimilarityToggle: true),
            MainScreenLayout.FilterBarMainLength(true, 42, includeSimilarityToggle: false));
        var standardFrames = MainScreenLayout.FilterBarFrames(true, 500, 0, 52, includeSimilarityToggle: false);
        c.Equal("标准版四项筛选整组居中", 250,
            (standardFrames.Sort.X + standardFrames.Count.MaxX) / 2);
    }
}
