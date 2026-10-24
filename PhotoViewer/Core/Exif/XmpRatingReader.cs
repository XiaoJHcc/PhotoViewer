using System.Globalization;
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
    Invalid,
    Conflict,
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
    public int ValueOrZero => State == XmpRatingState.Rated ? Value!.Value : 0;

    public static XmpRatingSnapshot Missing(XmpRatingSource source) =>
        new(XmpRatingState.Missing, null, source);
}

public static class XmpRatingReader
{
    public const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";

    public static XmpRatingSnapshot Read(IXmpMeta? xmp, XmpRatingSource source = XmpRatingSource.Embedded)
    {
        if (xmp == null) return XmpRatingSnapshot.Missing(source);
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

    public static XmpRatingSnapshot ReadDirectories(
        IEnumerable<XmpDirectory> directories,
        XmpRatingSource source = XmpRatingSource.Embedded)
    {
        var snapshots = directories
            .Select(directory => Read(directory.XmpMeta, source))
            .Where(snapshot => snapshot.State != XmpRatingState.Missing)
            .ToArray();
        if (snapshots.Length == 0) return XmpRatingSnapshot.Missing(source);
        if (snapshots.Any(snapshot => snapshot.State == XmpRatingState.Invalid))
            return snapshots.First(snapshot => snapshot.State == XmpRatingState.Invalid);
        var first = snapshots[0];
        return snapshots.All(snapshot => snapshot.State == first.State && snapshot.Value == first.Value)
            ? first
            : new XmpRatingSnapshot(XmpRatingState.Conflict, null, source, "多个内嵌 XMP 包含不同的 Rating");
    }

    public static async Task<XmpRatingSnapshot> ReadEmbeddedAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var directories = ImageMetadataReader.ReadMetadata(stream);
        return ReadDirectories(directories.OfType<XmpDirectory>());
    }

    public static async Task<XmpRatingSnapshot> ReadAsync(
        IFileRef file,
        CancellationToken cancellationToken = default)
    {
        if (PrefersSidecar(file.Name))
        {
            var sidecar = await ReadSidecarAsync(file, cancellationToken).ConfigureAwait(false);
            if (sidecar.HasValue) return sidecar.Value;
        }
        return await ReadEmbeddedAsync(file, cancellationToken).ConfigureAwait(false);
    }

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

    public static bool PrefersSidecar(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is
            ".arw" or ".cr2" or ".cr3" or ".nef" or ".raf" or ".orf" or ".rw2" or ".srw";
}
