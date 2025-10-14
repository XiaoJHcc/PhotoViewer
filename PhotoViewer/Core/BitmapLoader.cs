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
using Avalonia.Platform;
using Avalonia.Media; // 新增：用于合成去除透明度

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
        // 修改：根据像素格式估算大小（Bgr24=3，Bgra8888/Rgba8888=4，其余保守按4）
        int w = bitmap.PixelSize.Width;
        int h = bitmap.PixelSize.Height;
        int bpp = 4;
        try
        {
            if (bitmap is WriteableBitmap wb)
            {
                if (wb.Format == PixelFormats.Rgb24 || wb.Format == PixelFormats.Bgr24) bpp = 3;
                else if (wb.Format == PixelFormats.Rgba8888 || wb.Format == PixelFormats.Bgra8888) bpp = 4;
                else if (wb.Format == PixelFormats.Rgb565 || wb.Format == PixelFormats.Bgr565) bpp = 2;
            }
        }
        catch { /* 忽略，保守按4 */ }

        return (long)w * h * Math.Max(1, bpp);
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
    
    // 新增：忽略透明度（默认 false）
    public static bool IgnoreAlpha { get; set; } = false;

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
                // 修改：按设置计算 BPP
                int bpp = IgnoreAlpha ? 3 : 4;
                return (long)dims.Value.width * dims.Value.height * bpp;
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
            
            // 如果不需要旋转
            if (orientation == 1)
            {
                // 仅当需要移除Alpha通道时才进行转换
                return ConvertToDesiredFormat(originalBitmap);
            }
            
            // 应用EXIF旋转，此过程会直接生成最终格式的位图
            var finalBitmap = ApplyExifRotation(originalBitmap, orientation);
            
            // 释放原图内存
            originalBitmap.Dispose();
            
            if (finalBitmap == null)
            {
                Console.WriteLine($"Rotation failed for {file.Name}, returning null");
            }
            
            return finalBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load and rotate image ({file.Name}): {ex.Message}");
            return null;
        }
    }

    // 将位图按设置转为 Rgb24（忽略透明度）或原样返回
    private static unsafe Bitmap ConvertToDesiredFormat(Bitmap source)
    {
        // 未开启忽略透明度或源格式已符合要求，则原样返回
        if (!IgnoreAlpha || source.Format == PixelFormats.Rgb24)
        {
            return source;
        }

        // 仅处理带 Alpha 通道的常见格式
        if (source.Format != PixelFormats.Bgra8888 && source.Format != PixelFormats.Rgba8888)
        {
            return source;
        }

        var size = source.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return source;
        }

        WriteableBitmap? sourceWritable = null;
        bool sourceIsOriginal = source is WriteableBitmap;

        try
        {
            // 确保我们有一个可写的源位图
            if (sourceIsOriginal)
            {
                sourceWritable = (WriteableBitmap)source;
            }
            else
            {
                // 如果源不是 WriteableBitmap (例如 RenderTargetBitmap 或 Bitmap)，则复制一份
                sourceWritable = new WriteableBitmap(size, source.Dpi, source.Format.Value, source.AlphaFormat);
                using (var lockedFramebuffer = sourceWritable.Lock())
                {
                    source.CopyPixels(new PixelRect(size), lockedFramebuffer.Address, lockedFramebuffer.RowBytes * size.Height, lockedFramebuffer.RowBytes);
                }
                source.Dispose(); // 原始的非可写位图可以被释放了
            }

            // 创建目标位图，格式为 Rgb24 (每像素3字节)
            var target = new WriteableBitmap(size, sourceWritable.Dpi, PixelFormats.Rgb24);

            using (var sourceLock = sourceWritable.Lock())
            using (var targetLock = target.Lock())
            {
                var sourcePtr = (byte*)sourceLock.Address;
                var targetPtr = (byte*)targetLock.Address;
                int sourceStride = sourceLock.RowBytes;
                int targetStride = targetLock.RowBytes;

                bool isBgra = sourceWritable.Format == PixelFormats.Bgra8888;

                for (int y = 0; y < size.Height; y++)
                {
                    // 获取当前行的起始指针
                    var sourceLine = sourcePtr + y * sourceStride;
                    var targetLine = targetPtr + y * targetStride;

                    for (int x = 0; x < size.Width; x++)
                    {
                        // 源像素为4字节，目标像素为3字节
                        byte* pSource = sourceLine + x * 4;
                        byte* pTarget = targetLine + x * 3;

                        if (isBgra) // BGRA8888 -> Rgb24
                        {
                            // Avalonia 在非 Windows 平台（如 macOS, Linux, Android）上
                            // 实际可能是 RGBA 顺序，但 Format 报告为 Bgra8888。
                            // 为了跨平台兼容，我们检查字节序。
                            // R(pSource[2]), G(pSource[1]), B(pSource[0])
                            pTarget[0] = pSource[2]; // R
                            pTarget[1] = pSource[1]; // G
                            pTarget[2] = pSource[0]; // B
                        }
                        else // RGBA8888 -> Rgb24
                        {
                            // R(pSource[0]), G(pSource[1]), B(pSource[2])
                            pTarget[0] = pSource[0]; // R
                            pTarget[1] = pSource[1]; // G
                            pTarget[2] = pSource[2]; // B
                        }
                    }
                }
            }

            // 转换成功，释放临时的可写源位图
            sourceWritable.Dispose();
            return target;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert bitmap to Rgb24: {ex.Message}");
            // 如果转换失败，返回我们拥有的最新位图
            // 如果我们创建了一个副本，则返回该副本，否则返回原始位图
            return sourceIsOriginal ? source : (sourceWritable ?? source);
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
            Bitmap? rotatedBitmap = orientation switch
            {
                3 => RotateBitmap(originalBitmap, 180),
                6 => RotateBitmap(originalBitmap, 90),
                8 => RotateBitmap(originalBitmap, 270),
                _ => null
            };

            if (rotatedBitmap == null)
            {
                // 如果旋转失败或不需要旋转，返回原始位图的副本以统一后续处理流程
                // 但在此调用流程中，originalBitmap 会被释放，所以这里返回 null
                return null;
            }

            // 检查是否需要移除 Alpha 通道
            bool needsConversion = IgnoreAlpha && 
                                   (rotatedBitmap.Format == PixelFormats.Bgra8888 || rotatedBitmap.Format == PixelFormats.Rgba8888);

            if (needsConversion)
            {
                // ConvertToDesiredFormat 会在成功后 Dispose 传入的 rotatedBitmap
                return ConvertToDesiredFormat(rotatedBitmap);
            }

            return rotatedBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to apply EXIF rotation (orientation: {orientation}): {ex.Message}");
            return null; // 旋转失败时返回null，由调用方处理
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
                return null;
            }
            
            // 创建旋转后的位图，直接使用最终的目标格式
            RenderTargetBitmap? renderTarget = null;
            try
            {
                // 修正：RenderTargetBitmap 构造函数没有 format 参数
                renderTarget = new RenderTargetBitmap(newSize, originalBitmap.Dpi);
                if (renderTarget == null)
                {
                    Console.WriteLine("Failed to create RenderTargetBitmap");
                    return null;
                }
                
                using var context = renderTarget.CreateDrawingContext();
                if (context == null)
                {
                    Console.WriteLine("Failed to create DrawingContext");
                    renderTarget.Dispose();
                    return null;
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
                    
                    // 绘制原图。DrawImage会处理到目标RenderTarget格式的转换
                    context.DrawImage(originalBitmap, destRect);
                }
                
                return renderTarget;
            }
            catch (Exception ex)
            {
                renderTarget?.Dispose();
                Console.WriteLine($"Failed during bitmap rotation rendering: {ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to rotate bitmap by {degrees} degrees: {ex.Message}");
            return null;
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

    // 内存告警事件载体（UI 仅展示触发时的大小、数量与时间）
    public readonly record struct MemoryWarningEvent(long sizeMB, int count, DateTimeOffset time);

    /// <summary>
    /// 系统内存告警：按 LRU 精简缓存至目标上限占比（默认 50%），返回 (beforeBytes, afterBytes)。
    /// 复用 LRU 公共方法，减少重复代码。
    /// </summary>
    public static (long beforeBytes, long afterBytes) TrimOnMemoryWarning(double targetRatio = 0.5)
    {
        targetRatio = Math.Clamp(targetRatio, 0.1, 0.9);
        var targetBytes = (long)(MaxCacheSize * targetRatio);
        var (before, _, after) = LruRemoveToSize(targetBytes);
        return (before, after);
    }

    /// <summary>
    /// 按“当前缓存大小的比例”清理（用于系统告警：清理到触发时缓存大小的 80%）。
    /// 返回 (beforeBytes, beforeCount, afterBytes)。
    /// </summary>
    public static (long beforeBytes, int beforeCount, long afterBytes) TrimToCurrentRatio(double targetCurrentRatio)
    {
        targetCurrentRatio = Math.Clamp(targetCurrentRatio, 0.1, 0.95);
        var snapshot = CurrentCacheSize; // 注意：最终目标在锁内再计算一次，避免竞争
        var targetBytes = (long)(snapshot * targetCurrentRatio);
        return LruRemoveToSize(targetBytes);
    }

    /// <summary>
    /// 公共 LRU 清理：按最后访问时间升序移除，直到当前大小 <= targetBytes。
    /// 返回 (beforeBytes, beforeCount, afterBytes)。
    /// </summary>
    private static (long beforeBytes, int beforeCount, long afterBytes) LruRemoveToSize(long targetBytes)
    {
        lock (_cleanupLock)
        {
            var before = CurrentCacheSize;
            var beforeCount = CurrentCacheCount;
            if (before <= targetBytes || _cache.IsEmpty)
                return (before, beforeCount, before);

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

            var removedItems = new List<(string key, Bitmap bmp)>();
            foreach (var key in toRemove)
            {
                if (_cache.TryRemove(key, out var item))
                    removedItems.Add((key, item.Bitmap));
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
            return (before, beforeCount, after);
        }
    }
}
