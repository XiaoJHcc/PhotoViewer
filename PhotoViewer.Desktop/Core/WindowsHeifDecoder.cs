using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LibHeifSharp;
using PhotoViewer.Core;

namespace PhotoViewer.Desktop.Core;

public sealed class WindowsHeifDecoder : IHeifDecoder
{
    public bool IsSupported => true;

    /// <summary>
    /// 异步加载 HEIF 图片
    /// </summary>
    public async Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        return await LoadBitmapFromStreamAsync(stream);
    }

    /// <summary>
    /// 异步加载 HEIF 缩略图
    /// </summary>
    public async Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
    {
        await using var stream = await file.OpenReadAsync();
        return await LoadThumbnailFromStreamAsync(stream, maxSize);
    }

    /// <summary>
    /// 从流中加载 HEIF 图片为 Bitmap
    /// </summary>
    [Obsolete("Obsolete")]
    public async Task<Bitmap?> LoadBitmapFromStreamAsync(Stream stream)
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
                    using var context = new HeifContext();
                    context.ReadFromMemory(data);

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
    /// 从流中生成 HEIF 缩略图
    /// </summary>
    [Obsolete("Obsolete")]
    public async Task<Bitmap?> LoadThumbnailFromStreamAsync(Stream stream, int maxSize)
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
                    using var context = new HeifContext();
                    context.ReadFromMemory(data);

                    using var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null) return null;

                    // 优先尝试嵌入式缩略图
                    var thumbnailIds = imageHandle.GetThumbnailImageIds();
                    if (thumbnailIds.Count > 0)
                    {
                        try
                        {
                            HeifItemId? selectedId = null;
                            var bestLongSide = int.MaxValue;

                            foreach (var id in thumbnailIds)
                            {
                                using var th = imageHandle.GetThumbnailImage(id);
                                if (th == null) continue;

                                var w = (int)th.Width;
                                var h = (int)th.Height;
                                var longSide = Math.Max(w, h);

                                if (longSide >= maxSize && longSide < bestLongSide)
                                {
                                    selectedId = id;
                                    bestLongSide = longSide;
                                }
                            }

                            if (selectedId.HasValue)
                            {
                                using var thHandle = imageHandle.GetThumbnailImage(selectedId.Value);
                                if (thHandle != null)
                                {
                                    var bmp = DecodeThumbnailImage(thHandle, maxSize);
                                    if (bmp != null) return bmp;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Failed to decode embedded HEIF thumbnail: " + ex.Message);
                        }
                    }

                    // 无合适缩略图则从主图生成
                    return GenerateHeifThumbnail(imageHandle, maxSize);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load HEIF thumbnail: " + ex.Message);
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load HEIF thumbnail from stream: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 解码缩略图图像
    /// </summary>
    private static Bitmap? DecodeThumbnailImage(HeifImageHandle thumbnailHandle, int maxSize)
    {
        try
        {
            using var thumbnail = thumbnailHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (thumbnail == null) return null;
            return ConvertHeifImageToBitmapWithResize(thumbnail, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to decode thumbnail: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从主图像生成缩略图
    /// </summary>
    private static Bitmap? GenerateHeifThumbnail(HeifImageHandle imageHandle, int maxSize)
    {
        try
        {
            using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (image == null) return null;
            return ConvertHeifImageToBitmapWithResize(image, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to generate HEIF thumbnail: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 将 HeifImage 转换为 Avalonia Bitmap
    /// </summary>
    private static Bitmap? ConvertHeifImageToBitmap(HeifImage heifImage)
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

            // 使用实际尺寸创建BGRA数据
            var bgraData = new byte[actualWidth * actualHeight * 4];

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null) return null;

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
    /// 将 HeifImage 转换为指定尺寸的 Avalonia Bitmap（采样缩放）
    /// </summary>
    private static Bitmap? ConvertHeifImageToBitmapWithResize(HeifImage heifImage, int maxSize)
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
            
            // 每行像素（含对齐/padding）
            var pixelsPerRow = stride / bytesPerPixel;

            // 只要行内有效像素 < 报告的宽度，即认为是竖拍
            var swapPortrait = pixelsPerRow < reportedWidth;

            int actualOriginalWidth = swapPortrait ? reportedHeight : reportedWidth;
            int actualOriginalHeight = swapPortrait ? reportedWidth : reportedHeight;

            if (stride < bytesPerPixel) return null;

            var scale = Math.Min((double)maxSize / actualOriginalWidth, (double)maxSize / actualOriginalHeight);
            if (scale >= 1.0) scale = 1.0;

            var targetWidth = Math.Max(1, (int)(actualOriginalWidth * scale));
            var targetHeight = Math.Max(1, (int)(actualOriginalHeight * scale));

            var bgraData = new byte[targetWidth * targetHeight * 4];

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null) return null;

                // 有效行内像素（不含对齐填充）
                var validRowPixels = actualOriginalWidth;
                // padded 行像素（由 stride 得出）
                var paddedRowPixels = pixelsPerRow;

                for (int ty = 0; ty < targetHeight; ty++)
                {
                    for (int tx = 0; tx < targetWidth; tx++)
                    {
                        var sx = (int)(tx / scale);
                        var sy = (int)(ty / scale);

                        // 限定在实际有效范围内（避免读取对齐 padding 区域）
                        sx = Math.Min(sx, validRowPixels - 1);
                        sy = Math.Min(sy, actualOriginalHeight - 1);
                        sx = Math.Min(sx, paddedRowPixels - 1);

                        var sourceRowPtr = sourcePtr + (sy * stride);
                        var srcPixelOffset = sx * bytesPerPixel;
                        var destPixelOffset = (ty * targetWidth + tx) * 4;

                        // 保护：不越过本行 stride
                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        // RGB -> BGRA
                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2];
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1];
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];
                        bgraData[destPixelOffset + 3] = 255;
                    }
                }
            }

            return CreateBitmapFromBgraData(bgraData, targetWidth, targetHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to convert HEIF image to bitmap with resize: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从 BGRA 数据创建 Bitmap
    /// </summary>
    private static Bitmap? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = width * height * 4;
            if (bgraData.Length < expected) return null;

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);

            using (var locked = bitmap.Lock())
            {
                var destStride = locked.RowBytes;
                var destPtr = locked.Address;
                var srcStride = width * 4;

                unsafe
                {
                    var destBytePtr = (byte*)destPtr;

                    for (int y = 0; y < height; y++)
                    {
                        var srcOffset = y * srcStride;
                        var destOffset = y * destStride;
                        var copySize = Math.Min(srcStride, destStride);

                        if (srcOffset + copySize > bgraData.Length) break;

                        fixed (byte* srcPtr = &bgraData[srcOffset])
                        {
                            Buffer.MemoryCopy(srcPtr, destBytePtr + destOffset, copySize, copySize);
                        }
                    }
                }
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create bitmap from BGRA data: " + ex.Message);
            return null;
        }
    }
}