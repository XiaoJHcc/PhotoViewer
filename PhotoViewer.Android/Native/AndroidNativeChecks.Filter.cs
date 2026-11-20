using System;
using System.Linq;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
using Android.Widget;
using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check filter</c>（验收专用）：筛选入口的存活与锚点语义。
/// 入口全部是真实 Material 按钮——点按经控件自身的派发链落到按钮点击，选项经弹出窗里真实行视图的
/// 回调写回共享筛选 VM；覆盖跨内存探测周期存活、重复布局、无关设置、星级设置、行选择、锚点隐藏、
/// 旋转与窄屏滚动。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>筛选验收入口：真实入口可见 → 分组菜单存活/重排/选择 → 隐藏/旋转/窄屏滚动。</summary>
    private static async Task RunFilterBarChecksAsync()
    {
        var root = _root!;
        var main = _main!;
        var activity = _activity!;
        await WaitForScreenAsync();
        string folder = EnsureLayoutFolder(activity);
        await OpenFolderAndWaitAsync(folder);
        await Task.Delay(400);

        var filter = root.FilterBar;
        Check("filter 筛选入口可见", filter.Visibility == ViewStates.Visible && filter.EntryFrameForChecks.Width > 0,
            $"visible={filter.Visibility} entry={filter.EntryFrameForChecks} control={filter.ControlFrame} "
            + $"row={main.IsRowLayout} phone={filter.IsPhone}");

        await RunStepAsync("filter 默认排序", CheckFilterDefaultsAsync);
        await RunStepAsync("filter 分组菜单", CheckGroupedMenuAsync);
        await RunStepAsync("filter 星级多选", CheckGroupedRatingMultiSelectAsync);
        await RunStepAsync("filter 无关设置", CheckGroupedUnrelatedSettingAsync);
        await RunStepAsync("filter 星级设置", CheckGroupedSettingImpactAsync);
        await RunStepAsync("filter 行选择", CheckGroupedRowSelectionAsync);
        await RunStepAsync("filter 锚点隐藏", CheckGroupedHiddenClosesAsync);
        await RunStepAsync("filter 旋转与窄屏", CheckGroupedRotationAndNarrowAsync);
    }

    /// <summary>FILTER-02/FILTER-09：默认拍摄时间升序，排序与方向文案固定。</summary>
    private static Task CheckFilterDefaultsAsync()
    {
        var bar = _main!.FileVM.FilterBar;
        var name = bar.SortModes.Find(option => (SortMode)option.Value == SortMode.Name);
        var ascending = bar.SortOrders.Find(option => (SortOrder)option.Value == SortOrder.Ascending);
        var descending = bar.SortOrders.Find(option => (SortOrder)option.Value == SortOrder.Descending);
        Check("filter 默认排序为拍摄时间升序",
            bar.SortMode == SortMode.Date && bar.SortOrder == SortOrder.Ascending,
            $"sort={bar.SortMode}/{bar.SortOrder}");
        Check("filter 排序与方向文案",
            name?.DisplayName == "文件名" && ascending?.DisplayName == "升序" && descending?.DisplayName == "降序",
            $"name={name?.DisplayName ?? "无"} asc={ascending?.DisplayName ?? "无"} desc={descending?.DisplayName ?? "无"}");
        Check("filter 默认星级为全部",
            bar.IsRatingFilterSelected(PhotoListProjection.AllRatings) && !bar.IsRatingFilterActive,
            $"all={bar.IsRatingFilterSelected(PhotoListProjection.AllRatings)} active={bar.IsRatingFilterActive}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// FILTER-03/FILTER-08：真实行连续多选、外部状态就地更新、全部重置与取消最后条件回到全部；
    /// 全程同一个 PopupWindow，不重开也不重建行。同时核对每行语义图标与分组。
    /// </summary>
    private static async Task CheckGroupedRatingMultiSelectAsync()
    {
        var root = _root!;
        var main = _main!;
        var bar = main.FileVM.FilterBar;
        var filter = root.FilterBar;
        root.DismissFilterDropdown();
        bar.ToggleRatingFilter(PhotoListProjection.AllRatings);
        await Task.Delay(150);
        if (!await OpenGroupedMenuAsync(root))
        {
            Check("filter 星级多选", false, "下拉未打开");
            return;
        }
        var instance = filter.DropdownInstanceForChecks;
        var rows = filter.DropdownRowsForChecks;
        int sortRows = rows.Count(row => row.Group == "Sort");
        int orderRows = rows.Count(row => row.Group == "Order");
        int ratingRows = rows.Count(row => row.Group == "Rating");
        Check("filter 菜单行语义图标与分组",
            rows.Count == 14 && sortRows == 3 && orderRows == 2 && ratingRows == 9
            && rows.All(row => row.HasIcon && row.IconDescription.Length > 0),
            $"rows={rows.Count} sort={sortRows} order={orderRows} rating={ratingRows} "
            + $"icons={rows.Count(row => row.HasIcon)} glyphs={string.Join("|", rows.Select(row => row.IconDescription))}");
        var all = FindFilterRow(filter, "全部");
        Check("filter 菜单初始勾选全部",
            all.HasValue && all.Value.Selected && all.Value.Marked,
            $"all={Describe(all)} open={filter.IsDropdownOpen}");

        bool clicked = ClickMenuText(filter.DropdownContentViewForChecks, "三星");
        await Task.Delay(350);
        var three = FindFilterRow(filter, "三星");
        all = FindFilterRow(filter, "全部");
        Check("filter 星级多选勾选三星",
            clicked && bar.IsRatingFilterSelected("Eq3")
            && three.HasValue && three.Value.Selected && three.Value.Marked
            && all.HasValue && !all.Value.Selected && !all.Value.Marked
            && filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"clicked={clicked} eq3={bar.IsRatingFilterSelected("Eq3")} row={Describe(three)} "
            + $"all={Describe(all)} same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)}");

        clicked = ClickMenuText(filter.DropdownContentViewForChecks, "四星");
        await Task.Delay(350);
        var four = FindFilterRow(filter, "四星");
        Check("filter 星级连续多选三星+四星",
            clicked && bar.IsRatingFilterSelected("Eq3") && bar.IsRatingFilterSelected("Eq4")
            && four.HasValue && four.Value.Selected && four.Value.Marked
            && filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"clicked={clicked} eq3={bar.IsRatingFilterSelected("Eq3")} eq4={bar.IsRatingFilterSelected("Eq4")} "
            + $"row={Describe(four)} open={filter.IsDropdownOpen}");

        // 外部状态变化（非行点选）也必须就地更新勾选，不重开浮窗。
        bar.ToggleRatingFilter("Eq5");
        await Task.Delay(350);
        var five = FindFilterRow(filter, "五星");
        Check("filter 星级外部改动就地更新",
            bar.IsRatingFilterSelected("Eq5") && five.HasValue && five.Value.Selected && five.Value.Marked
            && filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"eq5={bar.IsRatingFilterSelected("Eq5")} row={Describe(five)} open={filter.IsDropdownOpen} "
            + $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)}");

        clicked = ClickMenuText(filter.DropdownContentViewForChecks, "全部");
        await Task.Delay(350);
        all = FindFilterRow(filter, "全部");
        three = FindFilterRow(filter, "三星");
        Check("filter 星级全部重置",
            clicked && !bar.IsRatingFilterActive
            && all.HasValue && all.Value.Selected && all.Value.Marked
            && three.HasValue && !three.Value.Selected && !three.Value.Marked
            && filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"clicked={clicked} active={bar.IsRatingFilterActive} all={Describe(all)} three={Describe(three)} "
            + $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)}");

        bool selectedFive = ClickMenuText(filter.DropdownContentViewForChecks, "五星");
        await Task.Delay(300);
        bool clearedFive = ClickMenuText(filter.DropdownContentViewForChecks, "五星");
        await Task.Delay(350);
        all = FindFilterRow(filter, "全部");
        Check("filter 取消最后条件回到全部",
            selectedFive && clearedFive && !bar.IsRatingFilterActive
            && all.HasValue && all.Value.Selected && all.Value.Marked
            && filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"select={selectedFive} clear={clearedFive} active={bar.IsRatingFilterActive} all={Describe(all)} "
            + $"open={filter.IsDropdownOpen}");

        bar.ToggleRatingFilter(PhotoListProjection.AllRatings);
        root.DismissFilterDropdown();
        await Task.Delay(200);
    }

    private static AndroidFilterBar.FilterMenuRowForChecks? FindFilterRow(AndroidFilterBar filter, string label)
    {
        foreach (var row in filter.DropdownRowsForChecks)
            if (row.Label == label) return row;
        return null;
    }

    private static string Describe(AndroidFilterBar.FilterMenuRowForChecks? row)
        => row.HasValue
            ? $"{row.Value.Label}:selected={row.Value.Selected},marked={row.Value.Marked},icon={row.Value.IconDescription}"
            : "缺失";

    /// <summary>真实入口点按打开 → 同一实例跨周期存活 → 重排/同几何 Arrange 不收起 → 行选择保持展开 → 返回键收起。</summary>
    private static async Task CheckGroupedMenuAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        root.DismissFilterDropdown();
        await Task.Delay(120);
        bool opened = await OpenGroupedMenuAsync(root);
        Check("filter 分组菜单实际点按打开", opened && filter.IsDropdownOpen && filter.DropdownKindForChecks == "Grouped",
            $"open={filter.IsDropdownOpen} kind={filter.DropdownKindForChecks} anchor={filter.DropdownAnchorForChecks}");
        if (!opened) return;
        var instance = filter.DropdownInstanceForChecks;
        var anchor = filter.DropdownAnchorForChecks;
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            await Task.Delay(2000);
            Check($"filter 分组菜单 {cycle * 2}s 同一实例仍开",
                ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen,
                $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen}");
        }
        for (int pass = 0; pass < 3; pass++)
        {
            root.RequestLayout();
            await Task.Delay(220);
            filter.CommitLayout();
        }
        Check("filter 分组菜单重排不挪位不改宽",
            ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen
            && filter.DropdownAnchorForChecks == anchor,
            $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen} "
            + $"anchor={anchor}→{filter.DropdownAnchorForChecks}");

        // 与 OnLayout 完全相同的参数再 Arrange 一次（幂等几何），不能因调用次数收起。
        var (row, contentWidth, leadingInset) = CurrentFilterGeometry(root);
        filter.Arrange(row, contentWidth, leadingInset);
        filter.Arrange(row, contentWidth, leadingInset);
        filter.CommitLayout();
        Check("filter 分组菜单重复相同 Arrange 仍开",
            ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen
            && filter.DropdownAnchorForChecks == anchor,
            $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen} "
            + $"anchor={anchor}→{filter.DropdownAnchorForChecks}");

        var filterVm = main.FileVM.FilterBar;
        var previousSort = filterVm.SortMode;
        bool clicked = ClickMenuText(filter.DropdownContentViewForChecks, "拍摄时间");
        await Task.Delay(250);
        Check("filter 分组菜单行选择保持展开",
            clicked && filterVm.SortMode == SortMode.Date && filter.IsDropdownOpen
            && ReferenceEquals(filter.DropdownInstanceForChecks, instance),
            $"clicked={clicked} sort={filterVm.SortMode} open={filter.IsDropdownOpen}");
        filterVm.SortMode = previousSort;
        await Task.Delay(150);

        bool dismissed = root.DismissFilterDropdown();
        Check("filter 分组菜单返回键收起", dismissed && !filter.IsDropdownOpen,
            $"dismissed={dismissed} open={filter.IsDropdownOpen}");
    }

    /// <summary>经真实入口的派发链点开分组菜单。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <returns>打开返回 true。</returns>
    private static async Task<bool> OpenGroupedMenuAsync(AndroidNativeRootView root)
    {
        var filter = root.FilterBar;
        var frame = filter.EntryFrameForChecks;
        if (frame.Width <= 0) return false;
        double density = AndroidUiTheme.Density(root.Context!);
        float x = (float)((frame.X + frame.Width / 2) * density);
        float y = (float)((frame.Y + frame.Height / 2) * density);
        long now = SystemClock.UptimeMillis();
        using (var down = TouchEvent(MotionEventActions.Down, x, y, now))
            filter.DispatchTouchEvent(down);
        using (var up = TouchEvent(MotionEventActions.Up, x, y, now + 40))
            filter.DispatchTouchEvent(up);
        // 视图把抬手后的点击排到 UI 线程执行：等它落地再读入口状态。
        await Task.Delay(200);
        return filter.IsDropdownOpen;
    }

    private static bool ClickMenuText(View? view, string text)
    {
        if (view == null) return false;
        if (view is TextView label && string.Equals(label.Text, text, StringComparison.Ordinal))
        {
            if (label.Parent is View row) row.PerformClick();
            return true;
        }
        if (view is ViewGroup group)
            for (int index = 0; index < group.ChildCount; index++)
                if (ClickMenuText(group.GetChildAt(index), text)) return true;
        return false;
    }

    /// <summary>与呈现无关的设置变更不得整条硬刷并把已打开下拉关掉。</summary>
    private static async Task CheckGroupedUnrelatedSettingAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        if (!await OpenGroupedMenuAsync(root))
        {
            Check("filter 无关设置不收起", false, "下拉未打开");
            return;
        }
        var instance = filter.DropdownInstanceForChecks;
        bool original = main.Settings.ShowZoomIndicator;
        main.Settings.ShowZoomIndicator = !original;
        await Task.Delay(1300);
        Check("filter 无关设置不收起",
            ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen,
            $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen} "
            + $"setting={original}→{!original}");
        main.Settings.ShowZoomIndicator = original;
        await Task.Delay(400);
        root.DismissFilterDropdown();
    }

    /// <summary>有呈现影响的设置（星级显隐）即时到达控制栏，且不影响已打开的下拉。</summary>
    private static async Task CheckGroupedSettingImpactAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        bool original = main.Settings.ShowRating;
        if (!await OpenGroupedMenuAsync(root))
        {
            Check("filter 星级设置即时生效且不收起下拉", false, "下拉未打开");
            return;
        }
        var instance = filter.DropdownInstanceForChecks;
        try
        {
            main.Settings.ShowRating = false;
            await Task.Delay(700);
            bool hidden = !root.ControlBar.HasRatingGroup;
            bool keptOpen = ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen;
            Check("filter 星级设置即时生效且不收起下拉", hidden && keptOpen,
                $"ratingGroup={root.ControlBar.HasRatingGroup} same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} "
                + $"open={filter.IsDropdownOpen}");
        }
        finally
        {
            main.Settings.ShowRating = original;
            root.DismissFilterDropdown();
            await Task.Delay(700);
            bool restored = root.Form == ScreenForm.Wide
                ? root.ControlBar.HasRatingGroup == original
                : (root.PhoneRating.Visibility == ViewStates.Visible) == original;
            Check("filter 星级设置恢复评分组", restored,
                $"ratingGroup={root.ControlBar.HasRatingGroup} phoneRating={root.PhoneRating.Visibility} "
                + $"form={root.Form} expected={original}");
        }
    }

    /// <summary>真实行选择：分组菜单里点非当前排序项，VM 改变且菜单保持展开。</summary>
    private static async Task CheckGroupedRowSelectionAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        var bar = main.FileVM.FilterBar;
        var current = bar.SortMode;
        string? targetLabel = null;
        SortMode targetMode = current;
        foreach (var option in bar.SortModes)
        {
            var mode = (SortMode)option.Value;
            if (mode == current) continue;
            targetLabel = option.DisplayName;
            targetMode = mode;
            break;
        }
        if (targetLabel == null || !await OpenGroupedMenuAsync(root))
        {
            Check("filter 行选择改 VM 且菜单保持", false, $"target={targetLabel ?? "无"} open={filter.IsDropdownOpen}");
            return;
        }
        bool clicked = ClickMenuText(filter.DropdownContentViewForChecks, targetLabel);
        await Task.Delay(400);
        Check("filter 行选择改 VM 且菜单保持",
            clicked && bar.SortMode == targetMode && filter.IsDropdownOpen,
            $"clicked={clicked} label={targetLabel} sort={bar.SortMode} 期望 {targetMode} open={filter.IsDropdownOpen}");
        bar.SortMode = current;
        root.DismissFilterDropdown();
        await Task.Delay(200);
    }

    /// <summary>筛选条被隐藏后锚点真实失效：下拉必须收起。</summary>
    private static async Task CheckGroupedHiddenClosesAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        if (!await OpenGroupedMenuAsync(root))
        {
            Check("filter 锚点隐藏关闭", false, "下拉未打开");
            return;
        }
        var instance = filter.DropdownInstanceForChecks;
        bool originalVisible = main.IsThumbnailViewVisible;
        bool wide = root.Form == ScreenForm.Wide;
        try
        {
            main.IsThumbnailViewVisible = false;
            if (wide)
            {
                await WaitAsync(() => !filter.IsDropdownOpen, "隐藏筛选条后收起下拉", 3000);
                Check("filter 锚点隐藏关闭",
                    !filter.IsDropdownOpen && !ReferenceEquals(filter.DropdownInstanceForChecks, instance),
                    $"open={filter.IsDropdownOpen} bar={filter.Visibility} visible={originalVisible}");
            }
            else
            {
                // 手机形态的筛选入口属于固定顶行，不随胶片栏隐藏；入口仍在即可继续使用。
                await Task.Delay(600);
                Check("filter 锚点隐藏关闭（手机入口常驻）",
                    filter.IsDropdownOpen && ReferenceEquals(filter.DropdownInstanceForChecks, instance)
                    && filter.Visibility == ViewStates.Visible,
                    $"open={filter.IsDropdownOpen} bar={filter.Visibility} film={main.IsThumbnailViewVisible}");
            }
        }
        finally
        {
            main.IsThumbnailViewVisible = originalVisible;
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// 旋转：已打开下拉在方向变化后必须收起；随后在行布局上复核窄屏文档宽度与真实滚动
    /// （视口/文档宽度与是否可滚只按实测，窄屏由外部 <c>wm density</c> 造成，不伪造几何）。
    /// </summary>
    private static async Task CheckGroupedRotationAndNarrowAsync()
    {
        var root = _root!;
        var main = _main!;
        var activity = _activity!;
        var filter = root.FilterBar;
        var previousOrientation = activity.RequestedOrientation;
        // 残留的固定布局模式会让行/列与方向脱钩：这一节固定用自动档，结束时还原。
        var previousLayoutMode = main.Settings.LayoutMode;
        bool previousVisible = main.IsThumbnailViewVisible;

        try
        {
            main.Settings.LayoutMode = LayoutMode.Auto;
            await Task.Delay(300);
            if (!await OpenGroupedMenuAsync(root))
            {
                Check("filter 旋转关闭", false, "下拉未打开");
                return;
            }
            var rotateStarted = DateTime.UtcNow;
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Landscape;
            await WaitAsync(() => root.Width > root.Height, "横屏布局", 20000);
            await Task.Delay(600);
            Check("filter 旋转关闭",
                !filter.IsDropdownOpen && root.Width > root.Height,
                $"open={filter.IsDropdownOpen} px={root.Width}x{root.Height} row={main.IsRowLayout} "
                + $"rotate={(DateTime.UtcNow - rotateStarted).TotalSeconds:0.0}s");

            activity.RequestedOrientation = previousOrientation;
            await WaitAsync(() => root.Height > root.Width, "回竖屏布局", 20000);
            await Task.Delay(600);

            var (row, contentWidth, leadingInset) = CurrentFilterGeometry(root);
            double documentWidth = Math.Max(contentWidth, filter.RequiredWidthDp);
            bool narrow = row && documentWidth > contentWidth + .5 && root.FilterScrollableForChecks;
            Check("filter 窄屏几何", true,
                $"density={AndroidUiTheme.Density(root.Context!):0.###} row={row} viewport={contentWidth:0.#} "
                + $"document={documentWidth:0.#} scrollable={root.FilterScrollableForChecks} narrow={narrow}");

            if (!narrow)
            {
                Check("filter 滚动（非窄屏跳过）", true,
                    $"viewport={contentWidth:0.#}dp document={documentWidth:0.#}dp；窄屏档用 wm density 525 复跑");
                return;
            }

            // 真实拖动手势滚动筛选条。
            int offset = await DragFilterHostAsync(root, +240);
            Check("filter 滚动可达", offset > 0, $"offset={offset} scrollable={root.FilterScrollableForChecks}");
            if (offset <= 0) return;

            // 下拉打开后重复重建：滚动偏移保持不变，同一实例仍开。
            if (!await OpenGroupedMenuAsync(root))
            {
                Check("filter 滚动偏移跨重建保留", false, "下拉未打开");
                return;
            }
            var scrollInstance = filter.DropdownInstanceForChecks;
            for (int pass = 1; pass <= 3; pass++)
            {
                root.RequestLayout();
                await Task.Delay(180);
            }
            Check("filter 滚动偏移跨重建保留",
                root.FilterScrollOffsetForChecks == offset
                && ReferenceEquals(filter.DropdownInstanceForChecks, scrollInstance) && filter.IsDropdownOpen,
                $"offset={offset}→{root.FilterScrollOffsetForChecks} "
                + $"same={ReferenceEquals(filter.DropdownInstanceForChecks, scrollInstance)} open={filter.IsDropdownOpen}");

            // 再次真实滚动：锚点移到旧坐标之外，下拉收起。
            int moved = await DragFilterHostAsync(root, -160);
            await Task.Delay(400);
            Check("filter 滚动收起失效锚点",
                moved != offset && !filter.IsDropdownOpen && root.FilterScrollOffsetForChecks == moved,
                $"offset={offset}→{moved} open={filter.IsDropdownOpen}");
        }
        finally
        {
            root.DismissFilterDropdown();
            main.IsThumbnailViewVisible = previousVisible;
            activity.RequestedOrientation = previousOrientation;
            await WaitAsync(() => root.Height > root.Width, "回竖屏布局", 20000);
            await Task.Delay(400);
            main.Settings.LayoutMode = previousLayoutMode;
            await Task.Delay(200);
        }
    }

    /// <summary>当前布局下与根视图 OnLayout 完全一致的筛选条 Arrange 参数。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <returns>行布局、内容宽（dp）、前导让位。</returns>
    private static (bool Row, double ContentWidth, double LeadingInset) CurrentFilterGeometry(AndroidNativeRootView root)
    {
        bool row = _main!.IsRowLayout;
        double contentWidth = row
            ? Math.Max(1, root.Panes.FileContent.Width)
            : MainScreenLayout.FileBarContentWidth;
        double leadingInset = (row ? 0 : root.SafeInsets.Top) + MainScreenLayout.FilterMargin;
        return (row, contentWidth, leadingInset);
    }

    /// <summary>在筛选条滚动宿主上拖一次（真实 Down/Move/Up 手势），返回稳定后的滚动偏移（px）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="dx">横向位移（正数表示手指向左、内容向右滚）。</param>
    /// <returns>滚动偏移（px）。</returns>
    private static async Task<int> DragFilterHostAsync(AndroidNativeRootView root, int dx)
    {
        var host = root.FilterScrollHostForChecks;
        if (host.Width < 1 || host.Height < 1) return root.FilterScrollOffsetForChecks;
        long now = SystemClock.UptimeMillis();
        float y = host.Height / 2f;
        float startX = host.Width * .8f;
        using (var down = TouchEvent(MotionEventActions.Down, startX, y, now))
            host.DispatchTouchEvent(down);
        for (int step = 1; step <= 6; step++)
        {
            float x = startX - dx * step / 6f;
            using var move = TouchEvent(MotionEventActions.Move, x, y, now + step * 16);
            host.DispatchTouchEvent(move);
        }
        using (var up = TouchEvent(MotionEventActions.Up, startX - dx, y, now + 130))
            host.DispatchTouchEvent(up);
        // 松手可能带惯性：等偏移稳定再读，避免把在途 fling 当成断言值。
        int last = root.FilterScrollOffsetForChecks;
        for (int settle = 0; settle < 12; settle++)
        {
            await Task.Delay(100);
            int next = root.FilterScrollOffsetForChecks;
            if (next == last && settle >= 2) break;
            last = next;
        }
        return last;
    }
}
