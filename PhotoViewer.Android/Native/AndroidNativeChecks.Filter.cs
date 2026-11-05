using System;
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
/// <c>--es pv-check filter</c>（验收专用）：筛选条三个自绘下拉的存活与锚点语义。
/// 全部从真实 UI 入口进入——点按直接派发到筛选条控件、走它自己的命中链，
/// 选项经弹出窗里真实行视图的点击回调写回共享筛选 VM；覆盖跨周期存活、重复布局、窄屏双 Arrange、
/// 滚动偏移保留、真实锚点失效（滚动/隐藏/旋转）与行选择。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>筛选下拉类型（与产品命中顺序一致）。</summary>
    private enum FilterKind { Sort, Order, Rating }

    /// <summary>筛选下拉验收入口：竖屏三下拉存活 → 设置/行选择/隐藏 → 横屏窄屏双 Arrange 与滚动。</summary>
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
        Check("filter 筛选条可见", filter.Visibility == ViewStates.Visible && filter.SortFrame.Width > 0,
            $"visible={filter.Visibility} sort={filter.SortFrame} row={main.IsRowLayout}");

        foreach (var kind in new[] { FilterKind.Sort, FilterKind.Order, FilterKind.Rating })
            await RunStepAsync($"filter {kind} 下拉", () => CheckDropdownLifetimeAsync(kind));

        await RunStepAsync("filter 无关设置", CheckUnrelatedSettingAsync);
        await RunStepAsync("filter 星级设置", CheckSettingImpactAsync);
        await RunStepAsync("filter 行选择", CheckRowSelectionAsync);
        await RunStepAsync("filter 锚点隐藏", CheckHiddenClosesAsync);
        await RunStepAsync("filter 旋转与窄屏", CheckAnchorAndNarrowAsync);
    }

    /// <summary>单个下拉：真实点按打开 → 跨三个 2s 周期同一实例仍开 → 重复布局/同几何 Arrange 仍开 → 返回键收起。</summary>
    /// <param name="kind">下拉类型。</param>
    private static async Task CheckDropdownLifetimeAsync(FilterKind kind)
    {
        var root = _root!;
        var filter = root.FilterBar;
        root.DismissFilterDropdown();
        await Task.Delay(100);

        bool opened = OpenDropdown(root, kind);
        Check($"filter {kind} 实际点按打开", opened && filter.IsDropdownOpen && filter.DropdownKindForChecks == kind.ToString(),
            $"open={filter.IsDropdownOpen} kind={filter.DropdownKindForChecks} anchor={filter.DropdownAnchorForChecks}");
        if (!opened) return;

        var instance = filter.DropdownInstanceForChecks;
        var started = DateTime.UtcNow;
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            await Task.Delay(2000);
            double elapsed = (DateTime.UtcNow - started).TotalSeconds;
            Check($"filter {kind} {cycle * 2}s 同一实例仍开",
                ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen,
                $"elapsed={elapsed:0.0}s same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen}");
        }

        for (int pass = 1; pass <= 3; pass++)
        {
            root.RequestLayout();
            await Task.Delay(160);
        }
        Check($"filter {kind} 重复 RequestLayout 仍开",
            ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen,
            $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen}");

        // 与 OnLayout 完全相同的参数再 Arrange 一次（幂等几何），不能因调用次数收起。
        var (row, contentWidth, leadingInset) = CurrentFilterGeometry(root);
        int anchorX = filter.DropdownAnchorForChecks.X;
        filter.Arrange(row, contentWidth, leadingInset);
        filter.Arrange(row, contentWidth, leadingInset);
        filter.CommitLayout();
        Check($"filter {kind} 重复相同 Arrange 仍开",
            ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen
            && filter.DropdownAnchorForChecks.X == anchorX,
            $"same={ReferenceEquals(filter.DropdownInstanceForChecks, instance)} open={filter.IsDropdownOpen} "
            + $"anchor={anchorX}→{filter.DropdownAnchorForChecks.X}");

        bool dismissed = root.DismissFilterDropdown();
        Check($"filter {kind} 返回键收起", dismissed && !filter.IsDropdownOpen,
            $"dismissed={dismissed} open={filter.IsDropdownOpen}");
        await Task.Delay(120);
    }

    /// <summary>与呈现无关的设置变更不得整条硬刷并把已打开下拉关掉。</summary>
    private static async Task CheckUnrelatedSettingAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        if (!OpenDropdown(root, FilterKind.Order))
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

    /// <summary>
    /// 有呈现影响的设置（星级显隐）即时到达控制栏：评分分组消失/恢复，且不影响已打开的下拉。
    /// </summary>
    private static async Task CheckSettingImpactAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        bool original = main.Settings.ShowRating;
        if (!OpenDropdown(root, FilterKind.Sort))
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
            Check("filter 星级设置恢复评分组", root.ControlBar.HasRatingGroup == original,
                $"ratingGroup={root.ControlBar.HasRatingGroup} expected={original}");
        }
    }

    /// <summary>真实行选择：点排序下拉里的非当前项，VM 改变且下拉关闭。</summary>
    private static async Task CheckRowSelectionAsync()
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
        if (targetLabel == null || !OpenDropdown(root, FilterKind.Sort))
        {
            Check("filter 行选择改 VM 并关闭", false, $"target={targetLabel ?? "无"} open={filter.IsDropdownOpen}");
            return;
        }

        bool clicked = TryClickDropdownRow(root, targetLabel);
        await Task.Delay(400);
        Check("filter 行选择改 VM 并关闭",
            clicked && bar.SortMode == targetMode && !filter.IsDropdownOpen,
            $"clicked={clicked} label={targetLabel} sort={bar.SortMode} 期望 {targetMode} open={filter.IsDropdownOpen}");
        // 还原为原值，后续步骤不受筛选变化影响。
        bar.SortMode = current;
        await Task.Delay(200);
    }

    /// <summary>筛选条被隐藏后锚点真实失效：下拉必须收起。</summary>
    private static async Task CheckHiddenClosesAsync()
    {
        var root = _root!;
        var main = _main!;
        var filter = root.FilterBar;
        if (!OpenDropdown(root, FilterKind.Rating))
        {
            Check("filter 锚点隐藏关闭", false, "下拉未打开");
            return;
        }
        var instance = filter.DropdownInstanceForChecks;
        bool originalVisible = main.IsThumbnailViewVisible;
        try
        {
            main.IsThumbnailViewVisible = false;
            await WaitAsync(() => !filter.IsDropdownOpen, "隐藏筛选条后收起下拉", 3000);
            Check("filter 锚点隐藏关闭",
                !filter.IsDropdownOpen && !ReferenceEquals(filter.DropdownInstanceForChecks, instance),
                $"open={filter.IsDropdownOpen} bar={filter.Visibility} visible={originalVisible}");
        }
        finally
        {
            main.IsThumbnailViewVisible = originalVisible;
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// 旋转：已打开下拉在方向变化后必须收起；随后在竖屏行布局上复核窄屏双 Arrange 与真实滚动
    /// （视口/文档宽度与是否可滚只按实测，窄屏由外部 <c>wm density</c> 造成，不伪造几何）。
    /// </summary>
    private static async Task CheckAnchorAndNarrowAsync()
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
            if (!OpenDropdown(root, FilterKind.Sort))
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

            // 一次 OnLayout 的两次 Arrange（视口宽 → 文档宽）：中间态与最终态都不能关掉已打开的下拉。
            if (!OpenDropdown(root, FilterKind.Rating))
            {
                Check("filter 双 Arrange 不收起", false, "下拉未打开");
                return;
            }
            var instance = filter.DropdownInstanceForChecks;
            filter.Arrange(row, contentWidth, leadingInset);
            filter.CommitLayout();
            bool openAfterViewport = ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen;
            filter.Arrange(row, documentWidth, leadingInset);
            filter.CommitLayout();
            Check("filter 双 Arrange 不收起",
                openAfterViewport && ReferenceEquals(filter.DropdownInstanceForChecks, instance) && filter.IsDropdownOpen,
                $"afterViewport={openAfterViewport} afterDocument={filter.IsDropdownOpen} "
                + $"anchor={filter.DropdownAnchorForChecks}");
            root.DismissFilterDropdown();
            await Task.Delay(120);

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
            if (!OpenDropdown(root, FilterKind.Order))
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
        return (row, contentWidth, MainScreenLayout.FilterMargin);
    }

    /// <summary>经控件自身命中链点开下拉（真实 UI 入口，不做反射）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="kind">下拉类型。</param>
    /// <returns>打开返回 true。</returns>
    private static bool OpenDropdown(AndroidNativeRootView root, FilterKind kind)
    {
        var bar = root.FilterBar;
        var frame = kind switch
        {
            FilterKind.Sort => bar.SortFrame,
            FilterKind.Order => bar.OrderFrame,
            _ => bar.RatingFrame,
        };
        double density = AndroidUiTheme.Density(root.Context!);
        float x = (float)((frame.X + frame.Width / 2) * density);
        float y = (float)((frame.Y + frame.Height / 2) * density);
        long now = SystemClock.UptimeMillis();
        using (var down = TouchEvent(MotionEventActions.Down, x, y, now))
            bar.DispatchTouchEvent(down);
        using (var up = TouchEvent(MotionEventActions.Up, x, y, now + 40))
            bar.DispatchTouchEvent(up);
        return bar.IsDropdownOpen;
    }

    /// <summary>点弹出窗里按文本匹配的真实行视图（回调就是产品写回筛选 VM 的那一个）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="label">行文本。</param>
    /// <returns>点到了返回 true。</returns>
    private static bool TryClickDropdownRow(AndroidNativeRootView root, string label)
    {
        var content = root.FilterBar.DropdownContentViewForChecks;
        var row = content == null ? null : FindClickableRow(content, label);
        if (row == null) return false;
        row.PerformClick();
        return true;
    }

    private static View? FindClickableRow(View view, string label)
    {
        if (view is ViewGroup group)
        {
            for (int index = 0; index < group.ChildCount; index++)
            {
                if (group.GetChildAt(index) is not { } child) continue;
                if (child.Clickable && child.Enabled && ContainsLabel(child, label)) return child;
                if (FindClickableRow(child, label) is { } nested) return nested;
            }
        }
        return null;
    }

    private static bool ContainsLabel(View view, string label)
    {
        if (view is TextView text && string.Equals(text.Text, label, StringComparison.Ordinal)) return true;
        if (view is ViewGroup group)
        {
            for (int index = 0; index < group.ChildCount; index++)
                if (group.GetChildAt(index) is { } child && ContainsLabel(child, label)) return true;
        }
        return false;
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
