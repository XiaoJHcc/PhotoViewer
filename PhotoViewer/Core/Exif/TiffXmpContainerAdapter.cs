using System.Buffers.Binary;
using System.Xml;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// TIFF 家族容器（TIFF / ARW / DNG 等经典 TIFF 结构）的 XMP 容器适配器。
/// 只按 TIFF 结构定位 IFD0 的 tag 700（XMP），不做任何文本扫描。
/// </summary>
/// <remarks>
/// <para>写入策略：</para>
/// <list type="bullet">
/// <item>新包能放进原长度：原位改写包字节，IFD entry 不动。</item>
/// <item>新包更长：把新包追加到文件末尾，再把 tag 700 的 count/offset 原位改指向新包。
/// 旧包字节保留但不再被引用，因此不搬动任何既有偏移（像素、缩略图、MakerNote 都保持原位）。
/// 回执记录追加前的文件长度，恢复时把长度回退到该值，去掉本次追加的包。</item>
/// <item>没有 tag 700：明确以 <see cref="StorageError.Unsupported"/> 拒绝。给 IFD0 增加条目只能在
/// 文件末尾重建 IFD0 并改头部指针，而实测这会让要求 IFD 链偏移单调递增的读取器丢掉缩略图等目录；
/// 补齐要连整条 IFD 链一起搬迁，风险高于收益。</item>
/// </list>
/// <para><see cref="XmpContainerPacketDescriptor"/> 的约定：<c>Offset</c> 是 tag 700 这条 IFD entry
/// 的文件偏移，<c>Data</c> 是该 entry 的原始 12 字节。两者合起来足以还原 inline（count ≤ 4）
/// 与 offset 两种原始形态，恢复时不需要重新猜测原布局。</para>
/// <para>恢复只回退“本次追加且在文件尾部、且能完整证明属于本适配器”的字节：文件尾部
/// 不是本次追加的包（例如被外部追加了内容）或存储不支持截断时，恢复明确失败，
/// 不会截掉外部数据，也不会把残留的孤儿字节当成成功。原位写入不截断。</para>
/// <para>结构性异常（字节序标记、magic、IFD0 边界、重复 tag、非法类型/偏移）一律以
/// <see cref="StorageError.Unsupported"/> 拒绝，绝不在不确定的布局上写入。</para>
/// </remarks>
public sealed class TiffXmpContainerAdapter : IXmpContainerAdapter
{
    private const ushort XmpTag = 0x02BC;
    private const ushort TiffMagic = 42;
    private const ushort BigTiffMagic = 43;
    private const ushort TypeByte = 1;
    private const ushort TypeUndefined = 7;
    private const int HeaderSize = 8;
    private const int EntrySize = 12;
    private const int InlineValueCapacity = 4;
    private const int MaxIfd0EntryCount = 4096;
    private const int MaxPacketLength = 16 * 1024 * 1024;

