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
    /// MetadataExtractor 目录名关键词 → 本类 _tables 中的模块键（按优先顺序匹配）
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
    /// 根据 MetadataExtractor 目录名和 tag 数值 ID 查询 ExifTool 补充名称。
    /// 利用目录名的字符串包含关系匹配厂商模块，再按 tag ID 精确查找。
    /// </summary>
    /// <param name="directoryName">MetadataExtractor 目录名，如 "Sony Makernote"</param>
    /// <param name="tagId">tag 数值 ID</param>
    /// <returns>补充名称；若无匹配则返回 null</returns>
    public static string? GetSupplementalName(string directoryName, int tagId)
    {
        foreach (var (keyword, module) in _keywordMap)
        {
            if (directoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                if (_tables.TryGetValue(module, out var table) &&
                    table.TryGetValue(tagId, out var name))
                {
                    return name;
                }
                return null;
            }
        }
        return null;
    }
}
