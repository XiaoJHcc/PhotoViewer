using System;
using System.Collections.Generic;
using PhotoViewer.Shared;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 业务语义 → Fluent UI System Icons 字形。窗口 chrome 仍使用系统 Segoe 字形。
/// 本表只负责显示；命令能否执行由共享 InputCommandMap 判定。
/// </summary>
internal static class WindowsIcons
{
    /// <summary>Windows 窗口按钮的系统图标字体。</summary>
    public const string FluentFontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets";

    /// <summary>随包 Fluent Regular 字体。</summary>
    public const string RegularFontFamily = "ms-appx:///Assets/Icons/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    /// <summary>随包 Fluent Filled 字体。</summary>
    public const string FilledFontFamily = "ms-appx:///Assets/Icons/FluentSystemIcons-Filled.ttf#FluentSystemIcons-Filled";

    public static string StarGlyph => IconCatalog.FluentGlyph(IconCatalog.RatingStarFilled);
    public static string StarOutlineGlyph => IconCatalog.FluentGlyph(IconCatalog.RatingStar);
    public static string ClearGlyph => IconCatalog.FluentGlyph(IconCatalog.RatingClear);
    public static string WarningGlyph => IconCatalog.FluentGlyph(IconCatalog.RatingWarning);

    // SHELL-02：自绘窗口按钮保留系统字形。
    public const string FullScreenGlyph = "\uE740";
    public const string ExitFullScreenGlyph = "\uE73F";
    public const string MinimizeGlyph = "\uE921";
    public const string MaximizeGlyph = "\uE922";
    public const string RestoreGlyph = "\uE923";
    public const string CloseGlyph = "\uE8BB";

    public static string DocumentGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuOpenFile);
    public static string FolderGlyph => IconCatalog.FluentGlyph(IconCatalog.CommandOpen);
    public static string PhotosGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuFilmstrip);
    public static string AnalysisGridGlyph => IconCatalog.FluentGlyph(IconCatalog.CommandAnalysis);
    public static string SliderGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuControl);
    public static string ExifGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuExif);
    public static string EnhanceGlyph => IconCatalog.FluentGlyph(IconCatalog.CommandEnhance);
    public static string ToolsGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuTools);
    public static string SettingsGlyph => IconCatalog.FluentGlyph(IconCatalog.MenuSettings);
    public static string SortDateGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterSortDate);
    public static string SortSizeGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterSortSize);
    public static string UpGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterAscending);
    public static string DownGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterDescending);
    public static string SelectAllGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterAll);
    public static string UnratedGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterUnrated);
    public static string BlockedGlyph => IconCatalog.FluentGlyph(IconCatalog.FilterRejected);
    public static string RemoveGlyph => IconCatalog.FluentGlyph(IconCatalog.SettingsRemove);
    public static string DragHandleGlyph => IconCatalog.FluentGlyph(IconCatalog.SettingsDrag);
    public static string PhotoPlaceholderGlyph => IconCatalog.FluentGlyph(IconCatalog.CommonPhotoPlaceholder);

    private static readonly Dictionary<string, string> CommandIds = new(StringComparer.Ordinal)
    {
        ["Open"] = IconCatalog.CommandOpen,
        ["Previous"] = IconCatalog.CommandPrevious,
        ["Next"] = IconCatalog.CommandNext,
        ["Exchange"] = IconCatalog.CommandExchange,
        ["Fit"] = IconCatalog.CommandFit,
        ["ZoomInPreset"] = IconCatalog.CommandZoomIn,
        ["ZoomOutPreset"] = IconCatalog.CommandZoomOut,
        ["ZoomInScale"] = IconCatalog.CommandZoomIn,
        ["ZoomOutScale"] = IconCatalog.CommandZoomOut,
        ["SetRating0"] = IconCatalog.RatingClear,
        ["SetRating1"] = IconCatalog.RatingStarFilled,
        ["SetRating2"] = IconCatalog.RatingStarFilled,
        ["SetRating3"] = IconCatalog.RatingStarFilled,
        ["SetRating4"] = IconCatalog.RatingStarFilled,
        ["SetRating5"] = IconCatalog.RatingStarFilled,
        ["ToggleAnalysisView"] = IconCatalog.CommandAnalysis,
        ["ToggleEnhance"] = IconCatalog.CommandEnhance,
    };

    private static readonly HashSet<string> WarnedMissing = new(StringComparer.Ordinal);

    /// <summary>按字形对应的语义状态选择随包 Regular / Filled 字体。</summary>
    public static string FontFamilyForGlyph(string glyph)
        => glyph == StarGlyph || glyph == WarningGlyph ? FilledFontFamily : RegularFontFamily;

    /// <summary>筛选的 1–5 星使用对应数字圆。</summary>
    public static string ForRatingFilter(string key) => key switch
    {
        "Eq1" => IconCatalog.FluentGlyph(IconCatalog.FilterRating1),
        "Eq2" => IconCatalog.FluentGlyph(IconCatalog.FilterRating2),
        "Eq3" => IconCatalog.FluentGlyph(IconCatalog.FilterRating3),
        "Eq4" => IconCatalog.FluentGlyph(IconCatalog.FilterRating4),
        "Eq5" => IconCatalog.FluentGlyph(IconCatalog.FilterRating5),
        _ => throw new ArgumentException($"未定义星级筛选图标：{key}", nameof(key)),
    };

    /// <summary>取命令图标；未登记的命令只记录并隐藏按钮，不影响输入派发。</summary>
    public static string? ForCommand(string command)
    {
        if (CommandIds.TryGetValue(command, out string? id))
        {
            return IconCatalog.FluentGlyph(id);
        }
        if (WarnedMissing.Add(command))
        {
            NativeStartupLog.Write($"图标映射缺少命令「{command}」,该命令不显示按钮;请在 WindowsIcons 中登记。");
        }
        return null;
    }

    public static string ForRatingGlyph(int stars)
        => stars == 0 ? ClearGlyph : StarGlyph;

    public static string ForCategory(string category) => category switch
    {
        "File" => IconCatalog.FluentGlyph(IconCatalog.SettingsFile),
        "Preview" => IconCatalog.FluentGlyph(IconCatalog.SettingsPreview),
        "Control" => IconCatalog.FluentGlyph(IconCatalog.SettingsControl),
        "Exif" => IconCatalog.FluentGlyph(IconCatalog.SettingsExif),
        "Ai" => IconCatalog.FluentGlyph(IconCatalog.SettingsAi),
        _ => throw new ArgumentException($"未定义设置分类图标：{category}", nameof(category)),
    };

    public static string ForToolCategory(string category) => category switch
    {
        "Exif" => IconCatalog.FluentGlyph(IconCatalog.ToolsExif),
        "Stats" => IconCatalog.FluentGlyph(IconCatalog.ToolsStats),
        "Dino" => IconCatalog.FluentGlyph(IconCatalog.ToolsDino),
        "Cv" => IconCatalog.FluentGlyph(IconCatalog.ToolsCv),
        _ => throw new ArgumentException($"未定义工具分类图标：{category}", nameof(category)),
    };

    public static bool TryGetRatingStars(string command, out int stars)
    {
        stars = 0;
        if (!command.StartsWith("SetRating", StringComparison.Ordinal))
        {
            return false;
        }
        return int.TryParse(command.AsSpan("SetRating".Length), out stars);
    }
}
