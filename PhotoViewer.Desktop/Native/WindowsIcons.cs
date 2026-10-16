using System;
using System.Collections.Generic;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 命令 → Windows 原生图标字形。
/// 与 macOS 的 SF Symbols 映射同理:平台层维护"命令 → 图标"的显式表,共享层只给命令标识,
/// 不修改快捷键 JSON 里的私用区字码。
/// </summary>
internal static class WindowsIcons
{
    /// <summary>Windows 11 图标字体。</summary>
    public const string FluentFontFamily = "Segoe Fluent Icons";

    /// <summary>Windows 10 图标字体(回退)。</summary>
    public const string ClassicFontFamily = "Segoe MDL2 Assets";

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

        // P3 延期:原生壳不显示入口,热键也不消费(与 macOS 的 MacSymbols 同策略)
        ["ToggleAnalysisView"] = null,
        ["ToggleEnhance"] = null,
    };

    /// <summary>
    /// 取命令的图标字形。
    /// 表里没有的命令视为映射表漂移,直接抛异常 —— 未知命令绝不能静默画成空按钮。
    /// </summary>
    /// <param name="command">命令标识</param>
    /// <returns>字形;null 表示该命令本阶段不显示</returns>
    public static string? ForCommand(string command)
    {
        if (Glyphs.TryGetValue(command, out var glyph))
        {
            return glyph;
        }

        throw new InvalidOperationException($"Windows 图标映射缺少命令「{command}」;请在 WindowsIcons 中登记。");
    }

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
