using System;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 缩略图来源类别。
/// </summary>
public enum ThumbnailOrigin
{
    /// <summary>EXIF/IFD1 内嵌缩略图（标准 ExifThumbnailDirectory）。</summary>
    ExifEmbedded,
    /// <summary>厂商 MakerNote 内的 PreviewImage（如 Sony Preview）。</summary>
    MakernotePreview,
    /// <summary>HEIF/HEIC 容器中的内嵌缩略图（由平台解码器选择最优尺寸条目）。</summary>
    HeifEmbedded,
    /// <summary>原图:通过解码原始图片子采样得到。</summary>
    FullImage,
}

/// <summary>
/// 单个缩略图来源描述:尺寸 + 来源类型 + 是否预旋转 + 内部加载策略。
/// 由 <see cref="ThumbnailService"/> 创建；上层只读使用。
/// 当尺寸未知时 <see cref="Width"/> / <see cref="Height"/> 为 0；
/// 此情况下选源时按"未知尺寸排到最后"处理。
/// <see cref="Width"/>/<see cref="Height"/> 始终是字节本身的像素维度（不含旋转），
/// 选源用 <see cref="DisplayShortSide"/>（考虑 PreRotated 标志后的显示短边）。
/// </summary>
public sealed class ThumbnailSource
{
    /// <summary>来源宽度（字节本身像素，未应用旋转）；未知时为 0。</summary>
    public int Width { get; }

    /// <summary>来源高度（字节本身像素，未应用旋转）；未知时为 0。</summary>
    public int Height { get; }

    /// <summary>来源类型。</summary>
    public ThumbnailOrigin Origin { get; }

    /// <summary>
    /// 该来源解码出的位图是否已经预先应用了显示旋转（true=显示朝向；false=传感器朝向，调用方需自行补旋转）。
    /// 平台 HEIF 解码器返回的位图通常已应用 Default Rotation，故为 true；
    /// 任何"按字节直读 + Avalonia 解码"的来源都是 false。
    /// </summary>
    public bool IsPreRotated { get; }

    /// <summary>字节自身短边像素;任一维为 0 则返回 0。</summary>
    public int ShortSide => (Width > 0 && Height > 0) ? Math.Min(Width, Height) : 0;

    /// <summary>字节自身长边像素;任一维为 0 则返回 0。</summary>
    public int LongSide => (Width > 0 && Height > 0) ? Math.Max(Width, Height) : 0;

    /// <summary>
    /// 内部加载策略:由具体提取器闭包持有 (file/offset/tagByteArray/解码器引用) 等。
    /// 仅供 ThumbnailService 调用;不暴露给业务层。
    /// </summary>
    internal Func<int, System.Threading.Tasks.Task<Avalonia.Media.Imaging.Bitmap?>> LoaderAsync { get; }

    internal ThumbnailSource(
        int width,
        int height,
        ThumbnailOrigin origin,
        bool isPreRotated,
        Func<int, System.Threading.Tasks.Task<Avalonia.Media.Imaging.Bitmap?>> loaderAsync)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Origin = origin;
        IsPreRotated = isPreRotated;
        LoaderAsync = loaderAsync;
    }
}
