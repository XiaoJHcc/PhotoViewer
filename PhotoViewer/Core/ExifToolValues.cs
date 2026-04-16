using System;
using System.Collections.Generic;

namespace PhotoViewer.Core;

/// <summary>
/// ExifTool 补充值映射查询，用于将 MetadataExtractor 无法解码的数值 tag 值
/// 翻译为人类可读的字符串。
/// 数据由 Tools/generate-exiftool-values.py 从 ExifTool Perl 源码 PrintConv 自动生成，
/// 存储于 ExifToolValues.Generated.cs（partial class 另一半）。
/// 更新方法：python3 Tools/generate-exiftool-values.py
/// </summary>
internal static partial class ExifToolValues
{
    /// <summary>
    /// 厂商模块的关键词映射（与 ExifToolTags 共用逻辑）。
    /// 用于将 MetadataExtractor 目录名映射到模块名。
    /// </summary>
    private static readonly (string Keyword, string Module)[] _keywordMap =
    [
        ("Sony",      "Sony"),
        ("Nikon",     "Nikon"),
        ("Canon",     "Canon"),
        ("Fujifilm",  "Fujifilm"),
        ("Panasonic", "Panasonic"),
        ("Olympus",   "Olympus"),
        ("Pentax",    "Pentax"),
        ("Sigma",     "Sigma"),
        ("Minolta",   "Minolta"),
        ("Samsung",   "Samsung"),
        ("Apple",     "Apple"),
        ("Reconyx",   "Reconyx"),
        ("Ricoh",     "Ricoh"),
        ("Casio",     "Casio"),
        ("Kodak",     "Kodak"),
        ("DJI",       "DJI"),
        ("Kyocera",   "Kyocera"),
        ("Sanyo",     "Sanyo"),
        ("FLIR",      "FLIR"),
    ];

