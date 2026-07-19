using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoViewer.Core.Database;

namespace DatasetBuilder;

/// <summary>
/// 裸文件路径版指纹聚合（算法照搬 <c>FolderFeatureIndexer.GroupByFingerprintAsync</c>，但不依赖 ImageFile / PhotoDatabase）。
/// 同次曝光的 RAW/HEIF/JPG 合为一组、只解码代表文件一次；代表文件按解码代价升序（HEIF→JPG→其他→RAW）。
/// 额外解析同名 <c>.xmp</c> sidecar 星级（Plan-3-1 §1.1：ARW 的 rating 通常在 sidecar）。
/// </summary>
public static class FingerprintGrouper
{
    /// <summary>扫描清单文件夹并按指纹聚合。</summary>
    /// <param name="folders">清单中的文件夹条目。</param>
    /// <returns>指纹组列表。</returns>
    public static List<FpGroup> Scan(IReadOnlyList<FolderEntry> folders)
    {
        var byFp = new Dictionary<string, List<SourceFile>>();
        var inputByFp = new Dictionary<string, PhotoFingerprintInput>();

        foreach (var entry in folders)
        {
            if (!System.IO.Directory.Exists(entry.Path))
            {
                Console.WriteLine($"[WARN] 文件夹不存在，跳过: {entry.Path}");
                continue;
            }
            var opt = entry.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            // 跳过 macOS AppleDouble 资源叉文件（._*）：Mac 拷贝产生的元数据残桩，非图像，解码必失败
            foreach (var path in System.IO.Directory.EnumerateFiles(entry.Path, "*.*", opt)
                         .Where(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
                         .Where(PhotoDecode.IsImage))
            {
                var exif = PhotoDecode.ReadExif(path);
                var captureTime = exif.CaptureTime ?? File.GetLastWriteTimeUtc(path);
                var input = new PhotoFingerprintInput
                {
                    FilenameNoExt = Path.GetFileNameWithoutExtension(path),
                    CaptureTime = captureTime,
                    CaptureSubSec = exif.SubSec,
                };
                var fp = PhotoFingerprint.Compute(input);

                var rating = ResolveRating(path, exif.Rating);
                var relPath = Path.GetRelativePath(entry.Path, path);
                var file = new SourceFile(path, relPath, entry, exif, rating);

                if (!byFp.TryGetValue(fp, out var list))
                {
                    list = new List<SourceFile>();
                    byFp[fp] = list;
                    inputByFp[fp] = input;
                }
                list.Add(file);
            }
        }

        var result = new List<FpGroup>(byFp.Count);
        foreach (var (fp, list) in byFp)
        {
            list.Sort((a, b) => DecodeCostScore(a.Path).CompareTo(DecodeCostScore(b.Path)));
            result.Add(new FpGroup(fp, inputByFp[fp], list));
        }
        return result;
    }

    /// <summary>解码代价评分：越小越快，同组取最小者作代表。</summary>
    private static int DecodeCostScore(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".heif" or ".heic" or ".hif" => 0,
            ".jpg" or ".jpeg" => 1,
            ".png" or ".webp" or ".bmp" or ".gif" or ".tiff" or ".tif" => 2,
            _ => 3, // RAW 等
        };

    /// <summary>
    /// 星级 = max(嵌入 XMP 星级, 同名 sidecar 星级)。sidecar 探测 "name.xmp" 与 "name.ext.xmp" 两种约定。
    /// </summary>
    private static int ResolveRating(string path, int embedded)
    {
        int rating = embedded;
        foreach (var sidecar in new[] { Path.ChangeExtension(path, ".xmp"), path + ".xmp" })
        {
            if (!File.Exists(sidecar)) continue;
            var r = ReadXmpRating(sidecar);
            if (r > rating) rating = r;
        }
        return rating;
    }

    /// <summary>从 .xmp sidecar 文件读 xmp:Rating；失败或缺失返回 0。</summary>
    private static int ReadXmpRating(string sidecarPath)
    {
        try
        {
            using var stream = File.OpenRead(sidecarPath);
            var dirs = ImageMetadataReader.ReadMetadata(stream);
            var xmp = dirs.OfType<XmpDirectory>().FirstOrDefault();
            var meta = xmp?.XmpMeta;
            if (meta == null) return 0;
            var s = meta.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:Rating");
            return s != null && int.TryParse(s, out var r) ? r : 0;
        }
        catch { return 0; }
    }
}

/// <summary>一个待入库文件（含来源标签与 EXIF）。</summary>
public sealed record SourceFile(string Path, string RelPath, FolderEntry Folder, PhotoExif Exif, int Rating);

/// <summary>一组同指纹的文件。</summary>
public sealed class FpGroup
{
    /// <summary>组指纹。</summary>
    public string Fingerprint { get; }

    /// <summary>写库用的指纹输入。</summary>
    public PhotoFingerprintInput Input { get; }

    /// <summary>组内文件，按解码代价升序（代表文件在索引 0）。</summary>
    public IReadOnlyList<SourceFile> Files { get; }

    public FpGroup(string fingerprint, PhotoFingerprintInput input, IReadOnlyList<SourceFile> files)
    {
        Fingerprint = fingerprint;
        Input = input;
        Files = files;
    }

    /// <summary>代表文件：喂给 DINO/CV 的那张（解码代价最低）。</summary>
    public SourceFile Representative => Files[0];

    /// <summary>组内星级 = 各文件解析星级的最大值（0 表示未评）。</summary>
    public int Rating => Files.Max(f => f.Rating);

    /// <summary>组内出现的扩展名集合（去点、小写、去重、排序，以 '|' 连接），如 "arw|hif"。</summary>
    public string Formats => string.Join('|',
        Files.Select(f => Path.GetExtension(f.Path).TrimStart('.').ToLowerInvariant())
             .Distinct().OrderBy(x => x));
}
