using System.Text;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>LocalFileRef：标识/属性、存在性、size/mtime/revision 与读取、取消。</summary>
internal static class LocalFileRefChecks
{
    public static void Run(Checks c, string root)
    {
        c.Section("LocalFileRef");

        var directory = Path.Combine(root, "file-ref");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "照片 space #1.jpg");
        var content = Encoding.UTF8.GetBytes("0123456789abcdef");
        File.WriteAllBytes(path, content);
        var modifiedUtc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);

        var file = new LocalFileRef(path);
        c.Equal("Name", "照片 space #1.jpg", file.Name);
        c.Equal("本地路径为绝对路径", path, file.TryGetLocalPath());
        c.Equal("TryGetLocalPath", path, file.TryGetLocalPath());
        c.True("Path 为 file Uri", file.Path.IsFile, file.Path.ToString());
        c.Equal("Path.LocalPath 往返", path, file.Path.LocalPath);
        c.SameId("StorageId 为本地路径身份", StorageItemId.FromLocalPath(path), file.StorageId);
        c.SameId("Uri 往返 StorageId（含空格/非 ASCII）", file.StorageId, StorageItemId.FromUri(file.Path));
        c.Equal("Capabilities 为本地能力", StorageCapabilities.Local, file.Capabilities);

        var properties = file.GetBasicPropertiesAsync().GetAwaiter().GetResult();
        c.True("Exists", properties.Exists);
        c.Equal("Size", (ulong)content.Length, properties.Size);
        c.Equal("DateModified 与写入的 UTC mtime 一致", new DateTimeOffset(modifiedUtc), properties.DateModified);
        c.True("Revision 非空", properties.Revision is not null);
        c.Equal("Revision.Size", (long?)content.Length, properties.Revision?.Size);
        c.Equal("Revision.LastModified", new DateTimeOffset(modifiedUtc), properties.Revision?.LastModified);

        using (var stream = file.OpenReadAsync().GetAwaiter().GetResult())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            c.Bytes("OpenReadAsync 内容", content, buffer.ToArray());
        }

        var parent = file.GetParentAsync().GetAwaiter().GetResult();
        c.True("GetParentAsync 返回父目录",
            parent is LocalFolderRef && parent.TryGetLocalPath() == directory,
            parent is null ? "返回 null" : $"{parent.GetType().Name} / {parent.TryGetLocalPath()}");

        var missingPath = Path.Combine(directory, "missing.jpg");
        var missing = new LocalFileRef(missingPath);
        var missingProperties = missing.GetBasicPropertiesAsync().GetAwaiter().GetResult();
        c.True("缺失文件 Exists=false", !missingProperties.Exists);
        c.Equal("缺失文件 Size 为空", (ulong?)null, missingProperties.Size);
        c.Equal("缺失文件 DateModified 为空", (DateTimeOffset?)null, missingProperties.DateModified);
        var missingRevision = missingProperties.Revision;
        c.True("缺失文件 Revision 无可比字段",
            missingRevision is null
            || (missingRevision.Value.Size is null && missingRevision.Value.LastModified is null),
            "Revision 仍带回退值");

        c.StorageFailsAsync("缺失文件 OpenReadAsync 报 ObjectGone", StorageError.ObjectGone,
            () => missing.OpenReadAsync()).GetAwaiter().GetResult();
        c.StorageFailsAsync("缺失文件 OpenRandomAccessAsync 报 ObjectGone", StorageError.ObjectGone,
            () => missing.OpenRandomAccessAsync(StorageAccessMode.Read)).GetAwaiter().GetResult();

        var cancelled = new CancellationToken(canceled: true);
        c.ThrowsAssignableAsync<OperationCanceledException>("GetBasicPropertiesAsync 响应预取消令牌",
            () => file.GetBasicPropertiesAsync(cancelled)).GetAwaiter().GetResult();
        c.ThrowsAssignableAsync<OperationCanceledException>("OpenReadAsync 响应预取消令牌",
            () => file.OpenReadAsync(cancelled)).GetAwaiter().GetResult();
        c.ThrowsAssignableAsync<OperationCanceledException>("OpenRandomAccessAsync 响应预取消令牌",
            () => file.OpenRandomAccessAsync(StorageAccessMode.Read, cancelled)).GetAwaiter().GetResult();
        c.ThrowsAssignableAsync<OperationCanceledException>("GetParentAsync 响应预取消令牌",
            () => file.GetParentAsync(cancelled)).GetAwaiter().GetResult();
        c.ThrowsAssignableAsync<OperationCanceledException>("DeleteAsync 响应预取消令牌",
            () => file.DeleteAsync(cancelled)).GetAwaiter().GetResult();
        c.ThrowsAssignableAsync<OperationCanceledException>("AtomicWriteAsync 响应预取消令牌",
            () => file.AtomicWriteAsync((_, _) => Task.CompletedTask, cancellationToken: cancelled))
            .GetAwaiter().GetResult();
        c.Bytes("取消后原文件字节不变", content, File.ReadAllBytes(path));
    }
}
