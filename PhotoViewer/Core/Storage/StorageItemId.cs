namespace PhotoViewer.Core.Storage;

public readonly record struct StorageItemId
{
    private StorageItemId(string value) => Value = value;

    public string Value { get; }

    public static StorageItemId FromLocalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) fullPath = fullPath.ToUpperInvariant();
        return new StorageItemId("file:" + fullPath);
    }

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
