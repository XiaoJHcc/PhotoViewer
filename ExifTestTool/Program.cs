using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Xmp;
using XmpCore;

// ========================================================
// EXIF 全量提取测试工具
// 目标：从 ARW 文件中提取尽可能多的元数据
// 与 EXIF信息查看.html 中的参考结果做对比
// ========================================================

var filePath = args.Length > 0 ? args[0] : "../A7C07315.ARW";
if (!File.Exists(filePath))
{
    Console.WriteLine($"File not found: {filePath}");
    return 1;
}

Console.WriteLine($"=== EXIF 全量解析: {Path.GetFileName(filePath)} ===\n");

using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
var directories = ImageMetadataReader.ReadMetadata(stream);

// 统计
int totalDirs = directories.Count;
int totalTags = directories.Sum(d => d.Tags.Count);
Console.WriteLine($"共发现 {totalDirs} 个目录, {totalTags} 个标签\n");

// ---- Part 1: 按目录遍历所有标签（类似 HTML 中的分组展示）----
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("Part 1: 全部目录 & 标签");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

foreach (var directory in directories)
{
    Console.WriteLine($"┌─ [{directory.Name}] ({directory.Tags.Count} tags)");
    foreach (var tag in directory.Tags)
    {
        string desc;
        try { desc = tag.Description ?? "(null)"; }
        catch { desc = "(error)"; }
        Console.WriteLine($"│  [{tag.Type:D5}] {tag.Name,-40} = {desc}");
    }
    
    // 显示目录中的错误
    if (directory.HasError)
    {
        foreach (var error in directory.Errors)
            Console.WriteLine($"│  ⚠ ERROR: {error}");
    }
    Console.WriteLine("└─\n");
}

// ---- Part 2: 关键字段验证（与 HTML 参考对比）----
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("Part 2: 关键字段验证（对比 HTML 参考）");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

var exifSubIfd = FindShootingExifSubIfd(directories);
var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
var xmpDirs = directories.OfType<XmpDirectory>().ToList();

static ExifSubIfdDirectory? FindShootingExifSubIfd(IReadOnlyList<MetadataExtractor.Directory> dirs)
{
    var subIfds = dirs.OfType<ExifSubIfdDirectory>().ToList();
    if (subIfds.Count == 0) return null;
    if (subIfds.Count == 1) return subIfds[0];
    foreach (var s in subIfds)
    {
        if (s.ContainsTag(ExifDirectoryBase.TagExposureTime) ||
            s.ContainsTag(ExifDirectoryBase.TagFNumber) ||
            s.ContainsTag(ExifDirectoryBase.TagIsoEquivalent))
            return s;
    }
    return subIfds.OrderByDescending(d => d.Tags.Count).First();
}

// HTML 参考值
var checks = new List<(string label, string expected, string actual)>();

// IFD0 字段
if (exifIfd0 != null)
{
    checks.Add(("Make", "SONY", exifIfd0.GetDescription(ExifDirectoryBase.TagMake) ?? ""));
    checks.Add(("Model", "ILCE-7CM2", exifIfd0.GetDescription(ExifDirectoryBase.TagModel) ?? ""));
    checks.Add(("Orientation", "Horizontal (normal)", exifIfd0.GetDescription(ExifDirectoryBase.TagOrientation) ?? ""));
    checks.Add(("Software", "ILCE-7CM2 v2.00", exifIfd0.GetDescription(ExifDirectoryBase.TagSoftware) ?? ""));
    checks.Add(("DateTime", "2026:04:04 13:56:00", exifIfd0.GetDescription(ExifDirectoryBase.TagDateTime) ?? ""));
}

