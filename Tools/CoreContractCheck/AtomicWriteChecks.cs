using System.Text;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>原子写：新建/替换、revision 校验、writer 失败与取消时的原文件保全和临时文件清理。</summary>
internal static class AtomicWriteChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("LocalFileRef.AtomicWriteAsync");

        var directory = Path.Combine(root, "atomic");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "target.txt");
        var file = new LocalFileRef(path);
        var siblingPath = Path.Combine(directory, "sibling.txt");
        File.WriteAllText(siblingPath, "sibling");

        var first = Encoding.UTF8.GetBytes("hello atomic");
        var writerCalls = 0;
        await file.AtomicWriteAsync(async (stream, token) =>
        {
            writerCalls++;
            await stream.WriteAsync(first, token);
        });
        c.Equal("新文件 writer 调用一次", 1, writerCalls);
        c.Bytes("新文件内容", first, File.ReadAllBytes(path));
        c.NoTempArtifacts("新文件无 .tmp/.bak 残留", directory);

        var replacement = Encoding.UTF8.GetBytes("replaced content");
        await file.AtomicWriteAsync(async (stream, token) => await stream.WriteAsync(replacement, token));
        c.Bytes("替换旧文件内容", replacement, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await file.AtomicWriteAsync(async (stream, token) => await stream.WriteAsync(replacement, token));
            c.Equal("原子替换保留 Unix 权限",
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        c.NoTempArtifacts("替换旧文件无 .tmp/.bak 残留", directory);
        c.Equal("同目录其它文件不受影响", "sibling", File.ReadAllText(siblingPath));

        var current = await file.GetBasicPropertiesAsync();
        var versioned = Encoding.UTF8.GetBytes("matched revision");
        await file.AtomicWriteAsync(
            async (stream, token) => await stream.WriteAsync(versioned, token),
            current.Revision);
        c.Bytes("expectedRevision 匹配时写入成功", versioned, File.ReadAllBytes(path));

        var beforeMismatch = File.ReadAllBytes(path);
        var staleSize = new StorageRevision(beforeMismatch.Length + 1, null);
        var mismatchWriterCalled = false;
        await c.StorageFailsAsync("expectedRevision 尺寸不匹配抛 ChangedExternally",
            StorageError.ChangedExternally,
            () => file.AtomicWriteAsync(
                (_, _) =>
                {
                    mismatchWriterCalled = true;
                    return Task.CompletedTask;
                },
                staleSize));
        c.True("revision 不匹配时不调用 writer", !mismatchWriterCalled);
        c.Bytes("revision 不匹配时原字节不变", beforeMismatch, File.ReadAllBytes(path));
        c.NoTempArtifacts("revision 不匹配无 .tmp/.bak 残留", directory);

        var props = await file.GetBasicPropertiesAsync();
        var revision = props.Revision!.Value;
        var staleTime = new StorageRevision(
            revision.Size,
            revision.LastModified!.Value.AddSeconds(-5));
        await c.StorageFailsAsync("expectedRevision 时间不匹配抛 ChangedExternally",
            StorageError.ChangedExternally,
            () => file.AtomicWriteAsync(async (stream, token) => await stream.WriteAsync(first, token), staleTime));
        c.Bytes("时间不匹配时原字节不变", beforeMismatch, File.ReadAllBytes(path));
        c.NoTempArtifacts("时间不匹配无 .tmp/.bak 残留", directory);

        var absentPath = Path.Combine(directory, "absent.txt");
        var absent = new LocalFileRef(absentPath);
        await c.StorageFailsAsync("expectedRevision 指向缺失文件抛 ObjectGone",
            StorageError.ObjectGone,
            () => absent.AtomicWriteAsync(
                async (stream, token) => await stream.WriteAsync(first, token),
                new StorageRevision(1, null)));
        c.True("revision 失配时不创建目标文件", !File.Exists(absentPath), "出现 " + absentPath);
        c.NoTempArtifacts("缺失文件失配无 .tmp/.bak 残留", directory);

        var beforeWriterFailure = File.ReadAllBytes(path);
        await c.StorageFailsAsync("writer 抛错映射为 StorageException(IoError)",
            StorageError.IoError,
            () => file.AtomicWriteAsync((_, _) => throw new InvalidOperationException("模拟写失败")));
        c.Bytes("writer 抛错后原字节不变", beforeWriterFailure, File.ReadAllBytes(path));
        c.NoTempArtifacts("writer 抛错后无 .tmp/.bak 残留", directory);

        await c.StorageFailsAsync("writer 抛 StorageException 原样透出",
            StorageError.AccessDenied,
            () => file.AtomicWriteAsync((_, _) => throw new StorageException(StorageError.AccessDenied, "模拟拒绝")));
        c.Bytes("writer 抛 StorageException 后原字节不变", beforeWriterFailure, File.ReadAllBytes(path));
        c.NoTempArtifacts("writer 抛 StorageException 后无 .tmp/.bak 残留", directory);

        using var cts = new CancellationTokenSource();
        await c.ThrowsAssignableAsync<OperationCanceledException>(
            "writer 中途取消抛 OperationCanceledException",
            () => file.AtomicWriteAsync(
                async (stream, token) =>
                {
                    await stream.WriteAsync(first, token);
                    cts.Cancel();
                },
                cancellationToken: cts.Token));
        c.Bytes("中途取消后原字节不变", beforeWriterFailure, File.ReadAllBytes(path));
        c.NoTempArtifacts("中途取消后无 .tmp/.bak 残留", directory);

        await c.ThrowsAssignableAsync<OperationCanceledException>(
            "预取消令牌抛 OperationCanceledException",
            () => file.AtomicWriteAsync(
                async (stream, token) => await stream.WriteAsync(first, token),
                cancellationToken: new CancellationToken(canceled: true)));
        c.Bytes("预取消后原字节不变", beforeWriterFailure, File.ReadAllBytes(path));
        c.NoTempArtifacts("预取消后无 .tmp/.bak 残留", directory);
    }
}
