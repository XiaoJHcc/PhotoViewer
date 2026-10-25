using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

public sealed record XmpContainerPacketDescriptor(long Offset, byte[] Data);

public sealed record XmpContainerPacket(
    long Offset,
    int Length,
    byte[] Data,
    XmpContainerPacketDescriptor? Descriptor = null);

/// <summary>
/// 一次评分写入的回执。回执只描述已经发生的物理修改：
/// <see cref="NoChange"/> 为 true 时磁盘未被触碰，补偿阶段必须跳过它。
/// </summary>
public sealed class XmpRatingWriteReceipt
{
    public required IFileRef TargetFile { get; init; }
    public required XmpRatingSnapshot Before { get; init; }
    public required XmpRatingSnapshot After { get; init; }
    public XmpContainerPacket? OriginalPacket { get; init; }
    public StorageRevision? Revision { get; init; }
    public long? OriginalFileLength { get; init; }
    public bool OriginalHadZeroLengthFinalBox { get; init; }
    public bool CreatedTarget { get; init; }

    /// <summary>同值写入：没有发生任何物理修改。</summary>
    public bool NoChange { get; init; }

    /// <summary>本次原位写入的文件绝对偏移；<see cref="NoChange"/> 时为 0。</summary>
    public long EditOffset { get; init; }

    /// <summary>写入前该区间的原始字节。</summary>
    public byte[]? EditOriginalBytes { get; init; }

    /// <summary>本次写入该区间的字节，用于补偿时确认磁盘仍是本次写入的内容。</summary>
    public byte[]? EditWrittenBytes { get; init; }

    /// <summary>
    /// 非 null 表示提交失败且无法证明已恢复原内容。事务层必须把该回执纳入逆序补偿，
    /// 并按磁盘真值报告，不能声称“未修改任何文件”。
    /// </summary>
    public string? Failure { get; init; }
}

/// <summary>
/// 单个评分来源（一个文件的内嵌 XMP 或一个已存在的 sidecar）的写入准备。
/// 预检阶段只读：<see cref="IXmpRatingStore.PrepareAsync"/> 返回本对象时不修改任何文件。
/// </summary>
public sealed class XmpRatingPreparation
{
    public required IFileRef TargetFile { get; init; }
    public required XmpRatingSource Source { get; init; }
    public required XmpRatingSnapshot Before { get; init; }
    public required int Rating { get; init; }
    public required bool NoChange { get; init; }

    /// <summary>本次预检使用的安全模式：关闭时提交仍做写后校验与 revision 比对，只跳过区间字节比对。</summary>
    public bool SafeMode { get; init; }

    /// <summary>预检时读取的文件修订，提交前必须仍然一致。</summary>
    public StorageRevision? Revision { get; init; }

    /// <summary>包在容器中的偏移（sidecar 为 0）。</summary>
    public long PacketOffset { get; init; }

    /// <summary>原始包字节；<see cref="NoChange"/> 时为 null。</summary>
    public byte[]? OriginalPacket { get; init; }

    /// <summary>最小改写区间在包内的偏移。</summary>
    public int EditOffset { get; init; }

    /// <summary>最小改写区间的原始字节。</summary>
    public byte[]? EditOriginalBytes { get; init; }

    /// <summary>最小改写区间的替换字节（与原始区间等长）。</summary>
    public byte[]? EditReplacementBytes { get; init; }

    /// <summary>本次写入是否通过向已有 canonical XMP 插入 Rating 实现。</summary>
    public bool InsertedRating { get; init; }
}

/// <summary>
/// 一个评分来源的读写契约。实现只操作该来源自身：
/// 内嵌存储永远不回退 sidecar，sidecar 存储永远不创建新文件。
/// </summary>
public interface IXmpRatingStore
{
    Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 预检：读取当前包并计算等长最小改写区间（<paramref name="rating"/> 允许 -1..5）。
    /// 只读；不支持的来源以 <see cref="StorageError.Unsupported"/> 抛出，使整组在首次修改前失败。
    /// </summary>
    Task<XmpRatingPreparation> PrepareAsync(
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default);

    /// <summary>提交预检结果；<see cref="XmpRatingPreparation.NoChange"/> 时返回 NoChange 回执且不写盘。</summary>
    Task<XmpRatingWriteReceipt> CommitAsync(
        XmpRatingPreparation preparation,
        CancellationToken cancellationToken = default);

    /// <summary>按回执逆序补偿；只恢复本次写入且磁盘仍是本次内容的来源。</summary>
    Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default);
}

/// <summary>单来源写入的便捷入口：预检 + 提交。</summary>
public static class XmpRatingStoreExtensions
{
    public static async Task<XmpRatingWriteReceipt> WriteAsync(
        this IXmpRatingStore store,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var preparation = await store.PrepareAsync(rating, safeMode, cancellationToken).ConfigureAwait(false);
        return await store.CommitAsync(preparation, cancellationToken).ConfigureAwait(false);
    }
}

public interface IXmpContainerAdapter
{
    Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default);

    Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default);
}
