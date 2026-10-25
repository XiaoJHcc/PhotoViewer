using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// 按文件扩展名选择内嵌 XMP 评分来源。RAW 也写回内嵌（ARW/CR2/NEF/ORF/RW2/SRW 为 TIFF，
/// CR3 为 HEIF）；不存在“专用 RAW 就写 sidecar”的隐式规则。
/// </summary>
/// <remarks>
/// <para>不支持的格式明确抛出 <see cref="StorageError.Unsupported"/>，绝不返回 null 或静默假成功。</para>
/// <para>PNG 本轮明确不支持写入：容器写入需要同步重建 iTXt 长度与 CRC，事务层不做冒险写；
/// 读取仍可由 <see cref="PngXmpContainerAdapter"/> 或元数据读取路径完成。</para>
/// <para><c>.xmp</c> 文件本身是一个已存在的评分来源；<see cref="CreateSidecar"/> 供同名 sidecar 使用，
/// 两者都不创建文件——创建 sidecar 的能力已从本类移除。</para>
/// </remarks>
public static class XmpRatingStoreFactory
{
    /// <summary>照片自身的内嵌 XMP 来源；<c>.xmp</c> 文件返回该文件自身。</summary>
    public static IXmpRatingStore Create(IFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = Path.GetExtension(file.Name);
        if (HasExtension(extension, ".xmp"))
            return new ContainerRatingStore(file, new XmpFileContainerAdapter(), XmpRatingSource.Sidecar);
        if (HasExtension(extension, ".png"))
            throw Unsupported(file.Name, extension, "PNG 写入需要同步重建 iTXt 长度与 CRC，本轮不支持");

        var adapter = CreateEmbeddedAdapter(extension)
            ?? throw Unsupported(file.Name, extension);
        return new ContainerRatingStore(file, adapter);
    }

    /// <summary>一个已存在的 <c>&lt;stem&gt;.xmp</c> 作为独立来源。</summary>
    public static IXmpRatingStore CreateSidecar(IFileRef sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        if (!HasExtension(Path.GetExtension(sidecar.Name), ".xmp"))
            throw new ArgumentException($"{sidecar.Name} 不是 .xmp 文件", nameof(sidecar));
        return new ContainerRatingStore(sidecar, new XmpFileContainerAdapter(), XmpRatingSource.Sidecar);
    }

    public static bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var extension = Path.GetExtension(fileName);
        return HasExtension(extension, ".xmp") || CreateEmbeddedAdapter(extension) != null;
    }

    /// <summary>该扩展名是否有内嵌 XMP 容器适配器（决定它能否作为一个可写的内嵌来源）。</summary>
    public static bool HasEmbeddedAdapter(string fileName) =>
        CreateEmbeddedAdapter(Path.GetExtension(fileName)) != null;

    private static IXmpContainerAdapter? CreateEmbeddedAdapter(string extension)
    {
        if (HasExtension(extension, ".jpg", ".jpeg", ".jpe", ".jfif"))
            return new JpegXmpContainerAdapter();
        if (HasExtension(extension, ".heic", ".heif", ".hif", ".avif", ".cr3"))
            return new HeifXmpContainerAdapter();
        if (HasExtension(extension, ".webp"))
            return new WebpXmpContainerAdapter();
        if (HasExtension(extension, ".tif", ".tiff", ".dng", ".arw", ".cr2", ".nef", ".orf", ".rw2", ".srw"))
            return new TiffXmpContainerAdapter();
        return null;
    }

    private static bool HasExtension(string extension, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase));

    private static StorageException Unsupported(string name, string extension, string? reason = null) =>
        new(
            StorageError.Unsupported,
            reason ??
            (string.IsNullOrEmpty(extension)
                ? $"{name} 没有扩展名，无法确定 XMP 评分写入方式"
                : $"{name} 的格式 {extension} 不支持 XMP 评分读写"));
}
