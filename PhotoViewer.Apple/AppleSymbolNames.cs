namespace PhotoViewer.Apple;

/// <summary>业务命令与菜单项到 SF Symbols 名称的平台中立映射；系统图标对象的创建留在各平台头部。</summary>
public static class AppleSymbolNames
{
    public static class Menu
    {
        public const string OpenFile = "doc";
        public const string OpenFolder = "folder";
        public const string Filmstrip = "rectangle.stack";
        public const string Analysis = "square.grid.2x2";
        public const string Control = "slider.horizontal.3";
        public const string Enhance = "wand.and.stars";
        public const string Tools = "wrench.and.screwdriver";
        public const string Settings = "gearshape";
        public const string Quit = "power";
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
