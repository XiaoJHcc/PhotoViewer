namespace PhotoViewer.Core.Storage;

public readonly record struct StorageItemId
{
    private StorageItemId(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// 本地路径身份：绝对化后再取文件系统认得的拼写（见 <see cref="LocalStorageIdentity"/>），
    /// macOS 上不区分大小写卷的 <c>a.jpg</c> / <c>A.JPG</c> 因此是同一身份。
    /// 路径不存在或最终项是符号链接时只规范父目录，最终项保留调用方拼写。
    /// </summary>
    /// <param name="path">本地路径。</param>
    /// <returns>本地存储身份。</returns>
    public static StorageItemId FromLocalPath(string path)
        => new(LocalStorageIdentity.CreateValueFromFullPath(Path.GetFullPath(path)));

    /// <summary>使用调用方已在所需访问作用域内解析的绝对路径建立身份。</summary>
    internal static StorageItemId FromResolvedLocalPath(string path)
        => new(LocalStorageIdentity.CreateValueFromResolvedFullPath(Path.GetFullPath(path)));

    public static StorageItemId FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsFile ? FromLocalPath(uri.LocalPath) : new StorageItemId(uri.AbsoluteUri);
    }

    public static StorageItemId FromProvider(string provider, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new StorageItemId(provider + ":" + id);
    }

    public override string ToString() => Value;
}
