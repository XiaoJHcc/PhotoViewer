namespace PhotoViewer.Core.Storage;

public enum StorageError
{
    AccessDenied,
    AccessRevoked,
    ReadOnly,
    NotSeekable,
    Unsupported,
    ObjectGone,
    ChangedExternally,
    InvalidName,
    IoError,
}

public sealed class StorageException : IOException
{
    public StorageException(StorageError error, string message, Exception? innerException = null)
        : base(message, innerException) => Error = error;

    public StorageError Error { get; }

    public static StorageException Unsupported(string operation) =>
        new(StorageError.Unsupported, $"当前存储不支持{operation}");

    public static StorageException NotSeekable() =>
        new(StorageError.NotSeekable, "当前存储不支持随机访问写入");

    public static StorageException ChangedExternally(string name) =>
        new(StorageError.ChangedExternally, $"{name} 已被其他程序修改");

    public static StorageException From(Exception exception, string operation)
    {
        if (exception is StorageException storage) return storage;
        return exception switch
        {
            UnauthorizedAccessException => new StorageException(StorageError.AccessDenied, $"无权{operation}", exception),
            FileNotFoundException or DirectoryNotFoundException => new StorageException(StorageError.ObjectGone, $"目标已不存在，无法{operation}", exception),
            NotSupportedException => new StorageException(StorageError.NotSeekable, $"当前存储不支持{operation}", exception),
            _ => new StorageException(StorageError.IoError, $"无法{operation}：{exception.Message}", exception),
        };
    }
}
