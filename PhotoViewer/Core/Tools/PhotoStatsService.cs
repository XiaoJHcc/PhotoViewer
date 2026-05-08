using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;

namespace PhotoViewer.Core.Tools;

/// <summary>照片统计结果行。</summary>
public record PhotoStatsRow(string FilePath, string FileName, int? EquivFocalLength, int Rating);

/// <summary>
/// 照片数据统计服务：批量遍历文件夹、读取 EXIF 关键字段并写出 CSV。
/// 依赖 System.IO 直接访问文件系统，仅适用于桌面端（Windows）。
/// </summary>
public static class PhotoStatsService
{
    /// <summary>
    /// 按通配符模式递归枚举多个文件夹中的文件，结果不重复。
    /// </summary>
    /// <param name="folderPaths">要扫描的文件夹路径列表。</param>
    /// <param name="patternsCsv">逗号或分号分隔的通配符列表，如 "*.HIF,*.JPG"。</param>
    /// <returns>不重复的文件绝对路径序列。</returns>
    public static IEnumerable<string> EnumerateFiles(IEnumerable<string> folderPaths, string patternsCsv)
    {
        var patterns = patternsCsv.Split(new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0)
            patterns = new[] { "*.*" };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folderPaths)
        {
            if (!System.IO.Directory.Exists(folder)) continue;
            foreach (var pattern in patterns)
            {
                foreach (var filePath in System.IO.Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                {
                    if (seen.Add(filePath))
                        yield return filePath;
                }
            }
        }
    }

    /// <summary>
    /// 从单个文件同步读取等效焦距与星级评分。
    /// 读取失败时字段留空而非抛出异常。
    /// </summary>
    public static PhotoStatsRow ReadStats(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var directories = ImageMetadataReader.ReadMetadata(stream);
            var equivFl = ReadEquivFocalLength(directories);
            var rating = ReadRating(directories);
            return new PhotoStatsRow(filePath, Path.GetFileName(filePath), equivFl, rating);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhotoStats] 读取失败 {filePath}: {ex.Message}");
            return new PhotoStatsRow(filePath, Path.GetFileName(filePath), null, 0);
        }
    }

    /// <summary>将统计行列表写出为带 BOM 的 UTF-8 CSV 文件（Excel 可直接打开）。</summary>
    /// <param name="rows">统计结果列表。</param>
    /// <param name="outputPath">输出 CSV 文件的完整路径。</param>
    public static void WriteCsv(IEnumerable<PhotoStatsRow> rows, string outputPath)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("文件名,等效焦距(mm),星级");
        foreach (var row in rows)
        {
            var focal = row.EquivFocalLength.HasValue ? row.EquivFocalLength.Value.ToString() : "";
            writer.WriteLine($"{EscapeCsv(row.FileName)},{focal},{row.Rating}");
        }
    }

    /// <summary>从元数据目录中读取 35mm 等效焦距（毫米整数）。</summary>
    private static int? ReadEquivFocalLength(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
        if (subIfds.Count == 0) return null;

        // 与 ExifLoader 相同的策略：优先选包含拍摄参数的 SubIFD
        var subIfd = subIfds.Count == 1 ? subIfds[0]
            : subIfds.FirstOrDefault(d =>
                d.ContainsTag(ExifDirectoryBase.TagExposureTime) ||
                d.ContainsTag(ExifDirectoryBase.TagFNumber) ||
                d.ContainsTag(ExifDirectoryBase.TagIsoEquivalent))
              ?? subIfds.OrderByDescending(d => d.Tags.Count).First();

        if (subIfd.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var fl))
            return fl;
        if (subIfd.TryGetRational(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var flr) && flr.Denominator != 0)
            return (int)Math.Round((double)flr.Numerator / flr.Denominator);

        return null;
    }

    /// <summary>从元数据目录中读取 XMP 星级评分（0–5）。</summary>
    private static int ReadRating(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var xmpDir = directories.OfType<XmpDirectory>().FirstOrDefault();
        if (xmpDir == null) return 0;

        var props = xmpDir.GetXmpProperties();
        foreach (var key in new[] { "xmp:Rating", "xap:Rating", "Rating" })
        {
            if (props.TryGetValue(key, out var val) && int.TryParse(val, out var r) && r >= 0 && r <= 5)
                return r;
        }
        return 0;
    }

    /// <summary>对包含逗号、引号或换行的 CSV 字段进行转义。</summary>
    private static string EscapeCsv(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return '"' + field.Replace("\"", "\"\"") + '"';
        return field;
    }
}
