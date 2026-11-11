using UIKit;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 筛选菜单：根菜单下三个内联单选分组（排序方式、排序方向、星级筛选），分组之间由系统画分隔线；
/// 选项保持菜单展开，选中后写回共享筛选状态，失焦才关闭。手机形态的筛选圆按钮与宽形态的单胶囊
/// （<see cref="iOSFilterControl"/>）共用本菜单；数据与当前值一律现读共享
/// <see cref="FilterBarViewModel"/>，本类不缓存筛选条件。
/// </summary>
internal static class iOSFilterMenu
{
    /// <summary>菜单正在显示时用系统入口就地替换内容，保持展开态并刷新勾选。</summary>
    /// <param name="button">带菜单的按钮。</param>
    internal static void UpdateVisibleMenu(UIButton button)
    {
        foreach (var interaction in button.Interactions)
        {
            if (interaction is not UIContextMenuInteraction menuInteraction) continue;
            menuInteraction.UpdateVisibleMenu(_ => button.Menu!);
            return;
        }
    }

    /// <summary>一次菜单构建的产物：系统菜单对象与按业务键索引的同源回调（<see cref="UIAction"/> 不公开 handler）。</summary>
    /// <param name="menu">交给按钮的菜单。</param>
    /// <param name="handlers">业务键 → 回调；键为 <c>sort:值</c> / <c>order:值</c> / <c>rating:键</c>。</param>
    internal sealed class FilterMenuResult(UIMenu menu, IReadOnlyDictionary<string, Action> handlers)
    {
        /// <summary>交给按钮的菜单。</summary>
        internal UIMenu Menu { get; } = menu;
        /// <summary>业务键 → 回调；与菜单里的 action 一一对应。</summary>
        internal IReadOnlyDictionary<string, Action> Handlers { get; } = handlers;
    }

    /// <summary>构建完整筛选菜单。</summary>
    /// <param name="filter">共享筛选状态。</param>
    /// <param name="applied">某个选项写回之后的回调（宿主据此刷新勾选态）。</param>
    /// <returns>菜单与同源回调。</returns>
    internal static FilterMenuResult Build(FilterBarViewModel filter, Action applied)
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal);
        var menu = UIMenu.Create(new UIMenuElement[]
        {
            Group("sort", filter.SortModes.Select(option => (option.DisplayName, (object)(SortMode)option.Value)).ToArray(),
                filter.SortMode, value => filter.SortMode = (SortMode)value, handlers, applied),
            Group("order", filter.SortOrders.Select(option => (option.DisplayName, (object)(SortOrder)option.Value)).ToArray(),
                filter.SortOrder, value => filter.SortOrder = (SortOrder)value, handlers, applied),
            Group("rating", filter.RatingFilters.Select(option => (option.DisplayName, (object)option.Key)).ToArray(),
                filter.SelectedRatingFilter, value => filter.SelectedRatingFilter = (string)value, handlers, applied),
        });
        return new FilterMenuResult(menu, handlers);
    }

    /// <summary>一个内联单选分组：无标题（系统据此画分隔线），当前项带勾选标记。</summary>
    /// <param name="group">业务分组键（回调索引前缀）。</param>
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
            void Handler()
            {
                apply(option.Value);
                applied();
            }

            var action = UIAction.Create(option.Title, null, null, _ => Handler());
            action.Attributes = UIMenuElementAttributes.KeepsMenuPresented;
            action.State = Equals(option.Value, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
            handlers[$"{group}:{option.Value}"] = Handler;
        }
        return UIMenu.Create(string.Empty, null, UIMenuIdentifier.None,
            UIMenuOptions.DisplayInline | UIMenuOptions.SingleSelection, actions);
    }
}
