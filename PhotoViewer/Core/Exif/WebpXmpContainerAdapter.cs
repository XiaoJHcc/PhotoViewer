using System.Buffers.Binary;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// WebP 内嵌 XMP 容器适配器：RIFF little-endian 的 <c>XMP </c> chunk。
/// 读取校验 chunk 边界与偶数对齐填充；写入更新 RIFF 长度，新增/删除 XMP chunk 时维护 VP8X 的 XMP 标志位。
/// 完整 chunk 等长（RIFF 尺寸与 VP8X 标志不变）时原位覆盖（只需随机访问），其余情况走原子替换。
/// </summary>
public sealed class WebpXmpContainerAdapter : IXmpContainerAdapter
{
    private const byte XmpFlag = 0x04;

    private static readonly uint XmpFourCc = BinaryPrimitives.ReadUInt32LittleEndian("XMP "u8);
    private static readonly uint Vp8xFourCc = BinaryPrimitives.ReadUInt32LittleEndian("VP8X"u8);

    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);
        return ToPacket(layout);
    }

    public async Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));

        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{file.Name} 已不存在");

        WebpLayout layout;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (safeMode && layout.Xmp is { } existing && layout.XmpChunkBytes is { } existingBytes)
            await EnsureChunkUnchangedAsync(file, existing.Offset, existingBytes, cancellationToken)
                .ConfigureAwait(false);

        var packet = BuildPacket(layout.XmpPayload, rating);
        var chunkBytes = BuildChunk(packet);

        var pending = new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = Snapshot(layout.XmpPayload),
            After = XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded),
            OriginalPacket = ToPacket(layout),
        };

        var wroteInPlace = layout.Xmp is { } current &&
                           layout.XmpChunkBytes is { } currentChunkBytes &&
                           await TryWriteInPlaceAsync(
                               file,
                               current.Offset,
                               currentChunkBytes,
                               chunkBytes,
                               properties.Revision,
                               safeMode,
                               cancellationToken).ConfigureAwait(false);

        if (!wroteInPlace)
        {
            var delta = chunkBytes.Length - (layout.Xmp?.TotalLength ?? 0);
            var edits = new List<XmpContainerEdit>();
            if (layout.Xmp is { } target)
                edits.Add(new XmpContainerEdit(target.Offset, target.TotalLength, chunkBytes));
            else
                edits.Add(new XmpContainerEdit(layout.RiffEnd, 0, chunkBytes));
            edits.Add(new XmpContainerEdit(4, 4, RiffSizeBytes(layout.RiffEnd - 8 + delta, file.Name)));
            if (layout.Xmp == null && layout.Vp8x is { } vp8x)
                edits.Add(new XmpContainerEdit(vp8x.Offset + 8, 1, [(byte)(layout.Vp8xFlags | XmpFlag)]));

            await XmpContainerIO.RewriteAsync(file, edits, properties.Revision, cancellationToken)
                .ConfigureAwait(false);
        }

        XmpRatingSnapshot verified;
        try
        {
            verified = await VerifyAsync(file, packet, rating, layout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception verificationError)
        {
            await RollbackAsync(file, pending, verificationError).ConfigureAwait(false);
            throw;
        }

        return new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = pending.Before,
            After = verified,
            OriginalPacket = pending.OriginalPacket,
            Revision = (await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false)).Revision,
            CreatedTarget = false,
        };
    }

    public async Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.TargetFile.StorageId != file.StorageId)
            throw new ArgumentException("回执与目标文件不一致", nameof(receipt));

        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (properties.Exists && receipt.Revision is { Size: { } expectedSize } &&
            properties.Size != (ulong)expectedSize)
            throw StorageException.ChangedExternally(file.Name);

        WebpLayout layout;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (receipt.OriginalPacket is { } original)
        {
            if (layout.Xmp is not { } current)
                throw StorageException.ChangedExternally(file.Name);
            var alreadyRestored = original.Descriptor is { } originalDescriptor
                ? layout.XmpChunkBytes is { } currentChunkBytes &&
                  currentChunkBytes.AsSpan().SequenceEqual(originalDescriptor.Data)
                : layout.XmpPayload is { } currentPayload && currentPayload.AsSpan().SequenceEqual(original.Data);
            if (alreadyRestored)
                return;
            EnsureCurrentRatingMatches(layout.XmpPayload, receipt, file.Name);

            var restoreChunk = original.Descriptor?.Data ?? BuildChunk(original.Data);
            var restoredInPlace = layout.XmpChunkBytes is { } chunkBytes &&
                                  await TryWriteInPlaceAsync(
                                      file,
                                      current.Offset,
                                      chunkBytes,
                                      restoreChunk,
                                      properties.Revision,
                                      safeMode: false,
                                      cancellationToken).ConfigureAwait(false);
            if (!restoredInPlace)
            {
                var edits = new List<XmpContainerEdit>
                {
                    new(current.Offset, current.TotalLength, restoreChunk),
                    new(4, 4, RiffSizeBytes(
                        layout.RiffEnd - 8 + restoreChunk.Length - current.TotalLength, file.Name)),
                };

                await XmpContainerIO.RewriteAsync(file, edits, properties.Revision, cancellationToken)
                    .ConfigureAwait(false);
            }

            var restored = await ReadCanonicalPacketAsync(file, cancellationToken).ConfigureAwait(false);
            if (restored == null || !restored.Data.AsSpan().SequenceEqual(original.Data))
                throw new InvalidOperationException("WebP XMP 回滚后内容不一致");
            return;
        }

        if (receipt.Before.State != XmpRatingState.Missing)
            throw new ArgumentException("回执缺少原包，但写入前已有 XMP", nameof(receipt));
        if (layout.Xmp is not { } added)
            return;
        EnsureCurrentRatingMatches(layout.XmpPayload, receipt, file.Name);

        var removalEdits = new List<XmpContainerEdit>
        {
            new(added.Offset, added.TotalLength, Array.Empty<byte>()),
            new(4, 4, RiffSizeBytes(layout.RiffEnd - 8 - added.TotalLength, file.Name)),
        };
        if (layout.Vp8x is { } removalVp8x && (layout.Vp8xFlags & XmpFlag) != 0)
            removalEdits.Add(new XmpContainerEdit(removalVp8x.Offset + 8, 1, [(byte)(layout.Vp8xFlags & ~XmpFlag)]));

        await XmpContainerIO.RewriteAsync(file, removalEdits, properties.Revision, cancellationToken)
            .ConfigureAwait(false);

        var after = await ReadCanonicalPacketAsync(file, cancellationToken).ConfigureAwait(false);
        if (after != null)
            throw new InvalidOperationException("WebP XMP 回滚后仍存在 XMP chunk");
    }

    private async Task RollbackAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        Exception verificationError)
    {
        try
        {
            await RestoreAsync(file, receipt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception restoreError) when (restoreError is not OperationCanceledException)
        {
            throw new StorageException(
                StorageError.IoError,
                $"XMP 写入后校验失败且回滚失败：{verificationError.Message}；{restoreError.Message}",
                verificationError);
        }

        throw new InvalidOperationException($"XMP 写入后校验失败，已回滚：{verificationError.Message}", verificationError);
    }

    private static async Task<XmpRatingSnapshot> VerifyAsync(
        IFileRef file,
        byte[] packet,
        int rating,
        WebpLayout before,
        CancellationToken cancellationToken)
    {
        WebpLayout after;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            after = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (after.XmpPayload is not { } payload)
            throw new InvalidOperationException("写入后 WebP 中未找到 XMP chunk");
        if (!payload.AsSpan().SequenceEqual(packet))
            throw new InvalidOperationException("写入后 WebP XMP 内容不一致");

        var snapshot = XmpRatingReader.ReadPacket(payload, XmpRatingSource.Embedded);
        if (snapshot.State != XmpRatingState.Rated || snapshot.Value != rating)
            throw new InvalidOperationException("写入后 WebP XMP Rating 回读不一致");
        if (before.Xmp == null && after.Vp8x != null && (after.Vp8xFlags & XmpFlag) == 0)
            throw new InvalidOperationException("写入后 WebP VP8X 未标记 XMP");

        var expected = before.Chunks.Where(chunk => chunk.FourCc != XmpFourCc).ToArray();
        var actual = after.Chunks.Where(chunk => chunk.FourCc != XmpFourCc).ToArray();
        if (expected.Length != actual.Length)
            throw new InvalidOperationException("写入后 WebP 其它 chunk 数量发生变化");
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].FourCc != actual[index].FourCc ||
                expected[index].PayloadLength != actual[index].PayloadLength)
                throw new InvalidOperationException("写入后 WebP 其它 chunk 结构发生变化");
        }
        if (before.Vp8x != null && after.Vp8x != null &&
            (byte)(before.Vp8xFlags & ~XmpFlag) != (byte)(after.Vp8xFlags & ~XmpFlag))
            throw new InvalidOperationException("写入后 WebP VP8X 其它标志位发生变化");

        return snapshot;
    }

    private static XmpRatingSnapshot Snapshot(byte[]? packet) =>
        packet == null
            ? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)
            : XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);

    private static void EnsureCurrentRatingMatches(byte[]? packet, XmpRatingWriteReceipt receipt, string fileName)
    {
        if (packet == null || receipt.After.State != XmpRatingState.Rated) return;
        XmpRatingSnapshot current;
        try
        {
            current = XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);
        }
        catch (XmpException)
        {
            throw StorageException.ChangedExternally(fileName);
        }
        if (current.State != receipt.After.State || current.Value != receipt.After.Value)
            throw StorageException.ChangedExternally(fileName);
    }

    private static byte[] BuildPacket(byte[]? template, int rating)
    {
        if (template == null) return XmpPacketEditor.SetRating(null, rating, preserveLength: false);
        try
        {
            return XmpPacketEditor.SetRating(template, rating, preserveLength: true);
        }
        catch (Exception exception) when (exception is XmpException or InvalidOperationException)
        {
            try
            {
                return XmpPacketEditor.SetRating(template, rating, preserveLength: false);
            }
            catch (XmpException parseError)
            {
                throw new InvalidOperationException($"现有 XMP 包无法解析，拒绝覆盖：{parseError.Message}", parseError);
            }
        }
    }

    private static XmpContainerPacket? ToPacket(WebpLayout layout)
    {
        if (layout.Xmp is not { } chunk ||
            layout.XmpPayload is not { } payload ||
            layout.XmpChunkBytes is not { } chunkBytes)
            return null;
        return new XmpContainerPacket(
            chunk.Offset + 8,
            chunk.PayloadLength,
            payload,
            new XmpContainerPacketDescriptor(chunk.Offset, chunkBytes));
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
        var chunks = new List<WebpChunk>();
        WebpChunk? vp8x = null;
        byte vp8xFlags = 0;
        WebpChunk? xmp = null;
        byte[]? xmpPayload = null;
        byte[]? xmpChunkBytes = null;
        long position = header.Length;

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
            var totalLength = 8 + payloadLength + (payloadLength & 1);
            if (position + totalLength > riffEnd)
                throw new InvalidDataException($"WebP chunk 缺少偶数对齐填充（偏移 {position}）");

            if (fourCc == Vp8xFourCc)
            {
                if (chunks.Count != 0)
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
                var chunkBytes = new byte[totalLength];
                chunkHeader.CopyTo(chunkBytes);
                await ReadFullyAsync(stream, chunkBytes.AsMemory(8, payloadLength), cancellationToken)
                    .ConfigureAwait(false);
                if ((payloadLength & 1) != 0)
                    await ReadFullyAsync(stream, chunkBytes.AsMemory(8 + payloadLength, 1), cancellationToken)
                        .ConfigureAwait(false);
                xmpPayload = chunkBytes.AsSpan(8, payloadLength).ToArray();
                xmpChunkBytes = chunkBytes;
                xmp = new WebpChunk(position, payloadLength, totalLength, fourCc);
            }
            else if (fourCc == Vp8xFourCc)
            {
                var vp8xPayload = new byte[payloadLength];
                await ReadFullyAsync(stream, vp8xPayload, cancellationToken).ConfigureAwait(false);
                vp8xFlags = vp8xPayload[0];
                vp8x = new WebpChunk(position, payloadLength, totalLength, fourCc);
            }
            else
            {
                await SkipFullyAsync(stream, payloadLength + (payloadLength & 1), buffer, cancellationToken)
                    .ConfigureAwait(false);
            }

            chunks.Add(new WebpChunk(position, payloadLength, totalLength, fourCc));
            position += totalLength;
        }

        if (position != riffEnd)
            throw new InvalidDataException("WebP RIFF 长度与 chunk 布局不一致");

        return new WebpLayout(riffEnd, chunks, vp8x, vp8xFlags, xmp, xmpPayload, xmpChunkBytes);
    }

    private static byte[] BuildChunk(byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        "XMP "u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    private static byte[] RiffSizeBytes(long riffSize, string fileName)
    {
        if (riffSize is < 0 or > uint.MaxValue)
            throw new StorageException(StorageError.Unsupported, $"{fileName} 超出 WebP 可表示的大小");
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)riffSize);
        return bytes;
    }

    /// <summary>
    /// 完整 XMP chunk 等长（RIFF 尺寸与 VP8X 标志都不变）时原位覆盖，
    /// 供只有随机访问、没有原子替换的存储使用；返回 false 表示应改走原子重写。
    /// </summary>
    private static async Task<bool> TryWriteInPlaceAsync(
        IFileRef file,
        long offset,
        byte[] originalBytes,
        byte[] updatedBytes,
        StorageRevision? expectedRevision,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        if (updatedBytes.Length != originalBytes.Length) return false;
        if (file.Capabilities.RandomAccessWrite == StorageCapability.Unsupported) return false;

        var originalPacket = new XmpContainerPacket(
            offset,
            originalBytes.Length,
            originalBytes,
            new XmpContainerPacketDescriptor(offset, originalBytes));
        try
        {
            await XmpContainerIO.WritePacketAsync(
                file,
                originalPacket,
                updatedBytes,
                expectedRevision,
                safeMode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is NotSupportedException ||
            exception is StorageException { Error: StorageError.Unsupported or StorageError.NotSeekable })
        {
            return false;
        }
    }

    private static async Task EnsureChunkUnchangedAsync(
        IFileRef file,
        long offset,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadRangeAsync(file, offset, expected.Length, cancellationToken).ConfigureAwait(false);
        if (!current.AsSpan().SequenceEqual(expected))
            throw StorageException.ChangedExternally(file.Name);
    }

    private static async Task<byte[]> ReadRangeAsync(
        IFileRef file,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var session = await file
                .OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                .ConfigureAwait(false);
            return await XmpContainerIO.ReadExactlyAsync(session, offset, length, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is NotSupportedException ||
            exception is StorageException { Error: StorageError.Unsupported or StorageError.NotSeekable })
        {
            await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            await SkipAsync(stream, offset, cancellationToken).ConfigureAwait(false);
            var data = new byte[length];
            await ReadFullyAsync(stream, data, cancellationToken).ConfigureAwait(false);
            return data;
        }
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

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("WebP 数据提前结束");
            remaining -= read;
        }
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

    private readonly record struct WebpChunk(long Offset, int PayloadLength, int TotalLength, uint FourCc);

    private sealed record WebpLayout(
        long RiffEnd,
        IReadOnlyList<WebpChunk> Chunks,
        WebpChunk? Vp8x,
        byte Vp8xFlags,
        WebpChunk? Xmp,
        byte[]? XmpPayload,
        byte[]? XmpChunkBytes);
}
