using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>FILTER-08：宽形态与手机形态共用的系统筛选菜单。</summary>
internal static class iOSFilterMenu
{
    private const string RootIdentifier = "filter";
    private const string SortGroup = "sort";
    private const string RatingGroup = "rating";
    private const string OrderGroup = "order";

    internal sealed record FilterMenuResult(UIMenu Menu, IReadOnlyDictionary<string, Action> Handlers);

    internal static FilterMenuResult Build(FilterBarViewModel filter, Action? selectionChanged = null)
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal);
        var sort = Group(SortGroup, true, filter.SortModes.Select(option =>
        {
            var value = (SortMode)option.Value;
            return new Option(value.ToString(), option.DisplayName, AppleSymbolNames.Filter.ForSortMode(value),
                value == filter.SortMode, () => filter.SortMode = value);
        }), handlers, selectionChanged);
        sort.PreferredElementSize = UIMenuElementSize.Medium;

        var rating = Group(RatingGroup, false, filter.RatingFilters.Select(option =>
            new Option(option.Key, option.DisplayName, AppleSymbolNames.Filter.ForRatingFilter(option.Key),
                filter.IsRatingFilterSelected(option.Key), () => filter.ToggleRatingFilter(option.Key))), handlers, selectionChanged);

        var order = Group(OrderGroup, true, filter.SortOrders.Select(option =>
        {
            var value = (SortOrder)option.Value;
            return new Option(value.ToString(), option.DisplayName, AppleSymbolNames.Filter.ForSortOrder(value),
                value == filter.SortOrder, () => filter.SortOrder = value);
        }), handlers, selectionChanged);

        var menu = UIMenu.Create(string.Empty, null, new NSString(RootIdentifier), default, [sort, rating, order]);
        return new FilterMenuResult(menu, handlers);
    }

    internal static void Attach(iOSMenuButton button, FilterBarViewModel filter)
    {
        // 菜单展示的是 UIKit 的副本；把新状态交给当前呈现的菜单，而非改源对象。
        UIMenu BuildForButton() => Build(filter, () =>
            button.ContextMenuInteraction?.UpdateVisibleMenu(_ => BuildForButton())).Menu;
        button.MenuProvider = BuildForButton;
        Refresh(button, filter);
    }

    internal static void Refresh(UIButton button, FilterBarViewModel filter)
    {
        button.AccessibilityValue = filter.RatingFilterSelectionText;
        if (button is iOSMenuButton menuButton)
        {
            var selected = filter.RatingFilters
                .Where(option => option.Key != PhotoListProjection.AllRatings && filter.IsRatingFilterSelected(option.Key))
                .ToArray();
            string symbol = selected.Length == 1
                ? AppleSymbolNames.Filter.ForRatingFilter(selected[0].Key)
                : AppleSymbolNames.Filter.Entry;
            menuButton.SetGlassState(selected.Length > 0, symbol);
        }
    }

    internal static bool ApplySelection(UIMenu menu, FilterBarViewModel filter)
        => TryApplySelection(menu, States(filter), out _);

    private sealed record Option(string Value, string Title, string Symbol, bool Selected, Action Apply);

    private static UIMenu Group(string group, bool singleSelection, IEnumerable<Option> options,
        Dictionary<string, Action> handlers, Action? selectionChanged)
    {
        var actions = new List<UIMenuElement>();
        foreach (var option in options)
        {
            string key = $"{group}:{option.Value}";
            var action = UIAction.Create(option.Title, iOSSymbols.Symbol(option.Symbol), key, _ =>
            {
                option.Apply();
                selectionChanged?.Invoke();
            });
            action.Attributes = UIMenuElementAttributes.KeepsMenuPresented;
            action.State = Mark(option.Selected);
            actions.Add(action);
            handlers.Add(key, option.Apply);
        }

        var flags = UIMenuOptions.DisplayInline;
        if (singleSelection) flags |= UIMenuOptions.SingleSelection;
        return UIMenu.Create(string.Empty, null, new NSString($"{RootIdentifier}.{group}"), flags, actions.ToArray());
    }

    private static bool TryApplySelection(UIMenu menu, IReadOnlyDictionary<string, UIMenuElementState> states, out bool changed)
    {
        var actions = Actions(menu).ToArray();
        changed = false;
        if (actions.Length != states.Count
            || !states.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actions.Select(action => action.Identifier))) return false;
        changed = ApplyStates(actions, states);
        return true;
    }

    private static bool ApplyStates(IEnumerable<UIAction> actions, IReadOnlyDictionary<string, UIMenuElementState> states)
    {
        bool changed = false;
        foreach (var action in actions)
        {
            if (action.Identifier is not { } id || !states.TryGetValue(id, out var state) || action.State == state) continue;
            action.State = state;
            changed = true;
        }
        return changed;
    }

    private static IEnumerable<UIAction> Actions(UIMenu menu)
        => menu.Children.OfType<UIAction>().Concat(menu.Children.OfType<UIMenu>().SelectMany(Actions));

    private static Dictionary<string, UIMenuElementState> States(FilterBarViewModel filter)
    {
        var states = new Dictionary<string, UIMenuElementState>(StringComparer.Ordinal);
        foreach (var option in filter.SortModes)
            states[$"{SortGroup}:{option.Value}"] = Mark((SortMode)option.Value == filter.SortMode);
        foreach (var option in filter.RatingFilters)
            states[$"{RatingGroup}:{option.Key}"] = Mark(filter.IsRatingFilterSelected(option.Key));
        foreach (var option in filter.SortOrders)
            states[$"{OrderGroup}:{option.Value}"] = Mark((SortOrder)option.Value == filter.SortOrder);
        return states;
    }

    private static UIMenuElementState Mark(bool selected)
        => selected ? UIMenuElementState.On : UIMenuElementState.Off;
}
