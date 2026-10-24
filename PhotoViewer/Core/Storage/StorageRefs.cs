using System.Runtime.CompilerServices;

namespace PhotoViewer.Core.Storage;

public interface IStorageItemRef
{
    string Name { get; }

    Uri Path { get; }

    StorageItemId StorageId => StorageItemId.FromUri(Path);

    string? TryGetLocalPath();

    StorageCapabilities Capabilities => default;
}

public interface IFileRef : IStorageItemRef
{
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default);

    Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default);

    Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default);

    Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
        StorageAccessMode mode,
        CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("随机访问");

    Task AtomicWriteAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        StorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("原子替换");

    Task DeleteAsync(CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("删除");
}

public interface IFolderRef : IStorageItemRef
{
    IAsyncEnumerable<IStorageItemRef> GetItemsAsync(CancellationToken cancellationToken = default);

    Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("查找子文件");

    Task<IFileRef> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        => throw StorageException.Unsupported("新建子文件");
}

public sealed class StorageItemBasicProperties
{
    public StorageItemBasicProperties(
        ulong? size,
        DateTimeOffset? dateModified,
        StorageRevision? revision = null,
        bool exists = true)
    {
        Size = size;
        DateModified = dateModified;
        Revision = revision ?? new StorageRevision(
            size.HasValue ? (long?)checked((long)size.Value) : null,
            dateModified);
        Exists = exists;
    }

    public ulong? Size { get; }

    public DateTimeOffset? DateModified { get; }

    public StorageRevision? Revision { get; }

    public bool Exists { get; }
}
