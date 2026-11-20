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
        PhotoGroupMap.Build([a, b], false, [new PhotoFormatRule("jpg", "JPG", [".jpg"])]);
        using var main = new MainViewModel();
        var dispatcher = new DelayedDispatcher();
        UiDispatcher.Initialize(dispatcher);
        try
        {
            var first = main.SetRatingWithResultAsync(a, 1);
            await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await main.SetRatingWithResultAsync(b, 2);
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
        await main.SetRatingWithResultAsync(a, 1);
        c.Equal("评分回读发现旧索引之外的 sidecar", 2, a.RatingSources.Count);
        c.True("新发现的 sidecar 参与统一写入", !a.HasRatingConflict
            && a.RatingSources.Any(source => source.Source == XmpRatingSource.Sidecar && source.Value == 1));
        File.Delete(sidecarPath);
        await main.SetRatingWithResultAsync(a, 1);
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

        await VerifyFilterSessionAsync(c, root);
    }

    private static async Task VerifyFilterSessionAsync(Checks c, string root)
    {
        c.Section("FILTER-02 / FILTER-03：默认拍摄时间与星级多选");
        using var main = new MainViewModel();
        var filter = main.FileVM.FilterBar;
        c.Equal("新会话默认拍摄时间", SortMode.Date, filter.SortMode);
        c.Equal("新会话默认升序", SortOrder.Ascending, filter.SortOrder);
        c.Equal("名称选项使用文件名", "文件名", filter.SortModes[0].DisplayName);
        c.Equal("只有九个星级筛选选项", 9, filter.RatingFilters.Count);

        var directory = Path.Combine(root, "filter-session");
        Directory.CreateDirectory(directory);
        string anchorPath = Path.Combine(directory, "a.jpg");
        string rawPath = Path.Combine(directory, "z.arw");
        await File.WriteAllBytesAsync(anchorPath, Jpeg(1));
        File.Copy("Fixtures/raw/A7C01489.ARW", rawPath);
        var fallback = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(anchorPath, fallback.UtcDateTime);
        File.SetLastWriteTimeUtc(rawPath, fallback.AddYears(1).UtcDateTime);
        await main.FolderVM.OpenImageAsync(new LocalFileRef(anchorPath), resetView: false);
        var list = main.FileVM.ThumbnailList;
        var anchor = main.CurrentFile;
        var raw = main.FolderVM.AllFiles.Single(file => file.Name == "z.arw");
        for (int i = 0; i < 250 && (list.FilteredFiles.FirstOrDefault() != raw || raw.PhotoDate >= fallback
            || main.FolderVM.AllFiles.Any(file => file.ModifiedDate == null || file.RatingSources.Count == 0)); i++)
            await Task.Delay(20);
        c.True("未浏览的 RAW 按 EXIF 排在靠修改时间排序的 JPG 之前",
            raw.PhotoDate is { } date && date < fallback && list.FilteredFiles.FirstOrDefault() == raw);
        c.Equal("无 EXIF 时回退文件修改时间", fallback, anchor?.PhotoDate);
        c.True("后台重排不切换显式目标", anchor != null && ReferenceEquals(main.CurrentFile, anchor));
        c.True("新会话只有‘全部’勾选", filter.IsRatingFilterSelected("All"));
        filter.ToggleRatingFilter("Eq1");
        filter.ToggleRatingFilter("Conflict");
        c.True("星级条件可独立勾选", filter.SelectedRatingFilters.SetEquals(["Eq1", "Conflict"]));
        c.Equal("分段显示多选", "多选", filter.RatingFilterLabel);
        c.True("并集保留一星目标", anchor != null && list.FilteredFiles.Contains(anchor));
        filter.ToggleRatingFilter("Eq1");
        filter.ToggleRatingFilter("Conflict");
        c.True("取消最后一项恢复全部", filter.IsRatingFilterSelected("All"));
        filter.ToggleRatingFilter("None");
        filter.ToggleRatingFilter("Eq2");
        filter.ToggleRatingFilter("All");
        c.True("点击全部清空条件且恢复全部卡片", filter.IsRatingFilterSelected("All") && list.FilteredFiles.Count == 2);
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
