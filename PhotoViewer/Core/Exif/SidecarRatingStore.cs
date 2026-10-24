using System.Xml;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// 服务专有 RAW 的 sidecar 评分存储。
/// </summary>
/// <remarks>
/// <para>sidecar 命名为 <see cref="XmpRatingReader.SidecarName"/>；存在时它是唯一权威，
/// 读取不再回看内嵌 XMP，避免两个位置给出不同评分。</para>
/// <para>首次创建 sidecar 时，用内嵌 canonical XMP 包作模板重新序列化，从而保留其它属性；
/// 内嵌包无法解析、或 canonical 包声明了扩展包时明确报
/// <see cref="StorageError.Unsupported"/>，不静默丢属性；内嵌没有 XMP 时创建最小包。</para>
/// <para>新建或空目标一律经 <c>CreateFile</c> 后以随机访问会话在偏移 0 写入并 Flush，
/// 不依赖 <see cref="IFileRef.AtomicWriteAsync"/>（Android SAF 不提供原子替换）；
/// 写入失败时删除新建文件、或把原有空文件截回 0 字节。已有 sidecar 等长时原位写；
/// 需要变长时必须有原子替换能力，平台不支持则在修改前失败——随机整写加截断只允许出现在
/// 补偿/自愈路径，用来恢复已知的原始包。</para>
/// <para>写入与恢复都会校验已知的 <see cref="StorageRevision"/>，避免把“刚读到的 revision”
/// 当作覆盖授权；恢复完成后再按字节校验内容。</para>
/// <para><c>.xmp</c> 独立文件用 <see cref="ForStandaloneXmp"/> 走同一套读写逻辑，
/// 只是目标就是文件自身，没有内嵌回退；文件不存在时同样先经父目录 <c>CreateFile</c>。</para>
/// </remarks>
public sealed class SidecarRatingStore : IXmpRatingStore
{
    private const int MaxPacketBytes = 64 << 20;

    private const string XmpNoteNamespace = "http://ns.adobe.com/xmp/note/";

    private readonly IFileRef _source;
    private readonly IXmpContainerAdapter? _embedded;
    private readonly bool _standalone;

    private SidecarRatingStore(IFileRef source, IXmpContainerAdapter? embedded, bool standalone)
    {
        _source = source;
        _embedded = embedded;
        _standalone = standalone;
    }

