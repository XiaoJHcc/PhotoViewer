using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Database;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 原生壳存储引用（A1/A2）：SAF <c>content://</c> 文档与本地文件路径都实现共享
/// <see cref="PhotoViewer.Core.Storage.IFileRef"/> / <c>IFolderRef</c>。
/// SAF 引用保留树 Uri，遍历/读取/写回都经 <see cref="ContentResolver"/>；本地路径用于验收夹具与 file:// 外部打开。
/// </summary>
public sealed class AndroidFileRef : PhotoViewer.Core.Storage.IFileRef
{
    private readonly ContentResolver _resolver;
    private readonly AndroidUri _uri;
    private readonly AndroidUri? _treeUri;
    private readonly string? _displayName;
    private readonly ulong? _size;
    private readonly DateTimeOffset? _modified;

    /// <summary>从 SAF 文档构造引用。</summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="documentUri">文档 Uri。</param>
    /// <param name="treeUri">所属树 Uri（写回/枚举用，可空）。</param>
    /// <param name="displayName">显示名（可空，缺省从 Uri 末段取）。</param>
    /// <param name="size">字节数。</param>
    /// <param name="modified">修改时间。</param>
    public AndroidFileRef(ContentResolver resolver, AndroidUri documentUri, AndroidUri? treeUri = null,
        string? displayName = null, ulong? size = null, DateTimeOffset? modified = null)
    {
        _resolver = resolver;
        _uri = documentUri;
        _treeUri = treeUri;
        _displayName = displayName;
        _size = size;
        _modified = modified;
        Path = new Uri(documentUri.ToString()!);
    }

    /// <summary>从本地文件构造引用（验收夹具 / file:// 外部打开）。</summary>
    /// <param name="path">本地绝对路径。</param>
    public AndroidFileRef(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        _resolver = Application.Context.ContentResolver!;
        _uri = AndroidUri.FromFile(new Java.IO.File(full))!;
        Path = new Uri(full);
        _displayName = System.IO.Path.GetFileName(full);
        var info = new FileInfo(full);
        _size = (ulong)Math.Max(0, info.Length);
        _modified = info.LastWriteTimeUtc;
    }

    /// <summary>文件显示名。</summary>
    public string Name => _displayName ?? _uri.LastPathSegment ?? "image";

    /// <summary>平台路径 URI。</summary>
    public Uri Path { get; }

    /// <summary>缓存键；SAF 用文档 Uri，本地文件用完整路径。</summary>
    public string PathKey => TryGetLocalPath() ?? _uri.ToString()!;

