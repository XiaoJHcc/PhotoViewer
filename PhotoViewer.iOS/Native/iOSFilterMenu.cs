using Foundation;
using UIKit;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 筛选菜单：根菜单下三个内联单选分组（排序方式、排序方向、星级筛选），分组之间由系统画分隔线；
/// 选项保持菜单展开（<see cref="UIMenuElementAttributes.KeepsMenuPresented"/>），选中后写回共享筛选状态，
/// 失焦才关闭。手机形态的筛选圆按钮与宽形态的合体胶囊（<see cref="iOSFilterControl"/>）共用本菜单；
/// 数据与当前值一律现读共享 <see cref="FilterBarViewModel"/>，本类不缓存筛选条件。
/// <para>分组与选项都带稳定标识（业务键 <c>sort:</c> / <c>order:</c> / <c>rating:</c>），刷新时按标识改写
/// <see cref="UIAction.State"/>，并把 <c>UIContextMenuInteraction.UpdateVisibleMenu</c> 交来的那份菜单原样返回；
/// 只有选项集合真的变化（菜单里的选项标识对不上）才重建一次。</para>
/// <para>已知限制（原生屏上勾选刷新未解决）：改写 action 状态不会让已展开的菜单可见地重绘，
/// 换成新菜单又必然滚回顶部。因此这里只维护「标识、回调、勾选态」这一层模型结构与写回路径，
/// 不能用 action 对象的状态变化代表用户在屏上看到的勾选变化。</para>
/// </summary>
internal static class iOSFilterMenu
{
    /// <summary>根菜单标识：重建时保持菜单身份。</summary>
    private const string RootIdentifier = "filter";
    /// <summary>排序方式分组键，同时是 action 标识前缀。</summary>
    private const string SortGroup = "sort";
    /// <summary>排序方向分组键，同时是 action 标识前缀。</summary>
    private const string OrderGroup = "order";
    /// <summary>星级筛选分组键，同时是 action 标识前缀。</summary>
    private const string RatingGroup = "rating";

    /// <summary>
    /// 现读共享筛选状态刷新按钮菜单与菜单里的勾选态：选项集合未变时不重建菜单，只按标识就地改写
    /// <see cref="UIAction.State"/>，并把展开中的菜单原样交回系统；勾选已是最新（无关状态通知）时
    /// 连系统更新入口都不调用。
    /// <para>这只同步模型侧勾选：已展开菜单的屏上勾选刷新在原生菜单路径上尚未解决（同一份菜单不重绘、
    /// 新菜单会滚回顶部），调用方与验收不得据此判断屏上结果。</para>
    /// </summary>
    /// <param name="button">带菜单的按钮。</param>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="applied">某个选项写回之后的回调（宿主据此刷新勾选态）。</param>
    internal static void UpdateVisibleMenu(UIButton button, FilterBarViewModel filter, Action applied)
    {
        var states = States(filter);
        if (button.Menu is not { } menu || !ApplyStates(menu, states, out bool changed))
        {
            menu = Build(filter, applied).Menu;
            button.Menu = menu;
            RefreshPresented(button, current => ApplyStates(current, states, out _) ? current : menu);
            return;
        }
        if (!changed) return;
        RefreshPresented(button, current => ApplyStates(current, states, out _) ? current : menu);
    }

    /// <summary>就地套用共享筛选状态的勾选（验收缝）：结构一致时只改 action 状态，不新建菜单。</summary>
    /// <param name="menu">目标菜单。</param>
    /// <param name="filter">共享筛选状态。</param>
    /// <returns>菜单里的选项标识与当前选项集合一一对应时为 true。</returns>
    internal static bool ApplySelection(UIMenu menu, FilterBarViewModel filter)
        => ApplyStates(menu, States(filter), out _);

    /// <summary>一次菜单构建的产物：系统菜单对象与按业务键索引的同源回调（<see cref="UIAction"/> 不公开 handler）。</summary>
    /// <param name="menu">交给按钮的菜单。</param>
    /// <param name="handlers">业务键 → 回调；键为 <c>sort:值</c> / <c>order:值</c> / <c>rating:键</c>，与 action 标识一致。</param>
    internal sealed class FilterMenuResult(UIMenu menu, IReadOnlyDictionary<string, Action> handlers)
    {
        /// <summary>交给按钮的菜单。</summary>
        internal UIMenu Menu { get; } = menu;
        /// <summary>业务键 → 回调；与菜单里的 action 一一对应。</summary>
        internal IReadOnlyDictionary<string, Action> Handlers { get; } = handlers;
    }

