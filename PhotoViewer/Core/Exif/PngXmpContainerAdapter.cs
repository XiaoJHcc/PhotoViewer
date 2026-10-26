using System.Buffers.Binary;
using System.Text;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// PNG 内嵌 XMP 容器适配器：iTXt keyword <c>XML:com.adobe.xmp</c>，仅未压缩文本。
/// 读取逐 chunk 校验边界与 CRC；tEXt/zTXt 中的 XMP 不视为 canonical。
/// </summary>
/// <remarks>
/// 评分事务明确不支持 PNG 写入（iTXt 长度/CRC 事务未实现，裸写文本会写坏文件），
/// 读取仍可用于完整 EXIF 展示。多个 iTXt XMP 包明确
/// <see cref="StorageError.Unsupported"/>。
/// </remarks>
public sealed class PngXmpContainerAdapter : IXmpContainerAdapter
{
    public const string XmpKeyword = "XML:com.adobe.xmp";

    private const int MaxChunkDataLength = int.MaxValue - 12;
    private const int MaxTextChunkCapture = 16 * 1024 * 1024;

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] XmpKeywordBytes = Encoding.ASCII.GetBytes(XmpKeyword);

    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);
        return layout.Xmp is { } xmp
            ? new XmpContainerPacket(xmp.Offset + 8 + xmp.Prefix.Length, xmp.Text.Length, xmp.Text)
            : null;
    }

    private static async Task<PngLayout> ScanAsync(Stream stream, CancellationToken cancellationToken)
    {
        var signature = new byte[Signature.Length];
        var signatureLength = await ReadAtMostAsync(stream, signature, cancellationToken).ConfigureAwait(false);
        if (signatureLength != Signature.Length || !signature.AsSpan().SequenceEqual(Signature))
            throw new InvalidDataException("不是 PNG 文件");

        var buffer = new byte[81920];
        long position = Signature.Length;
        PngChunk? iend = null;
        PngXmpChunk? xmp = null;
        var xmpCount = 0;
        var chunkIndex = 0;

        while (iend == null)
        {
            var header = new byte[8];
            var headerLength = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (headerLength == 0) break;
            if (headerLength < header.Length)
                throw new InvalidDataException($"PNG chunk 头不完整（偏移 {position}）");

            var chunkOffset = position;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > MaxChunkDataLength)
                throw new InvalidDataException($"PNG chunk 长度非法（偏移 {chunkOffset}）");
            var type = header[4..8];
            if (!IsChunkType(type))
                throw new InvalidDataException($"PNG chunk 类型非法（偏移 {chunkOffset}）");
            if (stream.CanSeek && chunkOffset + 12 + length > stream.Length)
                throw new InvalidDataException($"PNG chunk 超出文件边界（偏移 {chunkOffset}）");

            var dataLength = (int)length;
            var isITxt = type.SequenceEqual("iTXt"u8);
            var captureLimit = isITxt ? Math.Min(dataLength, MaxTextChunkCapture) : 0;

            var typeCrc = Crc32.Update(Crc32.Begin(), type);
            var payload = await ReadChunkPayloadAsync(
                    stream, dataLength, captureLimit, typeCrc, buffer, cancellationToken)
                .ConfigureAwait(false);
            var crcBytes = new byte[4];
            await ReadFullyAsync(stream, crcBytes, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes) != payload.Crc)
                throw new InvalidDataException($"PNG chunk CRC 校验失败（偏移 {chunkOffset}）");

            var chunk = new PngChunk(chunkOffset, 12 + dataLength);
            if (chunkIndex == 0 && (!type.SequenceEqual("IHDR"u8) || dataLength != 13))
                throw new InvalidDataException("PNG 首个 chunk 必须是合法的 IHDR");

            if (isITxt && payload.Data is { } itxt)
            {
                if (itxt.Length < dataLength)
                {
                    if (ClaimsXmpKeyword(itxt))
                        throw new NotSupportedException("PNG iTXt XMP 超过支持的大小");
                }
                else if (ParseITxt(itxt, chunkOffset) is { } candidate)
                {
                    if (candidate.Compressed)
                        throw new NotSupportedException("PNG 的压缩 iTXt XMP 不受支持");
                    if (++xmpCount > 1)
                        throw new InvalidDataException("PNG 包含多个 XMP 包，无法确定 canonical");
                    xmp = new PngXmpChunk(chunkOffset, candidate.Prefix, candidate.Text);
                }
            }

            if (type.SequenceEqual("IEND"u8))
            {
                if (dataLength != 0)
                    throw new InvalidDataException("PNG IEND chunk 长度必须为零");
                iend = chunk;
                break;
            }

            position = chunkOffset + chunk.TotalLength;
            chunkIndex++;
        }

        if (iend is not { } iendChunk)
            throw new InvalidDataException("PNG 缺少 IEND chunk");
        return new PngLayout(iendChunk, xmp);
    }

    private static async Task<(byte[]? Data, uint Crc)> ReadChunkPayloadAsync(
        Stream stream,
        int length,
        int captureLimit,
        uint crc,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var captured = captureLimit > 0 ? new byte[captureLimit] : null;
        var capturedCount = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var take = Math.Min(buffer.Length, remaining);
            await ReadFullyAsync(stream, buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            crc = Crc32.Update(crc, buffer.AsSpan(0, take));
            if (captured != null && capturedCount < captured.Length)
            {
                var count = Math.Min(take, captured.Length - capturedCount);
                buffer.AsSpan(0, count).CopyTo(captured.AsSpan(capturedCount));
                capturedCount += count;
            }
            remaining -= take;
        }
        return (captured, Crc32.Finish(crc));
    }

    private sealed record ITxtCandidate(bool Compressed, byte[] Prefix, byte[] Text);

    private static ITxtCandidate? ParseITxt(byte[] data, long chunkOffset)
    {
        var span = data.AsSpan();
        var keywordEnd = span.IndexOf((byte)0);
        if (keywordEnd <= 0 || !span[..keywordEnd].SequenceEqual(XmpKeywordBytes))
            return null;
        if (span.Length < keywordEnd + 5)
            throw new InvalidDataException($"PNG iTXt XMP 结构不完整（偏移 {chunkOffset}）");

        var compressionFlag = span[keywordEnd + 1];
        var compressionMethod = span[keywordEnd + 2];
        if (compressionFlag == 1)
            return new ITxtCandidate(true, [], []);
        if (compressionFlag != 0 || compressionMethod != 0)
            throw new InvalidDataException($"PNG iTXt XMP 压缩标记非法（偏移 {chunkOffset}）");

        var rest = span[(keywordEnd + 3)..];
        var languageEnd = rest.IndexOf((byte)0);
        if (languageEnd < 0)
            throw new InvalidDataException($"PNG iTXt XMP 缺少语言标记（偏移 {chunkOffset}）");
        var translated = rest[(languageEnd + 1)..];
        var translatedEnd = translated.IndexOf((byte)0);
        if (translatedEnd < 0)
            throw new InvalidDataException($"PNG iTXt XMP 缺少翻译关键字（偏移 {chunkOffset}）");

        var text = translated[(translatedEnd + 1)..];
        if (text.IsEmpty)
            throw new InvalidDataException($"PNG iTXt XMP 包为空（偏移 {chunkOffset}）");

        var prefixLength = data.Length - text.Length;
        return new ITxtCandidate(false, span[..prefixLength].ToArray(), text.ToArray());
    }

    private static bool ClaimsXmpKeyword(byte[] payload)
    {
        var keywordEnd = payload.AsSpan().IndexOf((byte)0);
        return keywordEnd > 0 && payload.AsSpan(0, keywordEnd).SequenceEqual(XmpKeywordBytes);
    }

    private static async Task ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("PNG 数据提前结束");
            read += count;
        }
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return read;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                return false;
        }
        return true;
    }

    private readonly record struct PngChunk(long Offset, int TotalLength);

    private sealed record PngXmpChunk(long Offset, byte[] Prefix, byte[] Text);

    private sealed record PngLayout(PngChunk Iend, PngXmpChunk? Xmp);

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Begin() => 0xFFFFFFFFu;

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var index = 0; index < table.Length; index++)
            {
                var value = (uint)index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }
}
