// PhotoViewer/Core/ExifChinese.cs
// EXIF 字段中文名称 —— 手动维护的覆盖表（最高优先级）
//
// 用法：
//   在 _overrideNames 中添加或修改条目即可覆盖自动生成的译名，永久生效
//   脚本只会重新生成 ExifChinese.Generated.cs，本文件永远不会被脚本修改
//
// 键名说明：
//   标准 EXIF 段 使用 MetadataExtractor 风格，如 "F-Number"、"Focal Length"
//   厂商 Makernote 段 使用 ExifTool 风格，如 "DriveMode"、"CreativeStyle"

using System;
using System.Collections.Generic;

namespace PhotoViewer.Core;

/// <summary>
/// EXIF 字段中文名称查询（手动覆盖表 + 自动生成表）。
/// 优先级：_overrideNames（本文件）> _generatedNames（ExifChinese.Generated.cs）
/// 生成/更新自动部分：python3 Tools/generate-chinese-template.py
/// </summary>
internal static partial class ExifChinese
{
    /// <summary>
    /// 手动覆盖的中文名称，优先级高于自动生成表。
    /// 在此处添加或修改条目后永久生效，不受脚本重新生成影响。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> _overrideNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 覆盖自动生成表中错误的译名，或补充缺失的中文名
            // 键名使用最终解析后的英文名（ExifToolTags 覆盖后的名称）

            // ===== Exif IFD0 / Exif Image =====
            ["New Subfile Type"]              = "子文件类型",
            ["PreviewImageStart"]             = "预览图像起始",
            ["PreviewImageLength"]            = "预览图像长度",

            // ===== SubIFD RAW 图像区 =====
            ["SonyRawFileType"]               = "RAW 文件类型",
            ["SonyToneCurve"]                 = "色调曲线",
            ["SonyRawImageSize"]              = "RAW 图像尺寸",
            ["VignettingCorrection"]          = "暗角校正",
            ["VignettingCorrParams"]          = "暗角校正参数",
            ["ChromaticAberrationCorrection"] = "色差校正",
            ["ChromaticAberrationCorrParams"] = "色差校正参数",
            ["DistortionCorrection"]          = "畸变校正",
            ["DistortionCorrParams"]          = "畸变校正参数",
            ["BlackLevel"]                    = "黑电平",
            ["WB_RGGBLevels"]                 = "白平衡 RGGB 值",
            ["SonyCropTopLeft"]               = "裁剪起始点",
            ["SonyCropSize"]                  = "裁剪尺寸",
            ["WhiteLevel"]                    = "白电平",
            ["DefaultCropOrigin"]             = "默认裁剪起点",
            ["DefaultCropSize"]               = "默认裁剪尺寸",

            // ===== Sony Makernote =====
            ["AFPointsUsed"]                  = "已用对焦点",
            ["JPEGHEIFSwitch"]                = "JPEG/HEIF 切换",
            ["FocusPosition2"]                = "对焦位置",
            ["AmbientTemperature"]            = "环境温度",

            // ===== 扩展 EXIF 标准字段 =====
            ["CompositeImage"]                = "合成图像",
        };

    /// <summary>
    /// 按英文标签名查询中文显示名称。
    /// 先查手动覆盖表，再查自动生成表；均无则返回 null。
    /// </summary>
    public static string? GetChineseName(string tagName)
    {
        if (_overrideNames.TryGetValue(tagName, out var cn))
            return cn;
        if (_generatedNames.TryGetValue(tagName, out cn))
            return cn;
        return null;
    }
}
