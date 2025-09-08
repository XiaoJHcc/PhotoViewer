using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
            Console.WriteLine($"读取 EXIF 数据失败 ({filePath}): {ex.Message}");
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
                Console.WriteLine($"批量加载 EXIF 失败 ({imageFile.Name}): {ex.Message}");
            }
        });
        
        await Task.WhenAll(tasks);
    }
}
