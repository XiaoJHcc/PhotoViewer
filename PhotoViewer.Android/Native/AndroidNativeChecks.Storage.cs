using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check storage</c>（验收专用）：经 <see cref="DebugTestDocumentsProvider"/> 的真实
/// <c>content://</c> 文档树核对 SAF 存储引用与写入链路——文档身份、嵌套目录枚举、缺失 size/mtime、
/// 内嵌/同名 sidecar XMP 写回（ARW 写内嵌且不创建 sidecar、已有 sidecar 随整组同步）、只读与非可定位拒绝、原子替换拒绝、Provider 拒绝访问与恢复，
/// 以及 ACTION_VIEW 经共享通道打开与同 UID 读取。Provider 与宿主同 UID，不验证第三方临时授权；
/// 全部素材只写 App 私有目录，不触碰用户照片；不模拟 MTP/USB 设备。
/// </summary>
internal static partial class AndroidNativeChecks
{
    private static readonly byte[] MinimalTiff =
    [
        0x49, 0x49, 0x2A, 0x00,             // II + magic 42
        0x08, 0x00, 0x00, 0x00,             // IFD0 偏移 8
        0x00, 0x00,                         // IFD0 条目数 0
        0x00, 0x00, 0x00, 0x00,             // 下一个 IFD = 0
    ];

    /// <summary>经典小端 TIFF（ARW 家族）：IFD0 单条 tag 700（BYTE）指向文件尾的 canonical XMP 包。</summary>
    /// <param name="rating">初始星级 0–5。</param>
    /// <returns>完整 TIFF 字节。</returns>
    private static byte[] BuildTiffWithXmpRating(int rating)
    {
        byte[] packet = BuildXmpPacket(rating, 512);
        const int ifdOffset = 8, entrySize = 12, entryCount = 1;
        int packetOffset = ifdOffset + 2 + entryCount * entrySize + 4;
        using var stream = new MemoryStream(packetOffset + packet.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write((uint)ifdOffset);
            writer.Write((ushort)entryCount);
            writer.Write((ushort)0x02BC);
            writer.Write((ushort)1);
            writer.Write((uint)packet.Length);
            writer.Write((uint)packetOffset);
            writer.Write(0u);
            writer.Write(packet);
        }

        return stream.ToArray();
    }

    /// <summary>Debug Provider writable 树中某个相对路径的 backing store 绝对路径（验收写夹具用）。</summary>
    /// <param name="relative">相对写树根的路径。</param>
    /// <returns>绝对路径。</returns>
    private static string PathForWritable(string relative) => IOPath.Combine(
        DebugTestDocumentsProvider.StoreRoot(Application.Context),
        DebugTestDocumentsProvider.WritableRoot,
        relative.Replace('/', IOPath.DirectorySeparatorChar));

    /// <summary>存储验收入口：重建 provider backing store 后按顺序核对七组语义。</summary>
    private static async Task RunStorageChecksAsync()
    {
        await WaitForScreenAsync();
        var activity = _activity!;
        var resolver = activity.ContentResolver ?? Application.Context.ContentResolver!;
        var providerUri = AndroidUri.Parse($"content://{DebugTestDocumentsProvider.TestAuthority}")!;
        try
        {
            ResetProviderStore(activity);
            DebugTestDocumentsProvider.SetRevoked(null);
            try
            {
                await CheckStorageIdentityAsync(resolver);
                await CheckNestedEnumerationAsync(resolver);
                await CheckWritableXmpAsync(resolver);
                await CheckReadonlyAndNonseekableAsync(resolver, activity);
                await CheckSidecarAndFileOpsAsync(resolver);
                await CheckRevocationAsync(resolver, providerUri);
                await CheckActionViewAsync(resolver);
            }
            finally
            {
                DebugTestDocumentsProvider.SetRevoked(null);
            }
        }
        catch (Exception exception)
        {
            Check("storage 流程异常", false, exception.ToString());
        }
    }

