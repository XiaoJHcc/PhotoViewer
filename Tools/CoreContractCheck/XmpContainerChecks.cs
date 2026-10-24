using System.Reactive.Concurrency;
using System.Security.Cryptography;
using System.Text;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

internal static class XmpContainerChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("容器 XMP 写入、恢复与评分事务");
        UiDispatcher.Initialize(new InlineDispatcher());

        await CheckContainerAsync(c, root, "release/native-p2-fixtures/color-srgb.jpg", new JpegXmpContainerAdapter());
        await CheckContainerAsync(c, root, "release/native-p2-heif/color-srgb.heic", new HeifXmpContainerAdapter());
        await CheckContainerAsync(c, root, "TestCase/A7C01489.ARW", new TiffXmpContainerAdapter());
        await CheckContainerAsync(c, root, "release/native-p2-acceptance/toolbar-rating-commands.png", new PngXmpContainerAdapter());

        var jpegWithoutXmp = Path.Combine(root, "without-xmp.jpg");
        await File.WriteAllBytesAsync(jpegWithoutXmp, [0xFF, 0xD8, 0xFF, 0xD9]);
        await CheckContainerAsync(c, root, jpegWithoutXmp, new JpegXmpContainerAdapter(), copyFixture: false);

        var webpWithoutXmp = Path.Combine(root, "without-xmp.webp");
        await File.WriteAllBytesAsync(webpWithoutXmp,
            Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes(4)).Concat(Encoding.ASCII.GetBytes("WEBP")).ToArray());
        await CheckContainerAsync(c, root, webpWithoutXmp, new WebpXmpContainerAdapter(), copyFixture: false);

        var raw = CopyFixture("TestCase/A7C01489.ARW", root);
        var rawBefore = await File.ReadAllBytesAsync(raw);
        var sidecar = SidecarRatingStore.ForPhoto(new LocalFileRef(raw), new TiffXmpContainerAdapter());
        var sidecarReceipt = await sidecar.WriteAsync(3, safeMode: true);
        c.Equal("RAW sidecar 写入回读", 3, (await sidecar.ReadAsync()).Value);
        c.Equal("RAW sidecar 回执来源", XmpRatingSource.Sidecar, sidecarReceipt.After.Source);
        c.Bytes("创建 sidecar 不改 RAW 本体", rawBefore, await File.ReadAllBytesAsync(raw));
        await sidecar.RestoreAsync(sidecarReceipt);
        c.True("恢复删除新建 sidecar", !File.Exists(Path.Combine(root, "A7C01489.xmp")));
        c.Bytes("sidecar 恢复后 RAW 字节不变", rawBefore, await File.ReadAllBytesAsync(raw));

        await CheckRatingConcurrencyAsync(c, root);
        await CheckGroupFailureAsync(c, root, partial: false);
        await CheckGroupFailureAsync(c, root, partial: true);
    }

    private static async Task CheckContainerAsync(
        Checks c,
        string root,
        string fixture,
        IXmpContainerAdapter adapter,
        bool copyFixture = true)
    {
        var path = copyFixture ? CopyFixture(fixture, root) : fixture;
        var original = await File.ReadAllBytesAsync(path);
        var file = new LocalFileRef(path);
        var name = Path.GetFileName(path);
        var receipt = await adapter.WriteRatingAsync(file, 4, safeMode: true);
        c.Equal($"{name} 写后 Rating", 4, receipt.After.Value);
        var read = await adapter.ReadCanonicalPacketAsync(file);
        c.Equal($"{name} 写后语义回读", 4, XmpRatingReader.ReadPacket(read!.Data, XmpRatingSource.Embedded).Value);
        await adapter.RestoreAsync(file, receipt);
        c.Bytes($"{name} 恢复原始字节", original, await File.ReadAllBytesAsync(path));
    }

    private static async Task CheckRatingConcurrencyAsync(Checks c, string root)
    {
        var path = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root);
        var image = new ImageFile(new LocalFileRef(path));
        var group = PhotoGroupMap.Build([image], false, Rules()).GroupFor(image)!;
        var service = new RatingService();
        var results = await Task.WhenAll(
            service.SetRatingAsync(group, 3, true),
            service.SetRatingAsync(group, 4, true),
            service.SetRatingAsync(group, 5, true));
        var store = XmpRatingStoreFactory.Create(image.File);
        c.Equal("连按 3→4→5 最终磁盘为 5", 5, (await store.ReadAsync()).Value);
        c.Equal("最后一个请求应用成功", RatingWriteOutcome.Applied, results[^1].Outcome);
        c.True("排队请求不伪报全部应用", results.Take(results.Length - 1)
            .All(result => result.Outcome is RatingWriteOutcome.Superseded or RatingWriteOutcome.Applied));
    }

    private static async Task CheckGroupFailureAsync(Checks c, string root, bool partial)
    {
        var primaryPath = CopyFixture("release/native-p2-fixtures/color-srgb.jpg", root);
        var companionPath = Path.Combine(root, "color-srgb.heif");
        File.Copy(primaryPath, companionPath, overwrite: true);
        var primaryOriginal = await File.ReadAllBytesAsync(primaryPath);
        var primary = new ImageFile(new LocalFileRef(primaryPath));
        var companion = new ImageFile(new LocalFileRef(companionPath));
        var group = PhotoGroupMap.Build([primary, companion], true, Rules()).GroupFor(primary)!;
        var first = new FaultStore(primary.File, failRestore: partial);
        var second = new FaultStore(companion.File, failWrite: true);
        var service = new RatingService(file => ReferenceEquals(file, primary.File) ? first : second);
        var result = await service.SetRatingAsync(group, 4, safeMode: true);
        c.Equal(partial ? "补偿失败返回 Partial" : "伴侣失败返回 RolledBack",
            partial ? RatingWriteOutcome.Partial : RatingWriteOutcome.RolledBack, result.Outcome);
        if (partial)
        {
            var actual = await XmpRatingStoreFactory.Create(primary.File).ReadAsync();
            c.Equal("Partial 主文件按真实写入值读取", 4, actual.Value);
            c.True("Partial 包含补偿错误", result.Files.Any(file => file.CompensationError != null));
        }
        else
        {
            c.Bytes("伴侣失败时代表文件恢复原字节", primaryOriginal, await File.ReadAllBytesAsync(primaryPath));
        }
    }

    private static PhotoFormatRule[] Rules() =>
    [
        new("jpg", "JPG", [".jpg"]),
        new("heif", "HEIF", [".heif"]),
    ];

    private static string CopyFixture(string relative, string root)
    {
        var path = Path.Combine(root, Path.GetFileName(relative));
        if (!File.Exists(relative)) throw new FileNotFoundException("缺少验收夹具", relative);
        File.Copy(relative, path, overwrite: true);
        return path;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public IScheduler MainThreadScheduler { get; } = ImmediateScheduler.Instance;
        public bool CheckAccess() => true;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());
    }

    private sealed class FaultStore : IXmpRatingStore
    {
        private readonly ContainerRatingStore _inner;
        private readonly bool _failWrite;
        private readonly bool _failRestore;

        public FaultStore(IFileRef file, bool failWrite = false, bool failRestore = false)
        {
            _inner = new ContainerRatingStore(file, new JpegXmpContainerAdapter());
            _failWrite = failWrite;
            _failRestore = failRestore;
        }

        public Task<XmpRatingSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(cancellationToken);

        public Task<XmpRatingWriteReceipt> WriteAsync(int rating, bool safeMode, CancellationToken cancellationToken = default)
        {
            if (_failWrite) throw new StorageException(StorageError.IoError, "注入写失败");
            return _inner.WriteAsync(rating, safeMode, cancellationToken);
        }

        public Task RestoreAsync(XmpRatingWriteReceipt receipt, CancellationToken cancellationToken = default)
        {
            if (_failRestore) throw new StorageException(StorageError.IoError, "注入补偿失败");
            return _inner.RestoreAsync(receipt, cancellationToken);
        }
    }
}