// ExifIFD 字段
if (exifSubIfd != null)
{
    checks.Add(("ExposureTime", "1/160", GetRawValue(exifSubIfd, ExifDirectoryBase.TagExposureTime)));
    checks.Add(("FNumber", "f/8.0", exifSubIfd.GetDescription(ExifDirectoryBase.TagFNumber) ?? ""));
    checks.Add(("ExposureProgram", "Aperture-priority AE", GetRawValue(exifSubIfd, ExifDirectoryBase.TagExposureProgram)));
    checks.Add(("ISO", "100", GetRawValue(exifSubIfd, ExifDirectoryBase.TagIsoEquivalent)));
    checks.Add(("DateTimeOriginal", "2026:04:04 13:56:00", GetRawValue(exifSubIfd, ExifDirectoryBase.TagDateTimeOriginal)));
    checks.Add(("ExposureBias", "0", GetRawValue(exifSubIfd, ExifDirectoryBase.TagExposureBias)));
    checks.Add(("MaxAperture", "2.8", GetRawValue(exifSubIfd, ExifDirectoryBase.TagMaxAperture)));
    checks.Add(("MeteringMode", "Multi-segment", exifSubIfd.GetDescription(ExifDirectoryBase.TagMeteringMode) ?? ""));
    checks.Add(("Flash", "Off, Did not fire", exifSubIfd.GetDescription(ExifDirectoryBase.TagFlash) ?? ""));
    checks.Add(("FocalLength", "46.0 mm", exifSubIfd.GetDescription(ExifDirectoryBase.TagFocalLength) ?? ""));
    checks.Add(("ColorSpace", "sRGB", exifSubIfd.GetDescription(ExifDirectoryBase.TagColorSpace) ?? ""));
    checks.Add(("ExifImageWidth", "7008", GetRawValue(exifSubIfd, ExifDirectoryBase.TagExifImageWidth)));
    checks.Add(("ExifImageHeight", "4672", GetRawValue(exifSubIfd, ExifDirectoryBase.TagExifImageHeight)));
    checks.Add(("WhiteBalance", "Auto", exifSubIfd.GetDescription(ExifDirectoryBase.TagWhiteBalance) ?? ""));
    checks.Add(("FocalLength35mm", "46 mm", exifSubIfd.GetDescription(ExifDirectoryBase.Tag35MMFilmEquivFocalLength) ?? ""));
    checks.Add(("LensModel", "E 28-75mm F2.8 A063", exifSubIfd.GetDescription(ExifDirectoryBase.TagLensModel) ?? ""));
    checks.Add(("LensSpec", "28-75mm f/2.8", exifSubIfd.GetDescription(ExifDirectoryBase.TagLensSpecification) ?? ""));
}

// XMP 字段
foreach (var xmpDir in xmpDirs)
{
    var props = xmpDir.GetXmpProperties();
    foreach (var kv in props)
    {
        if (kv.Key.Contains("Rating", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(("XMP Rating", "3", kv.Value));
        }
    }
}

// 检查 Sony Makernote
var sonyDirs = directories.Where(d => d.Name.Contains("Sony", StringComparison.OrdinalIgnoreCase)).ToList();
if (sonyDirs.Any())
{
    Console.WriteLine($"  Found {sonyDirs.Count} Sony Makernote directories\n");
}

// 输出对比结果
Console.WriteLine($"{"Field",-25} {"Expected",-35} {"Actual",-35} {"Match"}");
Console.WriteLine(new string('-', 100));
foreach (var (label, expected, actual) in checks)
{
    var match = !string.IsNullOrEmpty(actual) && !string.IsNullOrEmpty(expected) &&
                (actual.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                 expected.Contains(actual, StringComparison.OrdinalIgnoreCase));
    var status = match ? "✓" : "✗";
    Console.WriteLine($"{label,-25} {expected,-35} {actual,-35} {status}");
}

// ---- Part 3: XMP 详细解析 ----
Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
Console.WriteLine("Part 3: XMP 原始属性解析");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

foreach (var xmpDir in xmpDirs)
{
    Console.WriteLine($"[XMP Directory: {xmpDir.Name}]");
    var props = xmpDir.GetXmpProperties();
    foreach (var kv in props.OrderBy(p => p.Key))
    {
        Console.WriteLine($"  {kv.Key,-50} = {kv.Value}");
    }
    
    // 尝试直接用 XmpMeta 解析
    try
    {
        var xmpMeta = xmpDir.XmpMeta;
        if (xmpMeta != null)
        {
            Console.WriteLine("\n  [XmpMeta Properties]");
            var nsIterator = xmpMeta.Properties;
            foreach (var prop in nsIterator)
            {
                if (!string.IsNullOrEmpty(prop.Value))
                {
                    Console.WriteLine($"  {prop.Namespace,-30} {prop.Path,-40} = {prop.Value}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  XmpMeta iterator error: {ex.Message}");
    }
    Console.WriteLine();
}

// ---- Part 4: Sony Makernote 详细信息 ----
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("Part 4: Sony Makernote 详细信息");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

foreach (var dir in sonyDirs)
{
    Console.WriteLine($"[{dir.Name}] ({dir.Tags.Count} tags)");
    foreach (var tag in dir.Tags)
    {
        string desc;
        try { desc = tag.Description ?? "(null)"; }
        catch { desc = "(error)"; }
        // 截断过长的值
        if (desc.Length > 100) desc = desc[..100] + "...";
        Console.WriteLine($"  [{tag.Type:D5}] {tag.Name,-45} = {desc}");
    }
    Console.WriteLine();
}

// ---- Part 5: 提取能力总结 ----
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("Part 5: 目录类型汇总");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

foreach (var dir in directories)
{
    Console.WriteLine($"  {dir.GetType().Name,-45} -> \"{dir.Name}\" ({dir.Tags.Count} tags)");
}

return 0;

// ---- Helper ----
static string GetRawValue(MetadataExtractor.Directory dir, int tagType)
{
    try
    {
        return dir.GetDescription(tagType) ?? dir.GetString(tagType) ?? "(null)";
    }
    catch
    {
        return "(error)";
    }
}
