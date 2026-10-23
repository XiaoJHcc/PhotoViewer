using System;
using System.Text;
using System.Threading.Tasks;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check layout</c>（验收专用）：记录当前字体档/密度/窗口尺寸，并在竖屏、横屏、回竖屏三种状态下
/// 复核分区（非负、在屏内、边界相接）、筛选条、控制栏节点几何与首卡尺寸；横竖屏状态与布局模式按设置对拍。
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
            CheckGeometry(root, main, density, "横屏");

            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
            await WaitAsync(() => root.Height > root.Width, "竖屏布局", 8000);
            await Task.Delay(500);
            bool expectedPortraitRow = ExpectedRowLayout(main, landscape: false);
            Check("layout 竖屏方向与布局模式",
                !main.IsScreenLandscape && main.IsRowLayout == expectedPortraitRow,
                $"landscape={main.IsScreenLandscape} row={main.IsRowLayout} expectedRow={expectedPortraitRow} "
                + $"mode={main.Settings.LayoutMode} px={root.Width}x{root.Height}");
            CheckGeometry(root, main, density, "回竖屏");
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
        bool filterInside = layoutRow
            ? filter.SortFrame.X >= -.6 && filter.SimilarityFrame.MaxX <= content.Width + .6
            : filter.SortFrame.Y >= -.6 && filter.SimilarityFrame.MaxY <= content.Height + .6;
        // 窄屏：控件宽度不压缩，由筛选条滚动宿主承载，要求自然宽度确实超出且可滚。
        bool filterScrolls = layoutRow && !filterInside
            && filter.RequiredWidthDp > content.Width + .6 && root.FilterScrollableForChecks;
        filterInside |= filterScrolls;
        Check($"{phase} 筛选条在文件栏内容区", filterInside,
            $"sort={filter.SortFrame} toggle={filter.SimilarityFrame} content={content} "
            + $"required={filter.RequiredWidthDp:0.#} scroll={filterScrolls} horizontal={filter.Horizontal}");

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
