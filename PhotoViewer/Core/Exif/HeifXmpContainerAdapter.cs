using System.Buffers.Binary;
using System.Text;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// HEIF/HEIC/HIF/AVIF/CR3 等 ISOBMFF 容器的 XMP 读取适配器。
/// 只认 <c>meta</c> 下 <c>iinf</c> 中类型为 <c>mime</c>、内容类型为 XML/RDF 的项，
/// 通过 <c>iloc</c> 的全部 extent 定位到 <c>mdat</c> 内的精确文件范围。
/// </summary>
/// <remarks>
/// <para>多个 meta 盒、多个 <c>pitm</c>/<c>iinf</c>/<c>iloc</c>/<c>idat</c>、多个候选 XMP 项
/// （RDF 与 XML 同时出现也算多候选）、多 extent 或 <c>idat</c> 承载的 XMP 项一律
/// <see cref="StorageError.Unsupported"/>，不按顺序取第一个。</para>
/// <para>其它 item 的全部 extent 都会被解析成文件绝对范围：<c>idat</c> 的 box 偏移按 meta payload
/// 绝对基址换算，<c>iloc</c> 的 base_offset 对 idat 承载的 extent 同样生效，所有范围相加都做溢出检查。
/// XMP 范围与任一其它 item 的范围相交（共享 extent 或重叠）时明确拒绝；项间引用
/// （construction method 2）的 item 无法逐 extent 证明独占，同样拒绝。
/// 保证等长原位写入不会改到其它 item 的字节。</para>
/// </remarks>
public sealed class HeifXmpContainerAdapter : IXmpContainerAdapter
{
    private const uint BoxMeta = 0x6D657461;
    private const uint BoxPitm = 0x7069746D;
    private const uint BoxIinf = 0x69696E66;
    private const uint BoxInfe = 0x696E6665;
    private const uint BoxIloc = 0x696C6F63;
    private const uint BoxIdat = 0x69646174;
    private const uint BoxMdat = 0x6D646174;
    private const uint ItemTypeMime = 0x6D696D65;

    private const int MaxTopLevelBoxes = 4096;
    private const int MaxMetaBytes = 64 << 20;
    private const int MaxXmpPacketBytes = 64 << 20;
    private const int MaxExtentCount = 4096;
    private const int MaxTotalExtents = 1 << 16;
    private const int MaxIlocItems = 65536;

    private const string RdfXmlContentType = "application/rdf+xml";

