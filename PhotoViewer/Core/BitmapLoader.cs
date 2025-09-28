using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PhotoViewer.Core;

/// <summary>
/// 缓存项
/// </summary>
public class BitmapCacheItem
{
    public Bitmap Bitmap { get; set; }
    public long Size { get; set; }
    public DateTime LastAccessTime { get; set; }
    public string FilePath { get; set; }

    public BitmapCacheItem(Bitmap bitmap, string filePath)
    {
        Bitmap = bitmap;
        FilePath = filePath;
        Size = EstimateBitmapSize(bitmap);
        LastAccessTime = DateTime.Now;
    }

    private static long EstimateBitmapSize(Bitmap bitmap)
    {
        // 估算位图内存大小（像素数 × 4字节/像素）
        return (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
    }

    public void UpdateAccessTime()
    {
        LastAccessTime = DateTime.Now;
    }
}

/// <summary>
/// 图片缓存服务（静态类）
/// </summary>
public static class BitmapLoader
{
    private static readonly ConcurrentDictionary<string, BitmapCacheItem> _cache = new();
    private static readonly object _cleanupLock = new();
    
    // 缓存配置
    private static int _maxCacheCount = 30; // 修改默认值 30
    private static long _maxCacheSize = 4096L * 1024 * 1024; // 2048 MB
    
    // 缓存状态变化事件
    public static event Action<string, bool>? CacheStatusChanged;

    /// <summary>
    /// 最大缓存图片数量
    /// </summary>
    public static int MaxCacheCount
    {
        get => _maxCacheCount;
        set
        {
            _maxCacheCount = Math.Max(1, value);
            _ = Task.Run(CleanupCache);
        }
    }
    
    /// <summary>
    /// 最大缓存大小（字节）
    /// </summary>
    public static long MaxCacheSize
    {
        get => _maxCacheSize;
        set
        {
            _maxCacheSize = Math.Max(10L * 1024 * 1024, value); // 最小10MB
            _ = Task.Run(CleanupCache);
        }
    }
    
    /// <summary>
    /// 当前缓存数量
    /// </summary>
    public static int CurrentCacheCount => _cache.Count;
    
    /// <summary>
    /// 当前缓存大小
    /// </summary>
    public static long CurrentCacheSize => _cache.Values.Sum(item => item.Size);
    
    /// <summary>
    /// 检查文件是否在缓存中
    /// </summary>
    public static bool IsInCache(string filePath)
    {
        return _cache.ContainsKey(filePath);
    }
    
