using System;
using System.Collections.Generic;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif.Makernotes;

namespace PhotoViewer.Core;

/// <summary>
/// Sony MakerNote 专属解析：对焦点位置、对焦框尺寸、镜头规格 BCD 解码、加密 tag 调度等。
/// </summary>
internal static class SonyMakernoteParser
{
    /// <summary>
    /// 解析 Sony FocusPosition2 (0x2027)：4 x int16u [图像宽, 图像高, 对焦X, 对焦Y]。
    /// 支持 MetadataExtractor 以 4 个 int16u 字符串或 8 个字节字符串输出的两种格式。
    /// </summary>
    public static (int ImageWidth, int ImageHeight, int FocusX, int FocusY)? ParseFocusPosition(
        SonyType1MakernoteDirectory dir)
    {
        // 优先尝试直接读取原始字节
        var obj = dir.GetObject(0x2027);
        byte[]? bytes = obj switch
        {
            byte[] b => b,
            sbyte[] sb => sb.Select(x => (byte)x).ToArray(),
            _ => null
        };
        if (bytes != null && bytes.Length >= 8)
        {
            int iw = bytes[0] | (bytes[1] << 8);
            int ih = bytes[2] | (bytes[3] << 8);
            int fx = bytes[4] | (bytes[5] << 8);
            int fy = bytes[6] | (bytes[7] << 8);
            if (iw > 0 && ih > 0)
                return (iw, ih, fx, fy);
        }

        // 回退：解析描述字符串（空格分隔）
        var desc = dir.GetDescription(0x2027);
        if (string.IsNullOrEmpty(desc)) return null;

        var parts = desc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 4 个 int16u
        if (parts.Length >= 4 &&
            int.TryParse(parts[0], out int iw4) && int.TryParse(parts[1], out int ih4) &&
            int.TryParse(parts[2], out int fx4) && int.TryParse(parts[3], out int fy4) &&
            iw4 > 0 && ih4 > 0)
            return (iw4, ih4, fx4, fy4);
        // 8 个字节：按 little-endian int16u 重组
        if (parts.Length >= 8)
        {
            var vals = new int[8];
            bool ok = true;
            for (int i = 0; i < 8; i++)
                ok &= int.TryParse(parts[i], out vals[i]);
            if (ok)
            {
                int iw8 = vals[0] | (vals[1] << 8);
                int ih8 = vals[2] | (vals[3] << 8);
                int fx8 = vals[4] | (vals[5] << 8);
                int fy8 = vals[6] | (vals[7] << 8);
                if (iw8 > 0 && ih8 > 0)
                    return (iw8, ih8, fx8, fy8);
            }
        }
        return null;
    }

