using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoViewer.Core;

/// <summary>
/// 缩略图提取器：先尝试 EXIF 内嵌缩略图（极快、零解码），失败时回退到原图按宽度子采样解码。
/// </summary>
public static class ThumbnailExtractor
{
    /// <summary>缩略图目标宽度（像素）。EXIF 内嵌缩略图与原图回退路径共用。</summary>
    private const int TargetThumbnailWidth = 120;

    /// <summary>
    /// 尝试从 EXIF/IFD1 内嵌缩略图区域提取已编码的缩略图字节并解码为 Bitmap。
    /// 不存在或解析失败时返回 null。
    /// </summary>
    public static async Task<Bitmap?> TryLoadEmbeddedAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            // 1) 优先：标准 ExifThumbnailDirectory（即 IFD1 缩略图条目）。
            var exifThumbnailDirectory = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
            if (exifThumbnailDirectory != null)
            {
                var fromExifThumb = await TryReadFromExifThumbnailDirectoryAsync(file, exifThumbnailDirectory);
                if (fromExifThumb != null) return fromExifThumb;
            }

            // 2) 回退：遍历名称中包含 "preview" / "thumbnail" 的厂商目录（如 Sony PreviewImage）。
            foreach (var directory in directories)
            {
                var lname = directory.GetType().Name.ToLowerInvariant();
                if (!(lname.Contains("preview") || lname.Contains("thumbnail")))
                    continue;
                if (ReferenceEquals(directory, exifThumbnailDirectory)) continue;

                foreach (var tag in directory.Tags)
                {
                    try
                    {
                        var data = directory.GetByteArray(tag.Type);
                        if (data?.Length > 100 && IsValidImageData(data))
                        {
                            var thumb = CreateFromData(data);
                            if (thumb != null) return thumb;
                        }
                    }
                    catch
                    {
                        // 当前 tag 不是字节数组或读取异常，尝试下一个
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to extract embedded thumbnail (" + file.Name + "): " + ex.Message);
        }

        return null;
    }

    /// <summary>
    /// 由原图通过 Avalonia 解码时子采样得到缩略图（适用于无内嵌缩略图的常规位图）。
    /// </summary>
    public static async Task<Bitmap?> GenerateFromImageAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            // Avalonia 内置：解码时子采样到目标宽度，避免先全图解码再缩放的内存与耗时。
            return Bitmap.DecodeToWidth(stream, TargetThumbnailWidth);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to generate thumbnail from image (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从 ExifThumbnailDirectory 读取嵌入缩略图：先按已知字节数组 tag 读取，
    /// 再按 (Offset 0x0201, Length 0x0202) 二阶段从原文件偏移读取兜底。
    /// </summary>
    private static async Task<Bitmap?> TryReadFromExifThumbnailDirectoryAsync(IStorageFile file, ExifThumbnailDirectory dir)
    {
        // 读取常见的 thumbnail 字节 tag
        int[] thumbnailTags = { 0x0201, 0x0202, 0x0103 };
        foreach (var tag in thumbnailTags)
        {
            if (!dir.HasTagName(tag)) continue;
            try
            {
                var data = dir.GetByteArray(tag);
                if (data?.Length > 100 && IsValidImageData(data))
                {
                    var thumb = CreateFromData(data);
                    if (thumb != null) return thumb;
                }
            }
            catch
            {
                // 当前 tag 不是字节数组，继续尝试
            }
        }

        // 通过偏移量和长度从原文件读取
        if (dir.HasTagName(0x0201) && dir.HasTagName(0x0202))
        {
            try
            {
                var offset = dir.GetInt32(0x0201);
                var length = dir.GetInt32(0x0202);

                if (offset > 0 && length > 100)
                {
                    await using var thumbnailStream = await file.OpenReadAsync();
                    thumbnailStream.Seek(offset, SeekOrigin.Begin);

                    var thumbnailData = new byte[length];
                    var bytesRead = await thumbnailStream.ReadAsync(thumbnailData, 0, length);

                    if (bytesRead == length && IsValidImageData(thumbnailData))
                    {
                        var thumb = CreateFromData(thumbnailData);
                        if (thumb != null) return thumb;
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
    /// 通过文件头特征判断字节数据是否为受支持的图片格式（JPEG/PNG/BMP/TIFF）。
    /// </summary>
    private static bool IsValidImageData(byte[] data)
    {
        if (data == null || data.Length < 4) return false;

        // JPEG (FF D8)
        if (data[0] == 0xFF && data[1] == 0xD8) return true;
        // PNG (89 50 4E 47)
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
        // BMP (42 4D)
        if (data[0] == 0x42 && data[1] == 0x4D) return true;
        // TIFF (49 49 或 4D 4D)
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D)) return true;

        return false;
    }

    /// <summary>
    /// 将一段已编码的图片字节数据解码为目标宽度的 Bitmap。
    /// </summary>
    private static Bitmap? CreateFromData(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            return Bitmap.DecodeToWidth(ms, TargetThumbnailWidth);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create thumbnail from data: " + ex.Message);
            return null;
        }
    }
}
