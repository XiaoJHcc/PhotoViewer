using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoViewer.Core.Thumbnails;

/// <summary>
/// 缩略图服务门面：对外只暴露两个能力 ——
/// 1) <see cref="GetAvailableSourcesAsync"/>：列出文件可用的缩略图来源（含尺寸，未解码）；
/// 2) <see cref="GetThumbnailAsync"/>：按目标短边挑一个尺寸最合适的来源解码。
/// 设计约束：
/// - 仅从广义 EXIF/容器元数据里列出"已知尺寸的缩略图条目"，绝不触发原图全分辨率解码。
/// - HEIF 路径优先直接按 HEIC Thumbnail Data 条目的 Offset/Length 读 JPEG 字节解码（零 HEIF/HEVC 解码）；
///   仅当所有 JPEG 字节条目都无法解码时，退到平台 <see cref="HeifLoader"/> 兜底一次。
/// </summary>
public static class ThumbnailService
{
    /// <summary>
    /// 列出该文件所有可用的缩略图来源（尺寸由容器/EXIF 元数据读取；不做任何图像解码）。
    /// 顺序与容器里的声明顺序一致；尺寸未知时 <see cref="ThumbnailSource.Width"/> / <see cref="ThumbnailSource.Height"/> 为 0。
    /// </summary>
    public static async Task<IReadOnlyList<ThumbnailSource>> GetAvailableSourcesAsync(IStorageFile file)
    {
        if (file == null) return Array.Empty<ThumbnailSource>();

        try
        {
            await using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);
            return HeifLoader.IsHeifFile(file)
                ? EnumerateHeifSources(file, directories)
                : EnumerateNonHeifSources(file, directories);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to enumerate thumbnail sources (" + file.Name + "): " + ex.Message);
            return Array.Empty<ThumbnailSource>();
        }
    }

    /// <summary>
    /// 按目标短边 <paramref name="targetShortSide"/> 选取并解码一张缩略图：
    /// 优先"短边 ≥ target 中最小"的来源（最省 I/O 就能达到显示质量），
    /// 若无任何已知尺寸合格，再尝试尺寸未知的来源，
    /// 最后退到"短边 &lt; target 中最大"的条目。
    /// 任何来源都不会触发原图全分辨率解码；所有来源都失败时返回 null。
    /// </summary>
    public static async Task<Bitmap?> GetThumbnailAsync(IStorageFile file, int targetShortSide)
    {
        if (file == null || targetShortSide <= 0) return null;

        var sources = await GetAvailableSourcesAsync(file);
        if (sources.Count == 0) return null;

        foreach (var src in OrderByPriority(sources, targetShortSide))
        {
            try
            {
                var bmp = await src.LoaderAsync(targetShortSide);
                if (bmp != null) return bmp;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ThumbnailService: source " + src.Origin + " failed for " + file.Name + ": " + ex.Message);
            }
        }
        return null;
    }

    /// <summary>
    /// 选源优先级：短边 ≥ target 中最小优先（解码代价最低且质量达标），
    /// 其次是尺寸未知的（由平台解码器挑选），最后是"短边 &lt; target 中最大"（退而求其次）。
    /// </summary>
    private static IEnumerable<ThumbnailSource> OrderByPriority(IReadOnlyList<ThumbnailSource> sources, int target)
    {
        var qualified = sources.Where(s => s.ShortSide >= target).OrderBy(s => s.ShortSide);
        var unknown = sources.Where(s => s.ShortSide == 0);
        var insufficient = sources.Where(s => s.ShortSide > 0 && s.ShortSide < target).OrderByDescending(s => s.ShortSide);
        return qualified.Concat(unknown).Concat(insufficient);
    }

    // ============================================================
    // HEIF 容器：从 HEIC Thumbnail Properties/Data 目录读尺寸与字节偏移，
    // 直接按字节解码 JPEG 内嵌缩略图；仅对非 JPEG 字节（如 HEVC 条目）退到平台解码器。
    // ============================================================

    private static IReadOnlyList<ThumbnailSource> EnumerateHeifSources(IStorageFile file, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var result = new List<ThumbnailSource>();

        // 先把"属性目录"里的尺寸按出现顺序收齐，用于对齐到随后出现的"数据目录"条目。
        var propSizes = new List<(int w, int h)>();
        foreach (var dir in directories)
        {
            if (!IsHeicThumbnailProperties(dir)) continue;
            int w = TryReadInt(dir, 1);
            int h = TryReadInt(dir, 2);
            if (w > 0 && h > 0) propSizes.Add((w, h));
        }

        int dataIdx = 0;
        foreach (var dir in directories)
        {
            if (!IsHeicThumbnailData(dir)) continue;
            int offset = TryReadInt(dir, 1);
            int length = TryReadInt(dir, 2);

            int w = 0, h = 0;
            if (dataIdx < propSizes.Count)
            {
                w = propSizes[dataIdx].w;
                h = propSizes[dataIdx].h;
            }
            dataIdx++;

            if (offset <= 0 || length <= 100) continue;

            int capturedOffset = offset;
            int capturedLength = length;
            result.Add(new ThumbnailSource(
                width: w, height: h, origin: ThumbnailOrigin.HeifEmbedded,
                loaderAsync: target => ReadRangeAndDecodeAsync(file, capturedOffset, capturedLength, target)));
        }

        // 平台解码器兜底（尺寸未知）：当字节条目全部嗅探失败（如 HEVC 编码）时仍可走平台路径。
        // 不再追加任何全分辨率解码来源。
        result.Add(new ThumbnailSource(
            width: 0, height: 0, origin: ThumbnailOrigin.HeifEmbedded,
            loaderAsync: target => HeifLoader.LoadHeifThumbnailAsync(file, target)));

        return result;
    }

    /// <summary>
    /// 按 (offset, length) 从文件里截取一段字节，嗅探为合法图片（JPEG/PNG/BMP/TIFF）后按目标宽度解码。
    /// 不是图片字节（如 HEVC 裸流）时返回 null，让上层跳到下一个来源。
    /// </summary>
    private static async Task<Bitmap?> ReadRangeAndDecodeAsync(IStorageFile file, int offset, int length, int targetShortSide)
    {
        try
        {
            await using var s = await file.OpenReadAsync();
            s.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            int read = await s.ReadAsync(buffer, 0, length);
            if (read != length) return null;
            if (!IsValidImageData(buffer)) return null;
            return DecodeBytesToShortSide(buffer, targetShortSide);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ThumbnailService: read range failed (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    private static bool IsHeicThumbnailProperties(MetadataExtractor.Directory d) =>
        d.Name.IndexOf("Thumbnail Properties", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsHeicThumbnailData(MetadataExtractor.Directory d) =>
        d.Name.IndexOf("Thumbnail Data", StringComparison.OrdinalIgnoreCase) >= 0;

    private static int TryReadInt(MetadataExtractor.Directory dir, int tag)
    {
        try { return dir.GetInt32(tag); }
        catch { return 0; }
    }

    // ============================================================
    // JPEG / TIFF / ARW 等非 HEIF 容器：枚举 EXIF/IFD1 与厂商 Preview/Thumbnail 目录。
    // 尺寸能从元数据读出就读出；字节条目仅懒加载，不做原图解码。
    // ============================================================

    private static IReadOnlyList<ThumbnailSource> EnumerateNonHeifSources(IStorageFile file, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var result = new List<ThumbnailSource>();

        // 1) 标准 EXIF/IFD1 缩略图条目（tag 256/257 = Width/Height）
        var exifThumbDir = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
        if (exifThumbDir != null)
        {
            int w = TryReadInt(exifThumbDir, ExifDirectoryBase.TagImageWidth);
            int h = TryReadInt(exifThumbDir, ExifDirectoryBase.TagImageHeight);
            var capturedDir = exifThumbDir;
            result.Add(new ThumbnailSource(
                width: w, height: h, origin: ThumbnailOrigin.ExifEmbedded,
                loaderAsync: target => ReadEmbeddedFromExifThumbnailDirectoryAsync(file, capturedDir, target)));
        }

        // 2) 厂商 MakerNote / Preview / Thumbnail 目录（如 Sony PreviewImage）
        foreach (var dir in directories)
        {
            if (ReferenceEquals(dir, exifThumbDir)) continue;
            var lname = dir.GetType().Name.ToLowerInvariant();
            if (!(lname.Contains("preview") || lname.Contains("thumbnail"))) continue;
            AppendMakernoteSources(dir, result);
        }

        // 不再追加 FullImage 全图兜底：若没有任何内嵌缩略图，返回空列表由上层显示占位符。
        return result;
    }

    /// <summary>
    /// 把厂商 Preview/Thumbnail 目录里"看起来是合法图像字节"的 tag 列为来源；
    /// 尺寸信息大多无法从元数据直接取，填 0（未知），由选源策略决定尝试顺序。
    /// </summary>
    private static void AppendMakernoteSources(MetadataExtractor.Directory dir, List<ThumbnailSource> sink)
    {
        foreach (var tag in dir.Tags)
        {
            try
            {
                var data = dir.GetByteArray(tag.Type);
                if (data == null || data.Length <= 100 || !IsValidImageData(data)) continue;

                var capturedData = data;
                sink.Add(new ThumbnailSource(
                    width: 0, height: 0, origin: ThumbnailOrigin.MakernotePreview,
                    loaderAsync: target => Task.FromResult(DecodeBytesToShortSide(capturedData, target))));
            }
            catch
            {
                // 当前 tag 不是字节数组，跳过
            }
        }
    }

    /// <summary>
    /// 从 ExifThumbnailDirectory 读取嵌入缩略图字节：先按常见字节 tag，再按 (Offset 0x0201, Length 0x0202) 偏移读取。
    /// 始终只解码嵌入字节；任何情况都不会触发原图解码。
    /// </summary>
    private static async Task<Bitmap?> ReadEmbeddedFromExifThumbnailDirectoryAsync(IStorageFile file, ExifThumbnailDirectory dir, int targetShortSide)
    {
        int[] thumbnailTags = { 0x0201, 0x0202, 0x0103 };
        foreach (var tag in thumbnailTags)
        {
            if (!dir.HasTagName(tag)) continue;
            try
            {
                var data = dir.GetByteArray(tag);
                if (data?.Length > 100 && IsValidImageData(data))
                {
                    var thumb = DecodeBytesToShortSide(data, targetShortSide);
                    if (thumb != null) return thumb;
                }
            }
            catch
            {
                // 当前 tag 不是字节数组，继续
            }
        }

        if (dir.HasTagName(0x0201) && dir.HasTagName(0x0202))
        {
            try
            {
                var offset = dir.GetInt32(0x0201);
                var length = dir.GetInt32(0x0202);
                if (offset > 0 && length > 100)
                {
                    await using var s = await file.OpenReadAsync();
                    s.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[length];
                    var read = await s.ReadAsync(buffer, 0, length);
                    if (read == length && IsValidImageData(buffer))
                    {
                        return DecodeBytesToShortSide(buffer, targetShortSide);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to extract thumbnail by offset (" + file.Name + "): " + ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// 把已编码的图片字节解码为目标短边附近的位图。
    /// Avalonia 仅提供 DecodeToWidth；内嵌缩略图本就很小（几十到几百 KB），
    /// 无论目标是按宽度还是短边表达，都不会反向放大，精度足够。
    /// </summary>
    private static Bitmap? DecodeBytesToShortSide(byte[]? data, int targetShortSide)
    {
        if (data == null || data.Length < 100 || !IsValidImageData(data)) return null;
        try
        {
            using var ms = new MemoryStream(data);
            return Bitmap.DecodeToWidth(ms, targetShortSide);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to decode thumbnail bytes: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 通过文件头特征判断字节数据是否为受支持的图片格式（JPEG/PNG/BMP/TIFF）。
    /// </summary>
    private static bool IsValidImageData(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        if (data[0] == 0xFF && data[1] == 0xD8) return true; // JPEG
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
        if (data[0] == 0x42 && data[1] == 0x4D) return true; // BMP
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D)) return true; // TIFF
        return false;
    }
}
