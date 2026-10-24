using System.Xml;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// 内嵌 XMP 容器评分存储：只读写适配器认定的 canonical XMP 包，不猜测容器布局。
/// 写入与写后语义校验由 <see cref="IXmpContainerAdapter"/> 负责，本类只透传回执；
/// 适配器抛出异常即代表该文件没有可交给事务层补偿的改动。
/// </summary>
public sealed class ContainerRatingStore : IXmpRatingStore
{
    public ContainerRatingStore(IFileRef file, IXmpContainerAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(adapter);
        File = file;
        Adapter = adapter;
    }

    public IFileRef File { get; }

    public IXmpContainerAdapter Adapter { get; }

    public async Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var packet = await Adapter.ReadCanonicalPacketAsync(File, cancellationToken).ConfigureAwait(false);
        if (packet is null) return XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);

        try
        {
            return XmpRatingReader.ReadPacket(packet.Data, XmpRatingSource.Embedded);
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            return new XmpRatingSnapshot(
                XmpRatingState.Invalid,
                null,
                XmpRatingSource.Embedded,
                $"内嵌 XMP 无法解析：{exception.Message}");
        }
    }

    public Task<XmpRatingWriteReceipt> WriteAsync(
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default)
    {
        if (rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "XMP Rating 必须在 0 到 5 之间");

        return Adapter.WriteRatingAsync(File, rating, safeMode, cancellationToken);
    }

    public Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.TargetFile.StorageId != File.StorageId)
            throw new ArgumentException("回执与当前存储目标不一致", nameof(receipt));

        return Adapter.RestoreAsync(File, receipt, cancellationToken);
    }
}
