using Foundation;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

public sealed class iOSStorageAccessSessionFactory : IStorageAccessSessionFactory
{
    private static readonly LocalStorageAccessSessionFactory Local = new();

    public IStorageAccessSession Create(IStorageItemRef root)
    {
        var path = RequireLocalPath(root);
        if (!RequiresSecurityScope(path)) return Local.Create(root);
        return new ScopedSession(new iOSStorageAccessSession(NSUrl.CreateFileUrl(path, null)));
    }

    private static bool RequiresSecurityScope(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsWithin(path, home)) return false;
        var bundle = NSBundle.MainBundle.BundlePath;
        return !IsWithin(path, bundle);
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(fullRoot, StringComparison.Ordinal);
    }

    private static string RequireLocalPath(IStorageItemRef item)
    {
        var path = item.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            throw new StorageException(StorageError.Unsupported, $"{item.Name} 不是 iOS 本地文件 URL");
        return Path.GetFullPath(path);
    }

    private sealed class ScopedSession(iOSStorageAccessSession session) : IStorageAccessSession
    {
        public void Attach(IStorageItemRef item) => session.Acquire(NSUrl.CreateFileUrl(RequireLocalPath(item), null));

        public void Dispose() => session.Dispose();
    }
}
