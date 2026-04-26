using System;
using System.Collections.Generic;

namespace PhotoViewer.Core;

/// <summary>
/// ExifTool 补充 tag 名称查询，用于给 MetadataExtractor 无法识别的 tag 补充名称。
/// 数据由 Tools/generate-exiftool-tags.py 从 ExifTool Perl 源码自动生成，
/// 存储于 ExifToolTags.Generated.cs（partial class 另一半）。
/// 更新方法：python3 Tools/generate-exiftool-tags.py
/// </summary>
internal static partial class ExifToolTags
{
    /// <summary>
    /// 手动维护的 tag 名称覆盖表，优先级高于自动生成的 _tables。
    /// 用于修正生成表中的错误条目，或补充生成表中缺失的私有 tag。
    /// 脚本只更新 ExifToolTags.Generated.cs，本文件永远不会被脚本修改。
    /// 键结构：模块名（与 _keywordMap 保持一致）→ tagId → 英文名
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> _overrideTables =
        new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            ["Sony"] = new Dictionary<int, string>
            {
                // ===== Exif IFD0 / Exif Image 中的 Sony 私有 tag =====
                // 生成表误标为 MoreInfo0201，实际含义是预览图像起始偏移
                [0x0201] = "PreviewImageStart",
                // 生成表缺失：预览图像字节长度
                [0x0202] = "PreviewImageLength",

                // ===== SubIFD（RAW 图像区）中的 Sony ARW 专有 tag =====
                // Sony ARW 压缩模式（值 4 = Sony Lossless Compressed RAW 2）
                [0x7000] = "SonyRawFileType",
                // 色调曲线数据
                [0x7001] = "SonyToneCurve",
                // 暗角校正（当前 ILCE 格式，单值编码档位）
                [0x7031] = "VignettingCorrection",
                // 暗角校正参数（17 值，由 HTML 参考值匹配确认）
                [0x7032] = "VignettingCorrParams",
                // 色差校正（1 = Auto）
                [0x7034] = "ChromaticAberrationCorrection",
                // 色差校正参数（33 值，由 HTML 参考值匹配确认）
                [0x7035] = "ChromaticAberrationCorrParams",
                // 畸变校正（0 = Off，由 HTML 参考值匹配确认）
                [0x7036] = "DistortionCorrection",
                // 畸变校正参数（17 值，由 HTML 参考值匹配确认）
                [0x7037] = "DistortionCorrParams",
                // 生成表缺失，Sony ARW 格式的 RAW 图像尺寸（由 HTML 值 7032 4688 匹配确认）
                [0x7038] = "SonyRawImageSize",
                // 修正：生成表错误标为 WB_RGGBLevelsAuto；实际值 512×4 = 黑电平（由 HTML 匹配确认）
                [0x7310] = "BlackLevel",
                // 修正：生成表错误标为 WB_RGBLevelsDaylight；实际值 2654 1024 1024 1576 = 白平衡（由 HTML 匹配确认）
                [0x7313] = "WB_RGGBLevels",
                // Sony 特有裁剪起始坐标（值 12 8，由 HTML SonyCropTopLeft 匹配确认）
                [0x74c7] = "SonyCropTopLeft",
                // Sony 特有裁剪尺寸（值 7008 4672，由 HTML SonyCropSize 匹配确认）
                [0x74c8] = "SonyCropSize",

                // ===== SubIFD 中的 DNG 标准 tag（MetadataExtractor 不识别，借 Sony 路径解析）=====
                // 白电平（DNG 标准，值 16383 = 14-bit 精度，由 HTML WhiteLevel 匹配确认）
                [0xc61d] = "WhiteLevel",
                // 默认裁剪起点（DNG 标准，值 12 8，由 HTML DefaultCropOrigin 匹配确认）
                [0xc61f] = "DefaultCropOrigin",
                // 默认裁剪尺寸（DNG 标准，值 7008 4672，由 HTML DefaultCropSize 匹配确认）
                [0xc620] = "DefaultCropSize",

                // ===== Sony Makernote 中生成表缺失的 tag =====
                [0xB020] = "Creative Style",
                // 已用对焦点集合（10 位标志值，全零 = 未使用，由 HTML AFPointsUsed 匹配确认）
                [0x2020] = "AFPointsUsed",
                // JPEG/HEIF 切换模式（由 HTML JPEG-HEIFSwitch 匹配确认）
                [0x2038] = "JPEGHEIFSwitch",
                // 对焦位置（0–255 编码，值 255，由 HTML FocusPosition2 匹配确认）
                [0x203e] = "FocusPosition2",
                // 环境温度（有理数编码，由 HTML AmbientTemperature 匹配确认）
                [0x2049] = "AmbientTemperature",

                // ===== Exif SubIFD 中扩展 EXIF 2.32 tag（MetadataExtractor 不识别）=====
                // 合成图像标志（EXIF 2.32 新增，0 = Not a Composite Image）
                [0xa460] = "CompositeImage",
            },
        };
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
    /// 根据 MetadataExtractor 目录名和 tag 数值 ID 查询 ExifTool 补充名称。
    /// 先按目录名关键词匹配厂商模块；若无匹配，再用 cameraMake 作为 fallback 尝试匹配。
    /// </summary>
    /// <param name="directoryName">MetadataExtractor 目录名，如 "Sony Makernote"、"Exif IFD0"</param>
    /// <param name="tagId">tag 数值 ID</param>
    /// <param name="cameraMake">相机品牌（如 "SONY"），用于通用 EXIF 目录的 fallback 匹配</param>
    /// <returns>补充名称；若无匹配则返回 null</returns>
    public static string? GetSupplementalName(string directoryName, int tagId, string? cameraMake = null)
    {
        // 第一优先级：按目录名关键词精确匹配
        foreach (var (keyword, module) in _keywordMap)
        {
            if (directoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                // 先查手动覆盖表，再查自动生成表
                if (_overrideTables.TryGetValue(module, out var overrides) &&
                    overrides.TryGetValue(tagId, out var overrideName))
                    return overrideName;
                if (_tables.TryGetValue(module, out var table) &&
                    table.TryGetValue(tagId, out var name))
                    return name;
                return null;
            }
        }

        // 第二优先级：目录名无厂商关键词（如 "Exif IFD0"、"Exif SubIFD"）时，
        // 用相机品牌 fallback 匹配——厂商有时会把私有 tag 写入标准 EXIF 目录。
        if (cameraMake != null)
        {
            foreach (var (keyword, module) in _keywordMap)
            {
                if (cameraMake.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    // 先查手动覆盖表，再查自动生成表
                    if (_overrideTables.TryGetValue(module, out var overrides) &&
                        overrides.TryGetValue(tagId, out var overrideName))
                        return overrideName;
                    if (_tables.TryGetValue(module, out var table) &&
                        table.TryGetValue(tagId, out var name))
                        return name;
                    return null;
                }
            }
        }

        return null;
    }
}
