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

    /// <summary>星级冲突警示三角字形(RATING-04,与 <c>icon.warning</c> 同义)。</summary>
    public const string WarningGlyph = "\uE7BA";

    /// <summary>命令 → 单字形映射。null 表示该命令在本阶段不显示/不可达(P3 延期)。</summary>
    private static readonly Dictionary<string, string?> Glyphs = new(StringComparer.Ordinal)
    {
        // 打开用文件夹而不是“带箭头文档”，与旧壳和新手预期一致。
        ["Open"] = "\uE8B7",
        ["Previous"] = "\uE76B",
        ["Next"] = "\uE76C",
        ["Exchange"] = "\uE8AB",
        // 缩放适应沿用旧壳的对外双对角箭头（BackToWindow），不用带方框的 FitPage。
        ["Fit"] = "\uE73F",
        // 放大/缩小用纯加号/减号，不用放大镜（放大镜在 18 点尺寸下与“适应”难区分）。
        ["ZoomInPreset"] = "\uE710",
        ["ZoomOutPreset"] = "\uE738",
        ["ZoomInScale"] = "\uE710",
        ["ZoomOutScale"] = "\uE738",
        ["SetRating0"] = ClearGlyph,
        ["SetRating1"] = StarGlyph,
        ["SetRating2"] = StarGlyph,
        ["SetRating3"] = StarGlyph,
        ["SetRating4"] = StarGlyph,
        ["SetRating5"] = StarGlyph,

        // 主屏 P3 入口(第二轮补齐):分析栏开关与增强预览
        ["ToggleAnalysisView"] = "\uE9D9",
        // 增强预览画对比度半圆（旧壳字形同义），比闪电更贴近“明暗增强”而不是“性能/加速”。
        ["ToggleEnhance"] = "\uE7A1",
    };

    /// <summary>评分快捷命令底图:0 为清零叉,1–5 为实心星(数字另行叠在星心,TOOLBAR-04)。</summary>
    private static readonly Dictionary<int, string> RatingGlyphs = new()
    {
        [0] = ClearGlyph,
        [1] = StarGlyph,
        [2] = StarGlyph,
        [3] = StarGlyph,
        [4] = StarGlyph,
        [5] = StarGlyph,
    };

    /// <summary>已记录过缺失图标的命令,避免用户配置漂移时每次按键都写日志。</summary>
    private static readonly HashSet<string> WarnedMissing = new(StringComparer.Ordinal);

    /// <summary>
    /// 取命令的图标字形(只负责显示)。
    /// 表里没有的命令不显示按钮并记录一次日志;命令是否可达由共享
    /// <c>InputCommandMap.IsCommandKnown</c> 判定,图标不参与输入闸门。
    /// 不能让用户可编辑的快捷键 JSON 把进程打死。
    /// </summary>
    /// <param name="command">命令标识</param>
    /// <returns>字形;null 表示本阶段不显示该命令</returns>
    public static string? ForCommand(string command)
    {
        if (Glyphs.TryGetValue(command, out var glyph))
        {
            return glyph;
        }

        if (WarnedMissing.Add(command))
        {
            NativeStartupLog.Write($"图标映射缺少命令「{command}」,该命令不显示按钮;请在 WindowsIcons 中登记。");
        }

        return null;
    }

    /// <summary>
    /// 取评分快捷命令的底图字形:0 为清零叉,1–5 为实心星;越界星数按实心星兜底。
    /// </summary>
    /// <param name="stars">星数(0–5)</param>
    /// <returns>Segoe Fluent Icons 字形</returns>
    public static string ForRatingGlyph(int stars)
        => RatingGlyphs.TryGetValue(stars, out var glyph) ? glyph : StarGlyph;

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
