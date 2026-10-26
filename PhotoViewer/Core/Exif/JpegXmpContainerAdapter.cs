using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// JPEG 内嵌 XMP 容器适配器：只把标准 APP1 <c>http://ns.adobe.com/xap/1.0/\0</c>
/// 段视为 canonical XMP，EXIF、Photoshop 与 extended XMP 段一律不触碰。
/// </summary>
/// <remarks>
/// 段扫描严格按 marker 链推进并校验段长不越过文件末尾；多个 canonical APP1 明确
/// <see cref="StorageError.Unsupported"/>，不猜写入/读取目标。写入由
/// <see cref="ContainerRatingStore"/> 在同一包范围内做等长原位覆盖。
/// </remarks>
public sealed class JpegXmpContainerAdapter : IXmpContainerAdapter
{
    private static readonly byte[] CanonicalXmpIdentifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            await using var session = await file
                .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                .ConfigureAwait(false);
            var canonical = await ParseLayoutAsync(session, cancellationToken).ConfigureAwait(false);
            if (canonical is not { } segment) return null;
            var data = await ReadBytesAsync(
                    session, segment.PacketOffset, segment.PacketLength, cancellationToken)
                .ConfigureAwait(false);
            return new XmpContainerPacket(segment.PacketOffset, segment.PacketLength, data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, $"解析 {file.Name} 的 JPEG XMP");
        }
    }

    private static async Task<CanonicalSegment?> ParseLayoutAsync(
        IStorageRandomAccessSession session,
        CancellationToken cancellationToken)
    {
        var fileLength = session.Length;
        if (fileLength < 4)
            throw Malformed("JPEG 文件过短");

        var header = await ReadBytesAsync(session, 0, 2, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0xFF || header[1] != 0xD8)
            throw Malformed("文件不是以 SOI 开头的 JPEG");

        CanonicalSegment? canonical = null;
        long position = 2;
        var terminated = false;

        while (position < fileLength)
        {
            var marker = await ReadBytesAsync(session, position, 2, cancellationToken).ConfigureAwait(false);
            if (marker[0] != 0xFF)
                throw Malformed($"偏移 {position} 处缺少 JPEG marker 前缀");

            var markerStart = position;
            var code = (int)marker[1];
            position += 2;

            while (code == 0xFF)
            {
                var fill = await ReadBytesAsync(session, position, 1, cancellationToken).ConfigureAwait(false);
                code = fill[0];
                position++;
            }

            if (code == 0x00)
                throw Malformed($"偏移 {markerStart} 处出现无效 marker 0x00");

            if (code == 0xD9)
            {
                terminated = true;
                break;
            }

            if (code == 0xD8)
                throw Malformed("JPEG 中出现重复的 SOI");

            if (code == 0xDA)
            {
                var sosLengthBytes = await ReadBytesAsync(session, position, 2, cancellationToken)
                    .ConfigureAwait(false);
                var sosLength = (sosLengthBytes[0] << 8) | sosLengthBytes[1];
                if (sosLength < 2 || position + sosLength > fileLength)
                    throw Malformed("JPEG SOS 段长度无效");
                terminated = true;
                break;
            }

            if (code == 0x01 || code is >= 0xD0 and <= 0xD7)
                continue;

            var lengthBytes = await ReadBytesAsync(session, position, 2, cancellationToken).ConfigureAwait(false);
            var length = (lengthBytes[0] << 8) | lengthBytes[1];
            if (length < 2)
                throw Malformed($"偏移 {markerStart} 处 JPEG 段长度无效");

            var payloadOffset = position + 2;
            var payloadLength = length - 2;
            if (payloadLength > fileLength - payloadOffset)
                throw Malformed($"偏移 {markerStart} 处 JPEG 段被截断");

            if (code == 0xE1 && payloadLength >= CanonicalXmpIdentifier.Length)
            {
                var prefix = await ReadBytesAsync(
                        session, payloadOffset, CanonicalXmpIdentifier.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (prefix.AsSpan().SequenceEqual(CanonicalXmpIdentifier))
                {
                    if (canonical != null)
                        throw Unsupported("JPEG 中存在多个 canonical XMP APP1 段，无法确定 canonical 包");

                    var packetLength = payloadLength - CanonicalXmpIdentifier.Length;
                    if (packetLength == 0)
                        throw Malformed("JPEG canonical XMP 包为空");

                    canonical = new CanonicalSegment(
                        payloadOffset + CanonicalXmpIdentifier.Length,
                        packetLength);
                }
            }

            position = payloadOffset + payloadLength;
        }

        if (!terminated)
            throw Malformed("JPEG 缺少 EOI 或 SOS，文件可能被截断");

        return canonical;
    }

    private static async Task<byte[]> ReadBytesAsync(
        IStorageRandomAccessSession session,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || length > session.Length - offset)
            throw Malformed("JPEG 结构被截断");
        return await XmpContainerIO.ReadExactlyAsync(session, offset, length, cancellationToken)
            .ConfigureAwait(false);
    }

    private static StorageException Malformed(string message) =>
        new(StorageError.Unsupported, $"JPEG 结构不支持或不完整：{message}");

    private static StorageException Unsupported(string message) =>
        new(StorageError.Unsupported, message);

    private sealed record CanonicalSegment(long PacketOffset, int PacketLength);
}
