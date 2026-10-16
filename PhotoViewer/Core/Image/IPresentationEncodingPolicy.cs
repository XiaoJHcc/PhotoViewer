using PhotoViewer.Core.Memory;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 展示解码的像素编码协商策略（I2）：由平台在启动期注入，按尺寸档与当前内存压力给出本次解码应产出的编码。
/// Core 不含任何平台判断——macOS 恒为 RGBA16F 扩展线性 sRGB，iPad 代理档 RGBA16F、Full 档 8 位 Display P3，
/// 压力 ≥ Warning 时代理档同样降为 8 位 P3。
/// </summary>
public interface IPresentationEncodingPolicy
{
    /// <summary>
    /// 选择本次展示解码的像素编码。
    /// </summary>
    /// <param name="sizeClass">本次请求的尺寸档。</param>
    /// <param name="pressure">请求时刻的内存压力级别。</param>
    /// <returns>解码器必须产出的编码；解码器不支持时自行显式抛出，不做静默降级。</returns>
    PixelEncoding Select(ImageSizeClass sizeClass, PressureLevel pressure);
}
