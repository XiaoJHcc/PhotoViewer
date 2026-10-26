using System.Runtime.CompilerServices;

namespace PhotoViewer.Core.Storage;

public sealed class LocalFileRef : IFileRef
{
    private readonly string _localPath;
    private readonly StorageItemId _storageId;

    public LocalFileRef(string localPath)
        : this(localPath, StorageItemId.FromLocalPath(localPath))
    {
    }

    internal LocalFileRef(string localPath, StorageItemId storageId)
    {
        _localPath = System.IO.Path.GetFullPath(localPath);
        _storageId = storageId;
    }

    public string Name => System.IO.Path.GetFileName(_localPath);

    public Uri Path => new(_localPath);

    public StorageItemId StorageId => _storageId;

    public StorageCapabilities Capabilities => StorageCapabilities.Local;

    public string? TryGetLocalPath() => _localPath;

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<Stream>(new FileStream(_localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, $"读取 {Name}");
        }
    }

    public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(_localPath);
        if (!info.Exists)
            return Task.FromResult(new StorageItemBasicProperties(null, null, null, false));
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return Task.FromResult(new StorageItemBasicProperties(
            (ulong)info.Length, modified, new StorageRevision(info.Length, modified)));
    }

    public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = System.IO.Path.GetDirectoryName(_localPath);
        return Task.FromResult<IFolderRef?>(parent == null ? null : new LocalFolderRef(parent));
    }

    public Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
        StorageAccessMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<IStorageRandomAccessSession>(new LocalRandomAccessSession(_localPath, mode));
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, $"打开 {Name}");
        }
    }

    public async Task AtomicWriteAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        StorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        var directory = System.IO.Path.GetDirectoryName(_localPath)
            ?? throw new StorageException(StorageError.ObjectGone, $"{Name} 没有可写的父目录");
        var suffix = Guid.NewGuid().ToString("N");
        var tempPath = System.IO.Path.Combine(directory, $".{Name}.{suffix}.tmp");
        var backupPath = System.IO.Path.Combine(directory, $".{Name}.{suffix}.bak");
        var replaced = false;
        try
        {
            var originalAttributes = File.Exists(_localPath) ? File.GetAttributes(_localPath) : (FileAttributes?)null;
            if (originalAttributes?.HasFlag(FileAttributes.ReadOnly) == true)
                throw new StorageException(StorageError.AccessDenied, $"{Name} 为只读文件");
            var originalUnixMode = !OperatingSystem.IsWindows() && File.Exists(_localPath)
                ? File.GetUnixFileMode(_localPath)
                : (UnixFileMode?)null;
            if (originalUnixMode is { } mode &&
                (mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
                throw new StorageException(StorageError.AccessDenied, $"{Name} 为只读文件");

            AssertRevision(expectedRevision);
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 65536,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AssertRevision(expectedRevision);
            if (!OperatingSystem.IsWindows() && originalUnixMode is { } unixMode)
                File.SetUnixFileMode(tempPath, unixMode);
            if (originalAttributes is { } attributes) File.SetAttributes(tempPath, attributes);
            if (File.Exists(_localPath))
            {
                File.Replace(tempPath, _localPath, backupPath, true);
                replaced = true;
                // 替换已经生效：备份清理属于收尾，删不掉（目录在替换后被收紧权限等）不能把成功写回报成失败。
                try { File.Delete(backupPath); } catch { }
            }
            else
            {
                File.Move(tempPath, _localPath);
                replaced = true;
            }
        }
        catch (Exception exception)
        {
            if (!replaced && !File.Exists(_localPath) && File.Exists(backupPath))
            {
                try { File.Move(backupPath, _localPath); } catch { }
            }
            if (exception is OperationCanceledException) throw;
            throw StorageException.From(exception, $"写入 {Name}");
        }
        finally
        {
            if (!replaced && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(_localPath);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, $"删除 {Name}");
        }
    }

    internal string LocalPath => _localPath;

    private void AssertRevision(StorageRevision? expected)
    {
        if (expected is not { } revision) return;
        var info = new FileInfo(_localPath);
        if (!info.Exists) throw new StorageException(StorageError.ObjectGone, $"{Name} 已不存在");
        if (revision.Size is { } size && info.Length != size)
            throw StorageException.ChangedExternally(Name);
        if (revision.LastModified is { } modified &&
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != modified)
            throw StorageException.ChangedExternally(Name);
    }
}

public sealed class LocalFolderRef : IFolderRef
{
    private readonly string _localPath;
    private readonly StorageItemId _storageId;

    public LocalFolderRef(string localPath)
        : this(localPath, StorageItemId.FromLocalPath(localPath))
    {
    }

    internal LocalFolderRef(string localPath, StorageItemId storageId)
    {
        _localPath = System.IO.Path.GetFullPath(localPath);
        _storageId = storageId;
    }

    public string Name => new DirectoryInfo(_localPath).Name;

    public Uri Path => new(_localPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar);

    public StorageItemId StorageId => _storageId;

    public StorageCapabilities Capabilities => StorageCapabilities.Local;

    public string? TryGetLocalPath() => _localPath;

    public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var entry in Directory.EnumerateFileSystemEntries(_localPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IStorageItemRef item = Directory.Exists(entry)
                ? new LocalFolderRef(entry)
                : new LocalFileRef(entry);
            yield return item;
        }
    }

    public Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetChildPath(name);
        return Task.FromResult<IFileRef?>(File.Exists(path) ? new LocalFileRef(path) : null);
    }

    public Task<IFileRef> CreateFileAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetChildPath(name);
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)) { }
            return Task.FromResult<IFileRef>(new LocalFileRef(path));
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, $"新建 {name}");
        }
    }

    internal string LocalPath => _localPath;

    private string GetChildPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != System.IO.Path.GetFileName(name) ||
            name is "." or "..")
            throw new StorageException(StorageError.InvalidName, $"无效文件名：{name}");
        return System.IO.Path.Combine(_localPath, name);
    }
}
