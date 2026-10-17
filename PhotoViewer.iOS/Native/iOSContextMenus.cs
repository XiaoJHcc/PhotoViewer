using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生上下文菜单构建（[INPUT-01] 的触屏入口）：
/// 主图菜单与 Mac 原生右键菜单同序（打开文件夹、栏位显隐、增强、工具、设置），I5 起分析栏/增强/工具页都是真实可用项；
/// 胶片栏卡片菜单提供评分 0–5，并以菜单标题承载完整文件名与拍摄时间（[FILM-05] 的触屏 tooltip 等价物）。
/// 菜单在每次弹出时现建，勾选态与星级直接读共享状态，不做缓存。
/// </summary>
internal static class iOSContextMenus
{
    /// <summary>主图菜单标识（验收按标识核对配置来源）。</summary>
    internal const string PhotoIdentifier = "photo";
    /// <summary>卡片菜单标识。</summary>
    internal const string CardIdentifier = "card";

    /// <summary>一次菜单构建的产物：系统菜单对象与按 action 标识索引的同源回调。</summary>
    internal sealed class MenuResult(string identifier, UIMenu menu, IReadOnlyDictionary<string, Action> handlers)
    {
        /// <summary>菜单标识（写入 <see cref="UIContextMenuConfiguration"/>）。</summary>
        internal string Identifier { get; } = identifier;
        /// <summary>交给系统的菜单对象。</summary>
        internal UIMenu Menu { get; } = menu;
        /// <summary>action 标识 → 回调；与菜单里的 <see cref="UIAction"/> 一一对应（UIAction 不暴露 handler，验收经此调用同一份委托）。</summary>
        internal IReadOnlyDictionary<string, Action> Handlers { get; } = handlers;
    }

