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
    private static int _maxCacheCount = 30;
    private static long _maxCacheSize = 2048L * 1024 * 1024; // 2048 MB
    
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
            _maxCacheSize = Math.Max(256L * 1024 * 1024, value); // 最小256MB
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
    
    // 新增：预加载内存预留与容量序列化
    private static long _reservedBytes = 0;
    private static readonly SemaphoreSlim _capacitySemaphore = new(1, 1);

    // 新增：平均缓存项大小（字节）
    private static long GetAverageCacheItemSizeBytes()
    {
        var count = CurrentCacheCount;
        if (count <= 0) return 0;
        var total = CurrentCacheSize;
        if (total <= 0) return 0;
        return total / count;
    }

    /// <summary>
    /// 估算文件解码后的内存大小：
    /// 1) EXIF 尺寸（width*height*4）
    /// 2) 缓存平均占用
    /// 3) 兜底 100MB
    /// </summary>
    public static async Task<long> EstimateDecodedSizeAsync(IStorageFile file, CancellationToken ct = default)
    {
        try
        {
            // 1) EXIF 尺寸
            var dims = await ExifLoader.TryGetDimensionsAsync(file);
            if (ct.IsCancellationRequested) return 0;
            if (dims.HasValue && dims.Value.width > 0 && dims.Value.height > 0)
            {
                return (long)dims.Value.width * dims.Value.height * 4;
            }
        }
        catch
        {
            // 忽略，走回退
        }

        // 2) 回退到缓存平均占用
        var avg = GetAverageCacheItemSizeBytes();
        if (avg > 0) return avg;

        // 3) 最终兜底为 100MB
        return 100L * 1024 * 1024;
    }

    /// <summary>
    /// 在解码前确保容量：(缓存 + 预留 + 需求) 不超过上限；必要时按 LRU 同步释放到安全水位。
    /// </summary>
    private static async Task EnsureCapacityAsync(long needBytes)
    {
        if (needBytes <= 0) return;

        await _capacitySemaphore.WaitAsync();
        try
        {
            long safeLimit = (long)(MaxCacheSize * 0.8);
            bool tooLarge = needBytes > (long)(MaxCacheSize * 0.6);

            while (true)
            {
                var currentSize = CurrentCacheSize;
                var totalPlanned = currentSize + Interlocked.Read(ref _reservedBytes) + needBytes;
                long limit = tooLarge ? safeLimit : MaxCacheSize;
                if (totalPlanned <= limit) break;

                var sorted = _cache.ToList()
                    .OrderBy(kv => kv.Value.LastAccessTime)
                    .ToList();

                if (sorted.Count == 0) break;

                var itemsToRemove = new List<(string key, Bitmap bmp)>();
                long willFree = 0;

                foreach (var kv in sorted)
                {
                    if (_cache.TryRemove(kv.Key, out var item))
                    {
                        itemsToRemove.Add((kv.Key, item.Bitmap));
                        willFree += item.Size;
                        if (currentSize - willFree + Interlocked.Read(ref _reservedBytes) + needBytes <= safeLimit)
                            break;
                    }
                }

                if (itemsToRemove.Count == 0) break;

                var removedKeys = itemsToRemove.Select(t => t.key).ToList();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var k in removedKeys)
                        CacheStatusChanged?.Invoke(k, false);
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var t in itemsToRemove)
                        t.bmp.Dispose();
                });

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        finally
        {
            _capacitySemaphore.Release();
        }
    }

    /// <summary>
    /// 预取任务申请内存预留；若估算过大（>60%上限）则返回 null（跳过预取）。
    /// </summary>
    public static async Task<IDisposable?> ReserveForPreloadAsync(IStorageFile file, CancellationToken ct)
    {
        var estimate = await EstimateDecodedSizeAsync(file, ct);
        if (estimate <= 0) return null;

        if (estimate > (long)(MaxCacheSize * 0.6))
            return null;

        await EnsureCapacityAsync(estimate);
        Interlocked.Add(ref _reservedBytes, estimate);
        return new Reservation(() => Interlocked.Add(ref _reservedBytes, -estimate));
    }

    private sealed class Reservation : IDisposable
    {
        private Action? _release;
        public Reservation(Action release) => _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
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

        // 在解码前进行容量保障（基于估算）
        try
        {
            var estimate = await EstimateDecodedSizeAsync(file);
            await EnsureCapacityAsync(estimate);
        }
        catch { /* 容量保障失败不致命，继续尝试加载 */ }
        
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

    // 内存告警事件载体（供 MessageBus 广播用）
    public readonly record struct MemoryWarningEvent(long beforeMB, long afterMB, DateTimeOffset time);

    /// <summary>
    /// 系统内存告警：按 LRU 精简缓存至目标上限占比（默认 50%），返回 (beforeBytes, afterBytes)。
    /// </summary>
    public static (long beforeBytes, long afterBytes) TrimOnMemoryWarning(double targetRatio = 0.5)
    {
        targetRatio = Math.Clamp(targetRatio, 0.1, 0.9);
        lock (_cleanupLock)
        {
            var before = CurrentCacheSize;
            var targetBytes = (long)(MaxCacheSize * targetRatio);
            if (before <= targetBytes || _cache.IsEmpty)
                return (before, before);

            // 按最久未使用 -> 最近使用 排序
            var sortedItems = _cache.ToList()
                .OrderBy(kvp => kvp.Value.LastAccessTime)
                .ToList();

            var toRemove = new List<string>();
            long willFree = 0;
            foreach (var kv in sortedItems)
            {
                toRemove.Add(kv.Key);
                willFree += kv.Value.Size;
                if (before - willFree <= targetBytes) break;
            }

            // 实际移除并在 UI 线程释放位图
            var removedItems = new List<(string key, Bitmap bmp)>();
            foreach (var key in toRemove)
            {
                if (_cache.TryRemove(key, out var item))
                {
                    removedItems.Add((key, item.Bitmap));
                }
            }

            if (removedItems.Count > 0)
            {
                var removedKeys = removedItems.Select(t => t.key).ToList();

                // 通知缓存状态变化（UI 线程）
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var k in removedKeys)
                        CacheStatusChanged?.Invoke(k, false);
                });

                // 释放位图（UI 线程）
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var t in removedItems)
                        t.bmp.Dispose();
                });
            }

            var after = CurrentCacheSize;
            return (before, after);
        }
    }
}
