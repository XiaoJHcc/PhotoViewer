using System.Globalization;
using System.Xml;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoViewer.Core.Storage;
using XmpCore;

namespace PhotoViewer.Core;

public enum XmpRatingState
{
    Missing,
    Rated,
    Rejected,

    /// <summary>包可解析，但 Rating 值本身不可用（非整数、越界）或包内容损坏。</summary>
    Invalid,

    Conflict,

    /// <summary>来源根本无法读取：I/O 失败、权限被拒、容器结构不识别、包超过解析上限。</summary>
    Unreadable,
}

public enum XmpRatingSource
{
    Embedded,
    Sidecar,
}

public readonly record struct XmpRatingSnapshot(
    XmpRatingState State,
    int? Value,
    XmpRatingSource Source,
    string? Error = null)
{
    /// <summary>筛选与显示用的数值：Rated 取原值、Rejected 为 -1、其余（含 Unreadable）为 0。</summary>
    public int NumericValue => State switch
    {
        XmpRatingState.Rated => Value ?? 0,
        XmpRatingState.Rejected => -1,
        _ => 0,
    };

    /// <summary>可写入/可比较的数值；Missing / Invalid / Conflict / Unreadable 为 null（不与 0 混同）。</summary>
    public int? ValueOrNull => State is XmpRatingState.Rated or XmpRatingState.Rejected
        ? State == XmpRatingState.Rejected ? -1 : Value
        : null;

    /// <summary>只要 Rated 才算有星级；Rejected 与缺失/无效/不可读都返回 0，仅用于旧调用方。</summary>
    public int ValueOrZero => State == XmpRatingState.Rated ? Value!.Value : 0;

    public static XmpRatingSnapshot Missing(XmpRatingSource source) =>
        new(XmpRatingState.Missing, null, source);

    public static XmpRatingSnapshot Unreadable(XmpRatingSource source, string error) =>
        new(XmpRatingState.Unreadable, null, source, error);
}

/// <summary>内嵌 XMP 目录写入预检结果：数量/扩展声明/空元数据与合并快照。</summary>
public sealed record EmbeddedRatingProbe(
    int XmpDirectoryCount,
    bool HasExtendedXmp,
    bool HasNullMeta,
    XmpRatingSnapshot Snapshot);

public static class XmpRatingReader
{
    public const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";
    public const string XmpNoteNamespace = "http://ns.adobe.com/xmp/note/";

    public static XmpRatingSnapshot Read(IXmpMeta? xmp, XmpRatingSource source = XmpRatingSource.Embedded)
    {
        if (xmp == null)
        {
            return new XmpRatingSnapshot(
                XmpRatingState.Invalid, null, source, "XMP 目录没有可解析的元数据");
        }

        var property = xmp.GetProperty(XmpNamespace, "Rating");
        if (property?.Value == null) return XmpRatingSnapshot.Missing(source);
        if (!int.TryParse(property.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return new XmpRatingSnapshot(XmpRatingState.Invalid, null, source, "XMP Rating 不是整数");
        if (value == -1) return new XmpRatingSnapshot(XmpRatingState.Rejected, -1, source);
        if (value is >= 0 and <= 5) return new XmpRatingSnapshot(XmpRatingState.Rated, value, source);
        return new XmpRatingSnapshot(XmpRatingState.Invalid, null, source, $"XMP Rating 超出范围：{value}");
    }

    public static XmpRatingSnapshot ReadPacket(byte[] packet, XmpRatingSource source)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return Read(XmpMetaFactory.ParseFromBuffer(packet), source);
    }

    /// <summary>
    /// 合并同一文件的多份 XMP 目录。每个目录各自独立判定（Missing 也参与比较）：
    /// 任一份不可读/无效先返回该状态；否则全部目录必须状态与取值一致，
    /// 一份 Rated 另一份 Missing（或取值不同）判 <see cref="XmpRatingState.Conflict"/>。
    /// </summary>
    public static XmpRatingSnapshot ReadDirectories(
        IEnumerable<XmpDirectory> directories,
        XmpRatingSource source = XmpRatingSource.Embedded)
    {
        var snapshots = directories
            .Select(directory => ReadSafely(directory.XmpMeta, source))
            .ToArray();
        if (snapshots.Length == 0) return XmpRatingSnapshot.Missing(source);
        var brokenIndex = Array.FindIndex(snapshots, snapshot =>
            snapshot.State is XmpRatingState.Invalid or XmpRatingState.Unreadable);
        if (brokenIndex >= 0) return snapshots[brokenIndex];
        var first = snapshots[0];
        return snapshots.All(snapshot => snapshot.State == first.State && snapshot.Value == first.Value)
            ? first
            : new XmpRatingSnapshot(XmpRatingState.Conflict, null, source, "多个内嵌 XMP 包含不同的 Rating");
    }

    /// <summary>单个目录的读取：包内容损坏（XML/XMP 解析失败）判 Invalid，不让一个坏目录吞掉其余信息。</summary>
    private static XmpRatingSnapshot ReadSafely(IXmpMeta? xmp, XmpRatingSource source)
    {
        try
        {
            return Read(xmp, source);
        }
        catch (Exception exception) when (exception is XmpException or XmlException)
        {
            return new XmpRatingSnapshot(
                XmpRatingState.Invalid, null, source, $"XMP 无法解析：{exception.Message}");
        }
    }

    public static async Task<XmpRatingSnapshot> ReadEmbeddedAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var directories = ImageMetadataReader.ReadMetadata(stream);
        return ReadDirectories(directories.OfType<XmpDirectory>());
    }

