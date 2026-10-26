using System.Text;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>
/// 评分写入事务的回归项：预检写权限探测、同值 NoChange 的复核、写入后来源集合重发现、
/// 多份内嵌 XMP 中 Rated 与 Missing 的冲突判定、Unreadable 状态与只读来源整组零修改。
/// </summary>
internal static class RatingTransactionRegressionChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        await CheckReadOnlySidecarAsync(c, root);
        await CheckNoChangeExternalChangeAsync(c, root);
        await CheckNoChangeSameValueRewriteAsync(c, root);
        await CheckMultiPacketMissingConflictAsync(c, root);
        await CheckFinalSetAddedAsync(c, root);
        await CheckFinalSetRemovedAsync(c, root);
        await CheckUnreadableStateAsync(c, root);
    }

    // ---------------------------------------------------------------- 只读来源预检

    private static async Task CheckReadOnlySidecarAsync(Checks c, string root)
    {
        c.Section("预检写权限探测与只读 sidecar 整组零修改");

        var rules = Rules(("jpg", "JPG", ".jpg"));

        // 预检本身必须零写入：字节、长度、mtime 都不能变。
        var probePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "probe-prepare.jpg");
        var probeBytes = await File.ReadAllBytesAsync(probePath);
        var probeModified = File.GetLastWriteTimeUtc(probePath);
        var probeStore = XmpRatingStoreFactory.Create(new LocalFileRef(probePath));
        var probePreparation = await probeStore.PrepareAsync(3, safeMode: false);
        c.True("同值预检仍标记 NoChange", probePreparation.NoChange);
        c.Bytes("预检写权限探测不改字节", probeBytes, await File.ReadAllBytesAsync(probePath));
        c.Equal("预检写权限探测不改长度", (long)probeBytes.Length, new FileInfo(probePath).Length);
        c.Equal("预检写权限探测不改 mtime", probeModified, File.GetLastWriteTimeUtc(probePath));

        if (OperatingSystem.IsWindows())
        {
            c.Skip("只读 sidecar 预检失败", "Windows 分支需在 Windows 上验证，本机不冒充");
            return;
        }

        // 值不同：只读 sidecar 必须让整组在首次修改前失败，安全模式关闭也不例外。
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "readonly-side.jpg");
        var photoBytes = await File.ReadAllBytesAsync(photoPath);
        var sidecarPath = Path.Combine(root, "readonly-side.xmp");
        var sidecarBytes = Sidecar("5");
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes);
        File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead);
        try
        {
            var photo = new ImageFile(new LocalFileRef(photoPath));
            var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;
            var result = await new RatingService().SetRatingAsync(group, 4, safeMode: false);
            c.Equal("只读 sidecar（安全模式关闭）整组失败", RatingWriteOutcome.RolledBack, result.Outcome);
            c.True("只读 sidecar 不产生物理修改", !result.AnyFileModified);
            c.Bytes("只读 sidecar 失败后照片不变", photoBytes, await File.ReadAllBytesAsync(photoPath));
            c.Bytes("只读 sidecar 失败后 sidecar 不变", sidecarBytes, await File.ReadAllBytesAsync(sidecarPath));
            c.Equal("只读 sidecar 失败后权限不变", UnixFileMode.UserRead, File.GetUnixFileMode(sidecarPath));
            c.True("只读 sidecar 失败原因指明预检",
                result.FailureReason?.Contains("预检", StringComparison.Ordinal) == true);
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // 值相同：同值 NoChange 也要证明来源可写，不能被“无需写入”冒充成功。
        var samePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "readonly-same.jpg");
        var sameBytes = await File.ReadAllBytesAsync(samePath);
        var sameSidecarPath = Path.Combine(root, "readonly-same.xmp");
        var sameSidecarBytes = Sidecar("3");
        await File.WriteAllBytesAsync(sameSidecarPath, sameSidecarBytes);
        File.SetUnixFileMode(sameSidecarPath, UnixFileMode.UserRead);
        try
        {
            var samePhoto = new ImageFile(new LocalFileRef(samePath));
            var sameGroup = PhotoGroupMap.Build([samePhoto], false, rules).GroupFor(samePhoto)!;
            var sameResult = await new RatingService().SetRatingAsync(sameGroup, 3, safeMode: false);
            c.Equal("只读同值 sidecar 不返回 NoChange", RatingWriteOutcome.RolledBack, sameResult.Outcome);
            c.True("只读同值 sidecar 不声称成功", !sameResult.IsApplied);
            c.True("只读同值 sidecar 没有物理修改", !sameResult.AnyFileModified);
            c.Bytes("只读同值失败后照片不变", sameBytes, await File.ReadAllBytesAsync(samePath));
            c.Bytes("只读同值失败后 sidecar 不变", sameSidecarBytes, await File.ReadAllBytesAsync(sameSidecarPath));
        }
        finally
        {
            File.SetUnixFileMode(sameSidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ---------------------------------------------------------------- 同值中途外改

    private static async Task CheckNoChangeExternalChangeAsync(Checks c, string root)
    {
        c.Section("同值 NoChange 期间外部改值不得成功");

        var rules = Rules(("jpg", "JPG", ".jpg"));
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "nochange-ext.jpg");
        var photoBytes = await File.ReadAllBytesAsync(photoPath);
        var sidecarPath = Path.Combine(root, "nochange-ext.xmp");
        await File.WriteAllBytesAsync(sidecarPath, Sidecar("3"));

        var photo = new ImageFile(new LocalFileRef(photoPath));
        var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;

        // sidecar 预检返回 NoChange 之后，绕过事务直接改写磁盘为 5。
        var service = new RatingService(
            XmpRatingStoreFactory.Create,
            file => new HookedStore(
                XmpRatingStoreFactory.CreateSidecar(file),
                afterPrepare: () => File.WriteAllBytesAsync(sidecarPath, Sidecar("5"))));

        var result = await service.SetRatingAsync(group, 3, safeMode: true);
        c.Equal("同值中途外改不得返回 NoChange", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("同值中途外改不声称成功", !result.IsApplied);
        c.True("同值中途外改没有物理修改", !result.AnyFileModified);
        c.Bytes("同值中途外改保留照片字节", photoBytes, await File.ReadAllBytesAsync(photoPath));
        c.Equal("同值中途外改保留外部写入值", 5,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);
    }

    private static async Task CheckNoChangeSameValueRewriteAsync(Checks c, string root)
    {
        c.Section("同值 NoChange 期间来源被同值改写也不得成功");

        var rules = Rules(("jpg", "JPG", ".jpg"));
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "nochange-rewrite.jpg");
        var sidecarPath = Path.Combine(root, "nochange-rewrite.xmp");
        await File.WriteAllBytesAsync(sidecarPath, Sidecar("3"));

        // 值仍是 3，但整个包被外部重写（这里加了 dc:title）：revision 变化必须被发现。
        var rewritten = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\">"
            + "<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">外部重写</dc:title></rdf:Description>");

        var photo = new ImageFile(new LocalFileRef(photoPath));
        var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;
        var service = new RatingService(
            XmpRatingStoreFactory.Create,
            file => new HookedStore(
                XmpRatingStoreFactory.CreateSidecar(file),
                afterPrepare: () => File.WriteAllBytesAsync(sidecarPath, rewritten)));

        var result = await service.SetRatingAsync(group, 3, safeMode: true);
        c.Equal("同值改写不得返回 NoChange", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("同值改写没有物理修改", !result.AnyFileModified);
        c.Bytes("同值改写保留外部字节", rewritten, await File.ReadAllBytesAsync(sidecarPath));
        c.Equal("同值改写后外部值仍为 3", 3,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);
    }

    // ---------------------------------------------------------------- 多份内嵌 XMP

    private static async Task CheckMultiPacketMissingConflictAsync(Checks c, string root)
    {
        c.Section("多份内嵌 XMP：一包 Rated 一包 Missing 判冲突");

        var fixturePath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "multi-missing-src.jpg");
        var fixture = await File.ReadAllBytesAsync(fixturePath);
        var (offset, length) = FindCanonicalXmpSegment(fixture);
        var ratedSegment = fixture.AsSpan(offset, length).ToArray();
        var missingSegment = BuildCanonicalApp1(Packet(
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">"
            + "<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">缺 Rating 的包</dc:title></rdf:Description>",
            padding: 512));

        var combined = new byte[fixture.Length - length + ratedSegment.Length + missingSegment.Length];
        fixture.AsSpan(0, offset).CopyTo(combined);
        ratedSegment.CopyTo(combined.AsSpan(offset));
        missingSegment.CopyTo(combined.AsSpan(offset + ratedSegment.Length));
        fixture.AsSpan(offset + length).CopyTo(combined.AsSpan(offset + ratedSegment.Length + missingSegment.Length));

        var path = Path.Combine(root, "multi-missing.jpg");
        await File.WriteAllBytesAsync(path, combined);
        var file = new LocalFileRef(path);

        var probe = await XmpRatingReader.ProbeEmbeddedAsync(file);
        c.Equal("两份 canonical XMP 都被枚举", 2, probe.XmpDirectoryCount);

        var embedded = await XmpRatingReader.ReadEmbeddedAsync(file);
        c.Equal("一包 Rated 一包 Missing 判 Conflict", XmpRatingState.Conflict, embedded.State);
        c.Equal("冲突不挑一个值冒充一致", null, embedded.ValueOrNull);

        var exif = await ExifLoader.LoadExifDataAsync(file);
        c.True("完整 EXIF 读取保留冲突状态",
            exif is { RatingState: XmpRatingState.Conflict } && exif.Rating is null);

        var image = new ImageFile(file);
        image.ApplyRatingSources(exif!.RatingSources);
        var group = PhotoGroupMap.Build([image], false, Rules(("jpg", "JPG", ".jpg"))).GroupFor(image)!;
        c.True("Rated+Missing 多包组视为星级冲突", group.HasRatingConflict);

        var write = await new RatingService().SetRatingAsync(group, 4, safeMode: true);
        c.Equal("多份 XMP 冲突时写入预检失败", RatingWriteOutcome.RolledBack, write.Outcome);
        c.Bytes("多份 XMP 冲突预检零修改", combined, await File.ReadAllBytesAsync(path));
    }

    // ---------------------------------------------------------------- 最终来源集合

    private static async Task CheckFinalSetAddedAsync(Checks c, string root)
    {
        c.Section("写入后新增 sidecar 不得算成功（写后回滚）");

        var rules = Rules(("jpg", "JPG", ".jpg"));
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "added-source.jpg");
        var photoBytes = await File.ReadAllBytesAsync(photoPath);
        var sidecarPath = Path.Combine(root, "added-source.xmp");
        if (File.Exists(sidecarPath)) File.Delete(sidecarPath);

        var photo = new ImageFile(new LocalFileRef(photoPath));
        var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;

        // 内嵌来源预检通过后、提交之前，另一个程序新建了同名 sidecar。
        var service = new RatingService(
            file => new HookedStore(
                XmpRatingStoreFactory.Create(file),
                afterPrepare: () => File.WriteAllBytesAsync(sidecarPath, Sidecar("5"))),
            XmpRatingStoreFactory.CreateSidecar);

        var result = await service.SetRatingAsync(group, 4, safeMode: true);
        c.Equal("预检后新增 sidecar 不得 Applied", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("新增 sidecar 后已写来源被补偿", result.Files
            .SelectMany(file => file.Sources)
            .Any(source => source.Written && source.CompensationError == null));
        c.Bytes("新增 sidecar 后照片恢复原字节", photoBytes, await File.ReadAllBytesAsync(photoPath));
        c.Equal("新增 sidecar 未被本次写入", 5,
            (await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath))).NumericValue);
        c.True("新增 sidecar 失败原因说明来源集合变化",
            result.FailureReason?.Contains("新增", StringComparison.Ordinal) == true);
    }

    private static async Task CheckFinalSetRemovedAsync(Checks c, string root)
    {
        c.Section("写入后 sidecar 消失不得算成功");

        var rules = Rules(("jpg", "JPG", ".jpg"));
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "removed-source.jpg");
        var photoBytes = await File.ReadAllBytesAsync(photoPath);
        var sidecarPath = Path.Combine(root, "removed-source.xmp");
        await File.WriteAllBytesAsync(sidecarPath, Sidecar("3"));

        var photo = new ImageFile(new LocalFileRef(photoPath));
        var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;

        // sidecar 预检返回 NoChange 之后被外部删除；全组本应 NoChange，但来源已消失。
        var service = new RatingService(
            XmpRatingStoreFactory.Create,
            file => new HookedStore(
                XmpRatingStoreFactory.CreateSidecar(file),
                afterPrepare: () =>
                {
                    File.Delete(sidecarPath);
                    return Task.CompletedTask;
                }));

        var result = await service.SetRatingAsync(group, 3, safeMode: true);
        c.Equal("预检后 sidecar 消失不得 NoChange", RatingWriteOutcome.RolledBack, result.Outcome);
        c.True("sidecar 消失不声称成功", !result.IsApplied);
        c.True("sidecar 消失不产生物理修改", !result.AnyFileModified);
        c.Bytes("sidecar 消失后照片不变", photoBytes, await File.ReadAllBytesAsync(photoPath));
        c.True("sidecar 仍未重新创建", !File.Exists(sidecarPath));
        c.True("sidecar 消失失败原因说明来源消失",
            result.FailureReason?.Contains("消失", StringComparison.Ordinal) == true);
    }

    // ---------------------------------------------------------------- Unreadable

    private static async Task CheckUnreadableStateAsync(Checks c, string root)
    {
        c.Section("Unreadable 与 Invalid 的语义区分");

        var rules = Rules(("jpg", "JPG", ".jpg"));
        var photoPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root, "unreadable.jpg");
        var sidecarPath = Path.Combine(root, "unreadable.xmp");
        await File.WriteAllBytesAsync(sidecarPath, Sidecar("3"));

        if (OperatingSystem.IsWindows())
        {
            c.Skip("不可读 sidecar 映射 Unreadable", "Windows 分支需在 Windows 上验证，本机不冒充");
        }
        else
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.None);
            try
            {
                var source = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath));
                c.Equal("读取被拒的 sidecar 是 Unreadable", XmpRatingState.Unreadable, source.State);
                c.True("Unreadable 带明确原因", source.Error != null);
                c.Equal("Unreadable 不提供可比较数值", null, source.Snapshot.ValueOrNull);

                var photo = new ImageFile(new LocalFileRef(photoPath));
                photo.ApplyRatingSources(
                [
                    new RatingSourceState(XmpRatingSource.Embedded, photo.File,
                        new XmpRatingSnapshot(XmpRatingState.Rated, 3, XmpRatingSource.Embedded)),
                    source,
                ]);
                var group = PhotoGroupMap.Build([photo], false, rules).GroupFor(photo)!;
                c.True("可读来源 + Unreadable 来源视为冲突", group.HasRatingConflict);

                var unreadableFile = new ImageFile(new LocalFileRef(Path.Combine(root, "unreadable-only.jpg")));
                unreadableFile.ApplyRatingSources(
                [
                    new RatingSourceState(XmpRatingSource.Embedded, unreadableFile.File,
                        XmpRatingSnapshot.Unreadable(XmpRatingSource.Embedded, "读取被拒")),
                ]);
                c.Equal("Unreadable 代表状态", XmpRatingState.Unreadable, unreadableFile.RatingState);
                c.Equal("Unreadable 代表数值为 0", 0, unreadableFile.Rating);
                c.True("单个 Unreadable 代表视为冲突",
                    PhotoGroupMap.Build([unreadableFile], false, rules).GroupFor(unreadableFile)!.HasRatingConflict);
            }
            finally
            {
                File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        // 内容损坏仍按 Invalid：包读得到、但语义不可用，与“读不进来”分开。
        var corruptPath = Path.Combine(root, "corrupt-state.xmp");
        await File.WriteAllBytesAsync(corruptPath, Encoding.UTF8.GetBytes("<x:xmpmeta><rdf:RDF>"));
        var corrupt = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(corruptPath));
        c.Equal("损坏包仍是 Invalid", XmpRatingState.Invalid, corrupt.State);

        var outOfRangePath = Path.Combine(root, "out-of-range.xmp");
        await File.WriteAllBytesAsync(outOfRangePath, Sidecar("9"));
        var outOfRange = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(outOfRangePath));
        c.Equal("可解析的越界值是 Invalid", XmpRatingState.Invalid, outOfRange.State);
    }

    // ---------------------------------------------------------------- 测试替身与工具

    /// <summary>在预检/提交返回后插入一次外部动作的存储包装（只用于制造竞态，不改写事务语义）。</summary>
    private sealed class HookedStore : IXmpRatingStore
    {
        private readonly IXmpRatingStore _inner;
        private readonly Func<Task>? _afterPrepare;

        public HookedStore(IXmpRatingStore inner, Func<Task>? afterPrepare = null)
        {
            _inner = inner;
            _afterPrepare = afterPrepare;
        }

        public Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(cancellationToken);

        public async Task<XmpRatingPreparation> PrepareAsync(
            int rating,
            bool safeMode,
            CancellationToken cancellationToken = default)
        {
            var preparation = await _inner.PrepareAsync(rating, safeMode, cancellationToken).ConfigureAwait(false);
            if (_afterPrepare != null) await _afterPrepare().ConfigureAwait(false);
            return preparation;
        }

        public Task<XmpRatingWriteReceipt> CommitAsync(
            XmpRatingPreparation preparation,
            CancellationToken cancellationToken = default) =>
            _inner.CommitAsync(preparation, cancellationToken);

        public Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default) =>
            _inner.RestoreAsync(receipt, cancellationToken);
    }

    private static (int Offset, int Length) FindCanonicalXmpSegment(byte[] jpeg)
    {
        var index = 2; // 跳过 SOI
        while (index + 4 <= jpeg.Length)
        {
            if (jpeg[index] != 0xFF) break;
            var marker = jpeg[index + 1];
            if (marker == 0xDA) break;
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                index += 2;
                continue;
            }

            var length = (jpeg[index + 2] << 8) | jpeg[index + 3];
            if (length < 2) break;

            var headerStart = index + 4;
            if (marker == 0xE1 &&
                headerStart + 29 <= jpeg.Length &&
                jpeg.AsSpan(headerStart, 29).SequenceEqual("http://ns.adobe.com/xap/1.0/\0"u8))
            {
                return (index, length + 2);
            }

            index += 2 + length;
        }

        throw new InvalidOperationException("JPEG 夹具缺少 canonical XMP APP1");
    }

    private static byte[] BuildCanonicalApp1(byte[] packet)
    {
        var header = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        var length = 2 + header.Length + packet.Length;
        var segment = new byte[2 + length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)(length & 0xFF);
        header.CopyTo(segment.AsSpan(4));
        packet.CopyTo(segment.AsSpan(4 + header.Length));
        return segment;
    }

    private static byte[] Packet(string description, int padding = 4096)
    {
        var text = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + description
            + "</rdf:RDF></x:xmpmeta>"
            + new string(' ', padding);
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Sidecar(string rating) =>
        Packet($"<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"{rating}\" />");

    private static PhotoFormatRule[] Rules(params (string Id, string Name, string Extension)[] formats) =>
        formats
            .Select(item => new PhotoFormatRule(item.Id, item.Name, [item.Extension]))
            .ToArray();

    private static string CopyFixture(string relative, string root, string name)
    {
        if (!File.Exists(relative)) throw new FileNotFoundException("缺少验收夹具", relative);
        var path = Path.Combine(root, name);
        File.Copy(relative, path, overwrite: true);
        return path;
    }
}
