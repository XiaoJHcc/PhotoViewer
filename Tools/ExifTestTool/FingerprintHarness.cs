using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoViewer.Core.Database;

namespace ExifTestTool;

/// <summary>
/// 指纹算法验证工具：扫描目录内所有照片，输出每张照片的指纹与参与字段，
/// 并检查三组不变量：
///   1) 同一文件名（不同扩展名）的 RAW/HIF 对应同一指纹；
///   2) 同一次连拍各帧指纹各不相同；
///   3) 所有指纹在数据库主键意义下唯一。
/// </summary>
internal static class FingerprintHarness
{
    /// <summary>执行验证并返回进程退出码（0 成功，1 有异常）。</summary>
    public static int Run(string folder)
    {
        if (!System.IO.Directory.Exists(folder))
        {
            Console.WriteLine($"目录不存在: {folder}");
            return 1;
        }

        var files = System.IO.Directory.GetFiles(folder)
            .Where(p => IsPhoto(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine("未找到照片文件 (扩展名 .arw/.hif/.heic/.heif/.jpg/.jpeg)。");
            return 1;
        }

        Console.WriteLine($"=== 指纹验证: {folder} ({files.Length} 文件) ===\n");
        Console.WriteLine($"{"File",-22} {"CaptureTime",-20} {"SubSec",-8} Fingerprint");
        Console.WriteLine(new string('-', 100));

        var byFingerprint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byFilename = new Dictionary<string, List<(string ext, string fp)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path);
            var noext = Path.GetFileNameWithoutExtension(name);

            PhotoFingerprintInput input;
            try
            {
                input = BuildInputFromFile(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-22} <读取失败: {ex.Message}>");
                continue;
            }

            var fp = PhotoFingerprint.Compute(input);
            var time = input.CaptureTime?.ToString("yyyy-MM-ddTHH:mm:ss") ?? "-";
            var sub = input.CaptureSubSec ?? "-";
            Console.WriteLine($"{name,-22} {time,-20} {sub,-8} {fp}");

            if (!byFingerprint.TryGetValue(fp, out var list))
            {
                list = new List<string>();
                byFingerprint[fp] = list;
            }
            list.Add(name);

            if (!byFilename.TryGetValue(noext, out var bucket))
            {
                bucket = new List<(string, string)>();
                byFilename[noext] = bucket;
            }
            bucket.Add((ext, fp));
        }

        Console.WriteLine();
        Console.WriteLine("=== 不变量检查 ===");

        var pairErrors = 0;
        var pairChecks = 0;
        foreach (var (noext, bucket) in byFilename)
        {
            if (bucket.Count < 2) continue;
            pairChecks++;
            var distinct = bucket.Select(b => b.fp).Distinct().Count();
            if (distinct != 1)
            {
                pairErrors++;
                Console.WriteLine($"  ✗ {noext}: 同名多扩展指纹不一致 -> {string.Join(", ", bucket.Select(b => $"{b.ext}={b.fp[..8]}"))}");
            }
        }
        Console.WriteLine(pairErrors == 0
            ? $"  ✓ 同名配对: {pairChecks} 组全部一致"
            : $"  ✗ 同名配对: {pairErrors}/{pairChecks} 组不一致");

        var dupCollisions = byFingerprint.Where(kv => kv.Value.Count > 1).ToList();
        var crossShotCollisions = dupCollisions
            .Where(kv => kv.Value.Select(n => Path.GetFileNameWithoutExtension(n)).Distinct().Count() > 1)
            .ToList();
        if (crossShotCollisions.Count == 0)
        {
            Console.WriteLine("  ✓ 不同曝光指纹冲突: 0");
        }
        else
        {
            Console.WriteLine($"  ✗ 不同曝光指纹冲突: {crossShotCollisions.Count}");
            foreach (var kv in crossShotCollisions)
            {
                Console.WriteLine($"     {kv.Key[..8]} -> {string.Join(", ", kv.Value)}");
            }
        }

        Console.WriteLine();
        return (pairErrors == 0 && crossShotCollisions.Count == 0) ? 0 : 2;
    }

    private static bool IsPhoto(string ext) => ext.ToLowerInvariant() switch
    {
        ".arw" or ".hif" or ".heic" or ".heif" or ".jpg" or ".jpeg" => true,
        _ => false
    };

    /// <summary>直接读文件头做轻量 EXIF 解析，抽出指纹所需的三项输入。</summary>
    private static PhotoFingerprintInput BuildInputFromFile(string path)
    {
        var dirs = ImageMetadataReader.ReadMetadata(path);
        var subIfd = dirs.OfType<ExifSubIfdDirectory>()
            .FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal));

        DateTime? capture = null;
        string? subSec = null;
        if (subIfd != null)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
            {
                capture = dt;
            }
            const int tagSubSec = 37521;
            if (subIfd.ContainsTag(tagSubSec))
            {
                subSec = subIfd.GetString(tagSubSec);
            }
        }

        capture ??= File.GetLastWriteTime(path);

        return new PhotoFingerprintInput
        {
            FilenameNoExt = Path.GetFileNameWithoutExtension(path),
            CaptureTime = capture,
            CaptureSubSec = subSec
        };
    }
}
