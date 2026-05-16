using System;
using System.IO;

namespace PhotoViewer.Core.Image;

/// <summary>
/// JPEG 字节段尺寸解析：从 SOF0/SOF1/SOF2/SOF9... 的开头 9 字节读出真实图像高×宽。
/// JPEG 文件头先以 FF D8 开始，随后是若干 segment：每个以 FF Mn 开头，然后 2 字节大端长度。
/// SOF marker 取值 FFC0..FFCF（不含 FFC4/FFC8/FFCC，这些是 DHT/JPG/DAC）。
/// 段内布局：[长度 2B] [precision 1B] [height 2B 大端] [width 2B 大端] [...]
/// 此函数只在前 N 字节内查找首个 SOF marker，纯字节解析无启发式。
/// </summary>
internal static class JpegDimensionReader
{
    /// <summary>
    /// 解析 JPEG 字节真实宽高。返回 (width, height)；不是 JPEG 或没找到 SOF 时返回 (0,0)。
    /// </summary>
    public static (int width, int height) TryReadDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return (0, 0);
        if (!(data[0] == 0xFF && data[1] == 0xD8)) return (0, 0);

        int i = 2;
        while (i + 9 < data.Length)
        {
            // 跳过填充 0xFF 字节
            while (i < data.Length && data[i] == 0xFF) i++;
            if (i >= data.Length) break;

            byte marker = data[i++];

            // SOI/EOI 没有长度字段（已在头部跳过 SOI；遇 EOI 直接结束）
            if (marker == 0xD9) break;
            // RST0..RST7 (D0-D7) 与 TEM (01) 无 payload
            if (marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0x01) continue;

            if (i + 1 >= data.Length) break;
            int segLen = (data[i] << 8) | data[i + 1];
            i += 2;
            if (segLen < 2) break;
            int payloadLen = segLen - 2;

            // SOF marker：C0..CF，但排除 C4/C8/CC
            if (marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (payloadLen < 7 || i + 7 > data.Length) return (0, 0);
                int height = (data[i + 1] << 8) | data[i + 2];
                int width = (data[i + 3] << 8) | data[i + 4];
                if (width <= 0 || height <= 0) return (0, 0);
                return (width, height);
            }

            i += payloadLen;
        }
        return (0, 0);
    }

    /// <summary>
    /// 从流中按字节读 JPEG 头部直到首个 SOF marker，得出真实宽高。读够即停，避免读完整段缩略图。
    /// 适合从大文件中只读出 (offset, length) 范围内的前若干字节。
    /// </summary>
    public static (int width, int height) TryReadDimensionsFromStream(Stream stream, int maxBytes = 64 * 1024)
    {
        if (stream == null || maxBytes <= 0) return (0, 0);
        try
        {
            int toRead = Math.Min(maxBytes, 1 << 20);
            var buf = new byte[toRead];
            int read = 0;
            int total = 0;
            while (total < toRead)
            {
                read = stream.Read(buf, total, toRead - total);
                if (read <= 0) break;
                total += read;
            }
            if (total < 4) return (0, 0);
            return TryReadDimensions(buf.AsSpan(0, total));
        }
        catch
        {
            return (0, 0);
        }
    }
}
