using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>本地删除：删除已存在/缺失文件、重复删除、取消不产生副作用。</summary>
internal static class DeleteChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("LocalFileRef.DeleteAsync");

        var directory = Path.Combine(root, "delete");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "x.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        var file = new LocalFileRef(path);
        await file.DeleteAsync();
        c.True("删除后文件不存在", !File.Exists(path), "仍存在 " + path);

        var properties = await file.GetBasicPropertiesAsync();
        c.True("删除后 Exists=false", !properties.Exists);

        await file.DeleteAsync();
        c.Pass("重复删除已不存在文件不抛错");

        var missing = new LocalFileRef(Path.Combine(directory, "never.bin"));
        await missing.DeleteAsync();
        c.Pass("删除从未存在的文件不抛错");

        File.WriteAllBytes(path, [9, 8, 7]);
        await c.ThrowsAssignableAsync<OperationCanceledException>("DeleteAsync 响应预取消令牌",
            () => file.DeleteAsync(new CancellationToken(canceled: true)));
        c.True("取消删除后文件仍在", File.Exists(path), "文件被删除 " + path);
        c.Bytes("取消删除后内容不变", [9, 8, 7], File.ReadAllBytes(path));
    }
}
