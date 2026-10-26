using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Database;
using Android.Provider;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// SAF 文档投影（<c>DocumentsContract.Document</c> 一行）：显示名、MIME、大小、修改时间与 flags。
/// 大小/时间缺失或非法时保持 null，绝不把 -1 之类的哨兵值转成 <see cref="ulong"/> 巨值。
/// </summary>
internal readonly record struct AndroidDocumentMetadata(
    string? DisplayName,
    string? MimeType,
    ulong? Size,
    DateTimeOffset? Modified,
    int? Flags);

/// <summary>
/// Android 存储引用共用的 SAF 助手：文档身份、投影读取、异常归类与能力映射。
/// </summary>
internal static class AndroidStorageSupport
{
    private const string DocumentProvider = "android-document";

    internal static readonly string[] ProjectionColumns =
    [
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
        DocumentsContract.Document.ColumnFlags,
    ];

    internal static bool IsContentUri(Uri path) =>
        string.Equals(path.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 文档身份：本地路径 → <c>file:</c>；DocumentsProvider 文档 → <c>android-document:authority:documentId</c>；
    /// 其它 content URI → 原始 Uri。显示名永远不参与身份。
    /// </summary>
    internal static StorageItemId CreateStorageId(Uri path, AndroidUri? documentUri, string? documentId, string? localPath)
    {
        if (localPath != null) return StorageItemId.FromLocalPath(localPath);
        string? authority = documentUri?.Authority;
        if (documentId != null && !string.IsNullOrEmpty(authority) && IsContentUri(path))
            return StorageItemId.FromProvider(DocumentProvider, authority + ":" + documentId);
        return StorageItemId.FromUri(path);
    }

    /// <summary>文档 ID；非 DocumentsProvider 的 content URI 返回 null（此类 Uri 不应被当作文档寻址）。</summary>
    internal static string? GetDocumentId(AndroidUri? uri)
    {
        if (uri == null) return null;
        try
        {
            if (!IsDocumentUri(uri)) return null;
            string? id = DocumentsContract.GetDocumentId(uri);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>树 Uri 的根文档 ID；不是树 Uri 时返回 null。</summary>
    internal static string? GetTreeDocumentId(AndroidUri? uri, bool requireTree = true)
    {
        if (uri == null) return null;
        try
        {
            if (!DocumentsContract.IsTreeUri(uri))
                return requireTree ? null : GetDocumentId(uri);
            string? id = DocumentsContract.GetTreeDocumentId(uri);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool IsDocumentUri(AndroidUri uri)
    {
        try
        {
            var context = Application.Context;
            return context != null && DocumentsContract.IsDocumentUri(context, uri);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>从文档 ID 末段推导显示名（避免为取名字额外查询 Provider）。</summary>
    internal static string? NameFromDocumentId(string? documentId)
    {
        if (string.IsNullOrEmpty(documentId)) return null;
        int slash = documentId.LastIndexOf('/');
        string tail = slash >= 0 ? documentId[(slash + 1)..] : documentId;
        int colon = tail.LastIndexOf(':');
        if (colon >= 0) tail = tail[(colon + 1)..];
        return tail.Length == 0 ? null : tail;
    }

    /// <summary>父文档 ID；<c>primary:</c> 这类卷根没有可寻址的父目录，返回 null。</summary>
    internal static string? GetParentDocumentId(string documentId)
    {
        int slash = documentId.LastIndexOf('/');
        if (slash < 0) return null;
        string parent = documentId[..slash];
        return parent.EndsWith(':') || parent.Length == 0 ? null : parent;
    }

    /// <summary>文档 ID 是否位于树根之下（含根自身）。</summary>
    internal static bool IsWithinTree(string documentId, string rootId)
    {
        if (string.Equals(documentId, rootId, StringComparison.Ordinal)) return true;
        string prefix = rootId.EndsWith(':') || rootId.EndsWith('/') ? rootId : rootId + "/";
        return documentId.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>枚举/查找/新建用：读取一行的完整投影；Provider 失败时抛 typed 异常。</summary>
    internal static AndroidDocumentMetadata QueryDocument(ContentResolver resolver, AndroidUri uri, string name)
    {
        try
        {
            using var cursor = resolver.Query(uri, ProjectionColumns, null, null, null)
                ?? throw new StorageException(StorageError.ObjectGone, $"{name} 无法读取属性");
            if (!cursor.MoveToFirst())
                throw new StorageException(StorageError.ObjectGone, $"{name} 已不存在");
            return ReadMetadata(cursor);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Wrap(exception, $"读取 {name} 属性");
        }
    }

    /// <summary>
    /// 非 DocumentsProvider 的 content URI（例如 MediaStore 图片）用通用列取显示名/大小/时间；
    /// Provider 不支持这些列时返回空投影，不把失败当成 SAF 属性错误。
    /// </summary>
    internal static AndroidDocumentMetadata QueryGenericContent(ContentResolver resolver, AndroidUri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, ["_display_name", "_size", "date_modified"], null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return default;
            return new AndroidDocumentMetadata(
                ReadString(cursor, "_display_name"),
                null,
                ReadSize(cursor, "_size"),
                ReadModified(cursor, "date_modified"),
                null);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// 读取一个 <c>content://</c> URI 的属性：DocumentsProvider 文档走 SAF 投影，
    /// 其它（MediaStore 等只有数字 ID 的通用 provider）走通用列。
    /// </summary>
    internal static AndroidDocumentMetadata QueryContentMetadata(ContentResolver resolver, AndroidUri uri, string name)
        => IsDocumentUri(uri)
            ? QueryDocument(resolver, uri, name)
            : QueryGenericContent(resolver, uri);

    internal static AndroidDocumentMetadata ReadMetadata(ICursor cursor) => new(
        ReadString(cursor, DocumentsContract.Document.ColumnDisplayName),
        ReadString(cursor, DocumentsContract.Document.ColumnMimeType),
        ReadSize(cursor, DocumentsContract.Document.ColumnSize),
        ReadModified(cursor, DocumentsContract.Document.ColumnLastModified),
        ReadFlags(cursor, DocumentsContract.Document.ColumnFlags));

    internal static string? ReadString(ICursor cursor, string column)
    {
        int index = cursor.GetColumnIndex(column);
        return index < 0 || cursor.IsNull(index) ? null : cursor.GetString(index);
    }

    internal static ulong? ReadSize(ICursor cursor, string column)
    {
        int index = cursor.GetColumnIndex(column);
        if (index < 0 || cursor.IsNull(index)) return null;
        long value = cursor.GetLong(index);
        return value < 0 ? null : (ulong)value;
    }

    internal static DateTimeOffset? ReadModified(ICursor cursor, string column)
    {
        int index = cursor.GetColumnIndex(column);
        if (index < 0 || cursor.IsNull(index)) return null;
        long value = cursor.GetLong(index);
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    internal static int? ReadFlags(ICursor cursor, string column)
    {
        int index = cursor.GetColumnIndex(column);
        return index < 0 || cursor.IsNull(index) ? null : cursor.GetInt(index);
    }

    /// <summary>子文件名校验：拒绝空名、路径分隔符与 <c>.</c>/<c>..</c>，不做任何静默改写。</summary>
    internal static void ValidateChildName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || name != name.Trim())
            throw new StorageException(StorageError.InvalidName, $"无效文件名：{name}");
    }

    /// <summary>新建子文件的 MIME；XMP 侧车文件按 XML，其余按扩展名，未知回落 octet-stream。</summary>
    internal static string MimeTypeForName(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".xmp" or ".xml" => "application/xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".heic" or ".heif" => "image/heic",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".dng" => "image/x-adobe-dng",
            ".arw" => "image/x-sony-arw",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// Java 侧异常归类为共享 <see cref="StorageException"/>；不把同步 IO 失败吞成 bool。
    /// </summary>
    internal static StorageException Wrap(Exception exception, string operation)
    {
        if (exception is StorageException storage) return storage;

        string message = exception.Message ?? string.Empty;
        if (exception is Java.Nio.Channels.NonWritableChannelException)
            return new StorageException(StorageError.ReadOnly, $"{operation}失败：通道只读", exception);
        if (exception is Java.Nio.Channels.NonReadableChannelException || exception is Java.Lang.SecurityException)
            return new StorageException(StorageError.AccessDenied, $"无权{operation}", exception);
        if (message.Contains("EROFS", StringComparison.Ordinal)
            || message.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase))
            return new StorageException(StorageError.ReadOnly, $"{operation}失败：目标只读", exception);
        if (message.Contains("EACCES", StringComparison.Ordinal)
            || message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return new StorageException(StorageError.AccessDenied, $"无权{operation}", exception);
        if (message.Contains("ESPIPE", StringComparison.Ordinal)
            || message.Contains("Illegal seek", StringComparison.OrdinalIgnoreCase))
            return new StorageException(StorageError.NotSeekable, $"{operation}失败：目标不可定位", exception);
        if (exception is Java.IO.FileNotFoundException or FileNotFoundException or DirectoryNotFoundException)
            return new StorageException(StorageError.ObjectGone, $"目标已不存在，无法{operation}", exception);
        if (exception is UnauthorizedAccessException)
            return new StorageException(StorageError.AccessDenied, $"无权{operation}", exception);
        if (exception is NotSupportedException)
            return new StorageException(StorageError.NotSeekable, $"当前存储不支持{operation}", exception);
        return new StorageException(StorageError.IoError, $"无法{operation}：{message}", exception);
    }

    internal static StorageCapability FromFlags(int? flags, DocumentContractFlags mask) => flags is not { } value
        ? StorageCapability.Unknown
        : (value & (int)mask) != 0 ? StorageCapability.Supported : StorageCapability.Unsupported;
}

/// <summary>
/// Android 文件引用：SAF <c>content://</c> 文档与本地文件路径都实现共享 <see cref="IFileRef"/>。
/// SAF 引用保留树 Uri 与自身文档 ID，读取/写入/属性都经 <see cref="ContentResolver"/>；
/// 本地路径用于验收夹具与 <c>file://</c> 外部打开。
/// 目录枚举投影只是打开时的快照：属性读取与随机会话每次都重新查询，
/// 能力（Document flags）由后台投影补齐，名称/能力 getter 不做同步 binder 调用。
/// </summary>
public sealed class AndroidFileRef : IFileRef
{
    private readonly ContentResolver _resolver;
    private readonly AndroidUri _uri;
    private readonly AndroidUri? _treeUri;
    private readonly string? _localPath;
    private readonly string? _documentId;
    private readonly StorageItemId _storageId;
    private readonly object _metadataGate = new();

    private AndroidDocumentMetadata _metadata;
    private int _propertiesLoaded;
    private int _backgroundRefreshStarted;

    /// <summary>从 SAF 文档构造引用。</summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="documentUri">文档 Uri。</param>
    /// <param name="treeUri">所属树 Uri（父目录/枚举用，可空）。</param>
    /// <param name="displayName">显示名（可空，缺省从文档 ID 末段取）。</param>
    /// <param name="size">字节数。</param>
    /// <param name="modified">修改时间。</param>
    /// <param name="flags"><c>DocumentsContract.Document</c> flags（缺失时保持未知）。</param>
    public AndroidFileRef(ContentResolver resolver, AndroidUri documentUri, AndroidUri? treeUri = null,
        string? displayName = null, ulong? size = null, DateTimeOffset? modified = null, int? flags = null)
        : this(resolver, documentUri, treeUri,
            new AndroidDocumentMetadata(displayName, null, size, modified, flags),
            size.HasValue && modified.HasValue)
    {
    }

    /// <summary>枚举投影已完整读取时使用：构造时不再重复查询，但仍会在属性读取时重新核对。</summary>
    internal AndroidFileRef(ContentResolver resolver, AndroidUri documentUri, AndroidUri? treeUri,
        AndroidDocumentMetadata metadata, bool propertiesLoaded)
    {
        _resolver = resolver;
        _uri = documentUri;
        _treeUri = treeUri;
        _metadata = metadata;
        _propertiesLoaded = propertiesLoaded ? 1 : 0;
        Path = new Uri(documentUri.ToString()!);
        _documentId = AndroidStorageSupport.GetDocumentId(documentUri);
        _storageId = AndroidStorageSupport.CreateStorageId(Path, documentUri, _documentId, null);
    }

    /// <summary>从本地文件构造引用（验收夹具 / file:// 外部打开）。</summary>
    /// <param name="path">本地绝对路径。</param>
    public AndroidFileRef(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        _localPath = full;
        _resolver = Application.Context.ContentResolver!;
        _uri = AndroidUri.FromFile(new Java.IO.File(full))!;
        Path = new Uri(full);
        _metadata = new AndroidDocumentMetadata(System.IO.Path.GetFileName(full), null, null, null, null);
        _storageId = StorageItemId.FromLocalPath(full);
    }

    /// <summary>
    /// 文件显示名。只读缓存：首次访问时在后台补查真实投影，getter 本身不做 binder 调用。
    /// </summary>
    public string Name
    {
        get
        {
            EnsureBackgroundProjection();
            return CachedName;
        }
    }

    /// <summary>平台路径 URI。</summary>
    public Uri Path { get; }

    /// <summary>稳定身份；SAF 文档按 authority + documentId，本地文件按完整路径。</summary>
    public StorageItemId StorageId => _storageId;

    /// <summary>
    /// 能力三态：本地按文件系统，SAF 按 Provider 的 Document flags；AtomicReplace 恒为不支持
    /// （SAF 没有不删除原目标的原子替换）。flags 缺失时保持 Unknown，不武断判为不支持。
    /// flags 只能由后台投影补齐，getter 不做同步 binder 查询。
    /// </summary>
    public StorageCapabilities Capabilities
    {
        get
        {
            if (_localPath != null) return StorageCapabilities.Local;
            EnsureBackgroundProjection();
            var flags = Metadata.Flags;
            if (_documentId == null)
            {
                return new StorageCapabilities
                {
                    AtomicReplace = StorageCapability.Unsupported,
                    PersistAccess = StorageCapability.Unknown,
                };
            }

            return new StorageCapabilities
            {
                RandomAccessWrite = AndroidStorageSupport.FromFlags(flags, DocumentContractFlags.SupportsWrite),
                AtomicReplace = StorageCapability.Unsupported,
                CreateChild = StorageCapability.Unsupported,
                Delete = AndroidStorageSupport.FromFlags(flags, DocumentContractFlags.SupportsDelete),
                PersistAccess = StorageCapability.Supported,
            };
        }
    }

    /// <summary>本地路径；SAF 文档返回 null。</summary>
    /// <returns>本地路径或 null。</returns>
    public string? TryGetLocalPath() => _localPath;

    /// <summary>打开只读流。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>共享读取流。</returns>
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local)
        {
            try
            {
                return Task.FromResult<Stream>(new FileStream(local, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }
            catch (Exception exception)
            {
                throw AndroidStorageSupport.Wrap(exception, $"读取 {Name}");
            }
        }

        return Task.Run<Stream>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = _resolver.OpenInputStream(_uri)
                    ?? throw new StorageException(StorageError.ObjectGone, $"{Name} 无法打开");
                cancellationToken.ThrowIfCancellationRequested();
                // Provider 的流可能来自管道（不可定位）：能读 Position 的保持原流（MetadataExtractor 要求可 Seek），
                // 探测到 ESPIPE 的包一层明确不可 Seek 的流，避免 CopyToAsync 消费者反复探测 Position。
                try
                {
                    _ = stream.Position;
                    return stream;
                }
                catch (Exception)
                {
                    return new NonSeekableReadStream(stream);
                }
            }
            catch (Exception exception) when (exception is not StorageException and not OperationCanceledException)
            {
                throw AndroidStorageSupport.Wrap(exception, $"读取 {Name}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 读取文件大小与修改时间。每次调用都重新查询 Provider：目录枚举/打开时的投影只是快照，
    /// 外部程序改过文件后不能继续返回旧 size/mtime。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>基本属性。</returns>
    public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local) return Task.FromResult(ReadLocalProperties(local));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshProjection(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = Metadata;
            return new StorageItemBasicProperties(metadata.Size, metadata.Modified);
        }, cancellationToken);
    }

    /// <summary>取得父文件夹；SAF 文档优先返回实际父目录，超出树根时回退到树根。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>父文件夹或 null。</returns>
    public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local)
        {
            string? parent = System.IO.Path.GetDirectoryName(local);
            return Task.FromResult<IFolderRef?>(
                parent == null ? null : new AndroidFolderRef(parent));
        }
        return Task.FromResult(GetDocumentParent());
    }

    /// <summary>
    /// 打开随机访问会话；本地文件用共享本地实现，SAF 走文件描述符。
    /// 会话尺寸/时间取自打开瞬间的新鲜属性与描述符真实长度，不沿用目录投影。
    /// </summary>
    /// <param name="mode">只读或读写。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>随机访问会话。</returns>
    public async Task<IStorageRandomAccessSession> OpenRandomAccessAsync(
        StorageAccessMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local)
        {
            try
            {
                return new LocalRandomAccessSession(local, mode);
            }
            catch (Exception exception)
            {
                throw AndroidStorageSupport.Wrap(exception, $"打开 {Name}");
            }
        }

        var metadata = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshProjection(cancellationToken);
            return Metadata;
        }, cancellationToken).ConfigureAwait(false);

        return await AndroidRandomAccessSession.OpenAsync(
            _resolver, _uri, mode, metadata.Size, metadata.Modified, CachedName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 原子替换：本地文件复用 <see cref="LocalFileRef"/> 的临时文件 + rename；
    /// SAF 没有不删除原目标的原子替换，明确返回不支持（绝不以 Delete→Create 冒充）。
    /// </summary>
    /// <param name="writeAsync">写入回调。</param>
    /// <param name="expectedRevision">期望的旧版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入任务。</returns>
    public Task AtomicWriteAsync(
        Func<Stream, CancellationToken, Task> writeAsync,
        StorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local)
            return new LocalFileRef(local).AtomicWriteAsync(writeAsync, expectedRevision, cancellationToken);
        throw StorageException.Unsupported("原子替换");
    }

    /// <summary>删除文件：本地直接删除，SAF 用 <see cref="DocumentsContract.DeleteDocument"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除任务。</returns>
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local)
        {
            try
            {
                File.Delete(local);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                throw AndroidStorageSupport.Wrap(exception, $"删除 {Name}");
            }
        }

        if (_documentId == null) throw StorageException.Unsupported("删除");
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool deleted;
            try
            {
                deleted = DocumentsContract.DeleteDocument(_resolver, _uri);
            }
            catch (Exception exception)
            {
                throw AndroidStorageSupport.Wrap(exception, $"删除 {Name}");
            }
            if (!deleted)
                throw new StorageException(StorageError.IoError, $"无法删除 {Name}");
        }, cancellationToken);
    }