    /// <summary>重建三棵树与夹具：同名文件、子目录、缺失元数据文件、最小 TIFF 与只读/管道树。</summary>
    private static void ResetProviderStore(Activity activity)
    {
        string root = DebugTestDocumentsProvider.StoreRoot(activity);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        DebugTestDocumentsProvider.EnsureStore(activity);

        string writable = IOPath.Combine(root, DebugTestDocumentsProvider.WritableRoot);
        string sub = IOPath.Combine(writable, "sub");
        Directory.CreateDirectory(sub);
        WriteCheckJpeg(IOPath.Combine(writable, "photo.jpg"), 800, 600, rating: 0, xmpPadding: 2048);
        WriteCheckJpeg(IOPath.Combine(writable, "unknown-meta.jpg"), 320, 200, rating: 0);
        WriteCheckJpeg(IOPath.Combine(sub, "photo.jpg"), 640, 480, rating: 0);
        WriteCheckJpeg(IOPath.Combine(sub, "nested.jpg"), 320, 200, rating: 0);
        File.WriteAllBytes(IOPath.Combine(writable, "photo.arw"), MinimalTiff);

        string readonlyRoot = IOPath.Combine(root, DebugTestDocumentsProvider.ReadonlyRoot);
        string readonlyFile = IOPath.Combine(readonlyRoot, "photo.jpg");
        File.Copy(IOPath.Combine(writable, "photo.jpg"), readonlyFile, overwrite: true);
        File.SetUnixFileMode(readonlyFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        string nonseekableRoot = IOPath.Combine(root, DebugTestDocumentsProvider.NonseekableRoot);
        File.Copy(IOPath.Combine(writable, "photo.jpg"),
            IOPath.Combine(nonseekableRoot, "stream.jpg"), overwrite: true);
    }

    /// <summary>1) 身份：同一 document 经不同 tree URI 同 StorageId；同名不同文档必须不同。</summary>
    private static async Task CheckStorageIdentityAsync(ContentResolver resolver)
    {
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);
        var treeNested = TreeUri(DebugTestDocumentsProvider.WritableRoot + "/sub");
        var docViaRoot = DocumentUri(treeRoot, "writable/sub/photo.jpg");
        var docViaNested = DocumentUri(treeNested, "writable/sub/photo.jpg");
        var refViaRoot = new AndroidFileRef(resolver, docViaRoot, treeRoot);
        var refViaNested = new AndroidFileRef(resolver, docViaNested, treeNested);
        Check("storage 同一 document 不同 tree URI 同身份",
            refViaRoot.StorageId == refViaNested.StorageId && docViaRoot != docViaNested,
            $"root={refViaRoot.StorageId.Value} nested={refViaNested.StorageId.Value} uris={docViaRoot}/{docViaNested}");

        var writablePhoto = new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot);
        var readonlyTree = TreeUri(DebugTestDocumentsProvider.ReadonlyRoot);
        var readonlyPhoto = new AndroidFileRef(resolver, DocumentUri(readonlyTree, "readonly/photo.jpg"), readonlyTree);
        var props = await writablePhoto.GetBasicPropertiesAsync();
        Check("storage 同名不同文档不同身份",
            writablePhoto.Name == readonlyPhoto.Name && writablePhoto.StorageId != readonlyPhoto.StorageId
            && props.Size > 0,
            $"name={writablePhoto.Name} writable={writablePhoto.StorageId.Value} "
            + $"readonly={readonlyPhoto.StorageId.Value} size={props.Size}");
    }

    /// <summary>2) 嵌套目录枚举必须返回真实子目录内容；size=-1/mtime=null 必须保持 null。</summary>
    private static async Task CheckNestedEnumerationAsync(ContentResolver resolver)
    {
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);
        var rootFolder = new AndroidFolderRef(resolver, treeRoot, null);
        var rootItems = await ReadItemsAsync(rootFolder);
        var subFolder = rootItems.OfType<IFolderRef>()
            .FirstOrDefault(folder => folder.Name == "sub");
        if (subFolder == null)
        {
            Check("storage 嵌套目录枚举", false, "根枚举未返回 sub 目录");
            return;
        }

        var itemsFromRoot = await ReadItemsAsync(subFolder);
        var nestedTreeFolder = new AndroidFolderRef(
            resolver, TreeUri(DebugTestDocumentsProvider.WritableRoot + "/sub"), null);
        var itemsFromNestedTree = await ReadItemsAsync(nestedTreeFolder);
        var expected = new[] { "nested.jpg", "photo.jpg" };
        var namesRoot = itemsFromRoot.Select(item => item.Name).OrderBy(name => name).ToArray();
        var namesNested = itemsFromNestedTree.Select(item => item.Name).OrderBy(name => name).ToArray();
        Check("storage 嵌套目录枚举真实子目录内容",
            namesRoot.SequenceEqual(expected) && namesNested.SequenceEqual(expected),
            $"fromRoot={string.Join(",", namesRoot)} fromNestedTree={string.Join(",", namesNested)} "
            + $"rootCount={rootItems.Count}");

        var unknown = new AndroidFileRef(resolver,
            DocumentUri(treeRoot, "writable/unknown-meta.jpg"), treeRoot);
        var props = await unknown.GetBasicPropertiesAsync();
        var fromEnumeration = rootItems.OfType<IFileRef>()
            .FirstOrDefault(file => file.Name == "unknown-meta.jpg");
        var enumeratedProps = fromEnumeration == null ? null : await fromEnumeration.GetBasicPropertiesAsync();
        Check("storage size=-1/mtime=null 不变成 ulong 巨值",
            props.Size == null && props.DateModified == null
            && enumeratedProps is { Size: null, DateModified: null },
            $"direct={props.Size?.ToString() ?? "null"}/{props.DateModified?.ToString() ?? "null"} "
            + $"enumerated={enumeratedProps?.Size?.ToString() ?? "null"}/{enumeratedProps?.DateModified?.ToString() ?? "null"}");
    }

    /// <summary>3) 可写 content URI 的内嵌 XMP 原位写回：语义回读 4，非目标字节不变。</summary>
    private static async Task CheckWritableXmpAsync(ContentResolver resolver)
    {
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);
        var photo = new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot);
        byte[] before = await ReadAllBytesAsync(photo);
        var head = new byte[4];
        int headRead;
        long sessionLength;
        await using (var session = await photo.OpenRandomAccessAsync(StorageAccessMode.Read))
        {
            sessionLength = session.Length;
            headRead = await session.ReadAsync(0, head);
        }
        Check("storage 可写 content URI 随机读",
            before.Length > 4 && before[0] == 0xFF && before[1] == 0xD8
            && sessionLength == before.Length && headRead == 4 && head[0] == 0xFF && head[1] == 0xD8,
            $"stream={before.Length}:{before[0]:X2}{before[1]:X2} session={sessionLength}:{head[0]:X2}{head[1]:X2} read={headRead}");
        var adapter = new JpegXmpContainerAdapter();
        var packet = await adapter.ReadCanonicalPacketAsync(photo);
        byte[]? inPlace = null;
        string inPlaceError = "none";
        try
        {
            inPlace = packet == null ? null : XmpPacketEditor.SetRating(packet.Data, 4, preserveLength: true);
        }
        catch (Exception exception)
        {
            inPlaceError = exception.Message;
        }
        Check("storage XMP 原位编辑可行",
            packet != null && inPlace != null && inPlace.Length == packet.Data.Length,
            $"packet={packet?.Data.Length.ToString() ?? "null"} inPlace={inPlace?.Length.ToString() ?? "null"} error={inPlaceError}");

        var store = XmpRatingStoreFactory.Create(photo);
        await store.WriteAsync(4, safeMode: true);
        var snapshot = await store.ReadAsync();
        var embedded = await XmpRatingReader.ReadEmbeddedAsync(photo);
        byte[] after = await ReadAllBytesAsync(photo);

        var differences = new List<int>();
        for (int index = 0; index < Math.Min(before.Length, after.Length); index++)
            if (before[index] != after[index]) differences.Add(index);
        long packetOffset = packet?.Offset ?? -1;
        long packetEnd = packet == null ? -1 : packet.Offset + packet.Length;
        // 原位写回允许 XMP 包内被重新序列化（填充空白变化），包外（JPEG 图像数据等）必须逐字节不变。
        bool confinedToPacket = differences.Count > 0
            && differences.All(index => index >= packetOffset && index < packetEnd);
        Check("storage 可写 content URI XMP 原位写回",
            snapshot.ValueOrZero == 4 && embedded.ValueOrZero == 4
            && before.Length == after.Length && confinedToPacket,
            $"store={snapshot.ValueOrZero} embedded={embedded.ValueOrZero} length={before.Length}->{after.Length} "
            + $"diffs={differences.Count} packet={packetOffset}+{packet?.Length ?? 0}");
    }

    /// <summary>4) 只读树的 rw 拒绝、非可定位树的 NotSeekable，且两者都不改动原字节。</summary>
    private static async Task CheckReadonlyAndNonseekableAsync(ContentResolver resolver, Activity activity)
    {
        var readonlyTree = TreeUri(DebugTestDocumentsProvider.ReadonlyRoot);
        var readonlyPhoto = new AndroidFileRef(resolver,
            DocumentUri(readonlyTree, "readonly/photo.jpg"), readonlyTree);
        string readonlyPath = DebugTestDocumentsProvider.PathFor(activity, "readonly/photo.jpg");
        byte[] readonlyBefore = File.ReadAllBytes(readonlyPath);
        var readonlyStore = XmpRatingStoreFactory.Create(readonlyPhoto);
        var writeFailure = await CatchStorageAsync(() => readonlyStore.WriteAsync(3, safeMode: true));
        byte[] readonlyAfter = File.ReadAllBytes(readonlyPath);
        Check("storage 只读 content URI 写回 typed 失败且字节不变",
            writeFailure.Error is StorageError.AccessDenied or StorageError.ReadOnly
            && readonlyBefore.AsSpan().SequenceEqual(readonlyAfter),
            $"error={writeFailure.Error?.ToString() ?? "none"} message={writeFailure.Message} "
            + $"bytes={readonlyBefore.Length}->{readonlyAfter.Length}");

        var directFailure = await CatchStorageAsync(async () =>
        {
            await using var session = await readonlyPhoto.OpenRandomAccessAsync(StorageAccessMode.ReadWrite);
            await session.WriteAsync(0, new byte[] { 0x00 });
        });
        Check("storage 只读树拒绝 rw 打开",
            directFailure.Error is StorageError.AccessDenied or StorageError.ReadOnly,
            $"error={directFailure.Error?.ToString() ?? "none"} message={directFailure.Message}");

        var nonseekableTree = TreeUri(DebugTestDocumentsProvider.NonseekableRoot);
        var streamPhoto = new AndroidFileRef(resolver,
            DocumentUri(nonseekableTree, "nonseekable/stream.jpg"), nonseekableTree);
        string streamPath = DebugTestDocumentsProvider.PathFor(activity, "nonseekable/stream.jpg");
        byte[] streamBefore = File.ReadAllBytes(streamPath);

        using (var sequential = await streamPhoto.OpenReadAsync())
        using (var buffer = new MemoryStream())
        {
            await sequential.CopyToAsync(buffer);
            Check("storage nonseekable 顺序读取可用",
                buffer.ToArray().AsSpan().SequenceEqual(streamBefore),
                $"read={buffer.Length} expected={streamBefore.Length}");
        }

        var randomRead = await CatchStorageAsync(async () =>
        {
            await using var session = await streamPhoto.OpenRandomAccessAsync(StorageAccessMode.Read);
            await session.ReadAsync(0, new byte[16]);
        });
        var randomWrite = await CatchStorageAsync(async () =>
        {
            await using var session = await streamPhoto.OpenRandomAccessAsync(StorageAccessMode.ReadWrite);
            await session.WriteAsync(0, new byte[] { 0xAA, 0xBB });
        });
        byte[] streamAfter = File.ReadAllBytes(streamPath);
        Check("storage nonseekable 随机读写明确 NotSeekable 且不整文件重写",
            randomRead.Error == StorageError.NotSeekable && randomWrite.Error == StorageError.NotSeekable
            && streamBefore.AsSpan().SequenceEqual(streamAfter),
            $"read={randomRead.Error?.ToString() ?? "none"} write={randomWrite.Error?.ToString() ?? "none"} "
            + $"bytes={streamBefore.Length}->{streamAfter.Length}");
    }

    /// <summary>5) RAW 内嵌写入不建 sidecar、已有 sidecar 随整组同步、CreateFile 非法名、AtomicWrite 明确 Unsupported。</summary>
    private static async Task CheckSidecarAndFileOpsAsync(ContentResolver resolver)
    {
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);
        var folder = new AndroidFolderRef(resolver, treeRoot, null);
        // 无 tag 700 的 TIFF：内嵌来源不能安全新增，明确 Unsupported；不得用 sidecar 顶替。
        var arw = new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.arw"), treeRoot);
        var store = XmpRatingStoreFactory.Create(arw);
        var unsupported = await CatchStorageAsync(() => store.WriteAsync(3, safeMode: true));
        var sidecarAfterReject = await folder.FindFileAsync("photo.xmp");
        Check("storage ARW 无 tag 700 拒绝内嵌写入且不建 sidecar",
            unsupported.Error == StorageError.Unsupported && sidecarAfterReject == null,
            $"write={unsupported.Error?.ToString() ?? "none"} {unsupported.Message} "
            + $"sidecar={sidecarAfterReject?.Name ?? "null"}");

        // 有 tag 700 的 TIFF（ARW 家族）：写内嵌成功，且仍然不创建 sidecar。
        File.WriteAllBytes(PathForWritable("photo.arw"), BuildTiffWithXmpRating(1));
        var embedded = new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.arw"), treeRoot);
        var embeddedStore = XmpRatingStoreFactory.Create(embedded);
        var embeddedWrite = await CatchStorageAsync(() => embeddedStore.WriteAsync(3, safeMode: true));
        var embeddedRead = await embeddedStore.ReadAsync();
        var sidecarAfterEmbedded = await folder.FindFileAsync("photo.xmp");
        Check("storage ARW 写内嵌且不创建 sidecar",
            embeddedWrite.Error == null && embeddedRead.ValueOrZero == 3 && sidecarAfterEmbedded == null,
            $"write={embeddedWrite.Error?.ToString() ?? "ok"} {embeddedWrite.Message} "
            + $"embedded={embeddedRead.ValueOrZero} sidecar={sidecarAfterEmbedded?.Name ?? "null"}");

        // 已存在的同名 sidecar 是独立来源：整组写入必须同时更新内嵌与 sidecar。
        var sidecarFile = await folder.CreateFileAsync("photo.xmp");
        byte[] packet = BuildXmpPacket(2);
        await using (var write = await sidecarFile.OpenRandomAccessAsync(StorageAccessMode.ReadWrite))
        {
            await write.WriteAsync(0, packet);
            await write.SetLengthAsync(packet.Length);
            await write.FlushAsync();
        }

        var image = new ImageFile(embedded);
        var group = PhotoGroupMap.Build([image], false, [new PhotoFormatRule("arw", "ARW", [".arw"])])
            .GroupFor(image)!;
        var groupWrite = await new RatingService().SetRatingAsync(group, 4, safeMode: true);
        var embeddedAfterGroup = (await XmpRatingReader.ReadEmbeddedAsync(embedded)).ValueOrZero;
        var sidecarAfterGroup = (await XmpRatingReader.ReadSidecarAsync(embedded))?.ValueOrZero ?? -1;
        Check("storage 已有 sidecar 随整组同步",
            groupWrite.IsApplied && embeddedAfterGroup == 4 && sidecarAfterGroup == 4,
            $"outcome={groupWrite.Outcome} embedded={embeddedAfterGroup} sidecar={sidecarAfterGroup} "
            + $"reason={groupWrite.FailureReason ?? "none"}");

        if (sidecarFile != null) await sidecarFile.DeleteAsync();
        var afterDelete = await embeddedStore.ReadAsync();
        var sidecarAfter = await folder.FindFileAsync("photo.xmp");
        var sourcesAfterDelete = await RatingSourceReader.ReadAsync(embedded, RatingSidecarIndex.Empty);
        Check("storage sidecar 删除后不再作为来源",
            sidecarAfter == null && afterDelete.ValueOrZero == 4
            && sourcesAfterDelete.Sources.Count == 1
            && sourcesAfterDelete.Sources.All(source => source.Source == XmpRatingSource.Embedded),
            $"sidecar={sidecarAfter?.Name ?? "null"} embedded={afterDelete.ValueOrZero} "
            + $"sources={sourcesAfterDelete.Sources.Count}");

        var invalid = new List<string>();
        foreach (string name in new[] { "bad/name.jpg", " ", ".." })
        {
            var failure = await CatchStorageAsync(() => folder.CreateFileAsync(name));
            if (failure.Error != StorageError.InvalidName) invalid.Add($"{name}→{failure.Error?.ToString() ?? "none"}");
        }
        Check("storage CreateFile 非法名 typed InvalidName", invalid.Count == 0,
            invalid.Count == 0 ? "bad/name.jpg、空格、.. 均拒绝" : string.Join("；", invalid));

        var probe = await folder.CreateFileAsync("probe-write.jpg");
        var probeWrite = await CatchStorageAsync(async () =>
        {
            await using var session = await probe.OpenRandomAccessAsync(StorageAccessMode.ReadWrite);
            await session.WriteAsync(0, new byte[] { 1, 2, 3 });
            await session.FlushAsync();
        });
        var probeRead = await CatchStorageAsync(async () =>
        {
            byte[] bytes = await ReadAllBytesAsync(probe);
            if (bytes.Length != 3 || bytes[0] != 1 || bytes[1] != 2 || bytes[2] != 3)
                throw new InvalidOperationException($"回读不一致：{Convert.ToHexString(bytes)}");
        });
        Check("storage CreateFile 后可随机读写",
            probeWrite.Error == null && probeRead.Error == null,
            $"url={probe.Path} write={probeWrite.Error?.ToString() ?? "ok"} {probeWrite.Message} "
            + $"read={probeRead.Error?.ToString() ?? "ok"} {probeRead.Message}");
        try { await probe.DeleteAsync(); } catch (Exception) { }

        var photo = new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot);
        byte[] before = await ReadAllBytesAsync(photo);
        var atomic = await CatchStorageAsync(() => photo.AtomicWriteAsync(
            async (stream, token) => await stream.WriteAsync(new byte[] { 1, 2, 3 }, token)));
        byte[] after = await ReadAllBytesAsync(photo);
        var names = (await ReadItemsAsync(folder)).Select(item => item.Name).ToArray();
        Check("storage AtomicWrite 明确 Unsupported 且不伪造替换",
            atomic.Error == StorageError.Unsupported
            && before.AsSpan().SequenceEqual(after)
            && names.All(name => !name.Contains("tmp", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("atomic", StringComparison.OrdinalIgnoreCase)),
            $"error={atomic.Error?.ToString() ?? "none"} bytes={before.Length}->{after.Length} names={string.Join(",", names)}");
    }

    /// <summary>6) Debug Provider 注入拒绝访问后所有访问都是 typed AccessDenied；恢复后测试环境还原（不是系统 persisted grant 撤销）。</summary>
    private static async Task CheckRevocationAsync(ContentResolver resolver, AndroidUri providerUri)
    {
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);
        var folder = new AndroidFolderRef(resolver, treeRoot, null);
        string path = DebugTestDocumentsProvider.PathFor(_activity!, "writable/photo.jpg");
        byte[] before = File.ReadAllBytes(path);
        // 测试开关：让 Provider 对该树根抛 SecurityException，模拟“访问被拒绝”，
        // 不调用 ContentResolver.ReleasePersistableUriPermission，也不声称验证了系统授权撤销。
        resolver.Call(providerUri, "revoke", DebugTestDocumentsProvider.WritableRoot, null);
        try
        {
            var query = await CatchStorageAsync(async () =>
                await new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot)
                    .GetBasicPropertiesAsync());
            var enumeration = await CatchStorageAsync(() => ReadItemsAsync(folder));
            var write = await CatchStorageAsync(() =>
                XmpRatingStoreFactory.Create(
                    new AndroidFileRef(resolver, DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot))
                    .WriteAsync(2, safeMode: true));
            Check("storage Provider 拒绝访问后 typed AccessDenied",
                query.Error == StorageError.AccessDenied && enumeration.Error == StorageError.AccessDenied
                && write.Error == StorageError.AccessDenied,
                $"query={query.Error?.ToString() ?? "none"} list={enumeration.Error?.ToString() ?? "none"} "
                + $"write={write.Error?.ToString() ?? "none"}");
        }
        finally
        {
            resolver.Call(providerUri, "grant", null, null);
        }

        var restored = await new AndroidFileRef(resolver,
            DocumentUri(treeRoot, "writable/photo.jpg"), treeRoot).GetBasicPropertiesAsync();
        var restoredItems = await ReadItemsAsync(folder);
        byte[] after = File.ReadAllBytes(path);
        Check("storage Provider 恢复访问后测试环境还原",
            restored.Size > 0 && restoredItems.Count > 0 && before.AsSpan().SequenceEqual(after),
            $"size={restored.Size} items={restoredItems.Count} bytes={before.Length}->{after.Length}");
    }

    /// <summary>7) ACTION_VIEW：系统真实投递一次，再经共享通道打开，均落到同一 StorageId；Provider 与宿主同 UID，不证明第三方临时授权。</summary>
    private static async Task CheckActionViewAsync(ContentResolver resolver)
    {
        var main = _main!;
        var session = _session!;
        var activity = _activity!;
        var treeRoot = TreeUri(DebugTestDocumentsProvider.WritableRoot);

        // 7a. 真实系统投递：显式指向原生 Activity，由 ActivityManager 处理 FLAG_GRANT_READ_URI_PERMISSION。
        // Provider 与宿主同 UID，同 UID 不会产生可观测的 URI 授权记录；本段只验证共享打开落点与同 UID 真实读取。
        var photoUri = DocumentUri(treeRoot, "writable/photo.jpg");
        var expected = new AndroidFileRef(resolver, photoUri).StorageId;
        var external = new Intent(Intent.ActionView, photoUri);
        external.SetClass(activity, typeof(AndroidNativeActivity));
        external.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(external);
        try
        {
            await WaitAsync(() => main.CurrentFile?.File.StorageId == expected, "系统 ACTION_VIEW 定位", 20000);
        }
        catch (TimeoutException exception)
        {
            Check("storage ACTION_VIEW content URI 落到同一 StorageId", false, exception.Message);
            return;
        }
        var current = main.CurrentFile?.File;
        var providerInfo = activity.PackageManager?.ResolveContentProvider(
            photoUri.Authority!, PackageInfoFlags.MatchDefaultOnly);
        bool sameApp = providerInfo != null
            && (string.Equals(providerInfo.PackageName, activity.PackageName, StringComparison.Ordinal)
                || providerInfo.ApplicationInfo?.Uid == Process.MyUid());
        bool persisted = resolver.PersistedUriPermissions
            .Any(permission => string.Equals(permission.Uri?.ToString(), photoUri.ToString(), StringComparison.Ordinal));
        int readBytes = 0;
        int firstByte = -1;
        string readDetail;
        try
        {
            using var stream = await current!.OpenReadAsync();
            var head = new byte[1];
            readBytes = await stream.ReadAsync(head.AsMemory(0, 1));
            if (readBytes > 0) firstByte = head[0];
            readDetail = "ok";
        }
        catch (Exception exception)
        {
            readDetail = $"{exception.GetType().Name}：{exception.Message}";
        }
        Check("storage ACTION_VIEW content URI 落到同一 StorageId",
            current != null && current.StorageId == expected && current.Path.Scheme == ContentResolver.SchemeContent
            && current.TryGetLocalPath() == null,
            $"current={current?.StorageId.Value ?? "null"} expected={expected.Value} scheme={current?.Path.Scheme ?? "null"}");
        Check("storage ACTION_VIEW 同 UID 可读且不伪造持久授权",
            sameApp && !persisted && readBytes > 0,
            $"sameApp={sameApp} persisted={persisted} read={readBytes}B first=0x{firstByte:X2} {readDetail}");

        // 7b. 经共享 HandleIntentAsync 直接投递另一张，验证请求通道自己的落点。
        var nestedUri = DocumentUri(TreeUri(DebugTestDocumentsProvider.WritableRoot + "/sub"),
            "writable/sub/photo.jpg");
        var nestedExpected = new AndroidFileRef(resolver, nestedUri).StorageId;
        var direct = new Intent(Intent.ActionView, nestedUri);
        direct.AddFlags(ActivityFlags.GrantReadUriPermission);
        await AndroidNativeShell.HandleIntentAsync(direct);
        Check("storage HandleIntentAsync content URI 落到同一 StorageId",
            main.CurrentFile?.File.StorageId == nestedExpected,
            $"current={main.CurrentFile?.File.StorageId.Value ?? "null"} expected={nestedExpected.Value}");

        await WaitAsync(() => session.Image != null, "content URI 首图上屏", 20000);
        Check("storage ACTION_VIEW 后 content URI 可解码上屏",
            session.Image != null,
            $"image={session.Image?.Width}x{session.Image?.Height} src={session.Image?.SourceWidth}x{session.Image?.SourceHeight}");
    }

    private static AndroidUri TreeUri(string documentId) =>
        DocumentsContract.BuildTreeDocumentUri(DebugTestDocumentsProvider.TestAuthority, documentId)!;

    private static AndroidUri DocumentUri(AndroidUri treeUri, string documentId) =>
        DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId)!;

    private static async Task<List<IStorageItemRef>> ReadItemsAsync(IFolderRef folder)
    {
        var items = new List<IStorageItemRef>();
        await foreach (var item in folder.GetItemsAsync())
            items.Add(item);
        return items;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFileRef file)
    {
        using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>捕获共享存储异常并保留 typed 错误；非存储异常按无错误码返回类型名。</summary>
    private static async Task<(StorageError? Error, string Message)> CatchStorageAsync(Func<Task> action)
    {
        try
        {
            await action();
            return (null, "无异常");
        }
        catch (StorageException exception)
        {
            string detail = exception.Message;
            if (exception.InnerException is { } inner) detail += " ← " + inner.Message;
            return (exception.Error, detail);
        }
        catch (Exception exception)
        {
            return (null, $"{exception.GetType().Name}：{exception.Message}");
        }
    }
}
