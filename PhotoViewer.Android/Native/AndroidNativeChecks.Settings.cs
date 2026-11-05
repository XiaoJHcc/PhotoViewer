using System;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using PhotoViewer.Core.Design;

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
        int minRow = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidListRowMinHeight);
        int cardPadding = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidCardPaddingX);
        int chevronWidth = (int)AndroidUiTheme.Dp(activity, DesignTokens.SettingsAndroidNavigationChevronWidth);
        int checkHit = (int)AndroidUiTheme.Dp(activity, AndroidSettingsReorderList.CheckHitSizeDp);

        // 分类导航：48dp 最小行高（两行文本随系统字体增高），20dp 居中箭头，右留白只留卡片内边距。
        var categoryTitle = FindDescendant<TextView>(sheet, view => view.Text == "文件");
        var categoryRow = categoryTitle?.Parent?.Parent as ViewGroup;
        var chevron = categoryRow == null ? null : FindDescendant<TextView>(categoryRow, view => view.Text == "›");
        int categoryContentHeight = Math.Max((categoryTitle?.Parent as View)?.Height ?? 0, chevron?.Height ?? 0);
        int categoryHeight = Math.Max(minRow, categoryContentHeight
            + (categoryRow?.PaddingTop ?? 0) + (categoryRow?.PaddingBottom ?? 0));
        Check("分类行按 48dp 最小行高收窄且随字体增高",
            categoryRow != null && categoryRow.MinimumHeight == minRow && Math.Abs(categoryRow.Height - categoryHeight) <= 1,
            categoryRow == null ? "row=null" : $"{categoryRow.Height}px expected={categoryHeight}px min={minRow}px");
        Check("分类箭头 20dp 容器居中且右留白收窄",
            categoryRow != null && chevron != null
            && Math.Abs(chevron.Width - chevronWidth) <= 1
            && (chevron.Gravity & GravityFlags.HorizontalGravityMask) == GravityFlags.CenterHorizontal
            && Math.Abs(categoryRow.Width - (chevron.Left + chevron.Width / 2.0)
                - (cardPadding + chevronWidth / 2.0)) <= 1,
            categoryRow == null || chevron == null
                ? "chevron=null"
                : $"rowWidth={categoryRow.Width} chevronLeft={chevron.Left} chevronWidth={chevron.Width}"
                    + $" gravity={chevron.Gravity} expectedWidth={chevronWidth} expectedCenterFromRight={cardPadding + chevronWidth / 2.0}");

        // 纯触控控制列表：单行 48dp；勾选可见字形左缘对齐 16dp，命中区仍是 44dp。
        sheet.ShowPageForChecks("control");
        await Task.Delay(300);
        AndroidInputDevices.SetChecksOverride(false, false);
        await Task.Delay(350);
        var controlRow = sheet.HotkeyRowLayoutForChecks(0);
        Check("纯触控控制行统一 48dp 最小行高",
            controlRow is { } row0 && Math.Abs(row0.Height - minRow) <= 1 && row0.MinHeight == minRow,
            LayoutText(controlRow, $"{minRow}px"));
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
        Check("外设绑定行随内容增高且完整容纳绑定文本",
            boundRow is { } bound && bound.Height > minRow
            && !bound.PrimaryChipEllipsized && !bound.SecondaryChipEllipsized,
            LayoutText(boundRow, $">{minRow}px"));
        AndroidInputDevices.SetChecksOverride(null, null);
        await Task.Delay(250);

        // 文件格式行：副标题（扩展名）完整显示，行高随多行内容增高。
        sheet.ShowPageForChecks("files");
        await Task.Delay(300);
        var formatRow = sheet.FormatRowLayoutForChecks(1);
        Check("文件格式行副标题完整且行高按内容",
            formatRow is { } format && format.Height >= minRow && format.SubtitleVisible
            && !format.TitleEllipsized && !format.SubtitleEllipsized,
            LayoutText(formatRow, "subtitle visible"));

        // EXIF 显示表：单行 48dp、勾选对齐，且不再有说明脚注。
        sheet.ShowPageForChecks("exif");
        await Task.Delay(300);
        var exifRow = sheet.ExifRowLayoutForChecks(0);
        Check("EXIF 单行列表统一 48dp 最小行高",
            exifRow is { } exif && Math.Abs(exif.Height - minRow) <= 1 && exif.MinHeight == minRow,
            LayoutText(exifRow, $"{minRow}px"));
        Check("EXIF 行勾选字形左缘对齐 16dp",
            exifRow is { } exifAlign && Math.Abs(exifAlign.CheckGlyphLeft - cardPadding) <= 1,
            LayoutText(exifRow, $"{cardPadding}px"));
        Check("EXIF 显示表无脚注", !HasText(sheet, "拖动手柄排序"), HasText(sheet, "拖动手柄排序") ? "仍有脚注" : "无脚注");

        // 缩放预设行：48dp，不再有百分比说明脚注。
        sheet.ShowPageForChecks("preview");
        await Task.Delay(300);
        int presetHeight = sheet.ScalePresetRowHeightForChecks(0);
        Check("缩放预设行统一 48dp 最小行高", Math.Abs(presetHeight - minRow) <= 1,
            $"{presetHeight}px expected={minRow}px");
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

        bool scrollable = scroll.ChildCount > 0 && scroll.GetChildAt(0)!.Height > scroll.Height;
        scroll.ScrollTo(0, 0);
        await Task.Delay(150);
        var location = new int[2];
        scroll.GetLocationInWindow(location);
        // 落在卡片左侧的空白内边距上，避免子控件吃掉手势；事件从窗口根派发，走真实拦截链。
        float scrollX = location[0] + AndroidUiTheme.Dp(activity, 4);
        float scrollCenterY = location[1] + scroll.Height / 2f;
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
        scroll.Fling(0);
        scroll.ScrollTo(0, 0);
        await Task.Delay(200);
        int topAtRest = container.Top;
        int stateAtRest = behavior.State;
        DispatchDrag(decor, scrollX, location[1] + scroll.Height * .2f, 0, scroll.Height * .7f);
        await Task.Delay(500);
        Check("内容已到顶下滑不收起 sheet",
            scroll.ScrollY == 0 && behavior.State == stateAtRest && Math.Abs(container.Top - topAtRest) <= 1
            && AndroidNativeSettingsPresenter.IsOpen,
            $"scrollY={scroll.ScrollY} state={behavior.State}/{stateAtRest}"
                + $" top={container.Top}/{topAtRest} open={AndroidNativeSettingsPresenter.IsOpen}");

        var header = sheet.HeaderForChecks;
        var headerLocation = new int[2];
        header.GetLocationInWindow(headerLocation);
        DispatchDrag(decor, headerLocation[0] + header.Width / 2f,
            headerLocation[1] + header.Height / 2f, 0, container.Height * .8f);
        await Task.Delay(1200);
        Check("标题栏拖下关闭 sheet", !AndroidNativeSettingsPresenter.IsOpen,
            AndroidNativeSettingsPresenter.IsOpen ? "仍打开" : "已关闭");
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
