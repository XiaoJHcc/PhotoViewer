using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Platform;

/// <summary>
/// <see cref="DecodedImage"/> → Avalonia <see cref="WriteableBitmap"/> 适配器(去 Avalonia 重构的临时边界,原生壳阶段删除)。
/// 经 <see cref="ConditionalWeakTable{TKey,TValue}"/> 缓存:同一个 DecodedImage 至多转换一次,
/// 位图生命周期跟随 DecodedImage(缓存被淘汰、key 被 GC 后条目自动失效)。
/// </summary>
public static class DecodedImageAdapter
{
    private static readonly ConditionalWeakTable<DecodedImage, WriteableBitmap> Cache = new();

    /// <summary>
    /// 把已解码图像转换为 Avalonia 位图;同一实例重复调用返回同一位图。
    /// </summary>
    /// <param name="image">已解码图像;null 返回 null。</param>
    /// <returns>可直接上屏的位图。</returns>
    public static WriteableBitmap? ToBitmap(DecodedImage? image)
    {
        if (image == null) return null;
        return Cache.GetValue(image, CreateBitmap);
    }

    /// <summary>执行像素行拷贝,生成 WriteableBitmap。</summary>
    private static WriteableBitmap CreateBitmap(DecodedImage image)
    {
        var format = image.Encoding == PixelEncoding.Rgb24 ? PixelFormats.Rgb24 : PixelFormats.Bgra8888;
        var wb = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), format, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var rowBytes = image.Width * DecodedImage.BytesPerPixel(image.Encoding);
            unsafe
            {
                fixed (byte* srcPtr = image.Pixels)
                {
                    var destPtr = (byte*)fb.Address;
                    for (int y = 0; y < image.Height; y++)
                    {
                        var srcRow = srcPtr + (long)y * image.Stride;
                        var destRow = destPtr + (long)y * fb.RowBytes;
                        Buffer.MemoryCopy(srcRow, destRow, rowBytes, rowBytes);
                    }
                }
            }
        }
        return wb;
    }
}
