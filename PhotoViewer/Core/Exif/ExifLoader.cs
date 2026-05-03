using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Xmp;

namespace PhotoViewer.Core;

/// <summary>
/// EXIF 数据加载编排器：读取 MetadataExtractor 目录并拼装 <see cref="ExifData"/>。
/// 具体子任务委派给同目录下的 <see cref="ExifMetadataGrouper"/>、<see cref="SonyMakernoteParser"/> 与 <see cref="ExifOrientation"/>。
/// </summary>
public static class ExifLoader
{
    /// <summary>
    /// 异步加载文件的完整 EXIF 数据（仅解析元数据，不解码像素）。
    /// </summary>
    public static async Task<ExifData?> LoadExifDataAsync(IStorageFile file)
    {
        var filePath = file.Path.LocalPath;

        try
        {
            var exifData = new ExifData { FilePath = filePath };

            using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            // RAW 文件（如 Sony ARW）可能有多个 SubIFD，选择包含拍摄参数的那一个
            var exifSubIfd = FindShootingExifSubIfd(directories);
            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var xmpDirectory = directories.OfType<XmpDirectory>().FirstOrDefault();

            if (exifSubIfd != null)
                PopulateFromExifSubIfd(exifSubIfd, exifData);

            if (exifIfd0 != null)
                PopulateFromIfd0(exifIfd0, exifData);

            var fileTypeDir = directories.OfType<FileTypeDirectory>().FirstOrDefault();
            if (fileTypeDir != null)
            {
                exifData.FileTypeName = fileTypeDir.GetDescription(FileTypeDirectory.TagDetectedFileTypeName);
                exifData.MimeType = fileTypeDir.GetDescription(FileTypeDirectory.TagDetectedFileMimeType);
            }

            if (xmpDirectory != null)
                exifData.Rating = ReadXmpRating(xmpDirectory, filePath);

            exifData.AllMetadata = ExifMetadataGrouper.ExtractAll(directories);

            var sonyMakernote = directories.OfType<SonyType1MakernoteDirectory>().FirstOrDefault();
            if (sonyMakernote != null)
            {
                exifData.SonyFocusPosition = SonyMakernoteParser.ParseFocusPosition(sonyMakernote);
                exifData.SonyFocusFrameSize = SonyMakernoteParser.ParseFocusFrameSize(sonyMakernote);
            }

            return exifData;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read EXIF data (" + filePath + "): " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 仅加载文件的星级评分（用于快速筛选，避免读取完整 EXIF）。
    /// </summary>
    public static async Task<int> LoadRatingOnlyAsync(IStorageFile file)
    {
        var filePath = file.Path.LocalPath;

        try
        {
            using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);
            var xmpDirectory = directories.OfType<XmpDirectory>().FirstOrDefault();
            return xmpDirectory != null ? ReadXmpRating(xmpDirectory, filePath) ?? 0 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read rating data (" + filePath + "): " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 仅尝试从 EXIF/IFD 读取图片尺寸，失败返回 null（不解码整图）。
    /// </summary>
    public static async Task<(int width, int height)?> TryGetDimensionsAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            var exifSubIfd = FindShootingExifSubIfd(directories);
            if (exifSubIfd != null &&
                exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w1) &&
                exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h1) &&
                w1 > 0 && h1 > 0)
            {
                return (w1, h1);
            }

            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (exifIfd0 != null &&
                exifIfd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var w2) &&
                exifIfd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var h2) &&
                w2 > 0 && h2 > 0)
            {
                return (w2, h2);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在多个 ExifSubIfdDirectory 中找到包含拍摄参数的那一个。
    /// RAW 文件（如 Sony ARW）通常有两个 SubIFD：第一个描述 RAW 图像结构，
    /// 第二个包含拍摄参数（ExposureTime、FNumber、ISO 等）。
    /// </summary>
    private static ExifSubIfdDirectory? FindShootingExifSubIfd(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
        if (subIfds.Count == 0) return null;
        if (subIfds.Count == 1) return subIfds[0];

        foreach (var subIfd in subIfds)
        {
            if (subIfd.ContainsTag(ExifDirectoryBase.TagExposureTime) ||
                subIfd.ContainsTag(ExifDirectoryBase.TagFNumber) ||
                subIfd.ContainsTag(ExifDirectoryBase.TagIsoEquivalent))
            {
                return subIfd;
            }
        }

        // 回退：返回标签数最多的（通常是拍摄参数目录）
        return subIfds.OrderByDescending(d => d.Tags.Count).First();
    }

    /// <summary>
    /// 从 ExifSubIFD 读取拍摄参数字段。
    /// </summary>
    private static void PopulateFromExifSubIfd(ExifSubIfdDirectory subIfd, ExifData exifData)
    {
        if (subIfd.TryGetRational(ExifDirectoryBase.TagFNumber, out var fNumber))
            exifData.Aperture = fNumber;

        if (subIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var exposureTime))
            exifData.ExposureTime = exposureTime;

        if (subIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
            exifData.Iso = iso;

        if (subIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focalLength))
            exifData.FocalLength = focalLength;

        if (subIfd.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthInt))
            exifData.EquivFocalLength = new Rational(equivFocalLengthInt, 1);
        else if (subIfd.TryGetRational(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthRational))
            exifData.EquivFocalLength = equivFocalLengthRational;

        if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTime))
            exifData.DateTimeOriginal = dateTime;

        if (subIfd.HasTagName(ExifDirectoryBase.TagLensModel))
            exifData.LensModel = subIfd.GetDescription(ExifDirectoryBase.TagLensModel);

        if (subIfd.TryGetRational(ExifDirectoryBase.TagExposureBias, out var exposureBias))
            exifData.ExposureBias = exposureBias;

        if (subIfd.HasTagName(ExifDirectoryBase.TagWhiteBalance))
        {
            var wb = subIfd.GetDescription(ExifDirectoryBase.TagWhiteBalance);
            // TagWhiteBalance (37384) 在部分相机上返回 "Unknown"，用 TagWhiteBalanceMode (41987) 补充
            if (string.Equals(wb, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                subIfd.HasTagName(ExifDirectoryBase.TagWhiteBalanceMode))
            {
                wb = subIfd.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode);
            }
            exifData.WhiteBalance = wb;
        }

        if (subIfd.HasTagName(ExifDirectoryBase.TagFlash))
            exifData.Flash = subIfd.GetDescription(ExifDirectoryBase.TagFlash);

        if (subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var width))
            exifData.ImageWidth = width;
        if (subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var height))
            exifData.ImageHeight = height;

        if (subIfd.HasTagName(ExifDirectoryBase.TagExposureProgram))
            exifData.ExposureProgram = subIfd.GetDescription(ExifDirectoryBase.TagExposureProgram);

        if (subIfd.HasTagName(ExifDirectoryBase.TagMeteringMode))
            exifData.MeteringMode = subIfd.GetDescription(ExifDirectoryBase.TagMeteringMode);

        if (subIfd.HasTagName(ExifDirectoryBase.TagColorSpace))
            exifData.ColorSpace = subIfd.GetDescription(ExifDirectoryBase.TagColorSpace);

        if (subIfd.TryGetRational(ExifDirectoryBase.TagMaxAperture, out var maxAperture))
            exifData.MaxAperture = maxAperture;

        if (subIfd.HasTagName(ExifDirectoryBase.TagLensSpecification))
            exifData.LensSpecification = subIfd.GetDescription(ExifDirectoryBase.TagLensSpecification);

        if (subIfd.HasTagName(ExifDirectoryBase.TagExposureMode))
            exifData.ExposureMode = subIfd.GetDescription(ExifDirectoryBase.TagExposureMode);

        if (subIfd.HasTagName(ExifDirectoryBase.TagWhiteBalanceMode))
            exifData.WhiteBalanceMode = subIfd.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode);

        if (subIfd.HasTagName(ExifDirectoryBase.TagSceneCaptureType))
            exifData.SceneCaptureType = subIfd.GetDescription(ExifDirectoryBase.TagSceneCaptureType);

        if (subIfd.HasTagName(ExifDirectoryBase.TagContrast))
            exifData.Contrast = subIfd.GetDescription(ExifDirectoryBase.TagContrast);

        if (subIfd.HasTagName(ExifDirectoryBase.TagSaturation))
            exifData.Saturation = subIfd.GetDescription(ExifDirectoryBase.TagSaturation);

        if (subIfd.HasTagName(ExifDirectoryBase.TagSharpness))
            exifData.Sharpness = subIfd.GetDescription(ExifDirectoryBase.TagSharpness);

        if (subIfd.TryGetRational(ExifDirectoryBase.TagDigitalZoomRatio, out var digitalZoomRatio))
            exifData.DigitalZoomRatio = digitalZoomRatio;

        if (subIfd.HasTagName(ExifDirectoryBase.TagFileSource))
            exifData.FileSource = subIfd.GetDescription(ExifDirectoryBase.TagFileSource);

        if (subIfd.HasTagName(ExifDirectoryBase.TagSceneType))
            exifData.SceneType = subIfd.GetDescription(ExifDirectoryBase.TagSceneType);

        if (subIfd.HasTagName(ExifDirectoryBase.TagExifVersion))
            exifData.ExifVersion = subIfd.GetDescription(ExifDirectoryBase.TagExifVersion);

        if (subIfd.TryGetRational(ExifDirectoryBase.TagBrightnessValue, out var brightness))
            exifData.BrightnessValue = brightness;

        if (subIfd.TryGetRational(ExifDirectoryBase.TagCompressedAverageBitsPerPixel, out var compressedBpp))
            exifData.CompressedBitsPerPixel = compressedBpp;

        if (subIfd.HasTagName(ExifDirectoryBase.TagSensitivityType))
            exifData.SensitivityType = subIfd.GetDescription(ExifDirectoryBase.TagSensitivityType);

        if (subIfd.HasTagName(ExifDirectoryBase.TagTimeZone))
            exifData.TimeZone = subIfd.GetDescription(ExifDirectoryBase.TagTimeZone);

        // SubSecTimeOriginal (tag 37521)
        const int TagSubSecTimeOriginal = 37521;
        if (subIfd.HasTagName(TagSubSecTimeOriginal))
            exifData.SubSecTimeOriginal = subIfd.GetString(TagSubSecTimeOriginal);
    }

    /// <summary>
    /// 从 IFD0 读取相机/方向/软件等字段。
    /// </summary>
    private static void PopulateFromIfd0(ExifIfd0Directory ifd0, ExifData exifData)
    {
        if (ifd0.HasTagName(ExifDirectoryBase.TagMake))
            exifData.CameraMake = ifd0.GetDescription(ExifDirectoryBase.TagMake);

        if (ifd0.HasTagName(ExifDirectoryBase.TagModel))
            exifData.CameraModel = ifd0.GetDescription(ExifDirectoryBase.TagModel);

        if (ifd0.HasTagName(ExifDirectoryBase.TagOrientation))
        {
            exifData.Orientation = ifd0.GetDescription(ExifDirectoryBase.TagOrientation);
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientationValue))
                exifData.OrientationValue = orientationValue;
        }

        if (ifd0.HasTagName(ExifDirectoryBase.TagSoftware))
            exifData.Software = ifd0.GetDescription(ExifDirectoryBase.TagSoftware);
    }

    /// <summary>
    /// 从 XMP 目录解析 Rating (0~5)，匹配失败时回退到 XmpCore 解析原始字符串。
    /// 任一路径解析到的值超出 [0, 5] 一律丢弃。
    /// </summary>
    private static int? ReadXmpRating(XmpDirectory xmpDirectory, string filePath)
    {
        try
        {
            var xmpMeta = xmpDirectory.GetXmpProperties();

            string[] ratingPaths = { "xmp:Rating", "http://ns.adobe.com/xap/1.0/:Rating", "Rating", "xap:Rating" };
            foreach (var path in ratingPaths)
            {
                if (xmpMeta.TryGetValue(path, out var ratingValue) &&
                    int.TryParse(ratingValue, out var rating) && rating >= 0 && rating <= 5)
                {
                    return rating;
                }
            }

            // 回退：用 XmpCore 解析原始字符串
            try
            {
                var xmpString = xmpDirectory.GetXmpProperties().ToString();
                if (!string.IsNullOrEmpty(xmpString))
                {
                    var parsedXmp = XmpCore.XmpMetaFactory.ParseFromString(xmpString);
                    var ratingProperty = parsedXmp.GetProperty("http://ns.adobe.com/xap/1.0/", "Rating");
                    if (ratingProperty != null && int.TryParse(ratingProperty.Value, out var parsedRating) &&
                        parsedRating >= 0 && parsedRating <= 5)
                    {
                        return parsedRating;
                    }
                }
            }
            catch (Exception xmpEx)
            {
                Console.WriteLine("Failed to parse XMP with XmpCore (" + filePath + "): " + xmpEx.Message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read XMP Rating (" + filePath + "): " + ex.Message);
        }

        return null;
    }
}
