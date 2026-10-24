using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

public sealed record XmpContainerEdit(long Offset, long RemoveLength, byte[] InsertBytes);

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

    public static Task RewriteAsync(
        IFileRef file,
        IReadOnlyList<XmpContainerEdit> edits,
        StorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        var ordered = edits.OrderBy(edit => edit.Offset).ToArray();
        ValidateEdits(ordered);
        return file.AtomicWriteAsync(async (output, token) =>
        {
            await using var input = await file.OpenReadAsync(token).ConfigureAwait(false);
            long position = 0;
            foreach (var edit in ordered)
            {
                await CopyExactlyAsync(input, output, edit.Offset - position, token).ConfigureAwait(false);
                if (edit.InsertBytes.Length > 0)
                    await output.WriteAsync(edit.InsertBytes, token).ConfigureAwait(false);
                await SkipExactlyAsync(input, edit.RemoveLength, token).ConfigureAwait(false);
                position = edit.Offset + edit.RemoveLength;
            }
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }, expectedRevision, cancellationToken);
    }

    private static void ValidateEdits(IReadOnlyList<XmpContainerEdit> edits)
    {
        long previousEnd = 0;
        foreach (var edit in edits)
        {
            if (edit.Offset < previousEnd || edit.RemoveLength < 0)
                throw new ArgumentException("XMP 容器编辑范围重叠或无效", nameof(edits));
            previousEnd = edit.Offset + edit.RemoveLength;
        }
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long count,
        CancellationToken cancellationToken)
    {
        if (count < 0) throw new EndOfStreamException("XMP 容器范围无效");
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("XMP 容器提前结束");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task SkipExactlyAsync(Stream input, long count, CancellationToken cancellationToken)
    {
        if (count < 0) throw new EndOfStreamException("XMP 容器范围无效");
        if (input.CanSeek)
        {
            input.Seek(count, SeekOrigin.Current);
            return;
        }
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("XMP 容器提前结束");
            remaining -= read;
        }
    }
}
