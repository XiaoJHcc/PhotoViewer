using System.Xml;
using XmpCore;

namespace PhotoViewer.Core;

/// <summary>
/// 一个实际存在的评分来源：某个文件的内嵌 XMP，或一个已存在的同名 sidecar。
/// <see cref="Snapshot"/> 保留 Missing / Rated / Rejected / Invalid / Conflict / Unreadable 原始语义，
/// 详情不得用 <see cref="NumericValue"/> 把“无评分”和“0 星”混同。
/// </summary>
public sealed record RatingSourceState(
    XmpRatingSource Source,
    Storage.IFileRef File,
    XmpRatingSnapshot Snapshot)
{
    public XmpRatingState State => Snapshot.State;

    public int? Value => Snapshot.Value;

    public int NumericValue => Snapshot.NumericValue;

    public string? Error => Snapshot.Error;

    /// <summary>来源显示名：内嵌为文件名，sidecar 为 <c>&lt;stem&gt;.xmp</c>。</summary>
    public string DisplayName => File.Name;
}

/// <summary>
/// 文件夹一次枚举得到的 <c>&lt;stem&gt;.xmp</c> 索引。传入该索引后，读取评分不再为每个文件
/// 单独做整目录查找（SAF 上代价很高）；索引为空也表示“该目录确实没有 sidecar”。
/// </summary>
public sealed class RatingSidecarIndex
{
    private readonly Dictionary<string, Storage.IFileRef> _byStem = new(StringComparer.OrdinalIgnoreCase);

    public static RatingSidecarIndex Empty { get; } = new();

    public int Count => _byStem.Count;

    /// <summary>枚举到一个文件；只有 <c>.xmp</c> 会进入索引。共享的 <see cref="Empty"/> 不可写入。</summary>
    /// <returns>确实作为 sidecar 收录时返回 true。</returns>
    public bool Add(Storage.IFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (ReferenceEquals(this, Empty)) return false;
        if (!string.Equals(Path.GetExtension(file.Name), ".xmp", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrEmpty(stem)) return false;
        return _byStem.TryAdd(stem, file);
    }

    /// <summary>照片名对应的已存在 sidecar；没有返回 null。</summary>
    public Storage.IFileRef? Find(string photoName)
    {
        if (string.IsNullOrEmpty(photoName)) return null;
        var stem = Path.GetFileNameWithoutExtension(photoName);
        return stem.Length > 0 && _byStem.TryGetValue(stem, out var file) ? file : null;
    }
}

/// <summary>
/// 读取一个照片的全部评分来源：内嵌 XMP（永不 sidecar 遮蔽）+ 已存在的同名 sidecar。
/// 每个来源独立保留状态；读不进来的来源（I/O、权限、超过解析上限、已消失）以
/// <see cref="XmpRatingState.Unreadable"/> 呈现，包可读但内容不可用的来源以
/// <see cref="XmpRatingState.Invalid"/> 呈现，都带原因，不静默归零。
/// </summary>
public static class RatingSourceReader
{
    private const int MaxSidecarBytes = 64 << 20;

    /// <summary>全部来源，第一项恒为内嵌来源。</summary>
    public static async Task<(XmpRatingSnapshot Embedded, IReadOnlyList<RatingSourceState> Sources)> ReadAsync(
        Storage.IFileRef photo,
        RatingSidecarIndex? sidecarIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var embedded = await ReadEmbeddedAsync(photo, cancellationToken).ConfigureAwait(false);
        var sources = new List<RatingSourceState>(2)
        {
            new(XmpRatingSource.Embedded, photo, embedded),
        };

        var sidecar = await ResolveSidecarAsync(photo, sidecarIndex, cancellationToken).ConfigureAwait(false);
        if (sidecar != null)
            sources.Add(await ReadSidecarAsync(sidecar, cancellationToken).ConfigureAwait(false));

        return (embedded, sources);
    }

