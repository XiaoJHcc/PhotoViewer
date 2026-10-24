using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// 验收专用 DocumentsProvider：用 App 私有目录做 backing store，提供真实 <c>content://</c> 文档树。
/// 三棵固定树 —— <c>writable</c>（随机读写/新建/删除）、<c>readonly</c>（rw 拒绝）、
/// <c>nonseekable</c>（只给不可定位的管道 fd）。仅 Debug 构建注册；DocumentsProvider 要求
/// <c>exported=true</c> + <c>grantUriPermissions</c> + <c>MANAGE_DOCUMENTS</c> 读写权限
/// （同 UID 访问不受该权限限制，只有本应用与系统 DocumentsUI 能读写这棵测试树）。
/// Release 不注册 Provider。仅验收使用，不承载产品数据，也不是 MTP/USB 设备模拟。
/// </summary>
#if DEBUG
[ContentProvider(
    new[] { DebugTestDocumentsProvider.TestAuthority },
    Name = "cc.xiaojh.PhotoViewer.Core.DebugTestDocumentsProvider",
    // DocumentsProvider.attachInfo 强制 exported=true + grantUriPermissions + MANAGE_DOCUMENTS 保护；
    // 同 UID 访问不受该权限限制，因此只有本应用与系统 DocumentsUI 能读写这棵测试树。
    Exported = true,
    GrantUriPermissions = true,
    ReadPermission = "android.permission.MANAGE_DOCUMENTS",
    WritePermission = "android.permission.MANAGE_DOCUMENTS")]
#endif
public sealed class DebugTestDocumentsProvider : DocumentsProvider
{
    /// <summary>固定测试 authority（Provider 属性生成 manifest 条目时使用）。</summary>
    internal const string TestAuthority = "cc.xiaojh.PhotoViewer.debug.storage";

    internal const string WritableRoot = "writable";
    internal const string ReadonlyRoot = "readonly";
    internal const string NonseekableRoot = "nonseekable";

    private static readonly string[] Roots = [WritableRoot, ReadonlyRoot, NonseekableRoot];

    private static readonly object RevocationGate = new();
    private static readonly HashSet<string> RevokedRoots = new(StringComparer.Ordinal);

    private static readonly string[] DefaultRootColumns =
    [
        DocumentsContract.Root.ColumnRootId,
        DocumentsContract.Root.ColumnDocumentId,
        DocumentsContract.Root.ColumnTitle,
        DocumentsContract.Root.ColumnFlags,
        DocumentsContract.Root.ColumnIcon,
        DocumentsContract.Root.ColumnMimeTypes,
        DocumentsContract.Root.ColumnAvailableBytes,
    ];

    private static readonly string[] DefaultDocumentColumns =
    [
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
        DocumentsContract.Document.ColumnFlags,
    ];

    /// <summary>backing store 根目录（解锁/验收写入夹具用，产品路径不引用）。</summary>
    internal static string StoreRoot(Context context) =>
        Path.Combine(context.FilesDir!.AbsolutePath, "pv-debug-provider");

    /// <summary>把文档 ID 映射到 backing store 中的绝对路径；拒绝越出树根。</summary>
    internal static string PathFor(Context context, string documentId)
    {
        if (!TrySplit(documentId, out string rootId, out string? relative))
            throw new Java.IO.FileNotFoundException($"未知文档：{documentId}");
        string path = Path.Combine(StoreRoot(context), rootId);
        if (relative != null)
        {
            foreach (string segment in relative.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or ".."
                    || segment.IndexOfAny(['\\', '\0']) >= 0)
                    throw new Java.IO.FileNotFoundException($"非法文档路径：{documentId}");
            }
            path = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        return path;
    }

    /// <summary>建立三棵树目录；重复调用安全。</summary>
    internal static void EnsureStore(Context context)
    {
        foreach (string root in Roots)
            Directory.CreateDirectory(Path.Combine(StoreRoot(context), root));
    }

    /// <summary>测试开关：让 Provider 对指定树根拒绝访问（不是系统 URI 授权撤销）；<paramref name="rootId"/> 为空表示清空。</summary>
    internal static void SetRevoked(string? rootId)
    {
        lock (RevocationGate)
        {
            if (string.IsNullOrEmpty(rootId)) RevokedRoots.Clear();
            else RevokedRoots.Add(rootId);
        }
    }

