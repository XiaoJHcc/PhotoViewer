using System;
using System.Collections.Generic;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 容器声明的"显示朝向 + 传感器原始尺寸"。
/// HEIF 通过 <c>HEIC Primary Item Properties → Default Rotation</c> 声明逆时针旋转角度（0/90/180/270）；
/// JPEG/TIFF 通过 <c>ExifIFD0 → Orientation</c>（1..8）声明 EXIF 旋转/镜像。
/// 这里把两者统一成"显示坐标系下的旋转角与翻转标记"，再加上传感器原始 W/H，便于 letterbox 裁剪做几何对齐。
/// 此结构不做任何启发式判断，全部字段均直接来源于容器/EXIF 元数据。
/// </summary>
public readonly struct ImageOrientationInfo
{
    /// <summary>从传感器朝向到显示朝向所需顺时针旋转角度（0/90/180/270）。</summary>
    public int RotationDegreesCw { get; }

    /// <summary>是否需要在旋转前做水平翻转（对应 EXIF Orientation 2/4/5/7）。HEIF 容器无翻转概念，恒为 false。</summary>
    public bool MirrorHorizontal { get; }

    /// <summary>传感器原始宽（像素），未应用旋转。0 表示元数据未提供。</summary>
    public int SensorWidth { get; }

    /// <summary>传感器原始高（像素），未应用旋转。0 表示元数据未提供。</summary>
    public int SensorHeight { get; }

    /// <summary>传感器朝向下的纵横比（W/H）。元数据缺失时返回 0。</summary>
    public double SensorAspect => (SensorWidth > 0 && SensorHeight > 0) ? (double)SensorWidth / SensorHeight : 0.0;

    /// <summary>
    /// 显示朝向下的纵横比（W/H）。等价于 SensorAspect 在 90/270 度旋转后取倒数。
    /// </summary>
    public double DisplayAspect
    {
        get
        {
            var a = SensorAspect;
            if (a <= 0) return 0.0;
            return SwapsWidthHeight ? 1.0 / a : a;
        }
    }

    /// <summary>当前旋转角度是否会交换宽高（90/270 度）。</summary>
    public bool SwapsWidthHeight => RotationDegreesCw == 90 || RotationDegreesCw == 270;

    public ImageOrientationInfo(int rotationDegreesCw, bool mirrorHorizontal, int sensorWidth, int sensorHeight)
    {
        RotationDegreesCw = NormalizeRotation(rotationDegreesCw);
        MirrorHorizontal = mirrorHorizontal;
        SensorWidth = Math.Max(0, sensorWidth);
        SensorHeight = Math.Max(0, sensorHeight);
    }

    private static int NormalizeRotation(int degrees)
    {
        int r = ((degrees % 360) + 360) % 360;
        return r switch
        {
            0 or 90 or 180 or 270 => r,
            _ => 0,
        };
    }

    /// <summary>
    /// 从 MetadataExtractor 解析出的目录列表里读出方向信息。
    /// HEIF 走 HEIC Primary Item Properties；其它格式走 ExifIFD0 + ExifSubIFD。
    /// </summary>
    public static ImageOrientationInfo FromDirectories(IReadOnlyList<MetadataExtractor.Directory> directories, bool isHeif)
    {
        if (directories == null || directories.Count == 0)
        {
            return new ImageOrientationInfo(0, false, 0, 0);
        }

        if (isHeif)
        {
            return FromHeifDirectories(directories);
        }
        return FromExifDirectories(directories);
    }

    /// <summary>
    /// 从 HEIC Primary Item Properties 读取传感器尺寸 + Default Rotation。
    /// MetadataExtractor 返回的 Default Rotation 字符串是"0 degrees" / "90 degrees" / "180 degrees" / "270 degrees"，
    /// 这是 HEIF 标准定义的"显示前应用的逆时针旋转"。
    /// </summary>
    private static ImageOrientationInfo FromHeifDirectories(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var dir in directories)
        {
            if (!IsHeicPrimaryItemProperties(dir)) continue;

            int sw = TryReadInt(dir, 1);
            int sh = TryReadInt(dir, 2);
            int rotCcw = ParseHeifRotationDegrees(dir);
            int rotCw = (360 - rotCcw) % 360;
            return new ImageOrientationInfo(rotCw, false, sw, sh);
        }
        return new ImageOrientationInfo(0, false, 0, 0);
    }

    /// <summary>
    /// 从 ExifIFD0 读 Orientation；从 ExifSubIFD 读 ExifImageWidth/Height（传感器原始尺寸，与 Orientation 无关）。
    /// </summary>
    private static ImageOrientationInfo FromExifDirectories(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        int orientation = 1;
        int sw = 0, sh = 0;

        foreach (var dir in directories)
        {
            if (dir is ExifIfd0Directory ifd0)
            {
                orientation = TryReadIntOrDefault(ifd0, ExifDirectoryBase.TagOrientation, 1);
            }
            else if (dir is ExifSubIfdDirectory sub)
            {
                if (sw == 0) sw = TryReadIntOrDefault(sub, ExifDirectoryBase.TagExifImageWidth, 0);
                if (sh == 0) sh = TryReadIntOrDefault(sub, ExifDirectoryBase.TagExifImageHeight, 0);
            }
        }

        var (rotCw, mirror) = MapExifOrientationToCwRotationAndMirror(orientation);
        return new ImageOrientationInfo(rotCw, mirror, sw, sh);
    }

    /// <summary>
    /// EXIF Orientation (1..8) → (顺时针旋转角度, 是否水平镜像)。
    /// 1=正常 / 2=镜像 / 3=180 / 4=180+镜像 / 5=镜像+90CW / 6=90CW / 7=镜像+270CW / 8=270CW。
    /// </summary>
    private static (int rotCw, bool mirror) MapExifOrientationToCwRotationAndMirror(int orientation)
    {
        return orientation switch
        {
            1 => (0, false),
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (90, true),
            6 => (90, false),
            7 => (270, true),
            8 => (270, false),
            _ => (0, false),
        };
    }

    /// <summary>
    /// 读 HEIC Default Rotation。MetadataExtractor 把它存为 tag id 3 的 int（0/1/2/3 分别代表 0/90/180/270 度），
    /// 同时 description 是"X degrees"字符串。优先按整数读，失败再解析字符串。
    /// </summary>
    private static int ParseHeifRotationDegrees(MetadataExtractor.Directory dir)
    {
        try
        {
            int raw = dir.GetInt32(3);
            if (raw is 0 or 1 or 2 or 3)
            {
                return raw * 90;
            }
            if (raw is 0 or 90 or 180 or 270)
            {
                return raw;
            }
        }
        catch
        {
            // 落到字符串解析
        }

        try
        {
            string? desc = dir.GetDescription(3);
            if (string.IsNullOrEmpty(desc)) return 0;
            int spaceIdx = desc.IndexOf(' ');
            string head = spaceIdx > 0 ? desc.Substring(0, spaceIdx) : desc;
            if (int.TryParse(head, out int v) && v is 0 or 90 or 180 or 270)
            {
                return v;
            }
        }
        catch
        {
            // 忽略
        }
        return 0;
    }

    private static bool IsHeicPrimaryItemProperties(MetadataExtractor.Directory d) =>
        d.Name.IndexOf("Primary Item Properties", StringComparison.OrdinalIgnoreCase) >= 0;

    private static int TryReadInt(MetadataExtractor.Directory dir, int tag)
    {
        try { return dir.GetInt32(tag); }
        catch { return 0; }
    }

    private static int TryReadIntOrDefault(MetadataExtractor.Directory dir, int tag, int defaultValue)
    {
        try { return dir.HasTagName(tag) ? dir.GetInt32(tag) : defaultValue; }
        catch { return defaultValue; }
    }
}
