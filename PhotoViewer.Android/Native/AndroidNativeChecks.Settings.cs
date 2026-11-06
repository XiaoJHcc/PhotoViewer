using System;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 设置页体验验收（SETTINGS-04 / SETTINGS-15）：内容区手势、紧凑行高、分类箭头、勾选对齐、
/// 命令图标与页面脚注。手势断言向真实对话框窗口派发触摸，几何断言读取真实行视图。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>跑设置页体验检查；最后的标题拖动会走真实关闭路径，调用后 sheet 已关闭。</summary>
    /// <param name="sheet">当前设置 sheet（根页已构建）。</param>
    private static async Task RunSettingsExperienceChecksAsync(AndroidSettingsSheetView sheet)
    {
        try
        {
            await RunSettingsExperienceCoreAsync(sheet);
        }
        catch (Exception exception)
        {
            Check("设置体验检查（中止）", false, exception.GetType().Name + "：" + exception.Message);
        }
        finally
        {
            AndroidInputDevices.SetChecksOverride(null, null);
        }
    }

    /// <summary>设置页体验检查主体。</summary>
    /// <param name="sheet">当前设置 sheet。</param>
    private static async Task RunSettingsExperienceCoreAsync(AndroidSettingsSheetView sheet)
    {
        var activity = _activity!;
        await Task.Delay(200);
        // SETTINGS-15：48dp 只用于单行紧凑内容；普通设置行、分类行与工具行取标准行高。
        int compactRow = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidListRowMinHeight);
        int standardRow = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidRowMinHeight);
        int rowPaddingRight = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidRowPaddingRight);
        int cardPadding = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidCardPaddingX);
        int chevronWidth = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidNavigationChevronWidth);
        int checkHit = (int)AndroidUiTheme.Dp(activity, AndroidSettingsReorderList.CheckHitSizeDp);

        // 分类导航：标准行高（两行文本随系统字体增高），20dp 居中箭头，右留白为卡片内边距。
        var categoryTitle = FindDescendant<TextView>(sheet, view => view.Text == "文件");
        var categoryRow = categoryTitle?.Parent?.Parent as ViewGroup;
        var chevron = categoryRow == null ? null : FindDescendant<TextView>(categoryRow, view => view.Text == "›");
        int categoryContentHeight = Math.Max((categoryTitle?.Parent as View)?.Height ?? 0, chevron?.Height ?? 0);
        int categoryHeight = Math.Max(standardRow, categoryContentHeight
            + (categoryRow?.PaddingTop ?? 0) + (categoryRow?.PaddingBottom ?? 0));
        Check("分类行按标准行高且随字体增高",
            categoryRow != null && categoryRow.MinimumHeight == standardRow && Math.Abs(categoryRow.Height - categoryHeight) <= 1,
            categoryRow == null ? "row=null" : $"{categoryRow.Height}px expected={categoryHeight}px min={standardRow}px");
        Check("分类箭头 20dp 容器居中且右留白为行内边距",
            categoryRow != null && chevron != null
            && Math.Abs(chevron.Width - chevronWidth) <= 1
            && (chevron.Gravity & GravityFlags.HorizontalGravityMask) == GravityFlags.CenterHorizontal
            && Math.Abs(categoryRow.Width - (chevron.Left + chevron.Width / 2.0)
                - (rowPaddingRight + chevronWidth / 2.0)) <= 1,
            categoryRow == null || chevron == null
                ? "chevron=null"
                : $"rowWidth={categoryRow.Width} chevronLeft={chevron.Left} chevronWidth={chevron.Width}"
                    + $" gravity={chevron.Gravity} expectedWidth={chevronWidth} expectedCenterFromRight={rowPaddingRight + chevronWidth / 2.0}");

        // 纯触控控制列表：单行 48dp；勾选可见字形左缘对齐 16dp，命中区仍是 44dp。
        sheet.ShowPageForChecks("control");
        await Task.Delay(300);
        AndroidInputDevices.SetChecksOverride(false, false);
        await Task.Delay(350);
        var controlRow = sheet.HotkeyRowLayoutForChecks(0);
        Check("纯触控控制行统一 48dp 最小行高",
            controlRow is { } row0 && Math.Abs(row0.Height - compactRow) <= 1 && row0.MinHeight == compactRow,
            LayoutText(controlRow, $"{compactRow}px"));
        Check("勾选字形左缘对齐卡片 16dp",
            controlRow is { } row1 && Math.Abs(row1.CheckGlyphLeft - cardPadding) <= 1,
            LayoutText(controlRow, $"{cardPadding}px"));
        Check("勾选命中区保持 44dp",
            controlRow is { } row2 && Math.Abs(row2.CheckHitWidth - checkHit) <= 1,
            LayoutText(controlRow, $"{checkHit}px"));
        Check("命令图标 18sp 且未被二次放大、读屏忽略 PUA 字形",
            controlRow is { } row3 && row3.IconVisible
            && Math.Abs(row3.IconFontSizeSp - AndroidCommandIconView.GlyphSizeSp) < .01
            && Math.Abs(row3.IconTextSizePx - AndroidUiTheme.Sp(activity, AndroidCommandIconView.GlyphSizeSp)) <= 1
            && row3.IconIgnoredForAccessibility,
            LayoutText(controlRow, $"glyph={AndroidCommandIconView.GlyphSizeSp}sp"
                + $"={AndroidUiTheme.Sp(activity, AndroidCommandIconView.GlyphSizeSp):0.#}px"));

        var hotkeys = sheet.Settings.Hotkeys;
        var rating3 = sheet.HotkeyRowLayoutForChecks(HotkeyIndexOf(hotkeys, "SetRating3"));
        Check("评分图标为实心星叠数字 3",
            rating3 is { } r3 && r3.IconVisible && r3.IconSolid && r3.IconDigit == "3"
            && r3.IconSymbol == AndroidUiTheme.IconGlyph(DesignTokens.IconStarFilled),
            LayoutText(rating3, "solid star + 3"));
        var rating0 = sheet.HotkeyRowLayoutForChecks(HotkeyIndexOf(hotkeys, "SetRating0"));
        Check("0 星图标为清零叉",
            rating0 is { } r0 && r0.IconVisible && !r0.IconSolid && r0.IconDigit == null
            && r0.IconSymbol == AndroidUiTheme.IconGlyph(DesignTokens.IconClear),
            LayoutText(rating0, "clear glyph"));
        var enhance = sheet.HotkeyRowLayoutForChecks(HotkeyIndexOf(hotkeys, "ToggleEnhance"));
        Check("雾天增强行显示命令图标",
            enhance is { } en && en.IconVisible
            && en.IconSymbol == AndroidUiTheme.IconGlyph(DesignTokens.IconCommandToggleEnhance),
            LayoutText(enhance, DesignTokens.IconCommandToggleEnhance));

        int realized = 0;
        bool everyIcon = true;
        for (int index = 0; index < hotkeys.Count; index++)
        {
            if (sheet.HotkeyRowLayoutForChecks(index) is not { } layout) continue;
            realized++;
            if (!layout.IconVisible) everyIcon = false;
        }
        Check("控制列表完整展示全部命令及图标", everyIcon && realized == hotkeys.Count, $"{realized}/{hotkeys.Count} 行已实现");

        // 连接外设时两行绑定完整显示，行高按内容增高。
        AndroidInputDevices.SetChecksOverride(true, false);
        await Task.Delay(350);
        var boundRow = sheet.HotkeyRowLayoutForChecks(0);
        Check("外设绑定行按标准行高增高且完整容纳绑定文本",
            boundRow is { } bound && bound.MinHeight == standardRow && bound.Height > standardRow
            && !bound.PrimaryChipEllipsized && !bound.SecondaryChipEllipsized,
            LayoutText(boundRow, $">{standardRow}px"));
        AndroidInputDevices.SetChecksOverride(null, null);
        await Task.Delay(250);

        // 文件格式行：副标题（扩展名）完整显示，行高随多行内容增高。
        sheet.ShowPageForChecks("files");
        await Task.Delay(300);
        var formatRow = sheet.FormatRowLayoutForChecks(1);
        Check("文件格式行恢复标准行高且副标题完整",
            formatRow is { } format && format.MinHeight == standardRow && format.Height >= standardRow
            && format.SubtitleVisible && !format.TitleEllipsized && !format.SubtitleEllipsized,
            LayoutText(formatRow, $">={standardRow}px subtitle visible"));

        // SETTINGS-06：文件页两条能力提示按内容换行（不省略、不截断）；Android 隐藏用户缓存上限，
        // 能力数值恒为常规级、不告警；文案不再含 CPU 核数、预载需求与勾号。
        var infoLabels = sheet.FileInfoLabelsForChecks;
        Check("文件页内存提示两行就位", infoLabels.Count == 2, $"count={infoLabels.Count}");
        bool infoWraps = infoLabels.Count == 2;
        string infoGeometry = string.Empty;
        string longProbe = new string('适', 240);
        for (int index = 0; index < infoLabels.Count; index++)
        {
            var label = infoLabels[index];
            if (label.Width <= 0)
            {
                infoWraps = false;
                infoGeometry += $" [{index}]未布局";
                continue;
            }
            string original = label.Text ?? string.Empty;
            var width = View.MeasureSpec.MakeMeasureSpec(label.Width, MeasureSpecMode.Exactly);
            var height = View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified);
            label.Text = longProbe;
            label.Measure(width, height);
            infoWraps &= label.MeasuredHeight > label.LineHeight;
            infoGeometry += $" [{index}]w={label.Width} h={label.MeasuredHeight} line={label.LineHeight}";
            label.Text = original;
            label.Measure(width, height);
        }
        Check("内存提示行按内容换行", infoWraps, infoGeometry);

        var budgetLevel = sheet.Settings.PerformanceBudgetInfoLevel;
        var countLevel = sheet.Settings.BitmapCacheCountInfoLevel;
        Check("自动口径下内存提示不告警",
            !sheet.Settings.ShowsCacheLimits
            && budgetLevel == PerfInfoLevel.Normal && countLevel == PerfInfoLevel.Normal,
            $"showsCacheLimits={sheet.Settings.ShowsCacheLimits} perf={budgetLevel} cache={countLevel}");
        Check("提示语义色按级别接线",
            AndroidSettingsSheetView.LevelColorForChecks(PerfInfoLevel.Normal).ToArgb() == AndroidUiTheme.TextSecondary.ToArgb()
            && AndroidSettingsSheetView.LevelColorForChecks(PerfInfoLevel.Warning).ToArgb() == AndroidUiTheme.WarningText.ToArgb()
            && AndroidSettingsSheetView.LevelColorForChecks(PerfInfoLevel.Error).ToArgb() == AndroidUiTheme.ErrorText.ToArgb(),
            "常规=次要文字 / 警示=琥珀 / 错误=红");
        string budgetText = infoLabels.Count > 0 ? infoLabels[0].Text ?? string.Empty : string.Empty;
        string countText = infoLabels.Count > 1 ? infoLabels[1].Text ?? string.Empty : string.Empty;
        Check("内存提示为系统读数与两档张数",
            budgetText.Contains("系统内存") && budgetText.Contains("当前可用")
            && countText.Contains("适屏照片") && countText.Contains("3300 万像素照片")
            && !countText.Contains("最大") && !countText.Contains("缓存能力"),
            budgetText + " / " + countText);
        string infoText = budgetText + " / " + countText;
        Check("内存提示不再含 CPU 核数/预载需求/勾号",
            budgetText.Length > 0 && countText.Length > 0
            && !infoText.Contains("CPU") && !infoText.Contains("预载与对比") && !infoText.Contains("✓"),
            infoText);

        // EXIF 显示表：单行 48dp、勾选对齐，且不再有说明脚注。
        sheet.ShowPageForChecks("exif");
        await Task.Delay(300);
        var exifRow = sheet.ExifRowLayoutForChecks(0);
        Check("EXIF 单行列表统一 48dp 最小行高",
            exifRow is { } exif && Math.Abs(exif.Height - compactRow) <= 1 && exif.MinHeight == compactRow,
            LayoutText(exifRow, $"{compactRow}px"));
        Check("EXIF 行勾选字形左缘对齐 16dp",
            exifRow is { } exifAlign && Math.Abs(exifAlign.CheckGlyphLeft - cardPadding) <= 1,
            LayoutText(exifRow, $"{cardPadding}px"));
        Check("EXIF 显示表无脚注", !HasText(sheet, "拖动手柄排序"), HasText(sheet, "拖动手柄排序") ? "仍有脚注" : "无脚注");

        // 缩放预设行：48dp，不再有百分比说明脚注。
        sheet.ShowPageForChecks("preview");
        await Task.Delay(300);
        int presetHeight = sheet.ScalePresetRowHeightForChecks(0);
        Check("缩放预设行统一 48dp 最小行高", Math.Abs(presetHeight - compactRow) <= 1,
            $"{presetHeight}px expected={compactRow}px");
        bool percentNote = HasText(sheet, "物理 100") || HasText(sheet, "100 = 物理") || HasText(sheet, "单位为百分比");
        Check("缩放预设无百分比说明脚注", !percentNote, percentNote ? "仍有脚注" : "无脚注");

        // 控制表与 EXIF 表同一行结构，脚注同样删除。
        sheet.ShowPageForChecks("control");
        await Task.Delay(300);
        Check("控制表无脚注", !HasText(sheet, "拖动手柄排序"),
            HasText(sheet, "拖动手柄排序") ? "仍有脚注" : "无脚注");

        await CheckSheetGesturesAsync(sheet);
    }

    /// <summary>
    /// SETTINGS-04：向真实对话框窗口派发触摸，断言内容区上下滑只滚动内容、内容到顶下滑不收起，
    /// 最后从标题栏拖下关闭 sheet。
    /// </summary>
    /// <param name="sheet">当前设置 sheet。</param>
    private static async Task CheckSheetGesturesAsync(AndroidSettingsSheetView sheet)
    {
        var activity = _activity!;
        sheet.ShowPageForChecks("files");
        await Task.Delay(350);

        var dialog = AndroidNativeSettingsPresenter.CurrentDialog;
        var decor = dialog?.Window?.DecorView;
        var container = sheet.Parent as View;
        var scroll = sheet.ScrollHostForChecks;
        Check("设置内容区手势环境可读取",
            decor != null && container != null && scroll != null,
            $"decor={decor != null} container={container != null} scroll={scroll != null}");
        if (decor == null || container == null || scroll == null) return;
        BottomSheetBehavior? behavior = null;
        try
        {
            behavior = BottomSheetBehavior.From(container);
        }
        catch (Exception exception)
        {
            Check("设置 sheet 行为可读取", false, exception.Message);
            return;
        }
        if (behavior == null)
        {
            Check("设置 sheet 行为可读取", false, "behavior=null");
            return;
        }

        // SETTINGS-04：默认以半屏收起展示（不是先展开再闪回），内容高度仍是屏高 × 0.85。
        int screenHeight = activity.Resources?.DisplayMetrics?.HeightPixels ?? 0;
        var sheetParent = container.Parent as View;
        int peek = behavior.PeekHeight;
        int expectedPeek = (int)(screenHeight * DesignTokens.SettingsAndroidSheetPeekRatio);
        int expectedExpandedHeight = (int)(screenHeight * DesignTokens.SettingsAndroidSheetHeightRatio);
        int halfTolerance = Math.Max(2, (int)(screenHeight * .01));
        Check("设置 sheet 初始半屏收起",
            behavior.State == BottomSheetBehavior.StateCollapsed
            && Math.Abs(peek - expectedPeek) <= halfTolerance
            && sheetParent != null && Math.Abs(container.Top - (sheetParent.Height - peek)) <= halfTolerance,
            $"state={behavior.State} top={container.Top} parentHeight={sheetParent?.Height ?? -1}"
                + $" peek={peek} expectedTop={(sheetParent?.Height ?? 0) - peek}"
                + $" expectedPeek={expectedPeek} screen={screenHeight}");

        // SETTINGS-04：收起态内容布局只占可见的半屏，ScrollView 高度与可见区域一致；
        // 修复前内层仍按 85% 量测，底部内容落在屏外，真实上滑到末尾也看不到最后一项。
        int collapsedTop = container.Top;
        Check("收起态内容布局高度等于可见区域",
            SheetViewportMatches(sheet, container, scroll), SheetViewportText(sheet, container, scroll));
        var collapsedBounds = SheetScrollBounds(activity, decor, scroll);
        var collapsedGesture = await DragSheetToEndAsync(decor, scroll, collapsedBounds.X,
            collapsedBounds.Top, collapsedBounds.Bottom);
        Check("收起态真实上滑可到文件页末尾且最后一项完整可见",
            collapsedGesture.Ok && AndroidNativeSettingsPresenter.IsOpen
            && behavior.State == BottomSheetBehavior.StateCollapsed && Math.Abs(container.Top - collapsedTop) <= 1,
            collapsedGesture.Detail + $" state={behavior.State} top={container.Top}/{collapsedTop}"
                + $" open={AndroidNativeSettingsPresenter.IsOpen}");

        // 收起态的控制页 / EXIF / 缩放预设页：长表到末尾时最后一项仍完整落在可见滚动范围内。
        sheet.ShowPageForChecks("control");
        await Task.Delay(350);
        scroll.ScrollTo(0, 0);
        Check("收起态控制页内容高度等于可见区域",
            SheetViewportMatches(sheet, container, scroll), SheetViewportText(sheet, container, scroll));
        var controlTail = await ScrollSheetToEndAsync(scroll);
        Check("收起态控制页长表底部完整可见", controlTail.Ok, controlTail.Detail);

        sheet.ShowPageForChecks("exif");
        await Task.Delay(350);
        scroll.ScrollTo(0, 0);
        var exifTail = await ScrollSheetToEndAsync(scroll);
        Check("收起态 EXIF 页底部完整可见", exifTail.Ok, exifTail.Detail);

        sheet.ShowPageForChecks("preview");
        await Task.Delay(350);
        scroll.ScrollTo(0, 0);
        bool previewScrollable = (scroll.GetChildAt(0)?.Height ?? 0) > scroll.Height;
        var previewTail = await ScrollSheetToEndAsync(scroll);
        Check("收起态缩放预设页底部可达", !previewScrollable || previewTail.Ok,
            $"scrollable={previewScrollable} {previewTail.Detail}");

        sheet.ShowPageForChecks("files");
        await Task.Delay(350);
        scroll.FullScroll(FocusSearchDirection.Up);
        await Task.Delay(150);

        var header = sheet.HeaderForChecks;
        var headerLocation = new int[2];
        header.GetLocationInWindow(headerLocation);
        // 收起态从标题上拖：扩大到内容高度（屏高 × 0.85），不是直接关闭；拖动终点仍留在屏内。
        // 中途停住不抬起，先断言拖动中的内容高度已跟随当时的可见区域（不是只在动画结束后才修正）。
        float headerX = headerLocation[0] + header.Width / 2f;
        float headerY = headerLocation[1] + header.Height / 2f;
        float dragDistance = container.Height * .5f;
        long downTime = SystemClock.UptimeMillis();
        DispatchTouch(decor, MotionEventActions.Down, headerX, headerY, downTime, downTime);
        for (int step = 1; step <= 4; step++)
            DispatchTouch(decor, MotionEventActions.Move, headerX, headerY - dragDistance * step / 10f,
                downTime, downTime + step * 16);
        await Task.Delay(180);
        int expandedTopNow = sheetParent == null ? 0 : Math.Max(0, sheetParent.Height - container.Height);
        Check("拖动中内容高度跟随可见区域",
            container.Top < collapsedTop - 1 && container.Top > expandedTopNow + 1
            && SheetViewportMatches(sheet, container, scroll),
            $"top={container.Top} collapsedTop={collapsedTop} expandedTop={expandedTopNow} "
                + SheetViewportText(sheet, container, scroll));
        for (int step = 5; step <= 10; step++)
            DispatchTouch(decor, MotionEventActions.Move, headerX, headerY - dragDistance * step / 10f,
                downTime, downTime + step * 16);
        DispatchTouch(decor, MotionEventActions.Up, headerX, headerY - dragDistance,
            downTime, downTime + 11 * 16);
        await Task.Delay(900);
        int expandedTop = sheetParent == null ? 0 : Math.Max(0, sheetParent.Height - container.Height);
        Check("标题上拖扩大到原 85% 高度",
            behavior.State == BottomSheetBehavior.StateExpanded
            && Math.Abs(container.Height - expectedExpandedHeight) <= 2
            && Math.Abs(container.Top - expandedTop) <= 2
            && AndroidNativeSettingsPresenter.IsOpen,
            $"state={behavior.State} top={container.Top} expected={expandedTop}"
                + $" height={container.Height} expectedHeight={expectedExpandedHeight}"
                + $" headerY={headerLocation[1] + header.Height / 2f:0}"
                + $" open={AndroidNativeSettingsPresenter.IsOpen}");

        // SETTINGS-04：展开态内层高度回到 85% 内容高度，文件页末尾同样完整可见。
        Check("展开态内容布局高度等于可见区域",
            SheetViewportMatches(sheet, container, scroll), SheetViewportText(sheet, container, scroll));
        var expandedTail = await ScrollSheetToEndAsync(scroll);
        Check("展开态文件页末尾完整可见", expandedTail.Ok, expandedTail.Detail);

        bool scrollable = scroll.ChildCount > 0 && scroll.GetChildAt(0)!.Height > scroll.Height;
        await ResetSheetScrollAsync(scroll);
        var location = new int[2];
        scroll.GetLocationInWindow(location);
        // 只在滚动区可见范围内起手，事件从窗口根派发，走真实拦截链。
        int visibleTop = Math.Max(location[1], 0);
        int visibleBottom = Math.Min(location[1] + scroll.Height, decor.Height);
        float scrollCenterY = (visibleTop + visibleBottom) / 2f;
        // 落在卡片左侧的空白内边距上，避免子控件吃掉手势。
        float scrollX = location[0] + AndroidUiTheme.Dp(activity, 4);
        int topBefore = container.Top;
        int stateBefore = behavior.State;

        DispatchDrag(decor, scrollX, scrollCenterY, 0, -scroll.Height * .6f);
        await Task.Delay(450);
        Check("内容上滑只滚动内容不动 sheet",
            scrollable && scroll.ScrollY > 0 && behavior.State == stateBefore
            && Math.Abs(container.Top - topBefore) <= 1 && AndroidNativeSettingsPresenter.IsOpen,
            $"scrollable={scrollable} scrollY={scroll.ScrollY} state={behavior.State}/{stateBefore}"
                + $" top={container.Top}/{topBefore} open={AndroidNativeSettingsPresenter.IsOpen}");

        // 结束上一步可能仍在跑的惯性，再从顶端向下拖。
        await ResetSheetScrollAsync(scroll);
        int topAtRest = container.Top;
        int stateAtRest = behavior.State;
        DispatchDrag(decor, scrollX, visibleTop + (visibleBottom - visibleTop) * .2f, 0, scroll.Height * .7f);
        await Task.Delay(500);
        Check("内容已到顶下滑不收起 sheet",
            scroll.ScrollY == 0 && behavior.State == stateAtRest && Math.Abs(container.Top - topAtRest) <= 1
            && AndroidNativeSettingsPresenter.IsOpen,
            $"scrollY={scroll.ScrollY} state={behavior.State}/{stateAtRest}"
                + $" top={container.Top}/{topAtRest} open={AndroidNativeSettingsPresenter.IsOpen}");

        // 展开后再收起：内层高度回到半屏可见区域，文件页底部仍然完整可达。
        behavior.State = BottomSheetBehavior.StateCollapsed;
        await Task.Delay(800);
        int reCollapsedTop = sheetParent == null ? 0 : sheetParent.Height - peek;
        Check("重新收起后内容高度回到可见区域",
            behavior.State == BottomSheetBehavior.StateCollapsed
            && Math.Abs(container.Top - reCollapsedTop) <= 2
            && SheetViewportMatches(sheet, container, scroll),
            $"state={behavior.State} top={container.Top} expected={reCollapsedTop} "
                + SheetViewportText(sheet, container, scroll));
        var reCollapsedTail = await ScrollSheetToEndAsync(scroll);
        Check("重新收起后文件页底部仍完整可见", reCollapsedTail.Ok, reCollapsedTail.Detail);

        header.GetLocationInWindow(headerLocation);
        DispatchDrag(decor, headerLocation[0] + header.Width / 2f,
            headerLocation[1] + header.Height / 2f, 0, container.Height * .8f);
        await Task.Delay(1200);
        Check("标题栏拖下关闭 sheet", !AndroidNativeSettingsPresenter.IsOpen,
            AndroidNativeSettingsPresenter.IsOpen ? "仍打开" : "已关闭");
    }

    /// <summary>
    /// 半屏面板当前可见高度（px）：内容顶端到窗口可见底边（容器有底部 inset 内边距时一并扣除）。
    /// 收起、拖动、展开与窗口/insets 变化后，内层内容布局应当正好占这段高度。
    /// </summary>
    private static int SheetVisibleHeightPx(View content, View? container)
    {
        if (content.Height <= 0) return 0;
        int[] contentLocation = new int[2];
        content.GetLocationInWindow(contentLocation);
        int bottom = content.RootView?.Height ?? 0;
        if (container is { Height: > 0 })
        {
            int[] containerLocation = new int[2];
            container.GetLocationInWindow(containerLocation);
            int containerBottom = containerLocation[1] + container.Height - container.PaddingBottom;
            bottom = bottom > 0 ? Math.Min(bottom, containerBottom) : containerBottom;
        }
        // 浮层宿主（CoordinatorLayout）已按系统栏内缩，真正可用底边以宿主为准；
        // 只看窗口高度会把状态栏/导航栏占据的区域算进可见高度。
        if (container?.Parent is View host && host.Height > 0)
        {
            int[] hostLocation = new int[2];
            host.GetLocationInWindow(hostLocation);
            int hostBottom = hostLocation[1] + host.Height - host.PaddingBottom;
            bottom = bottom > 0 ? Math.Min(bottom, hostBottom) : hostBottom;
        }
        return Math.Clamp(bottom - contentLocation[1], 0, content.Height);
    }

    /// <summary>
    /// 半屏可见约束：内层根布局高度等于面板可见高度，ScrollView 正好填满根布局剩余区域，
    /// 且它的全局可见矩形未被屏外裁切。
    /// </summary>
    private static bool SheetViewportMatches(View content, View? container, View scroll)
    {
        int visible = SheetVisibleHeightPx(content, container);
        var root = content is ViewGroup contentGroup ? contentGroup.GetChildAt(0) : null;
        if (root == null || root.Height <= 0 || scroll.Height <= 0) return false;
        int expectedScroll = Math.Max(0, root.Height - scroll.Top);
        var rect = new Rect();
        bool fullyVisible = scroll.GetGlobalVisibleRect(rect)
            && rect.Width() == scroll.Width && rect.Height() == scroll.Height;
        int[] location = new int[2];
        scroll.GetLocationInWindow(location);
        int windowHeight = content.RootView?.Height ?? 0;
        bool onScreen = windowHeight > 0 && location[1] >= 0
            && location[1] + scroll.Height <= windowHeight;
        return Math.Abs(root.Height - visible) <= 1
            && Math.Abs(scroll.Height - expectedScroll) <= 1
            && fullyVisible && onScreen;
    }

    /// <summary>半屏可见约束的可读快照（报告用）。</summary>
    private static string SheetViewportText(View content, View? container, View scroll)
    {
        int visible = SheetVisibleHeightPx(content, container);
        var root = content is ViewGroup contentGroup ? contentGroup.GetChildAt(0) : null;
        var rect = new Rect();
        bool gotRect = scroll.GetGlobalVisibleRect(rect);
        int[] location = new int[2];
        scroll.GetLocationInWindow(location);
        int windowHeight = content.RootView?.Height ?? 0;
        return $"visible={visible} root={root?.Height ?? -1} scrollTop={scroll.Top}"
            + $" scrollHeight={scroll.Height} expectedScroll={Math.Max(0, (root?.Height ?? 0) - scroll.Top)}"
            + $" global={rect.Width()}x{rect.Height()}(ok={gotRect})"
            + $" screen={location[1]}..{location[1] + scroll.Height}/{windowHeight}";
    }

    /// <summary>滚动区在窗口内的真实可见起止与左侧空白起手点。</summary>
    private static (float X, int Top, int Bottom) SheetScrollBounds(Activity activity, View decor, View scroll)
    {
        int[] location = new int[2];
        scroll.GetLocationInWindow(location);
        int top = Math.Max(location[1], 0);
        int bottom = Math.Min(location[1] + scroll.Height, decor.Height);
        float x = location[0] + AndroidUiTheme.Dp(activity, 4);
        return (x, top, bottom);
    }

    /// <summary>把内容滚到末尾（FullScroll 真滚动路径），返回末尾项是否完整落在可见滚动范围内。</summary>
    private static async Task<(bool Ok, string Detail)> ScrollSheetToEndAsync(ScrollView scroll)
    {
        scroll.FullScroll(FocusSearchDirection.Down);
        await Task.Delay(250);
        int max = Math.Max(0, (scroll.GetChildAt(0)?.Height ?? 0) - scroll.Height);
        if (scroll.ScrollY < max - 2)
        {
            scroll.ScrollTo(0, max);
            await Task.Delay(120);
        }
        return SheetTail(scroll);
    }

    /// <summary>
    /// 在可见滚动区内派发真实单指上滑直到末尾（最多 24 轮，连续两轮无进展即停），
    /// 用来断言收起态手持手势也能看到最后一项。
    /// </summary>
    private static async Task<(bool Ok, string Detail)> DragSheetToEndAsync(
        View decor, ScrollView scroll, float x, int visibleTop, int visibleBottom)
    {
        int height = visibleBottom - visibleTop;
        int max = Math.Max(0, (scroll.GetChildAt(0)?.Height ?? 0) - scroll.Height);
        float startY = visibleTop + height * .75f;
        float distance = height * .6f;
        int previous = -1;
        int stagnant = 0;
        int rounds = 0;
        for (; rounds < 24 && scroll.ScrollY < max - 2 && stagnant < 2; rounds++)
        {
            DispatchDrag(decor, x, startY, 0, -distance);
            await Task.Delay(260);
            if (scroll.ScrollY <= previous + 2) stagnant++;
            else stagnant = 0;
            previous = scroll.ScrollY;
        }
        var tail = SheetTail(scroll);
        return (tail.Ok, $"{rounds} 次上滑 " + tail.Detail);
    }

    /// <summary>等惯性/平滑滚动结束后把内容复位到顶端；单次 ScrollTo 会被仍在跑的滚动继续推进。</summary>
    private static async Task ResetSheetScrollAsync(ScrollView scroll)
    {
        int stable = 0;
        for (int attempt = 0; attempt < 40 && stable < 2; attempt++)
        {
            scroll.ScrollTo(0, 0);
            await Task.Delay(50);
            if (scroll.ScrollY == 0) stable++;
            else stable = 0;
        }
    }

    /// <summary>末尾项完整性：滚动已到底，且最后一行条目的全局可见矩形等于自身（未被裁切）。</summary>
    private static (bool Ok, string Detail) SheetTail(ScrollView scroll)
    {
        if (scroll.GetChildAt(0) is not ViewGroup content || content.ChildCount == 0) return (false, "tail=null");
        var last = LastSheetItem(content, scroll.Height);
        var rect = new Rect();
        bool tailVisible = last != null && last.Height > 0 && last.GetGlobalVisibleRect(rect)
            && rect.Width() == last.Width && rect.Height() == last.Height;
        int max = Math.Max(0, content.Height - scroll.Height);
        bool atEnd = scroll.ScrollY >= max - 2;
        return (tailVisible && atEnd,
            $"scrollY={scroll.ScrollY} max={max} content={content.Height} viewport={scroll.Height}"
            + $" tail={last?.GetType().Name ?? "null"} tailH={last?.Height ?? -1}"
            + $" tailGlobal={rect.Width()}x{rect.Height()}"
            + $" atEnd={atEnd} tailVisible={tailVisible}");
    }

    /// <summary>最后一个能整体放进可见高度的条目：从内容末尾逐层下钻，跳过高于视口的分组容器。</summary>
    private static View? LastSheetItem(ViewGroup content, int viewport)
    {
        View? current = content;
        while (current is ViewGroup group && group.ChildCount > 0)
        {
            var last = group.GetChildAt(group.ChildCount - 1);
            if (last == null) return current;
            if (last.Height <= viewport || last is not ViewGroup lastGroup || lastGroup.ChildCount == 0)
                return last;
            current = last;
        }
        return current;
    }

    /// <summary>按命令 id 找快捷键行索引；找不到返回 -1。</summary>
    private static int HotkeyIndexOf(System.Collections.Generic.IReadOnlyList<PhotoViewer.ViewModels.Settings.SettingsViewModel.HotkeyItem> hotkeys, string command)
    {
        for (int index = 0; index < hotkeys.Count; index++)
            if (hotkeys[index].Command == command) return index;
        return -1;
    }

    /// <summary>视图树里是否有包含指定文本的可见文本行。</summary>
    private static bool HasText(View root, string text)
        => FindDescendant<TextView>(root, view => (view.Text ?? string.Empty).Contains(text, StringComparison.Ordinal)) != null;

    /// <summary>行布局快照的可读描述。</summary>
    private static string LayoutText(AndroidSettingsReorderList.RowLayoutSnapshot? layout, string expected)
        => layout is { } value
            ? $"h={value.Height} min={value.MinHeight} checkGlyphLeft={value.CheckGlyphLeft}"
                + $" checkHit={value.CheckHitWidth} icon={DescribeGlyph(value.IconSymbol)}"
                + $" digit={value.IconDigit ?? "-"} solid={value.IconSolid} font={value.IconTextSizePx:0.#}px"
                + $" axIgnored={value.IconIgnoredForAccessibility} subtitle={value.SubtitleVisible}"
                + $" ellipsize(t/s/c)={value.TitleEllipsized}/{value.SubtitleEllipsized}"
                + $"/{value.PrimaryChipEllipsized}/{value.SecondaryChipEllipsized} expected={expected}"
            : "row=null";

    /// <summary>向视图派发一段单指竖直拖动（DOWN → MOVE×steps → UP），走完整拦截链。</summary>
    private static void DispatchDrag(View target, float x, float y, float deltaX, float deltaY, int steps = 10)
    {
        long down = SystemClock.UptimeMillis();
        DispatchTouch(target, MotionEventActions.Down, x, y, down, down);
        for (int step = 1; step <= steps; step++)
        {
            DispatchTouch(target, MotionEventActions.Move, x + deltaX * step / steps, y + deltaY * step / steps,
                down, down + step * 16);
        }
        DispatchTouch(target, MotionEventActions.Up, x + deltaX, y + deltaY, down, down + (steps + 1) * 16);
    }

    private static void DispatchTouch(View target, MotionEventActions action, float x, float y,
        long downTime, long eventTime)
    {
        using var e = MotionEvent.Obtain(downTime, eventTime, action, x, y, 0)!;
        e.SetSource(InputSourceType.Touchscreen);
        target.DispatchTouchEvent(e);
    }
}
