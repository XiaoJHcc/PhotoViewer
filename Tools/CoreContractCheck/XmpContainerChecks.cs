using System.Reactive.Concurrency;
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
        c.Section("容器 XMP 只经评分存储读写");
        UiDispatcher.Initialize(new InlineDispatcher());

        await CheckContainerAsync(c, root, "release/native-p2-fixtures/color-srgb.jpg", new JpegXmpContainerAdapter());
        await CheckContainerAsync(c, root, "release/native-p2-heif/color-srgb.heic", new HeifXmpContainerAdapter());
        await CheckContainerAsync(c, root, "TestCase/A7C01489.ARW", new TiffXmpContainerAdapter());

        var jpegWithoutXmp = Path.Combine(root, "without-xmp.jpg");
        await File.WriteAllBytesAsync(jpegWithoutXmp, [0xFF, 0xD8, 0xFF, 0xD9]);
        await CheckMissingXmpAsync(c, jpegWithoutXmp, new JpegXmpContainerAdapter());

        var webpWithoutXmp = Path.Combine(root, "without-xmp.webp");
        await File.WriteAllBytesAsync(webpWithoutXmp,
            Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes(4)).Concat(Encoding.ASCII.GetBytes("WEBP")).ToArray());
        await CheckMissingXmpAsync(c, webpWithoutXmp, new WebpXmpContainerAdapter());

        await CheckRatingConcurrencyAsync(c, root);
    }

    private static async Task CheckContainerAsync(
        Checks c,
        string root,
        string fixture,
        IXmpContainerAdapter adapter)
    {
        var path = CopyFixture(fixture, root);
        var original = await File.ReadAllBytesAsync(path);
        var file = new LocalFileRef(path);
        var name = Path.GetFileName(path);
        var before = await adapter.ReadCanonicalPacketAsync(file);
        c.True($"{name} 有 canonical XMP", before is { Length: > 0 },
            before is null ? "适配器未定位到 canonical 包" : "包为空");

        var store = XmpRatingStoreFactory.Create(file);
        var receipt = await store.WriteAsync(4, safeMode: true);
        c.Equal($"{name} 写后 Rating", 4, receipt.After.Value);
        var read = await adapter.ReadCanonicalPacketAsync(file);
        c.Equal($"{name} 写后语义回读", 4,
            XmpRatingReader.ReadPacket(read!.Data, XmpRatingSource.Embedded).Value);
        c.Equal($"{name} 写入只改包内区间", original.Length, (await File.ReadAllBytesAsync(path)).Length);

        await store.RestoreAsync(receipt);
        c.Bytes($"{name} 恢复原始字节", original, await File.ReadAllBytesAsync(path));
    }

    private static async Task CheckMissingXmpAsync(
        Checks c,
        string path,
        IXmpContainerAdapter adapter)
    {
        var name = Path.GetFileName(path);
        var original = await File.ReadAllBytesAsync(path);
        var file = new LocalFileRef(path);
        var packet = await adapter.ReadCanonicalPacketAsync(file);
        c.True($"{name} 无 canonical XMP 时读取返回 null", packet is null, "适配器返回了包");

        // 不新建 XMP：没有 canonical 包时评分存储明确 Unsupported，且文件一个字节都不动。
        var store = XmpRatingStoreFactory.Create(file);
        await c.StorageFailsAsync(
            $"{name} 拒绝在缺失 XMP 时新建",
            StorageError.Unsupported,
            () => store.WriteAsync(4, safeMode: true));
        c.Bytes($"{name} 失败后文件未改动", original, await File.ReadAllBytesAsync(path));
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
            .All(result => result.Outcome is RatingWriteOutcome.Superseded
                or RatingWriteOutcome.Applied
                or RatingWriteOutcome.NoChange));
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
}
