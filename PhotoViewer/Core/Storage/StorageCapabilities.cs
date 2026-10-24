namespace PhotoViewer.Core.Storage;

public enum StorageCapability
{
    Unknown,
    Supported,
    Unsupported,
}

public readonly record struct StorageCapabilities
{
    public StorageCapability RandomAccessWrite { get; init; }
    public StorageCapability AtomicReplace { get; init; }
    public StorageCapability CreateChild { get; init; }
    public StorageCapability Delete { get; init; }
    public StorageCapability PersistAccess { get; init; }

    public static StorageCapabilities Local { get; } = new()
    {
        RandomAccessWrite = StorageCapability.Supported,
        AtomicReplace = StorageCapability.Supported,
        CreateChild = StorageCapability.Supported,
        Delete = StorageCapability.Supported,
        PersistAccess = StorageCapability.Unsupported,
    };
}

public readonly record struct StorageRevision(long? Size, DateTimeOffset? LastModified, string? Version = null);
