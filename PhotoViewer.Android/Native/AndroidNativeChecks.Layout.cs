using System;
using System.Text;
using System.Threading.Tasks;
using AndroidX.Core.View;
using PhotoViewer.Core.Design;
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

        var previousOrientation = activity.RequestedOrientation;
        activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
        await WaitAsync(() => root.Height > root.Width, "竖屏布局", 8000);
        await Task.Delay(500);

        var config = activity.Resources!.Configuration!;
        double density = AndroidUiTheme.Density(activity);
        Check("layout 配置", true,
            $"fontScale={config.FontScale} density={density:0.###} px={root.Width}x{root.Height} "
            + $"dp={root.Width / density:0.#}x{root.Height / density:0.#} orientation={config.Orientation} "
            + $"row={main.IsRowLayout} landscape={main.IsScreenLandscape} mode={main.Settings.LayoutMode} "
            + $"safe={root.SafeInsets}");

        CheckRootForm(root, "竖屏");
        CheckGeometry(root, main, density, "竖屏");
        CheckSafeInsets(root, landscape: root.Width > root.Height, "竖屏");
        await CheckZoomCenterOnPaneToggleAsync();
        CheckSyntheticInsetDerivation();
        bool navigationBaseline = NavigationBarVisible(activity);

        try
        {
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Landscape;
            await WaitAsync(() => root.Width > root.Height, "横屏布局", 8000);
            await Task.Delay(500);
            bool expectedLandscapeRow = ExpectedRowLayout(main, landscape: true);
            Check("layout 横屏方向与布局模式",
                main.IsScreenLandscape && (root.Form != ScreenForm.Wide || main.IsRowLayout == expectedLandscapeRow),
                $"landscape={main.IsScreenLandscape} row={main.IsRowLayout} expectedRow={expectedLandscapeRow} "
                + $"form={root.Form} mode={main.Settings.LayoutMode} px={root.Width}x{root.Height}");
            Check("layout 横屏隐藏系统状态栏", !nativeActivity.IsStatusBarVisibleForChecks(),
                $"statusVisible={nativeActivity.IsStatusBarVisibleForChecks()} safe={root.SafeInsets}");
            // 只忽略手势导航条占位，不隐藏导航栏。
            Check("layout 横屏不隐藏导航栏", NavigationBarVisible(activity) == navigationBaseline,
                $"portrait={navigationBaseline} landscape={NavigationBarVisible(activity)} safe={root.SafeInsets}");
            CheckRootForm(root, "横屏");
            CheckGeometry(root, main, density, "横屏");
            CheckSafeInsets(root, landscape: true, "横屏");
            if (root.Form == ScreenForm.Wide) CheckLandscapeControlReachesBottom(root, density, main);

            // FILTER-07：横屏刘海换到另一侧时，筛选/更多合体随真实槽位镜像，装饰不越出胶囊。
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.ReverseLandscape;
            var reversedDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < reversedDeadline && !(root.Width > root.Height && !root.NotchOnLeft))
                await Task.Delay(50);
            bool reversed = root.Width > root.Height && !root.NotchOnLeft;
            bool mirrorProbe = root.WindowInsetsForChecks.CutoutCenterX != null
                || Math.Abs(root.SafeInsets.Left - root.SafeInsets.Right) > .5;
            if (!mirrorProbe)
            {
                Check("layout 横屏刘海右布局（跳过）", true,
                    $"无显示挖孔且左右安全区对称，镜像方向不可判定 safe={root.SafeInsets}");
            }
            else if (reversed)
            {
                await Task.Delay(500);
                CheckRootForm(root, "横屏刘海右");
                CheckGeometry(root, main, density, "横屏刘海右");
                CheckSafeInsets(root, landscape: true, "横屏刘海右");
            }
            else
            {
                // 该设备不提供反向刘海（模拟器无显示挖孔或忽略反向方向）：用同一合体控件的镜像排布核验分段交换。
                Check("layout 横屏刘海右布局（设备无反向刘海，按合成合体核验）", CheckComboMirror(),
                    $"notchOnLeft={root.NotchOnLeft} px={root.Width}x{root.Height} safe={root.SafeInsets}");
            }

            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
            await WaitAsync(() => root.Height > root.Width, "竖屏布局", 8000);
            await Task.Delay(500);
            Check("layout 回竖屏恢复系统状态栏", nativeActivity.IsStatusBarVisibleForChecks(),
                $"statusVisible={nativeActivity.IsStatusBarVisibleForChecks()} safe={root.SafeInsets}");
            Check("layout 回竖屏导航栏可见性不变", NavigationBarVisible(activity) == navigationBaseline,
                $"portrait={navigationBaseline} back={NavigationBarVisible(activity)} safe={root.SafeInsets}");
            bool expectedPortraitRow = ExpectedRowLayout(main, landscape: false);
            Check("layout 竖屏方向与布局模式",
                !main.IsScreenLandscape && (root.Form != ScreenForm.Wide || main.IsRowLayout == expectedPortraitRow),
                $"landscape={main.IsScreenLandscape} row={main.IsRowLayout} expectedRow={expectedPortraitRow} "
                + $"form={root.Form} mode={main.Settings.LayoutMode} px={root.Width}x{root.Height}");
            CheckRootForm(root, "回竖屏");
            CheckGeometry(root, main, density, "回竖屏");
            CheckSafeInsets(root, landscape: false, "回竖屏");
            await RunToggleMatrixAsync(root, main);
        }
        finally
        {
            activity.RequestedOrientation = previousOrientation;
            await Task.Delay(400);
        }
    }

    /// <summary>放大态切换胶片/控制栏：视口中心归一化点与物理倍率保持不变（不能只比适应区内边距）。</summary>
    private static async Task CheckZoomCenterOnPaneToggleAsync()
    {
        var root = _root!;
        var main = _main!;
        root.Photo.ZoomToPhysical(2);
        await WaitAsync(() => !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 2) < .05, "放大到物理 200%", 8000);
        await Task.Delay(250);
        var before = root.Photo.Viewport;
        var center = before.CenterUV;
        double physical = before.PhysicalScale;
        bool film = main.IsThumbnailViewVisible;
        bool control = main.IsControlViewVisible;
        try
        {
            main.IsThumbnailViewVisible = false;
            await WaitLayoutAsync(root);
            await Task.Delay(250);
            var afterFilm = root.Photo.Viewport;
            Check("layout 放大后隐藏胶片保持中心/倍率",
                Near(afterFilm.CenterUV.U, center.U) && Near(afterFilm.CenterUV.V, center.V)
                && Math.Abs(afterFilm.PhysicalScale - physical) < .01,
                $"center={center.U:0.####},{center.V:0.####}->{afterFilm.CenterUV.U:0.####},{afterFilm.CenterUV.V:0.####} "
                + $"physical={physical:0.###}->{afterFilm.PhysicalScale:0.###}");

            main.IsThumbnailViewVisible = film;
            await WaitLayoutAsync(root);
            main.IsControlViewVisible = false;
            await WaitLayoutAsync(root);
            await Task.Delay(250);
            var afterControl = root.Photo.Viewport;
            Check("layout 放大后隐藏控制栏保持中心/倍率",
                Near(afterControl.CenterUV.U, center.U) && Near(afterControl.CenterUV.V, center.V)
                && Math.Abs(afterControl.PhysicalScale - physical) < .01,
                $"center={afterControl.CenterUV.U:0.####},{afterControl.CenterUV.V:0.####} "
                + $"physical={afterControl.PhysicalScale:0.###}");
        }
        finally
        {
            main.IsThumbnailViewVisible = film;
            main.IsControlViewVisible = control;
            await WaitLayoutAsync(root);
            root.Photo.FitToView();
            await Task.Delay(250);
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

        // SHELL-10：圆角避让线让元素半圆端恰好内切屏幕圆弧。取大圆角真机量级的合成半径，
        // 逐个近缘距离核对：有让位时端头圆心到圆角圆心的距离等于 r − ρ；无让位时端头圆心已离开圆角方块。
        const double screenRadius = 56.7, shapeRadius = 22;
        var samples = new List<string>();
        bool tangent = true;
        foreach (double edge in new[] { 0d, 10, 16, 24, 34.7, 40 })
        {
            double inset = AndroidWindowInsets.CornerInset(screenRadius, edge, shapeRadius);
            double span = screenRadius - shapeRadius;
            bool ok = inset > 0
                ? Math.Abs(Math.Sqrt(Math.Pow(span - inset, 2) + Math.Pow(span - edge, 2)) - span) < .01
                : edge >= span - .01;
            tangent &= ok;
            samples.Add($"y={edge:0.#}→{inset:0.##}{(ok ? "" : "✗")}");
        }
        bool degenerate = Near(AndroidWindowInsets.CornerInset(18, 0, shapeRadius), 0)
            && Near(AndroidWindowInsets.CornerInset(screenRadius, 0, 0), screenRadius);
        Check("layout 圆角避让线端头内切（合成半径）", tangent && degenerate,
            $"r={screenRadius} ρ={shapeRadius} {string.Join(' ', samples)} 小圆角无让位/矩形贴角让满={degenerate}");
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
        // 形态无关的共享几何对照（宽形态分区/筛选/控制栏/卡片，手机形态槽位/胶片档位/底行）。
        CheckMainScreenGeometry(root, main, density, phase);

        if (root.Form == ScreenForm.Wide)
        {
            var panes = root.Panes;
            bool row = main.IsRowLayout;
            var filter = root.FilterBar;
            var content = panes.FileContent;
            var filterTail = AndroidFilterBar.ShowsSimilarityToggle ? filter.SimilarityFrame : filter.ControlFrame;
            bool filterInside = root.FilterScrollableForChecks
                ? filter.ControlFrame.X >= -.6
                : filter.ControlFrame.X >= -.6 && filterTail.MaxX <= content.Width + .6;
            Check($"{phase} 筛选条在文件栏内容区",
                filterInside && Near(filter.ControlFrame.Height, MobileFilterLayout.Segment),
                $"control={filter.ControlFrame} tail={filterTail} content={content} row={row} "
                + $"scrollable={root.FilterScrollableForChecks}");
            // FILTER-07：只有整组自然宽没有溢出安全内容区时才要求居中；窄屏从前导 filter.margin 起排。
            if (row && !root.FilterScrollableForChecks)
                Check($"{phase} 行布局筛选整组居中",
                    Near((filter.ControlFrame.X + filterTail.MaxX) / 2, content.Width / 2),
                    $"center={(filter.ControlFrame.X + filterTail.MaxX) / 2:0.#} expected={content.Width / 2:0.#}");

            var nodes = root.ControlBar.NodesForChecks();
            var rootLocation = new int[2];
            var barLocation = new int[2];
            root.GetLocationInWindow(rootLocation);
            root.ControlBar.GetLocationInWindow(barLocation);
            double offsetX = (barLocation[0] - rootLocation[0]) / density;
            double offsetY = (barLocation[1] - rootLocation[1]) / density;
            double barMain = row ? root.ControlBar.Width / density : root.ControlBar.Height / density;
            double visibleMain = row ? panes.ControlContent.Width : panes.ControlContent.Height;
            bool overflowMain = barMain > visibleMain + .5;
            bool nodesOk = nodes.Count > 0;
            foreach (var node in nodes)
            {
                if (node.Width <= 0 || node.Height <= 0) { nodesOk = false; continue; }
                double left = offsetX + node.X, top = offsetY + node.Y;
                bool nodeInside = row
                    ? top >= panes.ControlBackground.Y - 1 && top + node.Height <= panes.ControlBackground.MaxY + 1
                        && (overflowMain || left >= panes.ControlBackground.X - 1 && left + node.Width <= panes.ControlBackground.MaxX + 1)
                    : left >= panes.ControlBackground.X - 1 && left + node.Width <= panes.ControlBackground.MaxX + 1
                        && (overflowMain || top >= panes.ControlBackground.Y - 1 && top + node.Height <= panes.ControlBackground.MaxY + 1);
                if (!nodeInside) nodesOk = false;
            }
            Check($"{phase} 控制栏分组与节点几何", root.ControlBar.GroupCount is >= 1 and <= 3 && nodesOk,
                $"groups={root.ControlBar.GroupCount} nodes={nodes.Count} docMain={barMain:0.#} visible={visibleMain:0.#} "
                + $"scroll={overflowMain} background={panes.ControlBackground}");
        }
    }

    /// <summary>镜像排布：合体容器内前段落在右半，两段都不越出胶囊。</summary>
    private static bool CheckComboMirror()
    {
        var activity = _activity!;
        double density = AndroidUiTheme.Density(activity);
        var combo = new AndroidGlassCombo(activity, AndroidIcons.Filter, "筛选", AndroidIcons.More, "更多");
        try
        {
            int width = (int)Math.Round(AndroidGlassCombo.Size * density);
            int height = (int)Math.Round(MobileFilterLayout.Segment * density);
            combo.SetMirrored(true);
            combo.Measure(global::Android.Views.View.MeasureSpec.MakeMeasureSpec(width, global::Android.Views.MeasureSpecMode.Exactly),
                global::Android.Views.View.MeasureSpec.MakeMeasureSpec(height, global::Android.Views.MeasureSpecMode.Exactly));
            combo.Layout(0, 0, width, height);
            return combo.First.Left == width / 2 && combo.First.Right == width
                && combo.Second.Left == 0 && combo.Second.Right == width / 2
                && combo.First.Top == 0 && combo.First.Bottom == height
                && combo.Second.Top == 0 && combo.Second.Bottom == height;
        }
        finally
        {
            combo.Dispose();
        }
    }

    /// <summary>当前形态按原生尺寸类别与用户模式重判，旋转后也必须一致。</summary>
    private static void CheckRootForm(AndroidNativeRootView root, string phase)
    {
        var metrics = AndroidScreenForm.Read(_activity!);
        var expected = ScreenFormResolver.Resolve(metrics.HorizontalCompact, metrics.VerticalCompact,
            root.Width / AndroidUiTheme.Density(_activity!), root.Height / AndroidUiTheme.Density(_activity!),
            metrics.PhoneDevice, _main!.Settings.LayoutMode, LayoutCapabilities.PhoneLayoutSupported);
        Check($"{phase} 形态判定", root.Form == expected,
            $"form={root.Form} expected={expected} classes={metrics.SizeClassText} phone={metrics.PhoneDevice} "
            + $"window={metrics.WidthDp:0.#}x{metrics.HeightDp:0.#} cutouts={root.WindowInsetsForChecks.DescribeCutouts()} "
            + $"corners={root.WindowInsetsForChecks.Corners}");
    }
}
