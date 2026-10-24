using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>LocalFolderRef：目录枚举（含取消）、FindFile/CreateFile 与非法名拒绝。</summary>
internal static class LocalFolderChecks
{
    private static readonly string[] InvalidNames =
    [
        "..",
        "../escape.jpg",
        "sub/b.jpg",
        "/abs.jpg",
        ".",
        "",
        "   ",
    ];

    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("LocalFolderRef");

        var directory = Path.Combine(root, "folder");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "a.jpg");
        File.WriteAllText(filePath, "a");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllText(Path.Combine(directory, "sub", "b.jpg"), "b");

        var folder = new LocalFolderRef(directory);
        c.Equal("Name", "folder", folder.Name);
        c.Equal("本地路径为绝对路径", directory, folder.TryGetLocalPath());
        c.True("Path 为 file Uri", folder.Path.IsFile, folder.Path.ToString());
        c.SameId("StorageId 为本地路径身份", StorageItemId.FromLocalPath(directory), folder.StorageId);
        c.Equal("Capabilities 为本地能力", StorageCapabilities.Local, folder.Capabilities);

        var items = new List<IStorageItemRef>();
        await foreach (var item in folder.GetItemsAsync())
        {
            items.Add(item);
        }

        c.Equal("枚举直接子项数量（不含孙项）", 2, items.Count);
        c.True("枚举包含文件 a.jpg",
            items.Any(item => item.Name == "a.jpg" && item is LocalFileRef),
            Describe(items));
        c.True("枚举包含子目录 sub",
            items.Any(item => item.Name == "sub" && item is LocalFolderRef),
            Describe(items));
        c.True("枚举不包含孙项 b.jpg",
            items.All(item => item.Name != "b.jpg"),
            Describe(items));

        var cancelled = false;
        try
        {
            await foreach (var _ in folder.GetItemsAsync(new CancellationToken(canceled: true)))
            {
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        c.True("目录枚举可取消", cancelled, "预取消令牌下未抛 OperationCanceledException");

        var found = await folder.FindFileAsync("a.jpg");
        c.True("FindFile 找到文件",
            found is LocalFileRef && found.TryGetLocalPath() == filePath && found.Name == "a.jpg",
            found is null ? "返回 null" : $"{found.GetType().Name} / {found.TryGetLocalPath()}");
        c.True("FindFile 缺失文件返回 null", await folder.FindFileAsync("nope.jpg") is null);
        c.True("FindFile 不把子目录当文件", await folder.FindFileAsync("sub") is null);

        foreach (var bad in InvalidNames)
        {
            await c.StorageFailsAsync($"FindFile 拒绝非法名 {Show(bad)}", StorageError.InvalidName,
                () => folder.FindFileAsync(bad));
            await c.StorageFailsAsync($"CreateFile 拒绝非法名 {Show(bad)}", StorageError.InvalidName,
                () => folder.CreateFileAsync(bad));
        }

        c.True("非法名未在上级目录生成文件",
            !File.Exists(Path.Combine(root, "escape.jpg")),
            "出现 " + Path.Combine(root, "escape.jpg"));
        c.True("非法名未在目录内生成文件",
            Directory.GetFileSystemEntries(directory).Length == 2,
            DescribeEntries(directory));

        var createdPath = Path.Combine(directory, "new.jpg");
        var created = await folder.CreateFileAsync("new.jpg");
        c.SameId("CreateFileAsync 身份", StorageItemId.FromLocalPath(createdPath), created.StorageId);
        c.True("CreateFileAsync 创建空文件",
            File.Exists(createdPath) && new FileInfo(createdPath).Length == 0);
        await c.ThrowsExactAsync<StorageException>("重复 CreateFileAsync 抛 StorageException",
            () => folder.CreateFileAsync("new.jpg"));

        var cancelledPath = Path.Combine(directory, "cancelled.jpg");
        await c.ThrowsAssignableAsync<OperationCanceledException>("CreateFileAsync 响应预取消令牌",
            () => folder.CreateFileAsync("cancelled.jpg", new CancellationToken(canceled: true)));
        c.True("取消后不生成文件", !File.Exists(cancelledPath), "出现 " + cancelledPath);

        var access = new LocalStorageAccessSessionFactory();
        using (access.Create(folder)) { }
        c.Pass("本地会话接受本地根");
        await c.StorageFailsAsync("本地会话拒绝 provider 根", StorageError.Unsupported, () =>
        {
            access.Create(new ProviderRef());
            return Task.CompletedTask;
        });
        using var lease = access.Create(folder);
        await c.StorageFailsAsync("本地会话拒绝 provider 附加项", StorageError.Unsupported, () =>
        {
            lease.Attach(new ProviderRef());
            return Task.CompletedTask;
        });
    }

    private sealed class ProviderRef : IStorageItemRef
    {
        public string Name => "provider";
        public Uri Path => new("content://provider/item");
        public string? TryGetLocalPath() => null;
    }

    private static string Show(string name) => name.Length == 0 ? "<空>" : "\"" + name + "\"";

    private static string Describe(IReadOnlyList<IStorageItemRef> items) =>
        "枚举到 " + string.Join(", ", items.Select(item => $"{item.Name}({item.GetType().Name})"));

    private static string DescribeEntries(string directory) =>
        "目录内 " + string.Join(", ", Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
}
