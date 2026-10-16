using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iPad 展示编码策略（I2 已定决策）：代理档用 RGBA16F 扩展线性 sRGB 保画质，全分辨率档用 8 位 Display P3 省一半内存；
/// 内存压力 ≥ Warning 时代理档同样降为 8 位 P3，让连翻期间的每张驻留字节减半而不降 Full 的画质门槛。
/// 渲染器把两条路径统一换算到扩展线性 sRGB 绘制，故热替换不产生可见色偏。
/// </summary>
public sealed class iOSPresentationEncodingPolicy : IPresentationEncodingPolicy
{
    /// <summary>按尺寸档与压力选择编码（Full 恒 8 位 P3；Proxy 在 Warning 起降为 8 位 P3）。</summary>
    /// <param name="sizeClass">尺寸档。</param>
    /// <param name="pressure">请求时刻的内存压力级别。</param>
    /// <returns>本次解码应产出的编码。</returns>
    public PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure)
        => sizeClass == ImageSizeClass.Full || pressure >= PressureLevel.Warning
            ? PixelEncoding.Rgba8
            : PixelEncoding.RgbaF16;
}
