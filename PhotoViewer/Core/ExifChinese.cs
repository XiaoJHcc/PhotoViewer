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
            ["YCbCr Coefficients"]            = "YCbCr 系数",
            ["YCbCr Sub-Sampling"]            = "YCbCr 子采样",
            ["Reference Black/White"]         = "参考黑白点",

            // ===== SubIFD RAW 图像区 =====
            ["CFA Repeat Pattern Dim"]        = "色彩滤波重复维度",
            ["CFA Pattern"]                   = "色彩滤波阵列",
            ["SonyRawFileType"]               = "SONY RAW 文件类型",
            ["SonyToneCurve"]                 = "SONY 色调曲线",
            ["SonyRawImageSize"]              = "SONY RAW 图像尺寸",
            ["VignettingCorrection"]          = "暗角校正",
            ["VignettingCorrParams"]          = "暗角校正参数",
            ["ChromaticAberrationCorrection"] = "色差校正",
            ["ChromaticAberrationCorrParams"] = "色差校正参数",
            ["DistortionCorrection"]          = "畸变校正",
            ["DistortionCorrParams"]          = "畸变校正参数",
            ["BlackLevel"]                    = "黑电平",
            ["WB_RGGBLevels"]                 = "白平衡 RGGB 值",
            ["SonyCropTopLeft"]               = "SONY 裁剪起始点",
            ["SonyCropSize"]                  = "SONY 裁剪尺寸",
            ["WhiteLevel"]                    = "白电平",
            ["DefaultCropOrigin"]             = "默认裁剪起点",
            ["DefaultCropSize"]               = "默认裁剪尺寸",
            ["Time Zone"]                     = "时区",
            ["Time Zone Original"]            = "时区（原始）",
            ["Time Zone Digitized"]           = "时区（数字化）",
            ["Focal Length 35"]               = "35mm 等效焦距",

            // ===== Sony Makernote =====
            ["Long Exposure Noise Reduction"] = "长时间曝光降噪",
            ["High ISO Noise Reduction"]      = "高 ISO 降噪",
            ["Multi Frame Noise Reduction"]   = "多帧降噪",
            ["Creative Style"]                = "创意风格",
            ["Scene Mode"]                    = "场景模式",
            ["Dynamic Range Optimizer"]       = "动态范围优化",
            ["Image Stabilisation"]           = "图像稳定",
            ["Color Mode"]                    = "色彩模式",
            ["WB Shift Amber/Magenta"]        = "白平衡偏移（色温/色调）",
            ["WBShiftAB_GM_Precise"]          = "白平衡偏移（AB/GM 精确）",
            ["Color Mode Setting"]            = "色彩模式设置",
            ["Color Temperature"]             = "色温",
            ["Color Compensation Filter"]     = "色彩补偿滤镜",
            ["Full Image Size"]               = "全图尺寸",
            ["Preview Image Size"]            = "预览图像尺寸",
            ["File Format"]                   = "文件格式",
            ["Image Quality"]                 = "图像质量",
            ["Flash Exposure Compensation"]   = "闪光曝光补偿",
            ["White Balance Fine Tune Value"] = "白平衡微调值",
            ["Sony Model ID"]                 = "SONY 型号 ID",
            ["Picture Effect"]                = "图片效果",
            ["Soft Skin Effect"]              = "美肤效果",
            ["Vignetting Correction"]         = "暗角校正",
            ["Lateral Chromatic Aberration"]  = "色差校正",
            ["Distortion Correction"]         = "畸变校正",
            ["Lens ID"]                       = "镜头 ID",
            ["Lens Spec"]                     = "镜头规格",
            ["Auto Portrait Framing"]         = "自动人像构图",
            ["FlashAction"]                   = "闪光动作",
            ["ElectronicFrontCurtainShutter"] = "电子前帘快门",
            ["Focus Mode"]                    = "对焦模式",
            ["AF Point Selected"]             = "对焦点选择",
            ["AFPointsUsed"]                  = "已用对焦点",
            ["AFTracking"]                    = "对焦跟踪",
            ["MultiFrameNREffect"]            = "多帧降噪效果",
            ["FocusLocation"]                 = "对焦位置",
            ["VariableLowPassFilter"]         = "可变低通滤镜",
            ["PrioritySetInAWB"]              = "自动白平衡优先设置",
            ["MeteringMode2"]                 = "测光模式",
            ["ExposureStandardAdjustment"]    = "曝光标准调整",
            ["RAWFileType"]                   = "RAW 文件类型",
            ["PixelShiftInfo"]                = "像素位移数据",
            ["Shadows"]                       = "阴影",
            ["Highlights"]                    = "高光",
            ["Fade"]                          = "褪色",
            ["SharpnessRange"]                = "锐度范围",
            ["Clarity"]                       = "清晰度",
            ["Fade"]                          = "褪色",
            ["FocusFrameSize"]                = "对焦框大小",
            ["JPEGHEIFSwitch"]                = "JPEG/HEIF 切换",
            ["FocusPosition2"]                = "对焦位置",
            ["AmbientTemperature"]            = "环境温度",
            ["HiddenInfo"]                    = "隐藏信息",
            ["Flash Level"]                   = "闪光等级",
            ["Release Mode"]                  = "快门释放模式",
            ["Sequence Number"]               = "连拍序号",
            ["Anti Blur"]                     = "抗模糊",
            ["Intelligent Auto"]              = "智能自动(iAuto)",

            ["White Balance 2"]               = "白平衡",

            // ===== 扩展 EXIF 标准字段 =====
            ["CompositeImage"]                = "合成图像",

            // ===== HEIC Primary Item Properties =====
            ["Default Rotation"]                       = "默认旋转",
            ["Pixel Depth in Bits"]                    = "像素位深",
            ["Color Data Format"]                      = "颜色数据格式",
            ["Primary Color Definitions"]              = "主要颜色定义",
            ["Optical Color Transfer Characteristic"]  = "光学色彩传输特性",
            ["Color Deviation Matrix Characteristics"] = "颜色偏差矩阵特性",
            ["Full-Range Color"]                       = "全范围颜色",
            ["HEVC Configuration Version"]             = "HEVC 配置版本",
            ["General Profile Space"]                  = "通用配置空间",
            ["General Tier Tag"]                       = "通用层级标签",
            ["General Profile"]                        = "通用配置文件",
            ["General Profile Compatibility"]          = "通用配置兼容性",
            ["General Level"]                          = "通用编码级别",
            ["Minimum Spacial Segmentation"]           = "最小空间分割",
            ["Parallelism Type"]                       = "并行类型",
            ["Chroma Format"]                          = "色度格式",
            ["Luma Bit Depth"]                         = "亮度位深",
            ["Chroma Bit Depth"]                       = "色度位深",
            ["Average Frame Rate"]                     = "平均帧率",
            ["Constant Frame Rate"]                    = "恒定帧率",
            ["Number of Temporal Layers"]              = "时间层数",
            ["Length or Size"]                         = "长度或大小",
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
