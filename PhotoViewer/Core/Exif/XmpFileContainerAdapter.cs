using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// 独立的 <c>.xmp</c> 文件（已存在的同名 sidecar 或用户直接打开的文件）作为评分来源。
/// 整个文件就是一个 XMP 包，偏移恒为 0；文件不存在或为空返回 null，绝不创建。
/// </summary>
public sealed class XmpFileContainerAdapter : IXmpContainerAdapter
{
    private const int MaxPacketBytes = 64 << 20;

    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var properties = await file.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
        if (!properties.Exists || properties.Size is null or 0) return null;
        if (properties.Size > MaxPacketBytes)
            throw new StorageException(
                StorageError.Unsupported,
                $"{file.Name} 的 XMP 包超过 {MaxPacketBytes / (1024 * 1024)}MB 上限");

        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream((int)properties.Size.Value);
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var data = buffer.ToArray();
        return data.Length == 0 ? null : new XmpContainerPacket(0, data.Length, data);
    }

    /// <summary>兼容旧的适配器入口：把整文件当作包，经评分存储做等长原位写入。</summary>
    public Task<XmpRatingWriteReceipt> WriteRatingAsync(
        IFileRef file,
        int rating,
        bool safeMode,
        CancellationToken cancellationToken = default) =>
        new ContainerRatingStore(file, this, XmpRatingSource.Sidecar)
            .WriteAsync(rating, safeMode, cancellationToken);

    /// <summary>兼容旧的适配器入口：按回执恢复整文件内容。</summary>
    public Task RestoreAsync(
        IFileRef file,
        XmpRatingWriteReceipt receipt,
        CancellationToken cancellationToken = default) =>
        new ContainerRatingStore(file, this, XmpRatingSource.Sidecar)
            .RestoreAsync(receipt, cancellationToken);
}
