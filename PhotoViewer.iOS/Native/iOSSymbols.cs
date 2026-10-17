using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using SkiaSharp;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生图标与小图工厂：命令→SF Symbols 名称的映射在共享 <see cref="PhotoViewer.Apple.AppleSymbolNames"/>，
/// 本类只把名称换成 UIKit 对象。缩略图与 Mac 同口径：把共享缩略图管线的 BGRA8 产物编码成带 sRGB 标签的 PNG，
/// 再交 UIKit 管理独立的像素生命周期（共享 <see cref="DecodedImage"/> 的租约仍由缩略图管线持有）。
/// </summary>
internal static class iOSSymbols
{
    /// <summary>取得系统符号；缺失即显式报告映射问题，不生成空白按钮。</summary>
    /// <param name="name">SF Symbols 名称。</param>
    /// <param name="pointSize">符号点尺寸；0 表示用系统默认配置。</param>
    /// <returns>可用于按钮的模板符号图。</returns>
    public static UIImage Symbol(string name, double pointSize = 0)
    {
        var image = UIImage.GetSystemImage(name) ?? throw new InvalidOperationException($"系统符号不可用：{name}");
        if (pointSize <= 0) return image;
        return image.ApplyConfiguration(UIImageSymbolConfiguration.Create((nfloat)pointSize))
            ?? throw new InvalidOperationException($"系统符号配置失败：{name}");
    }

    /// <summary>把小图编码成带 sRGB 标签的 PNG，再交 UIImage 管理独立的像素生命周期。</summary>
    /// <param name="source">缩略图管线产物（BGRA8 预乘）。</param>
    /// <returns>可由 UIImageView 直接展示的位图。</returns>
    public static unsafe UIImage CreateThumbnail(DecodedImage source)
    {
        if (source.Encoding != PixelEncoding.Bgra32) throw new InvalidOperationException("胶片栏需要 BGRA8 缩略图");
        using var space = SKColorSpace.CreateSrgb();
        fixed (byte* pixels = source.Pixels)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul, space), (IntPtr)pixels, source.Stride);
            using var encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("缩略图编码失败");
            using var data = NSData.FromArray(encoded.ToArray());
            return UIImage.LoadFromData(data) ?? throw new InvalidOperationException("缩略图解码失败");
        }
    }
}
