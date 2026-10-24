using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// JPEG 内嵌 XMP 容器适配器：只把标准 APP1 <c>http://ns.adobe.com/xap/1.0/\0</c>
/// 段视为 canonical XMP，EXIF、Photoshop 与 extended XMP 段一律不触碰。
/// </summary>
public sealed class JpegXmpContainerAdapter : IXmpContainerAdapter
{
    private static readonly byte[] CanonicalXmpIdentifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    private const int MaxSegmentLength = 0xFFFF;
    private static readonly int MaxPacketLength =
        MaxSegmentLength - 2 - CanonicalXmpIdentifier.Length;

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
            var read = await ReadCanonicalAsync(session, cancellationToken).ConfigureAwait(false);
            return read.Packet;
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

    public async Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 0 到 5 之间");

        try
        {
            var revision = await ReadRevisionAsync(file, cancellationToken).ConfigureAwait(false);
            JpegRead read;
            await using (var session = await file
                .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                .ConfigureAwait(false))
            {
                read = await ReadCanonicalAsync(session, cancellationToken).ConfigureAwait(false);
            }

            var originalPacket = read.Packet;
            var before = originalPacket == null
                ? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)
                : ParseSnapshot(originalPacket.Data);

            if (read.Layout.Canonical is { } segment && originalPacket is { } original)
            {
                await VerifyOriginalAsync(file, original, safeMode, cancellationToken).ConfigureAwait(false);

                var updated = TrySetRating(original.Data, rating, preserveLength: true);
                if (updated is { } inPlace)
                {
                    await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                    await XmpContainerIO.WritePacketAsync(
                        file, original, inPlace, revision, safeMode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var expanded = SetRatingOrThrow(original.Data, rating, preserveLength: false);
                    var replacement = BuildApp1Segment(expanded);
                    await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                    await XmpContainerIO.RewriteAsync(
                        file,
                        [new XmpContainerEdit(segment.SegmentOffset, segment.SegmentLength, replacement)],
                        revision,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await VerifyOriginalAsync(file, null, safeMode, cancellationToken).ConfigureAwait(false);
                var created = SetRatingOrThrow(null, rating, preserveLength: false);
                var insertedSegment = BuildApp1Segment(created);
                await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                await XmpContainerIO.RewriteAsync(
                    file,
                    [new XmpContainerEdit(2, 0, insertedSegment)],
                    revision,
                    cancellationToken).ConfigureAwait(false);
            }

            var verified = await ReadSnapshotAsync(file, cancellationToken).ConfigureAwait(false);
            if (verified.Packet == null ||
                verified.Snapshot.State != XmpRatingState.Rated ||
                verified.Snapshot.Value != rating)
            {
                throw new StorageException(
                    StorageError.IoError,
                    $"{file.Name} 的 XMP Rating 写入后校验失败");
            }

            var receiptRevision = await ReadRevisionAsync(file, cancellationToken).ConfigureAwait(false);
            return new XmpRatingWriteReceipt
            {
                TargetFile = file,
                Before = before,
                After = verified.Snapshot,
                OriginalPacket = originalPacket,
                Revision = receiptRevision,
            };
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
            throw StorageException.From(exception, $"写入 {file.Name} 的 JPEG XMP");
        }
    }

    public async Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(receipt);

        try
        {
            if (receipt.Revision is not { } expectedRevision)
                throw new StorageException(
                    StorageError.Unsupported, $"{file.Name} 的写入回执缺少 revision，无法安全恢复");

            var revision = await EnsureRevisionAsync(file, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
            JpegRead read;
            await using (var session = await file
                .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                .ConfigureAwait(false))
            {
                read = await ReadCanonicalAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (receipt.OriginalPacket is { } original)
            {
                if (read.Layout.Canonical is not { } current)
                    throw StorageException.ChangedExternally(file.Name);

                if (read.Packet is { } currentPacket && currentPacket.Length == original.Data.Length)
                {
                    await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                    await XmpContainerIO.WritePacketAsync(
                        file, currentPacket, original.Data, revision, safeMode: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var replacement = BuildApp1Segment(original.Data);
                    await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                    await XmpContainerIO.RewriteAsync(
                        file,
                        [new XmpContainerEdit(current.SegmentOffset, current.SegmentLength, replacement)],
                        revision,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else if (read.Layout.Canonical is { } inserted)
            {
                await EnsureRevisionAsync(file, revision, cancellationToken).ConfigureAwait(false);
                await XmpContainerIO.RewriteAsync(
                    file,
                    [new XmpContainerEdit(inserted.SegmentOffset, inserted.SegmentLength, [])],
                    revision,
                    cancellationToken).ConfigureAwait(false);
            }

            var verified = await ReadSnapshotAsync(file, cancellationToken).ConfigureAwait(false);
            if (!SameSemanticRating(verified.Snapshot, receipt.Before))
            {
                throw new StorageException(
                    StorageError.IoError,
                    $"{file.Name} 的 XMP Rating 恢复后校验失败");
            }
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
            throw StorageException.From(exception, $"恢复 {file.Name} 的 JPEG XMP");
        }
    }

    private static async Task<JpegRead> ReadCanonicalAsync(
        IStorageRandomAccessSession session,
        CancellationToken cancellationToken)
    {
        var layout = await ParseLayoutAsync(session, cancellationToken).ConfigureAwait(false);
        if (layout.Canonical is not { } canonical)
            return new JpegRead(layout, null);

        var data = await ReadBytesAsync(
            session, canonical.PacketOffset, canonical.PacketLength, cancellationToken).ConfigureAwait(false);
        return new JpegRead(
            layout,
            new XmpContainerPacket(canonical.PacketOffset, canonical.PacketLength, data));
    }

    private static async Task<(XmpContainerPacket? Packet, XmpRatingSnapshot Snapshot)> ReadSnapshotAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        await using var session = await file
            .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
            .ConfigureAwait(false);
        var read = await ReadCanonicalAsync(session, cancellationToken).ConfigureAwait(false);
        if (read.Packet is not { } packet)
            return (null, XmpRatingSnapshot.Missing(XmpRatingSource.Embedded));
        return (packet, ParseSnapshot(packet.Data));
    }

    private static async Task VerifyOriginalAsync(
        IFileRef file,
        XmpContainerPacket? original,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        if (!safeMode) return;

        await using var session = await file
            .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
            .ConfigureAwait(false);
        var read = await ReadCanonicalAsync(session, cancellationToken).ConfigureAwait(false);

        if (original == null)
        {
            if (read.Packet != null)
                throw StorageException.ChangedExternally(file.Name);
            return;
        }

        if (read.Packet is not { } current ||
            current.Offset != original.Offset ||
            !current.Data.AsSpan().SequenceEqual(original.Data))
        {
            throw StorageException.ChangedExternally(file.Name);
        }
    }

    private static async Task<StorageRevision> EnsureRevisionAsync(
        IFileRef file,
        StorageRevision expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadRevisionAsync(file, cancellationToken).ConfigureAwait(false);
        if (!RevisionMatches(expected, current))
            throw StorageException.ChangedExternally(file.Name);
        return current;
    }

    private static bool RevisionMatches(StorageRevision expected, StorageRevision current)
    {
        if (expected.Size is { } size && current.Size is { } currentSize && size != currentSize)
            return false;
        if (expected.LastModified is { } modified &&
            current.LastModified is { } currentModified &&
            modified != currentModified)
        {
            return false;
        }
        if (expected.Version != null && current.Version != null && expected.Version != current.Version)
            return false;
        return true;
    }

    private static async Task<StorageRevision> ReadRevisionAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{file.Name} 已不存在");
        return properties.Revision ?? new StorageRevision(
            properties.Size.HasValue ? checked((long)properties.Size.Value) : null,
            properties.DateModified);
    }

    private static XmpRatingSnapshot ParseSnapshot(byte[] packet)
    {
        try
        {
            return XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StorageException(
                StorageError.Unsupported, "JPEG canonical XMP 包无法解析", exception);
        }
    }

    private static bool SameSemanticRating(XmpRatingSnapshot left, XmpRatingSnapshot right) =>
        left.State == right.State && left.Value == right.Value;

    private static byte[]? TrySetRating(byte[] template, int rating, bool preserveLength)
    {
        try
        {
            return XmpPacketEditor.SetRating(template, rating, preserveLength);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[] SetRatingOrThrow(byte[]? template, int rating, bool preserveLength)
    {
        try
        {
            return XmpPacketEditor.SetRating(template, rating, preserveLength);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StorageException(
                StorageError.Unsupported, "canonical XMP 包无法解析或重新序列化", exception);
        }
    }

    private static byte[] BuildApp1Segment(byte[] packet)
    {
        if (packet.Length > MaxPacketLength)
        {
            throw new StorageException(
                StorageError.Unsupported,
                $"XMP 包长度 {packet.Length} 超出 JPEG APP1 段上限 {MaxPacketLength}");
        }

        var segmentLength = 2 + CanonicalXmpIdentifier.Length + packet.Length;
        var segment = new byte[2 + segmentLength];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(segmentLength >> 8);
        segment[3] = (byte)segmentLength;
        CanonicalXmpIdentifier.CopyTo(segment, 4);
        packet.CopyTo(segment, 4 + CanonicalXmpIdentifier.Length);
        return segment;
    }

    private static async Task<JpegLayout> ParseLayoutAsync(
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
                        throw Unsupported("JPEG 中存在多个 canonical XMP APP1 段，无法确定写入目标");

                    var packetLength = payloadLength - CanonicalXmpIdentifier.Length;
                    if (packetLength == 0)
                        throw Malformed("JPEG canonical XMP 包为空");

                    canonical = new CanonicalSegment(
                        markerStart,
                        checked((int)(payloadOffset + payloadLength - markerStart)),
                        payloadOffset + CanonicalXmpIdentifier.Length,
                        packetLength);
                }
            }

            position = payloadOffset + payloadLength;
        }

        if (!terminated)
            throw Malformed("JPEG 缺少 EOI 或 SOS，文件可能被截断");

        return new JpegLayout(canonical);
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

    private sealed record JpegLayout(CanonicalSegment? Canonical);

    private sealed record CanonicalSegment(
        long SegmentOffset,
        int SegmentLength,
        long PacketOffset,
        int PacketLength);

    private sealed record JpegRead(JpegLayout Layout, XmpContainerPacket? Packet);
}