    /// <summary>
    /// 写入预检用：重新读取文件的内嵌 XMP 目录，返回目录数量、是否含 extended XMP、
    /// 是否有无法解析的目录以及合并后的评分快照。调用方据此拒绝多目标/扩展 XMP。
    /// </summary>
    public static async Task<EmbeddedRatingProbe> ProbeEmbeddedAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var directories = ImageMetadataReader
            .ReadMetadata(stream)
            .OfType<XmpDirectory>()
            .ToArray();

        var hasNullMeta = directories.Any(directory => directory.XmpMeta == null);
        var hasExtended = directories.Any(directory =>
            directory.XmpMeta?.GetProperty(XmpNoteNamespace, "HasExtendedXMP")?.Value != null);

        XmpRatingSnapshot snapshot;
        if (directories.Length == 0)
        {
            snapshot = XmpRatingSnapshot.Missing(XmpRatingSource.Embedded);
        }
        else if (directories.Length > 1)
        {
            snapshot = new XmpRatingSnapshot(
                XmpRatingState.Conflict,
                null,
                XmpRatingSource.Embedded,
                "同一文件存在多份 XMP 目录，无法证明唯一写入目标");
        }
        else if (hasNullMeta)
        {
            snapshot = new XmpRatingSnapshot(
                XmpRatingState.Invalid,
                null,
                XmpRatingSource.Embedded,
                "XMP 目录没有可解析的元数据");
        }
        else
        {
            snapshot = Read(directories[0].XmpMeta);
        }

        return new EmbeddedRatingProbe(directories.Length, hasExtended, hasNullMeta, snapshot);
    }

    /// <summary>
    /// 读取文件自身的内嵌评分。内嵌是独立来源：sidecar 存在时也不会遮蔽这里的结果。
    /// </summary>
    public static async Task<XmpRatingSnapshot> ReadAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        return await ReadEmbeddedAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取已存在的 <c>&lt;stem&gt;.xmp</c>；不存在返回 null，绝不创建。</summary>
    public static async Task<XmpRatingSnapshot?> ReadSidecarAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        var parent = await file.GetParentAsync(cancellationToken).ConfigureAwait(false);
        if (parent == null) return null;
        var sidecar = await parent.FindFileAsync(SidecarName(file.Name), cancellationToken).ConfigureAwait(false);
        if (sidecar == null) return null;
        await using var stream = await sidecar.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var packet = new MemoryStream();
        await stream.CopyToAsync(packet, cancellationToken).ConfigureAwait(false);
        return ReadPacket(packet.ToArray(), XmpRatingSource.Sidecar);
    }

    public static string SidecarName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName) + ".xmp";
}
