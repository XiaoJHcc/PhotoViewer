using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Desktop.Core;

/// <summary>
/// Windows 展示编码策略：本阶段主图表面固定使用 RGBA16F 线性 scRGB，
/// 因此任何尺寸档与内存压力都返回 <see cref="PixelEncoding.RgbaF16"/>。
/// W-M7 引入 8 位/广色域展示后再按平台能力扩展，Core 不感知 Windows 判断。
/// </summary>
public sealed class WindowsPresentationEncodingPolicy : IPresentationEncodingPolicy
{
    /// <inheritdoc/>
    public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure) => PixelEncoding.RgbaF16;
}
