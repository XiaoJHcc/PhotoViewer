using System.Buffers.Binary;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// TIFF 家族容器（TIFF / ARW / DNG 等经典 TIFF 结构）的 XMP 容器适配器。
/// 只按 TIFF 结构定位 IFD0 的 tag 700（XMP），不做任何文本扫描。
/// </summary>
/// <remarks>
/// <para>为保证 XMP 包及其后续等长原位写入不碰到别的数据，读取时严格有界遍历
/// IFD0、next IFD 链、SubIFDs、EXIF、GPS 与 Interop 目录，并收集所有“受保护范围”：
/// 目录表本身、其它 tag 的外置数据、strip/tile 数据以及 JPEG 缩略图数据。
/// tag 700 的数据范围与任一受保护范围相交时明确 <see cref="StorageError.Unsupported"/>，
/// 绝不写入共享字节。</para>
/// <para>遍历有硬上限（目录数、单目录条目数、总条目数、数组值数）并记录已访问目录偏移；
/// 循环引用、重复目录、越界目录表、未知字段类型一律 <see cref="StorageError.Unsupported"/>。</para>
/// <para>没有 tag 700 时返回 null：新增条目需要重建 IFD0，会破坏部分读取器的 IFD 链顺序，
/// 本适配器不提供该写入路径。</para>
/// </remarks>
public sealed class TiffXmpContainerAdapter : IXmpContainerAdapter
{
    private const ushort XmpTag = 0x02BC;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagInteropIfd = 0xA005;
    private const ushort TagStripOffsets = 0x0111;
    private const ushort TagStripByteCounts = 0x0117;
    private const ushort TagTileOffsets = 0x0144;
    private const ushort TagTileByteCounts = 0x0145;
    private const ushort TagJpegInterchangeFormat = 0x0201;
    private const ushort TagJpegInterchangeFormatLength = 0x0202;

    private const ushort TiffMagic = 42;
    private const ushort BigTiffMagic = 43;
    private const ushort TypeByte = 1;
    private const ushort TypeUndefined = 7;
    private const int HeaderSize = 8;
    private const int EntrySize = 12;
    private const int InlineValueCapacity = 4;
    private const int MaxIfdCount = 256;
    private const int MaxDirectoryEntries = 4096;
    private const int MaxTotalEntries = 65536;
    private const int MaxArrayValues = 1 << 20;
    private const int MaxArrayBytes = 64 << 20;
    private const int MaxPacketLength = 16 * 1024 * 1024;

