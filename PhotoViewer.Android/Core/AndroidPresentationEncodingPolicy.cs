using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 展示编码恒为 8 位 RGBA；解码目标按设备能力选择，上传时规范到实际表面色域。
/// Android 不做 F16——代理与 Full 都是 8 位，代理↔Full 只换纹理尺寸，没有编码跳变。
/// </summary>
public sealed class AndroidPresentationEncodingPolicy : IPresentationEncodingPolicy
{
    /// <summary>任何尺寸档与压力级别都返回 8 位 RGBA。</summary>
    /// <param name="sizeClass">本次请求的尺寸档。</param>
    /// <param name="pressure">当前内存压力。</param>
    /// <returns><see cref="PixelEncoding.Rgba8"/>。</returns>
    public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure) => PixelEncoding.Rgba8;
}