    /// <summary>构建完整筛选菜单（只在选项集合变化时调用）。</summary>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="applied">某个选项写回之后的回调（宿主据此刷新勾选态）。</param>
    /// <returns>菜单与同源回调。</returns>
    internal static FilterMenuResult Build(FilterBarViewModel filter, Action applied)
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal);
        var menu = UIMenu.Create(string.Empty, null, new NSString(RootIdentifier), default, new UIMenuElement[]
        {
            Group(SortGroup, filter.SortModes.Select(option => (option.DisplayName, (object)(SortMode)option.Value)).ToArray(),
                filter.SortMode, value => filter.SortMode = (SortMode)value, handlers, applied),
            Group(OrderGroup, filter.SortOrders.Select(option => (option.DisplayName, (object)(SortOrder)option.Value)).ToArray(),
                filter.SortOrder, value => filter.SortOrder = (SortOrder)value, handlers, applied),
            Group(RatingGroup, filter.RatingFilters.Select(option => (option.DisplayName, (object)option.Key)).ToArray(),
                filter.SelectedRatingFilter, value => filter.SelectedRatingFilter = (string)value, handlers, applied),
        });
        return new FilterMenuResult(menu, handlers);
    }

    /// <summary>一个内联单选分组：无标题（系统据此画分隔线），当前项带勾选标记，选项标识即业务键。</summary>
    /// <param name="group">业务分组键（回调索引前缀与 action 标识前缀）。</param>
    /// <param name="options">选项（显示名 + 业务值）。</param>
    /// <param name="selected">当前业务值。</param>
    /// <param name="apply">写回入口。</param>
    /// <param name="handlers">同源回调登记表。</param>
    /// <param name="applied">写回之后的回调。</param>
    /// <returns>内联单选菜单。</returns>
    private static UIMenu Group(string group, (string Title, object Value)[] options, object selected, Action<object> apply,
        Dictionary<string, Action> handlers, Action applied)
    {
        var actions = new UIMenuElement[options.Length];
        for (int index = 0; index < options.Length; index++)
        {
            var option = options[index];
            string key = $"{group}:{option.Value}";
            void Handler()
            {
                apply(option.Value);
                applied();
            }

            var action = UIAction.Create(option.Title, null, key, _ => Handler());
            action.Attributes = UIMenuElementAttributes.KeepsMenuPresented;
            action.State = Mark(Equals(option.Value, selected));
            actions[index] = action;
            handlers[key] = Handler;
        }
        return UIMenu.Create(string.Empty, null, new NSString($"{RootIdentifier}.{group}"),
            UIMenuOptions.DisplayInline | UIMenuOptions.SingleSelection, actions);
    }

    /// <summary>把勾选态就地套到一份菜单上：只改 action 的 <see cref="UIAction.State"/>，不新建菜单与 action。
    /// 改的是模型侧状态，不会让已展开的菜单可见地重绘。</summary>
    /// <param name="menu">目标菜单（可以是系统在 <c>UIContextMenuInteraction.UpdateVisibleMenu</c> 里交来的那份）。</param>
    /// <param name="states">标识 → 勾选态。</param>
    /// <param name="changed">目标菜单上的勾选态是否真的发生变化。</param>
    /// <returns>菜单里的选项标识与 <paramref name="states"/> 一一对应时为 true；结构不同返回 false（需重建菜单）。</returns>
    private static bool ApplyStates(UIMenu menu, IReadOnlyDictionary<string, UIMenuElementState> states, out bool changed)
    {
        changed = false;
        int applied = 0;
        bool matched = true;
        foreach (var action in Actions(menu))
        {
            if (action.Identifier is not { Length: > 0 } identifier || !states.TryGetValue(identifier, out var state))
            {
                matched = false;
                continue;
            }
            applied++;
            if (action.State == state) continue;
            action.State = state;
            changed = true;
        }
        return matched && applied == states.Count;
    }

    /// <summary>把菜单更新交给系统：交回同一份菜单保持菜单与 action 身份（现网实测不会触发可见重绘，
    /// 换成新菜单则会把已展开的菜单滚回顶部）。</summary>
    /// <param name="button">带菜单的按钮。</param>
    /// <param name="update">系统更新入口的菜单变换。</param>
    private static void RefreshPresented(UIButton button, Func<UIMenu, UIMenu> update)
    {
        foreach (var interaction in button.Interactions)
        {
            if (interaction is not UIContextMenuInteraction menuInteraction) continue;
            menuInteraction.UpdateVisibleMenu(update);
            return;
        }
    }

    /// <summary>按业务键算出每个选项应有的勾选态（与菜单 action 标识一一对应）。</summary>
    /// <param name="filter">共享筛选状态。</param>
    /// <returns>标识 → 勾选态。</returns>
    private static Dictionary<string, UIMenuElementState> States(FilterBarViewModel filter)
    {
        var states = new Dictionary<string, UIMenuElementState>(StringComparer.Ordinal);
        foreach (var option in filter.SortModes)
            states[$"{SortGroup}:{option.Value}"] = Mark((SortMode)option.Value == filter.SortMode);
        foreach (var option in filter.SortOrders)
            states[$"{OrderGroup}:{option.Value}"] = Mark((SortOrder)option.Value == filter.SortOrder);
        foreach (var option in filter.RatingFilters)
            states[$"{RatingGroup}:{option.Key}"] = Mark(string.Equals(option.Key, filter.SelectedRatingFilter, StringComparison.Ordinal));
        return states;
    }

    /// <summary>菜单各分组里的 action（根菜单下三层：根 → 分组 → 选项）。</summary>
    /// <param name="menu">目标菜单。</param>
    /// <returns>按菜单顺序排列的 action。</returns>
    private static IEnumerable<UIAction> Actions(UIMenu menu)
        => menu.Children.OfType<UIMenu>().SelectMany(group => group.Children.OfType<UIAction>());

    /// <summary>勾选态常量。</summary>
    /// <param name="selected">是否当前项。</param>
    /// <returns>系统勾选态。</returns>
    private static UIMenuElementState Mark(bool selected) => selected ? UIMenuElementState.On : UIMenuElementState.Off;

}
