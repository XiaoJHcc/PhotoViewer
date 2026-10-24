using PhotoViewer.Core.Storage;

namespace PhotoViewer.Android.Core;

public sealed class AndroidStorageAccessSessionFactory : IStorageAccessSessionFactory
{
    private readonly AndroidStorageAccessManager _manager;

    public AndroidStorageAccessSessionFactory(AndroidStorageAccessManager manager) =>
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public IStorageAccessSession Create(IStorageItemRef root) => new AndroidStorageAccessSession(_manager, root);

    private sealed class AndroidStorageAccessSession : IStorageAccessSession
    {
        private readonly AndroidStorageAccessManager _manager;

        public AndroidStorageAccessSession(AndroidStorageAccessManager manager, IStorageItemRef root)
        {
            _manager = manager;
            _manager.Retain(root);
        }

        public void Attach(IStorageItemRef item) => _manager.Retain(item);

        public void Dispose() { }
    }
}
