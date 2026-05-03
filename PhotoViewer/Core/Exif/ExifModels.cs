using System;
using System.Collections.Generic;
using MetadataExtractor;

namespace PhotoViewer.Core;

/// <summary>
/// 元数据标签（目录中的一个字段）
/// </summary>
public class MetadataTag
{
    public int TagId { get; set; }
    /// <summary>ExifTool 风格英文名称</summary>
    public string Name { get; set; } = "";
    /// <summary>中文译名；无翻译时为 null</summary>
    public string? ChineseName { get; set; }
    public string Value { get; set; } = "";
    /// <summary>Tag ID 的十六进制表示，如 "0x0201"</summary>
    public string TagIdHex => $"0x{TagId:X4}";
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

    // ---- 扩展拍摄参数 ----

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

    // ---- Sony 对焦信息 ----

    /// <summary>
    /// Sony FocusPosition2 (0x2027)：图像尺寸与对焦点坐标（以左上角为原点的像素坐标）。
    /// 仅在 Sony 相机拍摄的文件中有效，非 Sony 文件此字段为 null。
    /// </summary>
    public (int ImageWidth, int ImageHeight, int FocusX, int FocusY)? SonyFocusPosition { get; set; }

    /// <summary>
    /// Sony FocusFrameSize (0x2037)：对焦框像素尺寸（宽×高）。
    /// 仅在 Sony 相机拍摄且对焦框数据有效时不为 null。
    /// </summary>
    public (int Width, int Height)? SonyFocusFrameSize { get; set; }

    // ---- 全量元数据（按目录分组）----

    /// <summary>
    /// 所有元数据分组列表，包含文件中的全部 EXIF/XMP/Makernote 等信息
    /// </summary>
    public List<MetadataGroup> AllMetadata { get; set; } = new();
}
