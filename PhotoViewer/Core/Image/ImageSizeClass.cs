namespace PhotoViewer.Core.Image;

/// <summary>
/// 位图缓存的尺寸档。<see cref="BitmapLoader"/> 以 (路径, 尺寸档) 为缓存键,
/// 缩略图/代理图/全分辨率同驻一个统一 LRU(分区预算,淘汰按保留排名生效)。
/// </summary>
public enum ImageSizeClass
{
    /// <summary>列表缩略图(短边 ~120)。分区有独立张数/字节上限。</summary>
    Thumb,

    /// <summary>代理图(渲染表面物理像素 × overscan;放大态用)。</summary>
    Proxy,

    /// <summary>全分辨率原图。</summary>
    Full,
}