    /// <summary>
    /// 解析 Sony FocusFrameSize (0x2037)：3 x int16u [框宽, 框高, 有效标志]。
    /// 标志为零时表示对焦框无效。
    /// </summary>
    public static (int Width, int Height)? ParseFocusFrameSize(SonyType1MakernoteDirectory dir)
    {
        var obj = dir.GetObject(0x2037);
        byte[]? bytes = obj switch
        {
            byte[] b => b,
            sbyte[] sb => sb.Select(x => (byte)x).ToArray(),
            _ => null
        };
        if (bytes != null && bytes.Length >= 6)
        {
            int fw = bytes[0] | (bytes[1] << 8);
            int fh = bytes[2] | (bytes[3] << 8);
            int flag = bytes[4] | (bytes[5] << 8);
            if (fw > 0 && fh > 0 && flag != 0)
                return (fw, fh);
        }

        var desc = dir.GetDescription(0x2037);
        if (string.IsNullOrEmpty(desc)) return null;

        var parts = desc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int w3) && int.TryParse(parts[1], out int h3) &&
            int.TryParse(parts[2], out int f3) && w3 > 0 && h3 > 0 && f3 != 0)
            return (w3, h3);
        if (parts.Length == 6)
        {
            var vals = new int[6];
            bool ok = true;
            for (int i = 0; i < 6; i++)
                ok &= int.TryParse(parts[i], out vals[i]);
            if (ok)
            {
                int w = vals[0] | (vals[1] << 8);
                int h = vals[2] | (vals[3] << 8);
                int flag = vals[4] | (vals[5] << 8);
                if (w > 0 && h > 0 && flag != 0)
                    return (w, h);
            }
        }
        return null;
    }

    /// <summary>
    /// 格式化 Sony MakerNote 中需要特殊解码的字段值。
    /// LensSpec (0xB02A): 8 字节解码为 "E 28-75mm F2.8" 格式。
    /// FocusFrameSize (0x2037): 3 个 int16u 解码为 "WxH" 格式。
    /// </summary>
    public static string? FormatMakernoteValue(int tagId, string currentDesc, MetadataExtractor.Directory directory)
    {
        return tagId switch
        {
            0xB02A => FormatLensSpec(directory),
            0x2037 => FormatFocusFrameSizeText(currentDesc),
            _ => null,
        };
    }

    /// <summary>
    /// 解码 Sony 加密 MakerNote tag，将原始二进制条目替换为可读字段。
    /// </summary>
    public static void DecodeCipherTagsInto(SonyType1MakernoteDirectory sonyDir, MetadataGroup group, string? cameraModel)
    {
        // 跨 tag 去重: 同名字段只保留第一个有效解码
        var globalSeen = new HashSet<string>();

        foreach (var tagId in SonyCipherTags.SupportedTagIds)
        {
            var decoded = SonyCipherTags.Decode(sonyDir, tagId, cameraModel);
            if (decoded == null || decoded.Count == 0)
                continue;

            decoded.RemoveAll(t => !globalSeen.Add(t.Name));
            if (decoded.Count == 0)
                continue;

            int origIdx = group.Tags.FindIndex(t => t.TagId == tagId);
            if (origIdx >= 0)
            {
                group.Tags.RemoveAt(origIdx);
                group.Tags.InsertRange(origIdx, decoded);
            }
            else
            {
                group.Tags.AddRange(decoded);
            }
        }
    }

    /// <summary>
    /// 解码 Sony LensSpec (0xB02A) 8 字节为 "E 28-75mm F2.8" 格式。
    /// Sony 使用 BCD 编码: 每个字节的十六进制表示即为十进制值。
    /// 例: 字节值 0x28 (十进制40) 表示数值 28。
    /// 字节布局: [flags1, shortFocalHi, shortFocalLo, longFocalHi, longFocalLo, maxApShort, maxApLong, flags2]
    /// </summary>
    private static string? FormatLensSpec(MetadataExtractor.Directory directory)
    {
        var obj = directory.GetObject(0xB02A);
        byte[]? bytes = obj switch
        {
            byte[] b => b,
            sbyte[] sb => sb.Select(x => (byte)x).ToArray(),
            _ => null,
        };
        if (bytes == null || bytes.Length != 8)
            return null;

        // BCD 解码焦距: 两个字节的十六进制表示拼合为十进制数值
        int shortFocal = Bcd2ToInt(bytes[1], bytes[2]);
        int longFocal = Bcd2ToInt(bytes[3], bytes[4]);

        // BCD 解码光圈: 单字节十六进制表示 / 10
        double maxApShort = Bcd1ToInt(bytes[5]) / 10.0;
        double maxApLong = Bcd1ToInt(bytes[6]) / 10.0;

        if (shortFocal == 0 || maxApShort == 0)
            return null;

        // 构建焦距和光圈字符串
        string focalStr = longFocal != shortFocal && longFocal != 0
            ? $"{shortFocal}-{longFocal}mm"
            : $"{shortFocal}mm";
        string apStr = maxApShort != maxApLong && maxApLong != 0
            ? $"F{maxApShort:G}-{maxApLong:G}"
            : $"F{maxApShort:G}";

        string result = $"{focalStr} {apStr}";

        // 解码镜头特性标志 (flags1 高字节 + flags2 低字节)
        int flags = (bytes[0] << 8) | bytes[7];
        var features = new List<string>();
        // 参照 ExifTool @lensFeatures 定义
        if ((flags & 0x4000) != 0) features.Add("PZ");
        int mountBits = flags & 0x0300;
        if (mountBits == 0x0100) features.Add("DT");
        else if (mountBits == 0x0200) features.Add("FE");
        else if (mountBits == 0x0300) features.Add("E");
        // 后缀特性
        var suffixes = new List<string>();
        int typeBits = flags & 0x00E0;
        if (typeBits == 0x0020) suffixes.Add("STF");
        else if (typeBits == 0x0040) suffixes.Add("Reflex");
        else if (typeBits == 0x0060) suffixes.Add("Macro");
        else if (typeBits == 0x0080) suffixes.Add("Fisheye");
        int glassBits = flags & 0x000C;
        if (glassBits == 0x0004) suffixes.Add("ZA");
        else if (glassBits == 0x0008) suffixes.Add("G");
        int motorBits = flags & 0x0003;
        if (motorBits == 0x0001) suffixes.Add("SSM");
        else if (motorBits == 0x0002) suffixes.Add("SAM");
        if ((flags & 0x8000) != 0) suffixes.Add("OSS");
        if ((flags & 0x2000) != 0) suffixes.Add("LE");
        if ((flags & 0x0800) != 0) suffixes.Add("II");

        if (features.Count > 0)
            result = string.Join(" ", features) + " " + result;
        if (suffixes.Count > 0)
            result += " " + string.Join(" ", suffixes);

        return result;
    }

    /// <summary>
    /// 格式化 FocusFrameSize (0x2037) 描述文本: int16u[3] → "WxH"。
    /// 第三个值为标志位，非零时有效。
    /// </summary>
    private static string? FormatFocusFrameSizeText(string currentDesc)
    {
        var parts = currentDesc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 可能是 3 个 int16u，也可能是 6 个 int8u (取决于 MetadataExtractor 解析)
        if (parts.Length == 3 && int.TryParse(parts[0], out int w3) &&
            int.TryParse(parts[1], out int h3) && int.TryParse(parts[2], out int flag3))
        {
            return flag3 != 0 ? $"{w3}x{h3}" : "n/a";
        }
        if (parts.Length == 6)
        {
            var vals = new int[6];
            for (int i = 0; i < 6; i++)
            {
                if (!int.TryParse(parts[i], out vals[i]))
                    return null;
            }
            int w = vals[0] | (vals[1] << 8);
            int h = vals[2] | (vals[3] << 8);
            int flag = vals[4] | (vals[5] << 8);
            return flag != 0 ? $"{w}x{h}" : "n/a";
        }
        return null;
    }

    /// <summary>
    /// BCD 解码: 将两个字节的十六进制表示拼合为十进制数值。
    /// 例: (0x01, 0x35) → 0135 → 135
    /// </summary>
    private static int Bcd2ToInt(byte hi, byte lo)
    {
        return (hi >> 4) * 1000 + (hi & 0xF) * 100 + (lo >> 4) * 10 + (lo & 0xF);
    }

    /// <summary>
    /// BCD 解码: 将单个字节的十六进制表示解释为十进制数值。
    /// 例: 0x28 → 28, 0x56 → 56
    /// </summary>
    private static int Bcd1ToInt(byte b)
    {
        return (b >> 4) * 10 + (b & 0xF);
    }
}