    /// <summary>
    /// 异步获取图片（带缓存和EXIF旋转）
    /// </summary>
    public static async Task<Bitmap?> GetBitmapAsync(IStorageFile file)
    {
        var filePath = file.Path.LocalPath;
        
        // 检查缓存
        if (_cache.TryGetValue(filePath, out var cachedItem))
        {
            cachedItem.UpdateAccessTime();
            return cachedItem.Bitmap;
        }
        
        try
        {
            // 加载图片
            var bitmap = await LoadBitmapWithExifRotationAsync(file);
            if (bitmap == null) return null;
            
            // 添加到缓存
            var cacheItem = new BitmapCacheItem(bitmap, filePath);
            _cache[filePath] = cacheItem;
            
            // 通知缓存状态变化（在UI线程中触发）
            Dispatcher.UIThread.Post(() => CacheStatusChanged?.Invoke(filePath, true));
            
            // 异步清理缓存
            _ = Task.Run(CleanupCache);
            
            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load image ({file.Name}): {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 加载图片并应用EXIF旋转
    /// </summary>
    private static async Task<Bitmap?> LoadBitmapWithExifRotationAsync(IStorageFile file)
    {
        try
        {
            Bitmap? originalBitmap = null;
            
            // 检查是否为 HEIF 格式
            if (HeifLoader.IsHeifFile(file))
            {
                originalBitmap = await HeifLoader.LoadHeifBitmapAsync(file);
            }
            else
            {
                await using var stream = await file.OpenReadAsync();
                originalBitmap = new Bitmap(stream);
            }
            
            // 验证原始位图是否有效
            if (originalBitmap == null || originalBitmap.PixelSize.Width <= 0 || originalBitmap.PixelSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap loaded from file: {file.Name}");
                originalBitmap?.Dispose();
                return null;
            }
            
            // 获取EXIF方向信息
            var orientation = await GetExifOrientationAsync(file);
            
            // 如果不需要旋转，直接返回原图
            if (orientation == 1)
            {
                return originalBitmap;
            }
            
            // 应用EXIF旋转
            var rotatedBitmap = ApplyExifRotation(originalBitmap, orientation);
            
            // 释放原图内存（只有在旋转成功且返回新位图时才释放）
            if (rotatedBitmap != null && rotatedBitmap != originalBitmap)
            {
                originalBitmap.Dispose();
                return rotatedBitmap;
            }
            else if (rotatedBitmap == null)
            {
                // 旋转失败，返回原图
                Console.WriteLine($"Rotation failed for {file.Name}, returning original bitmap");
                return originalBitmap;
            }
            
            return rotatedBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load and rotate image ({file.Name}): {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 获取EXIF方向值
    /// </summary>
    private static async Task<int> GetExifOrientationAsync(IStorageFile file)
    {
        try
        {
            var exifData = await ExifLoader.LoadExifDataAsync(file);
            return exifData?.OrientationValue ?? 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read EXIF orientation for {file.Name}: {ex.Message}");
            return 1;
        }
    }
    
    /// <summary>
    /// 应用EXIF旋转到位图
    /// </summary>
    private static Bitmap? ApplyExifRotation(Bitmap originalBitmap, int orientation)
    {
        if (originalBitmap == null)
        {
            Console.WriteLine("Cannot apply rotation to null bitmap");
            return null;
        }
        
        try
        {
            return orientation switch
            {
                3 => RotateBitmap(originalBitmap, 180),
                6 => RotateBitmap(originalBitmap, 90),
                8 => RotateBitmap(originalBitmap, 270),
                _ => originalBitmap
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to apply EXIF rotation (orientation: {orientation}): {ex.Message}");
            return originalBitmap; // 旋转失败时返回原图
        }
    }
    
    /// <summary>
    /// 旋转位图
    /// </summary>
    private static Bitmap? RotateBitmap(Bitmap originalBitmap, int degrees)
    {
        if (originalBitmap == null)
        {
            Console.WriteLine("Cannot rotate null bitmap");
            return null;
        }
        
        try
        {
            var originalSize = originalBitmap.PixelSize;
            
            // 验证原始尺寸
            if (originalSize.Width <= 0 || originalSize.Height <= 0)
            {
                Console.WriteLine($"Invalid bitmap size: {originalSize.Width}x{originalSize.Height}");
                return originalBitmap;
            }
            
            var newSize = (degrees % 180 == 0) ? 
                originalSize : 
                new PixelSize(originalSize.Height, originalSize.Width);
            
            // 验证新尺寸
            if (newSize.Width <= 0 || newSize.Height <= 0)
            {
                Console.WriteLine($"Invalid calculated size for rotation: {newSize.Width}x{newSize.Height}");
                return originalBitmap;
            }
            
            // 创建旋转后的位图
            RenderTargetBitmap? renderTarget = null;
            try
            {
                renderTarget = new RenderTargetBitmap(newSize);
                if (renderTarget == null)
                {
                    Console.WriteLine("Failed to create RenderTargetBitmap");
                    return originalBitmap;
                }
                
                using var context = renderTarget.CreateDrawingContext();
                if (context == null)
                {
                    Console.WriteLine("Failed to create DrawingContext");
                    renderTarget.Dispose();
                    return originalBitmap;
                }
                
                // 计算旋转中心
                var centerX = newSize.Width / 2.0;
                var centerY = newSize.Height / 2.0;
                
                // 应用变换矩阵
                var transforms = new List<Matrix>();
                
                // 移动到原点
                transforms.Add(Matrix.CreateTranslation(-centerX, -centerY));
                
                // 旋转
                var rotation = Matrix.CreateRotation(Math.PI * degrees / 180.0);
                transforms.Add(rotation);
                
                // 移回中心
                transforms.Add(Matrix.CreateTranslation(centerX, centerY));
                
                // 组合所有变换
                var combinedTransform = Matrix.Identity;
                foreach (var transform in transforms)
                {
                    combinedTransform *= transform;
                }
                
                // 应用变换
                using (context.PushTransform(combinedTransform))
                {
                    // 计算绘制矩形
                    var destRect = new Rect(0, 0, originalSize.Width, originalSize.Height);
                    if (degrees % 180 != 0)
                    {
                        destRect = new Rect(
                            (newSize.Width - originalSize.Width) / 2.0,
                            (newSize.Height - originalSize.Height) / 2.0,
                            originalSize.Width,
                            originalSize.Height
                        );
                    }
                    
                    // 绘制原图
                    context.DrawImage(originalBitmap, destRect);
                }
                
                return renderTarget;
            }
            catch (Exception ex)
            {
                renderTarget?.Dispose();
                Console.WriteLine($"Failed during bitmap rotation rendering: {ex.Message}");
                return originalBitmap;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to rotate bitmap by {degrees} degrees: {ex.Message}");
            return originalBitmap;
        }
    }
    
    /// <summary>
    /// 清理缓存
    /// </summary>
    private static void CleanupCache()
    {
        lock (_cleanupLock)
        {
            try
            {
                var itemsToRemove = new List<string>();
                
                // 按最后访问时间排序
                var sortedItems = _cache.ToList()
                    .OrderBy(kvp => kvp.Value.LastAccessTime)
                    .ToList();
                
                var currentSize = CurrentCacheSize;
                var currentCount = CurrentCacheCount;
                
                // 清理超出数量限制的项
                if (currentCount > MaxCacheCount)
                {
                    var itemsToRemoveByCount = sortedItems
                        .Take(currentCount - MaxCacheCount)
                        .Select(kvp => kvp.Key);
                    itemsToRemove.AddRange(itemsToRemoveByCount);
                }
                
                // 清理超出大小限制的项
                if (currentSize > MaxCacheSize)
                {
                    var remainingItems = sortedItems
                        .Where(kvp => !itemsToRemove.Contains(kvp.Key))
                        .ToList();
                    
                    long accumulatedSize = 0;
                    var targetSize = MaxCacheSize * 0.8; // 清理到80%容量
                    
                    for (int i = remainingItems.Count - 1; i >= 0; i--)
                    {
                        var item = remainingItems[i];
                        accumulatedSize += item.Value.Size;
                        
                        if (accumulatedSize > targetSize)
                        {
                            // 保留最近访问的项目，移除较旧的
                            for (int j = 0; j < i; j++)
                            {
                                itemsToRemove.Add(remainingItems[j].Key);
                            }
                            break;
                        }
                    }
                }
                
                // 执行清理
                foreach (var key in itemsToRemove.Distinct())
                {
                    if (_cache.TryRemove(key, out var item))
                    {
                        // 通知缓存状态变化（在UI线程中触发）
                        Dispatcher.UIThread.Post(() => CacheStatusChanged?.Invoke(key, false));
                        
                        // 在UI线程中释放位图
                        Dispatcher.UIThread.Post(() => item.Bitmap.Dispose());
                    }
                }
                
                if (itemsToRemove.Count > 0)
                {
                    Console.WriteLine($"Cache cleanup completed: removed {itemsToRemove.Count} items, " +
                                      $"current cache: {CurrentCacheCount} items, {CurrentCacheSize / (1024 * 1024)} MB");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cache cleanup failed: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public static void ClearCache()
    {
        lock (_cleanupLock)
        {
            try
            {
                var items = _cache.Values.ToList();
                var filePaths = items.Select(item => item.FilePath).ToList();
                _cache.Clear();
                
                // 通知所有文件缓存状态变化（在UI线程中触发）
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var filePath in filePaths)
                    {
                        CacheStatusChanged?.Invoke(filePath, false);
                    }
                });
                
                // 在UI线程中释放所有位图
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var item in items)
                    {
                        item.Bitmap.Dispose();
                    }
                });
                
                Console.WriteLine("Cache cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear cache: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// 移除特定文件的缓存
    /// </summary>
    public static void RemoveFromCache(string filePath)
    {
        if (_cache.TryRemove(filePath, out var item))
        {
            // 通知缓存状态变化（在UI线程中触发）
            Dispatcher.UIThread.Post(() => CacheStatusChanged?.Invoke(filePath, false));
            
            Dispatcher.UIThread.Post(() => item.Bitmap.Dispose());
        }
    }
    
    /// <summary>
    /// 预加载图片（不会阻塞UI）
    /// </summary>
    public static async Task PreloadBitmapAsync(IStorageFile file)
    {
        try
        {
            await GetBitmapAsync(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to preload image ({file.Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// 批量顺序预取（如某张失败不中断，支持取消）
    /// </summary>
    public static async Task PreloadBitmapsSequentiallyAsync(IEnumerable<IStorageFile> files, CancellationToken token)
    {
        foreach (var f in files)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!IsInCache(f.Path.LocalPath))
                    await PreloadBitmapAsync(f);
            }
            catch { /* 单个失败忽略 */ }
        }
    }
    
    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public static (int count, long sizeBytes, string info) GetCacheStats()
    {
        var count = CurrentCacheCount;
        var size = CurrentCacheSize;
        var info = $"Cache: {count}/{MaxCacheCount} items, {size / (1024 * 1024)}/{MaxCacheSize / (1024 * 1024)} MB";
        return (count, size, info);
    }
}
