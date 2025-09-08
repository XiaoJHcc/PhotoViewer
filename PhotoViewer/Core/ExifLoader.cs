using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoViewer.Core;

namespace PhotoViewer.Core;

/// <summary>
/// 单个文件的 EXIF 数据
/// </summary>
public class ExifData
{
    public string FilePath { get; set; } = "";
    
    // 原始数据类型，保持精度
    public Rational? Aperture { get; set; }
    public Rational? ExposureTime { get; set; }
    public int? Iso { get; set; }
    public Rational? FocalLength { get; set; }
    public Rational? EquivFocalLength { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public DateTime? DateTimeOriginal { get; set; }
    public string? LensModel { get; set; }
    
    // 图片尺寸信息
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    
    // 其他常用信息
    public string? Orientation { get; set; }
    public Rational? ExposureBias { get; set; }
    public string? WhiteBalance { get; set; }
    public string? Flash { get; set; }
}

/// <summary>
/// EXIF 数据加载器（静态工具类）
/// </summary>
public static class ExifLoader
{
    /// <summary>
    /// 异步加载文件的 EXIF 数据（仅读取 EXIF，不加载图片数据）
    /// </summary>
    public static async Task<ExifData?> LoadExifDataAsync(IStorageFile file)
    {
        var filePath = file.Path.LocalPath;
        
        try
        {
            var exifData = new ExifData { FilePath = filePath };
            
            // 只读取 EXIF 数据，不解码图片内容
            using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            var exifSubIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

            if (exifSubIfd != null)
            {
                // 光圈值 (原始 Rational)
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagFNumber, out var fNumber))
                {
                    exifData.Aperture = fNumber;
                }

                // 快门速度 (原始 Rational)
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var exposureTime))
                {
                    exifData.ExposureTime = exposureTime;
                }

                // ISO 感光度
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
                {
                    exifData.Iso = iso;
                }

