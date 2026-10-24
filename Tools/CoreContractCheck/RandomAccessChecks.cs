using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>随机访问会话：范围读写、Flush 落盘、只读拒写与打开失败映射。</summary>
internal static class RandomAccessChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("LocalRandomAccessSession");

        var directory = Path.Combine(root, "random");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.bin");
        var original = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(path, original);
        var modifiedUtc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);

        var file = new LocalFileRef(path);
        var patch = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var expected = original.ToArray();
        Array.Copy(patch, 0, expected, 4, patch.Length);

        await using (var session = await file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite))
        {
            c.Equal("ReadWrite Length", (long)original.Length, session.Length);
            c.Equal("ReadWrite Revision.Size", (long?)original.Length, session.Revision?.Size);
            c.Equal("ReadWrite Revision.LastModified",
                new DateTimeOffset(modifiedUtc), session.Revision?.LastModified);

            await session.WriteAsync(4, patch);
            var readBack = new byte[patch.Length];
            var read = await session.ReadAsync(4, readBack);
            c.Equal("范围写后读回字节数", patch.Length, read);
            c.Bytes("范围写后读回内容", patch, readBack);

            await session.FlushAsync();

            var whole = new byte[original.Length];
            var total = 0;
            while (total < whole.Length)
            {
                var n = await session.ReadAsync(total, whole.AsMemory(total));
                if (n == 0) break;
                total += n;
            }

            c.Equal("会话内读取覆盖整个文件", whole.Length, total);
            c.Bytes("范围写不改动其它字节", expected, whole);
            c.Equal("会话 Length 不因范围写改变", (long)original.Length, session.Length);
            c.Equal("EOF 之后读取返回 0", 0, await session.ReadAsync(original.Length, new byte[4]));
        }

        c.Bytes("Flush + Dispose 后落盘内容", expected, File.ReadAllBytes(path));

        var beforeReadOnly = File.ReadAllBytes(path);
        await using (var reader = await file.OpenRandomAccessAsync(StorageAccessMode.Read))
        {
            c.Equal("Read Length", (long)expected.Length, reader.Length);
            await c.ThrowsExactAsync<InvalidOperationException>(
                "Read 模式写入抛 InvalidOperationException",
                () => reader.WriteAsync(0, new byte[] { 0xFF }).AsTask());
            await reader.FlushAsync();
            c.Pass("Read 模式 Flush 不抛错");
        }

        c.Bytes("只读会话未改动文件", beforeReadOnly, File.ReadAllBytes(path));

        var cancelled = new CancellationToken(canceled: true);
        await c.ThrowsAssignableAsync<OperationCanceledException>(
            "OpenRandomAccessAsync 响应预取消令牌",
            () => file.OpenRandomAccessAsync(StorageAccessMode.ReadWrite, cancelled));

        var missing = new LocalFileRef(Path.Combine(directory, "missing.bin"));
        await c.StorageFailsAsync("缺失文件打开随机会话报 ObjectGone", StorageError.ObjectGone,
            () => missing.OpenRandomAccessAsync(StorageAccessMode.ReadWrite));

        if (OperatingSystem.IsWindows())
        {
            c.Skip("文件权限拒绝打开映射 AccessDenied", "Windows 分支需在 Windows 上验证，本机不冒充");
        }
        else
        {
            var readOnlyPath = Path.Combine(directory, "readonly.bin");
            File.WriteAllBytes(readOnlyPath, original);
            File.SetUnixFileMode(readOnlyPath, UnixFileMode.UserRead);
            try
            {
                var readOnlyFile = new LocalFileRef(readOnlyPath);
                await c.StorageFailsAsync("无写权限文件打开 ReadWrite 报 AccessDenied",
                    StorageError.AccessDenied,
                    () => readOnlyFile.OpenRandomAccessAsync(StorageAccessMode.ReadWrite));
                await using var readSession = await readOnlyFile.OpenRandomAccessAsync(StorageAccessMode.Read);
                c.Equal("无写权限文件仍可只读打开", (long)original.Length, readSession.Length);
            }
            finally
            {
                File.SetUnixFileMode(readOnlyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
