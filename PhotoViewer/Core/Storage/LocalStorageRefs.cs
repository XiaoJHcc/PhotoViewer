using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PhotoViewer.Core.Storage;

/// <summary>
/// 本地文件系统直接实现的 <see cref="IFileRef"/>(System.IO,不经平台存储层)。
/// 适用于沙盒内路径或桌面本机路径(压测驱动器、命令行工具等无选择器场景)。
/// </summary>
public sealed class LocalFileRef : IFileRef
{
    private readonly string _localPath;

    /// <summary>
    /// 以本地绝对路径创建文件引用。
    /// </summary>
    /// <param name="localPath">文件绝对路径。</param>
    public LocalFileRef(string localPath)
    {
        _localPath = System.IO.Path.GetFullPath(localPath);
    }

    /// <inheritdoc/>
    public string Name => System.IO.Path.GetFileName(_localPath);

    /// <inheritdoc/>
    public Uri Path => new(_localPath);

    /// <inheritdoc/>
    public string PathKey => _localPath;

    /// <inheritdoc/>
    public string? TryGetLocalPath() => _localPath;

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(File.OpenRead(_localPath));

    /// <inheritdoc/>
    public Task<StorageItemBasicProperties> GetBasicPropertiesAsync()
    {
        var info = new FileInfo(_localPath);
        return Task.FromResult(new StorageItemBasicProperties(
            info.Exists ? (ulong)info.Length : null,
            info.Exists ? new DateTimeOffset(info.LastWriteTime) : null));
    }

    /// <inheritdoc/>
    public Task<IFolderRef?> GetParentAsync()
    {
        var parent = System.IO.Path.GetDirectoryName(_localPath);
        return Task.FromResult<IFolderRef?>(parent == null ? null : new LocalFolderRef(parent));
    }
}

/// <summary>
/// 本地文件系统直接实现的 <see cref="IFolderRef"/>(System.IO,不经平台存储层)。
/// </summary>
public sealed class LocalFolderRef : IFolderRef
{
    private readonly string _localPath;

    /// <summary>
    /// 以本地绝对路径创建文件夹引用。
    /// </summary>
    /// <param name="localPath">文件夹绝对路径。</param>
    public LocalFolderRef(string localPath)
    {
        _localPath = System.IO.Path.GetFullPath(localPath);
    }

    /// <inheritdoc/>
    public string Name => System.IO.Path.GetFileName(_localPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    /// <inheritdoc/>
    public Uri Path => new(_localPath + System.IO.Path.DirectorySeparatorChar);

    /// <inheritdoc/>
    public string PathKey => _localPath;

    /// <inheritdoc/>
    public string? TryGetLocalPath() => _localPath;

    /// <inheritdoc/>
    public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync()
    {
        await Task.Yield();
        foreach (var entry in Directory.EnumerateFileSystemEntries(_localPath))
        {
            IStorageItemRef item = Directory.Exists(entry)
                ? new LocalFolderRef(entry)
                : new LocalFileRef(entry);
            yield return item;
        }
    }
}
