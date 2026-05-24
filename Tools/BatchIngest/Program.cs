using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using LibHeifSharp;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using Microsoft.ML.OnnxRuntime;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;

namespace BatchIngest;

/// <summary>
/// 批量入库工具：扫描照片文件夹，填充 photos.db（指纹 + EXIF + rating + DINO + CV）。
/// 开发者专用，为 Plan-3 训练 pipeline 准备数据。
/// </summary>
class Program
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".heif", ".heic", ".hif", ".arw"];

    private static readonly string[] DecodableExtensions =
        [".jpg", ".jpeg", ".heif", ".heic", ".hif"];

    private static int _completed;
    private static int _failed;
    private static int _skipped;
    private static int _total;

    static async Task<int> Main(string[] args)
    {
        var (inputPaths, recursive, concurrency) = ParseArgs(args);
        if (inputPaths.Count == 0) { PrintUsage(); return 1; }

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        DinoFeatureExtractor.ConfigureSession(o => o.AppendExecutionProvider_DML());
        PhotoDatabase.Initialize();
        Console.WriteLine($"数据库: {PhotoDatabase.DatabasePath}");

        var files = ScanFiles(inputPaths, recursive);
        _total = files.Count;
        Console.WriteLine($"扫描到 {_total} 个图片文件");
        if (_total == 0) return 0;

        var sw = Stopwatch.StartNew();
        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = files.Select(f => ProcessFileAsync(f, semaphore)).ToArray();
        await Task.WhenAll(tasks);

        sw.Stop();
        Console.WriteLine($"\n完成: {_completed} 入库, {_skipped} 跳过, {_failed} 失败, 耗时 {sw.Elapsed:hh\\:mm\\:ss}");
        return _failed > 0 ? 1 : 0;
    }

    static async Task ProcessFileAsync(string path, SemaphoreSlim sem)
    {
        await sem.WaitAsync();
        try { await ProcessOneAsync(path); }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {Path.GetFileName(path)}: {ex.Message}");
            Interlocked.Increment(ref _failed);
        }
        finally { sem.Release(); }
    }

    static async Task ProcessOneAsync(string path)
    {
        // 读取 EXIF
        var (captureTime, subSec, focalLength, aperture, shutter,
             equivFocal, rating) = ReadExif(path);

        // 计算指纹
        var input = new PhotoFingerprintInput
        {
            FilenameNoExt = Path.GetFileNameWithoutExtension(path),
            CaptureTime = captureTime ?? File.GetLastWriteTimeUtc(path),
            CaptureSubSec = subSec,
        };
        var fingerprint = PhotoFingerprint.Compute(input);

        // 评估缺失
        var missing = await PhotoDatabase.EvaluateMissingPartsAsync(
            fingerprint, DinoModelResources.ModelId, CvGridResult.CurrentVersion);

        // 构建 EXIF 快照
        double? cropFactor = null;
        if (focalLength is > 0 && equivFocal is > 0)
            cropFactor = equivFocal.Value / focalLength.Value;

        var exifSnapshot = new ExifSnapshot
        {
            FocalLength = focalLength,
            Aperture = aperture,
            ShutterSpeed = shutter,
            CropFactor = cropFactor,
            Rating = rating > 0 ? rating : null,
        };

        // 全部已入库 → 仅刷新 EXIF/rating
        if (!missing.AnyMissing)
        {
            await PhotoDatabase.WriteIndexedAsync(
                input, fingerprint, DinoModelResources.ModelId,
                null, null, null, null, 0, 0, exifSnapshot);
            Interlocked.Increment(ref _skipped);
            return;
        }

        // 不可解码（如 ARW）→ 只写身份和 EXIF
        if (!CanDecode(path))
        {
            await PhotoDatabase.WriteIndexedAsync(
                input, fingerprint, DinoModelResources.ModelId,
                null, null, null, null, 0, 0, exifSnapshot);
            Interlocked.Increment(ref _skipped);
            return;
        }

        // 解码位图
        Bitmap? bitmap = null;
        try
        {
            bitmap = LoadBitmap(path);
            if (bitmap == null)
            {
                Console.WriteLine($"[SKIP] 解码失败: {Path.GetFileName(path)}");
                Interlocked.Increment(ref _failed);
                return;
            }

            byte[]? clsBlob = null, patchBlob = null, cvBlob = null;
            string? cvSpec = null;
            int cvW = 0, cvH = 0;

            if (missing.NeedCls || missing.NeedPatches)
            {
                var (cls, patches) = await DinoFeatureExtractor
                    .ExtractDualAsync(bitmap, includePatches: missing.NeedPatches);
                clsBlob = EncodeFloats(cls);
                if (patches != null) patchBlob = EncodeFloats(patches);
            }

            if (missing.NeedCv)
            {
                var cvResult = await CvGridExtractor.ExtractAsync(bitmap);
                cvBlob = cvResult.Encode();
                cvSpec = CvGridResult.CurrentVersion;
                cvW = bitmap.PixelSize.Width;
                cvH = bitmap.PixelSize.Height;
            }

            await PhotoDatabase.WriteIndexedAsync(
                input, fingerprint, DinoModelResources.ModelId,
                clsBlob, patchBlob, cvBlob, cvSpec, cvW, cvH, exifSnapshot);

            var done = Interlocked.Increment(ref _completed);
            if (done % 20 == 0)
                Console.WriteLine($"  [{done + _skipped + _failed}/{_total}] {done} 入库");
        }
        finally { bitmap?.Dispose(); }
    }

    // ─── EXIF 读取（直接用 MetadataExtractor，不经 IStorageFile） ───

    static (DateTime? captureTime, string? subSec, double? focal, double? aperture,
            double? shutter, double? equivFocal, int rating) ReadExif(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var dirs = ImageMetadataReader.ReadMetadata(stream);

            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var xmp = dirs.OfType<XmpDirectory>().FirstOrDefault();

            DateTime? captureTime = null;
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                captureTime = dt;

            const int tagSubSec = 37521;
            string? subSec = sub?.GetString(tagSubSec);

            double? focal = GetRational(sub, ExifDirectoryBase.TagFocalLength);
            double? aperture = GetRational(sub, ExifDirectoryBase.TagFNumber);
            double? shutter = GetRational(sub, ExifDirectoryBase.TagExposureTime);
            double? equivFocal = GetRational(sub, ExifDirectoryBase.Tag35MMFilmEquivFocalLength);

            int rating = 0;
            if (xmp?.XmpMeta != null)
            {
                try
                {
                    var ratingStr = xmp.XmpMeta.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:Rating");
                    if (ratingStr != null) int.TryParse(ratingStr, out rating);
                }
                catch { }
            }

            return (captureTime, subSec, focal, aperture, shutter, equivFocal, rating);
        }
        catch { return (null, null, null, null, null, null, 0); }
    }

    static double? GetRational(MetadataExtractor.Directory? dir, int tag)
    {
        if (dir == null) return null;
        if (!dir.TryGetRational(tag, out var r)) return null;
        if (r.Denominator == 0) return null;
        return r.ToDouble();
    }

    // ─── 位图解码 ───

    static Bitmap? LoadBitmap(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heif" or ".heic" or ".hif")
            return DecodeHeif(path);
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    static Bitmap? DecodeHeif(string path)
    {
        var data = File.ReadAllBytes(path);
        using var context = new HeifContext(data);
        using var handle = context.GetPrimaryImageHandle();
        if (handle == null) return null;
        using var image = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
        if (image == null) return null;

        int w = (int)image.Width, h = (int)image.Height;
        var plane = image.GetPlane(HeifChannel.Interleaved);
        int stride = (int)plane.Stride;

        var wb = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96),
            Avalonia.Platform.PixelFormats.Bgra8888);
        using var locked = wb.Lock();
        unsafe
        {
            var src = (byte*)plane.Scan0;
            var dst = (byte*)locked.Address;
            int dstStride = locked.RowBytes;
            for (int y = 0; y < h; y++)
            {
                var srcRow = src + y * stride;
                var dstRow = dst + y * dstStride;
                for (int x = 0; x < w; x++)
                {
                    int si = x * 3, di = x * 4;
                    dstRow[di + 0] = srcRow[si + 2]; // B
                    dstRow[di + 1] = srcRow[si + 1]; // G
                    dstRow[di + 2] = srcRow[si + 0]; // R
                    dstRow[di + 3] = 255;            // A
                }
            }
        }
        return wb;
    }

    // ─── 辅助 ───

    static bool CanDecode(string path) =>
        DecodableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    static byte[] EncodeFloats(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    static List<string> ScanFiles(List<string> inputs, bool recursive)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<string>();
        foreach (var p in inputs)
        {
            if (File.Exists(p)) { if (IsImage(p)) result.Add(p); }
            else if (System.IO.Directory.Exists(p))
                result.AddRange(System.IO.Directory.EnumerateFiles(p, "*.*", opt).Where(IsImage));
            else Console.WriteLine($"[WARN] 路径不存在: {p}");
        }
        return result;
    }

    static bool IsImage(string p) =>
        ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant());

    static (List<string> inputs, bool recursive, int concurrency) ParseArgs(string[] args)
    {
        var inputs = new List<string>();
        bool recursive = true;
        int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-recursive": recursive = false; break;
                case "--concurrency" when i + 1 < args.Length:
                    concurrency = int.Parse(args[++i]); break;
                default:
                    if (!args[i].StartsWith("--")) inputs.Add(args[i]); break;
            }
        }
        return (inputs, recursive, concurrency);
    }

    static void PrintUsage()
    {
        Console.WriteLine("BatchIngest — 批量入库工具 (Plan-3)");
        Console.WriteLine();
        Console.WriteLine("用法: dotnet run --project Tools/BatchIngest -- <folder> [options]");
        Console.WriteLine();
        Console.WriteLine("  <folder>           输入路径（可多个）");
        Console.WriteLine("  --no-recursive     不递归子目录");
        Console.WriteLine("  --concurrency <n>  解码并发数（默认 CPU/2）");
    }
}
