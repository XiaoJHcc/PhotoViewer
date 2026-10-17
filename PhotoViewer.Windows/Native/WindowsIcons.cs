using System;
using System.Collections.Generic;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 命令 → Windows 原生图标字形。
/// 与 macOS 的 SF Symbols 映射同理:平台层维护"命令 → 图标"的显式表,共享层只给命令标识,
/// 不修改快捷键 JSON 里的私用区字码。
/// </summary>
internal static class WindowsIcons
{
    /// <summary>Windows 11 图标字体。带 MDL2 回退链,旧系统缺 Fluent 字体时不至于出现空框。</summary>
    public const string FluentFontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets";

    /// <summary>已评分星形字形。</summary>
    public const string StarGlyph = "\uE735";

    /// <summary>清零评分字形。</summary>
    public const string ClearGlyph = "\uE711";

    /// <summary>命令 → 单字形映射。null 表示该命令在本阶段不显示/不可达(P3 延期)。</summary>
    private static readonly Dictionary<string, string?> Glyphs = new(StringComparer.Ordinal)
    {
        ["Open"] = "\uE8E5",
        ["Previous"] = "\uE76B",
        ["Next"] = "\uE76C",
        ["Exchange"] = "\uE8AB",
        ["Fit"] = "\uE9A6",
        ["ZoomInPreset"] = "\uE8A3",
        ["ZoomOutPreset"] = "\uE71F",
        ["ZoomInScale"] = "\uE8A3",
        ["ZoomOutScale"] = "\uE71F",
        ["SetRating0"] = ClearGlyph,
        ["SetRating1"] = StarGlyph,
        ["SetRating2"] = StarGlyph,
        ["SetRating3"] = StarGlyph,
        ["SetRating4"] = StarGlyph,
        ["SetRating5"] = StarGlyph,

        // 主屏 P3 入口(第二轮补齐):分析栏开关与增强预览
        ["ToggleAnalysisView"] = "\uE9D9",
        ["ToggleEnhance"] = "\uE945",
    };

    /// <summary>已记录过缺失图标的命令,避免用户配置漂移时每次按键都写日志。</summary>
    private static readonly HashSet<string> WarnedMissing = new(StringComparer.Ordinal);

    /// <summary>
    /// 取命令的图标字形。
    /// 表里没有的命令视为配置漂移:记录一次日志并返回 null(不显示、不消费按键),
    /// 不能让用户可编辑的快捷键 JSON 把进程打死。
    /// </summary>
    /// <param name="command">命令标识</param>
    /// <returns>字形;null 表示该命令未知或本阶段不显示</returns>
    public static string? ForCommand(string command)
    {
        if (Glyphs.TryGetValue(command, out var glyph))
        {
            return glyph;
        }

        if (WarnedMissing.Add(command))
        {
            NativeStartupLog.Write($"图标映射缺少命令「{command}」,已按不可用处理;请在 WindowsIcons 中登记。");
        }

        return null;
    }

    /// <summary>
    /// 设置分类 → 导航图标字形。分类键由设置窗口固定使用(File/Preview/Control/Exif/Ai)。
    /// </summary>
    /// <param name="category">分类键</param>
    /// <returns>Segoe Fluent Icons 字形</returns>
    public static string ForCategory(string category) => category switch
    {
        "File" => "\uE8B7",
        "Preview" => "\uE8B9",
        "Control" => "\uE765",
        "Exif" => "\uE946",
        "Ai" => "\uE945",
        _ => "\uE713",
    };

    /// <summary>
    /// 工具窗口分类 → 导航图标字形。分类键由工具窗口固定使用(Exif/Stats/Dino)。
    /// </summary>
    /// <param name="category">分类键</param>
    /// <returns>Segoe Fluent Icons 字形</returns>
    public static string ForToolCategory(string category) => category switch
    {
        "Exif" => "\uE946",
        "Stats" => "\uE9D2",
        "Dino" => "\uE99A",
        _ => WindowsLayout.ToolsWindowGlyph,
    };

    /// <summary>
    /// 判断命令是否为"显示星数"的评分命令
    /// (TOOLBAR-01:控制栏里显示的评分快捷命令要保留星数)。
    /// </summary>
    /// <param name="command">命令标识</param>
    /// <param name="stars">星数(0 表示清零命令)</param>
    /// <returns>是否评分命令</returns>
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
