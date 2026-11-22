using System;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>业务语义 → 与 Windows 共享的 Fluent 字形；字体对象由 AndroidUiTheme 创建。</summary>
internal static class AndroidIcons
{
    internal static string OpenFolder => IconCatalog.FluentGlyph(IconCatalog.CommandOpen);
    internal static string Previous => IconCatalog.FluentGlyph(IconCatalog.CommandPrevious);
    internal static string Next => IconCatalog.FluentGlyph(IconCatalog.CommandNext);
    internal static string Filter => IconCatalog.FluentGlyph(IconCatalog.FilterEntry);
    internal static string More => IconCatalog.FluentGlyph(IconCatalog.FilterMore);
    internal static string Star => IconCatalog.FluentGlyph(IconCatalog.RatingStar);
    internal static string StarFilled => IconCatalog.FluentGlyph(IconCatalog.RatingStarFilled);
    internal static string Clear => IconCatalog.FluentGlyph(IconCatalog.RatingClear);
    internal static string Warning => IconCatalog.FluentGlyph(IconCatalog.RatingWarning);
    internal static string Check => IconCatalog.FluentGlyph(IconCatalog.CommonCheck);
    internal static string Back => IconCatalog.FluentGlyph(IconCatalog.NavigationBack);
    internal static string Forward => IconCatalog.FluentGlyph(IconCatalog.NavigationForward);
    internal static string Close => IconCatalog.FluentGlyph(IconCatalog.NavigationClose);
    internal static string Add => IconCatalog.FluentGlyph(IconCatalog.SettingsAdd);
    internal static string Remove => IconCatalog.FluentGlyph(IconCatalog.SettingsRemove);
    internal static string DragHandle => IconCatalog.FluentGlyph(IconCatalog.SettingsDrag);

    internal static string Glyph(string id) => IconCatalog.FluentGlyph(id);
    internal static bool Filled(string id) => IconCatalog.FluentFilled(id);

    internal static string ForCommand(string command) => command switch
    {
        "Open" => Glyph(IconCatalog.CommandOpen),
        "Previous" => Previous,
        "Next" => Next,
        "Exchange" => Glyph(IconCatalog.CommandExchange),
        "Fit" => Glyph(IconCatalog.CommandFit),
        "ZoomInPreset" or "ZoomInScale" => Glyph(IconCatalog.CommandZoomIn),
        "ZoomOutPreset" or "ZoomOutScale" => Glyph(IconCatalog.CommandZoomOut),
        "SetRating0" => Clear,
        "SetRating1" or "SetRating2" or "SetRating3" or "SetRating4" or "SetRating5" => StarFilled,
        "ToggleAnalysisView" => Glyph(IconCatalog.CommandAnalysis),
        "ToggleEnhance" => Glyph(IconCatalog.CommandEnhance),
        _ => throw new ArgumentException($"未定义 Android 命令图标：{command}", nameof(command)),
    };

    internal static string ForMenu(PhotoMenuItemId item) => item switch
    {
        PhotoMenuItemId.OpenFile => Glyph(IconCatalog.MenuOpenFile),
        PhotoMenuItemId.OpenFolder => OpenFolder,
        PhotoMenuItemId.Filmstrip => Glyph(IconCatalog.MenuFilmstrip),
        PhotoMenuItemId.Analysis => Glyph(IconCatalog.CommandAnalysis),
        PhotoMenuItemId.ControlBar => Glyph(IconCatalog.MenuControl),
        PhotoMenuItemId.Exif => Glyph(IconCatalog.MenuExif),
        PhotoMenuItemId.Enhance => Glyph(IconCatalog.CommandEnhance),
        PhotoMenuItemId.Tools => Glyph(IconCatalog.MenuTools),
        PhotoMenuItemId.Settings => Glyph(IconCatalog.MenuSettings),
        _ => throw new ArgumentException($"未定义 Android 菜单图标：{item}", nameof(item)),
    };

    internal static string SortModeId(SortMode mode) => mode switch
    {
        SortMode.Name => IconCatalog.FilterSortName,
        SortMode.Date => IconCatalog.FilterSortDate,
        SortMode.Size => IconCatalog.FilterSortSize,
        _ => throw new ArgumentException($"未定义 Android 排序图标：{mode}", nameof(mode)),
    };

    internal static string RatingFilterId(string key) => key switch
    {
        PhotoListProjection.AllRatings => IconCatalog.FilterAll,
        "None" => IconCatalog.FilterUnrated,
        "Eq1" => IconCatalog.FilterRating1,
        "Eq2" => IconCatalog.FilterRating2,
        "Eq3" => IconCatalog.FilterRating3,
        "Eq4" => IconCatalog.FilterRating4,
        "Eq5" => IconCatalog.FilterRating5,
        "Rejected" => IconCatalog.FilterRejected,
        "Conflict" => IconCatalog.FilterConflict,
        _ => throw new ArgumentException($"未定义 Android 星级筛选图标：{key}", nameof(key)),
    };
}