    /// <summary>
    /// 将 tag 的原始显示值翻译为可读名称。
    /// 查找逻辑：先按目录名关键词匹配厂商模块，再按 Exif 通用表查找，
    /// 最后用 cameraMake 作为 fallback。
    /// </summary>
    /// <param name="directoryName">MetadataExtractor 目录名</param>
    /// <param name="tagId">tag 数值 ID</param>
    /// <param name="rawValue">原始值字符串（如 "4"）</param>
    /// <param name="cameraMake">相机品牌</param>
    /// <returns>可读名称；无匹配则返回 null</returns>
    public static string? TranslateValue(string directoryName, int tagId, string rawValue, string? cameraMake = null)
    {
        if (string.IsNullOrEmpty(rawValue))
            return null;

        // 第一优先级：按目录名关键词匹配厂商模块
        foreach (var (keyword, module) in _keywordMap)
        {
            if (directoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var result = LookupInModule(module, tagId, rawValue);
                if (result != null)
                    return result;
                break;
            }
        }

        // 第二优先级：标准 EXIF 表（Exif.pm 中的通用 tag，如 SubIFD 中的 Sony 私有 tag）
        {
            var result = LookupInModule("Exif", tagId, rawValue);
            if (result != null)
                return result;
        }

        // 第三优先级：cameraMake fallback
        if (cameraMake != null)
        {
            foreach (var (keyword, module) in _keywordMap)
            {
                if (cameraMake.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    var result = LookupInModule(module, tagId, rawValue);
                    if (result != null)
                        return result;
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 在指定模块中查找 tag 值映射
    /// </summary>
    private static string? LookupInModule(string module, int tagId, string rawValue)
    {
        if (_tables.TryGetValue(module, out var moduleTags) &&
            moduleTags.TryGetValue(tagId, out var entry) &&
            entry.Values.TryGetValue(rawValue, out var displayString))
        {
            return displayString;
        }
        return null;
    }

    /// <summary>
    /// 常见 ExifTool PrintConv 值的中文翻译。
    /// 用于在 EXIF 详情视图中为已解码的英文值提供中文对照。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> _chineseValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ===== 通用开关/状态 =====
            ["Off"]       = "关",
            ["On"]        = "开",
            ["Yes"]       = "是",
            ["No"]        = "否",
            ["Auto"]      = "自动",
            ["Manual"]    = "手动",
            ["Normal"]    = "正常",
            ["Standard"]  = "标准",
            ["None"]      = "无",
            ["Unknown"]   = "未知",
            // ["n/a"]       = "不适用",
            ["Fired"]     = "已闪光",
            ["Did not fire"] = "未闪光",
            ["Flash did not fire"] = "未闪光",
            ["Auto exposure"] = "自动曝光",
            ["Auto white balance"] = "自动白平衡",

            // ===== 画质/尺寸 =====
            ["Fine"]       = "精细",
            ["Extra Fine"] = "超精细",
            ["Low"]        = "低",
            ["High"]       = "高",
            ["Medium"]     = "中",
            ["Large"]      = "大",
            ["Small"]      = "小",
            ["Weak"]       = "弱",
            ["Strong"]     = "强",

            // ===== 驱动/快门模式 =====
            ["Single Frame"]     = "单张",
            ["Continuous"]       = "连拍",
            ["Continuous High"]  = "高速连拍",
            ["Continuous Low"]   = "低速连拍",
            ["Speed Priority Continuous"] = "速度优先连拍",
            ["Self-timer 2 sec"] = "自拍定时 2 秒",
            ["Self-timer 10 sec"] = "自拍定时 10 秒",
            ["Remote Commander"] = "遥控",
            ["Mirror Lock-up"]   = "反光镜锁定",
            ["Bracketing"]       = "包围曝光",
            ["Single"]           = "单张",

            // ===== 对焦 =====
            ["AF-S"]  = "AF-S (单次)",
            ["AF-C"]  = "AF-C (连续)",
            ["AF-A"]  = "AF-A (自动)",
            ["DMF"]   = "DMF (手动优先)",
            ["MF"]    = "MF (手动)",

            // ===== 曝光程序 =====
            ["Not Defined"]              = "未定义",
            ["Program AE"]               = "P 程序自动",
            ["Aperture-priority AE"]     = "A 光圈优先",
            ["Aperture Priority"]        = "A 光圈优先",
            ["Shutter speed priority AE"] = "S 快门优先",
            ["Creative (Slow speed)"]    = "创意（慢速）",
            ["Action (High speed)"]      = "运动（高速）",
            ["Bulb"]                     = "B 门",

            // ===== 测光模式 =====
            ["Multi-segment"]              = "多重测光",
            ["Center-weighted average"]    = "中央重点平均",
            ["Spot"]                       = "点测光",
            ["Spot (Standard)"]            = "点测光（标准）",
            ["Spot (Large)"]               = "点测光（大）",
            ["Average"]                    = "平均测光",
            ["Highlight"]                  = "高光测光",

            // ===== 白平衡 =====
            ["Daylight"]    = "日光",
            ["Cloudy"]      = "阴天",
            ["Shade"]       = "阴影",
            ["Tungsten"]    = "白炽灯",
            ["Fluorescent"] = "荧光灯",
            ["Flash"]       = "闪光灯",
            ["Color Temperature"] = "色温",
            ["Color Filter"]      = "色彩滤镜",
            ["Custom"]     = "自定义",
            ["Custom 1"]   = "自定义1",
            ["Custom 2"]   = "自定义2",
            ["Custom 3"]   = "自定义3",
            ["Underwater"]  = "水下",
            ["Color Temperature/Color Filter"] = "色温/色彩滤镜",
            ["Incandescent"]        = "白炽灯",
            ["Cool White Fluorescent"] = "冷白荧光灯",
            ["Warm White Fluorescent"] = "暖白荧光灯",
            ["Day White Fluorescent"]  = "日光白荧光灯",
            ["Daylight Fluorescent"]   = "日光荧光灯",

            // ===== 创意风格/色彩 =====
            ["Vivid"]      = "鲜明",
            ["Portrait"]   = "人像",
            ["Landscape"]  = "风景",
            ["Sunset"]     = "日落",
            ["B&W"]        = "黑白",
            ["Monochrome"] = "单色",
            ["Sepia"]      = "棕褐色",
            ["Neutral"]    = "中性",
            ["Clear"]      = "清澈",
            ["Deep"]       = "深色",
            ["Light"]      = "明亮",
            ["Autumn Leaves"] = "秋叶",
            ["Real"]       = "真实",
            ["Night View/Portrait"] = "夜景/人像",
            ["Night Scene"] = "夜景",

            // ===== 色彩空间 =====
            // ["sRGB"]       = "sRGB",
            // ["Adobe RGB"]  = "Adobe RGB",

            // ===== 图像稳定/降噪 =====
            ["On (Auto)"]          = "开（自动）",
            ["On (Manual)"]        = "开（手动）",
            ["On (unused)"]        = "开（未使用）",
            ["On (dark subtracted)"] = "开（暗部扣除）",
            ["On (Continuous)"]    = "开（连续）",
            ["On (Shooting)"]      = "开（拍摄时）",

            // ===== 图像方向 =====
            ["Horizontal (normal)"] = "水平（正常）",
            ["Rotate 90 CW"]  = "顺时针 90°",
            ["Rotate 180"]    = "旋转 180°",
            ["Rotate 270 CW"] = "逆时针 90°",

            // ===== 对焦区域 =====
            ["Wide"]    = "广域",
            ["Center"]  = "中心",
            ["Flexible Spot"] = "自由点",
            ["Tracking"] = "追踪",
            ["Face Tracking"] = "人脸追踪",
            ["Touch"]    = "触摸",
            ["Multi"]    = "多点",
            ["Local"]    = "局部",
            ["Top"]      = "上",
            ["Bottom"]   = "下",
            ["Left"]     = "左",
            ["Right"]    = "右",
            ["Upper-right"] = "右上",
            ["Upper-left"]  = "左上",
            ["Lower-right"] = "右下",
            ["Lower-left"]  = "左下",
            ["Far Right"]   = "远右",
            ["Far Left"]    = "远左",

            // ===== 画质/文件格式 =====
            // ["RAW"]           = "RAW",
            ["RAW + JPEG"]    = "RAW + JPEG/HEIF",
            // ["JPEG"]          = "JPEG",
            // ["HEIF"]          = "HEIF",
            ["Compressed RAW"] = "压缩 RAW",
            ["Uncompressed RAW"] = "未压缩 RAW",
            ["Lossless Compressed RAW"] = "无损压缩 RAW",

            // ===== Sony RAW 类型 =====
            ["Sony Uncompressed 14-bit RAW"]  = "SONY 未压缩 RAW (14位)",
            ["Sony Uncompressed 12-bit RAW"]  = "SONY 未压缩 RAW (12位)",
            ["Sony Compressed RAW"]           = "SONY 压缩 RAW",
            ["Sony Lossless Compressed RAW"]  = "SONY 无损压缩 RAW",
            ["Sony Lossless Compressed RAW 2"] = "SONY 无损压缩 RAW 2",
            ["Sony Compressed RAW 2"]         = "SONY 压缩 RAW 2",

            // ===== 宽高比 =====
            // ["3:2"]      = "3:2",
            // ["16:9"]     = "16:9",
            // ["4:3"]      = "4:3",
            // ["1:1"]      = "1:1",
            ["Panorama"] = "全景",

            // ===== 闪光灯 =====
            ["Flash Fired"]           = "闪光灯已触发",
            ["External Flash Fired"]  = "外置闪光灯已触发",
            ["Wireless Controlled Flash Fired"] = "无线控制闪光灯已触发",
            ["Did not fire"]          = "未触发",
            ["Flash Inhibited"]       = "闪光灯已禁用",
            ["Built-in Flash present"] = "内置闪光灯就绪",
            ["Built-in Flash Fired"]  = "内置闪光灯已触发",
            ["External Flash present"] = "外置闪光灯就绪",
            ["No Flash present"]      = "无闪光灯",
            ["Fired, Fill-flash"]     = "已触发，补光闪光",
            ["Fired, Rear Sync"]      = "已触发，后帘同步",
            ["Fired, Wireless"]       = "已触发，无线",
            ["Fired, Slow Sync"]      = "已触发，慢速同步",
            ["Fired, Autoflash"]      = "已触发，自动闪光",
            ["ADI"]                   = "ADI 闪光",
            ["Pre-flash TTL"]         = "TTL 预闪",
            ["ADI Flash"]             = "ADI 闪光",

            // ===== 动态范围 =====
            ["Advanced Auto"]   = "高级自动",
            ["Advanced Level"]  = "高级等级",

            // ===== 镜头卡口 =====
            ["A-mount"]  = "A 卡口",
            ["E-mount"]  = "E 卡口",
            ["A-mount (1)"] = "A 卡口",
            ["APS-C"]    = "APS-C",
            ["Full-frame"] = "全画幅",

            // ===== 图片效果 =====
            ["Toy Camera"]       = "玩具相机",
            ["Pop Color"]        = "流行色彩",
            ["Posterization"]    = "色调分离",
            ["Posterization B/W"] = "色调分离（黑白）",
            ["Retro Photo"]      = "怀旧照片",
            ["Soft High Key"]    = "柔和高调",
            ["Partial Color"]    = "局部色彩",
            ["High Contrast Monochrome"] = "高对比度单色",
            ["Soft Focus"]       = "柔焦",
            ["HDR Painting"]     = "HDR 绘画",
            ["Rich-tone Monochrome"] = "丰富色调单色",
            ["Miniature"]        = "微缩景观",
            ["Water Color"]      = "水彩画",
            ["Illustration"]     = "插画",

            // ===== 美肤 =====
            ["Mid"]  = "中",

            // ===== 杂项 =====
            ["Macro"]    = "微距",
            ["Not a Composite Image"] = "非合成图像",
            ["ViewFinder"]           = "取景器",
            ["Phase-detect AF"]      = "相位检测 AF",
            ["Contrast AF"]          = "对比度 AF",
            ["Slight Smile"]         = "微笑",
            ["Normal Smile"]         = "正常笑容",
            ["Big Smile"]            = "大笑",
            ["Ambient"]              = "环境光",
            ["White"]                = "白色",
            ["Mechanical"]           = "机械快门",
            ["Silent / Electronic"]  = "静音/电子快门",
            ["Electronic"]           = "电子快门",
            ["Not Confirmed"]        = "未确认",
            ["Confirmed"]            = "已确认",
            ["Failed"]               = "失败",
            ["Clean"]                = "清洁",
        };

    /// <summary>
    /// 查询值的中文翻译。用于 EXIF 详情视图的中英对照显示。
    /// </summary>
    public static string? GetChineseValue(string englishValue)
    {
        if (_chineseValues.TryGetValue(englishValue, out var cn))
            return cn;
        return null;
    }
}
