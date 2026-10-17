using System;
using System.IO;
using System.Threading.Tasks;
using LibHeifSharp;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Windows.Core;

public sealed class LibHeifDecoder : IHeifDecoder
{
    public bool IsSupported => true;

    /// <summary>libheif 不应用 EXIF 方向,BitmapLoader 负责旋转。</summary>
    public bool OutputPreRotated => false;

    /// <summary>
    /// 解码 HEIF。maxSize = null 全分辨率;非空时代理(libheif 无子采样解码能力,
    /// P1-M4 口径:全解码 + Skia Mitchell 精缩——画质红线禁止最近邻)。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        return await HeifLoader.RunCpuDecodeAsync(async () =>
        {
            await using var stream = await file.OpenReadAsync();
            var full = await LoadBitmapFromStreamAsync(stream);
            if (full == null || !maxSize.HasValue) return full;

            var scaled = SkiaImageCodec.ScaleToLongEdge(full, maxSize.Value);
            if (scaled == null) return full; // null = 不需缩放(源已小于目标)或缩放失败,用全图保证正确性
            full.Release();
            return scaled;
        });
    }

    /// <summary>
    /// 从流中加载 HEIF 图片(内部实现辅助方法)
    /// </summary>
    private async Task<DecodedImage?> LoadBitmapFromStreamAsync(Stream stream)
    {
        try
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var data = ms.ToArray();

            return await Task.Run(() =>
            {
                try
                {
                    using var context = new HeifContext(data);

                    using var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null) return null;

                    using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                    if (image == null) return null;

                    return ConvertHeifImageToBitmap(image);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to decode HEIF image: " + ex.Message);
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load HEIF from stream: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 将 HeifImage 转换为已解码图像
    /// </summary>
    private static DecodedImage? ConvertHeifImageToBitmap(HeifImage heifImage)
    {
        try
        {
            var reportedWidth = (int)heifImage.Width;
            var reportedHeight = (int)heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            if (stride <= 0) return null;

            const int bytesPerPixel = 3;
            
            // 计算实际的图像尺寸
            var pixelsPerRow = stride / bytesPerPixel;
            var actualWidth = pixelsPerRow;
            var actualHeight = reportedHeight;
            
            // 对于竖拍照片，如果stride表明每行像素数少于报告的宽度，
            // 说明图像实际上是竖拍的，需要交换宽高
            if (pixelsPerRow < reportedWidth && pixelsPerRow == reportedHeight)
            {
                actualWidth = reportedHeight;
                actualHeight = reportedWidth;
            }
            else if (pixelsPerRow == reportedWidth)
            {
                actualWidth = reportedWidth;
                actualHeight = reportedHeight;
            }
            else
            {
                Console.WriteLine("Unusual dimensions: reported=" + reportedWidth + "x" + reportedHeight + ", stride=" + stride + ", pixels per row=" + pixelsPerRow);
            }

            if (stride < bytesPerPixel)
            {
                Console.WriteLine($"Stride too small: {stride}, needs at least {bytesPerPixel} bytes per pixel");
                return null;
            }

            // 使用实际尺寸创建BGRA数据(缓冲池租用,P1-M3)
            var bgraData = DecodedImage.RentPixels(actualWidth * actualHeight * 4);

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null)
                {
                    ImageBufferPool.Return(bgraData);
                    return null;
                }

                for (int y = 0; y < actualHeight; y++)
                {
                    var sourceRowPtr = sourcePtr + (y * stride);
                    var destRowOffset = y * actualWidth * 4;

                    for (int x = 0; x < actualWidth; x++)
                    {
                        var srcPixelOffset = x * bytesPerPixel;
                        var destPixelOffset = destRowOffset + (x * 4);

                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2];       // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1];   // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];       // R
                        bgraData[destPixelOffset + 3] = 255;    // A
                    }
                }
            }

            return CreateBitmapFromBgraData(bgraData, actualWidth, actualHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to convert HEIF image to bitmap: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从 BGRA 数据创建已解码图像
    /// </summary>
    private static DecodedImage? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = (long)width * height * 4;
            if (bgraData.Length < expected) return null;
            // 缓冲为池化租用(调用方经 RentPixels 取得),随图像租约归零归还池
            return DecodedImage.CreatePooled(bgraData, width, height, width * 4, PixelEncoding.Bgra32);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create image from BGRA data: " + ex.Message);
            return null;
        }
    }
}