    /// <inheritdoc />
    public override bool OnCreate()
    {
        try
        {
            EnsureStore(Context!);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DebugProvider] 初始化失败：{exception.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public override ICursor? QueryRoots(string[]? projection)
    {
        var columns = ResolveProjection(projection, DefaultRootColumns);
        var cursor = new MatrixCursor(columns);
        foreach (string root in Roots)
        {
            var row = new Java.Lang.Object[columns.Length];
            for (int index = 0; index < columns.Length; index++)
            {
                row[index] = columns[index] switch
                {
                    DocumentsContract.Root.ColumnRootId => new Java.Lang.String(root),
                    DocumentsContract.Root.ColumnDocumentId => new Java.Lang.String(root),
                    DocumentsContract.Root.ColumnTitle => new Java.Lang.String(TitleOf(root)),
                    DocumentsContract.Root.ColumnFlags => Java.Lang.Integer.ValueOf(RootFlags(root)),
                    DocumentsContract.Root.ColumnIcon => Java.Lang.Integer.ValueOf(global::Android.Resource.Drawable.IcMenuGallery),
                    DocumentsContract.Root.ColumnMimeTypes => new Java.Lang.String("image/*"),
                    DocumentsContract.Root.ColumnAvailableBytes => Java.Lang.Long.ValueOf(AvailableBytes(root)),
                    _ => null!,
                };
            }
            cursor.AddRow(row);
        }
        return cursor;
    }

    /// <inheritdoc />
    public override ICursor? QueryDocument(string? documentId, string[]? projection)
    {
        if (documentId == null) throw new Java.IO.FileNotFoundException("缺少 documentId");
        EnterDocument(documentId);
        var columns = ResolveProjection(projection, DefaultDocumentColumns);
        var cursor = new MatrixCursor(columns);
        if (!TrySplit(documentId, out string rootId, out string? relative))
            throw new Java.IO.FileNotFoundException($"未知文档：{documentId}");
        string path = PathFor(Context!, documentId);
        bool isDirectory = relative == null || Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            throw new Java.IO.FileNotFoundException($"文档不存在：{documentId}");
        cursor.AddRow(BuildDocumentRow(columns, rootId, documentId, path, isDirectory));
        return cursor;
    }

    /// <inheritdoc />
    public override ICursor? QueryChildDocuments(string? parentDocumentId, string[]? projection, string? sortOrder)
    {
        if (parentDocumentId == null) throw new Java.IO.FileNotFoundException("缺少 parentDocumentId");
        EnterDocument(parentDocumentId);
        if (!TrySplit(parentDocumentId, out string rootId, out string? relative))
            throw new Java.IO.FileNotFoundException($"未知目录：{parentDocumentId}");
        string parentPath = PathFor(Context!, parentDocumentId);
        if (!Directory.Exists(parentPath))
            throw new Java.IO.FileNotFoundException($"目录不存在：{parentDocumentId}");

        var columns = ResolveProjection(projection, DefaultDocumentColumns);
        var cursor = new MatrixCursor(columns);
        string prefix = relative == null ? rootId : rootId + "/" + relative;
        foreach (string entry in Directory.EnumerateFileSystemEntries(parentPath))
        {
            string name = Path.GetFileName(entry);
            bool isDirectory = Directory.Exists(entry);
            cursor.AddRow(BuildDocumentRow(columns, rootId, prefix + "/" + name, entry, isDirectory));
        }
        return cursor;
    }

    /// <inheritdoc />
    public override ICursor? QuerySearchDocuments(string? rootId, string? query, string[]? projection)
    {
        if (rootId == null) throw new Java.IO.FileNotFoundException("缺少 rootId");
        if (!Roots.Contains(rootId)) throw new Java.IO.FileNotFoundException($"未知树：{rootId}");
        EnterDocument(rootId);
        var columns = ResolveProjection(projection, DefaultDocumentColumns);
        var cursor = new MatrixCursor(columns);
        string rootPath = PathFor(Context!, rootId);
        if (!Directory.Exists(rootPath)) return cursor;
        foreach (string entry in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(entry);
            if (name.IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string docId = rootId + "/" + Path.GetRelativePath(rootPath, entry).Replace(Path.DirectorySeparatorChar, '/');
            cursor.AddRow(BuildDocumentRow(columns, rootId, docId, entry, Directory.Exists(entry)));
        }
        return cursor;
    }

    /// <inheritdoc />
    public override ICursor? QueryRecentDocuments(string? rootId, string[]? projection)
        => QuerySearchDocuments(rootId, string.Empty, projection);

    /// <inheritdoc />
    public override string? GetDocumentType(string? documentId)
    {
        if (documentId == null) throw new Java.IO.FileNotFoundException("缺少 documentId");
        EnterDocument(documentId);
        string path = PathFor(Context!, documentId);
        return Directory.Exists(path) ? DocumentsContract.Document.MimeTypeDir : AndroidStorageSupport.MimeTypeForName(path);
    }

    /// <inheritdoc />
    public override bool IsChildDocument(string? parentDocumentId, string? documentId)
    {
        if (parentDocumentId == null || documentId == null) return false;
        if (!TrySplit(parentDocumentId, out string parentRoot, out string? parentRelative)) return false;
        if (!TrySplit(documentId, out string childRoot, out string? childRelative)) return false;
        if (!string.Equals(parentRoot, childRoot, StringComparison.Ordinal)) return false;
        string prefix = parentRelative == null ? string.Empty : parentRelative + "/";
        return childRelative != null && childRelative.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override ParcelFileDescriptor? OpenDocument(string? documentId, string? mode, CancellationSignal? signal)
    {
        if (documentId == null) throw new Java.IO.FileNotFoundException("缺少 documentId");
        mode ??= "r";
        EnterDocument(documentId);
        if (!TrySplit(documentId, out string rootId, out string? relative) || relative == null)
            throw new Java.IO.FileNotFoundException($"不是可打开的文件：{documentId}");
        string path = PathFor(Context!, documentId);
        if (!File.Exists(path)) throw new Java.IO.FileNotFoundException($"文件不存在：{documentId}");

        if (string.Equals(rootId, ReadonlyRoot, StringComparison.Ordinal) && mode != "r")
            throw new Java.Lang.SecurityException("只读树拒绝读写打开");
        if (string.Equals(rootId, NonseekableRoot, StringComparison.Ordinal))
            return OpenNonseekable(path, mode);

        return ParcelFileDescriptor.Open(new Java.IO.File(path),
            (ParcelFileMode)ParcelFileDescriptor.ParseMode(mode));
    }

    /// <inheritdoc />
    public override string? CreateDocument(string? parentDocumentId, string? mimeType, string? displayName)
    {
        if (parentDocumentId == null) throw new Java.IO.FileNotFoundException("缺少 parentDocumentId");
        if (displayName == null) throw new Java.Lang.IllegalArgumentException("缺少文件名");
        EnterDocument(parentDocumentId);
        if (!TrySplit(parentDocumentId, out string rootId, out string? relative))
            throw new Java.IO.FileNotFoundException($"未知目录：{parentDocumentId}");
        if (!string.Equals(rootId, WritableRoot, StringComparison.Ordinal))
            throw new Java.Lang.SecurityException("只读树不接受新建");

        ValidateDisplayName(displayName);
        string parentPath = PathFor(Context!, parentDocumentId);
        if (!Directory.Exists(parentPath)) throw new Java.IO.FileNotFoundException($"目录不存在：{parentDocumentId}");
        string path = Path.Combine(parentPath, displayName);
        if (File.Exists(path) || Directory.Exists(path))
            throw new Java.IO.FileNotFoundException($"同名文档已存在：{displayName}");
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        }
        catch (Exception exception)
        {
            throw new Java.IO.IOException($"无法新建 {displayName}：{exception.Message}");
        }

        // DocumentsProvider.insert 把返回值当作文档 ID 再拼 URI，这里必须只返回 ID，不能返回完整 Uri。
        return relative == null
            ? rootId + "/" + displayName
            : rootId + "/" + relative + "/" + displayName;
    }

    /// <inheritdoc />
    public override void DeleteDocument(string? documentId)
    {
        if (documentId == null) throw new Java.IO.FileNotFoundException("缺少 documentId");
        EnterDocument(documentId);
        if (!TrySplit(documentId, out string rootId, out string? relative) || relative == null)
            throw new Java.IO.FileNotFoundException($"不能删除树根：{documentId}");
        if (!string.Equals(rootId, WritableRoot, StringComparison.Ordinal))
            throw new Java.Lang.SecurityException("只读树不接受删除");

        string path = PathFor(Context!, documentId);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        else throw new Java.IO.FileNotFoundException($"文档不存在：{documentId}");
    }

    /// <summary>测试开关：<c>revoke</c> 让指定树根拒绝访问，<c>grant</c> 清除（arg 为空时清空；不是系统授权）。</summary>
    public override Bundle? Call(string method, string? arg, Bundle? extras)
    {
        switch (method)
        {
            case "revoke":
                SetRevoked(arg);
                return new Bundle();
            case "grant":
                SetRevoked(null);
                return new Bundle();
            default:
                return base.Call(method, arg, extras);
        }
    }

    private ParcelFileDescriptor OpenNonseekable(string path, string mode)
    {
        var pipe = ParcelFileDescriptor.CreatePipe()
            ?? throw new Java.IO.IOException("无法创建管道");
        var readEnd = pipe[0]!;
        var writeEnd = pipe[1]!;
        if (mode == "r")
        {
            // 顺序读：后台把文件内容写进管道，调用方拿到只可顺序读取的 fd。
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var output = new ParcelFileDescriptor.AutoCloseOutputStream(writeEnd);
                    byte[] bytes = File.ReadAllBytes(path);
                    output.Write(bytes);
                    output.Flush();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[DebugProvider] 管道写入失败：{exception.Message}");
                    try { writeEnd.Dispose(); } catch (Exception) { }
                }
            });
            return readEnd;
        }

        // 随机读写：仍给管道写端；没有可定位语义，客户端必须在定位/截断时得到 NotSeekable。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var input = new ParcelFileDescriptor.AutoCloseInputStream(readEnd);
                byte[] buffer = new byte[8192];
                while (input.Read(buffer) > 0)
                {
                }
            }
            catch (Exception)
            {
                // 对端关闭即结束。
            }
        });
        return writeEnd;
    }

    private Java.Lang.Object[] BuildDocumentRow(
        string[] columns,
        string rootId,
        string documentId,
        string path,
        bool isDirectory)
    {
        string name = string.Equals(documentId, rootId, StringComparison.Ordinal)
            ? TitleOf(rootId)
            : Path.GetFileName(path);
        long? size;
        long? modified;
        if (!isDirectory && string.Equals(documentId, WritableRoot + "/unknown-meta.jpg", StringComparison.Ordinal))
        {
            // 验收：Provider 给出 size=-1 / mtime=null，客户端不得转成 ulong 巨值。
            size = -1;
            modified = null;
        }
        else if (isDirectory)
        {
            size = null;
            modified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        else
        {
            var info = new FileInfo(path);
            size = info.Length;
            modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        int flags = isDirectory
            ? (int)(string.Equals(rootId, WritableRoot, StringComparison.Ordinal)
                ? DocumentContractFlags.DirSupportsCreate | DocumentContractFlags.SupportsDelete
                : DocumentContractFlags.SupportsDelete)
            : string.Equals(rootId, WritableRoot, StringComparison.Ordinal)
                ? (int)(DocumentContractFlags.SupportsWrite | DocumentContractFlags.SupportsDelete)
                : 0;

        string mime = isDirectory
            ? DocumentsContract.Document.MimeTypeDir
            : AndroidStorageSupport.MimeTypeForName(name);

        var row = new Java.Lang.Object[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            row[index] = columns[index] switch
            {
                DocumentsContract.Document.ColumnDocumentId => new Java.Lang.String(documentId),
                DocumentsContract.Document.ColumnDisplayName => new Java.Lang.String(name),
                DocumentsContract.Document.ColumnMimeType => new Java.Lang.String(mime),
                DocumentsContract.Document.ColumnSize => size is { } bytes ? Java.Lang.Long.ValueOf(bytes) : null!,
                DocumentsContract.Document.ColumnLastModified => modified is { } stamp ? Java.Lang.Long.ValueOf(stamp) : null!,
                DocumentsContract.Document.ColumnFlags => Java.Lang.Integer.ValueOf(flags),
                _ => null!,
            };
        }
        return row;
    }

    private static string[] ResolveProjection(string[]? projection, string[] defaults)
        => projection is { Length: > 0 } ? projection : defaults;

    private static string TitleOf(string rootId) => rootId switch
    {
        WritableRoot => "Writable (debug)",
        ReadonlyRoot => "Readonly (debug)",
        NonseekableRoot => "Nonseekable (debug)",
        _ => rootId,
    };

    private static int RootFlags(string rootId) =>
        (int)(string.Equals(rootId, WritableRoot, StringComparison.Ordinal)
            ? DocumentRootFlags.SupportsCreate | DocumentRootFlags.SupportsIsChild | DocumentRootFlags.LocalOnly
            : DocumentRootFlags.LocalOnly);

    private long AvailableBytes(string rootId)
    {
        try
        {
            var info = new Java.IO.File(Path.Combine(StoreRoot(Context!), rootId));
            return info.UsableSpace;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName is "." or ".."
            || displayName.IndexOfAny(['/', '\\', '\0']) >= 0
            || displayName != displayName.Trim())
            throw new Java.Lang.IllegalArgumentException($"非法文件名：{displayName}");
    }

    private static bool TrySplit(string documentId, out string rootId, out string? relative)
    {
        rootId = documentId;
        relative = null;
        foreach (string root in Roots)
        {
            if (string.Equals(documentId, root, StringComparison.Ordinal))
            {
                rootId = root;
                return true;
            }
            if (documentId.StartsWith(root + "/", StringComparison.Ordinal))
            {
                rootId = root;
                relative = documentId[(root.Length + 1)..];
                return true;
            }
        }
        return false;
    }

    /// <summary>拒绝访问注入：测试开关打开时 Provider 抛 SecurityException（客户端应归类为 AccessDenied）。</summary>
    private static void EnterDocument(string documentId)
    {
        string rootId = documentId;
        foreach (string root in Roots)
        {
            if (string.Equals(documentId, root, StringComparison.Ordinal)
                || documentId.StartsWith(root + "/", StringComparison.Ordinal))
            {
                rootId = root;
                break;
            }
        }
        lock (RevocationGate)
        {
            if (RevokedRoots.Contains(rootId))
                throw new Java.Lang.SecurityException($"测试访问已关闭：{rootId}");
        }
    }
}
