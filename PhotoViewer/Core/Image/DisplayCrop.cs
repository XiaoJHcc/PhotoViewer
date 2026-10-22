using System;
using PhotoViewer.Core.Color;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 显示朝向的 1:1 细节裁剪：从容器朝向的原始像素里裁出显示朝向下某个归一化点周围的方形块。
/// 原生大图（如 42MP RAW 的 F16 线性表面）纹理方向由 GPU 应用，像素仍是容器朝向，
/// 因此裁剪必须先做坐标反映射再对小块做一次方向变换 — 全程不复制整幅源图。
/// </summary>
public static class DisplayCrop
{
    /// <summary>
    /// 裁出显示朝向的方形细节块（边长 <paramref name="pixels"/> 个源像素，1:1 无重采样）。
    /// 先把显示归一化中心反映射到容器朝向（见 <see cref="ImageOrientationMapping"/>），再按容器坐标裁剪，
    /// 最后把小块转成 sRGB8 并做方向变换。
    /// </summary>
    /// <param name="source">容器朝向的源图（编码与色彩标签不限，本方法只读不释放）。</param>
    /// <param name="orientation">源图的容器方向码（1..8）。</param>
    /// <param name="centerU">显示朝向归一化中心横坐标（0..1）。</param>
    /// <param name="centerV">显示朝向归一化中心纵坐标（0..1）。</param>
    /// <param name="pixels">裁剪边长（源像素）；被源图短边与边界夹取。</param>
    /// <returns>显示朝向的 sRGB8 方块；参数非法或裁剪失败返回 null。</returns>
    public static DecodedImage? CropSquare(DecodedImage source, int orientation, double centerU, double centerV, int pixels)
    {
        int side = Math.Min(pixels, Math.Min(source.Width, source.Height));
        if (side <= 0) return null;

        var (sourceU, sourceV) = ImageOrientationMapping.DisplayToSource(
            orientation, Math.Clamp(centerU, 0, 1), Math.Clamp(centerV, 0, 1));
        int x = Math.Clamp((int)Math.Round(sourceU * source.Width) - side / 2, 0, source.Width - side);
        int y = Math.Clamp((int)Math.Round(sourceV * source.Height) - side / 2, 0, source.Height - side);

        var crop = SkiaImageCodec.Crop(source, x, y, side, side);
        if (crop == null) return null;

        DecodedImage working = crop;
        if (crop.Encoding != PixelEncoding.Bgra32 || crop.ColorSpace != ImageColorSpace.Srgb)
        {
            working = ColorEngine.ToSrgb8(crop);
            crop.Dispose();
        }

        var (rotationCw, mirror) = ImageOrientationMapping.ToDisplay(orientation);
        if (rotationCw == 0 && !mirror) return working;

        try { return SkiaImageCodec.ApplyOrientation(working, rotationCw, mirror); }
        finally { working.Dispose(); }
    }
}
