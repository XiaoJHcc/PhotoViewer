using System;
using System.IO;
using SkiaSharp;

namespace PhotoViewer.Core.Color;

/// <summary>
/// JPEG 容器里的色彩元数据：内嵌 ICC profile 与"无 ICC 时"的 EXIF 色域声明。
/// 独立于 Skia 解码器读取 —— Skia 对无 ICC 的 JPEG 一律报 sRGB，无法区分"真 sRGB"和"未标记的 Adobe RGB"。
/// 解析只看标记段（APP1/APP2），到 SOS 即停，不解码像素。
/// </summary>
public readonly struct JpegColorMetadata
{
    /// <summary>ICC profile 的最小长度（128 字节头）。</summary>
    private const int IccHeaderLength = 128;

    /// <summary>ICC profile 中 'acsp' 签名所在偏移。</summary>
    private const int IccSignatureOffset = 36;

    /// <summary>SOS 之前的标记段数量上限（防御畸形文件导致的死循环）。</summary>
    private const int MaxSegments = 512;

    private JpegColorMetadata(byte[]? iccProfile, int? exifColorSpace, string? interoperabilityIndex)
    {
        IccProfile = iccProfile;
        ExifColorSpace = exifColorSpace;
        InteroperabilityIndex = interoperabilityIndex;
    }

    /// <summary>空结果（非 JPEG、无色彩元数据或解析未完成）。</summary>
    public static JpegColorMetadata Empty => default;

    /// <summary>内嵌 ICC profile 的原始字节；无 ICC 时为 null。</summary>
    public byte[]? IccProfile { get; }

    /// <summary>EXIF ColorSpace（0xA001）的数值；缺失时为 null。2 表示 Adobe RGB。</summary>
    public int? ExifColorSpace { get; }

    /// <summary>EXIF 互操作标识（InteroperabilityIndex，如 R03 表示 Adobe RGB）；缺失时为 null。</summary>
    public string? InteroperabilityIndex { get; }

    /// <summary>是否有可用的内嵌 ICC profile。</summary>
    public bool HasIccProfile => IccProfile is { Length: >= IccHeaderLength };

    /// <summary>
    /// 解析色彩元数据：有效 ICC 优先；profile 数值上就是 sRGB 时归为 sRGB（等价且可走查表快通道）；
    /// 其余 ICC 保留为 ICC 标签。无 ICC 时按 EXIF 色域声明推断，最终默认 sRGB。
    /// </summary>
    /// <returns>该 JPEG 的真实源色域标签。</returns>
    public ImageColorSpace ResolveColorSpace()
    {
        if (IccProfile is { } profile && IsValidIccProfile(profile))
        {
            try
            {
                using var space = SKColorSpace.CreateIcc(profile);
                if (space != null && space.IsSrgb) return ImageColorSpace.Srgb;
            }
            catch
            {
                // profile 结构异常时交给 skcms 按数值解释，仍按 ICC 标签保留原始字节。
            }
            return ImageColorSpace.Icc;
        }
        return ColorEngine.InferJpegColorSpace(ExifColorSpace, InteroperabilityIndex);
    }

    /// <summary>从流的当前位置解析标记段并恢复原位置；流不可寻址或非 JPEG 时返回 <see cref="Empty"/>。</summary>
    /// <param name="stream">JPEG 字节流；需可寻址。</param>
    /// <returns>色彩元数据。</returns>
    public static JpegColorMetadata Read(Stream stream)
    {
        if (stream == null || !stream.CanSeek) return Empty;

        long origin = stream.Position;
        try
        {
            return ReadSegments(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JpegColorMetadata.Read failed: {ex.Message}");
            return Empty;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>从内存字节解析标记段（供工具与已读入缓冲的调用方使用）。</summary>
    /// <param name="data">JPEG 字节。</param>
    /// <returns>色彩元数据。</returns>
    public static JpegColorMetadata Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return Empty;
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            return ReadSegments(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JpegColorMetadata.Read failed: {ex.Message}");
            return Empty;
        }
    }

    /// <summary>顺序读标记段：收集 ICC 分片与 EXIF 色域声明，遇到 SOS/EOI 停止。</summary>
    private static JpegColorMetadata ReadSegments(Stream stream)
    {
        Span<byte> two = stackalloc byte[2];
        if (!TryReadExactly(stream, two) || two[0] != 0xFF || two[1] != 0xD8) return Empty;

        byte[][]? iccChunks = null;
        int? exifColorSpace = null;
        string? interoperabilityIndex = null;

        for (int segment = 0; segment < MaxSegments; segment++)
        {
            int prefix = stream.ReadByte();
            if (prefix != 0xFF) break;
            int marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker < 0) break;

            // 无长度的独立标记（RSTn / TEM / SOI）：继续找下一个标记段。
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            // EOI 与 SOS：色彩元数据只可能在其之前。
            if (marker is 0xD9 or 0xDA) break;

            if (!TryReadExactly(stream, two)) break;
            int length = (two[0] << 8) | two[1];
            if (length < 2) break;
            var payload = new byte[length - 2];
            if (!TryReadExactly(stream, payload)) break;

            switch (marker)
            {
                case 0xE2:
                    AppendIccChunk(payload, ref iccChunks);
                    break;
                case 0xE1 when exifColorSpace == null:
                    if (TryReadExif(payload, out var colorSpace, out var index))
                    {
                        exifColorSpace = colorSpace;
                        interoperabilityIndex = index;
                    }
                    break;
            }
        }

        return new JpegColorMetadata(AssembleIcc(iccChunks), exifColorSpace, interoperabilityIndex);
    }

    /// <summary>收集 APP2 里的 ICC_PROFILE 分片（序号 + 总数 + 数据）。</summary>
    private static void AppendIccChunk(byte[] payload, ref byte[][]? chunks)
    {
        const int headerLength = 14; // "ICC_PROFILE\0" + 序号 + 总数
        if (payload.Length <= headerLength) return;
        var signature = "ICC_PROFILE\0"u8;
        if (!payload.AsSpan(0, signature.Length).SequenceEqual(signature)) return;

        int sequence = payload[12];
        int count = payload[13];
        if (sequence == 0 || count == 0 || sequence > count) return;

        chunks ??= new byte[count][];
        if (chunks.Length != count) return;
        var chunk = new byte[payload.Length - headerLength];
        Array.Copy(payload, headerLength, chunk, 0, chunk.Length);
        chunks[sequence - 1] = chunk;
    }

    /// <summary>按分片序号拼回完整 ICC profile；分片不全时返回 null（按"无有效 ICC"处理）。</summary>
    private static byte[]? AssembleIcc(byte[][]? chunks)
    {
        if (chunks == null) return null;
        int total = 0;
        foreach (var chunk in chunks)
        {
            if (chunk == null) return null;
            total += chunk.Length;
        }
        if (total < IccHeaderLength) return null;

        var profile = new byte[total];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, profile, offset, chunk.Length);
            offset += chunk.Length;
        }
        return IsValidIccProfile(profile) ? profile : null;
    }

    /// <summary>ICC profile 结构校验：长度够、声明长度可信、'acsp' 签名在位。</summary>
    private static bool IsValidIccProfile(byte[] profile)
    {
        if (profile.Length < IccHeaderLength) return false;
        if (profile[IccSignatureOffset] != 'a' || profile[IccSignatureOffset + 1] != 'c'
            || profile[IccSignatureOffset + 2] != 's' || profile[IccSignatureOffset + 3] != 'p') return false;
        long declared = (long)profile[0] << 24 | (long)profile[1] << 16 | (long)profile[2] << 8 | profile[3];
        return declared == 0 || declared <= profile.Length;
    }

    /// <summary>从 APP1 的 EXIF 段读出 ColorSpace 与 InteroperabilityIndex；非 EXIF 段返回 false。</summary>
    private static bool TryReadExif(byte[] payload, out int? colorSpace, out string? interoperabilityIndex)
    {
        colorSpace = null;
        interoperabilityIndex = null;
        var header = "Exif\0\0"u8;
        if (payload.Length < header.Length + 8) return false;
        if (!payload.AsSpan(0, header.Length).SequenceEqual(header)) return false;

        var tiff = payload.AsSpan(header.Length);
        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I') littleEndian = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') littleEndian = false;
        else return false;
        if (ReadUInt16(tiff, 2, littleEndian) != 42) return false;

        int ifd0 = (int)ReadUInt32(tiff, 4, littleEndian);
        int exifIfd = FindIfdOffset(tiff, ifd0, littleEndian, 0x8769);
        if (exifIfd > 0)
        {
            var value = FindTagValue(tiff, exifIfd, littleEndian, 0xA001);
            if (value.Count >= 1) colorSpace = (int)ReadUInt16(value.Data, 0, littleEndian);

            int interopIfd = FindIfdOffset(tiff, exifIfd, littleEndian, 0xA005);
            if (interopIfd > 0) interoperabilityIndex = ReadAscii(tiff, interopIfd, littleEndian, 0x0001);
        }
        if (colorSpace == null)
        {
            var value = FindTagValue(tiff, ifd0, littleEndian, 0xA001);
            if (value.Count >= 1) colorSpace = (int)ReadUInt16(value.Data, 0, littleEndian);
        }
        return colorSpace != null || interoperabilityIndex != null;
    }

    /// <summary>在某 IFD 里找子 IFD 指针标签（Exif 0x8769 / Interop 0xA005）并返回其偏移。</summary>
    private static int FindIfdOffset(ReadOnlySpan<byte> tiff, int ifd, bool littleEndian, int tag)
    {
        var value = FindTagValue(tiff, ifd, littleEndian, tag);
        return value.Count >= 1 && value.OffsetValue > 0 ? (int)value.OffsetValue : -1;
    }

    /// <summary>IFD 中某个标签的原始值视图；<see cref="Data"/> 指向 TIFF 缓冲内部（超出内联 4 字节时跟到偏移处）。</summary>
    private readonly ref struct TagValue(ReadOnlySpan<byte> data, long offsetValue, int count)
    {
        /// <summary>标签值的字节视图；缺失时为空。</summary>
        public ReadOnlySpan<byte> Data { get; } = data;

        /// <summary>标签值字段的原始偏移（子 IFD 指针就是偏移本身）。</summary>
        public long OffsetValue { get; } = offsetValue;

        /// <summary>标签值的元素个数。</summary>
        public int Count { get; } = count;
    }

    /// <summary>按标签号在 IFD 里取原始值。</summary>
    private static TagValue FindTagValue(ReadOnlySpan<byte> tiff, int ifd, bool littleEndian, int tag)
    {
        if (ifd <= 0 || ifd + 2 > tiff.Length) return default;
        int entries = ReadUInt16(tiff, ifd, littleEndian);
        int at = ifd + 2;
        for (int i = 0; i < entries; i++, at += 12)
        {
            if (at + 12 > tiff.Length) return default;
            if (ReadUInt16(tiff, at, littleEndian) != tag) continue;

            int type = ReadUInt16(tiff, at + 2, littleEndian);
            int count = (int)ReadUInt32(tiff, at + 4, littleEndian);
            int unit = TypeSize(type);
            if (unit == 0 || count < 0) return default;

            long offsetValue = ReadUInt32(tiff, at + 8, littleEndian);
            int size = unit * count;
            if (size <= 4) return new TagValue(tiff.Slice(at + 8, size), offsetValue, count);
            if (offsetValue <= 0 || offsetValue + size > tiff.Length) return new TagValue(default, offsetValue, count);
            return new TagValue(tiff.Slice((int)offsetValue, size), offsetValue, count);
        }
        return default;
    }

    /// <summary>读 ASCII 标签值（去掉尾部 NUL）；缺失或类型不符返回 null。</summary>
    private static string? ReadAscii(ReadOnlySpan<byte> tiff, int ifd, bool littleEndian, int tag)
    {
        var value = FindTagValue(tiff, ifd, littleEndian, tag);
        if (value.Count <= 0 || value.Data.IsEmpty) return null;
        int end = value.Data.IndexOf((byte)0);
        if (end < 0) end = value.Data.Length;
        return end == 0 ? null : System.Text.Encoding.ASCII.GetString(value.Data[..end]);
    }

    /// <summary>TIFF 类型码的单元字节数；未知类型返回 0。</summary>
    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,          // BYTE / ASCII / SBYTE / UNDEFINED
        3 or 8 => 2,                    // SHORT / SSHORT
        4 or 9 or 11 => 4,              // LONG / SLONG / FLOAT
        5 or 10 or 12 => 8,             // RATIONAL / SRATIONAL / DOUBLE
        _ => 0,
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset < 0 || offset + 2 > data.Length) return 0;
        return littleEndian
            ? (ushort)(data[offset] | data[offset + 1] << 8)
            : (ushort)(data[offset] << 8 | data[offset + 1]);
    }

    private static long ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset < 0 || offset + 4 > data.Length) return 0;
        return littleEndian
            ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
            : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }

    /// <summary>读满目标长度；流提前结束时返回 false（已读部分不回退）。</summary>
    private static bool TryReadExactly(Stream stream, Span<byte> buffer) => stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
}