    /// <inheritdoc />
    public Task<XmpContainerPacket?> ReadCanonicalPacketAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return GuardAsync(
            $"读取 {file.Name} 的 TIFF XMP",
            () => ReadCanonicalPacketCoreAsync(file, cancellationToken));
    }

    private static async Task<XmpContainerPacket?> ReadCanonicalPacketCoreAsync(
        IFileRef file,
        CancellationToken cancellationToken)
    {
        await using var session = await file.OpenRandomAccessAsync(StorageAccessMode.Read, cancellationToken)
            .ConfigureAwait(false);
        var read = ForSession(session);
        var layout = await ReadLayoutAsync(read, session.Length, file.Name, cancellationToken)
            .ConfigureAwait(false);
        if (layout.XmpEntry is not { } entry) return null;
        var offset = ValueOffset(layout.BigEndian, entry);
        var data = await read(offset, (int)entry.Count, cancellationToken).ConfigureAwait(false);
        return new XmpContainerPacket(offset, (int)entry.Count, data);
    }

    private static async Task<T> GuardAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageException.From(exception, operation);
        }
    }

    private static async Task<TiffLayout> ReadLayoutAsync(
        RangeReader read,
        long fileLength,
        string name,
        CancellationToken cancellationToken)
    {
        if (fileLength < HeaderSize) throw Unsupported(name, "文件短于 TIFF 头部");

        var header = await read(0, HeaderSize, cancellationToken).ConfigureAwait(false);
        bool bigEndian;
        if (header[0] == (byte)'I' && header[1] == (byte)'I') bigEndian = false;
        else if (header[0] == (byte)'M' && header[1] == (byte)'M') bigEndian = true;
        else throw Unsupported(name, "不是 TIFF/ARW/DNG 容器（缺少 II/MM 字节序标记）");

        var magic = ReadUInt16(header.AsSpan(2, 2), bigEndian);
        if (magic == BigTiffMagic) throw Unsupported(name, "BigTIFF（magic 43）不受支持");
        if (magic != TiffMagic) throw Unsupported(name, $"TIFF magic 不是 42（实际 {magic}）");

        var firstIfd = ReadUInt32(header.AsSpan(4, 4), bigEndian);
        var traversal = new Traversal(read, fileLength, name, bigEndian);
        await traversal.RunAsync(firstIfd, cancellationToken).ConfigureAwait(false);
        return traversal.ToLayout();
    }

    /// <summary>按 TIFF 结构有界遍历全部目录，并收集受保护范围。</summary>
    private sealed class Traversal
    {
        private readonly RangeReader _read;
        private readonly long _fileLength;
        private readonly string _name;
        private readonly bool _bigEndian;
        private readonly HashSet<long> _visited = [];
        private readonly List<ProtectedRange> _ranges = [];
        private TiffEntry? _xmp;
        private int _totalEntries;

        public Traversal(RangeReader read, long fileLength, string name, bool bigEndian)
        {
            _read = read;
            _fileLength = fileLength;
            _name = name;
            _bigEndian = bigEndian;
        }

        public async Task RunAsync(long firstIfd, CancellationToken cancellationToken)
        {
            var queue = new Queue<(long Offset, bool Root)>();
            queue.Enqueue((firstIfd, true));
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (offset, root) = queue.Dequeue();
                if (offset == 0) continue;
                if (_visited.Count >= MaxIfdCount)
                    throw Unsupported(_name, $"IFD 数量超过解析上限 {MaxIfdCount}");
                if (!_visited.Add(offset))
                    throw Unsupported(_name, $"IFD 链存在循环或重复目录引用：{offset}");

                var next = await ReadDirectoryAsync(offset, root, queue, cancellationToken)
                    .ConfigureAwait(false);
                if (next != 0)
                {
                    ValidateDirectoryOffset(next, "next IFD");
                    queue.Enqueue((next, false));
                }
            }
        }

        public TiffLayout ToLayout()
        {
            if (_xmp is not { } entry)
                return new TiffLayout { BigEndian = _bigEndian, XmpEntry = null };

            if (entry.Type is not (TypeByte or TypeUndefined))
                throw Unsupported(_name, $"tag 700 的类型 {entry.Type} 不受支持（只接受 BYTE/UNDEFINED）");
            if (entry.Count == 0) throw Unsupported(_name, "tag 700 的 count 为 0");
            if (entry.Count > MaxPacketLength)
                throw Unsupported(_name, $"tag 700 的 count 异常：{entry.Count}");

            var layout = new TiffLayout { BigEndian = _bigEndian, XmpEntry = entry };

            // 内联值就在 IFD0 entry 内，不与其它数据共用。
            if (entry.Count <= InlineValueCapacity) return layout;

            var offset = ValueOffset(_bigEndian, entry);
            var end = offset + entry.Count;
            if (offset < HeaderSize || end > _fileLength)
                throw Unsupported(_name, $"tag 700 的数据偏移越界：{offset}+{entry.Count}");

            foreach (var range in _ranges)
            {
                if (range.Start < end && range.End > offset)
                {
                    throw Unsupported(
                        _name,
                        $"tag 700 的 XMP 数据范围 {offset}+{entry.Count} 与{range.Kind} " +
                        $"{range.Start}+{range.End - range.Start} 共用字节，拒绝在共享范围写入");
                }
            }

            return layout;
        }

        private async Task<long> ReadDirectoryAsync(
            long offset,
            bool root,
            Queue<(long, bool)> queue,
            CancellationToken cancellationToken)
        {
            ValidateDirectoryOffset(offset, root ? "IFD0" : "目录");
            var countBytes = await _read(offset, 2, cancellationToken).ConfigureAwait(false);
            var entryCount = ReadUInt16(countBytes, _bigEndian);
            if (entryCount > MaxDirectoryEntries)
                throw Unsupported(_name, $"目录条目数异常：{entryCount}");
            _totalEntries += entryCount;
            if (_totalEntries > MaxTotalEntries)
                throw Unsupported(_name, $"目录条目总数超过解析上限 {MaxTotalEntries}");

            var tableOffset = offset + 2L;
            var tableLength = entryCount * (long)EntrySize + 4;
            if (tableOffset + tableLength > _fileLength)
                throw Unsupported(_name, "IFD 条目表越过文件末尾");
            var table = await _read(tableOffset, (int)tableLength, cancellationToken).ConfigureAwait(false);
            _ranges.Add(new ProtectedRange(offset, tableOffset + tableLength, "目录表"));

            var chunkTags = new Dictionary<ushort, TiffEntry>();
            for (var index = 0; index < entryCount; index++)
            {
                var raw = table.AsSpan(index * EntrySize, EntrySize).ToArray();
                var entry = new TiffEntry(
                    ReadUInt16(raw, _bigEndian),
                    ReadUInt16(raw.AsSpan(2), _bigEndian),
                    ReadUInt32(raw.AsSpan(4), _bigEndian),
                    tableOffset + index * (long)EntrySize,
                    raw);
                var totalSize = TypeSize(entry.Tag, entry.Type) * (long)entry.Count;
                if (entry.Tag == XmpTag)
                {
                    if (!root)
                        throw Unsupported(
                            _name, $"非 IFD0 目录 {offset} 中出现 XMP tag 700，无法确定 canonical 包");
                    if (_xmp is not null) throw Unsupported(_name, "IFD0 含重复的 XMP tag 700");
                    _xmp = entry;
                    continue;
                }

                if (totalSize > InlineValueCapacity)
                    AddDataRange(entry, totalSize);

                switch (entry.Tag)
                {
                    case TagSubIfds:
                    case TagExifIfd:
                    case TagGpsIfd:
                    case TagInteropIfd:
                        foreach (var value in await ReadValuesAsync(entry, cancellationToken)
                                     .ConfigureAwait(false))
                        {
                            if (value is <= 0 or > int.MaxValue) continue;
                            ValidateDirectoryOffset(value, $"tag {entry.Tag} 指向的目录");
                            queue.Enqueue((value, false));
                        }

                        break;
                    case TagStripOffsets:
                    case TagTileOffsets:
                    case TagStripByteCounts:
                    case TagTileByteCounts:
                    case TagJpegInterchangeFormat:
                    case TagJpegInterchangeFormatLength:
                        chunkTags[entry.Tag] = entry;
                        break;
                }
            }

            await AddDataRangesAsync(chunkTags, TagStripOffsets, TagStripByteCounts, "strip 数据", cancellationToken)
                .ConfigureAwait(false);
            await AddDataRangesAsync(chunkTags, TagTileOffsets, TagTileByteCounts, "tile 数据", cancellationToken)
                .ConfigureAwait(false);
            if (chunkTags.TryGetValue(TagJpegInterchangeFormat, out var thumbnail) &&
                chunkTags.TryGetValue(TagJpegInterchangeFormatLength, out var thumbnailLength))
            {
                var thumbnailOffset = await ReadValuesAsync(thumbnail, cancellationToken).ConfigureAwait(false);
                var thumbnailBytes = await ReadValuesAsync(thumbnailLength, cancellationToken)
                    .ConfigureAwait(false);
                if (thumbnailOffset.Count > 0 && thumbnailBytes.Count > 0)
                    AddSharedRange(thumbnailOffset[0], thumbnailBytes[0], "JPEG 缩略图");
            }

            return ReadUInt32(table.AsSpan(entryCount * EntrySize), _bigEndian);
        }

        private async Task AddDataRangesAsync(
            Dictionary<ushort, TiffEntry> chunkTags,
            ushort offsetsTag,
            ushort lengthsTag,
            string kind,
            CancellationToken cancellationToken)
        {
            if (!chunkTags.TryGetValue(offsetsTag, out var offsets) ||
                !chunkTags.TryGetValue(lengthsTag, out var lengths))
                return;

            var offsetsValues = await ReadValuesAsync(offsets, cancellationToken).ConfigureAwait(false);
            var lengthValues = await ReadValuesAsync(lengths, cancellationToken).ConfigureAwait(false);
            var count = Math.Min(offsetsValues.Count, lengthValues.Count);
            for (var index = 0; index < count; index++)
                AddSharedRange(offsetsValues[index], lengthValues[index], kind);
        }

        private void AddDataRange(TiffEntry entry, long totalSize)
        {
            var start = ReadUInt32(entry.Raw.AsSpan(8, 4), _bigEndian);
            AddSharedRange(start, totalSize, $" tag {entry.Tag} 的外置数据");
        }

        private void AddSharedRange(long start, long length, string kind)
        {
            if (start <= 0 || length <= 0 || start >= _fileLength) return;
            var end = length > _fileLength - start ? _fileLength : start + length;
            _ranges.Add(new ProtectedRange(start, end, kind));
        }

        private async Task<List<long>> ReadValuesAsync(
            TiffEntry entry,
            CancellationToken cancellationToken)
        {
            if (entry.Count > MaxArrayValues)
                throw Unsupported(_name, $"tag {entry.Tag} 的数组值数量超过解析上限 {MaxArrayValues}");
            var values = new List<long>();
            if (entry.Count == 0) return values;

            var entrySize = TypeSize(entry.Tag, entry.Type);
            var totalSize = entrySize * (long)entry.Count;
            if (totalSize is <= 0 or > int.MaxValue || totalSize > MaxArrayBytes)
                throw Unsupported(_name, $"tag {entry.Tag} 的数组长度异常：{totalSize}");
            var baseOffset = ValueOffset(_bigEndian, entry);
            if (baseOffset < HeaderSize || totalSize > _fileLength - baseOffset)
                throw Unsupported(_name, $"tag {entry.Tag} 的数组数据越界：{baseOffset}+{totalSize}");

            var bytes = await _read(baseOffset, (int)totalSize, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < entry.Count; index++)
            {
                values.Add(entrySize switch
                {
                    1 => bytes[index],
                    2 => ReadUInt16(bytes.AsSpan(index * 2, 2), _bigEndian),
                    _ => ReadUInt32(bytes.AsSpan(index * 4, 4), _bigEndian),
                });
            }

            return values;
        }

        private void ValidateDirectoryOffset(long offset, string what)
        {
            if (offset < HeaderSize || (offset & 1) != 0 || offset + 2 > _fileLength)
                throw Unsupported(_name, $"{what}偏移无效：{offset}");
        }
    }

    private static long ValueOffset(bool bigEndian, TiffEntry entry) =>
        TypeSize(entry.Tag, entry.Type) * (long)entry.Count <= InlineValueCapacity
            ? entry.Offset + 8
            : ReadUInt32(entry.Raw.AsSpan(8, 4), bigEndian);

    private static int TypeSize(ushort tag, ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        16 or 17 or 18 => 8,
        _ => throw new StorageException(StorageError.Unsupported, $"tag {tag} 的字段类型 {type} 不受支持"),
    };

    private static RangeReader ForSession(IStorageRandomAccessSession session) =>
        (offset, length, cancellationToken) =>
            XmpContainerIO.ReadExactlyAsync(session, offset, length, cancellationToken);

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(source)
            : BinaryPrimitives.ReadUInt16LittleEndian(source);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(source)
            : BinaryPrimitives.ReadUInt32LittleEndian(source);

    private static StorageException Unsupported(string name, string reason) =>
        new(StorageError.Unsupported, $"{name}：{reason}");

    private delegate Task<byte[]> RangeReader(long offset, int length, CancellationToken cancellationToken);

    private readonly record struct ProtectedRange(long Start, long End, string Kind);

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, long Offset, byte[] Raw);

    private sealed class TiffLayout
    {
        public required bool BigEndian { get; init; }
        public required TiffEntry? XmpEntry { get; init; }
    }
}