    /// <summary>
    /// 主图上下文菜单：打开文件夹、胶片栏/分析栏/控制栏显隐（勾选态取当前共享状态）、增强预览、
    /// 工具页与设置。分组用 <see cref="UIMenuOptions.DisplayInline"/> 形成系统分隔段。
    /// </summary>
    /// <param name="filmVisible">胶片栏当前是否可见。</param>
    /// <param name="controlVisible">控制栏当前是否可见。</param>
    /// <param name="analysisVisible">分析栏当前是否可见。</param>
    /// <param name="enhanced">增强预览当前是否开启。</param>
    /// <param name="openFolder">打开文件夹回调。</param>
    /// <param name="toggleFilm">切换胶片栏回调。</param>
    /// <param name="toggleControl">切换控制栏回调。</param>
    /// <param name="toggleAnalysis">切换分析栏回调。</param>
    /// <param name="toggleEnhance">切换增强预览回调。</param>
    /// <param name="openTools">打开工具页回调。</param>
    /// <param name="openSettings">打开设置回调。</param>
    /// <returns>菜单与同源回调。</returns>
    internal static MenuResult Photo(bool filmVisible, bool controlVisible, bool analysisVisible, bool enhanced,
        Action openFolder, Action toggleFilm, Action toggleControl, Action toggleAnalysis, Action toggleEnhance,
        Action openTools, Action openSettings)
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal);
        var open = Item(handlers, "photo.open-folder", "打开文件夹…", "folder", null, openFolder);
        var toggles = UIMenu.Create(string.Empty, null!, new NSString("photo.panes"), UIMenuOptions.DisplayInline,
        [
            Item(handlers, "photo.film", "胶片栏", "rectangle.stack", filmVisible, toggleFilm),
            Item(handlers, "photo.analysis", "分析栏", "square.grid.2x2", analysisVisible, toggleAnalysis),
            Item(handlers, "photo.control", "控制栏", "slider.horizontal.3", controlVisible, toggleControl),
        ]);
        var enhance = Item(handlers, "photo.enhance", "增强", "wand.and.stars", enhanced, toggleEnhance);
        var settings = UIMenu.Create(string.Empty, null!, new NSString("photo.settings"), UIMenuOptions.DisplayInline,
        [
            Item(handlers, "photo.tools", "工具…", "wrench.and.screwdriver", null, openTools),
            Item(handlers, "photo.settings.open", "设置…", "gearshape", null, openSettings),
        ]);
        var menu = UIMenu.Create(string.Empty, null!, new NSString(PhotoIdentifier), default, [open, toggles, enhance, settings]);
        return new MenuResult(PhotoIdentifier, menu, handlers);
    }

    /// <summary>
    /// 胶片栏卡片上下文菜单：标题为完整文件名与拍摄时间（[FILM-05] tooltip 的触屏等价物），
    /// 唯一功能是"评分 0–5"——写回走共享评分路径，不改变当前主图（[FILM-06] 暂留语义由共享列表负责）。
    /// </summary>
    /// <param name="file">卡片绑定的照片。</param>
    /// <param name="rate">评分回调（0 清空）。</param>
    /// <returns>菜单与同源回调。</returns>
    internal static MenuResult Card(ImageFile file, Action<int> rate)
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal);
        var items = new UIMenuElement[6];
        for (int rating = 0; rating < 6; rating++)
        {
            int value = rating;
            items[rating] = Item(handlers, $"card.rating.{rating}", rating == 0 ? "无星级" : $"{rating} 星",
                rating == 0 ? "xmark" : "star", file.Rating == rating, () => rate(value));
        }
        var ratingMenu = UIMenu.Create("评分", null!, new NSString("card.rating-menu"), default, items);
        string title = file.PhotoDate is { } date
            ? $"{file.Name} · {date:yyyy-MM-dd HH:mm:ss zzz}"
            : file.Name;
        var menu = UIMenu.Create(title, null!, new NSString(CardIdentifier), default, [ratingMenu]);
        return new MenuResult(CardIdentifier, menu, handlers);
    }

    /// <summary>创建一条 action 并登记同一份回调；勾选态为 null 时是普通命令。</summary>
    /// <param name="handlers">action 标识 → 回调。</param>
    /// <param name="identifier">action 标识。</param>
    /// <param name="title">显示标题。</param>
    /// <param name="symbol">SF Symbols 名称。</param>
    /// <param name="check">勾选态；null 表示不显示勾选。</param>
    /// <param name="handler">执行动作。</param>
    /// <returns>系统 action 对象。</returns>
    private static UIAction Item(Dictionary<string, Action> handlers, string identifier, string title, string symbol,
        bool? check, Action handler)
    {
        var action = UIAction.Create(title, iOSSymbols.Symbol(symbol), identifier, _ => handler());
        if (check.HasValue) action.State = check.Value ? UIMenuElementState.On : UIMenuElementState.Off;
        handlers[identifier] = handler;
        return action;
    }
}

/// <summary>
/// 上下文菜单交互委托：每次弹出时调用宿主提供的构建函数现建菜单，
/// 并把最近一次构建结果留在 <see cref="Menu"/> 供验收核对（<see cref="UIContextMenuConfiguration"/> 不公开 action provider）。
/// </summary>
internal sealed class iOSContextMenuDelegate : UIContextMenuInteractionDelegate
{
    private readonly Func<CGPoint, iOSContextMenus.MenuResult?> _build;

    /// <summary>绑定菜单构建函数。</summary>
    /// <param name="build">按触点构建菜单；返回 null 表示本次不弹菜单。</param>
    internal iOSContextMenuDelegate(Func<CGPoint, iOSContextMenus.MenuResult?> build) => _build = build;

    /// <summary>最近一次经系统入口构建的菜单（测试缝：验收读取菜单内容与回调）。</summary>
    internal iOSContextMenus.MenuResult? Menu { get; private set; }

    /// <summary>系统在长按/辅助点按命中时询问配置；菜单由同一份构建产物给出。</summary>
    /// <param name="interaction">上下文菜单交互。</param>
    /// <param name="location">触点（交互视图坐标）。</param>
    /// <returns>菜单配置；构建函数放弃时返回 null。</returns>
    public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
    {
        var result = _build(location);
        Menu = result;
        return result == null ? null
            : UIContextMenuConfiguration.Create(new NSString(result.Identifier), null!, _ => result.Menu);
    }
}
