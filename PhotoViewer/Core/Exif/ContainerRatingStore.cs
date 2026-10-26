using System.Xml;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// 单个 XMP 评分来源（文件内嵌容器，或一个已存在的 <c>.xmp</c>）的评分存储。
/// </summary>
/// <remarks>
/// <para>读取只通过适配器定位的 canonical 包，不猜测容器布局，也不回看别的来源。</para>
/// <para>写入只有一条正常路径：用 <see cref="XmpRatingSpanLocator"/> 在包内生成等长的最小改写区间
/// （值替换，或在包内没有 Rating 时按结构插入一个），再只把该区间原位覆盖回文件。
/// 包内没有可安全改写/插入的 Rating 区间（无 canonical XMP、多个 Rating、无法解析、
/// 空白不足）时预检直接报 <see cref="StorageError.Unsupported"/>，不新建 XMP、不扩容容器、
/// 不用整包重序列化兜底。</para>
/// <para>提交前始终比较预检记录的文件 revision（size/mtime/version）与当前值，外部修改直接
/// <c>ChangedExternally</c>；安全模式再额外比较整个原始 XMP 包。预检本身以 ReadWrite 打开现有内容
/// （不截断、不新建）并在包偏移做定位读取，证明写权限与随机寻址可用，全程零写入；同值
/// <c>NoChange</c> 也必须通过这一步，并在提交时复核 revision 与目标值，不能凭预检结果直接宣称成功。
/// 写后回读语义并核对区间外字节未变；失败时把原区间写回自愈。补偿同样只恢复“磁盘仍是本次写入内容”的来源。</para>
/// </remarks>
public sealed class ContainerRatingStore : IXmpRatingStore
{
    public ContainerRatingStore(IFileRef file, IXmpContainerAdapter adapter, XmpRatingSource source = XmpRatingSource.Embedded)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(adapter);
        File = file;
        Adapter = adapter;
        Source = source;
    }

    public IFileRef File { get; }

    public IXmpContainerAdapter Adapter { get; }

    /// <summary>该存储代表的来源类型（内嵌或已存在的 sidecar）。</summary>
    public XmpRatingSource Source { get; }

    public async Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
        return packet is null ? XmpRatingSnapshot.Missing(Source) : ParseOrInvalid(packet.Data);
    }

    public async Task<XmpRatingPreparation> PrepareAsync(
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        if (rating is < -1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 -1 到 5 之间");

        if (File.Capabilities.RandomAccessWrite == StorageCapability.Unsupported)
            throw StorageException.NotSeekable();

        var properties = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{File.Name} 已不存在");

        var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false)
            ?? throw Unsupported(
                $"{File.Name} 没有 canonical XMP。评分写入不新建 XMP，也不退化为 sidecar");

        // 只有严格语义可解析为 Rated/Rejected 且等于目标值才允许 NoChange。
        // 损坏、裸实体、重复 Rating 等不可读来源一律失败，不能当成功。
        var before = ParseStrict(packet.Data);

        // 同一文件多份 XMP 冲突无法靠改一份收敛；语义无效的单一 Rating 允许被纠正。
        if (before.State == XmpRatingState.Conflict)
            throw Unsupported($"{File.Name} 的内嵌 XMP 标注了多个互相冲突的 Rating：{Describe(before)}");

        // 写权限与随机寻址是真写之前必须证明的能力：同值 NoChange 也要通过，
        // 否则只读来源会以“无需写入”冒充成功，而整组永远无法收敛。
        var revision = await ProbeWriteAccessAsync(packet.Offset, cancellationToken).ConfigureAwait(false)
            ?? properties.Revision;

        if (IsSameRating(before, rating))
        {
            // 同值 Rated/Rejected 不需要任何结构定位或物理写入。
            return new XmpRatingPreparation
            {
                TargetFile = File,
                Source = Source,
                Before = before,
                Rating = rating,
                NoChange = true,
                SafeMode = safeMode,
                Revision = revision,
                PacketOffset = packet.Offset,
            };
        }

        var located = XmpRatingSpanLocator.TryLocate(packet.Data, out _, out var locateReason);
        XmpRatingEdit? edit = null;
        var inserted = false;

        if (located)
        {
            if (!XmpRatingSpanLocator.TryCreateEdit(packet.Data, rating, out var valueEdit, out _, out var editReason))
                throw Unsupported($"{File.Name}：{editReason ?? "无法在包内等长改写 Rating"}");
            edit = valueEdit;
        }
        else if (XmpRatingSpanLocator.TryCreateInsertion(packet.Data, rating, out var insertedEdit, out _, out var insertReason))
        {
            edit = insertedEdit;
            inserted = true;
        }
        else
        {
            throw Unsupported($"{File.Name}：{insertReason ?? locateReason ?? "无法在包内写入 Rating"}");
        }

        if (edit is not null)
        {
            if (edit.Original.Length != edit.Replacement.Length ||
                edit.Offset < 0 ||
                edit.Offset + edit.Length > packet.Data.Length)
            {
                throw Unsupported($"{File.Name} 的 Rating 改写区间无效");
            }
        }

        return new XmpRatingPreparation
        {
            TargetFile = File,
            Source = Source,
            Before = before,
            Rating = rating,
            NoChange = edit is null,
            SafeMode = safeMode,
            Revision = revision,
            PacketOffset = packet.Offset,
            OriginalPacket = edit is null ? null : packet.Data,
            EditOffset = edit?.Offset ?? 0,
            EditOriginalBytes = edit?.Original,
            EditReplacementBytes = edit?.Replacement,
            InsertedRating = inserted,
        };
    }

    public async Task<XmpRatingWriteReceipt> CommitAsync(
        XmpRatingPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.TargetFile.StorageId != File.StorageId)
            throw new ArgumentException("预检结果与当前存储目标不一致", nameof(preparation));

        if (preparation.NoChange)
        {
            // 同值不写盘，但必须证明“现在仍是目标值且写入期间未被外部修改”，不能回到预检结果就宣称成功。
            var current = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Exists)
                throw new StorageException(StorageError.ObjectGone, $"{File.Name} 已不存在");
            EnsureRevisionUnchanged(preparation.Revision, current.Revision, File.Name);

            var currentPacket = await ReadPacketAsync(cancellationToken).ConfigureAwait(false)
                ?? throw StorageException.ChangedExternally(File.Name);
            if (currentPacket.Offset != preparation.PacketOffset)
                throw StorageException.ChangedExternally(File.Name);

            var confirmed = ParseOrInvalid(currentPacket.Data);
            if (!IsSameRating(confirmed, preparation.Rating))
            {
                throw new StorageException(
                    StorageError.ChangedExternally,
                    $"{File.Name} 的 XMP Rating 在预检后已被外部修改为 {Describe(confirmed)}");
            }

            return new XmpRatingWriteReceipt
            {
                TargetFile = File,
                Before = preparation.Before,
                After = confirmed,
                Revision = current.Revision,
                NoChange = true,
                EditOffset = preparation.PacketOffset,
            };
        }

        var original = preparation.OriginalPacket
            ?? throw new ArgumentException("预检结果缺少原始 XMP 包", nameof(preparation));
        var originalEdit = preparation.EditOriginalBytes
            ?? throw new ArgumentException("预检结果缺少原始改写区间", nameof(preparation));
        var replacement = preparation.EditReplacementBytes
            ?? throw new ArgumentException("预检结果缺少替换区间", nameof(preparation));
        if (originalEdit.Length != replacement.Length)
            throw new ArgumentException("评分写入必须保持区间长度", nameof(preparation));

        var properties = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{File.Name} 已不存在");

        EnsureRevisionUnchanged(preparation.Revision, properties.Revision, File.Name);

        // 安全模式下不只看编辑区间：整个原始 XMP 包必须与预检时逐字节一致，
        // 防止 FileProvider/SAF 缓存 revision 时漏掉包内其它位置的外部修改。
        if (preparation.SafeMode)
        {
            var currentPacket = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            if (currentPacket is null ||
                currentPacket.Offset != preparation.PacketOffset ||
                currentPacket.Length != original.Length ||
                !currentPacket.Data.AsSpan().SequenceEqual(original))
            {
                throw StorageException.ChangedExternally(File.Name);
            }
        }

        var editOffset = preparation.PacketOffset + preparation.EditOffset;
        if (editOffset < 0)
            throw StorageException.ChangedExternally(File.Name);
        if (properties.Size is { } size && editOffset + replacement.Length > (long)size)
            throw StorageException.ChangedExternally(File.Name);

        var writeStarted = false;
        try
        {
            await using var session = await File
                .OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
                .ConfigureAwait(false);

            // 同一 RW 会话、Write 之前再次核对 revision 与字节，缩小“比完到写”之间的外部修改窗口。
            if (session.Revision is { } sessionRevision)
                EnsureRevisionUnchanged(preparation.Revision, sessionRevision, File.Name);
            if (preparation.SafeMode)
            {
                var currentPacket = await XmpContainerIO
                    .ReadExactlyAsync(session, preparation.PacketOffset, original.Length, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!currentPacket.AsSpan().SequenceEqual(original))
                    throw StorageException.ChangedExternally(File.Name);
            }
            else
            {
                var currentEdit = await XmpContainerIO
                    .ReadExactlyAsync(session, editOffset, originalEdit.Length, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!currentEdit.AsSpan().SequenceEqual(originalEdit))
                    throw StorageException.ChangedExternally(File.Name);
            }

            writeStarted = true;
            await session.WriteAsync(editOffset, replacement, CancellationToken.None).ConfigureAwait(false);
            await session.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!writeStarted) throw WrapFailure(exception);
            return await FailCommitAsync(
                    preparation, editOffset, originalEdit, replacement, exception)
                .ConfigureAwait(false);
        }

        XmpContainerPacket? afterPacket;
        try
        {
            afterPacket = await ReadPacketAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await FailCommitAsync(
                    preparation, editOffset, originalEdit, replacement, exception)
                .ConfigureAwait(false);
        }

        XmpRatingSnapshot? after = afterPacket is null ? null : ParseOrInvalid(afterPacket.Data);
        var verified = afterPacket is not null &&
            after is { } snapshot &&
            afterPacket.Offset == preparation.PacketOffset &&
            afterPacket.Length == original.Length &&
            IsSameRating(snapshot, preparation.Rating) &&
            OutsideEditEquals(original, afterPacket.Data, preparation.EditOffset, originalEdit.Length);
        if (!verified)
        {
            return await FailCommitAsync(
                    preparation,
                    editOffset,
                    originalEdit,
                    replacement,
                    new StorageException(StorageError.IoError, $"{File.Name} 的 XMP Rating 写入后校验失败"))
                .ConfigureAwait(false);
        }

        StorageRevision? writtenRevision;
        try
        {
            writtenRevision = (await File.GetBasicPropertiesAsync(CancellationToken.None).ConfigureAwait(false)).Revision;
        }
        catch (Exception exception)
        {
            return await FailCommitAsync(
                    preparation, editOffset, originalEdit, replacement, exception)
                .ConfigureAwait(false);
        }

        return new XmpRatingWriteReceipt
        {
            TargetFile = File,
            Before = preparation.Before,
            After = after.GetValueOrDefault(),
            OriginalPacket = new XmpContainerPacket(preparation.PacketOffset, original.Length, original),
            Revision = writtenRevision,
            EditOffset = editOffset,
            EditOriginalBytes = originalEdit,
            EditWrittenBytes = replacement,
        };
    }

    /// <summary>
    /// 提交失败后的统一处理：先证明是否真的产生了改动；有改动则尝试恢复本来源。
    /// 恢复成功按原始异常失败返回（无残留）；恢复失败返回带 <see cref="XmpRatingWriteReceipt.Failure"/>
    /// 的回执，让事务层把它纳入逆序补偿，而不是误报“未修改任何文件”。
    /// </summary>
    private async Task<XmpRatingWriteReceipt> FailCommitAsync(
        XmpRatingPreparation preparation,
        long editOffset,
        byte[] originalEdit,
        byte[] written,
        Exception failure)
    {
        var original = preparation.OriginalPacket!;
        XmpContainerPacket? current = null;
        try
        {
            current = await ReadPacketAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读取失败时无法证明未修改，继续走恢复/回执路径。
        }

        var unchanged = current is not null &&
            current.Offset == preparation.PacketOffset &&
            current.Length == original.Length &&
            current.Data.AsSpan().SequenceEqual(original);
        if (unchanged) throw WrapFailure(failure);

        // 只在本写入区间之外仍与原始包一致时才自愈，避免把外部已改动的其它内容混进来。
        var outsideUntouched = current is not null &&
            current.Offset == preparation.PacketOffset &&
            current.Length == original.Length &&
            OutsideEditEquals(original, current.Data, preparation.EditOffset, originalEdit.Length);
        if (outsideUntouched)
        {
            var restored = await TryRestoreRangeAsync(
                    editOffset, written, originalEdit, CancellationToken.None)
                .ConfigureAwait(false);
            if (restored) throw WrapFailure(failure);
        }

        var after = preparation.Before;
        if (current is not null)
        {
            try
            {
                after = ParseOrInvalid(current.Data);
            }
            catch (Exception)
            {
                // 保持写入前快照；事务层稍后会按磁盘真值重读。
            }
        }

        StorageRevision? revision = null;
        try
        {
            revision = (await File.GetBasicPropertiesAsync(CancellationToken.None).ConfigureAwait(false)).Revision;
        }
        catch (Exception)
        {
        }

        return new XmpRatingWriteReceipt
        {
            TargetFile = File,
            Before = preparation.Before,
            After = after,
            OriginalPacket = new XmpContainerPacket(preparation.PacketOffset, original.Length, original),
            Revision = revision,
            EditOffset = editOffset,
            EditOriginalBytes = originalEdit,
            EditWrittenBytes = written,
            Failure = WrapFailure(failure).Message,
        };
    }

    private static StorageException WrapFailure(Exception exception) =>
        exception is StorageException storage
            ? storage
            : new StorageException(StorageError.IoError, $"写入 XMP 评分失败：{exception.Message}", exception);

    public async Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.TargetFile.StorageId != File.StorageId)
            throw new ArgumentException("回执与当前存储目标不一致", nameof(receipt));
        if (receipt.NoChange)
            throw new ArgumentException("同值写入没有可补偿的修改", nameof(receipt));

        var originalPacket = receipt.OriginalPacket
            ?? throw Unsupported($"{File.Name} 的回执缺少原始 XMP 包，无法补偿");
        var originalEdit = receipt.EditOriginalBytes
            ?? throw Unsupported($"{File.Name} 的回执缺少原始区间，无法补偿");
        var writtenEdit = receipt.EditWrittenBytes
            ?? throw Unsupported($"{File.Name} 的回执缺少写入区间，无法补偿");
        if (originalEdit.Length != writtenEdit.Length)
            throw Unsupported($"{File.Name} 的回执区间长度不一致，无法补偿");

        var properties = await File.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{File.Name} 已不存在，无法补偿");
        EnsureRevisionUnchanged(receipt.Revision, properties.Revision, File.Name);

        var currentPacket = await ReadPacketAsync(cancellationToken).ConfigureAwait(false)
            ?? throw StorageException.ChangedExternally(File.Name);
        var editRelativeLong = receipt.EditOffset - originalPacket.Offset;
        if (editRelativeLong < 0 || editRelativeLong > int.MaxValue)
            throw StorageException.ChangedExternally(File.Name);
        var editRelative = (int)editRelativeLong;
        if (currentPacket.Offset != originalPacket.Offset ||
            currentPacket.Length != originalPacket.Data.Length ||
            !OutsideEditEquals(originalPacket.Data, currentPacket.Data, editRelative, writtenEdit.Length))
        {
            throw StorageException.ChangedExternally(File.Name);
        }

        await using (var session = await File
            .OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false))
        {
            if (session.Revision is { } sessionRevision)
                EnsureRevisionUnchanged(receipt.Revision, sessionRevision, File.Name);

            var currentRange = await XmpContainerIO
                .ReadExactlyAsync(session, receipt.EditOffset, writtenEdit.Length, CancellationToken.None)
                .ConfigureAwait(false);
            if (!currentRange.AsSpan().SequenceEqual(writtenEdit))
                throw StorageException.ChangedExternally(File.Name);

            var currentFull = await XmpContainerIO
                .ReadExactlyAsync(session, originalPacket.Offset, originalPacket.Data.Length, CancellationToken.None)
                .ConfigureAwait(false);
            if (!OutsideEditEquals(originalPacket.Data, currentFull, editRelative, writtenEdit.Length))
                throw StorageException.ChangedExternally(File.Name);

            await session.WriteAsync(receipt.EditOffset, originalEdit, CancellationToken.None).ConfigureAwait(false);
            await session.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var restored = await ReadPacketAsync(CancellationToken.None).ConfigureAwait(false);
        if (restored is null || !restored.Data.AsSpan().SequenceEqual(originalPacket.Data))
            throw new StorageException(StorageError.IoError, $"{File.Name} 的 XMP 补偿后校验失败");

        var snapshot = ParseOrInvalid(restored.Data);
        if (!SameSnapshot(receipt.Before, snapshot))
        {
            throw new StorageException(
                StorageError.IoError, $"{File.Name} 的 XMP 补偿后 Rating 与写入前不一致");
        }
    }

    private async Task<bool> TryRestoreRangeAsync(
        long editOffset,
        byte[] written,
        byte[] original,
        CancellationToken cancellationToken)
    {
        try
        {
            await XmpContainerIO.WritePacketAsync(
                    File,
                    new XmpContainerPacket(editOffset, written.Length, written),
                    original,
                    expectedRevision: null,
                    safeMode: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var restored = await ReadPacketAsync(CancellationToken.None).ConfigureAwait(false);
            if (restored is null) return false;
            var relative = editOffset - restored.Offset;
            if (relative < 0 || relative + original.Length > restored.Data.Length) return false;
            return restored.Data
                .Skip((int)relative)
                .Take(original.Length)
                .SequenceEqual(original);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task<XmpContainerPacket?> ReadPacketAsync(CancellationToken cancellationToken) =>
        Adapter.ReadCanonicalPacketAsync(File, cancellationToken);

    /// <summary>
    /// 预检的写权限/随机寻址探测：以 ReadWrite 打开同一文件的现有内容（不截断、不新建），
    /// 在包偏移处做一次定位读取，证明该来源现在确实可写且可按偏移定位；全程不写任何字节。
    /// 返回会话 revision，供提交阶段复核。
    /// </summary>
    private async Task<StorageRevision?> ProbeWriteAccessAsync(
        long packetOffset,
        CancellationToken cancellationToken)
    {
        await using var session = await File
            .OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        if (session.Length <= 0)
            throw new StorageException(StorageError.IoError, $"{File.Name} 为空，无法验证写入区间");
        if (packetOffset < 0 || packetOffset >= session.Length)
            throw StorageException.ChangedExternally(File.Name);

        var probe = new byte[1];
        var read = await session
            .ReadAsync(packetOffset, probe, cancellationToken)
            .ConfigureAwait(false);
        if (read != 1)
            throw new StorageException(StorageError.IoError, $"{File.Name} 无法按偏移定位写入区间");

        return session.Revision;
    }

    private XmpRatingSnapshot ParseOrInvalid(byte[] packet)
    {
        try
        {
            return XmpRatingReader.ReadPacket(packet, Source);
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            return Invalid(exception);
        }
    }

    private XmpRatingSnapshot ParseStrict(byte[] packet)
    {
        try
        {
            return XmpRatingReader.ReadPacket(packet, Source);
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            throw Unsupported($"{File.Name} 的 XMP 无法解析：{exception.Message}");
        }
    }

    private XmpRatingSnapshot Invalid(Exception exception) =>
        new(XmpRatingState.Invalid, null, Source, $"XMP 无法解析：{exception.Message}");

    private static bool IsSameRating(XmpRatingSnapshot snapshot, int rating) =>
        rating == -1
            ? snapshot.State == XmpRatingState.Rejected
            : snapshot.State == XmpRatingState.Rated && snapshot.Value == rating;

    private static bool SameSnapshot(XmpRatingSnapshot left, XmpRatingSnapshot right) =>
        left.State == right.State && left.Value == right.Value;

    private static bool OutsideEditEquals(
        byte[] original,
        byte[] current,
        int editOffset,
        int editLength)
    {
        if (current.Length != original.Length ||
            editOffset < 0 ||
            editLength < 0 ||
            editOffset + editLength > original.Length)
        {
            return false;
        }

        return original.AsSpan(0, editOffset).SequenceEqual(current.AsSpan(0, editOffset)) &&
            original.AsSpan(editOffset + editLength).SequenceEqual(current.AsSpan(editOffset + editLength));
    }

    private static void EnsureRevisionUnchanged(
        StorageRevision? expected,
        StorageRevision? current,
        string name)
    {
        if (expected is not { } wanted) return;
        var known = wanted.Size is not null || wanted.LastModified is not null || wanted.Version is not null;
        if (!known) return;
        if (current is not { } actual) throw StorageException.ChangedExternally(name);

        if (wanted.Size is { } size && actual.Size != size)
            throw StorageException.ChangedExternally(name);
        if (wanted.LastModified is { } modified && actual.LastModified != modified)
            throw StorageException.ChangedExternally(name);
        if (wanted.Version is { } version &&
            !string.Equals(actual.Version, version, StringComparison.Ordinal))
        {
            throw StorageException.ChangedExternally(name);
        }
    }

    private static string Describe(XmpRatingSnapshot snapshot) => snapshot.State switch
    {
        XmpRatingState.Invalid => snapshot.Error ?? "无效评分",
        XmpRatingState.Unreadable => snapshot.Error ?? "来源无法读取",
        XmpRatingState.Conflict => snapshot.Error ?? "多个 XMP 评分冲突",
        _ => snapshot.State.ToString(),
    };

    private StorageException Unsupported(string message) =>
        new(StorageError.Unsupported, message);
}
