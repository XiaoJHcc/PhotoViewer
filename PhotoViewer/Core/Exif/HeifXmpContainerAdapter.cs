using System.Buffers.Binary;
using System.Text;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// HEIF/HEIC/HIF/AVIF 等 ISOBMFF 容器的 XMP 读写适配器。
/// 只认 <c>meta</c> 下 <c>iinf</c> 中类型为 <c>mime</c>、内容类型为 XML/RDF 的项，
/// 通过 <c>iloc</c> 单 extent 定位到 <c>mdat</c> 内的精确文件范围。
/// 多 extent、<c>idat</c> 承载、长度不定或无法证明范围的结构明确报不支持。
/// </summary>
public sealed class HeifXmpContainerAdapter : IXmpContainerAdapter
{
    private const uint BoxMeta = 0x6D657461;
    private const uint BoxPitm = 0x7069746D;
    private const uint BoxIinf = 0x69696E66;
    private const uint BoxInfe = 0x696E6665;
    private const uint BoxIloc = 0x696C6F63;
    private const uint BoxMdat = 0x6D646174;
    private const uint ItemTypeMime = 0x6D696D65;

    private const int MaxTopLevelBoxes = 4096;
    private const int MaxMetaBytes = 64 << 20;
    private const int MaxXmpPacketBytes = 64 << 20;
    private const int MaxExtentCount = 4096;
    private const int MaxIlocItems = 65536;

    private const string RdfXmlContentType = "application/rdf+xml";

