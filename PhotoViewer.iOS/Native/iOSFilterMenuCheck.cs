using System.Diagnostics;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（测试钩子，独立筛选菜单回归）：核对 [FILTER-08] 的菜单结构、稳定业务标识、
/// 写回路径与模型选择状态。菜单的可见勾选、滚动和辅助技术焦点属于真实系统交互验收，
/// 不用托管对象身份代替屏上结果。
/// 由 <c>--pv-native-form-check</c> 在筛选写回核对之后调用（见报告行）。
/// </summary>
internal static class iOSFilterMenuCheck
{
    /// <summary>等待宿主刷新勾选的超时。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    /// <summary>排序方式分组前缀（筛选段就地更新用排序组，避免星级筛选清空列表影响当前照片）。</summary>
    private const string SortPrefix = "sort:";
    /// <summary>排序方向分组前缀。</summary>
    private const string OrderPrefix = "order:";
    /// <summary>星级筛选分组前缀。</summary>
    private const string RatingPrefix = "rating:";

    /// <summary>核对筛选菜单结构与模型侧勾选更新，返回报告行。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="main">共享主 VM。</param>
    /// <returns>报告行；任一项不符即抛异常（套件转 FAIL）。</returns>
    internal static async Task<IReadOnlyList<string>> RunAsync(iOSNativeRootViewController root, MainViewModel main)
    {
        var lines = new List<string>();
        var filter = main.FileVM.FilterBar;
        var entrySort = filter.SortMode;
        try
        {
            await VerifyIdentifiersAsync(filter, lines);
            await VerifySortSelectionAsync(root, filter, lines);
            await VerifyStructureDetectionAsync(root, filter, lines);
            return lines;
        }
        finally
        {
            await UiDispatcher.InvokeAsync(() => filter.SortMode = entrySort);
            await WaitAsync(() => filter.SortMode == entrySort, "排序方式还原");
            lines.Add("PASS 筛选菜单回归收尾：排序方式回到进入本项检查时的值");
        }
    }

