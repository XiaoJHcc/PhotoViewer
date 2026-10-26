using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PhotoViewer.Core;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>
/// 容器 XMP 的共享字节与结构异常回归：真实 ARW 往返，以及合成的 TIFF/HEIF 拒绝样例。
/// 全部用例只操作临时目录里的副本。
/// </summary>
internal static class ContainerSafetyRegressionChecks
{
    public static Task RunAsync(Checks c, string root) =>
        RunAsync(c, root, path => new LocalFileRef(path));

    internal static async Task RunAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        c.Section("容器 XMP 安全回归：共享字节与结构异常");
        await CheckArwRoundTripAsync(c, root, fileRef);
        await CheckTiffControlAsync(c, root, fileRef);
        await CheckTiffSharedStripAsync(c, root, fileRef);
        await CheckTiffSharedTagDataAsync(c, root, fileRef);
        await CheckTiffDuplicateXmpAsync(c, root, fileRef);
        await CheckTiffDirectoryCycleAsync(c, root, fileRef);
        await CheckHeifControlAsync(c, root, fileRef);
        await CheckHeifSharedExtentAsync(c, root, fileRef);
        await CheckHeifDuplicateIlocAsync(c, root, fileRef);
        await CheckHeifDuplicateMetaAsync(c, root, fileRef);
        await CheckHeifMultipleCandidatesAsync(c, root, fileRef);
        await CheckHeifReferenceItemAsync(c, root, fileRef);
        await CheckHeifIdatItemAsync(c, root, fileRef);
        await CheckHeifIdatBaseOverflowAsync(c, root, fileRef);
        await CheckHeifIdatAbsoluteAsync(c, root, fileRef);
    }

    private static readonly string[] ArwFixtures =
    [
        "TestCase/A7C01489.ARW",
        "TestCase/A7C01495.ARW",
    ];

    private static async Task CheckArwRoundTripAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        foreach (var fixture in ArwFixtures)
        {
            var label = Path.GetFileNameWithoutExtension(fixture);
            if (!File.Exists(fixture))
            {
                c.Skip($"{label} 0..5/-1 往返", "缺少夹具 " + fixture);
                continue;
            }

            var path = Path.Combine(root, label + "-roundtrip.arw");
            File.Copy(fixture, path, overwrite: true);
            var originalHash = await HashAsync(path);
            var file = fileRef(path);
            var store = XmpRatingStoreFactory.Create(file);
            var adapter = new TiffXmpContainerAdapter();
            var canonicalBefore = await adapter.ReadCanonicalPacketAsync(file);
            c.True($"{label} 能定位 canonical XMP", canonicalBefore is { Length: > 0 },
                canonicalBefore is null ? "适配器未定位到 tag 700" : "包为空");

            foreach (var rating in new[] { 0, 1, 2, 3, 4, 5, -1 })
            {
                var receipt = await store.WriteAsync(rating, safeMode: true);
                var expectedState = rating == -1 ? XmpRatingState.Rejected : XmpRatingState.Rated;
                c.Equal($"{label} 写 {rating} 后状态", expectedState, receipt.After.State);
                c.Equal($"{label} 写 {rating} 后取值", rating, receipt.After.Value ?? -1);

                var readback = await adapter.ReadCanonicalPacketAsync(file);
                var snapshot = XmpRatingReader.ReadPacket(readback!.Data, XmpRatingSource.Embedded);
                c.Equal($"{label} 写 {rating} 后 canonical 回读", rating, snapshot.Value ?? -1);

                if (receipt.NoChange)
                {
                    c.Equal($"{label} 写 {rating} 为同值不写盘", originalHash, await HashAsync(path));
                    continue;
                }

                await store.RestoreAsync(receipt);
                c.Equal($"{label} 写 {rating} 后补偿恢复原文件", originalHash, await HashAsync(path));
            }
        }
    }

    private static async Task CheckTiffControlAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = TiffXmpPacket(3);
        var path = Path.Combine(root, "tiff-control.tif");
        await File.WriteAllBytesAsync(path, BuildTiff(packet, stripOffset: 134 + packet.Length, stripLength: 16, duplicateXmp: false, cycle: false, sharedTagData: false));
        var original = await File.ReadAllBytesAsync(path);
        var file = fileRef(path);
        var adapter = new TiffXmpContainerAdapter();

        var canonical = await adapter.ReadCanonicalPacketAsync(file);
        c.True("合法 TIFF 能定位 canonical XMP", canonical is { Offset: 134 },
            canonical is null ? "未定位到包" : "偏移=" + canonical.Offset);
        c.Equal("合法 TIFF canonical Rating", 3,
            XmpRatingReader.ReadPacket(canonical!.Data, XmpRatingSource.Embedded).Value);

        var store = XmpRatingStoreFactory.Create(file);
        var receipt = await store.WriteAsync(4, safeMode: true);
        c.Equal("合法 TIFF 经 store 写入", 4, receipt.After.Value);
        await store.RestoreAsync(receipt);
        c.Bytes("合法 TIFF 补偿后字节还原", original, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// 已复现样本：XMP 数据与 StripOffsets/StripByteCounts 指向同一范围。
    /// 旧路径会“Applied 同时改像素”，这里必须零写拒绝。
    /// </summary>
    private static async Task CheckTiffSharedStripAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = TiffXmpPacket(3);
        var path = Path.Combine(root, "tiff-shared-strip.tif");
        await File.WriteAllBytesAsync(path, BuildTiff(
            packet,
            stripOffset: 134,
            stripLength: packet.Length,
            duplicateXmp: false,
            cycle: false,
            sharedTagData: false));
        await AssertRejectedAsync(c, path, new TiffXmpContainerAdapter(), "TIFF XMP 与 strip 数据共用范围", fileRef);
    }

    private static async Task CheckTiffSharedTagDataAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = TiffXmpPacket(3);
        var path = Path.Combine(root, "tiff-shared-tag.tif");
        await File.WriteAllBytesAsync(path, BuildTiff(
            packet,
            stripOffset: 134 + packet.Length,
            stripLength: 16,
            duplicateXmp: false,
            cycle: false,
            sharedTagData: true));
        await AssertRejectedAsync(c, path, new TiffXmpContainerAdapter(), "TIFF XMP 与其它 tag 外置数据共用范围", fileRef);
    }

    private static async Task CheckTiffDuplicateXmpAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = TiffXmpPacket(3);
        var path = Path.Combine(root, "tiff-duplicate-xmp.tif");
        await File.WriteAllBytesAsync(path, BuildTiff(
            packet,
            stripOffset: 146 + packet.Length,
            stripLength: 16,
            duplicateXmp: true,
            cycle: false,
            sharedTagData: false));
        await AssertRejectedAsync(c, path, new TiffXmpContainerAdapter(), "TIFF 重复 tag 700", fileRef);
    }

    private static async Task CheckTiffDirectoryCycleAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = TiffXmpPacket(3);
        var path = Path.Combine(root, "tiff-cycle.tif");
        await File.WriteAllBytesAsync(path, BuildTiff(
            packet,
            stripOffset: 134 + packet.Length,
            stripLength: 16,
            duplicateXmp: false,
            cycle: true,
            sharedTagData: false));
        await AssertRejectedAsync(c, path, new TiffXmpContainerAdapter(), "TIFF IFD 链循环", fileRef);
    }

    private static async Task CheckHeifControlAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var packet = HeifXmpPacket(3);
        var path = Path.Combine(root, "heif-control.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(packet, overlap: false, duplicateIloc: false, duplicateMeta: false, multipleCandidates: false));
        var original = await File.ReadAllBytesAsync(path);
        var file = fileRef(path);
        var adapter = new HeifXmpContainerAdapter();

        var canonical = await adapter.ReadCanonicalPacketAsync(file);
        c.True("合法 HEIF 能定位 canonical XMP", canonical is { Length: > 0 },
            canonical is null ? "未定位到 XMP 项" : "包为空");

        var store = XmpRatingStoreFactory.Create(file);
        var receipt = await store.WriteAsync(4, safeMode: true);
        c.Equal("合法 HEIF 经 store 写入", 4, receipt.After.Value);
        await store.RestoreAsync(receipt);
        c.Bytes("合法 HEIF 补偿后字节还原", original, await File.ReadAllBytesAsync(path));
    }

    private static async Task CheckHeifSharedExtentAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-shared-extent.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3), overlap: true, duplicateIloc: false, duplicateMeta: false, multipleCandidates: false));
        await AssertRejectedAsync(c, path, new HeifXmpContainerAdapter(), "HEIF XMP 与其它 item 共享 extent", fileRef);
    }

    private static async Task CheckHeifDuplicateIlocAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-duplicate-iloc.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3), overlap: false, duplicateIloc: true, duplicateMeta: false, multipleCandidates: false));
        await AssertRejectedAsync(c, path, new HeifXmpContainerAdapter(), "HEIF 重复 iloc", fileRef);
    }

    private static async Task CheckHeifDuplicateMetaAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-duplicate-meta.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3), overlap: false, duplicateIloc: false, duplicateMeta: true, multipleCandidates: false));
        await AssertRejectedAsync(c, path, new HeifXmpContainerAdapter(), "HEIF 重复 meta", fileRef);
    }

    private static async Task CheckHeifMultipleCandidatesAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-multi-candidates.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3), overlap: false, duplicateIloc: false, duplicateMeta: false, multipleCandidates: true));
        await AssertRejectedAsync(c, path, new HeifXmpContainerAdapter(), "HEIF RDF+XML 多候选不取第一个", fileRef);
    }

    private static async Task CheckHeifReferenceItemAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-reference-item.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3),
            overlap: false,
            duplicateIloc: false,
            duplicateMeta: false,
            multipleCandidates: false,
            referenceItem: true));
        await AssertRejectedAsync(
            c, path, new HeifXmpContainerAdapter(),
            "HEIF 项间引用 item 无法证明独占", fileRef);
    }

    /// <summary>idat 承载的 extent 使用绝对基址 + base_offset，且不覆盖 XMP。</summary>
    private static async Task CheckHeifIdatItemAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-idat-item.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3),
            overlap: false,
            duplicateIloc: false,
            duplicateMeta: false,
            multipleCandidates: false,
            idatItem: (8, 0, 56),
            idatPayloadLength: 64));
        await AssertUsableAsync(c, path, "HEIF idat extent（base_offset 生效）", fileRef);
    }

    /// <summary>base_offset + extent_offset 越过 idat payload 必须拒绝，而不是按 0 基址放行。</summary>
    private static async Task CheckHeifIdatBaseOverflowAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-idat-base-overflow.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3),
            overlap: false,
            duplicateIloc: false,
            duplicateMeta: false,
            multipleCandidates: false,
            idatItem: (60, 8, 4),
            idatPayloadLength: 64));
        await AssertRejectedAsync(
            c, path, new HeifXmpContainerAdapter(),
            "HEIF idat base_offset 越界", fileRef);
    }

    /// <summary>
    /// mdat 位于 meta 之前：idat 的 box 偏移必须按 meta payload 的绝对基址换算。
    /// 若把 idat 偏移当成相对 meta 的坐标，会误与靠前的 XMP 范围相交而拒绝。
    /// </summary>
    private static async Task CheckHeifIdatAbsoluteAsync(Checks c, string root, Func<string, IFileRef> fileRef)
    {
        var path = Path.Combine(root, "heif-idat-absolute.heic");
        await File.WriteAllBytesAsync(path, BuildHeif(
            HeifXmpPacket(3),
            overlap: false,
            duplicateIloc: false,
            duplicateMeta: false,
            multipleCandidates: false,
            idatItem: (0, 0, 64),
            idatPayloadLength: 64,
            mdatBeforeMeta: true,
            xmpOffsetInPayload: 24,
            payloadPadding: 800));
        await AssertUsableAsync(c, path, "HEIF idat 绝对基址", fileRef);
    }

    private static async Task AssertUsableAsync(
        Checks c,
        string path,
        string label,
        Func<string, IFileRef> fileRef)
    {
        var original = await File.ReadAllBytesAsync(path);
        var file = fileRef(path);
        var adapter = new HeifXmpContainerAdapter();
        var packet = await adapter.ReadCanonicalPacketAsync(file);
        c.True(label + "：能定位 canonical XMP", packet is { Length: > 0 },
            packet is null ? "未定位到 XMP 项" : "包为空");
        if (packet is null) return;
        c.Equal(label + "：包语义正确", 3,
            XmpRatingReader.ReadPacket(packet.Data, XmpRatingSource.Embedded).Value);

        var store = XmpRatingStoreFactory.Create(file);
        var receipt = await store.WriteAsync(4, safeMode: true);
        c.Equal(label + "：经 store 写入", 4, receipt.After.Value);
        await store.RestoreAsync(receipt);
        c.Bytes(label + "：补偿后字节还原", original, await File.ReadAllBytesAsync(path));
    }

    private static async Task AssertRejectedAsync(
        Checks c,
        string path,
        IXmpContainerAdapter adapter,
        string label,
        Func<string, IFileRef> fileRef)
    {
        var original = await File.ReadAllBytesAsync(path);
        var file = fileRef(path);
        await c.StorageFailsAsync(
            label + "：读取明确 Unsupported",
            StorageError.Unsupported,
            () => adapter.ReadCanonicalPacketAsync(file));

        var store = XmpRatingStoreFactory.Create(file);
        await c.StorageFailsAsync(
            label + "：评分写入拒绝",
            StorageError.Unsupported,
            () => store.WriteAsync(4, safeMode: true));
        c.Bytes(label + "：拒绝后零写", original, await File.ReadAllBytesAsync(path));
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static byte[] TiffXmpPacket(int rating) =>
        Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\""
            + rating + "\"/></rdf:RDF></x:xmpmeta>" + new string(' ', 256));

    private static byte[] HeifXmpPacket(int rating) =>
        Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\""
            + rating + "\"/></rdf:RDF></x:xmpmeta>" + new string(' ', 512));

    /// <summary>
    /// 合成经典 TIFF：IFD0 固定 10 个条目（表 8..134），XMP 包紧随表后从 134 开始。
    /// 旧实现已复现的样本就是这一布局，只是 strip 范围也指向 134。
    /// </summary>
    private static byte[] BuildTiff(
        byte[] packet,
        int stripOffset,
        int stripLength,
        bool duplicateXmp,
        bool cycle,
        bool sharedTagData)
    {
        var entryCount = duplicateXmp ? 11 : 10;
        var file = new byte[134 + packet.Length + 16];
        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);

        var table = new byte[2 + entryCount * 12 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(table, (ushort)entryCount);
        var position = 2;
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(position), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(position + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(position + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(position + 8), value);
            position += 12;
        }

        Entry(256, 3, 1, (uint)packet.Length);
        Entry(257, 3, 1, 1);
        Entry(258, 3, 1, 8);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 1);
        Entry(273, 4, 1, (uint)stripOffset);
        if (sharedTagData)
        {
            // ImageDescription（ASCII）外置数据与 XMP 包指向同一范围。
            Entry(270, 2, (uint)packet.Length, 134);
        }
        else
        {
            Entry(277, 3, 1, 1);
        }

        Entry(278, 4, 1, 1);
        Entry(279, 4, 1, (uint)stripLength);
        Entry(700, 1, (uint)packet.Length, 134);
        if (duplicateXmp)
            Entry(700, 1, (uint)packet.Length, 134);

        BinaryPrimitives.WriteUInt32LittleEndian(
            table.AsSpan(2 + entryCount * 12),
            cycle ? 8u : 0u);
        table.CopyTo(file.AsSpan(8));

        packet.CopyTo(file.AsSpan(134));
        if (134 + packet.Length < file.Length)
        {
            for (var index = 134 + packet.Length; index < file.Length; index++)
                file[index] = 0x5A;
        }

        return file;
    }

    private static byte[] BuildHeif(
        byte[] packet,
        bool overlap,
        bool duplicateIloc,
        bool duplicateMeta,
        bool multipleCandidates,
        bool referenceItem = false,
        (long BaseOffset, long Offset, long Length)? idatItem = null,
        int idatPayloadLength = 0,
        bool mdatBeforeMeta = false,
        int xmpOffsetInPayload = 16,
        int payloadPadding = 0)
    {
        const int photoLength = 16;
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), Be32(0), Encoding.ASCII.GetBytes("heic"));

        var mimeItems = new List<MimeItem> { new(8, "application/rdf+xml") };
        if (multipleCandidates) mimeItems.Add(new(9, "application/xml"));

        var placeholder = new List<IlocSpec>
        {
            new(1, 0, [(0, photoLength)]),
        };
        for (var index = 0; index < mimeItems.Count; index++)
            placeholder.Add(new IlocSpec(mimeItems[index].ItemId, 0, [(0, packet.Length)]));
        if (referenceItem) placeholder.Add(new IlocSpec(10, 2, [(0, 0)]));
        if (idatItem is { } idat) placeholder.Add(new IlocSpec(11, 1, [(idat.Offset, idat.Length)], idat.BaseOffset));

        var metaProbe = BuildMeta(
            mimeItems, placeholder, duplicateIloc, referenceItem, idatItem, idatPayloadLength);
        var payloadLength = Math.Max(photoLength + packet.Length, xmpOffsetInPayload + packet.Length) +
            payloadPadding;
        var payloadStart = ftyp.Length + (mdatBeforeMeta ? 8 : metaProbe.Length + 8);

        var photoOffset = payloadStart;
        var xmpOffset = payloadStart + xmpOffsetInPayload;
        var extents = new List<IlocSpec>();
        if (overlap)
        {
            // 主图 item 的范围覆盖 XMP 包：共享 extent。
            extents.Add(new IlocSpec(1, 0, [(xmpOffset, photoLength + packet.Length)]));
        }
        else
        {
            extents.Add(new IlocSpec(1, 0, [(photoOffset, photoLength)]));
        }

        for (var index = 0; index < mimeItems.Count; index++)
            extents.Add(new IlocSpec(mimeItems[index].ItemId, 0, [(xmpOffset, packet.Length)]));
        if (referenceItem) extents.Add(new IlocSpec(10, 2, [(0, 0)]));
        if (idatItem is { } idatFinal)
            extents.Add(new IlocSpec(11, 1, [(idatFinal.Offset, idatFinal.Length)], idatFinal.BaseOffset));

        var meta = BuildMeta(mimeItems, extents, duplicateIloc, referenceItem, idatItem, idatPayloadLength);
        var metaBoxes = duplicateMeta ? Concat(meta, meta) : meta;
        var payload = new byte[payloadLength];
        for (var index = 0; index < photoLength; index++) payload[index] = 0x33;
        packet.CopyTo(payload.AsSpan(xmpOffsetInPayload));

        var mdat = Box("mdat", payload);
        return mdatBeforeMeta ? Concat(ftyp, mdat, metaBoxes) : Concat(ftyp, metaBoxes, mdat);
    }

    private static byte[] BuildMeta(
        IReadOnlyList<MimeItem> mimeItems,
        IReadOnlyList<IlocSpec> items,
        bool duplicateIloc,
        bool referenceItem,
        (long BaseOffset, long Offset, long Length)? idatItem,
        int idatPayloadLength)
    {
        var infes = new List<byte[]>
        {
            Infe(1, "hvc1", null),
        };
        foreach (var item in mimeItems)
            infes.Add(Infe(item.ItemId, "mime", item.ContentType));
        if (referenceItem) infes.Add(Infe(10, "hvc1", null));
        if (idatItem is not null) infes.Add(Infe(11, "hvc1", null));

        var iinf = Box(
            "iinf",
            Be32(0),
            Be16(infes.Count),
            Concat([.. infes]));
        var pitm = Box("pitm", Be32(0), Be16(1));

        var ilocEntries = new List<byte[]>();
        foreach (var item in items)
        {
            var entry = new List<byte[]>
            {
                Be16(item.ItemId),
                Be16(item.ConstructionMethod),
                Be16(0),
                Be32((uint)item.BaseOffset),
                Be16(item.Extents.Length),
            };
            foreach (var (offset, length) in item.Extents)
            {
                entry.Add(Be32((uint)offset));
                entry.Add(Be32((uint)length));
            }

            ilocEntries.Add(Concat([.. entry]));
        }

        var ilocPayload = new List<byte[]>
        {
            Be32(1 << 24), // version 1
            Be16((4 << 12) | (4 << 8) | (4 << 4)), // offset/length/base_offset 均为 4 字节
            Be16(items.Count),
        };
        ilocPayload.AddRange(ilocEntries);

        var iloc = Box("iloc", Concat([.. ilocPayload]));
        var children = new List<byte[]> { duplicateIloc ? Concat(iloc, iloc) : iloc };
        if (idatPayloadLength > 0) children.Add(Box("idat", new byte[idatPayloadLength]));
        return Box("meta", Be32(0), pitm, iinf, Concat([.. children]));
    }

    private static byte[] Infe(int itemId, string type, string? contentType)
    {
        var parts = new List<byte[]>
        {
            Be32(2 << 24), // version 2
            Be16(itemId),
            Be16(0),
            Encoding.ASCII.GetBytes(type),
            new byte[] { 0 }, // 空 item_name
        };
        if (contentType != null)
        {
            parts.Add(Encoding.ASCII.GetBytes(contentType));
            parts.Add(new byte[] { 0 });
        }

        return Box("infe", Concat([.. parts]));
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var length = 8 + parts.Sum(part => part.Length);
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)length);
        Encoding.ASCII.GetBytes(type).CopyTo(data, 4);
        var offset = 8;
        foreach (var part in parts)
        {
            part.CopyTo(data, offset);
            offset += part.Length;
        }

        return data;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var data = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(data, offset);
            offset += part.Length;
        }

        return data;
    }

    private static byte[] Be16(int value)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)value);
        return data;
    }

    private static byte[] Be32(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        return data;
    }

    private sealed record MimeItem(int ItemId, string ContentType);

    private sealed record IlocSpec(
        int ItemId,
        int ConstructionMethod,
        (long Offset, long Length)[] Extents,
        long BaseOffset = 0);
}