    /// <summary>本地路径；SAF 文档返回 null。</summary>
    /// <returns>本地路径或 null。</returns>
    public string? TryGetLocalPath()
    {
        if (string.Equals(_uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return _uri.Path;
        return Path.IsFile ? Path.LocalPath : null;
    }

    /// <summary>打开只读流。</summary>
    /// <returns>共享读取流。</returns>
    public Task<Stream> OpenReadAsync()
    {
        string? local = TryGetLocalPath();
        if (local != null)
        {
            return Task.FromResult<Stream>(new FileStream(local, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        var stream = _resolver.OpenInputStream(_uri)
            ?? throw new IOException($"无法打开 {_uri}");
        return Task.FromResult(stream);
    }

    /// <summary>读取文件大小与修改时间。</summary>
    /// <returns>基本属性。</returns>
    public Task<PhotoViewer.Core.Storage.StorageItemBasicProperties> GetBasicPropertiesAsync()
    {
        if (TryGetLocalPath() is { } local)
        {
            var info = new FileInfo(local);
            return Task.FromResult(new PhotoViewer.Core.Storage.StorageItemBasicProperties(
                (ulong)Math.Max(0, info.Length), info.LastWriteTimeUtc));
        }
        if (_size.HasValue || _modified.HasValue)
            return Task.FromResult(new PhotoViewer.Core.Storage.StorageItemBasicProperties(_size, _modified));
        try
        {
            using var cursor = _resolver.Query(_uri, [OpenableColumns.Size, OpenableColumns.DisplayName], null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                ulong? size = cursor.IsNull(0) ? null : (ulong)cursor.GetLong(0);
                return Task.FromResult(new PhotoViewer.Core.Storage.StorageItemBasicProperties(size, null));
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取 SAF 属性失败（{Name}）：{exception.Message}");
        }
        return Task.FromResult(new PhotoViewer.Core.Storage.StorageItemBasicProperties(null, null));
    }

    /// <summary>取得父文件夹；SAF 树已知时返回树根，本地文件返回父目录。</summary>
    /// <returns>父文件夹或 null。</returns>
    public Task<PhotoViewer.Core.Storage.IFolderRef?> GetParentAsync()
    {
        if (TryGetLocalPath() is { } local)
        {
            string? parent = System.IO.Path.GetDirectoryName(local);
            return Task.FromResult<PhotoViewer.Core.Storage.IFolderRef?>(
                parent == null ? null : new AndroidFolderRef(parent));
        }
        if (_treeUri != null)
            return Task.FromResult<PhotoViewer.Core.Storage.IFolderRef?>(
                new AndroidFolderRef(_resolver, _treeUri, null));
        return Task.FromResult<PhotoViewer.Core.Storage.IFolderRef?>(null);
    }

    /// <summary>关联的树 Uri（渲染/写回诊断用）。</summary>
    internal AndroidUri DocumentUri => _uri;
    /// <summary>所属树 Uri。</summary>
    internal AndroidUri? TreeUri => _treeUri;
}

/// <summary>
/// Android SAF 目录引用：用 <see cref="DocumentsContract"/> 枚举直接子项，不递归；本地目录直接走文件系统。
/// </summary>
public sealed class AndroidFolderRef : PhotoViewer.Core.Storage.IFolderRef
{
    private readonly ContentResolver _resolver;
    private readonly AndroidUri _treeUri;
    private readonly string? _localPath;
    private string? _name;

    /// <summary>从 SAF 树构造目录引用。</summary>
    /// <param name="resolver">内容解析器。</param>
    /// <param name="treeUri">树 Uri。</param>
    /// <param name="name">显示名（可空）。</param>
    public AndroidFolderRef(ContentResolver resolver, AndroidUri treeUri, string? name)
    {
        _resolver = resolver;
        _treeUri = treeUri;
        _name = name;
        Path = new Uri(treeUri.ToString()!);
    }

    /// <summary>从本地目录构造引用。</summary>
    /// <param name="path">本地绝对路径。</param>
    public AndroidFolderRef(string path)
    {
        _localPath = System.IO.Path.GetFullPath(path);
        _resolver = Application.Context.ContentResolver!;
        _treeUri = AndroidUri.FromFile(new Java.IO.File(_localPath))!;
        _name = System.IO.Path.GetFileName(_localPath);
        Path = new Uri(_localPath);
    }

    /// <summary>目录显示名。</summary>
    public string Name => _name ?? _treeUri.LastPathSegment ?? "文件夹";

    /// <summary>目录路径 URI。</summary>
    public Uri Path { get; }

    /// <summary>缓存键。</summary>
    public string PathKey => _localPath ?? _treeUri.ToString()!;

    /// <summary>本地路径；SAF 树返回 null。</summary>
    /// <returns>本地路径或 null。</returns>
    public string? TryGetLocalPath() => _localPath;

    /// <summary>异步枚举直接子项；每 64 项让出一次，避免大目录阻塞 UI。</summary>
    /// <returns>文件与子目录序列。</returns>
    public async IAsyncEnumerable<PhotoViewer.Core.Storage.IStorageItemRef> GetItemsAsync()
    {
        if (_localPath != null)
        {
            int localCount = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(_localPath))
            {
                yield return Directory.Exists(entry)
                    ? new AndroidFolderRef(entry)
                    : new AndroidFileRef(entry);
                if (++localCount % 64 == 0) await Task.Yield();
            }
            yield break;
        }

        string rootId = DocumentsContract.GetTreeDocumentId(_treeUri)
            ?? throw new IOException("SAF 树缺少文档 ID");
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeUri, rootId)
            ?? throw new IOException("无法构造 SAF 子项 Uri");
        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize,
            DocumentsContract.Document.ColumnLastModified,
        ];
        int count = 0;
        using var cursor = _resolver.Query(childrenUri, projection, null, null, null);
        if (cursor == null) yield break;
        while (cursor.MoveToNext())
        {
            string? documentId = cursor.GetString(0);
            if (string.IsNullOrEmpty(documentId)) continue;
            string? displayName = cursor.GetString(1);
            string? mimeType = cursor.GetString(2);
            ulong? size = cursor.IsNull(3) ? null : (ulong)cursor.GetLong(3);
            DateTimeOffset? modified = cursor.IsNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(4));
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeUri, documentId);
            if (documentUri == null) continue;
            if (string.Equals(mimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal))
                yield return new AndroidFolderRef(_resolver, documentUri, null) { _name = displayName };
            else
                yield return new AndroidFileRef(_resolver, documentUri, _treeUri, displayName, size, modified);
            if (++count % 64 == 0) await Task.Yield();
        }
    }

    /// <summary>SAF 子目录的树 Uri 即其文档 Uri。</summary>
    internal AndroidUri DocumentUri => _treeUri;
}
