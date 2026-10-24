namespace PhotoViewer.Core.Storage;

public interface IStorageAccessSession : IDisposable
{
    void Attach(IStorageItemRef item);
}

public interface IStorageAccessSessionFactory
{
    IStorageAccessSession Create(IStorageItemRef root);
}

public sealed class LocalStorageAccessSessionFactory : IStorageAccessSessionFactory
{
    public IStorageAccessSession Create(IStorageItemRef root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.TryGetLocalPath() == null)
            throw new StorageException(StorageError.Unsupported, $"本地存储会话不支持 {root.Name}");
        return new LocalStorageAccessSession();
    }

    private sealed class LocalStorageAccessSession : IStorageAccessSession
    {
        public void Attach(IStorageItemRef item)
        {
            if (item.TryGetLocalPath() == null)
                throw new StorageException(StorageError.Unsupported, "本地存储会话不能附加非本地项");
        }

        public void Dispose() { }
    }
}