    /// <inheritdoc/>
    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var document = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false);
        return document?.Packet;
    }

    /// <inheritdoc/>
    public async Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));

        var document = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw Unsupported("文件中没有 XMP mime 项，新增 XMP 项不受支持");
        var original = document.Packet;
        var before = XmpRatingReader.ReadPacket(original.Data, XmpRatingSource.Embedded);

        if (before.State == XmpRatingState.Rated && before.Value == rating)
        {
            return new XmpRatingWriteReceipt
            {
                TargetFile = file,
                Before = before,
                After = before,
                OriginalPacket = original,
                Revision = document.Revision,
                CreatedTarget = false,
            };
        }

        byte[]? sameLength = null;
        try
        {
            sameLength = XmpPacketEditor.SetRating(original.Data, rating, preserveLength: true);
        }
        catch (XmpException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (sameLength is not null && sameLength.Length == original.Length)
            return await WriteInPlaceAsync(
                file, document, original, sameLength, rating, before, safeMode, cancellationToken)
                .ConfigureAwait(false);

        var updated = XmpPacketEditor.SetRating(original.Data, rating, preserveLength: false);
        if (updated.Length > MaxXmpPacketBytes)
            throw Unsupported("XMP 包超过解析上限");
        if (updated.Length == original.Length)
        {
            return await WriteInPlaceAsync(
                file, document, original, updated, rating, before, safeMode, cancellationToken)
                .ConfigureAwait(false);
        }

        return await WriteAppendedAsync(
            file, document, original, updated, rating, before, safeMode, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(receipt);

        var original = receipt.OriginalPacket
            ?? throw Unsupported("文件中没有 XMP mime 项，无法撤销新增项");
        var originalIndex = original.Descriptor
            ?? throw Unsupported("原始包缺少 iloc 索引，无法恢复");
        if (original.Data.Length != original.Length)
            throw Unsupported("原始包长度与数据不一致");

        var document = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw Unsupported("当前文件已没有 XMP mime 项，无法恢复索引");
        var current = document.Packet;
        var currentIndex = current.Descriptor
            ?? throw Unsupported("当前 XMP 项缺少 iloc 索引");
        if (currentIndex.Offset != originalIndex.Offset ||
            currentIndex.Data.Length != originalIndex.Data.Length)
        {
            throw Unsupported("iloc 项位置或长度已变化，无法原位恢复");
        }

        var recordedLength = receipt.OriginalFileLength;
        if (recordedLength is { } originalLength)
        {
            if (document.FileLength < originalLength)
                throw Unsupported("当前文件短于回执记录的原长度，无法恢复追加区");
            if (document.FileLength > originalLength)
            {
                EnsureRevisionMatches(receipt.Revision, document.Revision, file.Name);
                EnsureAppendedTail(document, originalLength, current, receipt.After, file.Name);
            }
        }

        var edits = new List<XmpContainerEdit>(2);
        if (!currentIndex.Data.AsSpan().SequenceEqual(originalIndex.Data))
        {
            edits.Add(new XmpContainerEdit(
                originalIndex.Offset,
                originalIndex.Data.Length,
                originalIndex.Data));
        }

        if (current.Offset != original.Offset ||
            current.Length != original.Length ||
            !current.Data.AsSpan().SequenceEqual(original.Data))
        {
            if (!IsInsideMdat(document.TopLevel, original.Offset, original.Length))
                throw Unsupported("原始包范围已不在 mdat 内，无法恢复");
            edits.Add(new XmpContainerEdit(original.Offset, original.Length, original.Data));
        }

        if (edits.Count > 0)
        {
            await XmpContainerIO.RewriteAsync(file, edits, receipt.Revision, cancellationToken)
                .ConfigureAwait(false);
        }

        if (recordedLength is { } expectedLength && document.FileLength > expectedLength)
        {
            await TruncateAppendedAsync(
                file,
                document,
                expectedLength,
                receipt,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        var restoredDocument = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw Unsupported("恢复后找不到 XMP mime 项");
        if (recordedLength is { } restoredLength && restoredDocument.FileLength != restoredLength)
            throw new InvalidDataException("HEIF XMP 恢复后的文件长度与原始长度不符");
        if (receipt.OriginalHadZeroLengthFinalBox &&
            (restoredDocument.TopLevel.Count == 0 || !restoredDocument.TopLevel[restoredDocument.TopLevel.Count - 1].SizeToEnd))
        {
            throw new InvalidDataException("HEIF XMP 恢复后的末尾盒未回到 size=0 状态");
        }

        var restored = restoredDocument.Packet;
        if (restored.Offset != original.Offset ||
            restored.Length != original.Length ||
            !restored.Data.AsSpan().SequenceEqual(original.Data))
        {
            throw new InvalidDataException("HEIF XMP 恢复后的包范围或内容与原始包不一致");
        }

        if (restored.Descriptor is not { } restoredIndex ||
            restoredIndex.Offset != originalIndex.Offset ||
            !restoredIndex.Data.AsSpan().SequenceEqual(originalIndex.Data))
        {
            throw new InvalidDataException("HEIF XMP 恢复后的 iloc 索引与原始索引不一致");
        }

        VerifySnapshot(restored.Data, receipt.Before, "恢复");
    }

    private static void EnsureRevisionMatches(
        StorageRevision? expected,
        StorageRevision? actual,
        string name)
    {
        if (expected is not { } expectedRevision || actual is not { } actualRevision) return;
        if (expectedRevision.Size is { } expectedSize &&
            actualRevision.Size is { } actualSize &&
            expectedSize != actualSize)
        {
            throw StorageException.ChangedExternally(name);
        }

        if (expectedRevision.LastModified is { } expectedModified &&
            actualRevision.LastModified is { } actualModified &&
            expectedModified != actualModified)
        {
            throw StorageException.ChangedExternally(name);
        }

        if (expectedRevision.Version is { } expectedVersion &&
            actualRevision.Version is { } actualVersion &&
            !string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal))
        {
            throw StorageException.ChangedExternally(name);
        }
    }

    private static void EnsureAppendedTail(
        HeifDocument document,
        long originalLength,
        XmpContainerPacket current,
        XmpRatingSnapshot expectedRating,
        string name)
    {
        if (document.TopLevel.Count == 0)
            throw Unsupported("当前文件没有顶层盒，不能截断追加区");

        var last = document.TopLevel[document.TopLevel.Count - 1];
        if (last.Type != BoxMdat ||
            last.Offset != originalLength ||
            last.End != document.FileLength ||
            last.SizeToEnd ||
            last.HeaderLength is not (8 or 16) ||
            last.PayloadOffset != current.Offset ||
            last.PayloadLength != current.Length ||
            current.Offset + current.Length != document.FileLength)
        {
            throw Unsupported("当前文件末尾不是本次写入的 mdat，不能截断追加区");
        }

        var snapshot = XmpRatingReader.ReadPacket(current.Data, XmpRatingSource.Embedded);
        if (snapshot.State != expectedRating.State || snapshot.Value != expectedRating.Value)
            throw StorageException.ChangedExternally(name);
    }

    private static async Task TruncateAppendedAsync(
        IFileRef file,
        HeifDocument document,
        long originalLength,
        XmpRatingWriteReceipt receipt,
        XmpContainerPacket appendedPacket,
        CancellationToken cancellationToken)
    {
        await using var session = await file
            .OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        if (session.Length < originalLength)
            throw Unsupported("当前文件短于回执记录的原长度，无法截断追加区");
        if (session.Length == originalLength) return;

        var tailLength = session.Length - originalLength;
        var headerLength = (int)(appendedPacket.Offset - originalLength);
        if (headerLength is not (8 or 16))
            throw Unsupported("追加区 mdat 头长度异常，不能截断");

        var header = await XmpContainerIO.ReadExactlyAsync(
            session,
            originalLength,
            headerLength,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesMdatHeader(header, tailLength))
            throw Unsupported("追加区不是完整 mdat，不能截断");

        if (appendedPacket.Offset + appendedPacket.Length != session.Length)
            throw Unsupported("追加区包范围与文件末尾不符，不能截断");

        var payload = await XmpContainerIO.ReadExactlyAsync(
            session,
            appendedPacket.Offset,
            appendedPacket.Length,
            cancellationToken).ConfigureAwait(false);
        if (!payload.AsSpan().SequenceEqual(appendedPacket.Data))
            throw StorageException.ChangedExternally(file.Name);

        if (receipt.OriginalHadZeroLengthFinalBox)
        {
            await RestoreZeroLengthFinalBoxAsync(
                session,
                document,
                originalLength,
                file.Name,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await session.SetLengthAsync(originalLength, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error == StorageError.Unsupported)
        {
            throw Unsupported("当前存储不支持截断追加数据：" + ex.Message);
        }

        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreZeroLengthFinalBoxAsync(
        IStorageRandomAccessSession session,
        HeifDocument document,
        long originalLength,
        string name,
        CancellationToken cancellationToken)
    {
        BoxInfo? finalBox = null;
        for (var i = document.TopLevel.Count - 1; i >= 0; i--)
        {
            var candidate = document.TopLevel[i];
            if (candidate.End == originalLength)
            {
                finalBox = candidate;
                break;
            }
        }

        if (finalBox is not { } box)
            throw Unsupported("找不到原始末尾盒，无法恢复 size=0 状态");
        if (box.HeaderLength != 8 || box.SizeToEnd)
            throw Unsupported("原始末尾盒不是可回写的 32 位尺寸头，无法恢复 size=0 状态");

        var expectedSize = originalLength - box.Offset;
        if (expectedSize <= 0 || (ulong)expectedSize > uint.MaxValue)
            throw Unsupported("原始末尾盒尺寸无法用 32 位表示");

        var current = await XmpContainerIO.ReadExactlyAsync(
            session,
            box.Offset,
            4,
            cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(current) != (uint)expectedSize)
            throw StorageException.ChangedExternally(name);

        await session.WriteAsync(box.Offset, new byte[4], cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesMdatHeader(ReadOnlySpan<byte> header, long tailLength)
    {
        if (header.Length == 8)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
            return type == BoxMdat &&
                tailLength <= uint.MaxValue &&
                size == (uint)tailLength;
        }

        if (header.Length == 16)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
            var largeSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            return size == 1 &&
                type == BoxMdat &&
                largeSize == (ulong)tailLength;
        }

        return false;
    }

    private static async Task<XmpRatingWriteReceipt> WriteInPlaceAsync(
        IFileRef file,
        HeifDocument document,
        XmpContainerPacket original,
        byte[] updated,
        int rating,
        XmpRatingSnapshot before,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        if (updated.Length != original.Length)
            throw Unsupported("原位写入必须保持包长度");
        if (safeMode) await EnsureUnchangedAsync(file, original, cancellationToken).ConfigureAwait(false);

        await XmpContainerIO.WritePacketAsync(
            file,
            original,
            updated,
            document.Revision,
            safeMode,
            cancellationToken).ConfigureAwait(false);

        var written = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("HEIF XMP 写入后找不到 mime 项");
        if (written.Packet.Offset != original.Offset ||
            written.Packet.Length != updated.Length ||
            !written.Packet.Data.AsSpan().SequenceEqual(updated))
        {
            throw new InvalidDataException("HEIF XMP 原位写入后的包范围或内容不符");
        }

        var after = VerifySnapshot(written.Packet.Data, null, "写入", rating);
        return new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = before,
            After = after,
            OriginalPacket = original,
            Revision = await ReadRevisionAsync(file, document.Revision, cancellationToken).ConfigureAwait(false),
            CreatedTarget = false,
        };
    }

    private static async Task<XmpRatingWriteReceipt> WriteAppendedAsync(
        IFileRef file,
        HeifDocument document,
        XmpContainerPacket original,
        byte[] updated,
        int rating,
        XmpRatingSnapshot before,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        var mdatHeader = BuildMdatHeader(updated.Length);
        var newOffset = document.FileLength + mdatHeader.Length;
        var relocatedIndex = RelocateEntry(document.Item, newOffset, updated.Length);

        var edits = new List<XmpContainerEdit>(3);
        AddEndOfFileSizePatch(document, edits);
        edits.Add(new XmpContainerEdit(
            document.Item.EntryOffset,
            document.Item.EntryBytes.Length,
            relocatedIndex));

        var mdat = new byte[mdatHeader.Length + updated.Length];
        mdatHeader.CopyTo(mdat, 0);
        updated.CopyTo(mdat, mdatHeader.Length);
        edits.Add(new XmpContainerEdit(document.FileLength, 0, mdat));

        if (safeMode) await EnsureUnchangedAsync(file, original, cancellationToken).ConfigureAwait(false);

        await XmpContainerIO.RewriteAsync(file, edits, document.Revision, cancellationToken)
            .ConfigureAwait(false);

        var written = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("HEIF XMP 扩容写入后找不到 mime 项");
        if (written.Packet.Offset != newOffset ||
            written.Packet.Length != updated.Length ||
            !written.Packet.Data.AsSpan().SequenceEqual(updated))
        {
            throw new InvalidDataException("HEIF XMP 扩容写入后的包范围或内容不符");
        }

        var after = VerifySnapshot(written.Packet.Data, null, "写入", rating);
        return new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = before,
            After = after,
            OriginalPacket = original,
            Revision = await ReadRevisionAsync(file, document.Revision, cancellationToken).ConfigureAwait(false),
            OriginalFileLength = document.FileLength,
            OriginalHadZeroLengthFinalBox =
                document.TopLevel.Count > 0 && document.TopLevel[document.TopLevel.Count - 1].SizeToEnd,
            CreatedTarget = false,
        };
    }

    private static void AddEndOfFileSizePatch(HeifDocument document, List<XmpContainerEdit> edits)
    {
        if (document.TopLevel.Count == 0) return;
        var last = document.TopLevel[document.TopLevel.Count - 1];
        if (!last.SizeToEnd) return;

        var actual = document.FileLength - last.Offset;
        if (actual <= 0 || (ulong)actual > uint.MaxValue)
            throw Unsupported("末尾盒以 0 长度声明但无法回写 32 位尺寸");

        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, (uint)actual);
        edits.Add(new XmpContainerEdit(last.Offset, 4, sizeBytes));
    }

    private static byte[] BuildMdatHeader(int payloadLength)
    {
        var size = 8L + payloadLength;
        if (size <= uint.MaxValue)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), BoxMdat);
            return header;
        }

        var large = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(large, 1);
        BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(4), BoxMdat);
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8), (ulong)size);
        return large;
    }

    private static byte[] RelocateEntry(IlocItem item, long offset, int length)
    {
        if (item.ExtentCount != 1)
            throw Unsupported("XMP 项包含多个 extent");
        if (item.ConstructionMethod != 0)
            throw Unsupported("XMP 项未使用文件偏移 construction method");
        if (!Fits(offset, item.ExtentOffsetSize))
            throw Unsupported("新 extent 偏移超出 iloc 字段宽度");
        if (!Fits(length, item.ExtentLengthSize))
            throw Unsupported("新 extent 长度超出 iloc 字段宽度");

        var raw = (byte[])item.EntryBytes.Clone();
        if (item.BaseOffsetSize > 0)
            WriteBigEndian(raw.AsSpan(item.BaseOffsetPosition, item.BaseOffsetSize), 0);
        WriteBigEndian(raw.AsSpan(item.ExtentOffsetPosition, item.ExtentOffsetSize), (ulong)offset);
        WriteBigEndian(raw.AsSpan(item.ExtentLengthPosition, item.ExtentLengthSize), (ulong)length);
        return raw;
    }

    private static bool Fits(long value, int fieldSize)
    {
        if (value < 0) return false;
        if (fieldSize == 0) return value == 0;
        if (fieldSize >= 8) return true;
        return (ulong)value < (1UL << (fieldSize * 8));
    }

    private static void WriteBigEndian(Span<byte> destination, ulong value)
    {
        for (var i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }

    private static async Task EnsureUnchangedAsync(
        IFileRef file,
        XmpContainerPacket expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.Packet.Offset != expected.Offset ||
            current.Packet.Length != expected.Length ||
            !current.Packet.Data.AsSpan().SequenceEqual(expected.Data) ||
            expected.Descriptor is not { } expectedIndex ||
            current.Packet.Descriptor is not { } currentIndex ||
            currentIndex.Offset != expectedIndex.Offset ||
            !currentIndex.Data.AsSpan().SequenceEqual(expectedIndex.Data))
        {
            throw StorageException.ChangedExternally(file.Name);
        }
    }

    private static async Task<StorageRevision?> ReadRevisionAsync(
        IFileRef file,
        StorageRevision? fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            return properties.Revision ?? fallback;
        }
        catch (StorageException)
        {
            return fallback;
        }
    }

    private static XmpRatingSnapshot VerifySnapshot(
        byte[] packet,
        XmpRatingSnapshot? expected,
        string operation,
        int? writtenRating = null)
    {
        var actual = XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);
        if (writtenRating is { } rating &&
            (actual.State != XmpRatingState.Rated || actual.Value != rating))
        {
            throw new InvalidDataException($"HEIF XMP {operation}后的 Rating 回读不符");
        }

        if (expected is { } snapshot &&
            (actual.State != snapshot.State || actual.Value != snapshot.Value))
        {
            throw new InvalidDataException($"HEIF XMP {operation}后的 Rating 与原始状态不符");
        }

        return actual;
    }

    private static bool IsInsideMdat(IReadOnlyList<BoxInfo> topLevel, long offset, int length)
    {
        if (offset < 0 || length < 0) return false;
        var end = offset + length;
        if (end < offset) return false;
        for (var i = 0; i < topLevel.Count; i++)
        {
            var box = topLevel[i];
            if (box.Type != BoxMdat) continue;
            if (offset >= box.PayloadOffset && end <= box.PayloadOffset + box.PayloadLength)
                return true;
        }
        return false;
    }

    private static async Task<HeifDocument?> ReadDocumentAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        await using var session = await file.OpenRandomAccessAsync(
            StorageAccessMode.Read,
            cancellationToken).ConfigureAwait(false);

        var fileLength = session.Length;
        if (fileLength < 16) return null;

        var topLevel = await ReadTopLevelBoxesAsync(session, fileLength, cancellationToken)
            .ConfigureAwait(false);
        BoxInfo? metaBox = null;
        for (var i = 0; i < topLevel.Count; i++)
        {
            var box = topLevel[i];
            if (box.Type == BoxMeta && metaBox is null) metaBox = box;
        }

        if (metaBox is not { } meta)
            return null;

        if (meta.PayloadLength < 4)
            throw Unsupported("meta 盒长度不足");
        if (meta.PayloadLength > MaxMetaBytes)
            throw Unsupported("meta 盒超过解析上限");
        if (meta.PayloadLength > int.MaxValue)
            throw Unsupported("meta 盒过大");

        var metaPayload = await XmpContainerIO.ReadExactlyAsync(
            session,
            meta.PayloadOffset,
            (int)meta.PayloadLength,
            cancellationToken).ConfigureAwait(false);
        if (metaPayload[0] != 0)
            throw Unsupported("meta 版本 " + metaPayload[0].ToString(System.Globalization.CultureInfo.InvariantCulture));

        var children = ReadSpanBoxes(metaPayload, 4, metaPayload.Length);
        SpanBox? pitm = null;
        SpanBox? iinf = null;
        SpanBox? iloc = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            switch (child.Type)
            {
                case BoxPitm:
                    pitm ??= child;
                    break;
                case BoxIinf:
                    iinf ??= child;
                    break;
                case BoxIloc:
                    iloc ??= child;
                    break;
            }
        }

        if (pitm is null)
            throw Unsupported("meta 缺少 pitm 主图声明");
        if (iinf is null)
            throw Unsupported("meta 缺少 iinf 项信息");
        if (iloc is null)
            throw Unsupported("meta 缺少 iloc 项位置");

        var primaryItemId = ParsePitm(metaPayload, pitm.Value);
        var items = ParseIinf(metaPayload, iinf.Value);
        var locations = ParseIloc(
            metaPayload,
            iloc.Value,
            meta.PayloadOffset,
            fileLength);

        var item = SelectXmpItem(items);
        if (item is null) return null;
        if (item.ItemId == primaryItemId)
            throw Unsupported("XMP mime 项不能是 pitm 主图项");

        if (!locations.TryGetValue(item.ItemId, out var location))
            throw Unsupported("XMP mime 项缺少 iloc 位置");
        if (location.ConstructionMethod != 0)
            throw Unsupported("XMP 项使用 idat 或项间引用 construction method");
        if (location.ExtentCount != 1)
            throw Unsupported("XMP 项包含多个 extent");
        if (location.ExtentLength <= 0)
            throw Unsupported("XMP 项 extent 长度未声明或为零");
        if (location.ExtentLength > MaxXmpPacketBytes)
            throw Unsupported("XMP 包超过解析上限");

        if (location.BaseOffset > fileLength ||
            location.ExtentOffset > fileLength - location.BaseOffset)
        {
            throw Unsupported("XMP 项数据范围超出文件");
        }

        var offset = location.BaseOffset + location.ExtentOffset;
        if (location.ExtentLength > fileLength - offset)
            throw Unsupported("XMP 项数据范围超出文件");
        if (!IsInsideMdat(topLevel, offset, (int)location.ExtentLength))
            throw Unsupported("XMP 项数据范围不在 mdat 内");

        var data = await XmpContainerIO.ReadExactlyAsync(
            session,
            offset,
            (int)location.ExtentLength,
            cancellationToken).ConfigureAwait(false);

        var packet = new XmpContainerPacket(
            offset,
            data.Length,
            data,
            new XmpContainerPacketDescriptor(location.EntryOffset, location.EntryBytes));

        return new HeifDocument
        {
            FileLength = fileLength,
            Revision = session.Revision,
            TopLevel = topLevel,
            Item = location,
            Packet = packet,
        };
    }

    private static async Task<List<BoxInfo>> ReadTopLevelBoxesAsync(
        IStorageRandomAccessSession session,
        long length,
        CancellationToken cancellationToken)
    {
        var boxes = new List<BoxInfo>(8);
        long offset = 0;
        while (offset < length)
        {
            if (length - offset < 8)
                throw Unsupported("文件末尾存在不完整的顶层盒");
            var headerLength = (int)Math.Min(16, length - offset);
            var header = await XmpContainerIO.ReadExactlyAsync(
                session,
                offset,
                headerLength,
                cancellationToken).ConfigureAwait(false);

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            var boxHeaderLength = 8L;
            var sizeToEnd = false;
            if (size == 1)
            {
                if (header.Length < 16)
                    throw Unsupported("顶层盒 largesize 被截断");
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                boxHeaderLength = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(length - offset);
                sizeToEnd = true;
            }

            if (size < (ulong)boxHeaderLength ||
                size > (ulong)(length - offset))
            {
                throw Unsupported("顶层盒尺寸越界");
            }

            boxes.Add(new BoxInfo(type, offset, (long)size, boxHeaderLength, sizeToEnd));
            if (boxes.Count > MaxTopLevelBoxes)
                throw Unsupported("顶层盒数量超过解析上限");
            offset += (long)size;
        }

        if (offset != length)
            throw Unsupported("顶层盒未覆盖文件末尾");
        return boxes;
    }

    private static List<SpanBox> ReadSpanBoxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<SpanBox>(8);
        var offset = start;
        while (offset < end)
        {
            if (end - offset < 8)
                throw Unsupported("盒末尾存在不完整子盒");

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (end - offset < 16)
                    throw Unsupported("子盒 largesize 被截断");
                size = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(end - offset);
            }

            if (size < (ulong)headerLength || size > (ulong)(end - offset))
                throw Unsupported("子盒尺寸越界");

            boxes.Add(new SpanBox(type, offset, (int)size, headerLength));
            if (boxes.Count > MaxTopLevelBoxes)
                throw Unsupported("子盒数量超过解析上限");
            offset += (int)size;
        }

        return boxes;
    }

    private static int ParsePitm(ReadOnlySpan<byte> meta, SpanBox box)
    {
        if (box.PayloadLength < 6)
            throw Unsupported("pitm 长度不足");
        var version = meta[box.PayloadOffset];
        var position = box.PayloadOffset + 4;
        if (version != 0 && box.PayloadLength < 8)
            throw Unsupported("pitm 项编号被截断");
        var value = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(position, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(meta.Slice(position, 4));
        if (value > int.MaxValue)
            throw Unsupported("pitm 项编号越界");
        return (int)value;
    }

    private static List<MimeItem> ParseIinf(ReadOnlySpan<byte> meta, SpanBox box)
    {
        if (box.PayloadLength < 6)
            throw Unsupported("iinf 长度不足");
        var version = meta[box.PayloadOffset];
        if (version > 1)
            throw Unsupported("iinf 版本 " + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var countSize = version == 0 ? 2 : 4;
        if (box.PayloadLength < 4 + countSize)
            throw Unsupported("iinf 项数被截断");
        var entryCount = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(box.PayloadOffset + 4, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(meta.Slice(box.PayloadOffset + 4, 4));

        var start = box.PayloadOffset + 4 + countSize;
        var entries = ReadSpanBoxes(meta, start, box.End);
        if (entries.Count != entryCount)
            throw Unsupported("iinf 声明项数与实际条目不符");

        var items = new List<MimeItem>(4);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Type != BoxInfe) continue;

            var position = entry.PayloadOffset;
            var end = entry.End;
            if (entry.PayloadLength < 8)
                throw Unsupported("infe 长度不足");
            var infeVersion = meta[position];
            if (infeVersion > 3)
                throw Unsupported("infe 版本 " + infeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            position += 4;

            var itemIdSize = infeVersion >= 3 ? 4 : 2;
            var itemId = ReadBigEndian(meta, ref position, itemIdSize, end);
            if (itemId > int.MaxValue)
                throw Unsupported("infe 项编号越界");
            ReadBigEndian(meta, ref position, 2, end);

            var itemType = infeVersion >= 2
                ? (uint)ReadBigEndian(meta, ref position, 4, end)
                : 0;
            ReadCString(meta, ref position, end, "infe 项名");

            string? contentType = null;
            string? contentEncoding = null;
            if (infeVersion >= 2)
            {
                if (itemType == ItemTypeMime)
                {
                    contentType = ReadCString(meta, ref position, end, "infe 内容类型");
                    contentEncoding = TryReadCString(meta, ref position, end);
                }
            }
            else if (position < end)
            {
                contentType = ReadCString(meta, ref position, end, "infe 内容类型");
                contentEncoding = TryReadCString(meta, ref position, end);
                if (!string.IsNullOrEmpty(contentType)) itemType = ItemTypeMime;
            }

            if (itemType != ItemTypeMime) continue;
            if (string.IsNullOrWhiteSpace(contentType)) continue;

            items.Add(new MimeItem
            {
                ItemId = (int)itemId,
                ContentType = contentType,
                ContentEncoding = contentEncoding,
            });
        }

        return items;
    }

    private static MimeItem? SelectXmpItem(List<MimeItem> items)
    {
        var rdf = new List<MimeItem>(1);
        var xml = new List<MimeItem>(1);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!IsUtf8Encoding(item.ContentEncoding)) continue;
            var mediaType = NormalizeMediaType(item.ContentType);
            if (mediaType == RdfXmlContentType) rdf.Add(item);
            else if (mediaType is "application/xml" or "text/xml") xml.Add(item);
        }

        var candidates = rdf.Count > 0 ? rdf : xml;
        if (candidates.Count > 1)
            throw Unsupported("存在多个 XML/RDF mime 项，无法确定 canonical XMP");
        return candidates.Count == 0 ? null : candidates[0];
    }

    private static bool IsUtf8Encoding(string? encoding) =>
        string.IsNullOrWhiteSpace(encoding) ||
        encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("utf8", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        var mediaType = separator >= 0 ? contentType[..separator] : contentType;
        return mediaType.Trim().ToLowerInvariant();
    }

    private static Dictionary<int, IlocItem> ParseIloc(
        ReadOnlySpan<byte> meta,
        SpanBox box,
        long metaAbsoluteOffset,
        long fileLength)
    {
        if (box.PayloadLength < 8)
            throw Unsupported("iloc 长度不足");
        var version = meta[box.PayloadOffset];
        if (version > 2)
            throw Unsupported("iloc 版本 " + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var sizes = BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(box.PayloadOffset + 4, 2));
        var offsetSize = (sizes >> 12) & 0xF;
        var lengthSize = (sizes >> 8) & 0xF;
        var baseOffsetSize = (sizes >> 4) & 0xF;
        var indexSize = version is 1 or 2 ? sizes & 0xF : 0;
        if (offsetSize > 8 || lengthSize > 8 || baseOffsetSize > 8 || indexSize > 8)
            throw Unsupported("iloc 字段宽度超过 8 字节");

        var position = box.PayloadOffset + 6;
        var end = box.End;
        var itemCountValue = ReadBigEndian(meta, ref position, version < 2 ? 2 : 4, end);
        if (itemCountValue > MaxIlocItems)
            throw Unsupported("iloc 项数超过解析上限");
        var itemCount = (int)itemCountValue;

        var items = new Dictionary<int, IlocItem>(itemCount);
        var itemIdSize = version < 2 ? 2 : 4;

        for (var i = 0; i < itemCount; i++)
        {
            var entryStart = position;
            var itemId = ReadBigEndian(meta, ref position, itemIdSize, end);
            if (itemId > int.MaxValue)
                throw Unsupported("iloc 项编号越界");

            var constructionMethod = 0;
            if (version is 1 or 2)
            {
                constructionMethod = (int)(ReadBigEndian(meta, ref position, 2, end) & 0xF);
            }
            var dataReferenceIndex = ReadBigEndian(meta, ref position, 2, end);
            if (dataReferenceIndex != 0)
                throw Unsupported("iloc 使用外部数据引用");

            var baseOffsetPosition = position;
            var baseOffset = ReadBigEndian(meta, ref position, baseOffsetSize, end);
            if (baseOffset > long.MaxValue)
                throw Unsupported("iloc base offset 越界");

            var extentCount = (int)ReadBigEndian(meta, ref position, 2, end);
            if (extentCount > MaxExtentCount)
                throw Unsupported("iloc extent 数量超过解析上限");

            long extentOffset = 0;
            long extentLength = 0;
            var extentOffsetPosition = position;
            var extentLengthPosition = position;
            for (var j = 0; j < extentCount; j++)
            {
                if (indexSize > 0)
                    ReadBigEndian(meta, ref position, indexSize, end);

                var currentOffsetPosition = position;
                var currentOffset = ReadBigEndian(meta, ref position, offsetSize, end);
                var currentLengthPosition = position;
                var currentLength = ReadBigEndian(meta, ref position, lengthSize, end);
                if (j == 0)
                {
                    extentOffsetPosition = currentOffsetPosition;
                    extentLengthPosition = currentLengthPosition;
                    extentOffset = currentOffset > long.MaxValue
                        ? long.MaxValue
                        : (long)currentOffset;
                    extentLength = currentLength > long.MaxValue
                        ? long.MaxValue
                        : (long)currentLength;
                }
            }

            var entryEnd = position;
            var item = new IlocItem
            {
                ItemId = (int)itemId,
                ConstructionMethod = constructionMethod,
                BaseOffset = (long)baseOffset,
                ExtentCount = extentCount,
                ExtentOffset = extentOffset,
                ExtentLength = extentLength,
                EntryOffset = metaAbsoluteOffset + entryStart,
                EntryBytes = meta.Slice(entryStart, entryEnd - entryStart).ToArray(),
                BaseOffsetPosition = baseOffsetPosition - entryStart,
                BaseOffsetSize = baseOffsetSize,
                ExtentOffsetPosition = extentOffsetPosition - entryStart,
                ExtentOffsetSize = offsetSize,
                ExtentLengthPosition = extentLengthPosition - entryStart,
                ExtentLengthSize = lengthSize,
            };

            if (!items.TryAdd(item.ItemId, item))
                throw Unsupported("iloc 项编号重复");

            if (item.BaseOffset > fileLength ||
                item.ExtentOffset > fileLength - item.BaseOffset)
            {
                throw Unsupported("iloc 项数据范围超出文件");
            }
        }

        return items;
    }

    private static ulong ReadBigEndian(
        ReadOnlySpan<byte> data,
        ref int position,
        int size,
        int end)
    {
        if (size < 0 || size > 8 || position < 0 || end - position < size)
            throw Unsupported("盒字段被截断");
        ulong value = 0;
        for (var i = 0; i < size; i++)
            value = (value << 8) | data[position + i];
        position += size;
        return value;
    }

    private static string ReadCString(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        string what)
    {
        var start = position;
        while (position < end && data[position] != 0) position++;
        if (position >= end)
            throw Unsupported(what + "缺少终止符");
        var value = Encoding.UTF8.GetString(data.Slice(start, position - start));
        position++;
        return value;
    }

    private static string? TryReadCString(ReadOnlySpan<byte> data, ref int position, int end)
    {
        if (position >= end) return null;
        var start = position;
        var limit = Math.Min(end, start + 64);
        while (position < limit && data[position] != 0)
        {
            if (data[position] is < 0x20 or > 0x7E)
            {
                position = start;
                return null;
            }
            position++;
        }

        if (position >= limit || data[position] != 0)
        {
            position = start;
            return null;
        }

        var value = Encoding.ASCII.GetString(data.Slice(start, position - start));
        position++;
        return value;
    }

    private static StorageException Unsupported(string reason) =>
        new(StorageError.Unsupported, "HEIF 容器不支持：" + reason);

    private sealed class HeifDocument
    {
        public required long FileLength { get; init; }

        public required StorageRevision? Revision { get; init; }

        public required IReadOnlyList<BoxInfo> TopLevel { get; init; }

        public required IlocItem Item { get; init; }

        public required XmpContainerPacket Packet { get; init; }
    }

    private sealed class MimeItem
    {
        public required int ItemId { get; init; }

        public required string ContentType { get; init; }

        public string? ContentEncoding { get; init; }
    }

    private sealed class IlocItem
    {
        public required int ItemId { get; init; }

        public required int ConstructionMethod { get; init; }

        public required long BaseOffset { get; init; }

        public required int ExtentCount { get; init; }

        public required long ExtentOffset { get; init; }

        public required long ExtentLength { get; init; }

        public required long EntryOffset { get; init; }

        public required byte[] EntryBytes { get; init; }

        public required int BaseOffsetPosition { get; init; }

        public required int BaseOffsetSize { get; init; }

        public required int ExtentOffsetPosition { get; init; }

        public required int ExtentOffsetSize { get; init; }

        public required int ExtentLengthPosition { get; init; }

        public required int ExtentLengthSize { get; init; }
    }

    private readonly record struct BoxInfo(
        uint Type,
        long Offset,
        long Size,
        long HeaderLength,
        bool SizeToEnd)
    {
        public long End => Offset + Size;

        public long PayloadOffset => Offset + HeaderLength;

        public long PayloadLength => Size - HeaderLength;
    }

    private readonly record struct SpanBox(
        uint Type,
        int Offset,
        int Size,
        int HeaderLength)
    {
        public int PayloadOffset => Offset + HeaderLength;

        public int PayloadLength => Size - HeaderLength;

        public int End => Offset + Size;
    }
}
