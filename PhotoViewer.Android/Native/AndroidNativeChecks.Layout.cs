using System;
using System.Text;
using System.Threading.Tasks;
using AndroidX.Core.View;
using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check layout</c>（验收专用）：记录当前字体档/密度/窗口尺寸，并在竖屏、横屏、回竖屏三种状态下
/// 复核分区（非负、在屏内、边界相接）、筛选条、控制栏节点几何与首卡尺寸；横竖屏状态与布局模式按设置对拍，
/// 安全区按原始 Insets 逐向重算（横屏忽略手势导航条底部、保留三键导航与刘海遮挡、不隐藏导航栏）。
/// 只读当前配置——字体档与密度由外部（<c>adb shell settings put system font_scale</c> / <c>wm density</c>）改档后重启再跑。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>布局模式入口：竖屏几何 → 横屏几何 → 回竖屏几何。</summary>
    private static async Task RunLayoutChecksAsync()
    {
        var root = _root!;
        var main = _main!;
        var activity = _activity!;
        var nativeActivity = (AndroidNativeActivity)activity;
        await WaitForScreenAsync();
        string folder = EnsureLayoutFolder(activity);
        await OpenFolderAndWaitAsync(folder);
        await Task.Delay(400);

        var config = activity.Resources!.Configuration!;
        double density = AndroidUiTheme.Density(activity);
        Check("layout 配置", true,
            $"fontScale={config.FontScale} density={density:0.###} px={root.Width}x{root.Height} "
            + $"dp={root.Width / density:0.#}x{root.Height / density:0.#} orientation={config.Orientation} "
            + $"row={main.IsRowLayout} landscape={main.IsScreenLandscape} mode={main.Settings.LayoutMode} "
            + $"safe={root.SafeInsets}");

        CheckGeometry(root, main, density, "竖屏");
        CheckSafeInsets(root, landscape: false, "竖屏");
        CheckSyntheticInsetDerivation();
        bool navigationBaseline = NavigationBarVisible(activity);

        var previousOrientation = activity.RequestedOrientation;
        try
        {
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Landscape;
            await WaitAsync(() => root.Width > root.Height, "横屏布局", 8000);
            await Task.Delay(500);
            bool expectedLandscapeRow = ExpectedRowLayout(main, landscape: true);
            Check("layout 横屏方向与布局模式",
                main.IsScreenLandscape && main.IsRowLayout == expectedLandscapeRow,
                $"landscape={main.IsScreenLandscape} row={main.IsRowLayout} expectedRow={expectedLandscapeRow} "
                + $"mode={main.Settings.LayoutMode} px={root.Width}x{root.Height}");
            Check("layout 横屏隐藏系统状态栏", !nativeActivity.IsStatusBarVisibleForChecks(),
                $"statusVisible={nativeActivity.IsStatusBarVisibleForChecks()} safe={root.SafeInsets}");
            // 只忽略手势导航条占位，不隐藏导航栏。
            Check("layout 横屏不隐藏导航栏", NavigationBarVisible(activity) == navigationBaseline,
                $"portrait={navigationBaseline} landscape={NavigationBarVisible(activity)} safe={root.SafeInsets}");
            CheckGeometry(root, main, density, "横屏");
            CheckSafeInsets(root, landscape: true, "横屏");
            CheckLandscapeControlReachesBottom(root, density, main);

            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
            await WaitAsync(() => root.Height > root.Width, "竖屏布局", 8000);
            await Task.Delay(500);
            Check("layout 回竖屏恢复系统状态栏", nativeActivity.IsStatusBarVisibleForChecks(),
                $"statusVisible={nativeActivity.IsStatusBarVisibleForChecks()} safe={root.SafeInsets}");
            Check("layout 回竖屏导航栏可见性不变", NavigationBarVisible(activity) == navigationBaseline,
                $"portrait={navigationBaseline} back={NavigationBarVisible(activity)} safe={root.SafeInsets}");
            bool expectedPortraitRow = ExpectedRowLayout(main, landscape: false);
            Check("layout 竖屏方向与布局模式",
                !main.IsScreenLandscape && main.IsRowLayout == expectedPortraitRow,
                $"landscape={main.IsScreenLandscape} row={main.IsRowLayout} expectedRow={expectedPortraitRow} "
                + $"mode={main.Settings.LayoutMode} px={root.Width}x{root.Height}");
            CheckGeometry(root, main, density, "回竖屏");
            CheckSafeInsets(root, landscape: false, "回竖屏");
        }
        finally
        {
            activity.RequestedOrientation = previousOrientation;
            await Task.Delay(400);
        }
    }

    /// <summary>按当前设置与屏幕方向给出期望布局（自动档：竖屏上下、横屏左右）。</summary>
    /// <param name="main">主 VM（读设置）。</param>
    /// <param name="landscape">是否横屏。</param>
    /// <returns>期望是否行布局。</returns>
    private static bool ExpectedRowLayout(MainViewModel main, bool landscape) => main.Settings.LayoutMode switch
    {
        LayoutMode.Row => true,
        LayoutMode.Col => false,
        _ => !landscape,
    };

    /// <summary>
    /// 安全区口径：竖屏保留底部系统栏与刘海；横屏只忽略手势导航条底部，三键导航遮挡与刘海照常扣除。
    /// 断言值每次都从当前原始 Insets 按当前方向重算，旋转后沿用旧值会在这里暴露。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="landscape">当前是否横屏。</param>
    /// <param name="phase">阶段名（竖屏/横屏/回竖屏）。</param>
    private static void CheckSafeInsets(AndroidNativeRootView root, bool landscape, string phase)
    {
        var raw = root.WindowInsetsForChecks;
        var safe = root.SafeInsets;
        var expected = raw.DeriveSafe(landscape);
        Check($"{phase} 安全区按当前方向由原始 Insets 派生",
            Near(safe.Left, expected.Left) && Near(safe.Top, expected.Top)
                && Near(safe.Right, expected.Right) && Near(safe.Bottom, expected.Bottom),
            $"safe={safe} expected={expected} landscape={landscape} gesture={raw.IsGestureNavigation} "
            + $"bars={raw.SystemBars} cutout={raw.Cutout} navB={raw.NavigationBottom:0.#} tappableB={raw.TappableBottom:0.#}");

        bool ignoreBottom = landscape && raw.IsGestureNavigation;
        double bottom = ignoreBottom ? raw.Cutout.Bottom : Math.Max(raw.SystemBars.Bottom, raw.Cutout.Bottom);
        Check(ignoreBottom ? $"{phase} 横屏忽略手势导航条底部" : $"{phase} 保留底部系统栏遮挡",
            Near(safe.Bottom, bottom),
            $"bottom={safe.Bottom:0.#} expected={bottom:0.#} systemB={raw.SystemBars.Bottom:0.#} "
            + $"cutoutB={raw.Cutout.Bottom:0.#} navB={raw.NavigationBottom:0.#} tappableB={raw.TappableBottom:0.#} "
            + $"gesture={raw.IsGestureNavigation} landscape={landscape}");
    }

    /// <summary>
    /// 派生口径的合成用例：不改设备导航模式，用三键/手势导航的 Insets 组合核对分支——
    /// 三键导航横屏保留真实遮挡，手势导航横屏只保留底部刘海，竖屏两者都与系统栏一致。
    /// </summary>
    private static void CheckSyntheticInsetDerivation()
    {
        static AndroidWindowInsets Raw(double barsBottom, double tappableBottom, double cutoutBottom = 0, double cutoutLeft = 0)
            => new(new AndroidInsets(0, 0, 0, barsBottom), new AndroidInsets(cutoutLeft, 0, 0, cutoutBottom),
                barsBottom, tappableBottom);

        var gestureLandscape = Raw(24, 0).DeriveSafe(landscape: true);
        var threeButtonLandscape = Raw(48, 48).DeriveSafe(landscape: true);
        var gestureCutoutLandscape = Raw(24, 0, cutoutBottom: 8, cutoutLeft: 51.8).DeriveSafe(landscape: true);
        var threeButtonPortrait = Raw(48, 48).DeriveSafe(landscape: false);
        Check("layout 安全区派生分支（合成 Insets）",
            Near(gestureLandscape.Bottom, 0) && Near(threeButtonLandscape.Bottom, 48)
                && Near(gestureCutoutLandscape.Bottom, 8) && Near(gestureCutoutLandscape.Left, 51.8)
                && Near(threeButtonPortrait.Bottom, 48),
            $"手势横屏B={gestureLandscape.Bottom:0.#} 三键横屏B={threeButtonLandscape.Bottom:0.#} "
            + $"手势+底部刘海横屏B={gestureCutoutLandscape.Bottom:0.#}/L={gestureCutoutLandscape.Left:0.#} "
            + $"三键竖屏B={threeButtonPortrait.Bottom:0.#}");
    }

    /// <summary>横屏忽略手势导航条后，行/列布局的控制栏内容都铺到窗口下缘（栏位背景本就铺到屏幕边缘）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="density">屏幕密度。</param>
    /// <param name="main">主 VM（读布局方向）。</param>
    private static void CheckLandscapeControlReachesBottom(AndroidNativeRootView root, double density, MainViewModel main)
    {
        var raw = root.WindowInsetsForChecks;
        if (!raw.IsGestureNavigation)
        {
            Check("layout 横屏底部口径", true,
                $"三键导航或无底部手势条：保留真实遮挡 navB={raw.NavigationBottom:0.#} "
                + $"tappableB={raw.TappableBottom:0.#} safe={root.SafeInsets}");
            return;
        }
        double heightDp = root.Height / density;
        Check("layout 横屏控制栏内容铺到窗口下缘", Near(root.Panes.ControlContent.MaxY, heightDp),
            $"contentMaxY={root.Panes.ControlContent.MaxY:0.#} screen={heightDp:0.#} row={main.IsRowLayout} safe={root.SafeInsets}");
    }

    /// <summary>导航栏是否可见：横屏只忽略手势条占位，不隐藏导航栏。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <returns>导航栏可见性。</returns>
    private static bool NavigationBarVisible(global::Android.App.Activity activity)
    {
        var insets = activity.Window?.DecorView?.RootWindowInsets;
        return insets != null
            && WindowInsetsCompat.ToWindowInsetsCompat(insets)?.IsVisible(WindowInsetsCompat.Type.NavigationBars()) == true;
    }

    /// <summary>当前方向下的分区/筛选条/控制栏/卡片几何断言（每条都带阶段前缀，便于对照三次测量）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="main">主 VM。</param>
    /// <param name="density">屏幕密度。</param>
    /// <param name="phase">阶段名（竖屏/横屏）。</param>
    private static void CheckGeometry(AndroidNativeRootView root, MainViewModel main, double density, string phase)
    {
        double widthDp = root.Width / density, heightDp = root.Height / density;
        var panes = root.Panes;
        bool layoutRow = main.IsRowLayout;

        var frames = new (string Name, LayoutRect Rect)[]
        {
            ("fileBar", panes.FileBar),
            ("fileContent", panes.FileContent),
            ("fileDivider", panes.FileDivider),
            ("photo", panes.Photo),
            ("controlDivider", panes.ControlDivider),
            ("controlBackground", panes.ControlBackground),
            ("controlContent", panes.ControlContent),
        };
        bool inside = true;
        var detail = new StringBuilder();
        foreach (var frame in frames)
        {
            detail.Append(frame.Name).Append('=').Append(frame.Rect).Append(' ');
            if (frame.Rect.X < -.6 || frame.Rect.Y < -.6
                || frame.Rect.MaxX > widthDp + .6 || frame.Rect.MaxY > heightDp + .6)
                inside = false;
        }
        Check($"{phase} 分区在屏幕内", inside,
            $"{detail.ToString().TrimEnd()} screen={widthDp:0.#}x{heightDp:0.#}dp safe={root.SafeInsets}");

        bool adjoin = layoutRow
            ? Near(panes.FileBar.MaxY, panes.FileDivider.Y) && Near(panes.FileDivider.MaxY, panes.Photo.Y)
                && Near(panes.Photo.MaxY, panes.ControlDivider.Y) && Near(panes.ControlDivider.Y, panes.ControlBackground.Y)
            // 列布局的控制栏背景从分隔线之后起排（行布局则把分隔线覆盖在背景里），两种口径都按实现核对。
            : Near(panes.FileBar.MaxX, panes.FileDivider.X) && Near(panes.FileDivider.MaxX, panes.Photo.X)
                && Near(panes.Photo.MaxX, panes.ControlDivider.X) && Near(panes.ControlDivider.MaxX, panes.ControlBackground.X);
        Check($"{phase} 分区边界相接", adjoin, layoutRow
            ? $"file={panes.FileBar.MaxY:0.#} div={panes.FileDivider.Y:0.#} photo={panes.Photo.Y:0.#}..{panes.Photo.MaxY:0.#} "
                + $"controlDiv={panes.ControlDivider.Y:0.#} controlBg={panes.ControlBackground.Y:0.#}"
            : $"file={panes.FileBar.MaxX:0.#} div={panes.FileDivider.X:0.#} photo={panes.Photo.X:0.#}..{panes.Photo.MaxX:0.#} "
                + $"controlDiv={panes.ControlDivider.X:0.#} controlBg={panes.ControlBackground.X:0.#}");

        var content = panes.FileContent;
        var filter = root.FilterBar;
        // EDITION-02：标准版末项是计数，AI 版末项是相似聚类开关。
        var filterTail = AndroidFilterBar.ShowsSimilarityToggle ? filter.SimilarityFrame : filter.CountFrame;
        bool filterInside = layoutRow
            ? filter.SortFrame.X >= -.6 && filterTail.MaxX <= content.Width + .6
            : filter.SortFrame.Y >= -.6 && filterTail.MaxY <= content.Height + .6;
        // 窄屏：控件宽度不压缩，由筛选条滚动宿主承载，要求自然宽度确实超出且可滚。
        bool filterScrolls = layoutRow && !filterInside
            && filter.RequiredWidthDp > content.Width + .6 && root.FilterScrollableForChecks;
        filterInside |= filterScrolls;
        Check($"{phase} 筛选条在文件栏内容区", filterInside,
            $"sort={filter.SortFrame} tail={filterTail} toggle={AndroidFilterBar.ShowsSimilarityToggle} content={content} "
            + $"required={filter.RequiredWidthDp:0.#} scroll={filterScrolls} horizontal={filter.Horizontal}");
        bool filterCentered = !layoutRow || filterScrolls
            || Near((filter.SortFrame.X + filterTail.MaxX) / 2, content.Width / 2);
        Check($"{phase} 行布局筛选整组居中", filterCentered,
            $"center={(filter.SortFrame.X + filterTail.MaxX) / 2:0.#} expected={content.Width / 2:0.#} scroll={filterScrolls}");
        Check($"{phase} 筛选首项仅保留普通顶部边距", Near(filter.SortFrame.Y, MainScreenLayout.FilterMargin),
            $"top={filter.SortFrame.Y:0.#} expected={MainScreenLayout.FilterMargin:0.#}");

        var nodes = root.ControlBar.NodesForChecks();
        var rootLocation = new int[2];
        var barLocation = new int[2];
        root.GetLocationInWindow(rootLocation);
        root.ControlBar.GetLocationInWindow(barLocation);
        double offsetX = (barLocation[0] - rootLocation[0]) / density;
        double offsetY = (barLocation[1] - rootLocation[1]) / density;
        // TOOLBAR-08：整条文档超出可用主轴时控制栏沿主轴滚动，节点允许超出可视区（只在滚动轴方向）。
        double barMain = layoutRow ? root.ControlBar.Width / density : root.ControlBar.Height / density;
        double visibleMain = layoutRow ? panes.ControlContent.Width : panes.ControlContent.Height;
        bool overflowMain = barMain > visibleMain + .5;
        bool nodesOk = nodes.Count > 0;
        double extentX = 0, extentY = 0;
        foreach (var node in nodes)
        {
            if (node.Width <= 0 || node.Height <= 0)
            {
                nodesOk = false;
                continue;
            }
            double left = offsetX + node.X, top = offsetY + node.Y;
            bool nodeInside = layoutRow
                ? top >= panes.ControlBackground.Y - 1 && top + node.Height <= panes.ControlBackground.MaxY + 1
                    && (overflowMain || left >= panes.ControlBackground.X - 1 && left + node.Width <= panes.ControlBackground.MaxX + 1)
                : left >= panes.ControlBackground.X - 1 && left + node.Width <= panes.ControlBackground.MaxX + 1
                    && (overflowMain || top >= panes.ControlBackground.Y - 1 && top + node.Height <= panes.ControlBackground.MaxY + 1);
            if (!nodeInside) nodesOk = false;
            extentX = Math.Max(extentX, left + node.Width);
            extentY = Math.Max(extentY, top + node.Height);
        }
        Check($"{phase} 控制栏三分组与节点几何", root.ControlBar.GroupCount == 3 && nodesOk,
            $"groups={root.ControlBar.GroupCount} nodes={nodes.Count} viewOffset=({offsetX:0.#},{offsetY:0.#}) "
            + $"extent=({extentX:0.#},{extentY:0.#}) docMain={barMain:0.#} visibleMain={visibleMain:0.#} "
            + $"scroll={overflowMain} background={panes.ControlBackground}");

        var card = root.Filmstrip.CardAt(0);
        double expectedCardWidth = layoutRow ? 96 : 116;
        Check($"{phase} 首卡尺寸", card != null
            && Math.Abs(card.Width / density - expectedCardWidth) < 1.5
            && Math.Abs(card.Height / density - 144) < 1.5,
            card == null
                ? "首卡未创建"
                : $"{card.Width / density:0.#}×{card.Height / density:0.#}dp 期望 {expectedCardWidth:0}×144dp");
    }
}
