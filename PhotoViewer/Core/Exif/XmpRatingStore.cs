using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

public sealed record XmpContainerPacketDescriptor(long Offset, byte[] Data);

public sealed record XmpContainerPacket(
    long Offset,
    int Length,
    byte[] Data,
    XmpContainerPacketDescriptor? Descriptor = null);

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
}

public interface IXmpRatingStore
{
    Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    Task<XmpRatingWriteReceipt> WriteAsync(
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default);
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
