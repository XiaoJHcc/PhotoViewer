using System;
using System.IO;
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
    /// 解码缩略图图像
    /// </summary>
    private static Bitmap? DecodeThumbnailImage(HeifImageHandle thumbnailHandle, int maxSize)
    {
        try
        {
            var thumbnail = thumbnailHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (thumbnail == null) return null;
            
            using (thumbnail)
            {
                var bitmap = ConvertHeifImageToBitmap(thumbnail);
                if (bitmap == null) return null;
                
                // 如果缩略图过大，进一步缩放
                if (bitmap.PixelSize.Width > maxSize || bitmap.PixelSize.Height > maxSize)
                {
                    var finalScale = Math.Min((double)maxSize / bitmap.PixelSize.Width, 
                                            (double)maxSize / bitmap.PixelSize.Height);
                    var finalSize = new PixelSize(
                        Math.Max(1, (int)(bitmap.PixelSize.Width * finalScale)),
                        Math.Max(1, (int)(bitmap.PixelSize.Height * finalScale))
                    );
                    
                    var scaledBitmap = bitmap.CreateScaledBitmap(finalSize);
                    bitmap.Dispose();
                    return scaledBitmap;
                }
                
                return bitmap;
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
            // 解码为缩略图尺寸
            var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (image == null) return null;
            
            using (image)
            {
                var bitmap = ConvertHeifImageToBitmap(image);
                if (bitmap == null) return null;
                
                // 如果需要进一步缩放
                if (bitmap.PixelSize.Width > maxSize || bitmap.PixelSize.Height > maxSize)
                {
                    var finalScale = Math.Min((double)maxSize / bitmap.PixelSize.Width, 
                                            (double)maxSize / bitmap.PixelSize.Height);
                    var finalSize = new PixelSize(
                        Math.Max(1, (int)(bitmap.PixelSize.Width * finalScale)),
                        Math.Max(1, (int)(bitmap.PixelSize.Height * finalScale))
                    );
                    
                    var scaledBitmap = bitmap.CreateScaledBitmap(finalSize);
                    bitmap.Dispose();
                    return scaledBitmap;
                }
                
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate HEIF thumbnail: {ex.Message}");
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
            
            // 获取像素数据 - 使用正确的属性访问
            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;
            
            // 从 Scan0 指针获取像素数据
            var stride = plane.Stride;
            var dataSize = stride * height;
            var rgbData = new byte[dataSize];
            
            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                for (int i = 0; i < dataSize; i++)
                {
                    rgbData[i] = sourcePtr[i];
                }
            }
            
            // 转换为 BGRA 格式
            var bgraData = ConvertRgbToBgra(rgbData, width, height, stride);
            
            // 创建 Avalonia Bitmap
            var pixelSize = new PixelSize(width, height);
            var bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
            
            using (var lockedBitmap = bitmap.Lock())
            {
                var destStride = lockedBitmap.RowBytes;
                var destPtr = lockedBitmap.Address;
                
                // 复制像素数据
                for (int y = 0; y < height; y++)
                {
                    var srcOffset = y * width * 4;
                    var destOffset = y * destStride;
                    
                    unsafe
                    {
                        var src = bgraData.AsSpan(srcOffset, width * 4);
                        var dest = new Span<byte>((byte*)destPtr + destOffset, width * 4);
                        src.CopyTo(dest);
                    }
                }
            }
            
            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert HEIF image to bitmap: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 将 RGB 数据转换为 BGRA 数据
    /// </summary>
    private static byte[] ConvertRgbToBgra(byte[] rgbData, int width, int height, int stride)
    {
        var bgraData = new byte[width * height * 4];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var srcIndex = y * stride + x * 3;
                var destIndex = (y * width + x) * 4;
                
                if (srcIndex + 2 < rgbData.Length)
                {
                    // RGB -> BGRA
                    bgraData[destIndex] = rgbData[srcIndex + 2];     // B
                    bgraData[destIndex + 1] = rgbData[srcIndex + 1]; // G
                    bgraData[destIndex + 2] = rgbData[srcIndex];     // R
                    bgraData[destIndex + 3] = 255;                   // A (不透明)
                }
            }
        }
        
        return bgraData;
    }
    
    /// <summary>
    /// 获取 HEIF 图片信息（不解码完整图片）
    /// </summary>
    // [Obsolete("Obsolete")]
    // public static async Task<(int width, int height)?> GetHeifImageInfoAsync(IStorageFile file)
    // {
    //     try
    //     {
    //         await using var stream = await file.OpenReadAsync();
    //         
    //         // 将流数据读取到字节数组
    //         using var memoryStream = new MemoryStream();
    //         await stream.CopyToAsync(memoryStream);
    //         var data = memoryStream.ToArray();
    //         
    //         return await Task.Run(() =>
    //         {
    //             try
    //             {
    //                 using var context = new HeifContext();
    //                 context.ReadFromMemory(data);
    //                 
    //                 var imageHandle = context.GetPrimaryImageHandle();
    //                 if (imageHandle == null) return null;
    //                 
    //                 using (imageHandle)
    //                 {
    //                     var width = imageHandle.Width;
    //                     var height = imageHandle.Height;
    //                     return (width, height);
    //                 }
    //             }
    //             catch (Exception ex)
    //             {
    //                 Console.WriteLine($"Failed to get HEIF image info: {ex.Message}");
    //                 return null;
    //             }
    //         });
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"Failed to get HEIF image info ({file.Name}): {ex.Message}");
    //         return null;
    //     }
    // }
}
