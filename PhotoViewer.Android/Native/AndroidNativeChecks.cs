using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Google.Android.Material.BottomSheet;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using AndroidInputManager = global::Android.Hardware.Input.InputManager;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳程序化自检（A6，验收专用）：<c>--es pv-check &lt;mode&gt;</c> 在首图上屏后核对主屏分区、
/// 安全区、筛选条/卡片/控制栏几何、Fit↔100% 往返、分析栏/相似面板/设置/工具页开合，并把报告写到
/// App 内部 <c>files/PhotoViewer/android-checks-&lt;mode&gt;.txt</c>，供 <c>adb exec-out run-as ... cat</c> 取回。
/// 仅测试路径调用，不改变产品行为。
/// </summary>
/// <remarks>
/// <c>faults</c> / <c>layout</c> / <c>input</c> / <c>storage</c> / <c>filter</c> / <c>zoom</c> 是独立模式：按精确模式名分发，素材入口见模块手册
/// （<see cref="Wants"/> 不覆盖它们），不并入 <c>all</c>。
/// </remarks>
internal static partial class AndroidNativeChecks
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Lines = [];
    private static AndroidNativeRootView? _root;
    private static MainViewModel? _main;
    private static PhotoLoadSession? _session;
    private static Activity? _activity;
    private static int _attempt;
    private static string _mode = "main";

    /// <summary>按启动参数决定是否启动检查。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示会话。</param>
    /// <param name="root">主屏根视图。</param>
    /// <param name="mode">检查模式；null/空不启动。</param>
    public static void MaybeStart(Activity activity, MainViewModel main, PhotoLoadSession session, AndroidNativeRootView root, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _activity = activity;
        _main = main;
        _session = session;
        _root = root;
        _mode = mode.Trim().ToLowerInvariant();
        Lines.Clear();
        _passed = 0;
        _failed = 0;
        Console.WriteLine($"[NativeCheck] 启动检查 mode={mode}");
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(Tick, 800);
    }

    /// <summary>等待首图上屏，然后按顺序跑检查；每一步之间留出布局/动画时间。</summary>
    private static void Tick()
    {
        if (_root == null || _main == null || _session == null || _activity == null) return;
        if (IsStandaloneMode)
        {
            _ = RunAsync();
            return;
        }
        if (_session.Image == null && _main.CurrentFile != null && _attempt++ < 60)
        {
            new Handler(Looper.MainLooper!).PostDelayed(Tick, 200);
            return;
        }
        _ = RunAsync();
    }

    private static async System.Threading.Tasks.Task RunAsync()
    {
        if (IsStandaloneMode)
        {
            await RunStandaloneAsync();
            return;
        }
        var root = _root!;
        var main = _main!;
        var session = _session!;
        Phase("主屏几何");
        Check("首图上屏", session.Image != null, session.Image == null ? "无图像" : $"{session.Image.Width}x{session.Image.Height}");
        Check("安全区非负", root.SafeInsets.Left >= 0 && root.SafeInsets.Top >= 0 && root.SafeInsets.Right >= 0 && root.SafeInsets.Bottom >= 0,
            $"{root.SafeInsets}");
        var panes = root.Panes;
        bool layoutRow = main.IsRowLayout;
        double screenWidthDp = root.Width / AndroidUiTheme.Density(root.Context!);
        double screenHeightDp = root.Height / AndroidUiTheme.Density(root.Context!);
        Check("文件栏背景铺边",
            Math.Abs(panes.FileBar.X) < .5 && Math.Abs(panes.FileBar.Y) < .5
            && (layoutRow
                ? Math.Abs(panes.FileBar.Width - screenWidthDp) < .5
                : Math.Abs(panes.FileBar.Height - screenHeightDp) < .5),
            $"{panes.FileBar} row={layoutRow}");
        if (layoutRow)
        {
            Check("分区无重叠且顺序正确",
                Near(panes.FileBar.MaxY, panes.FileDivider.Y) && Near(panes.FileDivider.MaxY, panes.Photo.Y)
                && Near(panes.Photo.MaxY, panes.ControlDivider.Y) && Near(panes.ControlDivider.Y, panes.ControlBackground.Y),
                $"file={panes.FileBar.MaxY:0.#} div={panes.FileDivider.Y:0.#} photo={panes.Photo.Y:0.#}..{panes.Photo.MaxY:0.#} controlDiv={panes.ControlDivider.Y:0.#}");
        }
        else
        {
            Check("分区无重叠且顺序正确",
                Near(panes.FileBar.MaxX, panes.FileDivider.X) && Near(panes.FileDivider.MaxX, panes.Photo.X)
                && Near(panes.Photo.MaxX, panes.ControlDivider.X) && Near(panes.ControlDivider.MaxX, panes.ControlBackground.X),
                $"file={panes.FileBar.MaxX:0.#} div={panes.FileDivider.X:0.#} photo={panes.Photo.X:0.#}..{panes.Photo.MaxX:0.#} controlDiv={panes.ControlDivider.X:0.#}");
        }

        var content = panes.FileContent;
        var filter = root.FilterBar;
        Check("筛选条在文件栏内容区内",
            (layoutRow
                ? filter.SortFrame.X >= 0 && filter.SimilarityFrame.MaxX <= content.Width + .5
                : filter.SortFrame.Y >= 0 && filter.SimilarityFrame.MaxY <= content.Height + .5)
            && filter.SortFrame.Height is >= 31 and <= 33 && filter.SimilarityFrame.Height is >= 26 and <= 28,
            $"sort={filter.SortFrame} toggle={filter.SimilarityFrame} content={content}");
        Check("筛选条计数宽度有限", filter.CountWidthDp is > 8 and < 200, $"{filter.CountWidthDp:0.#}dp");

        var files = main.FileVM.ThumbnailList.FilteredFiles;
        Check("胶片栏项数=筛选列表", root.Filmstrip.Files.Count == files.Count && files.Count > 0, $"{files.Count}");
        var card = root.Filmstrip.CardAt(0);
        double density = AndroidUiTheme.Density(root.Context!);
        double expectedCardWidth = layoutRow ? 96 : 116;
        Check($"卡片 {expectedCardWidth:0}×144dp", card != null
            && Math.Abs(card.Width / density - expectedCardWidth) < 1.5 && Math.Abs(card.Height / density - 144) < 1.5,
            card == null ? "首卡未创建" : $"{card.Width / density:0.#}×{card.Height / density:0.#}dp");
        Check("首卡绑定筛选首项", card != null && ReferenceEquals(card.BoundFile, files[0]), card?.BoundFile?.Name ?? "null");

        Check("控制栏三分组", root.ControlBar.GroupCount == 3, $"{root.ControlBar.GroupCount}");
        Check("控制栏命令含 Next/Fit", root.ControlBar.Commands.Contains("Next") && root.ControlBar.Commands.Contains("Fit"),
            string.Join(",", root.ControlBar.Commands));

        // Fit ↔ 物理 100% 往返（大素材才具备向上空间；小素材按契约夹回适应态也算通过）。
        bool canZoom = session.Image!.SourceWidth > 0 && root.Photo.Viewport.FitScale < 1 / density - 1e-9;
        root.Photo.ToggleFit();
        await System.Threading.Tasks.Task.Delay(700);
        if (canZoom)
        {
            Check("Fit→物理 100%", !root.Photo.IsFit && Math.Abs(root.Photo.PhysicalScale - 1) < .03,
                $"fit={root.Photo.IsFit} physical={root.Photo.PhysicalScale:0.###}");
            Check("100% 触发 Full", !session.IsProxy, $"proxy={session.IsProxy} {session.Image.Width}x{session.Image.Height}");
        }
        else
        {
            Check("小素材 100% 夹回适应", root.Photo.IsFit, $"fit={root.Photo.IsFit} physical={root.Photo.PhysicalScale:0.###}");
        }
        root.Photo.ToggleFit();
        await System.Threading.Tasks.Task.Delay(500);
        Check("回到适应态", root.Photo.IsFit, $"fit={root.Photo.IsFit}");

        Phase("STATE-01 缓存切图");
        // STATE-01：缓存命中切图保留前一张——两张都已上屏后再切，不得经过空图、不得显示加载提示。
        await CheckCachedSwitchAsync(_activity!, main, session, root);

        Phase("MENU-06 主图菜单");
        // MENU-06：主图菜单文案、勾选与置灰（快照取自真实 ShowPhotoMenu 构建）。
        await CheckPhotoMenuAsync(main, session, root);

        if (Wants("analysis") || Wants("all"))
        {
        Phase("analysis 分析栏");
        // 分析栏开合与瓦片几何。先生成一张确定性的 Sony 对焦 JPEG，保证对焦点+中心两张细节瓦片。
        var focusFolder = EnsureSonyFocusFixture(_activity!);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(focusFolder));
        bool focusReady = false;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (main.CurrentFile?.ExifData?.SonyFocusPosition != null && session.Image != null)
            {
                focusReady = true;
                break;
            }
            await System.Threading.Tasks.Task.Delay(150);
        }
        Check("Sony 对焦夹具加载", focusReady,
            $"{main.CurrentFile?.Name} focus={main.CurrentFile?.ExifData?.SonyFocusPosition != null} image={session.Image != null}");

        main.IsAnalysisViewVisible = true;
        await System.Threading.Tasks.Task.Delay(900);
        var pane = root.AnalysisPane;
        Check("分析栏可见", pane.Visibility == global::Android.Views.ViewStates.Visible && root.Panes.Analysis.Height > 0,
            $"{pane.Visibility} {root.Panes.Analysis}");
        Check("分析栏瓦片≥6", pane.TileCount >= 6, $"{pane.TileCount}");
        Check("分析栏瓦片边长合理", pane.TileSize > 20, $"{pane.TileSize:0.#}dp");

        // ANALYSIS-04/ANALYSIS-11：按下立即点亮、滑动切换/清除、抬起与取消清除、悬停独立。
        density = AndroidUiTheme.Density(root.Context!);
        var detailTiles = new List<AnalysisTile>();
        var diagnosticTiles = new List<AnalysisTile>();
        foreach (var tile in pane.Tiles)
        {
            if (tile.Kind == AnalysisTileKind.Detail) detailTiles.Add(tile);
            else if (tile.Kind == AnalysisTileKind.Diagnostic) diagnosticTiles.Add(tile);
        }
        Check("分析栏有对焦点与中心两瓦片", detailTiles.Count >= 2, $"{detailTiles.Count}");
        if (detailTiles.Count > 0)
        {
            var firstDetail = detailTiles[0];
            var firstFrame = FrameCenterPx(pane, firstDetail, density);
            var firstPoint = (firstDetail.CenterU, firstDetail.CenterV);
            var secondDetail = detailTiles.Count > 1 ? detailTiles[1] : detailTiles[0];
            var secondFrame = FrameCenterPx(pane, secondDetail, density);

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 1000));
            Check("细节按下立即点亮", pane.DetailActive
                && Near(pane.DetailPoint.U, firstPoint.CenterU) && Near(pane.DetailPoint.V, firstPoint.CenterV),
                $"active={pane.DetailActive} point={pane.DetailPoint} tile={firstPoint}");
            Check("细节按下主图绿框", session.IsProxy || root.DetailFrameVisible,
                $"proxy={session.IsProxy} frame={root.DetailFrameVisible}");

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, 1, 1, 1010));
            Check("细节滑到空白清除", !pane.DetailActive, $"active={pane.DetailActive}");

            if (detailTiles.Count > 1)
            {
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 1100));
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, secondFrame.X, secondFrame.Y, 1110));
                Check("细节滑入另一瓦片切换", pane.DetailActive
                    && Near(pane.DetailPoint.U, secondDetail.CenterU) && Near(pane.DetailPoint.V, secondDetail.CenterV),
                    $"active={pane.DetailActive} point={pane.DetailPoint} second={secondDetail.CenterU:0.###},{secondDetail.CenterV:0.###}");
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, firstFrame.X, firstFrame.Y, 1120));
                Check("细节滑回第一瓦片", pane.DetailActive
                    && Near(pane.DetailPoint.U, firstDetail.CenterU) && Near(pane.DetailPoint.V, firstDetail.CenterV),
                    $"active={pane.DetailActive} point={pane.DetailPoint}");
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Cancel, firstFrame.X, firstFrame.Y, 1130));
            }

            // 多指：仅 owner 指针可改变/清除活动目标，第二指与小动作不得夺走。
            var pointers = new[] { (Id: 0, X: firstFrame.X, Y: firstFrame.Y), (Id: 1, X: secondFrame.X, Y: secondFrame.Y) };
            pane.OnTouchEvent(MultiTouchEvent(1200, 1200, MotionEventActions.Down, 0, new[] { pointers[0] }));
            pane.OnTouchEvent(MultiTouchEvent(1200, 1210, MotionEventActions.PointerDown, 1, pointers));
            Check("第二指按下不改变活动目标", pane.DetailActive
                && Near(pane.DetailPoint.U, firstPoint.CenterU) && Near(pane.DetailPoint.V, firstPoint.CenterV),
                $"active={pane.DetailActive} point={pane.DetailPoint}");
            pane.OnTouchEvent(MultiTouchEvent(1200, 1220, MotionEventActions.PointerUp, 1, pointers));
            Check("非 owner 指针抬起不清除", pane.DetailActive
                && Near(pane.DetailPoint.U, firstPoint.CenterU) && Near(pane.DetailPoint.V, firstPoint.CenterV),
                $"active={pane.DetailActive} point={pane.DetailPoint}");
            pane.OnTouchEvent(MultiTouchEvent(1200, 1230, MotionEventActions.Up, 0, new[] { pointers[0] }));
            Check("owner 抬起结束多指", !pane.DetailActive, $"active={pane.DetailActive}");

            // 旧 PointerUp / Cancel 不得清除新建立的目标。
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, secondFrame.X, secondFrame.Y, 1300));
            pane.OnTouchEvent(MultiTouchEvent(1300, 1310, MotionEventActions.PointerUp, 0, new[] { (Id: 9, X: secondFrame.X, Y: secondFrame.Y) }));
            Check("旧 PointerUp 不清新目标", pane.DetailActive
                && Near(pane.DetailPoint.U, secondDetail.CenterU) && Near(pane.DetailPoint.V, secondDetail.CenterV),
                $"active={pane.DetailActive} point={pane.DetailPoint}");
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Cancel, secondFrame.X, secondFrame.Y, 1290));
            Check("旧 Cancel 不清新目标", pane.DetailActive, $"active={pane.DetailActive}");
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, secondFrame.X, secondFrame.Y, 1320));

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 1400));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 1410));
            Check("细节抬起清除", !pane.DetailActive, $"active={pane.DetailActive}");

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 1500));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Cancel, firstFrame.X, firstFrame.Y, 1510));
            Check("细节取消清除", !pane.DetailActive, $"active={pane.DetailActive}");

            int focusRequests = 0;
            void OnFocus(double u, double v) => focusRequests++;
            pane.DetailFocusRequested += OnFocus;
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 2000));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 2010));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 2100));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 2110));
            pane.DetailFocusRequested -= OnFocus;
            Check("细节双击定位独立于高亮", focusRequests == 1 && !pane.DetailActive,
                $"focus={focusRequests} active={pane.DetailActive}");

            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverMove, firstFrame.X, firstFrame.Y, 3000, InputSourceType.Mouse));
            Check("鼠标悬停点亮细节", pane.DetailActive, $"active={pane.DetailActive}");
            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverExit, firstFrame.X, firstFrame.Y, 3010, InputSourceType.Mouse));
            Check("鼠标离开清除绿框", !pane.DetailActive, $"active={pane.DetailActive}");

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 3100));
            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverMove, secondFrame.X, secondFrame.Y, 3110, InputSourceType.Mouse));
            Check("触控活动时悬停不抢占", pane.DetailActive
                && Near(pane.DetailPoint.U, firstPoint.CenterU) && Near(pane.DetailPoint.V, firstPoint.CenterV),
                $"active={pane.DetailActive} point={pane.DetailPoint}");
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 3120));

            if (diagnosticTiles.Count > 0)
            {
                var diagnosticFrame = FrameCenterPx(pane, diagnosticTiles[0], density);
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, diagnosticFrame.X, diagnosticFrame.Y, 3300));
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, firstFrame.X, firstFrame.Y, 3310));
                Check("诊断起手滑入细节不自动点亮", !pane.DetailActive, $"active={pane.DetailActive}");
                pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 3320));
            }

            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, 1, 1, 3400));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, firstFrame.X, firstFrame.Y, 3410));
            Check("空白起手滑入细节不自动点亮", !pane.DetailActive, $"active={pane.DetailActive}");
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, firstFrame.X, firstFrame.Y, 3420));

            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverMove, firstFrame.X, firstFrame.Y, 3200, InputSourceType.Touchpad));
            Check("触控板悬停点亮细节", pane.DetailActive, $"active={pane.DetailActive}");
            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverExit, firstFrame.X, firstFrame.Y, 3210, InputSourceType.Touchpad));

            pane.OnHoverEvent(HoverEvent(MotionEventActions.HoverMove, firstFrame.X, firstFrame.Y, 3220, InputSourceType.Touchscreen));
            Check("触屏不合成悬停", !pane.DetailActive, $"active={pane.DetailActive}");
            pane.ClearDetail();
        }

        if (diagnosticTiles.Count > 0)
        {
            var diagnostic = diagnosticTiles[0];
            var frame = FrameCenterPx(pane, diagnostic, density);
            var presenter = root.AnalysisPresenterForChecks;
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, frame.X, frame.Y, 4000));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, frame.X, frame.Y, 4010));
            Check("诊断点按落准星", presenter.CrosshairU is > .45 and < .55 && presenter.CrosshairV is > .45 and < .55,
                $"{presenter.CrosshairU:0.###},{presenter.CrosshairV:0.###}");

            var savedU = presenter.CrosshairU;
            var savedV = presenter.CrosshairV;
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, frame.X, frame.Y, 4100));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Move, 1, 1, 4110));
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Up, 1, 1, 4120));
            Check("诊断滑动释放不误点", presenter.CrosshairU == savedU && presenter.CrosshairV == savedV,
                $"{savedU:0.###},{savedV:0.###} → {presenter.CrosshairU:0.###},{presenter.CrosshairV:0.###}");
        }

        if (detailTiles.Count > 0)
        {
            var firstFrame = FrameCenterPx(pane, detailTiles[0], density);
            pane.OnTouchEvent(TouchEvent(MotionEventActions.Down, firstFrame.X, firstFrame.Y, 5000));
            main.IsAnalysisViewVisible = false;
            await System.Threading.Tasks.Task.Delay(400);
            Check("隐藏分析栏清除活动细节", !pane.DetailActive, $"active={pane.DetailActive}");
        }
        else
        {
            main.IsAnalysisViewVisible = false;
            await System.Threading.Tasks.Task.Delay(400);
        }
        Check("关闭分析栏释放空间", pane.Visibility != global::Android.Views.ViewStates.Visible, $"{pane.Visibility}");
        }

        if (Wants("similarity") || Wants("all"))
        {
        Phase("similarity 相似面板");
        // 相似面板开合。
        main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
        await System.Threading.Tasks.Task.Delay(900);
        Check("相似聚类面板可见", root.SimilarityPane.Visibility == global::Android.Views.ViewStates.Visible,
            $"{root.SimilarityPane.Visibility}");
        main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
        await System.Threading.Tasks.Task.Delay(400);
        }

        if (Wants("settings") || Wants("all"))
        {
        Phase("settings 设置");
        // 设置半屏与工具页开合（真实展示路径）。
        main.OpenSettingWindow();
        await System.Threading.Tasks.Task.Delay(700);
        Check("设置 sheet 打开", AndroidNativeSettingsPresenter.IsOpen, "IsOpen=false");
        var sheet = AndroidNativeSettingsPresenter.CurrentSheet;
        Check("设置工作副本可用", sheet != null, "CurrentSheet=null");
        if (sheet != null)
        {
            var settings = sheet.Settings;
            int hotkeyCount = settings.Hotkeys.Count;
            var firstHotkey = settings.Hotkeys[0];
            var secondHotkey = settings.Hotkeys[1];
            var savedPrimary = firstHotkey.PrimaryHotkey;
            var savedSecondary = firstHotkey.SecondaryHotkey;
            var savedSecondPrimary = secondHotkey.PrimaryHotkey;
            bool savedZoomIndicator = settings.ShowZoomIndicator;
            var savedPresets = new List<string>();
            foreach (var preset in settings.ScalePresets) savedPresets.Add(preset.Text);

            sheet.ShowPageForChecks("control");
            await System.Threading.Tasks.Task.Delay(250);
            Check("设置控制页可达", sheet.Page == "control", sheet.Page);
            Check("布局模式说明可见", !string.IsNullOrWhiteSpace(sheet.LayoutModeDescriptionForChecks()),
                sheet.LayoutModeDescriptionForChecks());

            // SETTINGS-08：布局分组标题固定为「边栏布局位置」，行标题仍是「布局模式」。
            Check("控制页布局分组标题为边栏布局位置",
                sheet.LayoutSectionTitleForChecks() == "边栏布局位置",
                $"title=\"{sheet.LayoutSectionTitleForChecks()}\"");

            // SETTINGS-01：控制栏功能行 勾选 → 命令图标 → 命令名。
            // 断言规则：可见勾选字形右侧 settings.android.icon.gap 处开始图标；图标左边距只抵消 44dp 命中区
            // 相对可见字形的透明内缩（勾选框 32dp 矢量帧内约 18dp 方框），命中区保持 44dp。快照记录两侧实际像素值。
            var iconRow = sheet.HotkeyRowSnapshotForChecks(0);
            int iconGapPx = (int)AndroidUiTheme.Dp(_activity!, DesignTokens.SettingsAndroidIconGap);
            int hitArea44Dp = (int)AndroidUiTheme.Dp(_activity!, AndroidSettingsReorderList.CheckHitSizeDp);
            int checkGlyphPx = (int)AndroidUiTheme.Dp(_activity!, AndroidSettingsReorderList.CheckBoxGlyphSizeDp);
            int insetOffsetPx = (hitArea44Dp - checkGlyphPx) / 2 + checkGlyphPx + iconGapPx - hitArea44Dp;
            int? hitWidth = HotkeyCheckHitWidthPx(sheet, firstHotkey.DisplaySymbol);
            bool iconVisible = iconRow is { IconVisible: true } && !string.IsNullOrEmpty(iconRow.Value.IconSymbol);
            Check("控制栏功能行显示命令图标",
                iconVisible && iconRow!.Value.IconSymbol == firstHotkey.DisplaySymbol,
                iconRow is { } iconValue ? $"icon={DescribeGlyph(iconValue.IconSymbol)} expected={firstHotkey.DisplaySymbol}" : DescribeRow(iconRow));
            Check("命令图标到命令名间距 16dp",
                iconRow is { } gapRow && Math.Abs(gapRow.IconToTitleGap - iconGapPx) <= 1,
                iconRow is { } gapValue ? $"gap={gapValue.IconToTitleGap}px expected={iconGapPx}px" : DescribeRow(iconRow));
            Check("勾选→图标边距只抵消透明内缩且命中区仍 44dp",
                iconRow is { } lineRow && Math.Abs(lineRow.CheckToIconGap - insetOffsetPx) <= 1
                && hitWidth is { } hit && Math.Abs(hit - hitArea44Dp) <= 1,
                iconRow is { } checkValue
                    ? $"checkToIcon={checkValue.CheckToIconGap}px expected={insetOffsetPx}px(仅抵消勾选框透明内缩)"
                        + $" hit={hitWidth?.ToString() ?? "null"}px expected={hitArea44Dp}px"
                    : DescribeRow(iconRow));

            // INPUT-13：键盘、鼠标/触控板任一连接即显示完整录制控件；纯触控全部隐藏。
            AndroidInputDevices.SetChecksOverride(true, false);
            await System.Threading.Tasks.Task.Delay(300);
            var keyboardRow = sheet.HotkeyRowSnapshotForChecks(0);
            Check("连接键盘显示完整绑定行", keyboardRow is { BindingControlsVisible: true, SubtitleVisible: false }
                && keyboardRow.Value.AccessibilityText.Contains("主绑定")
                && keyboardRow.Value.PrimaryChipText.Contains("Ctrl+N"),
                DescribeRow(keyboardRow));
            AndroidInputDevices.SetChecksOverride(false, true);
            await System.Threading.Tasks.Task.Delay(300);
            var pointerRow = sheet.HotkeyRowSnapshotForChecks(0);
            Check("连接鼠标/触控板显示完整绑定行", pointerRow is { BindingControlsVisible: true },
                DescribeRow(pointerRow));
            AndroidInputDevices.SetChecksOverride(false, false);
            await System.Threading.Tasks.Task.Delay(300);
            var touchRow = sheet.HotkeyRowSnapshotForChecks(0);
            Check("纯触控零绑定文字", touchRow is { BindingControlsVisible: false, SubtitleVisible: false }
                && !touchRow.Value.AccessibilityText.Contains("主绑定")
                && !touchRow.Value.AccessibilityText.Contains("次绑定")
                && !touchRow.Value.AccessibilityText.Contains("冲突")
                && !touchRow.Value.Subtitle.Contains("Ctrl"),
                DescribeRow(touchRow));
            Check("纯触控保留显隐与行序", settings.Hotkeys.Count == hotkeyCount
                && ReferenceEquals(settings.Hotkeys[0], firstHotkey), $"{settings.Hotkeys.Count}");

            // INPUT-08 / SETTINGS-11：录制、取消、清空、键鼠手势与冲突。
            AndroidInputDevices.SetChecksOverride(true, false);
            await System.Threading.Tasks.Task.Delay(250);
            sheet.BeginRecordingForChecks(0, true);
            Check("进入主绑定录制", sheet.IsRecording, "IsRecording=false");
            Check("Esc 取消保留原绑定", sheet.FeedRecordingKeyForChecks(Keycode.Escape)
                && !sheet.IsRecording && ReferenceEquals(firstHotkey.PrimaryHotkey, savedPrimary),
                $"{firstHotkey.PrimaryHotkeyText}");
            // 系统返回键与 Esc 同语义：取消录制并保留原绑定，不写入永远无法派发的死绑定。
            sheet.BeginRecordingForChecks(0, true);
            Check("系统返回键取消保留原绑定", sheet.FeedRecordingKeyForChecks(Keycode.Back)
                && !sheet.IsRecording && ReferenceEquals(firstHotkey.PrimaryHotkey, savedPrimary),
                $"{firstHotkey.PrimaryHotkeyText}");
            sheet.BeginRecordingForChecks(0, true);
            sheet.FeedRecordingKeyForChecks(Keycode.Del);
            Check("Delete 清空槽位", firstHotkey.PrimaryHotkey == null, firstHotkey.PrimaryHotkeyText);
            sheet.BeginRecordingForChecks(0, true);
            sheet.FeedRecordingKeyForChecks(Keycode.N);
            Check("键盘手势写入工作副本", firstHotkey.PrimaryHotkey?.Key?.Key == GestureKey.N,
                firstHotkey.PrimaryHotkeyText);
            sheet.BeginRecordingForChecks(0, false);
            sheet.FeedRecordingWheelForChecks(1);
            Check("滚轮手势写入工作副本", firstHotkey.SecondaryHotkey?.Mouse?.Action == MouseAction.WheelUp,
                firstHotkey.SecondaryHotkeyText);
            sheet.BeginRecordingForChecks(0, false);
            sheet.FeedRecordingButtonForChecks(MotionEventButtonState.Back);
            Check("鼠标侧键写入工作副本", firstHotkey.SecondaryHotkey?.Mouse?.Action == MouseAction.XButton1Click,
                firstHotkey.SecondaryHotkeyText);

            sheet.BeginRecordingForChecks(0, true);
            var mouseDown = HoverEvent(MotionEventActions.Down, 100, 100, 6000, InputSourceType.Mouse);
            Check("录制时鼠标左键被吞", sheet.HandleRecordingMouseTouch(mouseDown), "left not consumed");
            var mouseUp = HoverEvent(MotionEventActions.Up, 100, 100, 6010, InputSourceType.Mouse);
            Check("录制时鼠标右键/抬起被吞", sheet.HandleRecordingMouseTouch(mouseUp), "up not consumed");
            var fingerDown = TouchEvent(MotionEventActions.Down, 100, 100, 6020);
            Check("录制时触屏仍可操作界面", !sheet.HandleRecordingMouseTouch(fingerDown), "touch consumed");
            sheet.FeedRecordingKeyForChecks(Keycode.Escape);

            firstHotkey.PrimaryHotkey = savedPrimary;
            settings.CheckHotkeyConflicts();
            secondHotkey.PrimaryHotkey = firstHotkey.PrimaryHotkey;
            settings.CheckHotkeyConflicts();
            Check("相同绑定双方标记冲突", firstHotkey.HasPrimaryConflict && secondHotkey.HasPrimaryConflict,
                $"{firstHotkey.HasPrimaryConflict}/{secondHotkey.HasPrimaryConflict}");
            secondHotkey.PrimaryHotkey = savedSecondPrimary;
            settings.CheckHotkeyConflicts();

            sheet.BeginRecordingForChecks(1, true);
            AndroidInputDevices.SetChecksOverride(false, null);
            await System.Threading.Tasks.Task.Delay(300);
            Check("最后外设断开取消录制", !sheet.IsRecording
                && ReferenceEquals(secondHotkey.PrimaryHotkey, savedSecondPrimary), secondHotkey.PrimaryHotkeyText);

            // 连接变化不重置工作副本/草稿、不关闭页面、不弹软键盘。
            settings.ShowZoomIndicator = false;
            AndroidInputDevices.SetChecksOverride(true, false);
            await System.Threading.Tasks.Task.Delay(250);
            Check("外设变化保留工作副本与页面", !settings.ShowZoomIndicator && sheet.Page == "control",
                $"zoom={settings.ShowZoomIndicator} page={sheet.Page}");
            Check("外设变化不弹软键盘", _activity!.CurrentFocus is not global::Android.Widget.EditText,
                _activity.CurrentFocus?.GetType().Name ?? "null");
            settings.ShowZoomIndicator = savedZoomIndicator;

            var inputManager = _activity.GetSystemService(global::Android.Content.Context.InputService) as AndroidInputManager;
            bool deviceFilterOk = true;
            int deviceCount = 0;
            if (inputManager != null)
            {
                foreach (int id in inputManager.GetInputDeviceIds() ?? [])
                {
                    var device = inputManager.GetInputDevice(id);
                    if (device == null) continue;
                    deviceCount++;
                    if (device.IsVirtual
                        && (AndroidInputDevices.CountsAsKeyboard(device) || AndroidInputDevices.CountsAsPointer(device)))
                        deviceFilterOk = false;
                    if (device.KeyboardType != InputKeyboardType.Alphabetic
                        && AndroidInputDevices.CountsAsKeyboard(device))
                        deviceFilterOk = false;
                }
            }
            Check("虚拟键与非字母键盘不计入外设", deviceFilterOk, $"{deviceCount} devices");

            // SETTINGS-07：倍率行单一居中数值 + %，提交保持输入框实例并原地重排。
            sheet.ShowPageForChecks("preview");
            await System.Threading.Tasks.Task.Delay(200);

            // SETTINGS-07：缩放指示器开关组不显示「缩放位置显示」类分组小标题。
            Check("预览页指示器开关无分组标题", string.IsNullOrEmpty(sheet.PreviewSwitchSectionTitleForChecks()),
                $"title=\"{sheet.PreviewSwitchSectionTitleForChecks()}\"");

            var shape = sheet.ScalePresetRowShapeForChecks(0);
            Check("倍率行单一居中值+%", shape == "inputs=1 centered=1 percent=1 delete=1", shape);

            // SETTINGS-07：输入框 + % 作为一组居行中心（偏差 ≤1px），行尾删除按钮不挤入这一组。
            await System.Threading.Tasks.Task.Delay(150);
            string presetGeometry = sheet.ScalePresetGroupGeometryForChecks(0);
            double presetOffset = GeometryValue(presetGeometry, "offset");
            double presetRowWidth = GeometryValue(presetGeometry, "rowWidth");
            double presetDeleteLeft = GeometryValue(presetGeometry, "deleteLeft");
            double presetDeleteWidth = GeometryValue(presetGeometry, "deleteWidth");
            int? presetGroupRight = PresetGroupRightEdgePx(sheet);
            Check("倍率输入组居行中心(偏差≤1px)",
                !double.IsNaN(presetOffset) && Math.Abs(presetOffset) <= 1,
                presetGeometry);
            Check("倍率删除按钮不挤入输入组",
                !double.IsNaN(presetRowWidth) && !double.IsNaN(presetDeleteLeft) && !double.IsNaN(presetDeleteWidth)
                && presetDeleteLeft + presetDeleteWidth <= presetRowWidth + .5
                && presetGroupRight is { } groupRight && presetDeleteLeft >= groupRight,
                $"{presetGeometry} groupRight={presetGroupRight?.ToString() ?? "null"}");
            var targetPreset = settings.ScalePresets[0];
            string originalPreset = targetPreset.Text;
            var targetInput = sheet.ScalePresetInputIdentityForChecks(0);
            string committed = sheet.CommitScalePresetForChecks(0, "33.333");
            Check("倍率失焦写入工作副本", committed == "33.333" && sheet.ScalePresetsAscendingForChecks(),
                $"{originalPreset} → {committed} [{sheet.ScalePresetsForChecks()}]");
            int targetIndex = settings.ScalePresets.IndexOf(targetPreset);
            Check("倍率提交保留输入框实例", ReferenceEquals(targetInput, sheet.ScalePresetInputIdentityForChecks(targetIndex)),
                $"index={targetIndex}");
            string after1601 = sheet.CommitScalePresetForChecks(targetIndex, "1601");
            Check("倍率 1601 恢复原值", after1601 == "33.333", after1601);
            string afterNaN = sheet.CommitScalePresetForChecks(targetIndex, "NaN");
            Check("倍率 NaN 恢复原值", afterNaN == "33.333", afterNaN);
            string afterZero = sheet.CommitScalePresetForChecks(targetIndex, "0");
            Check("倍率 0 恢复原值", afterZero == "33.333", afterZero);

            // 未失焦草稿在添加/删除前先提交；不重建页面、不换输入框。
            var draftPreset = settings.ScalePresets[1];
            var draftInput = sheet.ScalePresetInputIdentityForChecks(1);
            sheet.SetScalePresetInputForChecks(1, "77.777");
            int beforeAdd = settings.ScalePresets.Count;
            int hundredsBefore = CountPresetText(settings, "100");
            sheet.AddScalePresetForChecks();
            int hundredsAfter = CountPresetText(settings, "100");
            int draftIndex = settings.ScalePresets.IndexOf(draftPreset);
            Check("＋新增默认 100% 项且不丢草稿", settings.ScalePresets.Count == beforeAdd + 1
                && hundredsAfter == hundredsBefore + 1 && draftPreset.Text == "77.777"
                && ReferenceEquals(draftInput, sheet.ScalePresetInputIdentityForChecks(draftIndex))
                && sheet.Page == "preview",
                $"{sheet.ScalePresetsForChecks()} page={sheet.Page}");
            int addedIndex = -1;
            for (int index = settings.ScalePresets.Count - 1; index >= 0; index--)
            {
                if (settings.ScalePresets[index].Text == "100")
                {
                    addedIndex = index;
                    break;
                }
            }
            sheet.RemoveScalePresetForChecks(addedIndex);
            Check("−删除不重建编辑页", settings.ScalePresets.Count == beforeAdd
                && ReferenceEquals(draftInput, sheet.ScalePresetInputIdentityForChecks(
                    settings.ScalePresets.IndexOf(draftPreset))) && sheet.Page == "preview",
                $"{sheet.ScalePresetsForChecks()} page={sheet.Page}");

            // 至少保留一项：删到只剩一项后删除无效。
            while (settings.ScalePresets.Count > 1) sheet.RemoveScalePresetForChecks(0);
            bool canRemoveOne = settings.CanRemoveScalePreset;
            sheet.RemoveScalePresetForChecks(0);
            Check("只剩一项时删除无效", !canRemoveOne && settings.ScalePresets.Count == 1,
                $"canRemove={canRemoveOne} count={settings.ScalePresets.Count}");
            settings.ScalePresets.Clear();
            foreach (string text in savedPresets) settings.ScalePresets.Add(new SettingsViewModel.ScalePreset(text));
            settings.SortScalePreset();

            firstHotkey.PrimaryHotkey = savedPrimary;
            firstHotkey.SecondaryHotkey = savedSecondary;
            secondHotkey.PrimaryHotkey = savedSecondPrimary;
            settings.CheckHotkeyConflicts();
            Check("倍率测试后快捷键行数保持", settings.Hotkeys.Count == hotkeyCount,
                $"{settings.Hotkeys.Count}/{hotkeyCount}");
            AndroidInputDevices.SetChecksOverride(null, null);
            sheet.ShowPageForChecks("root");
            await System.Threading.Tasks.Task.Delay(150);
            // SETTINGS-04 / SETTINGS-15：内容区手势、行高、分类箭头、勾选对齐、命令图标与脚注。
            await RunSettingsExperienceChecksAsync(sheet);
        }
        AndroidNativeSettingsPresenter.HandleBack();
        await System.Threading.Tasks.Task.Delay(500);
        Check("设置 sheet 关闭", !AndroidNativeSettingsPresenter.IsOpen, "IsOpen=true");
        }

        if (Wants("tools") || Wants("all"))
        {
        Phase("tools 工具页");
        main.OpenToolsWindow();
        await System.Threading.Tasks.Task.Delay(800);
        Check("工具 sheet 打开", AndroidToolsPresenter.IsOpen, "IsOpen=false");

        // SETTINGS-04：工具浮层与设置同一初始收起半屏。
        var toolsSheet = AndroidToolsPresenter.CurrentSheetForChecks;
        var toolsContainer = toolsSheet?.Parent as View;
        var toolsBehavior = toolsContainer == null ? null : BottomSheetBehavior.From(toolsContainer);
        var toolsParent = toolsContainer?.Parent as View;
        int toolsScreen = _activity!.Resources?.DisplayMetrics?.HeightPixels ?? 0;
        int toolsPeek = toolsBehavior?.PeekHeight ?? 0;
        int toolsTolerance = Math.Max(2, (int)(toolsScreen * .01));
        Check("工具 sheet 初始半屏收起",
            toolsBehavior != null && toolsBehavior.State == BottomSheetBehavior.StateCollapsed
            && Math.Abs(toolsPeek - (int)(toolsScreen * DesignTokens.SettingsAndroidSheetPeekRatio)) <= toolsTolerance
            && toolsParent != null && Math.Abs(toolsContainer!.Top - (toolsParent.Height - toolsPeek)) <= toolsTolerance,
            toolsContainer == null
                ? "sheet=null"
                : $"state={toolsBehavior?.State} top={toolsContainer.Top} parent={toolsParent?.Height}"
                    + $" peek={toolsPeek} screen={toolsScreen}");

        Check("菜单入口显示工具列表", main.Tools.CurrentTool == null && AndroidToolsPresenter.CurrentPageForChecks == "root",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");

        // TOOLBAR-06：控制栏 EXIF 整组入口（已展示列表）直达详情，且不触发 DINO 推理。
        root.ControlBar.TapExifGroup();
        await System.Threading.Tasks.Task.Delay(600);
        Check("EXIF 入口直达详情", AndroidToolsPresenter.IsOpen && main.Tools.CurrentTool is ExifDetailViewModel
            && AndroidToolsPresenter.CurrentPageForChecks == "exif",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");
        Check("EXIF 入口不触发 DINO", !main.Tools.DinoPresenter.IsBusy && main.Tools.DinoPresenter.Status == "未加载",
            $"busy={main.Tools.DinoPresenter.IsBusy} status={main.Tools.DinoPresenter.Status}");

        // SETTINGS-04：工具半屏同样只在可见区域内布局；EXIF 长页滚到末尾后最后一项必须完整可见。
        if (toolsSheet != null && toolsContainer != null && toolsSheet.ScrollHostForChecks is { } toolsScroll)
        {
            for (int wait = 0; wait < 20 && (toolsScroll.GetChildAt(0)?.Height ?? 0) <= toolsScroll.Height; wait++)
                await System.Threading.Tasks.Task.Delay(150);
            Check("工具收起态内容布局高度等于可见区域",
                SheetViewportMatches(toolsSheet, toolsContainer, toolsScroll),
                SheetViewportText(toolsSheet, toolsContainer, toolsScroll));
            var toolsTail = await ScrollSheetToEndAsync(toolsScroll);
            Check("工具 EXIF 收起态长页底部完整可见",
                toolsTail.Ok && toolsBehavior?.State == BottomSheetBehavior.StateCollapsed
                && toolsParent != null && Math.Abs(toolsContainer.Top - (toolsParent.Height - toolsPeek)) <= toolsTolerance,
                toolsTail.Detail + $" state={toolsBehavior?.State} top={toolsContainer.Top}/{toolsParent?.Height - toolsPeek}");
        }

        // 已展示 DINO 时仍明确切到 EXIF（TOOLBAR-06），不保留原页。
        main.Tools.OpenDinoDebug();
        await System.Threading.Tasks.Task.Delay(150);
        root.ControlBar.TapExifGroup();
        await System.Threading.Tasks.Task.Delay(600);
        Check("已展示 DINO 时 EXIF 入口切回详情", main.Tools.CurrentTool is ExifDetailViewModel
            && AndroidToolsPresenter.CurrentPageForChecks == "exif",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");

        // 控制栏直达仍保留工具列表作为返回目标。
        AndroidToolsPresenter.HandleBack();
        await System.Threading.Tasks.Task.Delay(400);
        Check("EXIF 详情返回工具列表", main.Tools.CurrentTool == null
            && AndroidToolsPresenter.CurrentPageForChecks == "root",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");
        root.ControlBar.TapExifGroup();
        await System.Threading.Tasks.Task.Delay(400);
        Check("返回列表后再次直达 EXIF", main.Tools.CurrentTool is ExifDetailViewModel
            && AndroidToolsPresenter.CurrentPageForChecks == "exif",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");

        // 主图菜单入口只前置并保留当前页。
        root.ShowTools();
        await System.Threading.Tasks.Task.Delay(300);
        Check("菜单入口保留当前页", main.Tools.CurrentTool is ExifDetailViewModel
            && AndroidToolsPresenter.CurrentPageForChecks == "exif",
            $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");

        // 加载中不算缺 EXIF；真正无照片时才回退工具列表。
        var loaded = main.CurrentFile;
        if (loaded != null)
        {
            main.Tools.SyncCurrentFile(new ImageFile(loaded.File));
            Check("EXIF 加载中仍可进入", main.Tools.IsExifDetailEnabled,
                $"enabled={main.Tools.IsExifDetailEnabled} loaded={loaded.IsExifLoaded}");
            root.ControlBar.TapExifGroup();
            await System.Threading.Tasks.Task.Delay(400);
            Check("加载中 EXIF 入口直达详情", main.Tools.CurrentTool is ExifDetailViewModel
                && AndroidToolsPresenter.CurrentPageForChecks == "exif",
                $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");
            main.Tools.SyncCurrentFile(loaded);

            main.CurrentFile = null;
            await System.Threading.Tasks.Task.Delay(400);
            Check("无照片 EXIF 不可用", !main.Tools.IsExifDetailEnabled, "IsExifDetailEnabled=true");
            root.ControlBar.TapExifGroup();
            await System.Threading.Tasks.Task.Delay(600);
            Check("无照片 EXIF 入口回退列表", main.Tools.CurrentTool == null
                && AndroidToolsPresenter.CurrentPageForChecks == "root",
                $"tool={main.Tools.CurrentTool?.GetType().Name ?? "null"} page={AndroidToolsPresenter.CurrentPageForChecks}");
            main.CurrentFile = loaded;
        }

        AndroidToolsPresenter.HandleBack();
        await System.Threading.Tasks.Task.Delay(500);
        Check("工具 sheet 关闭", !AndroidToolsPresenter.IsOpen, "IsOpen=true");
        }

        WriteReport();
    }

    /// <summary>把本次检查的逐条结果写到 App 内部 <c>files/PhotoViewer/android-checks-&lt;mode&gt;.txt</c>。</summary>
    private static void WriteReport()
    {
        var report = new StringBuilder();
        report.AppendLine($"Android native checks mode={_mode} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"PASS {_passed} / FAIL {_failed}");
        foreach (var line in Lines) report.AppendLine(line);
        var directory = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "PhotoViewer");
        Directory.CreateDirectory(directory);
        File.WriteAllText(IOPath.Combine(directory, $"android-checks-{_mode}.txt"), report.ToString());
        if (_mode is "zoom" or "input")
        {
            global::Android.Util.Log.Info("PhotoViewerChecks", $"{_mode}: PASS {_passed} / FAIL {_failed}");
            foreach (var line in Lines) global::Android.Util.Log.Info("PhotoViewerChecks", line);
        }
        Console.WriteLine(report.ToString());
        Console.WriteLine(_failed == 0 ? $"[NativeCheck] PASS {_mode} ({_passed} checks)" : $"[NativeCheck] FAIL {_mode} ({_failed} failed / {_passed + _failed})");
    }

    /// <summary>
    /// STATE-01：两张同尺寸同方向的照片都已上屏（进内存缓存）后再切回，缓存命中的切图要在 SetFile 返回时
    /// 直接换上新图，全程不出现空图快照，也不出现延迟加载提示。素材特意同尺寸，覆盖"只比几何会误判成
    /// 热替换"的情形；大图覆盖代理档缓存，小图（源本身小于代理目标）覆盖"已是源分辨率"的复用口径。
    /// </summary>
    private static async System.Threading.Tasks.Task CheckCachedSwitchAsync(
        Activity activity, MainViewModel main, PhotoLoadSession session, AndroidNativeRootView root)
    {
        await CheckCachedSwitchInFolderAsync(activity, main, session, root, 3000, 2000, "STATE-01 ");
        await CheckCachedSwitchInFolderAsync(activity, main, session, root, 1200, 800, "STATE-01 小图 ");
    }

    /// <summary>在同一文件夹内做一次"两张都已上屏后切回"的缓存命中切图断言；尺寸与报告前缀由调用方给定。</summary>
    private static async System.Threading.Tasks.Task CheckCachedSwitchInFolderAsync(
        Activity activity, MainViewModel main, PhotoLoadSession session, AndroidNativeRootView root,
        int width, int height, string label)
    {
        var folder = EnsureCachedSwitchFixtures(activity, width, height);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            files = main.FileVM.ThumbnailList.FilteredFiles;
            if (files.Count >= 2 && main.CurrentFile != null && session.Image != null) break;
            await System.Threading.Tasks.Task.Delay(100);
        }
        if (files.Count < 2)
        {
            Check($"{label}缓存命中切图", false, $"素材不足两张 ({files.Count})");
            return;
        }

        // 两张都上屏一次（A → B → A），让它们都进内存缓存。
        await SelectAndWaitAsync(main, session, files[1]);
        var secondPhoto = session.Image;
        await SelectAndWaitAsync(main, session, files[0]);

        int nullSamples = 0, loadingSamples = 0;
        void OnChanged()
        {
            if (session.Image == null) nullSamples++;
            if (session.IsLoadingVisible) loadingSamples++;
        }

        int generation = session.Generation;
        bool immediate;
        session.Changed += OnChanged;
        try
        {
            main.FileVM.ThumbnailList.SelectImageCommand(files[1]);
            // 命中时 SetFile 同步换图：返回时已是上一次上屏的同一份产物，且代际已推进。
            immediate = ReferenceEquals(main.CurrentFile, files[1])
                && secondPhoto != null && ReferenceEquals(session.Image, secondPhoto)
                && session.Generation > generation && !session.IsLoadingVisible && root.Photo.HasImage;
            // 后续状态通知（导航图刷新等）同样不得出现空图或加载提示。
            await System.Threading.Tasks.Task.Delay(600);
        }
        finally
        {
            session.Changed -= OnChanged;
        }

        Check($"{label}缓存命中切图即时上屏", immediate,
            $"image={session.Image?.Width}x{session.Image?.Height} generation={generation}->{session.Generation}");
        Check($"{label}缓存命中切图不经过空图", nullSamples == 0 && loadingSamples == 0,
            $"null={nullSamples} loading={loadingSamples}");
        await SelectAndWaitAsync(main, session, files[0]);
    }

    /// <summary>
    /// MENU-06：主图长按菜单的固定文案与前置图标、勾选跟随增强状态、无照片时「雾天增强」置灰而「工具…」仍可用。
    /// 快照直接取 <see cref="AndroidNativeRootView.ShowPhotoMenu"/> 本次构建出的项，断言后立即关闭菜单。
    /// </summary>
    private static async System.Threading.Tasks.Task CheckPhotoMenuAsync(MainViewModel main, PhotoLoadSession session, AndroidNativeRootView root)
    {
        var previous = main.CurrentFile;
        bool previousEnhanced = session.IsEnhanced;
        var point = ((float)(root.Width / 2.0), (float)(root.Height / 2.0));
        try
        {
            var items = OpenPhotoMenuAndSnapshot(root, point);
            bool found = TryFindPhotoMenuItem(items, "雾天增强", out var enhance);
            Check("MENU-06 雾天增强文案与勾选",
                found && enhance.Checked == session.IsEnhanced && enhance.Enabled,
                $"enhanced={session.IsEnhanced} found={found} items=[{DescribePhotoMenu(items)}]");

            // 触屏没有悬停提示，前置图标是唯一的命令符号：逐项核对顺序与字形语义（MENU-06）。
            var expectedIcons = new (string Text, string Token)[]
            {
                ("打开文件夹…", DesignTokens.IconMenuOpenFolder),
                ("胶片栏", DesignTokens.IconMenuFilmstrip),
                ("分析栏", DesignTokens.IconMenuAnalysis),
                ("控制栏", DesignTokens.IconMenuControl),
                ("雾天增强", DesignTokens.IconCommandToggleEnhance),
                ("工具…", DesignTokens.IconMenuTools),
                ("设置…", DesignTokens.IconMenuSettings),
            };
            bool iconsOk = items.Count == expectedIcons.Length;
            string iconMismatch = string.Empty;
            for (int index = 0; iconsOk && index < expectedIcons.Length; index++)
            {
                var (text, token) = expectedIcons[index];
                string glyph = AndroidUiTheme.IconGlyph(token);
                if (glyph.Length > 0 && string.Equals(items[index].Text, text, StringComparison.Ordinal)
                    && string.Equals(items[index].Icon, glyph, StringComparison.Ordinal)) continue;
                iconsOk = false;
                iconMismatch = $"{items[index].Text}:{DescribeGlyph(items[index].Icon)}≠{text}:{token}";
            }
            Check("MENU-06 菜单项前置图标", iconsOk && iconMismatch.Length == 0,
                iconsOk ? $"7 项图标按 {string.Join("/", expectedIcons.Select(item => item.Token))} 绘制"
                    : $"items=[{DescribePhotoMenu(items)}] 首个不符 {iconMismatch}");

            // 勾选不是静态文案：切一次增强再重建菜单，勾选必须跟着会话状态走。
            session.ToggleEnhanced();
            await WaitAsync(() => session.IsEnhanced, "开启增强", 10000);
            var toggledItems = OpenPhotoMenuAndSnapshot(root, point);
            bool toggledFound = TryFindPhotoMenuItem(toggledItems, "雾天增强", out var toggled);
            Check("MENU-06 勾选跟随增强状态", toggledFound && toggled.Checked,
                $"enhanced={session.IsEnhanced} checked={(toggledFound ? toggled.Checked.ToString() : "缺项")}");
            session.ToggleEnhanced();
            await WaitAsync(() => !session.IsEnhanced, "关闭增强", 10000);

            // MENU-07：无当前照片时打开与栏位项照常可用、「雾天增强」置灰、「工具…」仍可执行。
            main.CurrentFile = null;
            await WaitAsync(() => main.CurrentFile == null, "清空当前照片", 5000);
            var noPhotoItems = OpenPhotoMenuAndSnapshot(root, point);
            bool enhanceDisabled = TryFindPhotoMenuItem(noPhotoItems, "雾天增强", out var disabled) && !disabled.Enabled;
            bool toolsEnabled = TryFindPhotoMenuItem(noPhotoItems, "工具…", out var tools) && tools.Enabled;
            Check("MENU-06 无照片时增强置灰工具可用", enhanceDisabled && toolsEnabled,
                $"enhanceDisabled={enhanceDisabled} toolsEnabled={toolsEnabled} items=[{DescribePhotoMenu(noPhotoItems)}]");
        }
        finally
        {
            root.DismissPhotoMenu();
            if (session.IsEnhanced != previousEnhanced) session.ToggleEnhanced();
            if (previous != null && !ReferenceEquals(main.CurrentFile, previous))
            {
                main.CurrentFile = previous;
                try
                {
                    await WaitAsync(() => session.Image != null && ReferenceEquals(main.CurrentFile, previous),
                        "恢复当前照片", 20000);
                }
                catch (TimeoutException exception)
                {
                    Check("MENU-06 恢复当前照片", false, exception.Message);
                }
            }
        }
    }

    /// <summary>打开主图菜单、拷贝本次构建出的项，再关闭菜单（菜单本体不留在屏上干扰后续检查）。</summary>
    private static List<(string Text, bool Checked, bool Enabled, string Icon)> OpenPhotoMenuAndSnapshot(
        AndroidNativeRootView root, (float X, float Y) point)
    {
        root.ShowPhotoMenu(point.X, point.Y);
        var snapshot = new List<(string Text, bool Checked, bool Enabled, string Icon)>(root.PhotoMenuItemsForChecks);
        root.DismissPhotoMenu();
        return snapshot;
    }

    /// <summary>按文本严格匹配菜单项。</summary>
    private static bool TryFindPhotoMenuItem(IReadOnlyList<(string Text, bool Checked, bool Enabled, string Icon)> items,
        string text, out (string Text, bool Checked, bool Enabled, string Icon) item)
    {
        foreach (var candidate in items)
        {
            if (!string.Equals(candidate.Text, text, StringComparison.Ordinal)) continue;
            item = candidate;
            return true;
        }
        item = default;
        return false;
    }

    /// <summary>把菜单快照写成一行（勾选标 ✓、置灰标「灰」、图标标码位），报告里可直接人工复核文案与字形。</summary>
    private static string DescribePhotoMenu(IReadOnlyList<(string Text, bool Checked, bool Enabled, string Icon)> items)
        => string.Join(" / ", items.Select(item =>
            $"{item.Text}{(item.Checked ? "✓" : string.Empty)}{(item.Enabled ? string.Empty : "灰")}{DescribeGlyph(item.Icon)}"));

    /// <summary>图标字形写成 <c>[U+F7FC]</c>；缺失时写 <c>[无图标]</c>，报告与断言共用同一写法。</summary>
    private static string DescribeGlyph(string glyph)
        => string.IsNullOrEmpty(glyph) ? "[无图标]" : $"[U+{char.ConvertToUtf32(glyph, 0):X}]";

    /// <summary>选中一行并等它成为当前照片、新一代际的展示图已上屏。</summary>
    private static async System.Threading.Tasks.Task SelectAndWaitAsync(MainViewModel main, PhotoLoadSession session, ImageFile file)
    {
        int generation = session.Generation;
        main.FileVM.ThumbnailList.SelectImageCommand(file);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (ReferenceEquals(main.CurrentFile, file) && session.Generation > generation && session.Image != null) return;
            await System.Threading.Tasks.Task.Delay(100);
        }
    }

    /// <summary>生成两张同尺寸、同方向、内容不同的 JPEG（写入应用内部目录，按尺寸分文件夹复用）。</summary>
    private static string EnsureCachedSwitchFixtures(Activity activity, int width, int height)
    {
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, $"pv-check-switch-{width}");
        Directory.CreateDirectory(folder);
        for (int index = 0; index < 2; index++)
        {
            var path = IOPath.Combine(folder, index == 0 ? "switch-a.jpg" : "switch-b.jpg");
            if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
            using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(bitmap);
            using var paint = new Paint();
            paint.Color = index == 0 ? Color.Rgb(40, 90, 160) : Color.Rgb(160, 90, 40);
            canvas.DrawRect(0, 0, width, height, paint);
            using var stream = File.Create(path);
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, stream);
        }
        return folder;
    }

    /// <summary>瓦片中心（像素），按当前面板布局换算。</summary>
    private static (float X, float Y) FrameCenterPx(AndroidAnalysisPanel pane, AnalysisTile tile, double density)
    {
        var frame = pane.Frames[tile];
        return ((float)((frame.X + frame.Width / 2) * density), (float)((frame.Y + frame.Height / 2) * density));
    }

    /// <summary>统计缩放预设中指定文本的项数（重复值允许）。</summary>
    private static int CountPresetText(SettingsViewModel settings, string text)
    {
        int count = 0;
        foreach (var preset in settings.ScalePresets)
            if (preset.Text == text) count++;
        return count;
    }

    /// <summary>构造直接触控事件。</summary>
    private static MotionEvent TouchEvent(MotionEventActions action, float x, float y, long time)
    {
        var e = MotionEvent.Obtain(time, time, action, x, y, 0);
        e?.SetSource(InputSourceType.Touchscreen);
        return e!;
    }

    /// <summary>构造指定来源的悬停事件（用于鼠标/触控板与伪触屏对照）。</summary>
    private static MotionEvent HoverEvent(MotionEventActions action, float x, float y, long time, InputSourceType source)
    {
        var e = MotionEvent.Obtain(time, time, action, x, y, 0);
        e?.SetSource(source);
        return e!;
    }

    private static string DescribeRow(AndroidSettingsReorderList.RowSnapshot? row)
        => row is { } value
            ? $"bindings={value.BindingControlsVisible} subtitle={value.SubtitleVisible} ax={value.AccessibilityText} chips={value.PrimaryChipText}|{value.SecondaryChipText}"
            : "row=null";

    /// <summary>从设置页几何快照（<c>key=value</c> 空格分隔）取一项数值；缺失或不可解析返回 NaN。</summary>
    private static double GeometryValue(string snapshot, string key)
    {
        foreach (string part in snapshot.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = part.IndexOf('=');
            if (split <= 0 || !string.Equals(part[..split], key, StringComparison.Ordinal)) continue;
            return double.TryParse(part[(split + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : double.NaN;
        }
        return double.NaN;
    }

    /// <summary>在设置页真实视图树里按谓词深度优先查找第一个匹配视图（只读查询）。</summary>
    private static T? FindDescendant<T>(View root, Func<T, bool> match) where T : View
    {
        if (root is not ViewGroup group) return null;
        for (int index = 0; index < group.ChildCount; index++)
        {
            var child = group.GetChildAt(index);
            if (child == null) continue;
            if (child is T candidate && match(candidate)) return candidate;
            if (FindDescendant(child, match) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// 控制栏功能行的勾选命中区宽度（像素）：按命令图标定位到该行，取同行的勾选控件宽度；
    /// 找不到返回 null（图标行未实现或文本不匹配）。
    /// </summary>
    private static int? HotkeyCheckHitWidthPx(AndroidSettingsSheetView sheet, string symbol)
    {
        if (FindDescendant<AndroidCommandIconView>(sheet, view => view.Symbol == symbol) is not { } icon
            || icon.Parent is not ViewGroup line) return null;
        for (int index = 0; index < line.ChildCount; index++)
        {
            if (line.GetChildAt(index) is global::Android.Widget.CompoundButton check && check.Width > 0)
                return check.Width;
        }
        return null;
    }

    /// <summary>第一行倍率预设“输入框 + %”组的右缘（像素，相对行左缘）；找不到返回 null。</summary>
    private static int? PresetGroupRightEdgePx(AndroidSettingsSheetView sheet)
    {
        if (FindDescendant<global::Android.Widget.EditText>(sheet,
                view => view.ContentDescription == "缩放比例百分比") is not { } input
            || input.Parent is not ViewGroup row) return null;
        for (int index = 0; index < row.ChildCount; index++)
        {
            if (row.GetChildAt(index) is global::Android.Widget.TextView text && text.Text == "%")
                return text.Right;
        }
        return null;
    }

    /// <summary>构造多指事件，用于 owner 指针与旧 PointerUp 断言。</summary>
    private static MotionEvent MultiTouchEvent(long downTime, long eventTime, MotionEventActions action,
        int actionIndex, (int Id, float X, float Y)[] pointers)
    {
        var properties = new MotionEvent.PointerProperties[pointers.Length];
        var coordinates = new MotionEvent.PointerCoords[pointers.Length];
        for (int index = 0; index < pointers.Length; index++)
        {
            properties[index] = new MotionEvent.PointerProperties
            {
                Id = pointers[index].Id,
                ToolType = MotionEventToolType.Finger,
            };
            coordinates[index] = new MotionEvent.PointerCoords
            {
                X = pointers[index].X,
                Y = pointers[index].Y,
                Pressure = 1,
                Size = 1,
            };
        }
        int actionValue = (int)action;
        if (action is MotionEventActions.PointerDown or MotionEventActions.PointerUp)
            actionValue |= actionIndex << (int)MotionEventActions.PointerIndexShift;
        return MotionEvent.Obtain(downTime, eventTime, (MotionEventActions)actionValue, pointers.Length,
            properties, coordinates, MetaKeyStates.None, (MotionEventButtonState)0, 1, 1, 0,
            (Edge)0, InputSourceType.Touchscreen, MotionEventFlags.None)!;
    }

    /// <summary>生成确定性的 Sony 对焦 JPEG（自有权重夹具，写入应用内部目录）。</summary>
    private static string EnsureSonyFocusFixture(Activity activity)
    {
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-focus");
        Directory.CreateDirectory(folder);
        var path = IOPath.Combine(folder, "sony-focus.jpg");
        const int width = 1600, height = 1200, focusX = 1200, focusY = 300, frameWidth = 320, frameHeight = 240;
        using (var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!)
        using (var canvas = new Canvas(bitmap))
        using (var paint = new Paint())
        {
            paint.Color = Color.Red!;
            canvas.DrawRect(0, 0, 800, 600, paint);
            paint.Color = Color.Lime!;
            canvas.DrawRect(800, 0, 1600, 600, paint);
            paint.Color = Color.Blue!;
            canvas.DrawRect(0, 600, 800, 1200, paint);
            paint.Color = Color.Yellow!;
            canvas.DrawRect(800, 600, 1600, 1200, paint);
            using var encoded = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 92, encoded);
            var jpeg = encoded.ToArray();
            using var stream = File.Create(path);
            stream.Write(jpeg, 0, 2);
            stream.Write(BuildSonyFocusApp1(width, height, focusX, focusY, frameWidth, frameHeight));
            stream.Write(jpeg, 2, jpeg.Length - 2);
        }
        return folder;
    }

    /// <summary>Sony MakerNote APP1（0x2027 对焦位置 / 0x2037 对焦框），与共享解析器契约一致。</summary>
    private static byte[] BuildSonyFocusApp1(int width, int height, int focusX, int focusY,
        int frameWidth, int frameHeight)
    {
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, Encoding.ASCII, true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write(8u);

            writer.Write((ushort)2);
            writer.Write((ushort)0x0112);
            writer.Write((ushort)3);
            writer.Write(1u);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)0x8769);
            writer.Write((ushort)4);
            writer.Write(1u);
            writer.Write(38u);
            writer.Write(0u);

            writer.Write((ushort)1);
            writer.Write((ushort)0x927C);
            writer.Write((ushort)7);
            writer.Write(56u);
            writer.Write(56u);
            writer.Write(0u);

            writer.Write(Encoding.ASCII.GetBytes("SONY DSC "));
            writer.Write(new byte[3]);
            writer.Write((ushort)2);
            writer.Write((ushort)0x2027);
            writer.Write((ushort)3);
            writer.Write(4u);
            writer.Write(98u);
            writer.Write((ushort)0x2037);
            writer.Write((ushort)3);
            writer.Write(3u);
            writer.Write(106u);
            writer.Write(0u);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((ushort)focusX);
            writer.Write((ushort)focusY);
            writer.Write((ushort)frameWidth);
            writer.Write((ushort)frameHeight);
            writer.Write((ushort)1);
        }

        var tiffBytes = tiff.ToArray();
        var payload = new byte[6 + tiffBytes.Length];
        Encoding.ASCII.GetBytes("Exif").CopyTo(payload, 0);
        tiffBytes.CopyTo(payload, 6);
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        int length = payload.Length + 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)(length & 0xFF);
        payload.CopyTo(segment, 4);
        return segment;
    }

    private static void Check(string name, bool condition, string detail)
    {
        if (condition) _passed++;
        else _failed++;
        Lines.Add($"{(condition ? "PASS" : "FAIL")} {name} — {detail}");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < .6;

    /// <summary>当前检查模式是否包含某一组检查。</summary>
    private static bool Wants(string group) => _mode == group || _mode == "all";

    /// <summary>
    /// inline 主流程（<c>main</c> / <c>all</c>）的阶段日志：与独立模式的步骤日志同前缀，
    /// 崩溃时用最后一条把位置收窄到组边界。
    /// </summary>
    private static void Phase(string name) => Console.WriteLine($"[NativeCheck] 阶段：{name}（{_mode}）");

    /// <summary>
    /// 独立模式（<c>faults</c> / <c>layout</c> / <c>input</c> / <c>storage</c> / <c>filter</c>）：精确模式名匹配、自带素材与流程，
    /// 不并入 <c>all</c>（现有 <c>all</c> 五组连跑在该模拟器上偶发 SIGSEGV）。
    /// </summary>
    private static bool IsStandaloneMode => _mode is "faults" or "layout" or "input" or "storage" or "filter" or "zoom";
}