    /// <summary>关联的文档 Uri（诊断用）。</summary>
    internal AndroidUri DocumentUri => _uri;

    /// <summary>所属树 Uri。</summary>
    internal AndroidUri? TreeUri => _treeUri;

    private static StorageItemBasicProperties ReadLocalProperties(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new StorageItemBasicProperties(null, null, null, false);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return new StorageItemBasicProperties((ulong)info.Length, modified);
    }

    /// <summary>缓存中的显示名（含文档 ID / Uri 末段兜底），不触发任何查询。</summary>
    private string CachedName => Metadata.DisplayName
        ?? AndroidStorageSupport.NameFromDocumentId(_documentId)
        ?? _uri.LastPathSegment
        ?? "image";

    private AndroidDocumentMetadata Metadata
    {
        get
        {
            lock (_metadataGate) return _metadata;
        }
    }

    /// <summary>
    /// 外部打开用工厂：在后台线程读取真实投影（显示名/大小/时间/flags）。
    /// 只有数字 documentId 的 <c>content://</c>（MediaStore 等）Uri 末段不是文件名，
    /// 不先查通用列就无法按扩展名判定类型，打开链会把目标当成不支持的文件丢弃。
    /// </summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="documentUri">外部传入的 <c>content://</c> Uri。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>带真实显示名的文件引用。</returns>
    internal static Task<AndroidFileRef> CreateForContentUriAsync(
        ContentResolver resolver,
        AndroidUri documentUri,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = AndroidStorageSupport.NameFromDocumentId(AndroidStorageSupport.GetDocumentId(documentUri))
                ?? documentUri.LastPathSegment
                ?? "image";
            var metadata = AndroidStorageSupport.QueryContentMetadata(resolver, documentUri, name);
            // 通用 provider 不支持这些列时投影为空：保持未加载，后续访问会在后台再试。
            return new AndroidFileRef(resolver, documentUri, null, metadata, metadata.DisplayName != null);
        }, cancellationToken);
    }

    /// <summary>
    /// 重新查询 Provider 投影并更新缓存。属性/能力/随机会话共用这一条新鲜度路径；
    /// 调用方必须已经在后台线程上，binder 查询不得落在 UI 线程。
    /// </summary>
    private void RefreshProjection(CancellationToken cancellationToken)
    {
        var cached = Metadata;
        cancellationToken.ThrowIfCancellationRequested();
        if (_documentId == null)
        {
            // 通用 content URI 用通用列；空投影不覆盖已有显示名/大小/时间。
            var generic = AndroidStorageSupport.QueryGenericContent(_resolver, _uri);
            lock (_metadataGate)
            {
                _metadata = generic with
                {
                    DisplayName = generic.DisplayName ?? cached.DisplayName,
                    Size = generic.Size ?? cached.Size,
                    Modified = generic.Modified ?? cached.Modified,
                };
                if (_metadata.DisplayName != null) Volatile.Write(ref _propertiesLoaded, 1);
            }
            return;
        }

        var metadata = AndroidStorageSupport.QueryDocument(_resolver, _uri, cached.DisplayName
            ?? AndroidStorageSupport.NameFromDocumentId(_documentId)
            ?? _uri.LastPathSegment
            ?? "image");
        lock (_metadataGate)
        {
            _metadata = metadata;
            Volatile.Write(ref _propertiesLoaded, 1);
        }
    }

    /// <summary>
    /// 后台补齐属性与能力：枚举结果已带完整投影，其余（选择器/外部打开）在首次访问
    /// <see cref="Name"/> 或 <see cref="Capabilities"/> 时启动一次后台查询。
    /// getter 本身不做 binder 调用，UI 线程不会被 Provider 拖住。
    /// </summary>
    private void EnsureBackgroundProjection()
    {
        if (_localPath != null) return;
        if (Volatile.Read(ref _propertiesLoaded) == 1) return;
        if (Interlocked.Exchange(ref _backgroundRefreshStarted, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                RefreshProjection(CancellationToken.None);
            }
            catch (Exception)
            {
                // 查询失败时能力保持 Unknown；真正的属性读取会重新查询并向调用方抛 typed 异常。
            }
        });
    }

    private IFolderRef? GetDocumentParent()
    {
        if (_treeUri == null || _documentId == null) return null;
        try
        {
            string? rootId = AndroidStorageSupport.GetTreeDocumentId(_treeUri, requireTree: false);
            if (rootId == null) return null;
            var root = CreateFolder(_documentId == rootId ? _documentId : rootId);
            if (root == null) return null;
            if (string.Equals(_documentId, rootId, StringComparison.Ordinal)) return root;

            string? parentId = AndroidStorageSupport.GetParentDocumentId(_documentId);
            if (parentId == null || !AndroidStorageSupport.IsWithinTree(parentId, rootId)) return root;
            return CreateFolder(parentId) ?? root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AndroidFolderRef? CreateFolder(string documentId)
    {
        if (_treeUri == null) return null;
        AndroidUri? uri;
        try
        {
            uri = DocumentsContract.IsTreeUri(_treeUri)
                ? DocumentsContract.BuildDocumentUriUsingTree(_treeUri, documentId)
                : DocumentsContract.BuildDocumentUri(_treeUri.Authority!, documentId);
        }
        catch (Exception)
        {
            return null;
        }
        return uri == null
            ? null
            : new AndroidFolderRef(_resolver, _treeUri, uri, documentId, null, default, false);
    }
}

/// <summary>
/// Android SAF 目录引用：保留根树 Uri 与自身文档 ID，用 <see cref="DocumentsContract"/> 枚举直接子项，不递归。
/// 嵌套目录以 <c>BuildChildDocumentsUriUsingTree(根树, 自身文档 ID)</c> 枚举，绝不把子目录当成树根。
/// </summary>
public sealed class AndroidFolderRef : IFolderRef
{
    private readonly ContentResolver _resolver;
    private readonly AndroidUri _uri;
    private readonly AndroidUri? _treeUri;
    private readonly string? _localPath;
    private readonly string? _documentId;
    private readonly StorageItemId _storageId;
    private readonly object _metadataGate = new();

    private AndroidDocumentMetadata _metadata;
    private int _propertiesLoaded;
    private int _backgroundRefreshStarted;

    /// <summary>从 SAF 树构造目录引用（选择器返回的根树）。</summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="treeUri">树 Uri。</param>
    /// <param name="name">显示名（可空）。</param>
    public AndroidFolderRef(ContentResolver resolver, AndroidUri treeUri, string? name)
    {
        _resolver = resolver;
        _uri = treeUri;
        _treeUri = DocumentsContract.IsTreeUri(treeUri) ? treeUri : null;
        _documentId = _treeUri != null
            ? AndroidStorageSupport.GetTreeDocumentId(_treeUri)
            : AndroidStorageSupport.GetDocumentId(treeUri);
        _metadata = new AndroidDocumentMetadata(name, null, null, null, null);
        Path = new Uri(treeUri.ToString()!);
        _storageId = AndroidStorageSupport.CreateStorageId(Path, treeUri, _documentId, null);
    }

    /// <summary>从枚举结果构造子目录引用：保留根树 Uri 与自身文档 ID。</summary>
    internal AndroidFolderRef(ContentResolver resolver, AndroidUri? treeUri, AndroidUri documentUri,
        string? documentId, string? displayName, AndroidDocumentMetadata metadata, bool propertiesLoaded)
    {
        _resolver = resolver;
        _uri = documentUri;
        _treeUri = treeUri;
        _documentId = documentId;
        _metadata = metadata with { DisplayName = displayName ?? metadata.DisplayName };
        _propertiesLoaded = propertiesLoaded ? 1 : 0;
        Path = new Uri(documentUri.ToString()!);
        _storageId = AndroidStorageSupport.CreateStorageId(Path, documentUri, documentId, null);
    }

    /// <summary>从本地目录构造引用。</summary>
    /// <param name="path">本地绝对路径。</param>
    public AndroidFolderRef(string path)
    {
        _localPath = System.IO.Path.GetFullPath(path);
        _resolver = Application.Context.ContentResolver!;
        _uri = AndroidUri.FromFile(new Java.IO.File(_localPath))!;
        _metadata = new AndroidDocumentMetadata(System.IO.Path.GetFileName(_localPath), null, null, null, null);
        Path = new Uri(_localPath);
        _storageId = StorageItemId.FromLocalPath(_localPath);
    }

    /// <summary>
    /// 目录显示名。只读缓存：选择器根树等未带投影的引用在首次访问时后台补查，
    /// getter 本身不做 binder 调用。
    /// </summary>
    public string Name
    {
        get
        {
            EnsureBackgroundProjection();
            return CachedName;
        }
    }

    /// <summary>目录路径 URI。</summary>
    public Uri Path { get; }

    /// <summary>稳定身份；SAF 目录按 authority + 目录文档 ID。</summary>
    public StorageItemId StorageId => _storageId;

    /// <summary>
    /// 能力三态：本地按文件系统，SAF 按 Document flags；AtomicReplace 恒为不支持。
    /// flags 只能由后台投影补齐，getter 不做同步 binder 查询。
    /// </summary>
    public StorageCapabilities Capabilities
    {
        get
        {
            if (_localPath != null) return StorageCapabilities.Local;
            EnsureBackgroundProjection();
            var flags = Metadata.Flags;
            if (_documentId == null)
            {
                return new StorageCapabilities
                {
                    AtomicReplace = StorageCapability.Unsupported,
                    PersistAccess = StorageCapability.Unknown,
                };
            }

            return new StorageCapabilities
            {
                RandomAccessWrite = StorageCapability.Unsupported,
                AtomicReplace = StorageCapability.Unsupported,
                CreateChild = AndroidStorageSupport.FromFlags(flags, DocumentContractFlags.DirSupportsCreate),
                Delete = AndroidStorageSupport.FromFlags(flags, DocumentContractFlags.SupportsDelete),
                PersistAccess = StorageCapability.Supported,
            };
        }
    }

    /// <summary>本地路径；SAF 树返回 null。</summary>
    /// <returns>本地路径或 null。</returns>
    public string? TryGetLocalPath() => _localPath;

    /// <summary>异步枚举直接子项；每 64 项让出一次，避免大目录阻塞 UI。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件与子目录序列。</returns>
    public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_localPath != null)
        {
            int localCount = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(_localPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Directory.Exists(entry)
                    ? new AndroidFolderRef(entry)
                    : new AndroidFileRef(entry);
                if (++localCount % 64 == 0) await Task.Yield();
            }
            yield break;
        }

        var childrenUri = BuildChildrenUri();
        ICursor cursor;
        try
        {
            cursor = _resolver.Query(childrenUri, AndroidStorageSupport.ProjectionColumns, null, null, null)
                ?? throw new StorageException(StorageError.IoError, $"无法枚举 {Name}");
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AndroidStorageSupport.Wrap(exception, $"枚举 {Name}");
        }

        int count = 0;
        using (cursor)
        {
            while (cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? documentId = AndroidStorageSupport.ReadString(
                    cursor, DocumentsContract.Document.ColumnDocumentId);
                if (string.IsNullOrEmpty(documentId)) continue;

                var metadata = AndroidStorageSupport.ReadMetadata(cursor);
                var documentUri = BuildDocumentUri(documentId);
                if (documentUri == null) continue;

                if (string.Equals(metadata.MimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal))
                    yield return new AndroidFolderRef(
                        _resolver, _treeUri, documentUri, documentId, metadata.DisplayName, metadata, true);
                else
                    yield return new AndroidFileRef(_resolver, documentUri, _treeUri, metadata, true);

                if (++count % 64 == 0) await Task.Yield();
            }
        }
    }

    /// <summary>在树内按显示名查找直接子文件；Provider 失败抛 typed 异常，未找到返回 null。</summary>
    /// <param name="name">显示名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子文件或 null。</returns>
    public async Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default)
    {
        AndroidStorageSupport.ValidateChildName(name);
        await foreach (var item in GetItemsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is IFileRef file && string.Equals(file.Name, name, StringComparison.Ordinal))
                return file;
        }
        return null;
    }

    /// <summary>在本目录新建文件；MIME 按扩展名选择（.xmp → application/xml）。</summary>
    /// <param name="name">文件名（不含路径分隔符）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建文件引用。</returns>
    public Task<IFileRef> CreateFileAsync(string name, CancellationToken cancellationToken = default)
    {
        AndroidStorageSupport.ValidateChildName(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (_localPath is { } local) return Task.FromResult(CreateLocalFile(local, name));

        return Task.Run<IFileRef>(() => CreateDocument(name, cancellationToken), cancellationToken);
    }

    private IFileRef CreateDocument(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var childrenUri = BuildChildrenUri();
        AndroidUri? created;
        try
        {
            created = DocumentsContract.CreateDocument(
                _resolver, childrenUri, AndroidStorageSupport.MimeTypeForName(name), name);
        }
        catch (Exception exception) when (exception is Java.Lang.IllegalArgumentException)
        {
            throw new StorageException(StorageError.InvalidName, $"无法新建 {name}：{exception.Message}", exception);
        }
        catch (Exception exception)
        {
            throw AndroidStorageSupport.Wrap(exception, $"新建 {name}");
        }
        if (created == null)
            throw new StorageException(StorageError.IoError, $"无法新建 {name}");
        cancellationToken.ThrowIfCancellationRequested();
        return new AndroidFileRef(_resolver, created, _treeUri, name, 0UL, null);
    }

    private static IFileRef CreateLocalFile(string folder, string name)
    {
        string path = System.IO.Path.Combine(folder, name);
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)) { }
            return new AndroidFileRef(path);
        }
        catch (Exception exception)
        {
            throw AndroidStorageSupport.Wrap(exception, $"新建 {name}");
        }
    }

    private AndroidUri BuildChildrenUri()
    {
        if (_documentId == null)
            throw new StorageException(StorageError.Unsupported, $"{Name} 无法枚举：缺少文档 ID");
        try
        {
            AndroidUri? children = _treeUri != null
                ? DocumentsContract.BuildChildDocumentsUriUsingTree(_treeUri, _documentId)
                : DocumentsContract.BuildChildDocumentsUri(_uri.Authority!, _documentId);
            return children
                ?? throw new StorageException(StorageError.Unsupported, $"{Name} 无法构造子项 Uri");
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AndroidStorageSupport.Wrap(exception, $"枚举 {Name}");
        }
    }

    private AndroidUri? BuildDocumentUri(string documentId)
    {
        try
        {
            return _treeUri != null
                ? DocumentsContract.BuildDocumentUriUsingTree(_treeUri, documentId)
                : DocumentsContract.BuildDocumentUri(_uri.Authority!, documentId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>缓存中的显示名（含文档 ID / Uri 末段兜底），不触发任何查询。</summary>
    private string CachedName => Metadata.DisplayName
        ?? AndroidStorageSupport.NameFromDocumentId(_documentId)
        ?? _uri.LastPathSegment
        ?? "文件夹";

    private AndroidDocumentMetadata Metadata
    {
        get
        {
            lock (_metadataGate) return _metadata;
        }
    }

    /// <summary>查询并缓存目录自身的投影（调用方保证在后台线程）。</summary>
    private void RefreshProjection()
    {
        if (_documentId == null) return;
        var target = BuildDocumentUri(_documentId) ?? _uri;
        var metadata = AndroidStorageSupport.QueryDocument(_resolver, target, CachedName);
        lock (_metadataGate)
        {
            _metadata = metadata;
            Volatile.Write(ref _propertiesLoaded, 1);
        }
    }

    /// <summary>
    /// 后台补齐目录投影与能力：选择器返回的根树 / 未带投影的目录在首次访问
    /// <see cref="Name"/> 或 <see cref="Capabilities"/> 时启动一次后台查询，
    /// getter 本身不做 binder 调用。
    /// </summary>
    private void EnsureBackgroundProjection()
    {
        if (_localPath != null || _documentId == null) return;
        if (Volatile.Read(ref _propertiesLoaded) == 1) return;
        if (Interlocked.Exchange(ref _backgroundRefreshStarted, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                RefreshProjection();
            }
            catch (Exception)
            {
                // 查询失败时能力保持 Unknown。
            }
        });
    }

    /// <summary>SAF 目录自身的文档 Uri。</summary>
    internal AndroidUri DocumentUri => _uri;
}

/// <summary>
/// 只读、不可定位的托管流包装：Provider 可能返回管道描述符（<c>fstat</c> 可返回长度但
/// <c>lseek</c> 是 ESPIPE）。显式 <see cref="CanSeek"/> = false，顺序读取照常工作，
/// 长度/定位一律 <see cref="NotSupportedException"/>，不伪装成可定位文件。
/// </summary>
internal sealed class NonSeekableReadStream : Stream
{
    private readonly Stream _inner;

    internal NonSeekableReadStream(Stream inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("当前存储流不可定位");

    public override long Position
    {
        get => throw new NotSupportedException("当前存储流不可定位");
        set => throw new NotSupportedException("当前存储流不可定位");
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override int ReadByte() => _inner.ReadByte();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("当前存储流不可定位");

    public override void SetLength(long value) => throw new NotSupportedException("当前存储流只读");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("当前存储流只读");

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
