using System.Buffers.Binary;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// WebP 内嵌 XMP 容器适配器：RIFF little-endian 的 <c>XMP </c> chunk。
/// 读取校验 RIFF 边界、chunk 长度与偶数对齐填充；多个 XMP chunk 明确
/// <see cref="StorageError.Unsupported"/>。写入由 <see cref="ContainerRatingStore"/>
/// 在同一包范围内做等长原位覆盖，不改 RIFF 长度也不动 VP8X 标志。
/// </summary>
public sealed class WebpXmpContainerAdapter : IXmpContainerAdapter
{
    private static readonly uint XmpFourCc = BinaryPrimitives.ReadUInt32LittleEndian("XMP "u8);
    private static readonly uint Vp8xFourCc = BinaryPrimitives.ReadUInt32LittleEndian("VP8X"u8);

    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);
        if (layout.XmpOffset is not { } offset || layout.XmpPayload is not { } payload) return null;
        return new XmpContainerPacket(offset, payload.Length, payload);
    }

    private static async Task<WebpLayout> ScanAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var headerLength = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerLength != header.Length ||
            !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("不是 WebP 文件");

        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (stream.CanSeek && riffEnd > stream.Length)
            throw new InvalidDataException("WebP RIFF 长度超出文件");

        var buffer = new byte[81920];
        WebpChunk? xmp = null;
        byte[]? xmpPayload = null;
        long position = header.Length;
        var chunkIndex = 0;

        while (position < riffEnd)
        {
            if (riffEnd - position < 8)
                throw new InvalidDataException($"WebP chunk 头不完整（偏移 {position}）");

            var chunkHeader = new byte[8];
            await ReadFullyAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            if (size > int.MaxValue - 8)
                throw new InvalidDataException($"WebP chunk 长度非法（偏移 {position}）");

            var payloadLength = (int)size;
            if (position + 8 + payloadLength > riffEnd)
                throw new InvalidDataException($"WebP chunk 超出 RIFF 边界（偏移 {position}）");
            var totalLength = 8L + payloadLength + (payloadLength & 1);
            if (position + totalLength > riffEnd)
                throw new InvalidDataException($"WebP chunk 缺少偶数对齐填充（偏移 {position}）");

            if (fourCc == Vp8xFourCc)
            {
                if (chunkIndex != 0)
                    throw new InvalidDataException("WebP 的 VP8X 必须是第一个 chunk");
                if (payloadLength != 10)
                    throw new InvalidDataException("WebP VP8X chunk 长度必须为 10");
            }

            if (fourCc == XmpFourCc)
            {
                if (xmp != null)
                    throw new InvalidDataException("WebP 包含多个 XMP chunk，无法确定 canonical");
                if (payloadLength == 0)
                    throw new InvalidDataException("WebP XMP chunk 为空");

                var payload = new byte[payloadLength];
                await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                if ((payloadLength & 1) != 0)
                    await SkipFullyAsync(stream, 1, buffer, cancellationToken).ConfigureAwait(false);
                xmpPayload = payload;
                xmp = new WebpChunk(position, payloadLength, totalLength);
            }
            else
            {
                await SkipFullyAsync(stream, payloadLength + (payloadLength & 1), buffer, cancellationToken)
                    .ConfigureAwait(false);
            }

            position += totalLength;
            chunkIndex++;
        }

        if (position != riffEnd)
            throw new InvalidDataException("WebP RIFF 长度与 chunk 布局不一致");

        return new WebpLayout(xmp is { } chunk ? chunk.Offset + 8 : null, xmpPayload);
    }

    private static async Task ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("WebP 数据提前结束");
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

    private static async Task SkipFullyAsync(
        Stream stream,
        int count,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("WebP 数据提前结束");
            remaining -= read;
        }
    }

    private readonly record struct WebpChunk(long Offset, int PayloadLength, long TotalLength);

    private sealed record WebpLayout(long? XmpOffset, byte[]? XmpPayload);
}
