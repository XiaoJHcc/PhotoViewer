using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LibHeifSharp;

namespace PhotoViewer.Core;

/// <summary>
/// HEIF 格式图片加载器（静态工具类）
/// </summary>
public static class HeifLoader
{
    /// <summary>
    /// 检查文件是否为 HEIF 格式
    /// </summary>
    public static bool IsHeifFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".heif" or ".heic" or ".avif" or ".hif";
    }

    /// <summary>
    /// 检查文件是否为 HEIF 格式
    /// </summary>
    public static bool IsHeifFile(IStorageFile file)
    {
        return IsHeifFile(file.Path.LocalPath);
    }

    /// <summary>
    /// 异步加载 HEIF 图片为 Bitmap
    /// </summary>
    public static async Task<Bitmap?> LoadHeifBitmapAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            return await LoadHeifBitmapFromStreamAsync(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load HEIF image ({file.Name}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从流中加载 HEIF 图片为 Bitmap
    /// </summary>
    [Obsolete("Obsolete")]
    public static async Task<Bitmap?> LoadHeifBitmapFromStreamAsync(Stream stream)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var data = memoryStream.ToArray();

            return await Task.Run(() =>
            {
                try
                {
                    using var context = new HeifContext();
                    context.ReadFromMemory(data);

                    var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null) return null;

                    using (imageHandle)
                    {
                        var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                        if (image == null) return null;

                        using (image)
                        {
                            return ConvertHeifImageToBitmap(image);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to decode HEIF image: {ex.Message}");
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load HEIF from stream: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 异步生成 HEIF 缩略图
    /// </summary>
    public static async Task<Bitmap?> LoadHeifThumbnailAsync(IStorageFile file, int maxSize = 120)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            return await LoadHeifThumbnailFromStreamAsync(stream, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load HEIF thumbnail ({file.Name}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从流中生成 HEIF 缩略图
    /// </summary>
    [Obsolete("Obsolete")]
    public static async Task<Bitmap?> LoadHeifThumbnailFromStreamAsync(Stream stream, int maxSize = 120)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var data = memoryStream.ToArray();

            return await Task.Run(() =>
            {
                try
                {
                    using var context = new HeifContext();
                    context.ReadFromMemory(data);

                    var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null) return null;

                    using (imageHandle)
                    {
                        // 尝试获取嵌入式缩略图（优先使用长边>=maxSize的最小缩略图）
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

                                    var w = th.Width;
                                    var h = th.Height;
                                    var longSide = Math.Max(w, h);

                                    // 只选择长边>=maxSize，且尽可能小的缩略图
                                    if (longSide >= maxSize && longSide < bestLongSide)
                                    {
                                        selectedId = id;
                                        bestLongSide = longSide;
                                    }
                                }

                                if (selectedId.HasValue)
                                {
                                    using var thumbnailHandle = imageHandle.GetThumbnailImage(selectedId.Value);
                                    if (thumbnailHandle != null)
                                    {
                                        var thumbnail = DecodeThumbnailImage(thumbnailHandle, maxSize);
                                        if (thumbnail != null) return thumbnail;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to decode embedded HEIF thumbnail: {ex.Message}");
                            }
                        }

                        // 如果没有合适的嵌入式缩略图，生成缩略图
                        return GenerateHeifThumbnail(imageHandle, maxSize);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load HEIF thumbnail: {ex.Message}");
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load HEIF thumbnail from stream: {ex.Message}");
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
            var reportedWidth = heifImage.Width;
            var reportedHeight = heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = plane.Stride;
            if (stride <= 0) return null;

            var bytesPerPixel = 3;
            
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
                // 正常横拍照片
                actualWidth = reportedWidth;
                actualHeight = reportedHeight;
            }
            else
            {
                Console.WriteLine($"Unusual dimensions: reported={reportedWidth}x{reportedHeight}, stride={stride}, pixels per row={pixelsPerRow}");
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

                        // 确保不会越界访问源数据
                        if (srcPixelOffset + 2 >= stride)
                        {
                            Console.WriteLine($"Stopping at pixel x={x}, y={y} due to stride limit");
                            break;
                        }

                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset]; // R
                        bgraData[destPixelOffset + 3] = 255; // A
                    }
                }
            }

            return CreateBitmapFromBgraDataImproved(bgraData, actualWidth, actualHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert HEIF image to bitmap: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 BGRA 数据创建 Bitmap
    /// </summary>
    private static Bitmap? CreateBitmapFromBgraDataImproved(byte[] bgraData, int width, int height)
    {
        try
        {
            var pixelSize = new PixelSize(width, height);
            var expectedDataSize = width * height * 4;
            if (bgraData.Length < expectedDataSize) return null;

            var bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);

            using (var lockedBitmap = bitmap.Lock())
            {
                var destStride = lockedBitmap.RowBytes;
                var destPtr = lockedBitmap.Address;
                var sourceStride = width * 4;

                unsafe
                {
                    var destBytePtr = (byte*)destPtr;

                    for (int y = 0; y < height; y++)
                    {
                        var srcOffset = y * sourceStride;
                        var destOffset = y * destStride;
                        var copySize = Math.Min(sourceStride, destStride);

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
            Console.WriteLine($"Failed to create bitmap from BGRA data: {ex.Message}");
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
            HeifImage? thumbnail = null;
            try
            {
                thumbnail = thumbnailHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                if (thumbnail == null) return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to decode thumbnail: {ex.Message}");
                return null;
            }

            using (thumbnail)
            {
                // 直接在转换时指定目标尺寸，避免后续缩放
                return ConvertHeifImageToBitmapWithResize(thumbnail, maxSize);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to decode HEIF thumbnail image: {ex.Message}");
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
            HeifImage? image = null;
            try
            {
                image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                if (image == null) return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to decode main image: {ex.Message}");
                return null;
            }

            using (image)
            {
                // 直接在转换时指定目标尺寸，避免后续缩放
                return ConvertHeifImageToBitmapWithResize(image, maxSize);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate HEIF thumbnail: {ex.Message}");
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
            var reportedWidth = heifImage.Width;
            var reportedHeight = heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = plane.Stride;
            if (stride <= 0) return null;

            var bytesPerPixel = 3;

            // 每行像素（含对齐/padding）
            var pixelsPerRow = stride / bytesPerPixel;

            // 放宽竖拍判断：只要行内有效像素 < 报告的宽度，即认为是竖拍
            var swapPortrait = pixelsPerRow < reportedWidth;

            int actualOriginalWidth;
            int actualOriginalHeight;

            if (swapPortrait)
            {
                actualOriginalWidth = reportedHeight;
                actualOriginalHeight = reportedWidth; 
            }
            else
            {
                actualOriginalWidth = reportedWidth;
                actualOriginalHeight = reportedHeight;
            }

            if (stride < bytesPerPixel)
            {
                return null;
            }

            // 计算缩放（基于实际宽高）
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

                for (int targetY = 0; targetY < targetHeight; targetY++)
                {
                    for (int targetX = 0; targetX < targetWidth; targetX++)
                    {
                        var sourceX = (int)(targetX / scale);
                        var sourceY = (int)(targetY / scale);

                        // 限定在实际有效范围内（避免读取对齐 padding 区域）
                        sourceX = Math.Min(sourceX, validRowPixels - 1);
                        sourceY = Math.Min(sourceY, actualOriginalHeight - 1);

                        // 同时也不超过 padded 行像素，避免 stride 越界
                        sourceX = Math.Min(sourceX, paddedRowPixels - 1);

                        var sourceRowPtr = sourcePtr + (sourceY * stride);
                        var srcPixelOffset = sourceX * bytesPerPixel;
                        var destPixelOffset = (targetY * targetWidth + targetX) * 4;

                        // 保护：不越过本行 stride
                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        // RGB -> BGRA
                        bgraData[destPixelOffset]     = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];     // R
                        bgraData[destPixelOffset + 3] = 255;                               // A
                    }
                }
            }

            return CreateBitmapFromBgraDataImproved(bgraData, targetWidth, targetHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert HEIF image to bitmap with resize: {ex.Message}");
            return null;
        }
    }
}