                // 实际焦距 (原始 Rational)
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focalLength))
                {
                    exifData.FocalLength = focalLength;
                }
                
                // 35mm 等效焦距 - 修复获取方式
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthInt))
                {
                    exifData.EquivFocalLength = new Rational(equivFocalLengthInt, 1);
                }
                else if (exifSubIfd.TryGetRational(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthRational))
                {
                    exifData.EquivFocalLength = equivFocalLengthRational;
                }

                // 拍摄时间
                if (exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTime))
                {
                    exifData.DateTimeOriginal = dateTime;
                }

                // 镜头信息
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagLensModel))
                {
                    exifData.LensModel = exifSubIfd.GetDescription(ExifDirectoryBase.TagLensModel);
                }
                
                // 曝光补偿
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagExposureBias, out var exposureBias))
                {
                    exifData.ExposureBias = exposureBias;
                }
                
                // 白平衡
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagWhiteBalance))
                {
                    exifData.WhiteBalance = exifSubIfd.GetDescription(ExifDirectoryBase.TagWhiteBalance);
                }
                
                // 闪光灯
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagFlash))
                {
                    exifData.Flash = exifSubIfd.GetDescription(ExifDirectoryBase.TagFlash);
                }
                
                // 图片尺寸
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var width))
                {
                    exifData.ImageWidth = width;
                }
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var height))
                {
                    exifData.ImageHeight = height;
                }
            }

            if (exifIfd0 != null)
            {
                // 相机制造商
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagMake))
                {
                    exifData.CameraMake = exifIfd0.GetDescription(ExifDirectoryBase.TagMake);
                }

                // 相机型号
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagModel))
                {
                    exifData.CameraModel = exifIfd0.GetDescription(ExifDirectoryBase.TagModel);
                }
                
                // 方向
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagOrientation))
                {
                    exifData.Orientation = exifIfd0.GetDescription(ExifDirectoryBase.TagOrientation);
                }
            }
            
            return exifData;
        }
        catch (Exception ex)
        {
            Console.WriteLine("读取 EXIF 数据失败 (" + filePath + "): " + ex.Message);
            return null;
        }
    }
    
    /// <summary>
    /// 批量加载文件夹内所有图片的 EXIF 数据
    /// </summary>
    public static async Task LoadFolderExifDataAsync(IEnumerable<ImageFile> imageFiles)
    {
        var tasks = imageFiles.Select(async imageFile =>
        {
            try
            {
                await imageFile.LoadExifDataAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("批量加载 EXIF 失败 (" + imageFile.Name + "): " + ex.Message);
            }
        });
        
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 尝试从EXIF中加载嵌入式缩略图
    /// </summary>
    public static async Task<Bitmap?> TryLoadExifThumbnailAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);
            
            // 方法1: 查找ExifThumbnailDirectory并尝试不同的标签
            var exifThumbnailDirectory = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
            if (exifThumbnailDirectory != null)
            {
                try
                {
                    // 尝试常见的缩略图标签
                    var thumbnailTags = new int[]
                    {
                        0x0201, // JPEG Interchange Format (thumbnail offset)
                        0x0202, // JPEG Interchange Format Length (thumbnail length)
                        0x0103, // Compression
                        0x0201, // Thumbnail data
                    };

                    foreach (var tag in thumbnailTags)
                    {
                        if (exifThumbnailDirectory.HasTagName(tag))
                        {
                            try
                            {
                                var data = exifThumbnailDirectory.GetByteArray(tag);
                                if (data?.Length > 100)
                                {
                                    // 检查是否是有效的图片数据
                                    if (IsValidImageData(data))
                                    {
                                        var thumbnail = await CreateThumbnailFromDataAsync(data);
                                        if (thumbnail != null)
                                        {
                                            return thumbnail;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                    
                    // 尝试通过偏移量和长度获取缩略图
                    if (exifThumbnailDirectory.HasTagName(0x0201) && exifThumbnailDirectory.HasTagName(0x0202))
                    {
                        try
                        {
                            var offset = exifThumbnailDirectory.GetInt32(0x0201);
                            var length = exifThumbnailDirectory.GetInt32(0x0202);
                            
                            if (offset > 0 && length > 100)
                            {
                                // 重新读取文件以获取缩略图数据
                                await using var thumbnailStream = await file.OpenReadAsync();
                                thumbnailStream.Seek(offset, SeekOrigin.Begin);
                                
                                var thumbnailData = new byte[length];
                                var bytesRead = await thumbnailStream.ReadAsync(thumbnailData, 0, length);
                                
                                if (bytesRead == length && IsValidImageData(thumbnailData))
                                {
                                    var thumbnail = await CreateThumbnailFromDataAsync(thumbnailData);
                                    if (thumbnail != null)
                                    {
                                        return thumbnail;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("通过偏移量提取缩略图失败 (" + file.Name + "): " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("从ExifThumbnailDirectory提取缩略图失败 (" + file.Name + "): " + ex.Message);
                }
            }
            
            // 方法2: 遍历所有目录寻找可能的缩略图数据
            foreach (var directory in directories)
            {
                var directoryName = directory.GetType().Name.ToLower();
                if (directoryName.Contains("thumbnail") || directoryName.Contains("preview") || directoryName.Contains("ifd1"))
                {
                    try
                    {
                        foreach (var tag in directory.Tags)
                        {
                            try
                            {
                                var data = directory.GetByteArray(tag.Type);
                                if (data?.Length > 100 && IsValidImageData(data))
                                {
                                    var thumbnail = await CreateThumbnailFromDataAsync(data);
                                    if (thumbnail != null)
                                    {
                                        return thumbnail;
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("从目录 " + directoryName + " 提取缩略图失败: " + ex.Message);
                    }
                }
            }
            
            // 方法3: 查找Exif IFD1 (通常包含缩略图)
            var exifIfd1 = directories.Where(d => d.GetType().Name.Contains("Ifd1") || d.GetType().Name.Contains("IFD1")).FirstOrDefault();
            if (exifIfd1 != null)
            {
                try
                {
                    // IFD1通常包含缩略图信息
                    var thumbnailTags = new int[] { 0x0201, 0x0202, 0x0103, 0x0100, 0x0101 };
                    
                    foreach (var tag in thumbnailTags)
                    {
                        if (exifIfd1.HasTagName(tag))
                        {
                            try
                            {
                                var data = exifIfd1.GetByteArray(tag);
                                if (data?.Length > 100 && IsValidImageData(data))
                                {
                                    var thumbnail = await CreateThumbnailFromDataAsync(data);
                                    if (thumbnail != null)
                                    {
                                        return thumbnail;
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("从IFD1提取缩略图失败 (" + file.Name + "): " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("提取EXIF缩略图失败 (" + file.Name + "): " + ex.Message);
        }
        
        return null;
    }

    /// <summary>
    /// 检查数据是否是有效的图片数据
    /// </summary>
    public static bool IsValidImageData(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        
        // 检查JPEG文件头 (FF D8)
        if (data[0] == 0xFF && data[1] == 0xD8) return true;
        
        // 检查PNG文件头 (89 50 4E 47)
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
        
        // 检查BMP文件头 (42 4D)
        if (data[0] == 0x42 && data[1] == 0x4D) return true;
        
        // 检查TIFF文件头 (49 49 或 4D 4D)
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D)) return true;
        
        return false;
    }

    /// <summary>
    /// 从字节数据创建缩略图
    /// </summary>
    public static async Task<Bitmap?> CreateThumbnailFromDataAsync(byte[] data)
    {
        try
        {
            using var thumbnailStream = new MemoryStream(data);
            var thumbnail = new Bitmap(thumbnailStream);
            
            // 如果EXIF缩略图过大，则缩放到合适大小
            if (thumbnail.PixelSize.Width > 120)
            {
                var scale = 120.0 / thumbnail.PixelSize.Width;
                var newSize = new PixelSize(
                    (int)(thumbnail.PixelSize.Width * scale),
                    (int)(thumbnail.PixelSize.Height * scale)
                );
                var scaledThumbnail = thumbnail.CreateScaledBitmap(newSize);
                thumbnail.Dispose();
                return scaledThumbnail;
            }
            
            return thumbnail;
        }
        catch (Exception ex)
        {
            Console.WriteLine("从数据创建缩略图失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从原图生成缩略图
    /// </summary>
    public static async Task<Bitmap?> GenerateThumbnailFromImageAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var originalBitmap = new Bitmap(stream);
            
            // 生成缩略图 (120px宽度)
            if (originalBitmap.PixelSize.Width > 120)
            {
                var scale = 120.0 / originalBitmap.PixelSize.Width;
                var newSize = new PixelSize(
                    (int)(originalBitmap.PixelSize.Width * scale),
                    (int)(originalBitmap.PixelSize.Height * scale)
                );
                var scaledBitmap = originalBitmap.CreateScaledBitmap(newSize);
                originalBitmap.Dispose();
                return scaledBitmap;
            }
            
            return originalBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine("从原图生成缩略图失败 (" + file.Name + "): " + ex.Message);
            return null;
        }
    }
}
