using System;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoViewer.Core.Image;
using SkiaSharp;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 把 presenter 给出的 8 位诊断网格位图(1:1 原尺寸)按照片纵横比铺开并最近邻放大到可读尺寸,
/// 再交给 XAML。分析栏与 DINO 诊断工具页共用,避免两边各抄一份网格拉伸口径。
/// </summary>
internal static class WindowsDiagnosticBitmap
{
    /// <summary>
    /// 按纵横比把方格诊断图放大到长边固定、另一向按比例收的位图。
    /// </summary>
    /// <param name="image">派生位图(如 32×32 热力图);null 返回 null</param>
    /// <param name="aspect">照片纵横比(宽/高);网格按它铺开,瓦片里才与原片比例一致</param>
    /// <returns>XAML 位图;源为空或取租约失败时返回 null</returns>
    public static WriteableBitmap? ToUpscaled(DecodedImage? image, double aspect)
    {
        if (image == null || !image.TryAcquire())
        {
            return null;
        }

        try
        {
            ResolveSize(aspect, WindowsLayout.AnalysisDiagnosticLongEdge, out int width, out int height);
            using var bitmap = new SKBitmap(new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque,
                SKColorSpace.CreateSrgb()));
            var info = new SKImageInfo(
                image.Width,
                image.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque,
                SKColorSpace.CreateSrgb());
            unsafe
            {
                fixed (byte* pixels = image.Pixels)
                {
                    using var source = SKImage.FromPixels(info, (IntPtr)pixels, image.Stride)
                        ?? throw new InvalidOperationException("诊断位图无法包装为 Skia 图像");
                    using var canvas = new SKCanvas(bitmap);
                    canvas.DrawImage(
                        source,
                        SKRect.Create(0, 0, image.Width, image.Height),
                        SKRect.Create(0, 0, width, height),
                        new SKSamplingOptions(SKFilterMode.Nearest),
                        new SKPaint());
                }
            }

            return WindowsImageAdapter.ToSource(width, height, bitmap.GetPixelSpan());
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>按照片纵横比求诊断位图尺寸:长边固定,另一向按比例收。</summary>
    /// <param name="aspect">照片纵横比(宽/高)</param>
    /// <param name="longEdge">长边像素</param>
    /// <param name="width">输出宽</param>
    /// <param name="height">输出高</param>
    private static void ResolveSize(double aspect, int longEdge, out int width, out int height)
    {
        double ratio = aspect > 0 ? aspect : 1;
        if (ratio >= 1)
        {
            width = longEdge;
            height = Math.Max(1, (int)Math.Round(longEdge / ratio));
        }
        else
        {
            width = Math.Max(1, (int)Math.Round(longEdge * ratio));
            height = longEdge;
        }
    }
}