    /// <summary>只读内嵌来源；sidecar 是否更“权威”不影响这里。</summary>
    public static async Task<XmpRatingSnapshot> ReadEmbeddedAsync(
        Storage.IFileRef photo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await XmpRatingReader.ReadEmbeddedAsync(photo, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsContentCorruption(exception))
        {
            // 包内容损坏（XML/XMP 解析失败）仍按 Invalid：来源读到了，但语义不可用。
            return new XmpRatingSnapshot(
                XmpRatingState.Invalid,
                null,
                XmpRatingSource.Embedded,
                $"内嵌 XMP 无法解析：{exception.Message}");
        }
        catch (Exception exception)
        {
            return XmpRatingSnapshot.Unreadable(
                XmpRatingSource.Embedded,
                $"读取内嵌 XMP 失败：{exception.Message}");
        }
    }

    /// <summary>读取一个已存在的 sidecar 文件作为独立来源。</summary>
    public static async Task<RatingSourceState> ReadSidecarAsync(
        Storage.IFileRef sidecar,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await sidecar.GetBasicPropertiesAsync(cancellationToken).ConfigureAwait(false);
            if (!properties.Exists)
            {
                return new RatingSourceState(
                    XmpRatingSource.Sidecar,
                    sidecar,
                    XmpRatingSnapshot.Unreadable(
                        XmpRatingSource.Sidecar, "sidecar 已不存在"));
            }

            if (properties.Size is 0)
            {
                return new RatingSourceState(
                    XmpRatingSource.Sidecar,
                    sidecar,
                    new XmpRatingSnapshot(
                        XmpRatingState.Missing, null, XmpRatingSource.Sidecar, "同名 sidecar 为空，没有评分"));
            }

            if (properties.Size is { } size && size > MaxSidecarBytes)
            {
                return new RatingSourceState(
                    XmpRatingSource.Sidecar,
                    sidecar,
                    XmpRatingSnapshot.Unreadable(
                        XmpRatingSource.Sidecar, "sidecar 超过解析上限"));
            }

            await using var stream = await sidecar.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream(properties.Size is { } length ? (int)length : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var snapshot = XmpRatingReader.ReadPacket(buffer.ToArray(), XmpRatingSource.Sidecar);
            if (snapshot.State == XmpRatingState.Missing)
            {
                snapshot = snapshot with { Error = "同名 sidecar 存在但没有 Rating" };
            }

            return new RatingSourceState(XmpRatingSource.Sidecar, sidecar, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsContentCorruption(exception))
        {
            // 包内容损坏（XML/XMP 解析失败）按 Invalid；读不进来（I/O、权限）才是 Unreadable。
            return new RatingSourceState(
                XmpRatingSource.Sidecar,
                sidecar,
                new XmpRatingSnapshot(
                    XmpRatingState.Invalid,
                    null,
                    XmpRatingSource.Sidecar,
                    $"sidecar 的 XMP 无法解析：{exception.Message}"));
        }
        catch (Exception exception)
        {
            return new RatingSourceState(
                XmpRatingSource.Sidecar,
                sidecar,
                XmpRatingSnapshot.Unreadable(
                    XmpRatingSource.Sidecar,
                    $"读取 sidecar 失败：{exception.Message}"));
        }
    }

    /// <summary>包内容本身无法解析（与“来源读不进来”区分）。</summary>
    private static bool IsContentCorruption(Exception exception) =>
        exception is XmpException or XmlException;

    /// <summary>找出照片对应的已存在 sidecar；有索引时不再触碰目录。</summary>
    public static async Task<Storage.IFileRef?> ResolveSidecarAsync(
        Storage.IFileRef photo,
        RatingSidecarIndex? sidecarIndex,
        CancellationToken cancellationToken = default)
    {
        if (sidecarIndex != null) return sidecarIndex.Find(photo.Name);

        var parent = await photo.GetParentAsync(cancellationToken).ConfigureAwait(false);
        if (parent == null) return null;
        return await parent
            .FindFileAsync(XmpRatingReader.SidecarName(photo.Name), cancellationToken)
            .ConfigureAwait(false);
    }
}
