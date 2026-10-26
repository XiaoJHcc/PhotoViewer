using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>StorageItemId：本地路径规范化、file URI 归一、非 file URI 原样保留。</summary>
internal static class StorageItemIdChecks
{
    public static void Run(Checks c, string root)
    {
        c.Section("StorageItemId");

        var expectedPath = Path.Combine(root, "a", "b", "photo.jpg");
        var id = StorageItemId.FromLocalPath(expectedPath);
        c.True("本地路径以 file: 前缀", id.Value.StartsWith("file:", StringComparison.Ordinal), $"实际 {id.Value}");
        var identityPath = id.Value["file:".Length..];
        c.True("本地路径身份为绝对路径", Path.IsPathFullyQualified(identityPath), identityPath);
        c.Equal("本地路径身份已规范化", Path.GetFullPath(identityPath), identityPath);
        c.Equal("本地路径身份保留文件名", "photo.jpg", Path.GetFileName(identityPath));
        c.SameId("身份路径再次构造得到同一身份", id, StorageItemId.FromLocalPath(identityPath));

        var messy = Path.Combine(root, "a", ".", "c", "..", "b", "photo.jpg");
        c.SameId("本地路径折叠 . 与 ..", id, StorageItemId.FromLocalPath(messy));

        var relative = Path.Combine("contract-check", "relative", "photo.jpg");
        c.SameId("相对路径与绝对路径同身份",
            StorageItemId.FromLocalPath(Path.GetFullPath(relative)),
            StorageItemId.FromLocalPath(relative));

        c.Equal("ToString 与 Value 一致", id.Value, id.ToString());

        if (OperatingSystem.IsWindows())
        {
            var upper = "file:" + Path.GetFullPath(expectedPath).ToUpperInvariant();
            c.Equal("Windows 本地路径大写归一", upper, id.Value);
        }
        else
        {
            c.Skip("Windows 本地路径大写归一", "当前非 Windows，不在本机冒充验证");
        }

        var localUri = new Uri(expectedPath);
        c.True("本地路径生成的 Uri 为 file", localUri.IsFile, localUri.ToString());
        c.SameId("file URI 归一为本地路径", id, StorageItemId.FromUri(localUri));
        c.SameId("含 .. 的 file URI 归一",
            StorageItemId.FromLocalPath(Path.Combine(root, "x.jpg")),
            StorageItemId.FromUri(new Uri("file://" + root + "/a/../x.jpg")));

        var content = StorageItemId.FromUri(new Uri("content://com.example.photos/image/42%3Fsize=large"));
        c.Equal("content URI 原样保留", "content://com.example.photos/image/42%3Fsize=large", content.Value);
        c.True("content URI 不被写成 file:",
            !content.Value.StartsWith("file:", StringComparison.Ordinal), content.Value);

        var media = StorageItemId.FromUri(new Uri("content://media/external/images/media/1"));
        c.Equal("content URI 带 authority 原样保留", "content://media/external/images/media/1", media.Value);

        var https = StorageItemId.FromUri(new Uri("https://example.com/photos/x.jpg"));
        c.Equal("https URI 原样保留", "https://example.com/photos/x.jpg", https.Value);

        var provider = StorageItemId.FromProvider("xmp", "media/1");
        c.Equal("provider 身份拼接", "xmp:media/1", provider.Value);

        c.ThrowsExact<ArgumentNullException>("FromUri(null) 拒绝", () => StorageItemId.FromUri(null!));
        c.ThrowsExact<ArgumentException>("FromProvider 空 provider 拒绝",
            () => StorageItemId.FromProvider("", "id"));
        c.ThrowsExact<ArgumentException>("FromProvider 空白 id 拒绝",
            () => StorageItemId.FromProvider("xmp", "   "));
    }
}
