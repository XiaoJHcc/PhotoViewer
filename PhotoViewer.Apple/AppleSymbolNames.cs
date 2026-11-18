using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Apple;

/// <summary>业务命令与菜单项到 SF Symbols 名称的平台中立映射；系统图标对象的创建留在各平台头部。
/// 本表只管 UI：命令能否执行以共享 <c>InputCommandMap.IsCommandKnown</c> 为准，输入派发不得以缺图标作否决条件。</summary>
public static class AppleSymbolNames
{
    public static class Menu
    {
        public const string OpenFile = "doc";
        public const string OpenFolder = "folder";
        public const string Filmstrip = "rectangle.stack";
        public const string Analysis = "square.grid.2x2";
        public const string Control = "slider.horizontal.3";
        public const string Exif = "text.below.photo";
        public const string Enhance = "wand.and.stars";
        public const string Tools = "wrench.and.screwdriver";
        public const string Settings = "gearshape";
        public const string Quit = "power";
    }

    public static class Filter
    {
        public const string Entry = "line.3.horizontal.decrease";

        public static string ForSortMode(SortMode mode) => mode switch
        {
            SortMode.Name => "textformat",
            SortMode.Date => "camera",
            SortMode.Size => "internaldrive",
            _ => throw new InvalidOperationException($"未定义排序方式图标：{mode}"),
        };

        public static string ForSortOrder(SortOrder order) => order == SortOrder.Ascending ? "arrow.up" : "arrow.down";

        public static string ForRatingFilter(string key) => key switch
        {
            "All" => "square.grid.3x3",
            "None" => "star.slash",
            "Eq1" => "1.circle",
            "Eq2" => "2.circle",
            "Eq3" => "3.circle",
            "Eq4" => "4.circle",
            "Eq5" => "5.circle",
            "Rejected" => "xmark.circle",
            "Conflict" => "exclamationmark.triangle",
            _ => throw new InvalidOperationException($"未定义星级筛选图标：{key}"),
        };
    }

    /// <summary>按业务命令取得 SF Symbols 名称；未定义映射的命令显式抛出，避免生成无图标按钮。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>系统符号名称。</returns>
    public static string? ForCommand(string command) => command switch
    {
        "Open" => Menu.OpenFolder, "Previous" => "chevron.left", "Next" => "chevron.right",
        "Exchange" => "arrow.left.arrow.right", "Fit" => "arrow.up.left.and.arrow.down.right",
        "ZoomInPreset" or "ZoomInScale" => "plus", "ZoomOutPreset" or "ZoomOutScale" => "minus",
        "SetRating0" => "xmark", "SetRating1" or "SetRating2" or "SetRating3" or "SetRating4" or "SetRating5" => "star",
        "ToggleAnalysisView" => Menu.Analysis, "ToggleEnhance" => Menu.Enhance,
        _ => throw new InvalidOperationException($"未定义原生命令图标：{command}"),
    };
}
