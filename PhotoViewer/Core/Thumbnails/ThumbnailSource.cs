using System;

namespace PhotoViewer.Core.Thumbnails;

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
    /// <summary>原图：通过解码原始图片子采样得到。</summary>
    FullImage,
}

/// <summary>
/// 单个缩略图来源描述：尺寸 + 来源类型 + 内部加载策略。
/// 由 <see cref="ThumbnailService"/> 创建；上层只读使用。
/// 当尺寸未知时 <see cref="Width"/> / <see cref="Height"/> 为 0；
/// 此情况下选源时按"未知尺寸排到最后"处理。
/// </summary>
public sealed class ThumbnailSource
{
    /// <summary>来源宽度（像素）；未知时为 0。</summary>
    public int Width { get; }

    /// <summary>来源高度（像素）；未知时为 0。</summary>
    public int Height { get; }

    /// <summary>来源类型。</summary>
    public ThumbnailOrigin Origin { get; }

    /// <summary>短边像素；任一维为 0 则返回 0。</summary>
    public int ShortSide => (Width > 0 && Height > 0) ? Math.Min(Width, Height) : 0;

    /// <summary>长边像素；任一维为 0 则返回 0。</summary>
    public int LongSide => (Width > 0 && Height > 0) ? Math.Max(Width, Height) : 0;

    /// <summary>
    /// 内部加载策略：由具体提取器闭包持有 (file/offset/tagByteArray/解码器引用) 等。
    /// 仅供 ThumbnailService 调用；不暴露给业务层。
    /// </summary>
    internal Func<int, System.Threading.Tasks.Task<Avalonia.Media.Imaging.Bitmap?>> LoaderAsync { get; }

    internal ThumbnailSource(
        int width,
        int height,
        ThumbnailOrigin origin,
        Func<int, System.Threading.Tasks.Task<Avalonia.Media.Imaging.Bitmap?>> loaderAsync)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Origin = origin;
        LoaderAsync = loaderAsync;
    }
}
