using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;

namespace CoreContractCheck;

internal static class OpenRequestChannelChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("外部打开请求队列");
        var firstFile = new LocalFileRef(Path.Combine(root, "first.jpg"));
        var secondFile = new LocalFileRef(Path.Combine(root, "second.jpg"));
        var first = new OpenRequest([firstFile], OpenRequestSource.SystemOpen);
        var duplicate = new OpenRequest([firstFile], OpenRequestSource.Share);
        var second = new OpenRequest([secondFile], OpenRequestSource.SystemOpen);
        c.True("同目标不同来源视为重复", first.HasSameTargets(duplicate));
        c.True("不同目标不视为重复", !first.HasSameTargets(second));

        var channel = new OpenRequestChannel();
        c.True("UI 就绪前接受请求", channel.Publish(first));
        c.True("pending 重复请求被合并", !channel.Publish(duplicate));
        c.True("不同请求可入队", channel.Publish(second));
        c.True("空批次不入队", !channel.Publish(new OpenRequest([], OpenRequestSource.Automation)));

        var opened = new List<string>();
        var errors = new List<string>();
        var consumer = channel.RunAsync(async (request, token) =>
        {
            await Task.Yield();
            if (request.Items[0].Name == "first.jpg") throw new IOException("injected");
            opened.Add(request.Items[0].Name);
        }, (request, exception) => errors.Add(request.Items[0].Name + ":" + exception.Message));

        await Task.Delay(30);
        var secondConsumer = await Task.Run(async () =>
        {
            try
            {
                await channel.RunAsync((_, _) => Task.CompletedTask, (_, _) => { });
                return "no error";
            }
            catch (InvalidOperationException)
            {
                return "single consumer";
            }
        });
        c.Equal("只允许一个消费者", "single consumer", secondConsumer);

        channel.Complete();
        await consumer.WaitAsync(TimeSpan.FromSeconds(2));
        c.Equal("handler 异常后继续下一项", "second.jpg", string.Join(',', opened));
        c.Equal("异常有统一出口", "first.jpg:injected", string.Join(',', errors));
        c.True("完成后拒绝新请求", !channel.Publish(new OpenRequest([firstFile], OpenRequestSource.CommandLine)));

        var drain = new OpenRequestChannel();
        drain.Publish(first);
        drain.Complete();
        var drained = 0;
        await drain.RunAsync((_, _) =>
        {
            drained++;
            return Task.CompletedTask;
        }, (_, _) => { }).WaitAsync(TimeSpan.FromSeconds(2));
        c.Equal("完成前排空已接受请求", 1, drained);

        await RunRepeatActivationAsync(c, root);
    }

    /// <summary>
    /// 重复外部激活：同一目录/当前照片再次拖入时原地重枚举（新增可见、删除消失），
    /// 保留当前照片对象、筛选与视口状态（不重新 fit、不滚动），并触发前置窗口事件。
    /// </summary>
    private static async Task RunRepeatActivationAsync(Checks c, string root)
    {
        c.Section("外部激活重复刷新");
        UiDispatcher.Initialize(new InlineDispatcher());
        var reactiveBuilder = ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder();
        reactiveBuilder.WithCoreServices();
        reactiveBuilder.WithMainThreadScheduler(UiDispatcher.MainThreadScheduler);
        reactiveBuilder.BuildApp();
        TryInitializeTestDecoder();

        using var main = new MainViewModel();
        var repeats = 0;
        main.OpenCoordinator.RepeatActivation += () => repeats++;
        var fits = 0;
        main.FitToViewRequested += () => fits++;
        var scrolls = 0;
        main.FolderVM.ScrollToCurrentRequested += () => scrolls++;

        var directory = Path.Combine(root, "refresh");
        Directory.CreateDirectory(directory);
        var aPath = Path.Combine(directory, "a.jpg");
        var bPath = Path.Combine(directory, "b.jpg");
        var hasFixtures = WritePhoto(aPath);
        hasFixtures &= WritePhoto(bPath);

        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("首次打开枚举两个文件", 2, main.FolderVM.AllFiles.Count);
        var current = main.CurrentFile;
        c.True("首次打开有当前照片", current != null, "CurrentFile 为 null");
        if (current == null) return;

        var loadedA = main.FolderVM.AllFiles.FirstOrDefault(file => file.Name == "a.jpg");
        var fitsBefore = fits;
        var scrollsBefore = scrolls;

        // 首次打开的元数据（属性 + 当前图完整 EXIF）先就绪，便于验证刷新确实重读。
        var baselineReady = await WaitUntilAsync(
            () => loadedA is { IsExifLoaded: true, FileSize: not null },
            TimeSpan.FromSeconds(5));
        var exifBeforeRefresh = loadedA?.ExifData;
        if (!baselineReady)
        {
            c.Fail("首次打开元数据就绪", "a.jpg 未在 5s 内加载 EXIF 与文件大小");
        }

        // 目录重复激活：发现新增文件，保留当前照片与已有对象，并重读属性、EXIF、评分与 sidecar。
        WritePhoto(Path.Combine(directory, "c.jpg"));
        if (hasFixtures)
        {
            await WriteRatingAsync(aPath, 3);
        }
        var sizeOnDisk = (ulong)new FileInfo(aPath).Length;
        var modifiedOnDisk = new DateTimeOffset(new FileInfo(aPath).LastWriteTimeUtc, TimeSpan.Zero);
        var repeatBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("目录重复激活触发前置窗口", repeatBefore + 1, repeats);
        c.Equal("目录重复激活重新枚举发现新增", 3, main.FolderVM.AllFiles.Count);
        c.True("新增文件进入列表", main.FolderVM.AllFiles.Any(file => file.Name == "c.jpg"), DescribeAll(main));
        c.True("目录重复激活保留当前照片对象", ReferenceEquals(current, main.CurrentFile),
            $"before={current.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.True("仍存在的文件按 StorageId 复用对象",
            loadedA != null && ReferenceEquals(loadedA, main.FolderVM.AllFiles.FirstOrDefault(file => file.Name == "a.jpg")),
            "a.jpg 对象被重建");
        c.Equal("目录重复激活不重新 fit", fitsBefore, fits);
        c.Equal("目录重复激活不滚动", scrollsBefore, scrolls);
        if (hasFixtures)
        {
            // 同目录刷新在返回前已 await 复用对象的元数据重读，不再靠轮询等待。
            c.True("刷新重读基本属性大小", loadedA?.FileSize == sizeOnDisk,
                $"期望 {sizeOnDisk}，实际 {loadedA?.FileSize}");
            c.True("刷新重读修改时间", loadedA?.ModifiedDate == modifiedOnDisk,
                $"期望 {modifiedOnDisk:O}，实际 {loadedA?.ModifiedDate:O}");
            c.Equal("刷新重读磁盘星级", 3, loadedA?.Rating ?? -1);
            c.True("刷新重建完整 EXIF", exifBeforeRefresh != null
                && !ReferenceEquals(exifBeforeRefresh, loadedA?.ExifData),
                exifBeforeRefresh == null ? "刷新前 EXIF 为空" : "EXIF 对象未被重读");
        }
        else
        {
            c.Skip("刷新重读属性/EXIF/星级", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
        }

        // sidecar 是独立来源：新增后刷新可见，删除后刷新不再复活（返回前已重读完成）。
        var sidecarPath = Path.Combine(directory, "a.xmp");
        await File.WriteAllTextAsync(sidecarPath, SidecarPacket(rating: 5));
        repeatBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("新增 sidecar 后重复激活触发前置窗口", repeatBefore + 1, repeats);
        c.Equal("sidecar 不进图片列表", 3, main.FolderVM.AllFiles.Count);
        c.True("刷新读取新增 sidecar",
            loadedA?.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.NumericValue == 5) == true,
            "来源=" + string.Join(',', loadedA?.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}") ?? []));

        File.Delete(sidecarPath);
        repeatBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("删除 sidecar 后重复激活触发前置窗口", repeatBefore + 1, repeats);
        c.True("删除的 sidecar 不再复活",
            loadedA?.RatingSources.All(source => source.Source != XmpRatingSource.Sidecar) == true,
            "来源=" + string.Join(',', loadedA?.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}") ?? []));
        c.Equal("删除 sidecar 后内嵌评分保留", 3, loadedA?.Rating ?? -1);

        // 删除后重复激活：删除的文件退出列表，当前照片不受影响。
        File.Delete(bPath);
        repeatBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("删除文件后重复激活触发前置窗口", repeatBefore + 1, repeats);
        c.Equal("目录重复激活发现删除", 2, main.FolderVM.AllFiles.Count);
        c.True("已删除文件不再出现", main.FolderVM.AllFiles.All(file => file.Name != "b.jpg"), DescribeAll(main));
        c.True("删除后仍保留当前照片", ReferenceEquals(current, main.CurrentFile),
            $"before={current.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.Equal("删除后重复激活不重新 fit", fitsBefore, fits);

        // 当前照片重复激活：按文件进入当前目录重枚举，不切换、不重置。
        WritePhoto(Path.Combine(directory, "d.jpg"));
        repeatBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([current.File], OpenRequestSource.DragDrop);
        c.Equal("照片重复激活触发前置窗口", repeatBefore + 1, repeats);
        c.True("照片重复激活重枚举发现新增",
            main.FolderVM.AllFiles.Any(file => file.Name == "d.jpg"), DescribeAll(main));
        c.True("照片重复激活保留当前照片对象", ReferenceEquals(current, main.CurrentFile),
            $"before={current.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.Equal("照片重复激活不重新 fit", fitsBefore, fits);
        c.Equal("照片重复激活不滚动", scrollsBefore, scrolls);

        // 同组伴侣重复激活：仍走当前目录重枚举，代表文件与当前照片不变。
        var groupDirectory = Path.Combine(root, "refresh-group");
        Directory.CreateDirectory(groupDirectory);
        WritePhoto(Path.Combine(groupDirectory, "p.jpg"));
        WritePhoto(Path.Combine(groupDirectory, "p.jpeg"));
        WritePhoto(Path.Combine(groupDirectory, "q.jpg"));

        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFolderRef(groupDirectory)], OpenRequestSource.SystemOpen);
        var representative = main.CurrentFile;
        var partner = representative?.Group?.Files
            .FirstOrDefault(file => file.File.StorageId != representative.File.StorageId);
        c.True("同名伴侣参与分组", partner != null,
            representative?.Group == null ? "当前照片无组信息" : "组内没有其他成员");
        var groupFitsBefore = fits;
        if (representative != null && partner != null)
        {
            repeatBefore = repeats;
            await main.OpenCoordinator.PublishAndWaitAsync([partner.File], OpenRequestSource.SystemOpen);
            c.Equal("伴侣重复激活触发前置窗口", repeatBefore + 1, repeats);
            c.True("伴侣重复激活保留当前代表", ReferenceEquals(representative, main.CurrentFile),
                $"before={representative.Name} after={main.CurrentFile?.Name ?? "null"}");
            c.Equal("伴侣重复激活保持枚举结果", 3, main.FolderVM.AllFiles.Count);
            c.Equal("伴侣重复激活不重新 fit", groupFitsBefore, fits);
        }

        await CheckSingleImageRefreshAsync(c, main, root);
        await CheckExplicitFileOpenRefreshAsync(c, main, root);
        await CheckRefreshPermissionAsync(c, main, root);
        await CheckPickerViewportAsync(c, main, root);
        await CheckCrossDirectoryFallbackAsync(c, main, root);
        await CheckLoadSessionIdentityAsync(c, root);
        await CheckStaleMetadataAsync(c, root);
    }

    /// <summary>刷新时目录枚举被拒：保留整个目录与当前照片，只刷新仍可访问的文件，并给出 STATE 提示。</summary>
    private static async Task CheckRefreshPermissionAsync(Checks c, MainViewModel main, string root)
    {
        c.Section("刷新权限不足保留目录");
        var directory = Path.Combine(root, "refresh-denied");
        Directory.CreateDirectory(directory);
        var aPath = Path.Combine(directory, "a.jpg");
        var bPath = Path.Combine(directory, "b.jpg");
        var hasFixtures = WritePhoto(aPath);
        hasFixtures &= WritePhoto(bPath);
        if (!hasFixtures)
        {
            c.Skip("刷新权限不足保留目录", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
            return;
        }

        // 初始已有 a 的 sidecar；刷新失败时必须能发现它被删除，也能发现 b 新增的 sidecar。
        var sidecarA = Path.Combine(directory, "a.xmp");
        var sidecarB = Path.Combine(directory, "b.xmp");
        await File.WriteAllTextAsync(sidecarA, SidecarPacket(rating: 5));

        var flaky = new FlakyFolderRef(new LocalFolderRef(directory));
        var file = new FolderFileRef(new LocalFileRef(aPath), flaky);
        await main.OpenCoordinator.PublishAndWaitAsync([file], OpenRequestSource.SystemOpen);
        c.Equal("初次打开枚举整个目录", 2, main.FolderVM.AllFiles.Count);
        var current = main.CurrentFile;
        var a = Find(main, "a.jpg");
        var b = Find(main, "b.jpg");
        c.True("初次打开有当前照片", current != null, "CurrentFile 为 null");
        if (current == null || a == null || b == null) return;
        c.True("初次打开读到已有 sidecar",
            a.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.NumericValue == 5),
            "来源=" + string.Join(',', a.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}")));

        var failures = new List<string>();
        void OnOpenFailed(string message) => failures.Add(message);
        main.OpenFailed += OnOpenFailed;
        flaky.FailEnumeration = true;
        await WriteRatingAsync(aPath, 4);
        File.Delete(sidecarA);
        await File.WriteAllTextAsync(sidecarB, SidecarPacket(rating: 3));
        var fits = 0;
        main.FitToViewRequested += () => fits++;
        await main.OpenCoordinator.PublishAndWaitAsync([file], OpenRequestSource.SystemOpen);
        main.OpenFailed -= OnOpenFailed;

        c.True("权限不足给出 STATE 提示",
            failures.Count == 1 && failures[0].Contains("无法读取文件夹", StringComparison.Ordinal),
            failures.Count == 0 ? "未触发 OpenFailed" : string.Join(" | ", failures));
        c.Equal("不把整个目录降级成单图", 2, main.FolderVM.AllFiles.Count);
        c.True("保留当前照片", ReferenceEquals(current, main.CurrentFile),
            $"before={current.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.Equal("当前目标刷新不 Fit", 0, fits);
        // 失败刷新在返回前已用逐文件查找刷新可访问项：评分与 sidecar 增删都应立即反映。
        c.Equal("仍刷新可访问项的评分", 4, a.Rating);
        c.True("失败刷新按磁盘删除 sidecar",
            a.RatingSources.All(source => source.Source != XmpRatingSource.Sidecar),
            "来源=" + string.Join(',', a.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}")));
        c.True("失败刷新发现新增 sidecar",
            b.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.NumericValue == 3),
            "来源=" + string.Join(',', b.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}")));
    }

    /// <summary>
    /// 首次打开目录后，任何显式打开文件（当前/非当前、拖入）都要重新枚举整个父目录：
    /// 新增/删除、全部 Rating、sidecar、属性与 EXIF 都要更新；当前目标不 Fit，新目标按 OPEN-05 Fit。
    /// </summary>
    private static async Task CheckExplicitFileOpenRefreshAsync(Checks c, MainViewModel main, string root)
    {
        c.Section("显式打开文件刷新整个父目录");
        var directory = Path.Combine(root, "explicit-refresh");
        Directory.CreateDirectory(directory);
        var aPath = Path.Combine(directory, "a.jpg");
        var bPath = Path.Combine(directory, "b.jpg");
        var dPath = Path.Combine(directory, "d.jpg");
        var hasFixtures = WritePhoto(aPath);
        hasFixtures &= WritePhoto(bPath);
        hasFixtures &= WritePhoto(dPath);
        if (!hasFixtures)
        {
            c.Skip("显式打开文件刷新整个父目录", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
            return;
        }

        var fits = 0;
        main.FitToViewRequested += () => fits++;
        var repeats = 0;
        main.OpenCoordinator.RepeatActivation += () => repeats++;

        await main.OpenCoordinator.PublishAndWaitAsync(
            [new LocalFolderRef(directory)], OpenRequestSource.SystemOpen);
        c.Equal("首次打开三个文件", 3, main.FolderVM.AllFiles.Count);
        var a = Find(main, "a.jpg");
        var b = Find(main, "b.jpg");
        var initial = main.CurrentFile;
        c.True("首次打开有当前照片", initial != null, "CurrentFile 为 null");
        if (initial == null || a == null || b == null) return;

        var ready = await WaitUntilAsync(
            () => a.FileSize != null && b.FileSize != null,
            TimeSpan.FromSeconds(5));
        c.True("首次打开元数据就绪", ready, $"a.FileSize={a.FileSize} b.FileSize={b.FileSize}");
        var aExifBefore = a.ExifData;

        // 磁盘变化：新增 c、删除 d、改 a/b 星级、给 a 加 sidecar。
        var cPath = Path.Combine(directory, "c.jpg");
        WritePhoto(cPath);
        File.Delete(dPath);
        await WriteRatingAsync(aPath, 4);
        await WriteRatingAsync(bPath, 2);
        var sidecarA = Path.Combine(directory, "a.xmp");
        await File.WriteAllTextAsync(sidecarA, SidecarPacket(rating: 5));
        var aSize = (ulong)new FileInfo(aPath).Length;
        var aModified = ModifiedOnDisk(aPath);
        var bModified = ModifiedOnDisk(bPath);

        // 非当前文件 + 拖入：整目录刷新，当前照片切到目标并按 OPEN-05 重新 Fit。
        var fitsBefore = fits;
        var repeatsBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFileRef(bPath)], OpenRequestSource.DragDrop);
        c.Equal("新目标重新 Fit", fitsBefore + 1, fits);
        c.Equal("非当前文件不触发重复激活", repeatsBefore, repeats);
        c.True("非当前文件成为当前照片",
            main.CurrentFile != null && ReferenceEquals(b, main.CurrentFile),
            $"before={initial.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.True("打开非当前文件也发现新增", Find(main, "c.jpg") != null, DescribeAll(main));
        c.True("打开非当前文件也发现删除", Find(main, "d.jpg") == null, DescribeAll(main));
        c.Equal("刷新后无重复卡片",
            main.FolderVM.AllFiles.Select(file => file.File.StorageId).Distinct().Count(),
            main.FolderVM.AllFiles.Count);

        // 刷新在返回前已 await 复用对象的元数据重读，直接断言，不再轮询。
        c.True("非目标文件也刷新全部 Rating", a.Rating == 4 && b.Rating == 2,
            $"a.Rating={a.Rating}(4) b.Rating={b.Rating}(2)");
        c.True("非目标文件也刷新基本属性",
            a.FileSize == aSize && a.ModifiedDate == aModified && b.ModifiedDate == bModified,
            $"a.Size={a.FileSize}({aSize}) a.Mod={a.ModifiedDate:O}({aModified:O}) "
            + $"b.Mod={b.ModifiedDate:O}({bModified:O})");
        c.True("非目标文件也刷新完整 EXIF",
            a.IsExifLoaded && b.IsExifLoaded && !ReferenceEquals(aExifBefore, a.ExifData),
            $"a.ExifLoaded={a.IsExifLoaded} b.ExifLoaded={b.IsExifLoaded} "
            + $"aExifChanged={!ReferenceEquals(aExifBefore, a.ExifData)}");
        c.True("非目标文件也刷新 sidecar",
            a.RatingSources.Any(source =>
                source.Source == XmpRatingSource.Sidecar && source.NumericValue == 5),
            "a.Sources=" + string.Join(',', a.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}")));

        // 当前文件再次打开：整目录继续刷新，但不 Fit、不换对象。
        var bExifBefore = b.ExifData;
        var ePath = Path.Combine(directory, "e.jpg");
        WritePhoto(ePath);
        File.Delete(cPath);
        await WriteRatingAsync(aPath, 3);
        await WriteRatingAsync(bPath, 4);
        File.Delete(sidecarA);
        var sidecarB = Path.Combine(directory, "b.xmp");
        await File.WriteAllTextAsync(sidecarB, SidecarPacket(rating: 2));
        var aModifiedAgain = ModifiedOnDisk(aPath);
        var bModifiedAgain = ModifiedOnDisk(bPath);

        fitsBefore = fits;
        repeatsBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFileRef(bPath)], OpenRequestSource.SystemOpen);
        c.Equal("当前目标不重新 Fit", fitsBefore, fits);
        c.Equal("当前目标触发前置窗口", repeatsBefore + 1, repeats);
        c.True("当前目标保留对象", ReferenceEquals(b, main.CurrentFile),
            $"before={b.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.True("当前目标也发现新增", Find(main, "e.jpg") != null, DescribeAll(main));
        c.True("当前目标也发现删除", Find(main, "c.jpg") == null, DescribeAll(main));
        var sidecarGone = a.RatingSources.All(source => source.Source != XmpRatingSource.Sidecar);
        var sidecarAdded = b.RatingSources.Any(source =>
            source.Source == XmpRatingSource.Sidecar && source.NumericValue == 2);
        c.True("当前目标刷新整个目录的 Rating", a.Rating == 3 && b.Rating == 4,
            $"a.Rating={a.Rating}(3) b.Rating={b.Rating}(4)");
        c.True("当前目标刷新整个目录的属性",
            a.ModifiedDate == aModifiedAgain && b.ModifiedDate == bModifiedAgain,
            $"a.Mod={a.ModifiedDate:O}({aModifiedAgain:O}) b.Mod={b.ModifiedDate:O}({bModifiedAgain:O})");
        c.True("当前目标刷新整个目录的 EXIF",
            !ReferenceEquals(bExifBefore, b.ExifData),
            "b 的 EXIF 对象未被重读");
        c.True("当前目标刷新整个目录的 sidecar 增删", sidecarGone && sidecarAdded,
            "a.Sources=" + string.Join(',', a.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}"))
            + " b.Sources=" + string.Join(',', b.RatingSources.Select(source => $"{source.Source}:{source.NumericValue}")));

        // 拖入非当前文件：再次整目录刷新并重新 Fit。
        var fPath = Path.Combine(directory, "f.jpg");
        WritePhoto(fPath);
        File.Delete(ePath);
        await WriteRatingAsync(aPath, 5);
        await WriteRatingAsync(bPath, 1);

        fitsBefore = fits;
        repeatsBefore = repeats;
        await main.OpenCoordinator.PublishAndWaitAsync([new LocalFileRef(aPath)], OpenRequestSource.DragDrop);
        c.Equal("拖入新目标重新 Fit", fitsBefore + 1, fits);
        c.Equal("拖入新目标不触发重复激活", repeatsBefore, repeats);
        c.True("拖入新目标成为当前照片",
            main.CurrentFile != null && ReferenceEquals(a, main.CurrentFile),
            $"after={main.CurrentFile?.Name ?? "null"}");
        c.True("拖入也发现新增", Find(main, "f.jpg") != null, DescribeAll(main));
        c.True("拖入也发现删除", Find(main, "e.jpg") == null, DescribeAll(main));
        c.True("拖入也刷新非目标文件评分", a.Rating == 5 && b.Rating == 1,
            $"a.Rating={a.Rating}(5) b.Rating={b.Rating}(1)");
    }

    private static ImageFile? Find(MainViewModel main, string name) =>
        main.FolderVM.AllFiles.FirstOrDefault(file => file.Name == name);

    private static DateTimeOffset ModifiedOnDisk(string path) =>
        new(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);

    private static Task WriteRatingAsync(string path, int rating) =>
        XmpRatingStoreFactory.Create(new LocalFileRef(path)).WriteAsync(rating, safeMode: true);

    /// <summary>单图模式重复激活：同一对象重读属性/EXIF/评分，不 Fit；读取失败以 STATE-03 上报。</summary>
    private static async Task CheckSingleImageRefreshAsync(Checks c, MainViewModel main, string root)
    {
        var directory = Path.Combine(root, "refresh-single");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "single.jpg");
        var hasFixture = WritePhoto(path);

        var fits = 0;
        main.FitToViewRequested += () => fits++;

        var file = new ParentlessFileRef(new LocalFileRef(path));
        await main.OpenCoordinator.PublishAndWaitAsync([file], OpenRequestSource.SystemOpen);
        var single = main.CurrentFile;
        c.True("单图模式打开", main.FolderVM.CurrentFolder == null,
            "CurrentFolder=" + (main.FolderVM.CurrentFolder?.Name ?? "null"));
        c.True("单图成为当前照片", single != null && single.File.StorageId == file.StorageId,
            "CurrentFile=" + (single?.Name ?? "null"));
        if (single == null) return;

        // 首次单图打开就应带基本属性（大小/修改时间），不依赖后续后台补齐。
        c.True("首次单图加载基本属性", single.FileSize != null && single.ModifiedDate != null,
            $"FileSize={single.FileSize} ModifiedDate={single.ModifiedDate}");
        var exifReady = await WaitUntilAsync(() => single.IsExifLoaded, TimeSpan.FromSeconds(5));
        c.True("首次单图 EXIF 就绪", exifReady, "5s 内未加载 EXIF");
        var fitsBefore = fits;
        var exifBefore = single.ExifData;
        var sizeOnDisk = (ulong)new FileInfo(path).Length;
        if (hasFixture)
        {
            await WriteRatingAsync(path, 4);
            sizeOnDisk = (ulong)new FileInfo(path).Length;
        }

        await main.OpenCoordinator.PublishAndWaitAsync([file], OpenRequestSource.DragDrop);
        c.True("单图重复激活保留对象", ReferenceEquals(single, main.CurrentFile),
            $"before={single.Name} after={main.CurrentFile?.Name ?? "null"}");
        c.Equal("单图重复激活不重新 fit", fitsBefore, fits);
        c.Equal("单图重复激活保持单图列表", 1, main.FolderVM.AllFiles.Count);
        if (hasFixture)
        {
            // ReloadFileAsync 在返回前完成重读，不再轮询等待。
            c.True("单图刷新重读属性与评分",
                single is { Rating: 4 } && single.FileSize == sizeOnDisk,
                $"Rating={single.Rating} FileSize={single.FileSize} 期望 {sizeOnDisk}");
            c.True("单图刷新重建 EXIF", exifBefore != null && !ReferenceEquals(exifBefore, single.ExifData),
                exifBefore == null ? "刷新前 EXIF 为空" : "EXIF 对象未被重读");
        }
        else
        {
            c.Skip("单图刷新重读属性与评分", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
        }

        // 文件被删除后再激活：刷新失败必须有可见原因（STATE-02/STATE-03），不能静默。
        var failures = new List<string>();
        void OnOpenFailed(string message) => failures.Add(message);
        main.OpenFailed += OnOpenFailed;
        File.Delete(path);
        await main.OpenCoordinator.PublishAndWaitAsync([file], OpenRequestSource.DragDrop);
        main.OpenFailed -= OnOpenFailed;
        c.True("单图刷新失败上报 STATE-03",
            failures.Count == 1 && failures[0].Contains("single.jpg", StringComparison.Ordinal),
            failures.Count == 0 ? "未触发 OpenFailed" : string.Join(" | ", failures));
    }

    /// <summary>在途 EXIF/来源读取的过期结果不得覆盖刷新后的新来源。</summary>
    private static async Task CheckStaleMetadataAsync(Checks c, string root)
    {
        c.Section("过期元数据读取不覆盖新来源");
        var fixture = Path.Combine("release", "native-p2-fixtures", "color-srgb.jpg");
        if (!File.Exists(fixture))
        {
            c.Skip("过期读取不覆盖新评分", "缺少夹具 " + fixture);
            return;
        }

        var directory = Path.Combine(root, "stale");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "s.jpg");
        File.Copy(fixture, path, overwrite: true);
        await WriteRatingAsync(path, 3);

        var gated = new GatedFileRef(new LocalFileRef(path));
        var imageFile = new ImageFile(gated);
        await imageFile.LoadExifDataAsync();
        c.True("基线 EXIF 已加载", imageFile.IsExifLoaded && imageFile.Rating == 3,
            $"IsExifLoaded={imageFile.IsExifLoaded} Rating={imageFile.Rating}");

        var exifBefore = imageFile.ExifData;
        gated.Arm();
        var refresh = imageFile.RefreshMetadataAsync();
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        c.True("刷新期间保留旧 EXIF 展示",
            ReferenceEquals(exifBefore, imageFile.ExifData) && imageFile.Rating == 3,
            $"Rating={imageFile.Rating}");

        var newer = new XmpRatingSnapshot(XmpRatingState.Rated, 5, XmpRatingSource.Embedded);
        imageFile.ApplyRatingSources([new RatingSourceState(XmpRatingSource.Embedded, gated, newer)]);
        c.Equal("新来源立即可见", 5, imageFile.Rating);

        gated.Release();
        var applied = await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        c.True("被取代的刷新结果不应用", !applied, "过期刷新仍然写回了结果");
        c.Equal("过期读取不覆盖新评分", 5, imageFile.Rating);
        c.Equal("过期读取不复活旧来源", 1, imageFile.RatingSources.Count);
        c.True("刷新失败不保留旧加载态", !imageFile.IsExifLoading, "IsExifLoading 未收起");
        c.True("旧 EXIF 展示未被清空", imageFile.ExifData != null, "ExifData 变成 null");

        // 在途来源读取已经按旧索引拿到 sidecar，删除后完成也不得把该来源写回。
        var sidecarPath = Path.Combine(directory, "s.xmp");
        await File.WriteAllTextAsync(sidecarPath, SidecarPacket(rating: 5));
        var sidecarIndex = new RatingSidecarIndex();
        sidecarIndex.Add(new LocalFileRef(sidecarPath));
        var gatedRating = new GatedFileRef(new LocalFileRef(path));
        var ratingFile = new ImageFile(gatedRating);
        gatedRating.Arm();
        var staleRatings = ratingFile.LoadRatingOnlyAsync(sidecarIndex: sidecarIndex);
        await gatedRating.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var newerEmbedded = new XmpRatingSnapshot(XmpRatingState.Rated, 4, XmpRatingSource.Embedded);
        ratingFile.ApplyRatingSources([new RatingSourceState(XmpRatingSource.Embedded, gatedRating, newerEmbedded)]);
        File.Delete(sidecarPath);
        gatedRating.Release();
        await staleRatings.WaitAsync(TimeSpan.FromSeconds(10));
        c.Equal("过期来源读取不覆盖新评分", 4, ratingFile.Rating);
        c.True("删除的 sidecar 不被过期读取复活",
            ratingFile.RatingSources.All(source => source.Source != XmpRatingSource.Sidecar),
            "来源=" + string.Join(',', ratingFile.RatingSources.Select(source => source.Source.ToString())));

        var successFile = new ImageFile(new LocalFileRef(path));
        c.True("全新对象刷新成功", await successFile.RefreshMetadataAsync());
        c.True("刷新后基本属性就绪", successFile.FileSize != null && successFile.IsExifLoaded,
            $"FileSize={successFile.FileSize} IsExifLoaded={successFile.IsExifLoaded}");
    }

    /// <summary>跨目录显式打开：目标先上屏，父目录枚举失败按 OPEN-03 回退单图，而不是同目录 refreshMode 保留旧列表。</summary>
    private static async Task CheckCrossDirectoryFallbackAsync(Checks c, MainViewModel main, string root)
    {
        c.Section("跨目录打开失败回退单图");
        var previousFolder = main.FolderVM.CurrentFolder;
        var previousCount = main.FolderVM.AllFiles.Count;
        c.True("回退前已有目录列表", previousFolder != null && previousCount > 0,
            $"folder={previousFolder?.Name ?? "null"} count={previousCount}");
        if (previousFolder == null || previousCount == 0) return;

        var directory = Path.Combine(root, "cross-denied");
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, "target.jpg");
        if (!WritePhoto(targetPath))
        {
            c.Skip("跨目录打开失败回退单图", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
            return;
        }

        var denied = new FlakyFolderRef(new LocalFolderRef(directory)) { FailEnumeration = true };
        var target = new FolderFileRef(new LocalFileRef(targetPath), denied);
        var failures = new List<string>();
        void OnOpenFailed(string message) => failures.Add(message);
        main.OpenFailed += OnOpenFailed;
        var fits = 0;
        main.FitToViewRequested += () => fits++;
        await main.OpenCoordinator.PublishAndWaitAsync([target], OpenRequestSource.SystemOpen);
        main.OpenFailed -= OnOpenFailed;

        c.Equal("跨目录新目标重新 Fit", 1, fits);
        c.True("目标先上屏并按显式打开回退单图",
            main.CurrentFile != null && main.CurrentFile.File.StorageId == target.StorageId,
            "CurrentFile=" + (main.CurrentFile?.Name ?? "null"));
        c.Equal("回退单图只保留目标", 1, main.FolderVM.AllFiles.Count);
        c.True("回退单图不保留旧目录", main.FolderVM.CurrentFolder == null,
            "CurrentFolder=" + (main.FolderVM.CurrentFolder?.Name ?? "null"));
        c.True("跨目录失败给出 STATE 提示",
            failures.Count == 1 && failures[0].Contains("无法读取文件夹", StringComparison.Ordinal),
            failures.Count == 0 ? "未触发 OpenFailed" : string.Join(" | ", failures));
    }

    /// <summary>选择器取消/重开当前目标不改变视口；只有新目录按 resetView 回到适应态。</summary>
    private static async Task CheckPickerViewportAsync(Checks c, MainViewModel main, string root)
    {
        c.Section("选择器与视口");
        var currentFolder = main.FolderVM.CurrentFolder;
        var current = main.CurrentFile;
        c.True("选择器测试有当前目录与照片", currentFolder != null && current != null,
            $"folder={currentFolder?.Name ?? "null"} file={current?.Name ?? "null"}");
        if (currentFolder == null || current == null) return;

        var picker = new StubPicker();
        FilePicker.Initialize(picker);
        var fits = 0;
        main.FitToViewRequested += () => fits++;

        // 取消文件选择器：不 Fit、不改当前照片。
        picker.File = null;
        var before = fits;
        await main.FolderVM.OpenFilePickerAsync();
        c.Equal("取消选择器不 Fit", before, fits);
        c.True("取消选择器不改变当前照片", ReferenceEquals(current, main.CurrentFile),
            "CurrentFile=" + (main.CurrentFile?.Name ?? "null"));

        // 重开当前照片：不 Fit、不换对象。
        picker.File = current.File;
        before = fits;
        await main.FolderVM.OpenFilePickerAsync();
        c.Equal("选择器重开当前照片不 Fit", before, fits);
        c.True("选择器当前照片保留对象", ReferenceEquals(current, main.CurrentFile),
            "CurrentFile=" + (main.CurrentFile?.Name ?? "null"));

        // 重开当前目录：不 Fit，列表保留。
        picker.PreferFolderPicker = true;
        picker.Folder = currentFolder;
        before = fits;
        await main.FolderVM.OpenFilePickerAsync();
        c.Equal("选择器重开当前目录不 Fit", before, fits);
        c.True("选择器当前目录保留列表", main.FolderVM.AllFiles.Count > 0,
            $"count={main.FolderVM.AllFiles.Count}");

        // 新目录：按 resetView 回到适应态。
        var newDirectory = Path.Combine(root, "picker-new");
        Directory.CreateDirectory(newDirectory);
        if (!WritePhoto(Path.Combine(newDirectory, "a.jpg")))
        {
            c.Skip("选择器打开新目录 Fit", "缺少夹具 release/native-p2-fixtures/color-srgb.jpg");
            return;
        }

        picker.Folder = new LocalFolderRef(newDirectory);
        before = fits;
        await main.FolderVM.OpenFilePickerAsync();
        c.Equal("选择器打开新目录 Fit", before + 1, fits);
        c.True("选择器新目录成为当前目录",
            main.FolderVM.CurrentFolder?.StorageId == new LocalFolderRef(newDirectory).StorageId,
            "CurrentFolder=" + (main.FolderVM.CurrentFolder?.Name ?? "null"));
    }

    /// <summary>展示会话对同一 StorageId 的重复 SetFile 幂等：有效图时不换代，错误态允许重试。</summary>
    private static async Task CheckLoadSessionIdentityAsync(Checks c, string root)
    {
        c.Section("展示会话同一身份幂等");
        var fixture = Path.Combine("release", "native-p2-fixtures", "color-srgb.jpg");
        if (!File.Exists(fixture))
        {
            c.Skip("同一 StorageId 的 SetFile 不换代", "缺少夹具 " + fixture);
            return;
        }

        using var session = new PhotoLoadSession();
        session.SetFile(new LocalFileRef(fixture));
        var settled = await WaitUntilAsync(
            () => session.Image != null || !string.IsNullOrEmpty(session.Error),
            TimeSpan.FromSeconds(5));
        c.True("首帧解码有结果", settled, "5s 内既无图也无错误");
        if (session.Image != null)
        {
            var generation = session.Generation;
            session.SetFile(new LocalFileRef(fixture));
            c.Equal("有效图下同一 StorageId 不换代", generation, session.Generation);
        }
        else
        {
            c.Skip("有效图下同一 StorageId 不换代", "展示解码不可用：" + session.Error);
        }

        // 错误态：同一 StorageId 再次设置必须重新发起解码，不能因为短路卡死在错误页。
        var missingPath = Path.Combine(root, "missing-retry.jpg");
        using var retrySession = new PhotoLoadSession();
        retrySession.SetFile(new LocalFileRef(missingPath));
        var errored = await WaitUntilAsync(
            () => !string.IsNullOrEmpty(retrySession.Error),
            TimeSpan.FromSeconds(5));
        c.True("缺失文件进入错误态", errored, "5s 内未给出错误");
        var failedGeneration = retrySession.Generation;
        retrySession.SetFile(new LocalFileRef(missingPath));
        c.Equal("错误态同一 StorageId 允许重试", failedGeneration + 1, retrySession.Generation);

        var generationBeforeOther = session.Generation;
        session.SetFile(new LocalFileRef(Path.Combine(root, "refresh", "a.jpg")));
        c.Equal("不同 StorageId 的 SetFile 换代", generationBeforeOther + 1, session.Generation);
    }

    /// <summary>尽力注入一个测试展示解码器，便于验证“有有效图时不换代”；失败则相关子项跳过。</summary>
    private static void TryInitializeTestDecoder()
    {
        try
        {
            BitmapLoader.InitializePresentationDecoder(new TestPresentationDecoder(), new TestEncodingPolicy());
        }
        catch (InvalidOperationException)
        {
            // 进程内已有缓存/在途解码：保留既有口径，相关断言按 Skip 处理。
        }
    }

    /// <summary>放一张真实 JPEG（夹具可用时）或占位文件，返回是否用了真实夹具。</summary>
    private static bool WritePhoto(string path)
    {
        var fixture = Path.Combine("release", "native-p2-fixtures", "color-srgb.jpg");
        if (File.Exists(fixture))
        {
            File.Copy(fixture, path, overwrite: true);
            return true;
        }

        File.WriteAllText(path, Path.GetFileName(path));
        return false;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private static string DescribeAll(MainViewModel main) =>
        "列表=" + string.Join(',', main.FolderVM.AllFiles.Select(file => file.Name));

    private static string SidecarPacket(int rating) =>
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"" + rating + "\"/>"
        + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>父目录不可得的文件引用：强制走单图模式。</summary>
    private sealed class ParentlessFileRef : IFileRef
    {
        private readonly IFileRef _inner;

        public ParentlessFileRef(IFileRef inner) => _inner = inner;

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;

        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(cancellationToken);

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IFolderRef?>(null);
    }

    /// <summary>在首次读基本属性/打开文件时闸住，供测试在刷新在途时插入更新的一次评分写入。</summary>
    private sealed class GatedFileRef : IFileRef
    {
        private readonly IFileRef _inner;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public GatedFileRef(IFileRef inner) => _inner = inner;

        public Task Entered => _entered.Task;

        /// <summary>从下一次读取开始生效（基线读取要在布防前完成）。</summary>
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;

        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            IsArmed
                ? GatedAsync(() => _inner.OpenReadAsync(cancellationToken), cancellationToken)
                : _inner.OpenReadAsync(cancellationToken);

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            IsArmed
                ? GatedAsync(() => _inner.GetBasicPropertiesAsync(cancellationToken), cancellationToken)
                : _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            _inner.GetParentAsync(cancellationToken);

        private bool IsArmed => Volatile.Read(ref _armed) != 0;

        private async Task<T> GatedAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await read().ConfigureAwait(false);
        }
    }

    /// <summary>父目录可注入的文件引用（用于让刷新命中一个会拒绝枚举的目录）。</summary>
    private sealed class FolderFileRef : IFileRef
    {
        private readonly IFileRef _inner;
        private readonly IFolderRef _parent;

        public FolderFileRef(IFileRef inner, IFolderRef parent)
        {
            _inner = inner;
            _parent = parent;
        }

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;

        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(cancellationToken);

        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetBasicPropertiesAsync(cancellationToken);

        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IFolderRef?>(_parent);
    }

    /// <summary>首次枚举正常、之后按 <see cref="FailEnumeration"/> 拒绝的目录。</summary>
    private sealed class FlakyFolderRef : IFolderRef
    {
        private readonly IFolderRef _inner;

        public FlakyFolderRef(IFolderRef inner) => _inner = inner;

        public bool FailEnumeration { get; set; }

        public string Name => _inner.Name;
        public Uri Path => _inner.Path;
        public StorageItemId StorageId => _inner.StorageId;
        public StorageCapabilities Capabilities => _inner.Capabilities;

        public string? TryGetLocalPath() => _inner.TryGetLocalPath();

        public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (FailEnumeration)
                throw new StorageException(StorageError.AccessDenied, "注入目录拒绝");
            await foreach (var item in _inner.GetItemsAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task<IFileRef?> FindFileAsync(string name, CancellationToken cancellationToken = default) =>
            // 只拒绝目录枚举；逐文件存在性探测仍走真实目录（权限失败时刷新可访问项需要它）。
            _inner.FindFileAsync(name, cancellationToken);
    }

    /// <summary>测试用展示解码：Skia 解成 Bgra32，足够驱动 PhotoLoadSession 的“有效图”判定。</summary>
    private sealed class TestPresentationDecoder : IPresentationImageDecoder
    {
        public async Task<DecodedImage?> DecodeAsync(
            IFileRef file,
            int? maxLongEdge,
            PixelEncoding encoding,
            CancellationToken cancellationToken)
        {
            await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            return maxLongEdge is { } edge
                ? SkiaImageCodec.DecodeToLongEdge(stream, edge)
                : SkiaImageCodec.Decode(stream);
        }

        public Task<DecodedImage?> DecodeThumbnailAsync(
            IFileRef file,
            int minShortEdge,
            CancellationToken cancellationToken) => Task.FromResult<DecodedImage?>(null);
    }

    private sealed class TestEncodingPolicy : IPresentationEncodingPolicy
    {
        public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure) => PixelEncoding.Bgra32;
    }

    /// <summary>返回预设结果的文件选择器桩。</summary>
    private sealed class StubPicker : IFilePickerService
    {
        public bool PreferFolderPicker { get; set; }

        public IFolderRef? Folder { get; set; }

        public IFileRef? File { get; set; }

        public Task<IFolderRef?> PickFolderAsync(string title) => Task.FromResult(Folder);

        public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters) =>
            Task.FromResult(File);
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

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) =>
            Task.FromResult(func());
    }
}
