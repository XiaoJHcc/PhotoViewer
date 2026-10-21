namespace PhotoViewer.Core.Image;

/// <summary>
/// 解码尺寸档提示:Full = 全分辨率;Proxy = 长边不超过 MaxLongEdge 的代理图
/// (渲染表面物理像素 × overscan,供 fit/浏览快速上屏;100% 及以上放大永远以 Full 呈现)。
/// </summary>
public readonly record struct DecodeSizeHint(ImageSizeClass Class, int MaxLongEdge)
{
    /// <summary>全分辨率。</summary>
    public static readonly DecodeSizeHint Full = new(ImageSizeClass.Full, 0);

    /// <summary>代理图:长边上限像素。</summary>
    public static DecodeSizeHint ProxyTo(int maxLongEdge) => new(ImageSizeClass.Proxy, maxLongEdge);
}
