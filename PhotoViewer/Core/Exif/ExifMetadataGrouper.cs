using System.Collections.Generic;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Xmp;

namespace PhotoViewer.Core;

/// <summary>
/// 将 MetadataExtractor 解析得到的全部目录展开为按目录分组的可读 tag 列表，
/// 期间根据 ExifChinese / ExifToolTags / ExifToolValues 进行翻译，并对 Sony MakerNote 做特殊处理。
/// </summary>
internal static class ExifMetadataGrouper
{
    /// <summary>
    /// 提取所有目录中的全部标签，按目录名分组。
    /// </summary>
    public static List<MetadataGroup> ExtractAll(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var groups = new List<MetadataGroup>();

        // 从 IFD0 中提取相机品牌和型号，供后续匹配厂商 tag 名称时作为 fallback
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var cameraMake = ifd0?.GetDescription(ExifDirectoryBase.TagMake);
        var cameraModel = ifd0?.GetDescription(ExifDirectoryBase.TagModel);

        foreach (var directory in directories)
        {
            // XMP 目录特殊处理：展开 XMP 属性而非原始 tag
            if (directory is XmpDirectory xmpDir)
            {
                if (TryExpandXmp(xmpDir, groups))
                    continue;
                // XMP 解析失败时回退到原始 tag 显示
                groups.Add(ExtractDirectoryGroup(directory, cameraMake));
                continue;
            }

            groups.Add(ExtractDirectoryGroup(directory, cameraMake));

            // Sony 加密 MakerNote tag 解码：将 [N values] 二进制块替换为解码后的可读字段
            if (directory is SonyType1MakernoteDirectory sonyDir)
            {
                SonyMakernoteParser.DecodeCipherTagsInto(sonyDir, groups[^1], cameraModel);
            }
        }

        return groups;
    }

    /// <summary>
    /// 按 XMP 命名空间前缀展开 XMP 属性（如 xmp:Rating → "XMP-xmp"，xmpMM:DocumentID → "XMP-xmpMM"）。
    /// 解析失败时返回 false 让调用方走原始 tag 回退路径。
    /// </summary>
    private static bool TryExpandXmp(XmpDirectory xmpDir, List<MetadataGroup> groups)
    {
        try
        {
            var xmpProps = xmpDir.GetXmpProperties();
            if (xmpProps.Count == 0)
                return false;

            var nsByPrefix = new Dictionary<string, List<MetadataTag>>();
            foreach (var kv in xmpProps)
            {
                var prefix = "XMP";
                var colonIdx = kv.Key.IndexOf(':');
                if (colonIdx > 0)
                    prefix = "XMP-" + kv.Key[..colonIdx];

                if (!nsByPrefix.TryGetValue(prefix, out var tagList))
                {
                    tagList = new List<MetadataTag>();
                    nsByPrefix[prefix] = tagList;
                }
                tagList.Add(new MetadataTag
                {
                    Name = kv.Key,
                    Value = kv.Value ?? ""
                });
            }
            foreach (var ns in nsByPrefix.OrderBy(n => n.Key))
            {
                groups.Add(new MetadataGroup { Name = ns.Key, Tags = ns.Value });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将单个 MetadataExtractor 目录转换为 MetadataGroup。
    /// </summary>
    /// <param name="directory">MetadataExtractor 目录</param>
    /// <param name="cameraMake">相机品牌（用于在通用 EXIF 目录中 fallback 查找厂商 tag 名称）</param>
    private static MetadataGroup ExtractDirectoryGroup(MetadataExtractor.Directory directory, string? cameraMake)
    {
        var group = new MetadataGroup { Name = directory.Name };
        foreach (var tag in directory.Tags)
        {
            string desc;
            try { desc = tag.Description ?? ""; } catch { desc = ""; }

            // 跳过超长的二进制数据值
            if (desc.Length > 200)
                desc = desc[..200] + "...";

            // 对 MetadataExtractor 未能识别的 tag，尝试从 ExifTool 数据库补充名称
            var tagName = tag.Name;
            if (tagName.StartsWith("Unknown tag", System.StringComparison.Ordinal))
            {
                var supplemental = ExifToolTags.GetSupplementalName(directory.Name, tag.Type, cameraMake);
                if (supplemental != null)
                    tagName = supplemental;
            }

            // 尝试将原始数值翻译为可读名称（如 "4" → "Sony Lossless Compressed RAW 2"）
            var translated = ExifToolValues.TranslateValue(directory.Name, tag.Type, desc, cameraMake);
            if (translated != null)
                desc = translated;

            // Sony MakerNote 特殊字段格式化
            if (directory is SonyType1MakernoteDirectory)
                desc = SonyMakernoteParser.FormatMakernoteValue(tag.Type, desc, directory) ?? desc;

            var chineseValue = ExifToolValues.GetChineseValue(desc);
            group.Tags.Add(new MetadataTag
            {
                TagId = tag.Type,
                Name = tagName,
                ChineseName = ExifChinese.GetChineseName(tagName),
                Value = chineseValue ?? desc
            });
        }
        return group;
    }
}