    /// <summary>专有 RAW：<paramref name="photo"/> 是照片本体，评分写入同名 sidecar。</summary>
    public static SidecarRatingStore ForPhoto(IFileRef photo, IXmpContainerAdapter embeddedAdapter)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(embeddedAdapter);
        return new SidecarRatingStore(photo, embeddedAdapter, standalone: false);
    }

    /// <summary><c>.xmp</c> 独立文件：直接读写该文件自身。</summary>
    public static SidecarRatingStore ForStandaloneXmp(IFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SidecarRatingStore(file, null, standalone: true);
    }

    public IFileRef SourceFile => _source;

    public async Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_standalone)
            return await ReadFileAsync(_source, XmpRatingSource.Sidecar, cancellationToken).ConfigureAwait(false);

        var sidecar = await FindSidecarAsync(cancellationToken).ConfigureAwait(false);
        if (sidecar != null)
            return await ReadFileAsync(sidecar, XmpRatingSource.Sidecar, cancellationToken).ConfigureAwait(false);

        var packet = await _embedded!
            .ReadCanonicalPacketAsync(_source, cancellationToken)
            .ConfigureAwait(false);
        if (packet is null) return XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);
        return ParseOrInvalid(packet.Data, XmpRatingSource.Embedded);
    }

    public async Task<XmpRatingWriteReceipt> WriteAsync(
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        if (rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 0 到 5 之间");

        if (_standalone)
        {
            var properties = await _source.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            if (properties.Exists)
            {
                return await WriteExistingAsync(_source, properties, rating, safeMode, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CreateMissingStandaloneAsync(rating, cancellationToken).ConfigureAwait(false);
        }

        var parent = await GetParentAsync(cancellationToken).ConfigureAwait(false);
        var name = XmpRatingReader.SidecarName(_source.Name);
        var sidecar = await parent.FindFileAsync(name, cancellationToken).ConfigureAwait(false);
        if (sidecar != null)
        {
            var properties = await sidecar.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            if (properties.Exists)
            {
                return await WriteExistingAsync(sidecar, properties, rating, safeMode, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var embeddedPacket = await _embedded!
            .ReadCanonicalPacketAsync(_source, cancellationToken)
            .ConfigureAwait(false);
        if (embeddedPacket is not null && HasExtendedXmp(embeddedPacket.Data))
        {
            throw new StorageException(
                StorageError.Unsupported,
                $"{_source.Name} 的 XMP 声明了扩展包（xmpNote:HasExtendedXMP），无法在不丢失属性的前提下创建 sidecar");
        }

        var before = embeddedPacket is null
            ? XmpRatingSnapshot.Missing(XmpRatingSource.Embedded)
            : ParseOrInvalid(embeddedPacket.Data, XmpRatingSource.Embedded);
        var packetBytes = BuildPacket(embeddedPacket?.Data, rating, _source.Name);

        // CreateFile 之后即进入物理修改阶段，不再响应取消。
        var created = await parent.CreateFileAsync(name, CancellationToken.None).ConfigureAwait(false);
        return await CreateAsync(
                created,
                packetBytes,
                before,
                rating,
                originalBytes: null,
                expectedRevision: null,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var target = receipt.TargetFile;

        if (receipt.OriginalPacket is null)
        {
            // 没有原始包：该目标由本次写入创建，补偿就是删除。
            if (!receipt.CreatedTarget)
                throw new ArgumentException("回执既没有原始 XMP 包，也没有标记为新建目标", nameof(receipt));
            await DeleteAsync(target, receipt.Revision, cancellationToken).ConfigureAwait(false);
            return;
        }

        var properties = await target.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists)
            throw new StorageException(StorageError.ObjectGone, $"{target.Name} 已不存在，无法恢复原始 XMP");

        // 只接受回执里记录的“写后 revision”，不拿刚读到的 revision 当授权。
        RequireRevisionMatches(receipt.Revision, properties.Revision, target.Name);

        var original = receipt.OriginalPacket.Data;
        var current = await ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        if (current.AsSpan().SequenceEqual(original)) return;

        if (current.Length == original.Length)
        {
            await XmpContainerIO.WritePacketAsync(
                    target,
                    new XmpContainerPacket(0, current.Length, current),
                    original,
                    properties.Revision,
                    safeMode: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (original.Length == 0)
        {
            // 原为空 sidecar：截断恢复，绝不为了清空去走原子替换。
            await WriteRandomAccessAsync(target, [], properties.Revision, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteRecoveryWholeAsync(target, original, properties.Revision, cancellationToken)
                .ConfigureAwait(false);
        }

        var restored = await ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        if (!restored.AsSpan().SequenceEqual(original))
        {
            throw new StorageException(
                StorageError.IoError, $"{target.Name} 的 XMP 恢复后校验失败");
        }
    }

    private async Task<XmpRatingWriteReceipt> CreateMissingStandaloneAsync(
        int rating,
        CancellationToken cancellationToken)
    {
        var parent = await _source.GetParentAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new StorageException(
                         StorageError.ObjectGone, $"{_source.Name} 没有可创建文件的父目录");

        cancellationToken.ThrowIfCancellationRequested();

        // 不存在的文件不能直接随机打开：先建空文件，再按新建目标写入。
        var created = await parent.CreateFileAsync(_source.Name, CancellationToken.None).ConfigureAwait(false);
        return await CreateAsync(
                created,
                BuildPacket(null, rating, created.Name),
                XmpRatingSnapshot.Missing(XmpRatingSource.Sidecar),
                rating,
                originalBytes: null,
                expectedRevision: null,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<XmpRatingWriteReceipt> WriteExistingAsync(
        IFileRef file,
        StorageItemBasicProperties properties,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        if (properties.Size == 0)
        {
            // 空 sidecar 没有任何可保留的属性，按新建处理（补偿时还原为空文件）。
            return await CreateAsync(
                    file,
                    BuildPacket(null, rating, file.Name),
                    XmpRatingSnapshot.Missing(XmpRatingSource.Sidecar),
                    rating,
                    originalBytes: [],
                    expectedRevision: properties.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var original = await ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        if (!TryParse(original, XmpRatingSource.Sidecar, out var before, out var parseError))
        {
            throw new StorageException(
                StorageError.Unsupported,
                $"{file.Name} 的 XMP 无法解析，拒绝覆盖以免丢失其它属性：{parseError}");
        }

        if (before.State == XmpRatingState.Rated && before.Value == rating)
        {
            return new XmpRatingWriteReceipt
            {
                TargetFile = file,
                Before = before,
                After = before,
                OriginalPacket = new XmpContainerPacket(0, original.Length, original),
                Revision = properties.Revision,
            };
        }

        // 从这里起进入物理修改阶段：取消不再生效，保证不会留下半写状态。
        var updated = BuildUpdated(original, rating, file.Name);
        StorageRevision? writtenRevision = null;
        try
        {
            if (updated.Length == original.Length)
            {
                await XmpContainerIO.WritePacketAsync(
                        file,
                        new XmpContainerPacket(0, original.Length, original),
                        updated,
                        properties.Revision,
                        safeMode,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteAtomicWholeAsync(file, updated, properties.Revision, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            writtenRevision = await ReadRevisionAsync(file, CancellationToken.None).ConfigureAwait(false);
            var after = await ReadFileAsync(file, XmpRatingSource.Sidecar, CancellationToken.None)
                .ConfigureAwait(false);
            if (after.State != XmpRatingState.Rated || after.Value != rating)
                throw new StorageException(StorageError.IoError, $"{file.Name} 的 XMP Rating 写入后校验失败");

            return new XmpRatingWriteReceipt
            {
                TargetFile = file,
                Before = before,
                After = after,
                OriginalPacket = new XmpContainerPacket(0, original.Length, original),
                Revision = writtenRevision,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 只有 IoError 才可能已经在磁盘上留下改动；其它错误（外部修改、只读等）
            // 发生在写入之前，此时回写原内容反而会覆盖别人的修改。
            if (exception is StorageException storage && storage.Error != StorageError.IoError)
                throw;

            var restored = await TryRestoreOriginalAsync(
                    file, original, updated, writtenRevision, CancellationToken.None)
                .ConfigureAwait(false);
            if (exception is StorageException known)
            {
                if (restored) throw;
                throw new StorageException(
                    known.Error, $"{known.Message}（恢复原始 XMP 失败，磁盘可能已改变）", known);
            }

            throw restored
                ? new StorageException(
                    StorageError.IoError, $"{file.Name} 的 XMP 写入失败，已恢复原始内容", exception)
                : new StorageException(
                    StorageError.IoError, $"{file.Name} 的 XMP 写入失败，且未能恢复原始内容", exception);
        }
    }

    private async Task<XmpRatingWriteReceipt> CreateAsync(
        IFileRef target,
        byte[] packet,
        XmpRatingSnapshot before,
        int rating,
        byte[]? originalBytes,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        StorageRevision? writtenRevision = null;
        try
        {
            // 新建/空目标只能随机访问写入：SAF 没有原子替换。
            await WriteRandomAccessAsync(target, packet, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
            writtenRevision = await ReadRevisionAsync(target, cancellationToken).ConfigureAwait(false);

            var after = await ReadFileAsync(target, XmpRatingSource.Sidecar, cancellationToken)
                .ConfigureAwait(false);
            if (after.State != XmpRatingState.Rated || after.Value != rating)
                throw new StorageException(
                    StorageError.IoError, $"{target.Name} 的 XMP Rating 写入后校验失败");

            return new XmpRatingWriteReceipt
            {
                TargetFile = target,
                Before = before,
                After = after,
                OriginalPacket = originalBytes is null
                    ? null
                    : new XmpContainerPacket(0, originalBytes.Length, originalBytes),
                Revision = writtenRevision,
                CreatedTarget = originalBytes is null,
            };
        }
        catch
        {
            if (originalBytes is null)
            {
                await TryDeleteAsync(target).ConfigureAwait(false);
            }
            else
            {
                await TryRestoreOriginalAsync(
                        target, originalBytes, packet, writtenRevision, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 写入失败后的自愈：只在磁盘内容仍是我们刚写入的字节、且已知的写后 revision 仍匹配时
    /// 才回写原内容，避免覆盖外部新修改。
    /// </summary>
    private static async Task<bool> TryRestoreOriginalAsync(
        IFileRef file,
        byte[] original,
        byte[]? expectedWritten,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            if (!properties.Exists) return false;
            if (!RevisionMatchesIfKnown(expectedRevision, properties.Revision)) return false;

            var current = await ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            if (current.AsSpan().SequenceEqual(original)) return true;
            if (expectedWritten is not null && !current.AsSpan().SequenceEqual(expectedWritten)) return false;

            if (current.Length == original.Length)
            {
                await XmpContainerIO.WritePacketAsync(
                        file,
                        new XmpContainerPacket(0, current.Length, current),
                        original,
                        properties.Revision,
                        safeMode: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (original.Length == 0)
            {
                await WriteRandomAccessAsync(file, [], properties.Revision, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteRecoveryWholeAsync(file, original, properties.Revision, cancellationToken)
                    .ConfigureAwait(false);
            }

            var restored = await ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            return restored.AsSpan().SequenceEqual(original);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<IFileRef?> FindSidecarAsync(CancellationToken cancellationToken)
    {
        var parent = await GetParentAsync(cancellationToken).ConfigureAwait(false);
        return await parent
            .FindFileAsync(XmpRatingReader.SidecarName(_source.Name), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IFolderRef> GetParentAsync(CancellationToken cancellationToken) =>
        await _source.GetParentAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new StorageException(
            StorageError.ObjectGone, $"{_source.Name} 没有可放置 sidecar 的父目录");

    /// <summary>
    /// 扩展 XMP 包的数据不在 canonical 包里，无法随模板并入 sidecar。
    /// 扩展包存在时 canonical 包会声明 <c>xmpNote:HasExtendedXMP</c>，据此明确拒绝，
    /// 而不是静默丢掉只存在于扩展包里的属性。
    /// </summary>
    private static bool HasExtendedXmp(byte[] packet)
    {
        try
        {
            var xmp = XmpMetaFactory.ParseFromBuffer(packet);
            return xmp.GetProperty(XmpNoteNamespace, "HasExtendedXMP")?.Value != null;
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            // 无法解析的模板稍后会由 BuildPacket 报 Unsupported。
            return false;
        }
    }

    private static async Task DeleteAsync(
        IFileRef file,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists) return;

        // 删除同样是破坏性操作：文件在写入后被外部改过就不动它。
        RequireRevisionMatches(expectedRevision, properties.Revision, file.Name);

        await file.DeleteAsync(cancellationToken).ConfigureAwait(false);
        var after = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (after.Exists)
            throw new StorageException(StorageError.IoError, $"删除 {file.Name} 失败");
    }

    private static async Task TryDeleteAsync(IFileRef file)
    {
        try
        {
            if (!(await file.GetBasicPropertiesAsync(CancellationToken.None).ConfigureAwait(false)).Exists)
                return;
            await file.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 清理失败不覆盖原始写入异常。
        }
    }

    /// <summary>
    /// 随机访问整写：写入偏移 0 后按目标长度截断并 Flush。
    /// 新建与清空都走这里，不依赖平台的原子替换能力。
    /// </summary>
    private static async Task WriteRandomAccessAsync(
        IFileRef file,
        byte[] bytes,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var session = await file
            .OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        RequireRevisionMatches(expectedRevision, session.Revision, file.Name);

        if (bytes.Length > 0)
            await session.WriteAsync(0, bytes, cancellationToken).ConfigureAwait(false);
        // 新建（0 → n）不需要截断；只有覆盖更短内容/清空才要求平台提供 SetLength。
        if (session.Length > bytes.Length)
            await session.SetLengthAsync(bytes.Length, cancellationToken).ConfigureAwait(false);
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 正常写入的变长替换：必须原子替换。平台明确不具备原子替换能力时在修改前失败，
    /// 绝不用“随机整写 + 截断”降级——崩溃窗口会留下半份 XML。
    /// </summary>
    private static async Task WriteAtomicWholeAsync(
        IFileRef file,
        byte[] bytes,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (file.Capabilities.AtomicReplace == StorageCapability.Unsupported)
            throw NotAtomicCapable(file.Name);

        try
        {
            await file.AtomicWriteAsync(
                    async (output, token) =>
                    {
                        await output.WriteAsync(bytes, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                    },
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageException exception) when (exception.Error == StorageError.Unsupported)
        {
            throw NotAtomicCapable(file.Name, exception);
        }

        static StorageException NotAtomicCapable(string name, Exception? inner = null) =>
            new(
                StorageError.Unsupported,
                $"{name} 的 XMP 包变长需要原子替换，当前存储不支持，未修改文件",
                inner);
    }

    /// <summary>
    /// 补偿/自愈专用整写：优先原子替换，平台不支持时退回随机访问整写加截断。
    /// 这条降级路径本身不是原子操作，只允许用于把内容恢复成已知的原始包，
    /// 正常写入一律走 <see cref="WriteAtomicWholeAsync"/>。
    /// </summary>
    private static async Task WriteRecoveryWholeAsync(
        IFileRef file,
        byte[] bytes,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (file.Capabilities.AtomicReplace == StorageCapability.Unsupported)
        {
            await WriteRandomAccessAsync(file, bytes, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await file.AtomicWriteAsync(
                    async (output, token) =>
                    {
                        await output.WriteAsync(bytes, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                    },
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageException exception) when (exception.Error == StorageError.Unsupported)
        {
            await WriteRandomAccessAsync(file, bytes, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<StorageRevision?> ReadRevisionAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        return properties.Exists ? properties.Revision : null;
    }

    private static void RequireRevisionMatches(
        StorageRevision? expected,
        StorageRevision? current,
        string name)
    {
        if (expected is not { } wanted) return;
        if (wanted.Size is null && wanted.LastModified is null && wanted.Version is null) return;
        if (current is not { } actual || !RevisionMatches(wanted, actual))
            throw StorageException.ChangedExternally(name);
    }

    private static bool RevisionMatchesIfKnown(StorageRevision? expected, StorageRevision? current)
    {
        if (expected is not { } wanted) return true;
        if (wanted.Size is null && wanted.LastModified is null && wanted.Version is null) return true;
        return current is { } actual && RevisionMatches(wanted, actual);
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

    private static async Task<XmpRatingSnapshot> ReadFileAsync(
        IFileRef file,
        XmpRatingSource source,
        CancellationToken cancellationToken)
    {
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists || properties.Size == 0) return XmpRatingSnapshot.Missing(source);
        var bytes = await ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        return ParseOrInvalid(bytes, source);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFileRef file, CancellationToken cancellationToken)
    {
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPacketBytes)
            {
                throw new StorageException(
                    StorageError.Unsupported,
                    $"{file.Name} 的 XMP 包超过 {MaxPacketBytes / (1024 * 1024)}MB 上限");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static XmpRatingSnapshot ParseOrInvalid(byte[] packet, XmpRatingSource source)
    {
        TryParse(packet, source, out var snapshot, out _);
        return snapshot;
    }

    private static bool TryParse(
        byte[] packet,
        XmpRatingSource source,
        out XmpRatingSnapshot snapshot,
        out string? error)
    {
        try
        {
            snapshot = XmpRatingReader.ReadPacket(packet, source);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            snapshot = new XmpRatingSnapshot(
                XmpRatingState.Invalid, null, source, $"XMP 无法解析：{exception.Message}");
            error = exception.Message;
            return false;
        }
    }

    private static byte[] BuildPacket(byte[]? template, int rating, string name)
    {
        try
        {
            return XmpPacketEditor.SetRating(template, rating, preserveLength: false);
        }
        catch (Exception exception) when (exception is XmpException or XmlException or InvalidOperationException)
        {
            throw new StorageException(
                StorageError.Unsupported,
                template is null
                    ? $"{name}：无法生成最小 XMP 包"
                    : $"{name}：内嵌 XMP 无法重新序列化，拒绝在丢失其它属性的前提下创建 sidecar",
                exception);
        }
    }

    private static byte[] BuildUpdated(byte[] original, int rating, string name)
    {
        try
        {
            return XmpPacketEditor.SetRating(original, rating, preserveLength: true);
        }
        catch (Exception exception) when (exception is XmpException or XmlException or InvalidOperationException)
        {
            try
            {
                return XmpPacketEditor.SetRating(original, rating, preserveLength: false);
            }
            catch (Exception growException) when (growException is XmpException or XmlException or InvalidOperationException)
            {
                throw new StorageException(
                    StorageError.Unsupported, $"{name} 的 XMP 无法重新序列化", growException);
            }
        }
    }
}
