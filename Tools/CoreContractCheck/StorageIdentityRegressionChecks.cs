using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>
/// 本地存储身份回归：macOS 不区分大小写卷上同一文件的不同拼写必须得到同一 <see cref="StorageItemId"/>，
/// 身份取文件系统实际拼写（不做整体小写），兼容区分大小写卷、Unicode 与尚不存在的路径；
/// 身份在引用建立时缓存，<see cref="IStorageItemRef.StorageId"/> 读取不再访问文件系统。
/// 其余平台保持原有词法身份（Windows 统一大写），只核对身份与地址分离。
/// 所有夹具都建在临时目录内并随临时根目录整体清理。
/// </summary>
internal static class StorageIdentityRegressionChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("存储身份回归");

        var directory = Path.Combine(root, "identity");
        Directory.CreateDirectory(directory);
        var lowerPath = Path.Combine(directory, "a.jpg");
        File.WriteAllText(lowerPath, "lower");
        var upperPath = Path.Combine(directory, "A.JPG");

        var lowerRef = new LocalFileRef(lowerPath);
        var folder = new LocalFolderRef(directory);
        var member = (await EnumerateAsync(folder)).Single(item => item.Name == "a.jpg");
        c.SameId("枚举成员与显式引用身份一致", lowerRef.StorageId, member.StorageId);
        c.SameId("目录引用身份与 FromLocalPath 一致", StorageItemId.FromLocalPath(directory), folder.StorageId);
        c.True("身份路径仍是绝对路径", Path.IsPathFullyQualified(IdentityPath(member.StorageId)),
            member.StorageId.Value);
        c.Equal("身份保留文件名", OperatingSystem.IsWindows() ? "A.JPG" : "a.jpg",
            Path.GetFileName(IdentityPath(member.StorageId)));

        if (!OperatingSystem.IsMacOS())
        {
            c.Skip("macOS 文件系统拼写身份", "只有 macOS 用 realpath 取实际拼写；其余平台保持词法身份");
            return;
        }

        // 另一种大小写能否打开同一文件，就是卷是否区分大小写的现成判据。
        bool caseInsensitive = File.Exists(upperPath);
        var explicitUpper = new LocalFileRef(upperPath);
        if (caseInsensitive)
        {
            c.SameId("不区分大小写卷：显式目标与枚举成员同一身份", member.StorageId, explicitUpper.StorageId);
            c.SameId("不区分大小写卷：FromLocalPath 与枚举成员同一身份",
                member.StorageId, StorageItemId.FromLocalPath(upperPath));
            c.SameId("不区分大小写卷：file URI 与枚举成员同一身份",
                member.StorageId, StorageItemId.FromUri(new Uri(upperPath)));

            var spellings = new[]
            {
                member.StorageId,
                explicitUpper.StorageId,
                StorageItemId.FromLocalPath(upperPath),
                StorageItemId.FromUri(new Uri(upperPath)),
            };
            c.Equal("不区分大小写卷：四种写法只算一个身份", 1, spellings.Distinct().Count());
            c.Equal("显式目标保留调用方地址拼写", upperPath, explicitUpper.TryGetLocalPath());
            c.Equal("显式目标保留调用方文件名", "A.JPG", explicitUpper.Name);
            c.SameId("不区分大小写卷：目录大写拼写与枚举目录同一身份",
                folder.StorageId, new LocalFolderRef(directory.ToUpperInvariant()).StorageId);
        }
        else
        {
            File.WriteAllText(upperPath, "upper");
            c.True("区分大小写卷：大小写不同是不同文件",
                explicitUpper.StorageId != member.StorageId,
                $"两者都是 {member.StorageId.Value}");
            c.SameId("区分大小写卷：显式目标与自身拼写身份一致",
                StorageItemId.FromLocalPath(upperPath), explicitUpper.StorageId);
            c.Equal("区分大小写卷：枚举到两个成员", 2, (await EnumerateAsync(folder)).Count);
        }

        VerifyOnDiskSpelling(c, root);
        VerifyUnicode(c, directory);
        VerifyMissingPath(c, directory);
        if (caseInsensitive)
        {
            VerifyCachedIdentity(c, root);
        }
        else
        {
            c.Skip("身份在构造时缓存", "当前卷区分大小写，无法用大小写不同构造同一文件的两个引用");
        }

        VerifySymlinkHandling(c, directory, lowerPath);
    }

    /// <summary>身份取磁盘实际拼写：大写文件名经小写地址访问时仍报磁盘那一份大小写。</summary>
    private static void VerifyOnDiskSpelling(Checks c, string root)
    {
        var mixedDirectory = Path.Combine(root, "identity-case");
        Directory.CreateDirectory(mixedDirectory);
        var mixedPath = Path.Combine(mixedDirectory, "Photo.JPG");
        File.WriteAllText(mixedPath, "mixed");
        var fromLower = new LocalFileRef(Path.Combine(mixedDirectory, "photo.jpg"));
        if (!File.Exists(Path.Combine(mixedDirectory, "photo.jpg")))
        {
            c.Skip("身份取磁盘大小写（不整体小写）", "当前卷区分大小写，小写拼写不是同一文件");
            return;
        }

        c.Equal("身份取磁盘大小写（不整体小写）", "Photo.JPG",
            Path.GetFileName(IdentityPath(fromLower.StorageId)));
        c.Equal("同一文件经小写地址仍报磁盘文件名的身份",
            new LocalFileRef(mixedPath).StorageId, fromLower.StorageId);
    }

    private static void VerifyUnicode(Checks c, string directory)
    {
        const string nfc = "caf\u00e9.jpg";
        const string nfd = "cafe\u0301.jpg";
        var nfdPath = Path.Combine(directory, nfd);
        File.WriteAllText(nfdPath, "unicode");
        var nfcPath = Path.Combine(directory, nfc);
        if (!File.Exists(nfcPath))
        {
            c.Skip("Unicode 等价写法同一身份", "当前卷不把 NFC/NFD 视作同一文件");
            return;
        }

        var fromNfd = new LocalFileRef(nfdPath);
        var fromNfc = new LocalFileRef(nfcPath);
        c.SameId("Unicode 等价写法同一身份", fromNfd.StorageId, fromNfc.StorageId);
        var stored = Path.GetFileName(IdentityPath(fromNfd.StorageId));
        c.True("Unicode 身份取磁盘这一份拼写", stored == nfd || stored == nfc, $"实际 {stored}");
    }

    private static void VerifyMissingPath(Checks c, string directory)
    {
        var missingPath = Path.Combine(directory, "Missing.XMP");
        var missingId = new LocalFileRef(missingPath).StorageId;
        c.True("尚不存在的路径也有稳定身份",
            missingId.Value.StartsWith("file:", StringComparison.Ordinal) &&
            Path.IsPathFullyQualified(IdentityPath(missingId)),
            missingId.Value);
        c.Equal("尚不存在的路径保留调用方大小写", "Missing.XMP",
            Path.GetFileName(IdentityPath(missingId)));
        c.SameId("尚不存在的路径两次构造成同一身份",
            missingId, new LocalFileRef(missingPath).StorageId);

        if (Directory.Exists(directory.ToUpperInvariant()))
        {
            c.SameId("尚不存在的路径按父目录实际拼写归并",
                missingId, new LocalFileRef(Path.Combine(directory.ToUpperInvariant(), "Missing.XMP")).StorageId);
        }
        else
        {
            c.Skip("尚不存在的路径按父目录实际拼写归并", "当前卷区分大小写，大写父目录不是同一目录");
        }

        File.WriteAllText(missingPath, "created");
        c.SameId("文件创建后身份与创建前一致",
            missingId, new LocalFileRef(missingPath).StorageId);
    }

    private static void VerifyCachedIdentity(Checks c, string root)
    {
        var directory = Path.Combine(root, "identity-cache");
        Directory.CreateDirectory(directory);
        var actualPath = Path.Combine(directory, "Cached.JPG");
        File.WriteAllText(actualPath, "cached");
        var wrongCasePath = Path.Combine(directory, "cached.jpg");
        var cached = new LocalFileRef(wrongCasePath);
        var identity = cached.StorageId;
        c.SameId("身份在构造时取自实际拼写", new LocalFileRef(actualPath).StorageId, identity);

        File.Delete(actualPath);
        c.SameId("StorageId 读取不重新探测文件系统", identity, cached.StorageId);
        c.True("删除后新建引用退回给定拼写（证明上面的读取没有重新探测）",
            new LocalFileRef(wrongCasePath).StorageId != identity, $"两者都是 {identity.Value}");
    }

    private static void VerifySymlinkHandling(Checks c, string directory, string targetPath)
    {
        var linkPath = Path.Combine(directory, "link.jpg");
        try
        {
            if (!File.Exists(linkPath))
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
        }
        catch (Exception exception)
        {
            c.Skip("文件符号链接保持自身身份", "无法创建符号链接：" + exception.Message);
            return;
        }

        var link = new LocalFileRef(linkPath);
        var target = new LocalFileRef(targetPath);
        c.True("文件符号链接不并到目标文件身份", link.StorageId != target.StorageId,
            $"链接 {link.StorageId.Value} / 目标 {target.StorageId.Value}");
        c.Equal("符号链接身份保留链接自身文件名", "link.jpg",
            Path.GetFileName(IdentityPath(link.StorageId)));
    }

    private static async Task<List<IStorageItemRef>> EnumerateAsync(IFolderRef folder)
    {
        var items = new List<IStorageItemRef>();
        await foreach (var item in folder.GetItemsAsync())
        {
            items.Add(item);
        }

        return items;
    }

    private static string IdentityPath(StorageItemId id) => id.Value["file:".Length..];
}
