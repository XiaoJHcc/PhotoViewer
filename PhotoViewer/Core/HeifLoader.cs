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
                        // 尝试获取嵌入式缩略图
                        var thumbnailIds = imageHandle.GetThumbnailImageIds();
                        if (thumbnailIds.Count > 0)
                        {
                            try
                            {
                                var thumbnailHandle = imageHandle.GetThumbnailImage(thumbnailIds[0]);
                                if (thumbnailHandle != null)
                                {
                                    using (thumbnailHandle)
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

                        // 如果没有嵌入式缩略图，生成缩略图
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
            var width = heifImage.Width;
            var height = heifImage.Height;
            if (width <= 0 || height <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = plane.Stride;
            if (stride <= 0) return null;

            var bytesPerPixel = 3;
            var expectedRowSize = width * bytesPerPixel;
            if (stride < expectedRowSize) return null;

            var bgraData = new byte[width * height * 4];

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null) return null;

                for (int y = 0; y < height; y++)
                {
                    var sourceRowPtr = sourcePtr + (y * stride);
                    var destRowOffset = y * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        var srcPixelOffset = x * bytesPerPixel;
                        var destPixelOffset = destRowOffset + (x * 4);

                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset]; // R
                        bgraData[destPixelOffset + 3] = 255; // A
                    }
                }
            }

            return CreateBitmapFromBgraDataImproved(bgraData, width, height);
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
            var originalWidth = heifImage.Width;
            var originalHeight = heifImage.Height;
            if (originalWidth <= 0 || originalHeight <= 0) return null;

            // 计算目标尺寸
            var scale = Math.Min((double)maxSize / originalWidth, (double)maxSize / originalHeight);
            var targetWidth = Math.Max(1, (int)(originalWidth * scale));
            var targetHeight = Math.Max(1, (int)(originalHeight * scale));

            // 如果不需要缩放，使用原始转换方法
            if (scale >= 1.0)
            {
                return ConvertHeifImageToBitmap(heifImage);
            }

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = plane.Stride;
            if (stride <= 0) return null;

            var bytesPerPixel = 3;
            var expectedRowSize = originalWidth * bytesPerPixel;
            if (stride < expectedRowSize) return null;

            // 直接创建目标尺寸的 BGRA 数据
            var bgraData = new byte[targetWidth * targetHeight * 4];

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null) return null;

                // 使用采样方式直接生成目标尺寸
                for (int targetY = 0; targetY < targetHeight; targetY++)
                {
                    for (int targetX = 0; targetX < targetWidth; targetX++)
                    {
                        // 计算对应的源像素位置
                        var sourceX = (int)(targetX / scale);
                        var sourceY = (int)(targetY / scale);

                        // 确保不超出边界
                        sourceX = Math.Min(sourceX, originalWidth - 1);
                        sourceY = Math.Min(sourceY, originalHeight - 1);

                        var sourceRowPtr = sourcePtr + (sourceY * stride);
                        var srcPixelOffset = sourceX * bytesPerPixel;
                        var destPixelOffset = (targetY * targetWidth + targetX) * 4;

                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        // RGB -> BGRA 转换
                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset]; // R
                        bgraData[destPixelOffset + 3] = 255; // A
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