    /// <inheritdoc />
    public Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return GuardAsync(
            $"读取 {file.Name} 的 TIFF XMP",
            () => ReadCanonicalPacketCoreAsync(file, cancellationToken));
    }

    private static async Task<XmpContainerPacket?> ReadCanonicalPacketCoreAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
            .ConfigureAwait(false);
        var reader = ForSession(session);
        var layout = await ReadLayoutAsync(reader, session.Length, file.Name, cancellationToken)
            .ConfigureAwait(false);
        return layout.XmpEntry is null
            ? null
            : await ReadPacketAsync(layout, reader, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        if (file.Capabilities.RandomAccessWrite == StorageCapability.Unsupported)
            throw StorageException.NotSeekable();
        return GuardAsync(
            $"写入 {file.Name} 的 TIFF XMP",
            () => WriteRatingCoreAsync(file, rating, safeMode, cancellationToken));
    }

    private static async Task<XmpRatingWriteReceipt> WriteRatingCoreAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        var beforeProperties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!beforeProperties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{file.Name} 已不存在");
        var expectedRevision = beforeProperties.Revision;

        XmpContainerPacket? original;
        await using (var session = await file.OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                         .ConfigureAwait(false))
        {
            var reader = ForSession(session);
            var layout = await ReadLayoutAsync(reader, session.Length, file.Name, cancellationToken)
                .ConfigureAwait(false);
            original = layout.XmpEntry is null
                ? null
                : await ReadPacketAsync(layout, reader, cancellationToken).ConfigureAwait(false);
        }

        var before = original is null
            ? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)
            : TryReadSnapshot(original.Data)
              ?? new XmpRatingSnapshot(
                  XmpRatingState.Invalid, null, XmpRatingSource.Embedded, "内嵌 XMP 无法解析");

        // 无 tag 700 时必须给 IFD0 增加条目，而 IFD0 条目按 tag 升序且紧跟条目表之后就是元数据，
        // 只能在文件末尾重建 IFD0 并改头部指针。实测这会让要求 IFD 链偏移单调递增的读取器
        // （MetadataExtractor，即本工程的 EXIF 读取路径）丢掉 IFD1 缩略图等目录，
        // 补齐需要连整条 IFD 链一起搬迁，风险高于收益，因此明确拒绝而不是写坏文件。
        if (original is null)
            throw Unsupported(
                file.Name,
                "IFD0 没有 XMP tag 700；新增 tag 700 需要重建 IFD0（会破坏部分读取器的 IFD 链顺序），本适配器不支持");

        if (before.State == XmpRatingState.Rated && before.Value == rating)
            return new XmpRatingWriteReceipt
            {
                TargetFile = file,
                Before = before,
                After = before,
                OriginalPacket = original,
                Revision = beforeProperties.Revision,
                CreatedTarget = false,
            };

        var outcome = await ReplacePacketAsync(file, original, rating, safeMode, expectedRevision, cancellationToken)
            .ConfigureAwait(false);

        var after = await ReadSnapshotAsync(file, cancellationToken).ConfigureAwait(false);
        if (after.State != XmpRatingState.Rated || after.Value != rating)
        {
            var rolledBack = outcome.Undo is not null &&
                             await TryRunAsync(outcome.Undo, CancellationToken.None).ConfigureAwait(false);
            throw new StorageException(
                StorageError.IoError,
                rolledBack
                    ? $"{file.Name}：写入后校验失败，已回滚原包"
                    : $"{file.Name}：写入后校验失败");
        }

        var afterProperties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        return new XmpRatingWriteReceipt
        {
            TargetFile = file,
            Before = before,
            After = after,
            OriginalPacket = original,
            Revision = afterProperties.Revision,
            OriginalFileLength = outcome.AppendedFrom,
            CreatedTarget = false,
        };
    }

    /// <inheritdoc />
    public Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(receipt);
        return GuardAsync(
            $"恢复 {file.Name} 的 TIFF XMP",
            async () =>
            {
                if (receipt.OriginalPacket is null)
                    await RemoveAddedTagAsync(file, receipt, cancellationToken).ConfigureAwait(false);
                else
                    await RestoreOriginalPacketAsync(file, receipt, cancellationToken).ConfigureAwait(false);
                return true;
            });
    }

    private static async Task<T> GuardAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
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
            throw StorageException.From(exception, operation);
        }
    }

    private static async Task<PacketWriteOutcome> ReplacePacketAsync(
        IFileRef file,
        XmpContainerPacket original,
        int rating,
        bool safeMode,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        var inPlace = TryEditPreservingLength(original.Data, rating);
        if (inPlace is not null)
        {
            await EnsureEntryUnchangedAsync(file, original, cancellationToken).ConfigureAwait(false);
            await XmpContainerIO.WritePacketAsync(
                    file, original, inPlace, expectedRevision, safeMode, cancellationToken)
                .ConfigureAwait(false);
            return new PacketWriteOutcome(
                async token =>
                {
                    var written = new XmpContainerPacket(original.Offset, inPlace.Length, inPlace);
                    await XmpContainerIO.WritePacketAsync(file, written, original.Data, null, false, token)
                        .ConfigureAwait(false);
                },
                AppendedFrom: null);
        }

        var grown = TryEditWithTemplate(original.Data, rating)
                    ?? XmpPacketEditor.SetRating(null, rating, preserveLength: false);
        var appendedFrom = await AppendPacketAsync(
                file, original, grown, safeMode, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        var descriptor = original.Descriptor!;
        return new PacketWriteOutcome(
            token => UndoAppendAsync(
                file, descriptor.Offset, descriptor.Data, appendedFrom, grown.Length, token),
            appendedFrom);
    }

    /// <summary>
    /// 包变长时只追加新包并改 tag 700 的 count/offset：先落新包再改 entry，
    /// 中途失败时 entry 仍指向旧包，文件保持有效（尾部多出的字节不被引用）。
    /// </summary>
    private static async Task<long> AppendPacketAsync(
        IFileRef file,
        XmpContainerPacket original,
        byte[] packet,
        bool safeMode,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        var descriptor = original.Descriptor
            ?? throw Unsupported(file.Name, "缺少 tag 700 的 IFD entry 记录，拒绝追加写入");

        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        EnsureRevision(session.Revision, expectedRevision, file.Name);

        var reader = ForSession(session);
        var layout = await ReadLayoutAsync(reader, session.Length, file.Name, cancellationToken)
            .ConfigureAwait(false);
        var entry = layout.XmpEntry
            ?? throw StorageException.ChangedExternally(file.Name);
        if (entry.Offset != descriptor.Offset ||
            !entry.Raw.AsSpan().SequenceEqual(descriptor.Data) ||
            ValueOffset(layout, entry) != original.Offset ||
            entry.Count != original.Length)
            throw StorageException.ChangedExternally(file.Name);

        if (safeMode)
        {
            var current = await XmpContainerIO
                .ReadExactlyAsync(session, original.Offset, original.Length, cancellationToken)
                .ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original.Data))
                throw StorageException.ChangedExternally(file.Name);
        }

        var appendStart = session.Length;
        var packetOffset = AlignEven(appendStart);
        if (packetOffset + packet.Length > uint.MaxValue)
            throw Unsupported(file.Name, "XMP 包追加位置超出经典 TIFF 的 32 位偏移上限");

        var payload = new byte[(int)(packetOffset - appendStart) + packet.Length];
        packet.CopyTo(payload, (int)(packetOffset - appendStart));
        await session.WriteAsync(appendStart, payload, cancellationToken).ConfigureAwait(false);
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);

        var entryBytes = BuildEntry(entry.Type, (uint)packet.Length, (uint)packetOffset, layout.BigEndian);
        await session.WriteAsync(entry.Offset, entryBytes, cancellationToken).ConfigureAwait(false);
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
        return appendStart;
    }

    private static async Task RestoreOriginalPacketAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken)
    {
        var original = receipt.OriginalPacket!;
        var descriptor = original.Descriptor
            ?? throw Unsupported(file.Name, "缺少原始 IFD entry 记录，无法恢复");
        if (descriptor.Data.Length != EntrySize)
            throw Unsupported(file.Name, "原始 IFD entry 记录长度无效");

        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        var reader = ForSession(session);
        var layout = await ReadLayoutAsync(reader, session.Length, file.Name, cancellationToken)
            .ConfigureAwait(false);
        var entry = layout.XmpEntry
            ?? throw StorageException.ChangedExternally(file.Name);
        if (entry.Offset != descriptor.Offset)
            throw StorageException.ChangedExternally(file.Name);

        long? truncateTo = null;
        if (entry.Raw.AsSpan().SequenceEqual(descriptor.Data))
        {
            // entry 未被改写：只可能做过原位包改写，把原包字节写回原位置；原位写不截断。
            if (ValueOffset(layout, entry) != original.Offset || entry.Count != original.Length)
                throw StorageException.ChangedExternally(file.Name);
            var current = await XmpContainerIO
                .ReadExactlyAsync(session, original.Offset, original.Length, cancellationToken)
                .ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original.Data))
            {
                if (receipt.After.State == XmpRatingState.Rated &&
                    TryReadSnapshot(current) is { } snapshot &&
                    snapshot.Value != receipt.After.Value)
                    throw StorageException.ChangedExternally(file.Name);
                await session.WriteAsync(original.Offset, original.Data, cancellationToken).ConfigureAwait(false);
                await session.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // entry 被改指新包：旧包字节必须原样还在，否则无法还原。
            // inline 原包（count ≤ 4）没有独立的包区，其字节就在 entry 内，由下面的 entry 回写还原。
            var inlineOriginal = original.Length <= InlineValueCapacity;
            if (!inlineOriginal)
            {
                if (original.Offset + original.Length > session.Length)
                    throw StorageException.ChangedExternally(file.Name);
                var stale = await XmpContainerIO
                    .ReadExactlyAsync(session, original.Offset, original.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (!stale.AsSpan().SequenceEqual(original.Data))
                    throw StorageException.ChangedExternally(file.Name);
            }
            var current = await ReadPacketAsync(layout, reader, cancellationToken).ConfigureAwait(false);
            if (receipt.After.State == XmpRatingState.Rated &&
                TryReadSnapshot(current.Data) is { } snapshot &&
                snapshot.Value != receipt.After.Value)
                throw StorageException.ChangedExternally(file.Name);

            // 追加写入的包整段压在文件尾部：只有能证明尾部就是本次追加的内容时才回退长度，
            // 绝不截掉外部新增的字节。
            truncateTo = ResolveTruncateTarget(
                file.Name, receipt, current.Offset, current.Length, session.Length);

            await session.WriteAsync(descriptor.Offset, descriptor.Data, cancellationToken).ConfigureAwait(false);
            await session.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (truncateTo is { } target)
            {
                try
                {
                    await session.SetLengthAsync(target, cancellationToken).ConfigureAwait(false);
                    await session.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is StorageException or NotSupportedException)
                {
                    throw new StorageException(
                        StorageError.Unsupported,
                        $"{file.Name}：原包与 tag 700 已恢复，但当前存储不支持截断，" +
                        $"无法移除本次追加的 {session.Length - target} 字节 XMP 包，恢复未完全成功",
                        exception);
                }
            }
        }

        await VerifyRestoredAsync(file, original, receipt, truncateTo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 计算恢复时要回退到的文件长度；追加区的形状对不上（被外部追加/移动过）就明确拒绝，
    /// 宁可让恢复失败也不截掉不属于本次写入的数据。
    /// </summary>
    private static long? ResolveTruncateTarget(
        string name,
        XmpRatingWriteReceipt receipt,
        long packetOffset,
        long packetLength,
        long currentLength)
    {
        if (receipt.OriginalFileLength is not { } originalLength)
            throw new StorageException(
                StorageError.Unsupported,
                $"{name}：回执缺少原文件长度，无法移除本次追加的 XMP 包，已放弃恢复以免留下孤儿数据");
        // 扩容写入必然让文件变长：entry 已改指新包却看不到追加区，说明文件已被外部重写/截断。
        if (originalLength >= currentLength)
            throw StorageException.ChangedExternally(name);
        if (packetOffset != AlignEven(originalLength) || packetOffset + packetLength != currentLength)
            throw new StorageException(
                StorageError.ChangedExternally,
                $"{name}：文件尾部不是本次追加的 XMP 包（可能被外部追加了内容），已放弃恢复以免截断外部数据");
        return originalLength;
    }

    /// <summary>
    /// 撤销新增的 tag 700：把去掉该条目的 IFD0 原位覆盖回同一个 IFD0 块。
    /// 块尾多出的 12 字节成为不再引用的空洞，文件长度与其余偏移不变。
    /// </summary>
    /// <remarks>
    /// 这里不回退文件长度：新增路径会把新的 IFD0 放到文件尾部，当前 IFD0 就落在追加区里，
    /// 截断会直接毁掉目录。本适配器不提供新增路径（会明确 Unsupported），此分支只可能由
    /// 外部构造的回执进入，因此保持“原位去掉条目、不动长度”的最小改动。
    /// </remarks>
    private async Task RemoveAddedTagAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (file.Capabilities.AtomicReplace == StorageCapability.Unsupported)
            throw StorageException.Unsupported("原子替换");

        TiffLayout layout;
        StorageRevision? expectedRevision;
        await using (var session = await file.OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
                         .ConfigureAwait(false))
        {
            expectedRevision = session.Revision;
            var reader = ForSession(session);
            layout = await ReadLayoutAsync(reader, session.Length, file.Name, cancellationToken)
                .ConfigureAwait(false);
            var entry = layout.XmpEntry
                ?? throw StorageException.ChangedExternally(file.Name);
            var current = await ReadPacketAsync(layout, reader, cancellationToken).ConfigureAwait(false);
            if (receipt.After.State == XmpRatingState.Rated &&
                TryReadSnapshot(current.Data) is { } snapshot &&
                snapshot.Value != receipt.After.Value)
                throw StorageException.ChangedExternally(file.Name);
            if (entry.Count == 0)
                throw StorageException.ChangedExternally(file.Name);
        }

        var remaining = layout.Entries.Where(entry => entry.Tag != XmpTag).ToArray();
        if (remaining.Length != layout.EntryCount - 1)
            throw Unsupported(file.Name, "IFD0 中的 XMP 条目无法唯一确定");
        var block = BuildIfdBlock(remaining, layout.NextIfdOffset, layout.BigEndian);

        await RewriteAsync(
                file,
                [(layout.Ifd0Offset, block)],
                [],
                expectedRevision,
                async (temp, length, token) =>
                {
                    var rebuilt = await ReadLayoutAsync(ForStream(temp), length, file.Name, token)
                        .ConfigureAwait(false);
                    if (rebuilt.XmpEntry is not null ||
                        rebuilt.Ifd0Offset != layout.Ifd0Offset ||
                        rebuilt.EntryCount != remaining.Length ||
                        rebuilt.NextIfdOffset != layout.NextIfdOffset)
                        throw new StorageException(StorageError.IoError, $"{file.Name}：撤销 tag 700 后校验失败");
                },
                file.Name,
                cancellationToken)
            .ConfigureAwait(false);

        var packet = await ReadCanonicalPacketAsync(file, cancellationToken).ConfigureAwait(false);
        if (packet is not null)
            throw new StorageException(StorageError.IoError, $"{file.Name}：撤销后仍存在 tag 700");
    }

    private static async Task VerifyRestoredAsync(
        IFileRef file,
        XmpContainerPacket original,
        XmpRatingWriteReceipt receipt,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        var restored = await ReadCanonicalPacketCoreAsync(file, cancellationToken).ConfigureAwait(false);
        if (restored is null || !restored.Data.AsSpan().SequenceEqual(original.Data))
            throw new StorageException(StorageError.IoError, $"{file.Name}：恢复后校验失败");
        if (receipt.Before.State == XmpRatingState.Rated &&
            TryReadSnapshot(restored.Data) is { } snapshot &&
            snapshot.Value != receipt.Before.Value)
            throw new StorageException(StorageError.IoError, $"{file.Name}：恢复后的 Rating 与写入前不一致");
        if (expectedLength is not { } length) return;
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (properties.Size is { } size && size != (ulong)length)
            throw new StorageException(
                StorageError.IoError,
                $"{file.Name}：恢复后文件长度未回到原长度（{size} ≠ {length}）");
    }

    private static async Task EnsureEntryUnchangedAsync(
        IFileRef file,
        XmpContainerPacket original,
        CancellationToken cancellationToken)
    {
        var descriptor = original.Descriptor
            ?? throw Unsupported(file.Name, "缺少 tag 700 的 IFD entry 记录，拒绝写入");
        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
            .ConfigureAwait(false);
        var layout = await ReadLayoutAsync(ForSession(session), session.Length, file.Name, cancellationToken)
            .ConfigureAwait(false);
        var entry = layout.XmpEntry;
        if (entry is null ||
            entry.Offset != descriptor.Offset ||
            !entry.Raw.AsSpan().SequenceEqual(descriptor.Data) ||
            ValueOffset(layout, entry) != original.Offset ||
            entry.Count != original.Length)
            throw StorageException.ChangedExternally(file.Name);
    }

    private static async Task<XmpRatingSnapshot> ReadSnapshotAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        var packet = await ReadCanonicalPacketCoreAsync(file, cancellationToken).ConfigureAwait(false);
        if (packet is null) return XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);
        return TryReadSnapshot(packet.Data)
               ?? new XmpRatingSnapshot(
                   XmpRatingState.Invalid, null, XmpRatingSource.Embedded, "内嵌 XMP 无法解析");
    }

    private static XmpRatingSnapshot? TryReadSnapshot(byte[] packet)
    {
        try
        {
            return XmpRatingReader.ReadPacket(packet, XmpRatingSource.Embedded);
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            return null;
        }
    }

    /// <summary>原长度能容纳新属性时返回与原包等长的新包；否则返回 null 走追加路径。</summary>
    private static byte[]? TryEditPreservingLength(byte[] packet, int rating)
    {
        try
        {
            var updated = XmpPacketEditor.SetRating(packet, rating, preserveLength: true);
            return updated.Length == packet.Length ? updated : null;
        }
        catch (Exception exception) when (exception is XmpException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>原包无法解析（例如 inline 数据）时返回 null，由调用方新建整包。</summary>
    private static byte[]? TryEditWithTemplate(byte[] packet, int rating)
    {
        try
        {
            return XmpPacketEditor.SetRating(packet, rating, preserveLength: false);
        }
        catch (Exception exception) when (exception is XmpException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<TiffLayout> ReadLayoutAsync(
        RangeReader read,
        long fileLength,
        string name,
        CancellationToken cancellationToken)
    {
        if (fileLength < HeaderSize) throw Unsupported(name, "文件短于 TIFF 头部");

        var header = await read(0, HeaderSize, cancellationToken).ConfigureAwait(false);
        bool bigEndian;
        if (header[0] == (byte)'I' && header[1] == (byte)'I') bigEndian = false;
        else if (header[0] == (byte)'M' && header[1] == (byte)'M') bigEndian = true;
        else throw Unsupported(name, "不是 TIFF/ARW/DNG 容器（缺少 II/MM 字节序标记）");

        var magic = ReadUInt16(header.AsSpan(2, 2), bigEndian);
        if (magic == BigTiffMagic) throw Unsupported(name, "BigTIFF（magic 43）不受支持");
        if (magic != TiffMagic) throw Unsupported(name, $"TIFF magic 不是 42（实际 {magic}）");

        var firstIfd = ReadUInt32(header.AsSpan(4, 4), bigEndian);
        if (firstIfd < HeaderSize || (firstIfd & 1) != 0 || firstIfd + 2L > fileLength)
            throw Unsupported(name, $"IFD0 偏移无效：{firstIfd}");

        var countBytes = await read(firstIfd, 2, cancellationToken).ConfigureAwait(false);
        var entryCount = ReadUInt16(countBytes, bigEndian);
        if (entryCount > MaxIfd0EntryCount)
            throw Unsupported(name, $"IFD0 条目数异常：{entryCount}");

        var tableOffset = firstIfd + 2L;
        var tableLength = entryCount * (long)EntrySize + 4;
        if (tableOffset + tableLength > fileLength)
            throw Unsupported(name, "IFD0 条目表越过文件末尾");
        var table = await read(tableOffset, (int)tableLength, cancellationToken).ConfigureAwait(false);

        var entries = new List<TiffEntry>(entryCount);
        TiffEntry? xmp = null;
        for (var index = 0; index < entryCount; index++)
        {
            var raw = table.AsSpan(index * EntrySize, EntrySize).ToArray();
            var entry = new TiffEntry(
                ReadUInt16(raw, bigEndian),
                ReadUInt16(raw.AsSpan(2), bigEndian),
                ReadUInt32(raw.AsSpan(4), bigEndian),
                tableOffset + index * (long)EntrySize,
                raw);
            if (entry.Tag == XmpTag)
            {
                if (xmp is not null) throw Unsupported(name, "IFD0 含重复的 XMP tag 700");
                xmp = entry;
            }
            entries.Add(entry);
        }

        var nextIfd = ReadUInt32(table.AsSpan(entryCount * EntrySize), bigEndian);
        if (nextIfd != 0 && (nextIfd < HeaderSize || (nextIfd & 1) != 0 || nextIfd + 2L > fileLength))
            throw Unsupported(name, $"IFD0 的 next IFD 偏移无效：{nextIfd}");

        if (xmp is { } entry700)
        {
            if (entry700.Type is not (TypeByte or TypeUndefined))
                throw Unsupported(name, $"tag 700 的类型 {entry700.Type} 不受支持（只接受 BYTE/UNDEFINED）");
            if (entry700.Count == 0) throw Unsupported(name, "tag 700 的 count 为 0");
            if (entry700.Count > MaxPacketLength)
                throw Unsupported(name, $"tag 700 的 count 异常：{entry700.Count}");
            if (entry700.Count > InlineValueCapacity)
            {
                var offset = ReadUInt32(entry700.Raw.AsSpan(8, 4), bigEndian);
                var tableEnd = tableOffset + tableLength;
                var overlapsIfd0 = offset < tableEnd && offset + (long)entry700.Count > firstIfd;
                if (offset < HeaderSize || offset + (long)entry700.Count > fileLength || overlapsIfd0)
                    throw Unsupported(name, $"tag 700 的数据偏移越界或压住 IFD0：{offset}+{entry700.Count}");
            }
        }

        return new TiffLayout
        {
            BigEndian = bigEndian,
            Ifd0Offset = firstIfd,
            EntryCount = entryCount,
            NextIfdOffset = nextIfd,
            Entries = entries,
            XmpEntry = xmp,
        };
    }

    private static async Task<XmpContainerPacket> ReadPacketAsync(
        TiffLayout layout,
        RangeReader read,
        CancellationToken cancellationToken)
    {
        var entry = layout.XmpEntry
            ?? throw new InvalidOperationException("当前布局不含 XMP tag");
        var offset = ValueOffset(layout, entry);
        var data = await read(offset, (int)entry.Count, cancellationToken).ConfigureAwait(false);
        return new XmpContainerPacket(
            offset,
            (int)entry.Count,
            data,
            new XmpContainerPacketDescriptor(entry.Offset, entry.Raw));
    }

    private static long ValueOffset(TiffLayout layout, TiffEntry entry) =>
        entry.Count <= InlineValueCapacity
            ? entry.Offset + 8
            : ReadUInt32(entry.Raw.AsSpan(8, 4), layout.BigEndian);

    private static byte[] BuildEntry(ushort type, uint count, uint value, bool bigEndian)
    {
        var entry = new byte[EntrySize];
        WriteUInt16(entry, 0, XmpTag, bigEndian);
        WriteUInt16(entry, 2, type, bigEndian);
        WriteUInt32(entry, 4, count, bigEndian);
        WriteUInt32(entry, 8, value, bigEndian);
        return entry;
    }

    private static byte[] BuildIfdBlock(
        IReadOnlyList<TiffEntry> entries,
        uint nextIfd,
        bool bigEndian)
    {
        var block = new byte[2 + entries.Count * EntrySize + 4];
        WriteUInt16(block, 0, (ushort)entries.Count, bigEndian);
        for (var index = 0; index < entries.Count; index++)
            entries[index].Raw.CopyTo(block, 2 + index * EntrySize);
        WriteUInt32(block, 2 + entries.Count * EntrySize, nextIfd, bigEndian);
        return block;
    }

    /// <summary>
    /// 原子重写：按偏移覆盖若干段并可在末尾追加。可读的临时流会在替换前先按 TIFF 结构校验，
    /// 校验失败时 <see cref="IFileRef.AtomicWriteAsync"/> 不会替换原文件。
    /// </summary>
    private static Task RewriteAsync(
        IFileRef file,
        IReadOnlyList<(long Offset, byte[] Data)> patches,
        byte[] append,
        StorageRevision? expectedRevision,
        VerifyRewritten? verify,
        string name,
        CancellationToken cancellationToken)
    {
        var ordered = patches.OrderBy(patch => patch.Offset).ToArray();
        long previousEnd = 0;
        foreach (var patch in ordered)
        {
            if (patch.Offset < previousEnd)
                throw new ArgumentException("重写区段重叠", nameof(patches));
            previousEnd = patch.Offset + patch.Data.Length;
        }

        return file.AtomicWriteAsync(
            async (output, token) =>
            {
                await using var input = await file.OpenReadAsync(token).ConfigureAwait(false);
                if (previousEnd > input.Length)
                    throw new EndOfStreamException("重写区段超出文件末尾");
                long position = 0;
                foreach (var patch in ordered)
                {
                    await CopyRangeAsync(input, output, patch.Offset - position, token).ConfigureAwait(false);
                    await output.WriteAsync(patch.Data, token).ConfigureAwait(false);
                    await SkipRangeAsync(input, patch.Data.Length, token).ConfigureAwait(false);
                    position = patch.Offset + patch.Data.Length;
                }
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                if (append.Length > 0)
                    await output.WriteAsync(append, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                if (verify is null || !output.CanRead || !output.CanSeek) return;
                await verify(output, output.Length, token).ConfigureAwait(false);
            },
            expectedRevision,
            cancellationToken);
    }

    /// <summary>
    /// 写入后校验失败时的回滚：先写回原 entry，再在“尾部确实是本次追加的包”时退回长度，
    /// 避免把孤儿包留在文件里；尾部形状不符就不截断（可能混入了外部内容）。
    /// </summary>
    private static async Task UndoAppendAsync(
        IFileRef file,
        long entryOffset,
        byte[] entryBytes,
        long appendedFrom,
        int packetLength,
        CancellationToken cancellationToken)
    {
        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        await session.WriteAsync(entryOffset, entryBytes, cancellationToken).ConfigureAwait(false);
        if (session.Length == AlignEven(appendedFrom) + packetLength)
            await session.SetLengthAsync(appendedFrom, cancellationToken).ConfigureAwait(false);
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryRunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void EnsureRevision(StorageRevision? current, StorageRevision? expected, string name)
    {
        if (current is not { } actual || expected is not { } wanted) return;
        if (wanted.Size is { } size && actual.Size is { } actualSize && size != actualSize)
            throw StorageException.ChangedExternally(name);
        if (wanted.LastModified is { } modified &&
            actual.LastModified is { } actualModified &&
            modified != actualModified)
            throw StorageException.ChangedExternally(name);
    }

    private static RangeReader ForSession(IStorageRandomAccessSession session) =>
        (offset, length, cancellationToken) =>
            XmpContainerIO.ReadExactlyAsync(session, offset, length, cancellationToken);

    private static RangeReader ForStream(Stream stream) =>
        (offset, length, cancellationToken) =>
            ReadFromStreamAsync(stream, offset, length, cancellationToken);

    private static async Task<byte[]> ReadFromStreamAsync(
        Stream stream,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || offset + length > stream.Length)
            throw new EndOfStreamException("XMP 包范围超出文件");
        stream.Position = offset;
        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(data.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("XMP 包提前结束");
            read += count;
        }
        return data;
    }

    private static async Task CopyRangeAsync(
        Stream input,
        Stream output,
        long count,
        CancellationToken cancellationToken)
    {
        if (count < 0) throw new EndOfStreamException("重写范围无效");
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await input
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("文件提前结束");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task SkipRangeAsync(
        Stream input,
        long count,
        CancellationToken cancellationToken)
    {
        if (count < 0) throw new EndOfStreamException("重写范围无效");
        if (input.CanSeek)
        {
            input.Seek(count, SeekOrigin.Current);
            return;
        }
        await CopyRangeAsync(input, Stream.Null, count, cancellationToken).ConfigureAwait(false);
    }

    private static long AlignEven(long value) => (value & 1) == 0 ? value : value + 1;

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(source)
            : BinaryPrimitives.ReadUInt16LittleEndian(source);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(source)
            : BinaryPrimitives.ReadUInt32LittleEndian(source);

    private static void WriteUInt16(Span<byte> target, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(target[offset..], value);
        else BinaryPrimitives.WriteUInt16LittleEndian(target[offset..], value);
    }

    private static void WriteUInt32(Span<byte> target, int offset, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);
        else BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);
    }

    private static StorageException Unsupported(string name, string reason) =>
        new(StorageError.Unsupported, $"{name}：{reason}");

    private delegate Task<byte[]> RangeReader(long offset, int length, CancellationToken cancellationToken);

    private delegate Task VerifyRewritten(Stream output, long length, CancellationToken cancellationToken);

    private readonly record struct PacketWriteOutcome(
        Func<CancellationToken, Task>? Undo,
        long? AppendedFrom);

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, long Offset, byte[] Raw);

    private sealed class TiffLayout
    {
        public required bool BigEndian { get; init; }
        public required long Ifd0Offset { get; init; }
        public required ushort EntryCount { get; init; }
        public required uint NextIfdOffset { get; init; }
        public required IReadOnlyList<TiffEntry> Entries { get; init; }
        public required TiffEntry? XmpEntry { get; init; }
    }
}
