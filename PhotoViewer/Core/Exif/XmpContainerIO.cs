using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// 评分写入只做定长原位覆盖：包范围由适配器定位，容器结构本身不被改写
/// （不追加、不重建段/盒/目录，也不做整包重序列化）。
/// </summary>
public static class XmpContainerIO
{
    public static async Task<byte[]> ReadExactlyAsync(
        IStorageRandomAccessSession session,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || offset + length > session.Length)
            throw new EndOfStreamException("XMP 包范围超出文件");
        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await session.ReadAsync(offset + read, data.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("XMP 包提前结束");
            read += count;
        }
        return data;
    }

    public static async Task WritePacketAsync(
        IFileRef file,
        XmpContainerPacket original,
        byte[] updated,
        StorageRevision? expectedRevision,
        bool safeMode,
        CancellationToken cancellationToken)
    {
        if (updated.Length != original.Length)
            throw new ArgumentException("原位 XMP 写入必须保持包长度", nameof(updated));
        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        if (safeMode)
        {
            var current = await ReadExactlyAsync(session, original.Offset, original.Length, cancellationToken)
                .ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original.Data))
                throw StorageException.ChangedExternally(file.Name);
        }
        await session.WriteAsync(original.Offset, updated, cancellationToken).ConfigureAwait(false);
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRevision is { } revision && session.Revision is { } actual &&
            revision.Version != null && actual.Version != revision.Version)
            throw StorageException.ChangedExternally(file.Name);
    }
}
