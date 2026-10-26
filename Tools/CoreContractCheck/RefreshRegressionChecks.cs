using System.Reflection;
using System.Reactive.Concurrency;
using System.Text;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace CoreContractCheck;

internal static class RefreshRegressionChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("独立照片刷新与来源重新发现");
        var directory = Path.Combine(root, "refresh-regression");
        Directory.CreateDirectory(directory);
        var aPath = Path.Combine(directory, "a.jpg");
        var bPath = Path.Combine(directory, "b.jpg");
        await File.WriteAllBytesAsync(aPath, Jpeg(1));
        await File.WriteAllBytesAsync(bPath, Jpeg(2));
        var a = new ImageFile(new LocalFileRef(aPath));
        var b = new ImageFile(new LocalFileRef(bPath));
        a.UpdateCachedRating(5);
        b.UpdateCachedRating(5);
        var groups = PhotoGroupMap.Build([a, b], false,
            [new PhotoFormatRule("jpg", "JPG", [".jpg"])]);
        using var main = new MainViewModel();
        var method = typeof(MainViewModel).GetMethod("RefreshRatingsFromDiskAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dispatcher = new DelayedDispatcher();
        UiDispatcher.Initialize(dispatcher);
        try
        {
            var first = (Task)method.Invoke(main, [groups.GroupFor(a)!])!;
            await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await (Task)method.Invoke(main, [groups.GroupFor(b)!])!;
            dispatcher.Release.TrySetResult();
            await first;
            c.Equal("另一照片刷新不作废本照片结果", 1, a.Rating);
            c.Equal("后发独立照片刷新生效", 2, b.Rating);
        }
        finally
        {
            dispatcher.Release.TrySetResult();
            UiDispatcher.Initialize(new InlineDispatcher());
        }

        a.RatingSidecarIndex = RatingSidecarIndex.Empty;
        var sidecarPath = Path.ChangeExtension(aPath, ".xmp");
        await File.WriteAllBytesAsync(sidecarPath, Packet(4));
        await (Task)method.Invoke(main, [groups.GroupFor(a)!])!;
        c.Equal("评分回读发现旧索引之外的 sidecar", 2, a.RatingSources.Count);
        c.True("新增 sidecar 冲突按磁盘呈现", a.HasRatingConflict);
        File.Delete(sidecarPath);
        await (Task)method.Invoke(main, [groups.GroupFor(a)!])!;
        c.Equal("评分回读不复活已删除 sidecar", 1, a.RatingSources.Count);

        var badPath = Path.Combine(directory, "unsupported.jpg");
        await File.WriteAllTextAsync(badPath, "readable bytes without metadata");
        var bad = new ImageFile(new LocalFileRef(badPath));
        c.True("文件可读但无 EXIF 的刷新不冒充打开失败", await bad.RefreshMetadataAsync());
        c.True("元数据不可解析时完成加载态", bad.IsExifLoaded && !bad.IsExifLoading);
        c.True("元数据不可解析仍保留来源错误", bad.RatingSources.Single().Error != null);

        var messages = new List<string>();
        main.OpenFailed += messages.Add;
        var parentless = new ParentlessFileRef(a.File);
        await main.FolderVM.OpenImageAsync(parentless, resetView: false);
        await main.FolderVM.OpenImageAsync(parentless, resetView: false);
        c.True("单图重复刷新明确父目录访问边界", messages.Contains(StatusMessages.SinglePhotoRefreshed));
        c.Equal("单图刷新保留列表身份", 1, main.FolderVM.AllFiles.Count);

        using var settings = new SettingsViewModel();
        settings.BitmapCacheMaxCount = int.MaxValue;
        settings.BitmapCacheMaxMemory = int.MaxValue;
        c.Equal("工作副本缓存张数输入上界", 400, settings.BitmapCacheMaxCount);
        c.Equal("工作副本缓存内存输入上界", 32768, settings.BitmapCacheMaxMemory);
    }

    private static byte[] Packet(int value) => Encoding.UTF8.GetBytes(
        $"<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating='{value}'/></rdf:RDF></x:xmpmeta>");

    private static byte[] Jpeg(int value)
    {
        var packet = Packet(value);
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        var length = packet.Length + identifier.Length + 2;
        return new byte[] { 255, 216, 255, 225, (byte)(length >> 8), (byte)length }
            .Concat(identifier).Concat(packet).Concat(new byte[] { 255, 217 }).ToArray();
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public IScheduler MainThreadScheduler => ImmediateScheduler.Instance;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> action, UiPriority priority = UiPriority.Normal) => Task.FromResult(action());
    }

    private sealed class DelayedDispatcher : IUiDispatcher
    {
        private int _used;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CheckAccess() => true;
        public IScheduler MainThreadScheduler => ImmediateScheduler.Instance;
        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
        public async Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            if (Interlocked.Exchange(ref _used, 1) == 0)
            {
                Started.TrySetResult();
                await Release.Task;
            }
            action();
        }
        public Task<T> InvokeAsync<T>(Func<T> action, UiPriority priority = UiPriority.Normal) => Task.FromResult(action());
    }

    private sealed class ParentlessFileRef(IFileRef inner) : IFileRef
    {
        public string Name => inner.Name;
        public Uri Path => inner.Path;
        public StorageItemId StorageId => inner.StorageId;
        public StorageCapabilities Capabilities => inner.Capabilities;
        public string? TryGetLocalPath() => inner.TryGetLocalPath();
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => inner.OpenReadAsync(cancellationToken);
        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) => inner.GetBasicPropertiesAsync(cancellationToken);
        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderRef?>(null);
        public Task<IStorageRandomAccessSession> OpenRandomAccessAsync(StorageAccessMode mode, CancellationToken cancellationToken = default) => inner.OpenRandomAccessAsync(mode, cancellationToken);
    }
}
