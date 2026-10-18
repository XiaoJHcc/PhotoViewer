using System;
using System.Collections.Generic;
using System.Text;

namespace PhotoViewer.Android.Core;

/// <summary>
/// 从 HEIF/BMIFF 容器提取内嵌 JPEG 缩略图 item（Sony HIF 常见）。
/// 只读 iinf/iloc，不依赖 libheif 的 thumbnail 插件；供胶片/导航在无快解插件时先出小图。
/// </summary>
internal static class HeifJpegThumbnailExtractor
{
    /// <summary>提取最大的 JPEG item 字节；没有返回 null。</summary>
    public static byte[]? TryExtract(byte[] data)
    {
        if (data.Length < 16) return null;
        var meta = FindChild(data, 0, data.Length, "meta");
        if (meta == null) return null;
        var (metaStart, metaEnd) = meta.Value;
        var itemTypes = ParseItemTypes(data, metaStart + 12, metaEnd);
        if (itemTypes.Count == 0) return null;
        var extents = ParseItemLocations(data, metaStart + 12, metaEnd);
        (int Offset, int Length)? best = null;
        foreach (var (id, type) in itemTypes)
        {
            if (!string.Equals(type, "jpeg", StringComparison.Ordinal)) continue;
            if (!extents.TryGetValue(id, out var ranges)) continue;
            foreach (var range in ranges)
            {
                if (range.Length <= 4 || range.Offset < 0 || (long)range.Offset + range.Length > data.Length) continue;
                if (best == null || range.Length > best.Value.Length) best = range;
            }
        }
        if (best is not { } found) return null;
        if (data[found.Offset] != 0xFF || data[found.Offset + 1] != 0xD8) return null;
        var jpeg = new byte[found.Length];
        Buffer.BlockCopy(data, found.Offset, jpeg, 0, found.Length);
        return jpeg;
    }

    private static (int Start, int End)? FindChild(byte[] data, int start, int end, string type)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = ReadU32(data, pos);
            if (size == 1)
            {
                if (pos + 16 > end) return null;
                size = (long)ReadU64(data, pos + 8);
                if (size < 16 || pos + size > end) return null;
            }
            else if (size == 0) size = end - pos;
            if (size < 8 || pos + size > end) return null;
            if (string.Equals(Encoding.ASCII.GetString(data, pos + 4, 4), type, StringComparison.Ordinal))
                return (pos, (int)(pos + size));
            pos += (int)size;
        }
        return null;
    }

    private static Dictionary<int, string> ParseItemTypes(byte[] data, int start, int end)
    {
        var result = new Dictionary<int, string>();
        var iinf = FindChild(data, start, end, "iinf");
        if (iinf == null) return result;
        int q = iinf.Value.Start + 8;
        int version = data[q];
        q += 4;
        int count = version == 0 ? ReadU16(data, q) : (int)ReadU32(data, q);
        q += version == 0 ? 2 : 4;
        for (int index = 0; index < count && q + 8 <= iinf.Value.End; index++)
        {
            long size = ReadU32(data, q);
            if (size < 8 || q + size > iinf.Value.End) break;
            string boxType = Encoding.ASCII.GetString(data, q + 4, 4);
            if (boxType == "infe")
            {
                int r = q + 8;
                int infeVersion = data[r];
                r += 4;
                if (infeVersion >= 2 && r + 8 <= q + size)
                {
                    int id = ReadU16(data, r);
                    r += 2;
                    r += 2; // protection
                    string type = Encoding.ASCII.GetString(data, r, 4);
                    result[id] = type;
                }
            }
            q += (int)size;
        }
        return result;
    }

    private static Dictionary<int, List<(int Offset, int Length)>> ParseItemLocations(byte[] data, int start, int end)
    {
        var result = new Dictionary<int, List<(int, int)>>();
        var iloc = FindChild(data, start, end, "iloc");
        if (iloc == null) return result;
        int q = iloc.Value.Start + 8;
        int version = data[q];
        q += 4;
        int offsetSize = data[q] >> 4;
        int lengthSize = data[q] & 0x0F;
        int baseOffsetSize = data[q + 1] >> 4;
        int indexSize = data[q + 1] & 0x0F;
        q += 2;
        int count = ReadU16(data, q);
        q += 2;
        for (int index = 0; index < count && q + 2 <= iloc.Value.End; index++)
        {
            int id = ReadU16(data, q);
            q += 2;
            if (version is 1 or 2) q += 2; // construction_method
            q += 2; // data_reference_index
            long baseOffset = ReadSized(data, q, baseOffsetSize);
            q += baseOffsetSize;
            int extentCount = ReadU16(data, q);
            q += 2;
            var ranges = new List<(int, int)>();
            for (int extent = 0; extent < extentCount; extent++)
            {
                if (indexSize > 0) q += indexSize;
                long offset = ReadSized(data, q, offsetSize);
                q += offsetSize;
                long length = ReadSized(data, q, lengthSize);
                q += lengthSize;
                long absolute = baseOffset + offset;
                if (absolute is >= 0 and <= int.MaxValue && length is > 0 and <= int.MaxValue)
                    ranges.Add(((int)absolute, (int)length));
            }
            result[id] = ranges;
        }
        return result;
    }

    private static long ReadSized(byte[] data, int offset, int size)
    {
        if (size <= 0) return 0;
        if (offset < 0 || offset + size > data.Length) return 0;
        long value = 0;
        for (int i = 0; i < size; i++) value = (value << 8) | data[offset + i];
        return value;
    }

    private static int ReadU16(byte[] data, int offset)
        => offset + 2 <= data.Length ? (data[offset] << 8) | data[offset + 1] : 0;

    private static int ReadU32(byte[] data, int offset)
        => offset + 4 <= data.Length ? (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3] : 0;

    private static long ReadU64(byte[] data, int offset)
        => offset + 8 <= data.Length ? ((long)ReadU32(data, offset) << 32) | (uint)ReadU32(data, offset + 4) : 0;
}
