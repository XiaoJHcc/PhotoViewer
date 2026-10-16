using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS 展示编码策略：恒为 RGBA16F 扩展线性 sRGB（P2 起已验收的行为，I2 编码协商不得改变 Mac 产物）。
/// 显示器采样只受显示色彩空间影响，F16 表面同时承载广色域超范围分量与 8 位 sRGB 图源。
/// </summary>
public sealed class MacPresentationEncodingPolicy : IPresentationEncodingPolicy
{
    /// <summary>任何尺寸档与压力级别都返回 RGBA16F 扩展线性 sRGB。</summary>
    /// <param name="sizeClass">尺寸档（Mac 不区分）。</param>
    /// <param name="pressure">压力级别（Mac 不区分）。</param>
    /// <returns>RGBA16F。</returns>
    public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure) => PixelEncoding.RgbaF16;
}
