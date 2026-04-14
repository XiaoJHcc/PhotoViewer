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
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Xmp;
using PhotoViewer.Core;

namespace PhotoViewer.Core;

/// <summary>
/// 元数据标签（目录中的一个字段）
/// </summary>
public class MetadataTag
{
    public int TagId { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// 元数据分组（对应 EXIF 目录，如 IFD0、ExifIFD、XMP 等）
/// </summary>
public class MetadataGroup
{
    public string Name { get; set; } = "";
    public List<MetadataTag> Tags { get; set; } = new();
}

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
    
    /// <summary>
    /// 图片方向（EXIF Orientation 数值）
    /// 1=正常, 3=180度旋转, 6=顺时针90度, 8=逆时针90度
    /// </summary>
    public int OrientationValue { get; set; } = 1;
    
    public Rational? ExposureBias { get; set; }
    public string? WhiteBalance { get; set; }
    public string? Flash { get; set; }
    
    /// <summary>
    /// XMP Rating 评分 (0-5)
    /// </summary>
    public int? Rating { get; set; }
    
    // ---- 新增：扩展拍摄参数 ----
    
    /// <summary>曝光程序（如 Aperture-priority AE）</summary>
    public string? ExposureProgram { get; set; }
    
    /// <summary>测光模式（如 Multi-segment）</summary>
    public string? MeteringMode { get; set; }
    
    /// <summary>色彩空间（如 sRGB）</summary>
    public string? ColorSpace { get; set; }
    
    /// <summary>最大光圈</summary>
    public Rational? MaxAperture { get; set; }
    
    /// <summary>镜头规格（如 28-75mm f/2.8）</summary>
    public string? LensSpecification { get; set; }
    
    /// <summary>曝光模式（如 Auto exposure）</summary>
    public string? ExposureMode { get; set; }
    
    /// <summary>白平衡模式（如 Auto white balance）</summary>
    public string? WhiteBalanceMode { get; set; }
    
    /// <summary>场景拍摄类型（如 Standard）</summary>
    public string? SceneCaptureType { get; set; }
    
    /// <summary>对比度</summary>
    public string? Contrast { get; set; }
    
    /// <summary>饱和度</summary>
    public string? Saturation { get; set; }
    
    /// <summary>锐度</summary>
    public string? Sharpness { get; set; }
    
    /// <summary>数码变焦比</summary>
    public Rational? DigitalZoomRatio { get; set; }
    
    /// <summary>文件来源（如 Digital Still Camera）</summary>
    public string? FileSource { get; set; }
    
    /// <summary>场景类型</summary>
    public string? SceneType { get; set; }
    
    /// <summary>Exif 版本（如 2.32）</summary>
    public string? ExifVersion { get; set; }
    
    /// <summary>亮度值</summary>
    public Rational? BrightnessValue { get; set; }
    
    /// <summary>压缩位/像素</summary>
    public Rational? CompressedBitsPerPixel { get; set; }
    
    /// <summary>软件版本</summary>
    public string? Software { get; set; }
    
    /// <summary>感光度类型</summary>
    public string? SensitivityType { get; set; }
    
    /// <summary>时区偏移</summary>
    public string? TimeZone { get; set; }
    
    /// <summary>亚秒时间（用于精确到毫秒的时间戳）</summary>
    public string? SubSecTimeOriginal { get; set; }
    
    /// <summary>文件类型名称（如 ARW、JPEG 等）</summary>
    public string? FileTypeName { get; set; }
    
    /// <summary>检测到的 MIME 类型</summary>
    public string? MimeType { get; set; }
    
    // ---- 全量元数据（按目录分组）----
    
    /// <summary>
    /// 所有元数据分组列表，包含文件中的全部 EXIF/XMP/Makernote 等信息
    /// </summary>
    public List<MetadataGroup> AllMetadata { get; set; } = new();
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

            // 找到包含拍摄参数的 ExifSubIFD（ARW 等 RAW 文件可能有多个 SubIFD，
            // 第一个往往是 RAW 图像结构信息，拍摄参数在后续的 SubIFD 中）
            var exifSubIfd = FindShootingExifSubIfd(directories);
            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var xmpDirectory = directories.OfType<XmpDirectory>().FirstOrDefault();

            // ---- 从 ExifSubIFD（拍摄参数）读取 ----
            if (exifSubIfd != null)
            {
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagFNumber, out var fNumber))
                    exifData.Aperture = fNumber;

                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var exposureTime))
                    exifData.ExposureTime = exposureTime;

                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
                    exifData.Iso = iso;

                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focalLength))
                    exifData.FocalLength = focalLength;
                
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthInt))
                    exifData.EquivFocalLength = new Rational(equivFocalLengthInt, 1);
                else if (exifSubIfd.TryGetRational(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var equivFocalLengthRational))
                    exifData.EquivFocalLength = equivFocalLengthRational;

                if (exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTime))
                    exifData.DateTimeOriginal = dateTime;

                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagLensModel))
                    exifData.LensModel = exifSubIfd.GetDescription(ExifDirectoryBase.TagLensModel);
                
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagExposureBias, out var exposureBias))
                    exifData.ExposureBias = exposureBias;
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagWhiteBalance))
                {
                    var wb = exifSubIfd.GetDescription(ExifDirectoryBase.TagWhiteBalance);
                    // TagWhiteBalance (37384) 在部分相机上返回 "Unknown"，用 TagWhiteBalanceMode (41987) 补充
                    if (string.Equals(wb, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                        exifSubIfd.HasTagName(ExifDirectoryBase.TagWhiteBalanceMode))
                    {
                        wb = exifSubIfd.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode);
                    }
                    exifData.WhiteBalance = wb;
                }
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagFlash))
                    exifData.Flash = exifSubIfd.GetDescription(ExifDirectoryBase.TagFlash);
                
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var width))
                    exifData.ImageWidth = width;
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var height))
                    exifData.ImageHeight = height;

                // ---- 新增：扩展拍摄参数 ----
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagExposureProgram))
                    exifData.ExposureProgram = exifSubIfd.GetDescription(ExifDirectoryBase.TagExposureProgram);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagMeteringMode))
                    exifData.MeteringMode = exifSubIfd.GetDescription(ExifDirectoryBase.TagMeteringMode);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagColorSpace))
                    exifData.ColorSpace = exifSubIfd.GetDescription(ExifDirectoryBase.TagColorSpace);
                
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagMaxAperture, out var maxAperture))
                    exifData.MaxAperture = maxAperture;
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagLensSpecification))
                    exifData.LensSpecification = exifSubIfd.GetDescription(ExifDirectoryBase.TagLensSpecification);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagExposureMode))
                    exifData.ExposureMode = exifSubIfd.GetDescription(ExifDirectoryBase.TagExposureMode);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagWhiteBalanceMode))
                    exifData.WhiteBalanceMode = exifSubIfd.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagSceneCaptureType))
                    exifData.SceneCaptureType = exifSubIfd.GetDescription(ExifDirectoryBase.TagSceneCaptureType);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagContrast))
                    exifData.Contrast = exifSubIfd.GetDescription(ExifDirectoryBase.TagContrast);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagSaturation))
                    exifData.Saturation = exifSubIfd.GetDescription(ExifDirectoryBase.TagSaturation);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagSharpness))
                    exifData.Sharpness = exifSubIfd.GetDescription(ExifDirectoryBase.TagSharpness);
                
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagDigitalZoomRatio, out var digitalZoomRatio))
                    exifData.DigitalZoomRatio = digitalZoomRatio;
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagFileSource))
                    exifData.FileSource = exifSubIfd.GetDescription(ExifDirectoryBase.TagFileSource);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagSceneType))
                    exifData.SceneType = exifSubIfd.GetDescription(ExifDirectoryBase.TagSceneType);
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagExifVersion))
                    exifData.ExifVersion = exifSubIfd.GetDescription(ExifDirectoryBase.TagExifVersion);
                
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagBrightnessValue, out var brightness))
                    exifData.BrightnessValue = brightness;
                
                if (exifSubIfd.TryGetRational(ExifDirectoryBase.TagCompressedAverageBitsPerPixel, out var compressedBpp))
                    exifData.CompressedBitsPerPixel = compressedBpp;
                
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagSensitivityType))
                    exifData.SensitivityType = exifSubIfd.GetDescription(ExifDirectoryBase.TagSensitivityType);
                
                // 时区与亚秒时间
                if (exifSubIfd.HasTagName(ExifDirectoryBase.TagTimeZone))
                    exifData.TimeZone = exifSubIfd.GetDescription(ExifDirectoryBase.TagTimeZone);
                
                // SubSecTimeOriginal (tag 37521)
                const int TagSubSecTimeOriginal = 37521;
                if (exifSubIfd.HasTagName(TagSubSecTimeOriginal))
                    exifData.SubSecTimeOriginal = exifSubIfd.GetString(TagSubSecTimeOriginal);
            }

            // ---- 从 IFD0 读取 ----
            if (exifIfd0 != null)
            {
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagMake))
                    exifData.CameraMake = exifIfd0.GetDescription(ExifDirectoryBase.TagMake);

                if (exifIfd0.HasTagName(ExifDirectoryBase.TagModel))
                    exifData.CameraModel = exifIfd0.GetDescription(ExifDirectoryBase.TagModel);
                
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagOrientation))
                {
                    exifData.Orientation = exifIfd0.GetDescription(ExifDirectoryBase.TagOrientation);
                    if (exifIfd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientationValue))
                        exifData.OrientationValue = orientationValue;
                }
                
                if (exifIfd0.HasTagName(ExifDirectoryBase.TagSoftware))
                    exifData.Software = exifIfd0.GetDescription(ExifDirectoryBase.TagSoftware);
            }
            
            // ---- 读取文件类型信息 ----
            var fileTypeDir = directories.OfType<MetadataExtractor.Formats.FileType.FileTypeDirectory>().FirstOrDefault();
            if (fileTypeDir != null)
            {
                exifData.FileTypeName = fileTypeDir.GetDescription(MetadataExtractor.Formats.FileType.FileTypeDirectory.TagDetectedFileTypeName);
                exifData.MimeType = fileTypeDir.GetDescription(MetadataExtractor.Formats.FileType.FileTypeDirectory.TagDetectedFileMimeType);
            }
            
            // ---- 读取 XMP 数据 ----
            if (xmpDirectory != null)
            {
                try
                {
                    var xmpMeta = xmpDirectory.GetXmpProperties();
                    
                    var ratingPaths = new[] { "xmp:Rating", "http://ns.adobe.com/xap/1.0/:Rating", "Rating", "xap:Rating" };
                    foreach (var path in ratingPaths)
                    {
                        if (xmpMeta.ContainsKey(path))
                        {
                            var ratingValue = xmpMeta[path];
                            if (int.TryParse(ratingValue, out var rating) && rating >= 0 && rating <= 5)
                            {
                                exifData.Rating = rating;
                                break;
                            }
                        }
                    }
                    
                    if (!exifData.Rating.HasValue)
                    {
                        try
                        {
                            var xmpString = xmpDirectory.GetXmpProperties().ToString();
                            if (!string.IsNullOrEmpty(xmpString))
                            {
                                var parsedXmp = XmpCore.XmpMetaFactory.ParseFromString(xmpString);
                                var ratingProperty = parsedXmp.GetProperty("http://ns.adobe.com/xap/1.0/", "Rating");
                                if (ratingProperty != null && int.TryParse(ratingProperty.Value, out var parsedRating) && parsedRating >= 0 && parsedRating <= 5)
                                {
                                    exifData.Rating = parsedRating;
                                }
                            }
                        }
                        catch (Exception xmpEx)
                        {
                            Console.WriteLine("Failed to parse XMP with XmpCore (" + filePath + "): " + xmpEx.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read XMP Rating (" + filePath + "): " + ex.Message);
                }
            }
            
            // ---- 提取全量元数据（按目录分组）----
            exifData.AllMetadata = ExtractAllMetadata(directories);
            
            return exifData;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read EXIF data (" + filePath + "): " + ex.Message);
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
        
        // 优先选择包含 ExposureTime 或 FNumber 标签的 SubIFD
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
    /// 提取所有目录中的全部标签，按目录名分组
    /// </summary>
    private static List<MetadataGroup> ExtractAllMetadata(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var groups = new List<MetadataGroup>();
        
        // 从 IFD0 中提取相机品牌，供后续匹配厂商 tag 名称时作为 fallback
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var cameraMake = ifd0?.GetDescription(ExifDirectoryBase.TagMake);
        
        foreach (var directory in directories)
        {
            // XMP 目录特殊处理：展开 XMP 属性而非原始 tag
            if (directory is XmpDirectory xmpDir)
            {
                try
                {
                    var xmpProps = xmpDir.GetXmpProperties();
                    if (xmpProps.Count > 0)
                    {
                        // 按 XMP 命名空间前缀分组（如 xmp:Rating -> "XMP-xmp", xmpMM:DocumentID -> "XMP-xmpMM"）
                        var nsByPrefix = new Dictionary<string, List<MetadataTag>>();
                        foreach (var kv in xmpProps)
                        {
                            var prefix = "XMP";
                            var colonIdx = kv.Key.IndexOf(':');
                            if (colonIdx > 0)
                                prefix = "XMP-" + kv.Key[..colonIdx];
                            
                            if (!nsByPrefix.TryGetValue(prefix, out var tagList))
                            {
                                tagList = new List<MetadataTag>();
                                nsByPrefix[prefix] = tagList;
                            }
                            tagList.Add(new MetadataTag
                            {
                                Name = kv.Key,
                                Value = kv.Value ?? ""
                            });
                        }
                        foreach (var ns in nsByPrefix.OrderBy(n => n.Key))
                        {
                            groups.Add(new MetadataGroup { Name = ns.Key, Tags = ns.Value });
                        }
                    }
                }
                catch
                {
                    // XMP 解析失败时回退到原始 tag 显示
                    groups.Add(ExtractDirectoryGroup(directory, cameraMake));
                }
                continue;
            }
            
            groups.Add(ExtractDirectoryGroup(directory, cameraMake));
        }
        
        return groups;
    }
    
    /// <summary>
    /// 将单个 MetadataExtractor 目录转换为 MetadataGroup
    /// </summary>
    /// <param name="directory">MetadataExtractor 目录</param>
    /// <param name="cameraMake">相机品牌（用于在通用 EXIF 目录中 fallback 查找厂商 tag 名称）</param>
    private static MetadataGroup ExtractDirectoryGroup(MetadataExtractor.Directory directory, string? cameraMake = null)
    {
        var group = new MetadataGroup { Name = directory.Name };
        foreach (var tag in directory.Tags)
        {
            string desc;
            try { desc = tag.Description ?? ""; } catch { desc = ""; }
            
            // 跳过超长的二进制数据值
            if (desc.Length > 200)
                desc = desc[..200] + "...";
            
            // 对 MetadataExtractor 未能识别的 tag，尝试从 ExifTool 数据库补充名称
            var tagName = tag.Name;
            if (tagName.StartsWith("Unknown tag", StringComparison.Ordinal))
            {
                var supplemental = ExifToolTags.GetSupplementalName(directory.Name, tag.Type, cameraMake);
                if (supplemental != null)
                    tagName = supplemental;
            }

            // 中文显示名称（最高优先级）
            var displayName = ExifChinese.GetChineseName(tagName) ?? tagName;

            group.Tags.Add(new MetadataTag
            {
                TagId = tag.Type,
                Name = displayName,
                Value = desc
            });
        }
        return group;
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
                            Console.WriteLine("Failed to extract thumbnail by offset (" + file.Name + "): " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to extract thumbnail from ExifThumbnailDirectory (" + file.Name + "): " + ex.Message);
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
                        Console.WriteLine("Failed to extract thumbnail from directory " + directoryName + ": " + ex.Message);
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
                    Console.WriteLine("Failed to extract thumbnail from IFD1 (" + file.Name + "): " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to extract EXIF thumbnail (" + file.Name + "): " + ex.Message);
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
    public static Task<Bitmap?> CreateThumbnailFromDataAsync(byte[] data)
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
                return Task.FromResult<Bitmap?>(scaledThumbnail);
            }
            
            return Task.FromResult<Bitmap?>(thumbnail);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create thumbnail from data: " + ex.Message);
            return Task.FromResult<Bitmap?>(null);
        }
    }

    /// <summary>
    /// 从原图生成缩略图（优化版本，使用快速解码）
    /// </summary>
    public static async Task<Bitmap?> GenerateThumbnailFromImageAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            
            // 方法1: 尝试使用解码时缩放（如果支持）
            try
            {
                // 先读取图片尺寸信息，不解码完整图片
                var imageInfo = await GetImageInfoAsync(stream);
                if (imageInfo.HasValue)
                {
                    var (width, height) = imageInfo.Value;
                    
                    // 计算目标缩放比例
                    var targetSize = 120;
                    var scale = Math.Min((double)targetSize / width, (double)targetSize / height);
                    
                    // 如果原图很大，使用快速采样解码
                    if (scale < 0.5)
                    {
                        return await GenerateThumbnailWithSampling(stream, targetSize);
                    }
                }
            }
            catch
            {
                // 如果快速方法失败，回退到常规方法
            }
            
            // 方法2: 常规解码但优化内存使用
            stream.Seek(0, SeekOrigin.Begin);
            var originalBitmap = new Bitmap(stream);
            
            // 生成缩略图 (120px目标尺寸)
            if (originalBitmap.PixelSize.Width > 120 || originalBitmap.PixelSize.Height > 120)
            {
                var scale = Math.Min(120.0 / originalBitmap.PixelSize.Width, 120.0 / originalBitmap.PixelSize.Height);
                var newSize = new PixelSize(
                    Math.Max(1, (int)(originalBitmap.PixelSize.Width * scale)),
                    Math.Max(1, (int)(originalBitmap.PixelSize.Height * scale))
                );
                var scaledBitmap = originalBitmap.CreateScaledBitmap(newSize);
                originalBitmap.Dispose();
                return scaledBitmap;
            }
            
            return originalBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to generate thumbnail from image (" + file.Name + "): " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 快速获取图片尺寸信息（不解码完整图片）
    /// </summary>
    private static Task<(int width, int height)?> GetImageInfoAsync(Stream stream)
    {
        try
        {
            // 尝试从EXIF或文件头获取尺寸信息
            var directories = ImageMetadataReader.ReadMetadata(stream);
            
            // 优先从EXIF获取尺寸
            var exifSubIfd = FindShootingExifSubIfd(directories);
            if (exifSubIfd != null)
            {
                if (exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var exifWidth) &&
                    exifSubIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var exifHeight))
                {
                    return Task.FromResult<(int width, int height)?>((exifWidth, exifHeight));
                }
            }
            
            // 从IFD0获取尺寸
            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (exifIfd0 != null)
            {
                if (exifIfd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var ifdWidth) &&
                    exifIfd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var ifdHeight))
                {
                    return Task.FromResult<(int width, int height)?>((ifdWidth, ifdHeight));
                }
            }
            
            // 尝试从JPEG文件头快速读取尺寸
            stream.Seek(0, SeekOrigin.Begin);
            var jpegSize = ReadJpegDimensions(stream);
            if (jpegSize.HasValue)
            {
                return Task.FromResult<(int width, int height)?>(jpegSize);
            }
            
            return Task.FromResult<(int width, int height)?>(null);
        }
        catch
        {
            return Task.FromResult<(int width, int height)?>(null);
        }
    }

    /// <summary>
    /// 从JPEG文件头快速读取尺寸
    /// </summary>
    private static (int width, int height)? ReadJpegDimensions(Stream stream)
    {
        try
        {
            var buffer = new byte[4];
            stream.Seek(0, SeekOrigin.Begin);
            
            // 检查JPEG文件头
            if (stream.Read(buffer, 0, 2) != 2 || buffer[0] != 0xFF || buffer[1] != 0xD8)
                return null;
            
            // 查找SOF段
            while (stream.Position < stream.Length - 8)
            {
                if (stream.Read(buffer, 0, 2) != 2 || buffer[0] != 0xFF)
                    continue;
                
                var marker = buffer[1];
                
                // SOF0, SOF1, SOF2等段包含图片尺寸
                if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) || 
                    (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                {
                    // 跳过段长度
                    if (stream.Read(buffer, 0, 2) != 2) return null;
                    
                    // 跳过精度字节
                    stream.Seek(1, SeekOrigin.Current);
                    
                    // 读取高度和宽度
                    var heightBytes = new byte[2];
                    var widthBytes = new byte[2];
                    
                    if (stream.Read(heightBytes, 0, 2) != 2 || stream.Read(widthBytes, 0, 2) != 2)
                        return null;
                    
                    var height = (heightBytes[0] << 8) | heightBytes[1];
                    var width = (widthBytes[0] << 8) | widthBytes[1];
                    
                    return (width, height);
                }
                else
                {
                    // 跳过其他段
                    if (stream.Read(buffer, 0, 2) != 2) break;
                    var segmentLength = (buffer[0] << 8) | buffer[1];
                    stream.Seek(segmentLength - 2, SeekOrigin.Current);
                }
            }
            
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 使用采样方式生成缩略图（适用于大图片）
    /// </summary>
    private static Task<Bitmap?> GenerateThumbnailWithSampling(Stream stream, int targetSize)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            
            // 使用较低质量但更快的解码方式
            var originalBitmap = new Bitmap(stream);
            
            var originalWidth = originalBitmap.PixelSize.Width;
            var originalHeight = originalBitmap.PixelSize.Height;
            
            // 计算采样步长（跳过像素以提高速度）
            var sampleRate = Math.Max(originalWidth / targetSize, originalHeight / targetSize);
            sampleRate = Math.Max(1, sampleRate / 2); // 适度采样，保持质量
            
            if (sampleRate > 1)
            {
                // 创建采样后的尺寸
                var sampledWidth = Math.Max(1, originalWidth / sampleRate);
                var sampledHeight = Math.Max(1, originalHeight / sampleRate);
                
                // 先进行粗糙缩放
                var roughSize = new PixelSize(sampledWidth, sampledHeight);
                var roughBitmap = originalBitmap.CreateScaledBitmap(roughSize);
                originalBitmap.Dispose();
                
                // 再精确缩放到目标尺寸
                var finalScale = Math.Min((double)targetSize / sampledWidth, (double)targetSize / sampledHeight);
                if (finalScale < 1.0)
                {
                    var finalSize = new PixelSize(
                        Math.Max(1, (int)(sampledWidth * finalScale)),
                        Math.Max(1, (int)(sampledHeight * finalScale))
                    );
                    var finalBitmap = roughBitmap.CreateScaledBitmap(finalSize);
                    roughBitmap.Dispose();
                    return Task.FromResult<Bitmap?>(finalBitmap);
                }
                
                return Task.FromResult<Bitmap?>(roughBitmap);
            }
            else
            {
                // 小图片直接缩放
                var scale = Math.Min((double)targetSize / originalWidth, (double)targetSize / originalHeight);
                if (scale < 1.0)
                {
                    var newSize = new PixelSize(
                        Math.Max(1, (int)(originalWidth * scale)),
                        Math.Max(1, (int)(originalHeight * scale))
                    );
                    var scaledBitmap = originalBitmap.CreateScaledBitmap(newSize);
                    originalBitmap.Dispose();
                    return Task.FromResult<Bitmap?>(scaledBitmap);
                }
                
                return Task.FromResult<Bitmap?>(originalBitmap);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to generate sampled thumbnail: " + ex.Message);
            return Task.FromResult<Bitmap?>(null);
        }
    }

    /// <summary>
    /// 根据 EXIF Orientation 值计算旋转角度
    /// </summary>
    /// <param name="orientationValue">EXIF Orientation 值</param>
    /// <returns>旋转角度（0, 90, 180, 270）</returns>
    public static double GetRotationAngle(int orientationValue)
    {
        return orientationValue switch
        {
            1 => 0,    // 正常
            2 => 0,    // 水平翻转（暂不处理翻转，只处理旋转）
            3 => 180,  // 180度旋转
            4 => 180,  // 180度旋转+水平翻转
            5 => 90,   // 90度旋转+水平翻转
            6 => 90,   // 顺时针90度
            7 => 270,  // 270度旋转+水平翻转
            8 => 270,  // 逆时针90度（顺时针270度）
            _ => 0     // 未知值，不旋转
        };
    }

    /// <summary>
    /// 检查是否需要水平翻转
    /// </summary>
    /// <param name="orientationValue">EXIF Orientation 值</param>
    /// <returns>是否需要水平翻转</returns>
    public static bool NeedsHorizontalFlip(int orientationValue)
    {
        return orientationValue == 2 || orientationValue == 4 || 
               orientationValue == 5 || orientationValue == 7;
    }

    /// <summary>
    /// 仅加载文件的星级评分（用于快速筛选）
    /// </summary>
    public static async Task<int> LoadRatingOnlyAsync(IStorageFile file)
    {
        var filePath = file.Path.LocalPath;
        
        try
        {
            // 只读取 XMP 数据中的 Rating 信息，不解码图片内容
            using var stream = await file.OpenReadAsync();
            var directories = ImageMetadataReader.ReadMetadata(stream);

            var xmpDirectory = directories.OfType<XmpDirectory>().FirstOrDefault();
            
            // 读取 XMP Rating 数据
            if (xmpDirectory != null)
            {
                try
                {
                    var xmpMeta = xmpDirectory.GetXmpProperties();
                    
                    // 尝试多种可能的 Rating 属性路径
                    var ratingPaths = new[]
                    {
                        "xmp:Rating",
                        "http://ns.adobe.com/xap/1.0/:Rating",
                        "Rating",
                        "xap:Rating"
                    };
                    
                    foreach (var path in ratingPaths)
                    {
                        if (xmpMeta.ContainsKey(path))
                        {
                            var ratingValue = xmpMeta[path];
                            if (int.TryParse(ratingValue, out var rating) && rating >= 0 && rating <= 5)
                            {
                                return rating;
                            }
                        }
                    }
                    
                    // 如果上述方法未找到，尝试直接使用 XmpCore 解析
                    try
                    {
                        // 获取原始 XMP 字符串并用 XmpCore 解析
                        var xmpString = xmpDirectory.GetXmpProperties().ToString();
                        if (!string.IsNullOrEmpty(xmpString))
                        {
                            var parsedXmp = XmpCore.XmpMetaFactory.ParseFromString(xmpString);
                            var ratingProperty = parsedXmp.GetProperty("http://ns.adobe.com/xap/1.0/", "Rating");
                            if (ratingProperty != null && int.TryParse(ratingProperty.Value, out var parsedRating) && parsedRating >= 0 && parsedRating <= 5)
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
            }
            
            return 0; // 默认无星级
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read rating data (" + filePath + "): " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 仅尝试从 EXIF/IFD 读取图片尺寸，失败返回 null（不解码整图）
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
}
