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
        c.Section("容器 XMP 写入、恢复与评分事务");
        UiDispatcher.Initialize(new InlineDispatcher());

        await CheckContainerAsync(c, root, "release/native-p2-fixtures/color-srgb.jpg", new JpegXmpContainerAdapter());
        await CheckContainerAsync(c, root, "release/native-p2-heif/color-srgb.heic", new HeifXmpContainerAdapter());
        await CheckContainerAsync(c, root, "TestCase/A7C01489.ARW", new TiffXmpContainerAdapter());

        var jpegWithoutXmp = Path.Combine(root, "without-xmp.jpg");
        await File.WriteAllBytesAsync(jpegWithoutXmp, [0xFF, 0xD8, 0xFF, 0xD9]);
        await CheckContainerAsync(c, root, jpegWithoutXmp, new JpegXmpContainerAdapter(), copyFixture: false);

        var webpWithoutXmp = Path.Combine(root, "without-xmp.webp");
        await File.WriteAllBytesAsync(webpWithoutXmp,
            Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes(4)).Concat(Encoding.ASCII.GetBytes("WEBP")).ToArray());
        await CheckContainerAsync(c, root, webpWithoutXmp, new WebpXmpContainerAdapter(), copyFixture: false);

        await CheckRatingConcurrencyAsync(c, root);
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
