namespace PhotoViewer.Core.Storage;

public enum StorageAccessMode
{
    Read,
    ReadWrite,
}

public interface IStorageRandomAccessSession : IAsyncDisposable
{
    long Length { get; }

    StorageRevision? Revision { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("调整文件长度");

    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalRandomAccessSession : IStorageRandomAccessSession
{
    private readonly FileStream _stream;
    private readonly StorageAccessMode _mode;

    public LocalRandomAccessSession(string path, StorageAccessMode mode)
    {
        _mode = mode;
        var access = mode == StorageAccessMode.Read ? FileAccess.Read : FileAccess.ReadWrite;
        _stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = access,
            Share = FileShare.Read,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
        });
        Revision = new StorageRevision(_stream.Length, File.GetLastWriteTimeUtc(path));
    }

    public long Length => _stream.Length;

    public StorageRevision? Revision { get; }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _stream.Position = offset;
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_mode == StorageAccessMode.Read)
            throw new InvalidOperationException("只读存储会话不能写入");
        _stream.Position = offset;
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        if (_mode == StorageAccessMode.Read)
            throw new InvalidOperationException("只读存储会话不能调整文件长度");
        _stream.SetLength(length);
        await Task.CompletedTask;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_mode == StorageAccessMode.Read) return;
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Flush(true);
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
}
