using System.Buffers.Binary;
using System.Text;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// PNG 内嵌 XMP 容器适配器：iTXt keyword <c>XML:com.adobe.xmp</c>，仅未压缩文本。
/// 读取逐 chunk 校验边界与 CRC；写入重建 iTXt 长度与 CRC，并保留语言/翻译字段。
/// tEXt/zTXt 中的 XMP 不视为 canonical，写入时拒绝以免留下冲突包。
/// 完整 chunk 等长时原位覆盖（只需随机访问），扩容或新增走原子替换。
/// </summary>
public sealed class PngXmpContainerAdapter : IXmpContainerAdapter
{
    public const string XmpKeyword = "XML:com.adobe.xmp";

    private const int MaxChunkDataLength = int.MaxValue - 12;
    private const int TextKeywordProbeLength = 256;
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
        return layout.Xmp is { } xmp ? ToPacket(xmp) : null;
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

        PngLayout layout;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (layout.HasForeignXmp)
            throw new NotSupportedException("PNG 的 tEXt/zTXt XMP 不受支持，写入会保留旧的冲突包");

        var original = layout.Xmp;
        if (original != null && safeMode)
            await EnsureChunkUnchangedAsync(file, original.Chunk.Offset, original.ChunkBytes, cancellationToken)
                .ConfigureAwait(false);

        var packet = BuildPacket(original?.Text, rating);
        var chunkBytes = BuildChunk(original?.Prefix ?? CreateXmpPrefix(), packet);

