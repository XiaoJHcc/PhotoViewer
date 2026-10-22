using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 原生壳本地文件引用，使用共享存储契约。
/// 安全作用域由 <see cref="iOSStorageAccessManager"/> 统一保留（打开文件/文件夹时由共享层触发），
/// 因此本类型只按真实路径读写。
/// </summary>
public sealed class iOSFileRef : IFileRef
{
    private readonly string _path;

    /// <summary>创建本地文件引用，统一绝对路径。</summary>
    /// <param name="path">文件路径。</param>
    public iOSFileRef(string path) { _path = System.IO.Path.GetFullPath(path); Path = new Uri(_path); }

    /// <summary>文件显示名。</summary>
    public string Name => System.IO.Path.GetFileName(_path);

    /// <summary>本地文件 URI。</summary>
    public Uri Path { get; }

    /// <summary>主缓存路径键。</summary>
    public string PathKey => _path;

    /// <summary>返回本地路径。</summary>
    public string TryGetLocalPath() => _path;

    /// <summary>打开异步共享读取流，允许评分写回与其他读者并存。</summary>
    public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous));

    /// <summary>取得文件大小及修改时间。</summary>
    public Task<StorageItemBasicProperties> GetBasicPropertiesAsync()
    {
        var info = new FileInfo(_path);
        return Task.FromResult(new StorageItemBasicProperties((ulong)info.Length, info.LastWriteTimeUtc));
    }

    /// <summary>取得父目录；iOS 无父目录权限时枚举失败，由既有单图回退处理。</summary>
    public Task<IFolderRef?> GetParentAsync() => Task.FromResult<IFolderRef?>(System.IO.Path.GetDirectoryName(_path) is { } parent ? new iOSFolderRef(parent) : null);
}

/// <summary>iOS 原生壳目录引用，惰性枚举且不递归。</summary>
public sealed class iOSFolderRef : IFolderRef
{
    private readonly string _path;

    /// <summary>创建目录引用。</summary>
    /// <param name="path">目录路径。</param>
    public iOSFolderRef(string path) { _path = System.IO.Path.GetFullPath(path); Path = new Uri(_path); }

    /// <summary>目录显示名。</summary>
    public string Name => System.IO.Path.GetFileName(_path);

    /// <summary>本地目录 URI。</summary>
    public Uri Path { get; }

    /// <summary>目录路径键。</summary>
    public string PathKey => _path;

    /// <summary>返回本地路径。</summary>
    public string TryGetLocalPath() => _path;

    /// <summary>异步枚举直接子项，每批让出一次，避免大目录阻塞 UI。</summary>
    public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync()
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(_path))
        {
            yield return Directory.Exists(path) ? new iOSFolderRef(path) : new iOSFileRef(path);
            if (++count % 128 == 0) await Task.Yield();
        }
    }
}
