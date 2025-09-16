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
            // 将流数据读取到字节数组
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var data = memoryStream.ToArray();
            
            return await Task.Run(() =>
            {
                try
                {
                    using var context = new HeifContext();
                    
                    // 从字节数据读取 HEIF 文件
                    context.ReadFromMemory(data);
                    
                    // 获取主图像句柄
                    var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null)
                    {
                        Console.WriteLine("Failed to get primary image handle from HEIF");
                        return null;
                    }
                    
                    using (imageHandle)
                    {
                        // 解码图像为 RGB 格式 - 使用正确的方法名和枚举
                        var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                        if (image == null)
                        {
                            Console.WriteLine("Failed to decode HEIF image");
                            return null;
                        }
                        
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
            // 将流数据读取到字节数组
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var data = memoryStream.ToArray();
            
            return await Task.Run(() =>
            {
                try
                {
                    using var context = new HeifContext();
                    
                    // 从字节数据读取 HEIF 文件
                    context.ReadFromMemory(data);
                    
                    // 获取主图像句柄
                    var imageHandle = context.GetPrimaryImageHandle();
                    if (imageHandle == null)
                    {
                        Console.WriteLine("Failed to get primary image handle from HEIF for thumbnail");
                        return null;
                    }
                    
                    using (imageHandle)
                    {
                        // 先尝试获取嵌入式缩略图
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
                                        if (thumbnail != null)
                                        {
                                            return thumbnail;
                                        }
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
            
            if (width <= 0 || height <= 0) 
            {
                Console.WriteLine($"Invalid HEIF image dimensions: {width}x{height}");
                return null;
            }
            
            Console.WriteLine($"Converting HEIF image: {width}x{height}, colorspace: {heifImage.Colorspace}");
            
            // 获取像素数据
            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null)
            {
                Console.WriteLine("Failed to get interleaved plane from HEIF image");
                return null;
            }
            
            var stride = plane.Stride;
            if (stride <= 0)
            {
                Console.WriteLine($"Invalid stride value: {stride}");
                return null;
            }
            
            Console.WriteLine($"Plane info: stride={stride}, scan0={plane.Scan0}");
            
            // 对于 RGB24 格式，每像素 3 字节
            var bytesPerPixel = 3;
            var expectedRowSize = width * bytesPerPixel;
            
            // 验证 stride 是否合理
            if (stride < expectedRowSize)
            {
                Console.WriteLine($"Stride {stride} is less than expected row size {expectedRowSize}");
                return null;
            }
            
            // 计算实际需要的数据大小
            var totalDataSize = stride * height;
            Console.WriteLine($"Expected data size: {totalDataSize} bytes");
            
            // 创建 BGRA 数据数组
            var bgraData = new byte[width * height * 4];
            
            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null)
                {
                    Console.WriteLine("Invalid source pointer from HEIF plane");
                    return null;
                }
                
                // 逐行转换 RGB 到 BGRA
                for (int y = 0; y < height; y++)
                {
                    var sourceRowPtr = sourcePtr + (y * stride);
                    var destRowOffset = y * width * 4;
                    
                    for (int x = 0; x < width; x++)
                    {
                        var srcPixelOffset = x * bytesPerPixel;
                        var destPixelOffset = destRowOffset + (x * 4);
                        
                        // 边界检查
                        if (destPixelOffset + 3 >= bgraData.Length)
                        {
                            Console.WriteLine($"Destination buffer overflow at pixel ({x}, {y})");
                            break;
                        }
                        
                        // RGB -> BGRA 转换
                        bgraData[destPixelOffset] = sourceRowPtr[srcPixelOffset + 2];     // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];     // R
                        bgraData[destPixelOffset + 3] = 255;                              // A (不透明)
                    }
                }
            }
            
            // 创建 Avalonia Bitmap - 确保数据完整复制
            var bitmap = CreateBitmapFromBgraDataImproved(bgraData, width, height);
            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert HEIF image to bitmap: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
    
    /// <summary>
    /// 从 BGRA 数据创建 Bitmap - 改进版本
    /// </summary>
    private static Bitmap? CreateBitmapFromBgraDataImproved(byte[] bgraData, int width, int height)
    {
        try
        {
            var pixelSize = new PixelSize(width, height);
            
            // 验证数据大小
            var expectedDataSize = width * height * 4;
            if (bgraData.Length < expectedDataSize)
            {
                Console.WriteLine($"Insufficient BGRA data: expected {expectedDataSize}, got {bgraData.Length}");
                return null;
            }
            
            var bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
            
            using (var lockedBitmap = bitmap.Lock())
            {
                var destStride = lockedBitmap.RowBytes;
                var destPtr = lockedBitmap.Address;
                var sourceStride = width * 4; // BGRA 每像素 4 字节
                
                Console.WriteLine($"Bitmap creation: destStride={destStride}, sourceStride={sourceStride}");
                
                unsafe
                {
                    var destBytePtr = (byte*)destPtr;
                    
                    // 逐行复制数据
                    for (int y = 0; y < height; y++)
                    {
                        var srcOffset = y * sourceStride;
                        var destOffset = y * destStride;
                        var copySize = Math.Min(sourceStride, destStride);
                        
                        // 边界检查
                        if (srcOffset + copySize > bgraData.Length)
                        {
                            Console.WriteLine($"Source data insufficient for row {y}");
                            break;
                        }
                        
                        // 使用固定指针进行内存复制
                        fixed (byte* srcPtr = &bgraData[srcOffset])
                        {
                            Buffer.MemoryCopy(srcPtr, destBytePtr + destOffset, copySize, copySize);
                        }
                    }
                }
            }
            
            Console.WriteLine($"Successfully created bitmap: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}");
            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create bitmap from BGRA data: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
    
    /// <summary>
    /// 解码缩略图图像 - 修复版本
    /// </summary>
    private static Bitmap? DecodeThumbnailImage(HeifImageHandle thumbnailHandle, int maxSize)
    {
        try
        {
            Console.WriteLine("Attempting to decode embedded HEIF thumbnail");
            
            // 只尝试 RGB24 格式，因为日志显示这个格式是成功的
            HeifImage? thumbnail = null;
            try
            {
                thumbnail = thumbnailHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                if (thumbnail != null)
                {
                    Console.WriteLine($"Successfully decoded thumbnail: {thumbnail.Width}x{thumbnail.Height}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to decode thumbnail with Rgb/InterleavedRgb24: {ex.Message}");
                return null;
            }
            
            if (thumbnail == null)
            {
                Console.WriteLine("Failed to decode thumbnail - thumbnail is null");
                return null;
            }
            
            using (thumbnail)
            {
                var bitmap = ConvertHeifImageToBitmap(thumbnail);
                if (bitmap == null) 
                {
                    Console.WriteLine("ConvertHeifImageToBitmap returned null for thumbnail");
                    return null;
                }
                
                // 检查缩略图是否需要缩放
                if (bitmap.PixelSize.Width > maxSize || bitmap.PixelSize.Height > maxSize)
                {
                    try
                    {
                        var finalScale = Math.Min((double)maxSize / bitmap.PixelSize.Width, 
                                                (double)maxSize / bitmap.PixelSize.Height);
                        var finalSize = new PixelSize(
                            Math.Max(1, (int)(bitmap.PixelSize.Width * finalScale)),
                            Math.Max(1, (int)(bitmap.PixelSize.Height * finalScale))
                        );
                        
                        Console.WriteLine($"Scaling thumbnail from {bitmap.PixelSize} to {finalSize}");
                        
                        // 创建新的缩放位图前，验证原位图的有效性
                        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
                        {
                            Console.WriteLine("Original bitmap has invalid dimensions for scaling");
                            bitmap.Dispose();
                            return null;
                        }
                        
                        var scaledBitmap = bitmap.CreateScaledBitmap(finalSize);
                        bitmap.Dispose();
                        return scaledBitmap;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to scale thumbnail: {ex.Message}");
                        // 缩放失败时返回原图
                        return bitmap;
                    }
                }
                
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to decode HEIF thumbnail image: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
    
    /// <summary>
    /// 从主图像生成缩略图 - 修复版本
    /// </summary>
    private static Bitmap? GenerateHeifThumbnail(HeifImageHandle imageHandle, int maxSize)
    {
        try
        {
            Console.WriteLine("Generating HEIF thumbnail from main image");
            
            // 只尝试 RGB24 格式
            HeifImage? image = null;
            try
            {
                image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                if (image != null)
                {
                    Console.WriteLine($"Successfully decoded main image: {image.Width}x{image.Height}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to decode main image with Rgb/InterleavedRgb24: {ex.Message}");
                return null;
            }
            
            if (image == null)
            {
                Console.WriteLine("Failed to decode main image - image is null");
                return null;
            }
            
            using (image)
            {
                var bitmap = ConvertHeifImageToBitmap(image);
                if (bitmap == null) 
                {
                    Console.WriteLine("ConvertHeifImageToBitmap returned null for main image");
                    return null;
                }
                
                // 检查是否需要缩放
                if (bitmap.PixelSize.Width > maxSize || bitmap.PixelSize.Height > maxSize)
                {
                    try
                    {
                        var finalScale = Math.Min((double)maxSize / bitmap.PixelSize.Width, 
                                                (double)maxSize / bitmap.PixelSize.Height);
                        var finalSize = new PixelSize(
                            Math.Max(1, (int)(bitmap.PixelSize.Width * finalScale)),
                            Math.Max(1, (int)(bitmap.PixelSize.Height * finalScale))
                        );
                        
                        Console.WriteLine($"Scaling main image from {bitmap.PixelSize} to {finalSize}");
                        
                        // 创建新的缩放位图前，验证原位图的有效性
                        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
                        {
                            Console.WriteLine("Original bitmap has invalid dimensions for scaling");
                            bitmap.Dispose();
                            return null;
                        }
                        
                        var scaledBitmap = bitmap.CreateScaledBitmap(finalSize);
                        bitmap.Dispose();
                        return scaledBitmap;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to scale main image: {ex.Message}");
                        // 缩放失败时返回原图
                        return bitmap;
                    }
                }
                
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate HEIF thumbnail: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
}
