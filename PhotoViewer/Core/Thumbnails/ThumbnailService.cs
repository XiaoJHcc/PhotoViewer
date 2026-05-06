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
/// 2) <see cref="GetThumbnailAsync"/>：取一个不低于指定短边的缩略图并缩放到目标尺寸。
/// 内部对 JPEG/TIFF 类容器扫描 EXIF/IFD1 缩略图与厂商 PreviewImage；HEIF 容器分派给 <see cref="HeifLoader"/>。
/// </summary>
public static class ThumbnailService
{
    /// <summary>
    /// 列出该文件所有可用的缩略图来源（按解码代价升序：内嵌缩略图 → PreviewImage → 全图回退）。
    /// 不进行实际位图解码；仅做必要的元数据/容器扫描。来源尺寸未知时 <see cref="ThumbnailSource.Width"/> / <see cref="ThumbnailSource.Height"/> 为 0。
    /// </summary>
    public static async Task<IReadOnlyList<ThumbnailSource>> GetAvailableSourcesAsync(IStorageFile file)
    {
        if (file == null) return Array.Empty<ThumbnailSource>();

        if (HeifLoader.IsHeifFile(file))
        {
            // HEIF：平台解码器内部已在多个内嵌条目中按尺寸择优；这里只暴露一个聚合来源 + 全图回退。
            return new ThumbnailSource[]
            {
                new(width: 0, height: 0, origin: ThumbnailOrigin.HeifEmbedded,
                    loaderAsync: async target => await HeifLoader.LoadHeifThumbnailAsync(file, target)),
                new(width: 0, height: 0, origin: ThumbnailOrigin.FullImage,
                    loaderAsync: async _ => await HeifLoader.LoadHeifBitmapAsync(file)),
            };
        }

        return await EnumerateContainerSourcesAsync(file);
    }

    /// <summary>
    /// 取一个短边不低于 <paramref name="minShortSide"/> 的缩略图并缩放到该短边附近。
    /// 选源策略：按 <see cref="GetAvailableSourcesAsync"/> 顺序逐个尝试解码，
    /// 取首个成功且解码后短边 ≥ <paramref name="minShortSide"/> 的位图；
    /// 若没有任何来源满足"短边足够"，则返回最后一个成功解码出的位图（保证总是返回一个能用的结果）。
    /// </summary>
    public static async Task<Bitmap?> GetThumbnailAsync(IStorageFile file, int minShortSide)
    {
        if (file == null || minShortSide <= 0) return null;

        var sources = await GetAvailableSourcesAsync(file);
        Bitmap? lastDecoded = null;

        foreach (var src in sources)
        {
            try
            {
                var bmp = await src.LoaderAsync(minShortSide);
                if (bmp == null) continue;

                var shortSide = Math.Min(bmp.PixelSize.Width, bmp.PixelSize.Height);
                if (shortSide >= minShortSide)
                {
                    lastDecoded?.Dispose();
                    return bmp;
                }

                // 当前来源解码出的位图短边偏小，留作兜底，继续尝试更高质量的来源
                lastDecoded?.Dispose();
                lastDecoded = bmp;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ThumbnailService: source " + src.Origin + " failed for " + file.Name + ": " + ex.Message);
            }
        }

        return lastDecoded;
    }

    // ============================================================
    // 以下为 JPEG/TIFF 等非 HEIF 容器的来源枚举与字节解码实现
    // ============================================================

    /// <summary>
    /// 枚举一张文件中可用的非-HEIF 缩略图来源（包括 EXIF/IFD1 缩略图、厂商 PreviewImage、以及保底的全图来源）。
    /// 不进行真正的位图解码；仅读取容器元数据与字节范围。
    /// </summary>
    private static async Task<IReadOnlyList<ThumbnailSource>> EnumerateContainerSourcesAsync(IStorageFile file)
    {
        var result = new List<ThumbnailSource>();

        try
        {
            await using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            // 1) 标准 EXIF/IFD1 缩略图条目
            var exifThumbDir = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
            if (exifThumbDir != null)
            {
                var capturedDir = exifThumbDir;
                result.Add(new ThumbnailSource(
                    width: 0, height: 0, origin: ThumbnailOrigin.ExifEmbedded,
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
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to enumerate thumbnail sources (" + file.Name + "): " + ex.Message);
        }

        // 3) 保底：原图全图解码（尺寸未知；放在列表最后）
        result.Add(new ThumbnailSource(
            width: 0, height: 0, origin: ThumbnailOrigin.FullImage,
            loaderAsync: target => GenerateFromFullImageAsync(file, target)));

        return result;
    }

    /// <summary>
    /// 在厂商 Preview/Thumbnail 类目录中,把任何"看起来是合法图像字节"的 tag 列为一个 <see cref="ThumbnailSource"/>。
    /// </summary>
    private static void AppendMakernoteSources(MetadataExtractor.Directory dir, List<ThumbnailSource> sink)
    {
        foreach (var tag in dir.Tags)
        {
            try
            {
                var data = dir.GetByteArray(tag.Type);
                if (data == null || data.Length <= 100 || !IsValidImageData(data)) continue;

                var capturedDir = dir;
                var capturedTagType = tag.Type;
                sink.Add(new ThumbnailSource(
                    width: 0, height: 0, origin: ThumbnailOrigin.MakernotePreview,
                    loaderAsync: target => Task.FromResult(DecodeBytesToShortSide(capturedDir.GetByteArray(capturedTagType), target))));
            }
            catch
            {
                // 当前 tag 不是字节数组,跳过
            }
        }
    }

    /// <summary>
    /// 从 ExifThumbnailDirectory 读取嵌入缩略图字节并解码:
    /// 先尝试常见字节 tag,再回退按 (Offset 0x0201, Length 0x0202) 从原文件偏移读取。
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
                // 当前 tag 不是字节数组,继续
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
    /// 按目标短边对原图做子采样解码,得到接近目标尺寸的位图。
    /// </summary>
    private static async Task<Bitmap?> GenerateFromFullImageAsync(IStorageFile file, int targetShortSide)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            // Avalonia 仅提供按宽度的子采样接口；FullImage 是最后兜底，宁可略高也接受。
            return Bitmap.DecodeToWidth(stream, targetShortSide);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to generate thumbnail from image (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 把已编码的图片字节解码为目标短边的位图。
    /// 由于 Avalonia 仅有 DecodeToWidth,这里近似用 target 作为目标宽度;
    /// 内嵌缩略图通常很小(~160px),解码到 target 不会先放大,所以多数情况精度足够。
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
    /// 通过文件头特征判断字节数据是否为受支持的图片格式(JPEG/PNG/BMP/TIFF)。
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
