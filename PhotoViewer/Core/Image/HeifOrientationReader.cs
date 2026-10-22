using System;
using System.IO;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 直接从 HEIF 容器头部读取 Primary Item 的 <c>irot</c>。
/// 用于 MetadataExtractor 在首个 SAF 文件上偶发拿不到 Default Rotation 时兜底。
/// </summary>
public static class HeifOrientationReader
{
    private const int ProbeBytes = 64 * 1024;

    /// <summary>读取显示前需顺时针旋转的角度（0/90/180/270）；没有 irot 返回 null。</summary>
    public static int? TryReadRotationDegreesCw(Stream stream)
    {
        if (stream == null) return null;
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            var buffer = new byte[ProbeBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
            var span = buffer.AsSpan(0, total);
            int index = 0;
            while (index + 8 <= span.Length)
            {
                int found = span.Slice(index).IndexOf("irot"u8);
                if (found < 0) break;
                int tag = index + found;
                int boxStart = tag - 4;
                if (boxStart < 0) break;
                uint size = ((uint)span[boxStart] << 24) | ((uint)span[boxStart + 1] << 16)
                    | ((uint)span[boxStart + 2] << 8) | span[boxStart + 3];
                if (size >= 9 && tag + 5 <= span.Length)
                {
                    int angle = span[tag + 4] & 0x03; // 90° 逆时针单位
                    return (360 - angle * 90) % 360;
                }
                index = tag + 4;
            }
        }
        catch
        {
            // 读不到就当无方向，沿用上层 EXIF/元数据结果。
        }
        return null;
    }
}