    /// <summary>核对标识：三个分组的每个选项都有业务键标识，且与处理器键一一对应（验收按标识找 action）。</summary>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyIdentifiersAsync(FilterBarViewModel filter, List<string> lines)
    {
        var built = await UiDispatcher.InvokeAsync(() => iOSFilterMenu.Build(filter, () => { }));
        var (identifiers, groups, allGroups) = await UiDispatcher.InvokeAsync(() =>
        {
            var menu = built.Menu;
            return (Identifiers: Actions(menu).Select(action => action.Identifier ?? "")
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                Groups: menu.Children.Length,
                AllGroups: menu.Children.All(child => child is UIMenu));
        });
        if (identifiers.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("筛选菜单有选项缺少标识");
        if (!identifiers.All(IsFilterIdentifier))
            throw new InvalidOperationException($"筛选菜单标识未带分组前缀：{string.Join(" / ", identifiers)}");
        var handlerKeys = built.Handlers.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (!identifiers.SequenceEqual(handlerKeys, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"筛选菜单标识与处理器键不一致：标识 {string.Join(",", identifiers)} / 处理器 {string.Join(",", handlerKeys)}");
        if (groups != 3 || !allGroups)
            throw new InvalidOperationException($"筛选菜单应为三个内联分组，实际 {groups} 项（全为分组={allGroups}）");
        lines.Add($"PASS 筛选菜单标识：{identifiers.Length} 个选项都带稳定业务键标识，与处理器键一一对应");
    }

    /// <summary>
    /// 核对选择写回后菜单中的业务状态与分组单选关系；不把托管对象身份当作系统菜单交互结果。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifySortSelectionAsync(iOSNativeRootViewController root, FilterBarViewModel filter, List<string> lines)
    {
        var target = filter.SortModes.Select(option => (SortMode)option.Value)
            .FirstOrDefault(mode => mode != filter.SortMode);
        if (target == filter.SortMode) throw new InvalidOperationException("排序方式只有一个选项，无法核对就地更新");
        string targetIdentifier = $"{SortPrefix}{target}";

        var filterButton = root.FilterBar.FilterButton;
        await UiDispatcher.InvokeAsync(() => filter.SortMode = target);
        await WaitAsync(() => ActionById(filterButton.Menu, targetIdentifier)?.State == UIMenuElementState.On,
            $"筛选段模型勾选「{target}」更新");

        var snapshot = await UiDispatcher.InvokeAsync(() =>
        {
            var menu = filterButton.Menu!;
            return (Actions: Actions(menu).ToArray(),
                Selected: ActionById(menu, targetIdentifier)?.State ?? UIMenuElementState.Off,
                OthersOff: Actions(menu)
                    .Where(action => action.Identifier?.StartsWith(SortPrefix, StringComparison.Ordinal) == true
                        && !string.Equals(action.Identifier, targetIdentifier, StringComparison.Ordinal))
                    .All(action => action.State == UIMenuElementState.Off));
        });
        if (snapshot.Selected != UIMenuElementState.On || !snapshot.OthersOff)
            throw new InvalidOperationException($"筛选段模型选择未更新：{targetIdentifier}={snapshot.Selected}，其余同组项仍勾选={!snapshot.OthersOff}");
        lines.Add($"PASS 筛选段模型状态：选中「{target}」后 {snapshot.Actions.Length} 个选项的标识与单选状态正确");

        foreach (var (button, label) in new[] { (root.PhoneFilterButton, "筛选圆按钮"), (root.PhoneFilterSegment, "合体筛选段") })
            await WaitAsync(() => ActionById(button.Menu, targetIdentifier)?.State == UIMenuElementState.On,
                $"{label}模型选择「{target}」更新");
        lines.Add("PASS 手机筛选入口模型状态：筛选选择已同步");
    }

    /// <summary>核对结构检测：选项集合变化要求重建一次，还原后恢复就地勾选。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="lines">报告行。</param>
    private static async Task VerifyStructureDetectionAsync(iOSNativeRootViewController root, FilterBarViewModel filter, List<string> lines)
    {
        var menu = await UiDispatcher.InvokeAsync(() => root.FilterBar.FilterButton.Menu
            ?? throw new InvalidOperationException("筛选段没有菜单"));
        var probe = new RatingFilterOption { DisplayName = "验收临时项", Key = "CheckTemporary" };
        bool needsRebuild;
        try
        {
            await UiDispatcher.InvokeAsync(() => filter.RatingFilters.Add(probe));
            needsRebuild = await UiDispatcher.InvokeAsync(() => !iOSFilterMenu.ApplySelection(menu, filter));
        }
        finally
        {
            await UiDispatcher.InvokeAsync(() => filter.RatingFilters.Remove(probe));
        }
        bool matches = await UiDispatcher.InvokeAsync(() => iOSFilterMenu.ApplySelection(menu, filter));
        if (!needsRebuild) throw new InvalidOperationException("选项集合变化时结构检测未要求重建（菜单会缺项）");
        if (!matches) throw new InvalidOperationException("选项集合还原后结构检测仍报不一致");
        lines.Add("PASS 结构检测：选项集合变化报需重建一次，还原后模型结构与勾选重新一致");
    }

    /// <summary>菜单各分组的 action（根 → 分组 → 选项）。</summary>
    /// <param name="menu">目标菜单；null 时为空。</param>
    /// <returns>按菜单顺序排列的 action。</returns>
    private static IEnumerable<UIAction> Actions(UIMenu? menu)
        => menu == null
            ? Enumerable.Empty<UIAction>()
            : menu.Children.OfType<UIMenu>().SelectMany(group => group.Children.OfType<UIAction>());

    /// <summary>按标识查找 action。</summary>
    /// <param name="menu">目标菜单；null 时没有命中。</param>
    /// <param name="identifier">业务键标识。</param>
    /// <returns>命中的 action；没有返回 null。</returns>
    private static UIAction? ActionById(UIMenu? menu, string identifier)
        => Actions(menu).FirstOrDefault(action => string.Equals(action.Identifier, identifier, StringComparison.Ordinal));

    /// <summary>标识是否带筛选菜单的分组前缀。</summary>
    /// <param name="identifier">业务键标识。</param>
    /// <returns>带 <c>sort:</c> / <c>order:</c> / <c>rating:</c> 前缀时为 true。</returns>
    private static bool IsFilterIdentifier(string identifier)
        => identifier.StartsWith(SortPrefix, StringComparison.Ordinal)
            || identifier.StartsWith(OrderPrefix, StringComparison.Ordinal)
            || identifier.StartsWith(RatingPrefix, StringComparison.Ordinal);

    /// <summary>把条件放到 UI 线程上轮询等待。</summary>
    /// <param name="condition">等待条件（UI 线程执行）。</param>
    /// <param name="label">超时信息。</param>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < Timeout)
        {
            if (await UiDispatcher.InvokeAsync(condition)) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }
}