    /// <inheritdoc/>
    public async Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var document = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false);
        return document?.Packet;
    }

    private static async Task<HeifDocument?> ReadDocumentAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        await using var session = await file.OpenRandomAccessAsync(
            StorageAccessMode.Read,
            cancellationToken).ConfigureAwait(false);

        var fileLength = session.Length;
        if (fileLength < 16) return null;

        var topLevel = await ReadTopLevelBoxesAsync(session, fileLength, cancellationToken)
            .ConfigureAwait(false);
        BoxInfo? metaBox = null;
        foreach (var box in topLevel)
        {
            if (box.Type != BoxMeta) continue;
            if (metaBox is not null)
                throw Unsupported("文件包含多个 meta 盒，无法确定 canonical XMP");
            metaBox = box;
        }

        if (metaBox is not { } meta) return null;

        if (meta.PayloadLength < 4)
            throw Unsupported("meta 盒长度不足");
        if (meta.PayloadLength > MaxMetaBytes)
            throw Unsupported("meta 盒超过解析上限");
        if (meta.PayloadLength > int.MaxValue)
            throw Unsupported("meta 盒过大");

        var metaPayload = await XmpContainerIO.ReadExactlyAsync(
            session,
            meta.PayloadOffset,
            (int)meta.PayloadLength,
            cancellationToken).ConfigureAwait(false);
        if (metaPayload[0] != 0)
            throw Unsupported(
                "meta 版本 " + metaPayload[0].ToString(System.Globalization.CultureInfo.InvariantCulture));

        var children = ReadSpanBoxes(metaPayload, 4, metaPayload.Length);
        SpanBox? pitm = null;
        SpanBox? iinf = null;
        SpanBox? iloc = null;
        SpanBox? idat = null;
        foreach (var child in children)
        {
            switch (child.Type)
            {
                case BoxPitm:
                    if (pitm is not null) throw Unsupported("meta 包含重复的 pitm 盒");
                    pitm = child;
                    break;
                case BoxIinf:
                    if (iinf is not null) throw Unsupported("meta 包含重复的 iinf 盒");
                    iinf = child;
                    break;
                case BoxIloc:
                    if (iloc is not null) throw Unsupported("meta 包含重复的 iloc 盒");
                    iloc = child;
                    break;
                case BoxIdat:
                    if (idat is not null) throw Unsupported("meta 包含重复的 idat 盒");
                    idat = child;
                    break;
            }
        }

        if (pitm is null)
            throw Unsupported("meta 缺少 pitm 主图声明");
        if (iinf is null)
            throw Unsupported("meta 缺少 iinf 项信息");
        if (iloc is null)
            throw Unsupported("meta 缺少 iloc 项位置");

        var primaryItemId = ParsePitm(metaPayload, pitm.Value);
        var items = ParseIinf(metaPayload, iinf.Value);
        var locations = ParseIloc(metaPayload, iloc.Value, fileLength);

        var item = SelectXmpItem(items);
        if (item is null) return null;
        if (item.ItemId == primaryItemId)
            throw Unsupported("XMP mime 项不能是 pitm 主图项");

        if (!locations.TryGetValue(item.ItemId, out var location))
            throw Unsupported("XMP mime 项缺少 iloc 位置");
        if (location.ConstructionMethod != 0)
            throw Unsupported("XMP 项使用 idat 或项间引用 construction method");
        if (location.Extents.Count != 1)
            throw Unsupported("XMP 项包含多个 extent");
        var extent = location.Extents[0];
        if (extent.Length <= 0)
            throw Unsupported("XMP 项 extent 长度未声明或为零");
        if (extent.Length > MaxXmpPacketBytes)
            throw Unsupported("XMP 包超过解析上限");

        var offset = AddOffset(location.BaseOffset, extent.Offset, "XMP 项 base offset 与 extent 偏移");
        if (offset > fileLength || extent.Length > fileLength - offset)
            throw Unsupported("XMP 项数据范围超出文件");
        if (!IsInsideMdat(topLevel, offset, extent.Length))
            throw Unsupported("XMP 项数据范围不在 mdat 内");

        var xmpEnd = AddLength(offset, extent.Length, "XMP 项范围");
        foreach (var other in locations.Values)
        {
            if (other.ItemId == location.ItemId) continue;
            if (other.ConstructionMethod == 2)
            {
                // 项间引用（method 2）的数据由被引用项提供，无法逐 extent 证明与 XMP 不共享字节。
                throw Unsupported(
                    $"item {other.ItemId} 使用项间引用 construction method，无法证明与 XMP 范围独占");
            }

            foreach (var otherExtent in other.Extents)
            {
                var (otherStart, otherEnd) = ResolveExtentRange(
                    other, otherExtent, idat, meta.PayloadOffset, fileLength);
                if (otherStart < xmpEnd && otherEnd > offset)
                {
                    throw Unsupported(
                        $"XMP 项范围 {offset}+{extent.Length} 与 item {other.ItemId} 的 " +
                        $"extent {otherStart}+{otherEnd - otherStart} 重叠或共享字节");
                }
            }
        }

        var data = await XmpContainerIO.ReadExactlyAsync(
            session,
            offset,
            (int)extent.Length,
            cancellationToken).ConfigureAwait(false);

        return new HeifDocument
        {
            Packet = new XmpContainerPacket(offset, data.Length, data),
        };
    }

    /// <summary>
    /// 解析其它 item 的 extent 绝对文件范围。长度为 0 表示“延伸到所在容器末尾”
    /// （ISO/IEC 14496-12），因此也参与重叠检查，不能当成空范围跳过。
    /// <c>idat</c> 的 box 偏移相对 meta payload，必须加上 meta payload 的绝对偏移；
    /// <c>iloc</c> 的 base_offset 对 idat 承载的 extent 同样有效。
    /// </summary>
    private static (long Start, long End) ResolveExtentRange(
        IlocItem item,
        IlocExtent extent,
        SpanBox? idat,
        long metaAbsoluteOffset,
        long fileLength)
    {
        long start;
        long limit;
        switch (item.ConstructionMethod)
        {
            case 0:
                start = AddOffset(item.BaseOffset, extent.Offset, $"item {item.ItemId} 的 extent 偏移");
                limit = fileLength;
                break;
            case 1:
                if (idat is not { } idatBox)
                    throw Unsupported($"item {item.ItemId} 使用 idat 但 meta 中没有 idat 盒");
                var idatStart = AddOffset(metaAbsoluteOffset, idatBox.PayloadOffset, "idat 绝对偏移");
                limit = AddLength(idatStart, idatBox.PayloadLength, "idat 绝对范围");
                if (limit > fileLength)
                    throw Unsupported("idat 盒范围超出文件");
                var relativeStart = AddOffset(
                    item.BaseOffset, extent.Offset, $"item {item.ItemId} 的 idat extent 偏移");
                if (relativeStart > idatBox.PayloadLength)
                    throw Unsupported($"item {item.ItemId} 的 idat extent 越界");
                start = AddOffset(idatStart, relativeStart, "idat extent 绝对偏移");
                break;
            default:
                throw Unsupported($"item {item.ItemId} 的 construction method {item.ConstructionMethod} 不受支持");
        }

        if (start < 0 || start > limit)
            throw Unsupported($"item {item.ItemId} 的 extent 范围超出所在容器");
        if (extent.Length == 0) return (start, limit);

        var end = AddLength(start, extent.Length, $"item {item.ItemId} 的 extent 范围");
        if (end > limit)
            throw Unsupported($"item {item.ItemId} 的 extent 范围超出所在容器");
        return (start, end);
    }

    private static long AddOffset(long left, long right, string what)
    {
        if (left < 0 || right < 0 || left > long.MaxValue - right)
            throw Unsupported(what + " 相加溢出或为负");
        return left + right;
    }

    private static long AddLength(long start, long length, string what)
    {
        if (start < 0 || length < 0 || start > long.MaxValue - length)
            throw Unsupported(what + " 相加溢出或为负");
        return start + length;
    }

    private static bool IsInsideMdat(IReadOnlyList<BoxInfo> topLevel, long offset, long length)
    {
        if (offset < 0 || length < 0) return false;
        foreach (var box in topLevel)
        {
            if (box.Type != BoxMdat || offset < box.PayloadOffset) continue;
            var relative = offset - box.PayloadOffset;
            if (relative <= box.PayloadLength && length <= box.PayloadLength - relative)
                return true;
        }
        return false;
    }

    private static async Task<List<BoxInfo>> ReadTopLevelBoxesAsync(
        IStorageRandomAccessSession session,
        long length,
        CancellationToken cancellationToken)
    {
        var boxes = new List<BoxInfo>(8);
        long offset = 0;
        while (offset < length)
        {
            if (length - offset < 8)
                throw Unsupported("文件末尾存在不完整的顶层盒");
            var headerLength = (int)Math.Min(16, length - offset);
            var header = await XmpContainerIO.ReadExactlyAsync(
                session,
                offset,
                headerLength,
                cancellationToken).ConfigureAwait(false);

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            var boxHeaderLength = 8L;
            var sizeToEnd = false;
            if (size == 1)
            {
                if (header.Length < 16)
                    throw Unsupported("顶层盒 largesize 被截断");
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                boxHeaderLength = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(length - offset);
                sizeToEnd = true;
            }

            if (size < (ulong)boxHeaderLength ||
                size > (ulong)(length - offset))
            {
                throw Unsupported("顶层盒尺寸越界");
            }

            boxes.Add(new BoxInfo(type, offset, (long)size, boxHeaderLength, sizeToEnd));
            if (boxes.Count > MaxTopLevelBoxes)
                throw Unsupported("顶层盒数量超过解析上限");
            offset += (long)size;
        }

        if (offset != length)
            throw Unsupported("顶层盒未覆盖文件末尾");
        return boxes;
    }

    private static List<SpanBox> ReadSpanBoxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<SpanBox>(8);
        var offset = start;
        while (offset < end)
        {
            if (end - offset < 8)
                throw Unsupported("盒末尾存在不完整子盒");

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (end - offset < 16)
                    throw Unsupported("子盒 largesize 被截断");
                size = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(end - offset);
            }

            if (size < (ulong)headerLength || size > (ulong)(end - offset))
                throw Unsupported("子盒尺寸越界");

            boxes.Add(new SpanBox(type, offset, (int)size, headerLength));
            if (boxes.Count > MaxTopLevelBoxes)
                throw Unsupported("子盒数量超过解析上限");
            offset += (int)size;
        }

        return boxes;
    }

    private static int ParsePitm(ReadOnlySpan<byte> meta, SpanBox box)
    {
        if (box.PayloadLength < 6)
            throw Unsupported("pitm 长度不足");
        var version = meta[box.PayloadOffset];
        var position = box.PayloadOffset + 4;
        if (version != 0 && box.PayloadLength < 8)
            throw Unsupported("pitm 项编号被截断");
        var value = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(position, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(meta.Slice(position, 4));
        if (value > int.MaxValue)
            throw Unsupported("pitm 项编号越界");
        return (int)value;
    }

    private static List<ItemInfo> ParseIinf(ReadOnlySpan<byte> meta, SpanBox box)
    {
        if (box.PayloadLength < 6)
            throw Unsupported("iinf 长度不足");
        var version = meta[box.PayloadOffset];
        if (version > 1)
            throw Unsupported("iinf 版本 " + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var countSize = version == 0 ? 2 : 4;
        if (box.PayloadLength < 4 + countSize)
            throw Unsupported("iinf 项数被截断");
        var entryCount = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(box.PayloadOffset + 4, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(meta.Slice(box.PayloadOffset + 4, 4));

        var start = box.PayloadOffset + 4 + countSize;
        var entries = ReadSpanBoxes(meta, start, box.End);
        if (entries.Count != entryCount)
            throw Unsupported("iinf 声明项数与实际条目不符");

        var items = new List<ItemInfo>(4);
        var seenIds = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.Type != BoxInfe) continue;

            var position = entry.PayloadOffset;
            var end = entry.End;
            if (entry.PayloadLength < 8)
                throw Unsupported("infe 长度不足");
            var infeVersion = meta[position];
            if (infeVersion > 3)
                throw Unsupported("infe 版本 " + infeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            position += 4;

            var itemIdSize = infeVersion >= 3 ? 4 : 2;
            var itemId = ReadBigEndian(meta, ref position, itemIdSize, end);
            if (itemId > int.MaxValue)
                throw Unsupported("infe 项编号越界");
            ReadBigEndian(meta, ref position, 2, end);

            var itemType = infeVersion >= 2
                ? (uint)ReadBigEndian(meta, ref position, 4, end)
                : 0;
            ReadCString(meta, ref position, end, "infe 项名");

            string? contentType = null;
            string? contentEncoding = null;
            if (infeVersion >= 2)
            {
                if (itemType == ItemTypeMime)
                {
                    contentType = ReadCString(meta, ref position, end, "infe 内容类型");
                    contentEncoding = TryReadCString(meta, ref position, end);
                }
            }
            else if (position < end)
            {
                contentType = ReadCString(meta, ref position, end, "infe 内容类型");
                contentEncoding = TryReadCString(meta, ref position, end);
                if (!string.IsNullOrEmpty(contentType)) itemType = ItemTypeMime;
            }

            if (!seenIds.Add((int)itemId))
                throw Unsupported("iinf 项编号重复");

            items.Add(new ItemInfo
            {
                ItemId = (int)itemId,
                ItemType = itemType,
                ContentType = contentType,
                ContentEncoding = contentEncoding,
            });
        }

        return items;
    }

    /// <summary>
    /// 选出唯一的 canonical XMP 项。RDF 与 XML 同时出现、或多份同类项都属于多候选，
    /// 一律拒绝而不是按列表顺序取第一个。
    /// </summary>
    private static ItemInfo? SelectXmpItem(List<ItemInfo> items)
    {
        var candidates = new List<ItemInfo>(1);
        foreach (var item in items)
        {
            if (item.ItemType != ItemTypeMime) continue;
            if (string.IsNullOrWhiteSpace(item.ContentType)) continue;
            if (!IsUtf8Encoding(item.ContentEncoding)) continue;
            var mediaType = NormalizeMediaType(item.ContentType);
            if (mediaType == RdfXmlContentType || mediaType is "application/xml" or "text/xml")
                candidates.Add(item);
        }

        if (candidates.Count > 1)
        {
            throw Unsupported(
                "存在 " + candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " 个 XML/RDF mime 项（" + string.Join(",", candidates.Select(item => item.ItemId)) +
                "），无法确定 canonical XMP");
        }

        return candidates.Count == 0 ? null : candidates[0];
    }

    private static bool IsUtf8Encoding(string? encoding) =>
        string.IsNullOrWhiteSpace(encoding) ||
        encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("utf8", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        var mediaType = separator >= 0 ? contentType[..separator] : contentType;
        return mediaType.Trim().ToLowerInvariant();
    }

    private static Dictionary<int, IlocItem> ParseIloc(
        ReadOnlySpan<byte> meta,
        SpanBox box,
        long fileLength)
    {
        if (box.PayloadLength < 8)
            throw Unsupported("iloc 长度不足");
        var version = meta[box.PayloadOffset];
        if (version > 2)
            throw Unsupported("iloc 版本 " + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var sizes = BinaryPrimitives.ReadUInt16BigEndian(meta.Slice(box.PayloadOffset + 4, 2));
        var offsetSize = (sizes >> 12) & 0xF;
        var lengthSize = (sizes >> 8) & 0xF;
        var baseOffsetSize = (sizes >> 4) & 0xF;
        var indexSize = version is 1 or 2 ? sizes & 0xF : 0;
        if (offsetSize > 8 || lengthSize > 8 || baseOffsetSize > 8 || indexSize > 8)
            throw Unsupported("iloc 字段宽度超过 8 字节");

        var position = box.PayloadOffset + 6;
        var end = box.End;
        var itemCountValue = ReadBigEndian(meta, ref position, version < 2 ? 2 : 4, end);
        if (itemCountValue > MaxIlocItems)
            throw Unsupported("iloc 项数超过解析上限");
        var itemCount = (int)itemCountValue;

        var items = new Dictionary<int, IlocItem>(itemCount);
        var itemIdSize = version < 2 ? 2 : 4;
        var totalExtents = 0;

        for (var i = 0; i < itemCount; i++)
        {
            var itemId = ReadBigEndian(meta, ref position, itemIdSize, end);
            if (itemId > int.MaxValue)
                throw Unsupported("iloc 项编号越界");

            var constructionMethod = 0;
            if (version is 1 or 2)
                constructionMethod = (int)(ReadBigEndian(meta, ref position, 2, end) & 0xF);
            var dataReferenceIndex = ReadBigEndian(meta, ref position, 2, end);
            if (dataReferenceIndex != 0)
                throw Unsupported("iloc 使用外部数据引用");

            var baseOffsetValue = ReadBigEndian(meta, ref position, baseOffsetSize, end);
            if (baseOffsetValue > long.MaxValue)
                throw Unsupported("iloc base offset 越界");
            var baseOffset = (long)baseOffsetValue;

            var extentCount = (int)ReadBigEndian(meta, ref position, 2, end);
            if (extentCount > MaxExtentCount)
                throw Unsupported("iloc extent 数量超过解析上限");
            totalExtents += extentCount;
            if (totalExtents > MaxTotalExtents)
                throw Unsupported("iloc extent 总数超过解析上限");

            if (baseOffset > fileLength)
                throw Unsupported("iloc 项数据范围超出文件");

            var extents = new List<IlocExtent>(extentCount);
            for (var j = 0; j < extentCount; j++)
            {
                if (indexSize > 0)
                    ReadBigEndian(meta, ref position, indexSize, end);

                var extentOffset = ReadBigEndian(meta, ref position, offsetSize, end);
                var extentLength = ReadBigEndian(meta, ref position, lengthSize, end);
                if (extentOffset > long.MaxValue || extentLength > long.MaxValue)
                    throw Unsupported("iloc extent 字段越界");
                if ((long)extentOffset > fileLength - baseOffset)
                    throw Unsupported("iloc extent 偏移超出文件");
                extents.Add(new IlocExtent((long)extentOffset, (long)extentLength));
            }

            var item = new IlocItem
            {
                ItemId = (int)itemId,
                ConstructionMethod = constructionMethod,
                BaseOffset = baseOffset,
                Extents = extents,
            };

            if (!items.TryAdd(item.ItemId, item))
                throw Unsupported("iloc 项编号重复");
        }

        return items;
    }

    private static ulong ReadBigEndian(
        ReadOnlySpan<byte> data,
        ref int position,
        int size,
        int end)
    {
        if (size < 0 || size > 8 || position < 0 || end - position < size)
            throw Unsupported("盒字段被截断");
        ulong value = 0;
        for (var i = 0; i < size; i++)
            value = (value << 8) | data[position + i];
        position += size;
        return value;
    }

    private static string ReadCString(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        string what)
    {
        var start = position;
        while (position < end && data[position] != 0) position++;
        if (position >= end)
            throw Unsupported(what + "缺少终止符");
        var value = Encoding.UTF8.GetString(data.Slice(start, position - start));
        position++;
        return value;
    }

    private static string? TryReadCString(ReadOnlySpan<byte> data, ref int position, int end)
    {
        if (position >= end) return null;
        var start = position;
        var limit = Math.Min(end, start + 64);
        while (position < limit && data[position] != 0)
        {
            if (data[position] is < 0x20 or > 0x7E)
            {
                position = start;
                return null;
            }
            position++;
        }

        if (position >= limit || data[position] != 0)
        {
            position = start;
            return null;
        }

        var value = Encoding.ASCII.GetString(data.Slice(start, position - start));
        position++;
        return value;
    }

    private static StorageException Unsupported(string reason) =>
        new(StorageError.Unsupported, "HEIF 容器不支持：" + reason);

    private sealed class HeifDocument
    {
        public required XmpContainerPacket Packet { get; init; }
    }

    private sealed class ItemInfo
    {
        public required int ItemId { get; init; }

        public required uint ItemType { get; init; }

        public string? ContentType { get; init; }

        public string? ContentEncoding { get; init; }
    }

    private sealed class IlocItem
    {
        public required int ItemId { get; init; }

        public required int ConstructionMethod { get; init; }

        public required long BaseOffset { get; init; }

        public required IReadOnlyList<IlocExtent> Extents { get; init; }
    }

    private readonly record struct IlocExtent(long Offset, long Length);

    private readonly record struct BoxInfo(
        uint Type,
        long Offset,
        long Size,
        long HeaderLength,
        bool SizeToEnd)
    {
        public long End => Offset + Size;

        public long PayloadOffset => Offset + HeaderLength;

        public long PayloadLength => Size - HeaderLength;
    }

    private readonly record struct SpanBox(
        uint Type,
        int Offset,
        int Size,
        int HeaderLength)
    {
        public int PayloadOffset => Offset + HeaderLength;

        public int PayloadLength => Size - HeaderLength;

        public int End => Offset + Size;
    }
}