        var pending = new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = Snapshot(original?.Text),
            After = XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded),
            OriginalPacket = original == null ? null : ToPacket(original),
        };

        if (original == null || !await TryWriteInPlaceAsync(
                file,
                original.Chunk.Offset,
                original.ChunkBytes,
                chunkBytes,
                properties.Revision,
                safeMode,
                cancellationToken).ConfigureAwait(false))
        {
            var edits = original == null
                ? new[] { new XmpContainerEdit(layout.Iend.Offset, 0, chunkBytes) }
                : new[] { new XmpContainerEdit(original.Chunk.Offset, original.Chunk.TotalLength, chunkBytes) };
            await XmpContainerIO.RewriteAsync(file, edits, properties.Revision, cancellationToken)
                .ConfigureAwait(false);
        }

        XmpRatingSnapshot verified;
        try
        {
            verified = await VerifyAsync(file, packet, rating, cancellationToken).ConfigureAwait(false);
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

        PngLayout layout;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (receipt.OriginalPacket is { } original)
        {
            if (layout.Xmp is not { } current)
                throw StorageException.ChangedExternally(file.Name);
            if (original.Descriptor is { } originalDescriptor
                    ? current.ChunkBytes.AsSpan().SequenceEqual(originalDescriptor.Data)
                    : current.Text.AsSpan().SequenceEqual(original.Data))
                return;
            EnsureCurrentRatingMatches(current.Text, receipt, file.Name);

            var restoreChunk = original.Descriptor?.Data ?? BuildChunk(CreateXmpPrefix(), original.Data);
            if (!await TryWriteInPlaceAsync(
                    file,
                    current.Chunk.Offset,
                    current.ChunkBytes,
                    restoreChunk,
                    properties.Revision,
                    safeMode: false,
                    cancellationToken).ConfigureAwait(false))
            {
                await XmpContainerIO.RewriteAsync(
                    file,
                    [new XmpContainerEdit(current.Chunk.Offset, current.Chunk.TotalLength, restoreChunk)],
                    properties.Revision,
                    cancellationToken).ConfigureAwait(false);
            }

            var restored = await ReadCanonicalPacketAsync(file, cancellationToken).ConfigureAwait(false);
            if (restored == null || !restored.Data.AsSpan().SequenceEqual(original.Data))
                throw new InvalidOperationException("PNG XMP 回滚后内容不一致");
            return;
        }

        if (receipt.Before.State != XmpRatingState.Missing)
            throw new ArgumentException("回执缺少原包，但写入前已有 XMP", nameof(receipt));
        if (layout.Xmp is not { } added)
            return;
        EnsureCurrentRatingMatches(added.Text, receipt, file.Name);

        await XmpContainerIO.RewriteAsync(
            file,
            [new XmpContainerEdit(added.Chunk.Offset, added.Chunk.TotalLength, Array.Empty<byte>())],
            properties.Revision,
            cancellationToken).ConfigureAwait(false);

        var after = await ReadCanonicalPacketAsync(file, cancellationToken).ConfigureAwait(false);
        if (after != null)
            throw new InvalidOperationException("PNG XMP 回滚后仍存在 XMP");
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
        CancellationToken cancellationToken)
    {
        PngLayout layout;
        await using (var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            layout = await ScanAsync(stream, cancellationToken).ConfigureAwait(false);

        if (layout.Xmp is not { } xmp)
            throw new InvalidOperationException("写入后 PNG 中未找到 XMP");
        if (!xmp.Text.AsSpan().SequenceEqual(packet))
            throw new InvalidOperationException("写入后 PNG XMP 内容不一致");

        var snapshot = XmpRatingReader.ReadPacket(xmp.Text, XmpRatingSource.Embedded);
        if (snapshot.State != XmpRatingState.Rated || snapshot.Value != rating)
            throw new InvalidOperationException("写入后 PNG XMP Rating 回读不一致");
        return snapshot;
    }

    private static XmpRatingSnapshot Snapshot(byte[]? packet) =>
        packet == null
            ? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)
            : XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);

    private static void EnsureCurrentRatingMatches(byte[] packet, XmpRatingWriteReceipt receipt, string fileName)
    {
        if (receipt.After.State != XmpRatingState.Rated) return;
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

    private static XmpContainerPacket ToPacket(PngXmpChunk xmp) =>
        new(
            xmp.Chunk.Offset + 8 + xmp.Prefix.Length,
            xmp.Text.Length,
            xmp.Text,
            new XmpContainerPacketDescriptor(xmp.Chunk.Offset, xmp.ChunkBytes));

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
        var hasForeignXmp = false;
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
            var isText = type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8);
            var isITxt = type.SequenceEqual("iTXt"u8);
            var captureLimit = isITxt
                ? Math.Min(dataLength, MaxTextChunkCapture)
                : isText
                    ? Math.Min(dataLength, TextKeywordProbeLength)
                    : 0;

            var typeCrc = Crc32.Update(Crc32.Begin(), type);
            var payload = await ReadChunkPayloadAsync(stream, dataLength, captureLimit, typeCrc, buffer, cancellationToken)
                .ConfigureAwait(false);
            var crcBytes = new byte[4];
            await ReadFullyAsync(stream, crcBytes, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes) != payload.Crc)
                throw new InvalidDataException($"PNG chunk CRC 校验失败（偏移 {chunkOffset}）");

            var chunk = new PngChunk(chunkOffset, 12 + dataLength);
            if (chunkIndex == 0 && (!type.SequenceEqual("IHDR"u8) || dataLength != 13))
                throw new InvalidDataException("PNG 首个 chunk 必须是合法的 IHDR");

            if (isText && ClaimsXmpKeyword(payload.Data))
                hasForeignXmp = true;

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
                    xmp = new PngXmpChunk(
                        chunk,
                        BuildChunkRecord(header, itxt, crcBytes),
                        candidate.Prefix,
                        candidate.Text);
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
        return new PngLayout(iendChunk, xmp, hasForeignXmp);
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

    private static bool ClaimsXmpKeyword(byte[]? payload)
    {
        if (payload == null) return false;
        var keywordEnd = payload.AsSpan().IndexOf((byte)0);
        return keywordEnd > 0 && payload.AsSpan(0, keywordEnd).SequenceEqual(XmpKeywordBytes);
    }

    private static byte[] CreateXmpPrefix()
    {
        var prefix = new byte[XmpKeywordBytes.Length + 5];
        XmpKeywordBytes.CopyTo(prefix, 0);
        return prefix;
    }

    private static byte[] BuildChunk(byte[] prefix, byte[] text)
    {
        var dataLength = prefix.Length + text.Length;
        var chunk = new byte[12 + dataLength];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)dataLength);
        "iTXt"u8.CopyTo(chunk.AsSpan(4));
        prefix.CopyTo(chunk.AsSpan(8));
        text.CopyTo(chunk.AsSpan(8 + prefix.Length));
        var crc = Crc32.Finish(Crc32.Update(Crc32.Begin(), chunk.AsSpan(4, 4 + dataLength)));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + dataLength), crc);
        return chunk;
    }

    private static byte[] BuildChunkRecord(byte[] header, byte[] data, byte[] crcBytes)
    {
        var record = new byte[12 + data.Length];
        header.CopyTo(record);
        data.CopyTo(record.AsSpan(8));
        crcBytes.CopyTo(record.AsSpan(8 + data.Length));
        return record;
    }

    /// <summary>
    /// 完整 chunk 等长时原位覆盖，供只有随机访问、没有原子替换的存储使用；
    /// 返回 false 表示能力或长度不满足，调用方应改走原子重写。
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
            if (read == 0) throw new EndOfStreamException("PNG 数据提前结束");
            remaining -= read;
        }
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

    private sealed record PngXmpChunk(PngChunk Chunk, byte[] ChunkBytes, byte[] Prefix, byte[] Text);

    private sealed record PngLayout(PngChunk Iend, PngXmpChunk? Xmp, bool HasForeignXmp);

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
