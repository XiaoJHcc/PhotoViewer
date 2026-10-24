using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// 按文件扩展名选择评分存储：内嵌 XMP 的图片走容器适配器，专有 RAW 走 sidecar，
/// <c>.xmp</c> 按独立 sidecar 处理。不受支持的格式明确抛出
/// <see cref="StorageError.Unsupported"/>，绝不返回 null 或静默假成功。
/// </summary>
public static class XmpRatingStoreFactory
{
    public static IXmpRatingStore Create(IFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = Path.GetExtension(file.Name);
        if (HasExtension(extension, ".xmp")) return SidecarRatingStore.ForStandaloneXmp(file);

        if (XmpRatingReader.PrefersSidecar(file.Name))
        {
            IXmpContainerAdapter embedded = HasExtension(extension, ".cr3")
                ? new HeifXmpContainerAdapter()
                : new TiffXmpContainerAdapter();
            return SidecarRatingStore.ForPhoto(file, embedded);
        }

        var adapter = CreateEmbeddedAdapter(extension)
            ?? throw Unsupported(file.Name, extension);
        return new ContainerRatingStore(file, adapter);
    }

    public static bool IsSupported(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var extension = Path.GetExtension(fileName);
        return HasExtension(extension, ".xmp")
               || XmpRatingReader.PrefersSidecar(fileName)
               || CreateEmbeddedAdapter(extension) != null;
    }

    private static IXmpContainerAdapter? CreateEmbeddedAdapter(string extension)
    {
        if (HasExtension(extension, ".jpg", ".jpeg", ".jpe", ".jfif"))
            return new JpegXmpContainerAdapter();
        if (HasExtension(extension, ".heic", ".heif", ".hif", ".avif"))
            return new HeifXmpContainerAdapter();
        if (HasExtension(extension, ".png"))
            return new PngXmpContainerAdapter();
        if (HasExtension(extension, ".webp"))
            return new WebpXmpContainerAdapter();
        if (HasExtension(extension, ".tif", ".tiff", ".dng"))
            return new TiffXmpContainerAdapter();
        return null;
    }

    private static bool HasExtension(string extension, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase));

    private static StorageException Unsupported(string name, string extension) =>
        new(
            StorageError.Unsupported,
            string.IsNullOrEmpty(extension)
                ? $"{name} 没有扩展名，无法确定 XMP 评分写入方式"
                : $"{name} 的格式 {extension} 不支持 XMP 评分写入");
}
