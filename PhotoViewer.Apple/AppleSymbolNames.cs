using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Apple;

/// <summary>业务语义到 SF Symbols 名称；系统图标对象的创建留在各平台头部。</summary>
public static class AppleSymbolNames
{
    public static class Menu
    {
        public static string OpenFile => IconCatalog.Apple(IconCatalog.MenuOpenFile);
        public static string OpenFolder => IconCatalog.Apple(IconCatalog.CommandOpen);
        public static string Filmstrip => IconCatalog.Apple(IconCatalog.MenuFilmstrip);
        public static string Analysis => IconCatalog.Apple(IconCatalog.CommandAnalysis);
        public static string Control => IconCatalog.Apple(IconCatalog.MenuControl);
        public static string Exif => IconCatalog.Apple(IconCatalog.MenuExif);
        public static string Enhance => IconCatalog.Apple(IconCatalog.CommandEnhance);
        public static string Tools => IconCatalog.Apple(IconCatalog.MenuTools);
        public static string Settings => IconCatalog.Apple(IconCatalog.MenuSettings);
        public static string Quit => IconCatalog.Apple(IconCatalog.MenuQuit);
    }

    public static class Filter
    {
        public static string Entry => IconCatalog.Apple(IconCatalog.FilterEntry);

        public static string ForSortMode(SortMode mode) => mode switch
        {
            SortMode.Name => IconCatalog.Apple(IconCatalog.FilterSortName),
            SortMode.Date => IconCatalog.Apple(IconCatalog.FilterSortDate),
            SortMode.Size => IconCatalog.Apple(IconCatalog.FilterSortSize),
            _ => throw new InvalidOperationException($"未定义排序方式图标：{mode}"),
        };

        public static string ForSortOrder(SortOrder order)
            => IconCatalog.Apple(order == SortOrder.Ascending ? IconCatalog.FilterAscending : IconCatalog.FilterDescending);

        public static string ForRatingFilter(string key) => key switch
        {
            "All" => IconCatalog.Apple(IconCatalog.FilterAll),
            "None" => IconCatalog.Apple(IconCatalog.FilterUnrated),
            "Eq1" => IconCatalog.Apple(IconCatalog.FilterRating1),
            "Eq2" => IconCatalog.Apple(IconCatalog.FilterRating2),
            "Eq3" => IconCatalog.Apple(IconCatalog.FilterRating3),
            "Eq4" => IconCatalog.Apple(IconCatalog.FilterRating4),
            "Eq5" => IconCatalog.Apple(IconCatalog.FilterRating5),
            "Rejected" => IconCatalog.Apple(IconCatalog.FilterRejected),
            "Conflict" => IconCatalog.Apple(IconCatalog.FilterConflict),
            _ => throw new InvalidOperationException($"未定义星级筛选图标：{key}"),
        };
    }

    /// <summary>按业务命令取得 SF Symbols 名称；未定义映射的命令显式抛出，避免生成无图标按钮。</summary>
    public static string? ForCommand(string command) => command switch
    {
        "Open" => Menu.OpenFolder,
        "Previous" => IconCatalog.Apple(IconCatalog.CommandPrevious),
        "Next" => IconCatalog.Apple(IconCatalog.CommandNext),
        "Exchange" => IconCatalog.Apple(IconCatalog.CommandExchange),
        "Fit" => IconCatalog.Apple(IconCatalog.CommandFit),
        "ZoomInPreset" or "ZoomInScale" => IconCatalog.Apple(IconCatalog.CommandZoomIn),
        "ZoomOutPreset" or "ZoomOutScale" => IconCatalog.Apple(IconCatalog.CommandZoomOut),
        "SetRating0" => IconCatalog.Apple(IconCatalog.RatingClear),
        "SetRating1" or "SetRating2" or "SetRating3" or "SetRating4" or "SetRating5"
            => IconCatalog.Apple(IconCatalog.RatingStar),
        "ToggleAnalysisView" => Menu.Analysis,
        "ToggleEnhance" => Menu.Enhance,
        _ => throw new InvalidOperationException($"未定义原生命令图标：{command}"),
    };
}
