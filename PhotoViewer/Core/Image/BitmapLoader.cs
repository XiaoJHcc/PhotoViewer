using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 缓存项
/// </summary>
public class BitmapCacheItem
{
    /// <summary>已解码图像。</summary>
    public DecodedImage Image { get; set; }
    public long Size { get; set; }
    public DateTime LastAccessTime { get; set; }
    public string FilePath { get; set; }

    public BitmapCacheItem(DecodedImage image, string filePath)
    {
        Image = image;
        FilePath = filePath;
        Size = EstimateImageSize(image);
        LastAccessTime = DateTime.Now;
    }

    private static long EstimateImageSize(DecodedImage image)
    {
        return (long)image.Width * image.Height * Math.Max(1, DecodedImage.BytesPerPixel(image.Encoding));
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
    // 在途解码去重：同路径请求共享同一任务，完成后移除
    private static readonly ConcurrentDictionary<string, Lazy<Task<DecodedImage?>>> _inFlight = new();
    private static readonly object _cleanupLock = new();

    // 淘汰保护集：临近当前图与最近浏览过的路径，淘汰选择时整体排到最后（volatile 整体替换）
    private static volatile HashSet<string> _protectedPaths = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 设置淘汰保护集：临近当前图与最近浏览过的路径。
    /// 所有淘汰路径（EnsureCapacity / Cleanup / LruRemoveToSize）统一把保护项排到选择序列末尾，
    /// 只有当非保护项全淘汰完仍不够时才动保护项（保护项之间也按 LRU）。
    /// </summary>
    public static void SetProtectedPaths(IReadOnlyCollection<string> paths)
    {
        _protectedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 淘汰选择序列：非保护项按 LRU（最后访问时间升序）在前，保护项整体排在末尾（内部同样按 LRU）。
    /// </summary>
    private static List<KeyValuePair<string, BitmapCacheItem>> OrderForEviction()
    {
        var protectedSet = _protectedPaths;
        return _cache.ToList()
            .OrderBy(kv => protectedSet.Contains(kv.Key) ? 1 : 0)
            .ThenBy(kv => kv.Value.LastAccessTime)
            .ToList();
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
    public static async Task<long> EstimateDecodedSizeAsync(IFileRef file, CancellationToken ct = default)
    {
        try
        {
            // 1) EXIF 尺寸
            var dims = await ExifLoader.TryGetDimensionsAsync(file).ConfigureAwait(false);
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

                var sorted = OrderForEviction();

                if (sorted.Count == 0) break;

                var itemsToRemove = new List<(string key, DecodedImage img)>();
                long willFree = 0;

                foreach (var kv in sorted)
                {
                    if (_cache.TryRemove(kv.Key, out var item))
                    {
                        itemsToRemove.Add((kv.Key, item.Image));
                        willFree += item.Size;
                        if (currentSize - willFree + Interlocked.Read(ref _reservedBytes) + needBytes <= safeLimit)
                            break;
                    }
                }

                if (itemsToRemove.Count == 0) break;

                var removedKeys = itemsToRemove.Select(t => t.key).ToList();
                await UiDispatcher.InvokeAsync(() =>
                {
                    foreach (var k in removedKeys)
                        CacheStatusChanged?.Invoke(k, false);
                });

                await UiDispatcher.InvokeAsync(() =>
                {
                    foreach (var t in itemsToRemove)
                        t.img.Dispose();
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
    public static async Task<IDisposable?> ReserveForPreloadAsync(IFileRef file, CancellationToken ct)
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
    /// 异步获取图片（带缓存和EXIF旋转）；同路径并发请求在途去重，共享同一解码任务。
    /// </summary>
    public static Task<DecodedImage?> GetBitmapAsync(IFileRef file, CancellationToken ct = default)
    {
        var filePath = file.PathKey;
        
        // 检查缓存
        if (_cache.TryGetValue(filePath, out var cachedItem))
        {
            cachedItem.UpdateAccessTime();
            return Task.FromResult<DecodedImage?>(cachedItem.Image);
        }

        // 缓存未命中：合并同路径的在途请求（首个请求的 ct 生效于共享任务）
        var lazy = _inFlight.GetOrAdd(filePath, _ => new Lazy<Task<DecodedImage?>>(
            () => LoadAndCacheAsync(file, filePath, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        return AwaitInFlightAsync(filePath, lazy, task);
    }

    /// <summary>
    /// 等待在途任务完成（无论成败）后将其移出去重表；只移除自己那一轮 Lazy，避免误删新一轮请求。
    /// </summary>
    private static async Task<DecodedImage?> AwaitInFlightAsync(string filePath, Lazy<Task<DecodedImage?>> lazy, Task<DecodedImage?> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<DecodedImage?>>>(filePath, lazy));
        }
    }

    /// <summary>
    /// 缓存未命中路径：容量保障 → 线程池解码 → 入缓存。
    /// </summary>
    private static async Task<DecodedImage?> LoadAndCacheAsync(IFileRef file, string filePath, CancellationToken ct)
    {
        // 在解码前进行容量保障（基于估算）
        try
        {
            var estimate = await EstimateDecodedSizeAsync(file, ct).ConfigureAwait(false);
            await EnsureCapacityAsync(estimate).ConfigureAwait(false);
        }
        catch { /* 容量保障失败不致命，继续尝试加载 */ }
        
        try
        {
            // 加载图片（解码本体在线程池执行，续体不回 UI 线程）
            var image = await Task.Run(() => LoadBitmapWithExifRotationAsync(file, ct), ct).ConfigureAwait(false);
            if (image == null) return null;
            
            // 添加到缓存
            var cacheItem = new BitmapCacheItem(image, filePath);
            _cache[filePath] = cacheItem;
            
            // 通知缓存状态变化（在UI线程中触发）
            UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, true));
            
            // 异步清理缓存
            _ = Task.Run(CleanupCache);
            
            return image;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load image ({file.Name}): {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// 加载图片并应用EXIF旋转
    /// </summary>
    private static async Task<DecodedImage?> LoadBitmapWithExifRotationAsync(IFileRef file, CancellationToken ct = default)
    {
        try
        {
            DecodedImage? originalImage = null;
            // RAW 平台解码器（macOS ImageIO）内部已应用 EXIF 方向，下方不再二次旋转
            bool decoderPreRotated = false;

            // 检查是否为 HEIF 格式
            if (HeifLoader.IsHeifFile(file))
            {
                originalImage = await HeifLoader.LoadHeifBitmapAsync(file).ConfigureAwait(false);
            }
            else if (RawLoader.IsRawFile(file))
            {
                originalImage = await RawLoader.LoadRawBitmapAsync(file).ConfigureAwait(false);
                decoderPreRotated = originalImage != null;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                originalImage = SkiaImageCodec.Decode(stream);
            }
            
            // 验证原始图像是否有效
            if (originalImage == null || originalImage.Width <= 0 || originalImage.Height <= 0)
            {
                Console.WriteLine($"Invalid image loaded from file: {file.Name}");
                originalImage?.Dispose();
                return null;
            }
            
            // 获取EXIF方向信息（RAW 解码器已预旋转的不再重复读取）
            var orientation = decoderPreRotated ? 1 : await GetExifOrientationAsync(file).ConfigureAwait(false);
            
            // 只有 3/6/8 需要旋转，其余（包括 0、1 及未知值）均视为无旋转
            if (orientation != 3 && orientation != 6 && orientation != 8)
            {
                return ConvertToDesiredFormat(originalImage);
            }
            
            // 应用EXIF旋转，此过程会直接生成最终格式的图像
            var finalImage = ApplyExifRotation(originalImage, orientation);
            
            // 释放原图内存
            originalImage.Dispose();
            
            if (finalImage == null)
                throw new InvalidOperationException($"EXIF 旋转失败（方向值 {orientation}）");
            
            return finalImage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load and rotate image ({file.Name}): {ex.Message}");
            throw;
        }
    }

    // 将图像按设置转为 Rgb24（忽略透明度）或原样返回；成功转换时释放传入图像
    private static DecodedImage ConvertToDesiredFormat(DecodedImage source)
    {
        // 未开启忽略透明度或源格式已符合要求，则原样返回
        if (!IgnoreAlpha || source.Encoding == PixelEncoding.Rgb24)
        {
            return source;
        }

        // 仅处理 Bgra32 来源
        if (source.Encoding != PixelEncoding.Bgra32)
        {
            return source;
        }

        try
        {
            var w = source.Width;
            var h = source.Height;
            var rgb = new byte[(long)w * h * 3];
            for (int y = 0; y < h; y++)
            {
                var srcRow = (long)y * source.Stride;
                var destRow = (long)y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    var si = srcRow + x * 4;
                    var di = destRow + x * 3;
                    // BGRA8888: B=p[0], G=p[1], R=p[2]
                    rgb[di] = source.Pixels[si + 2];     // R
                    rgb[di + 1] = source.Pixels[si + 1]; // G
                    rgb[di + 2] = source.Pixels[si];     // B
                }
            }

            source.Dispose();
            return new DecodedImage(rgb, w, h, w * 3, PixelEncoding.Rgb24);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert image to Rgb24: {ex.Message}");
            return source;
        }
    }
    
    /// <summary>
    /// 获取EXIF方向值
    /// </summary>
    private static async Task<int> GetExifOrientationAsync(IFileRef file)
    {
        try
        {
            var exifData = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
            return exifData?.OrientationValue ?? 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read EXIF orientation for {file.Name}: {ex.Message}");
            return 1;
        }
    }
    
    /// <summary>
    /// 应用EXIF旋转到图像
    /// </summary>
    private static DecodedImage? ApplyExifRotation(DecodedImage originalImage, int orientation)
    {
        try
        {
            DecodedImage? rotatedImage = orientation switch
            {
                3 => SkiaImageCodec.ApplyOrientation(originalImage, 180, false),
                6 => SkiaImageCodec.ApplyOrientation(originalImage, 90, false),
                8 => SkiaImageCodec.ApplyOrientation(originalImage, 270, false),
                _ => null
            };

            if (rotatedImage == null)
            {
                // 旋转失败时返回 null，由调用方处理
                return null;
            }

            // 检查是否需要移除 Alpha 通道
            bool needsConversion = IgnoreAlpha && rotatedImage.Encoding == PixelEncoding.Bgra32;

            if (needsConversion)
            {
                // ConvertToDesiredFormat 会在成功后 Dispose 传入的 rotatedImage
                return ConvertToDesiredFormat(rotatedImage);
            }

            return rotatedImage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to apply EXIF rotation (orientation: {orientation}): {ex.Message}");
            return null; // 旋转失败时返回null，由调用方处理
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
                
                // 按淘汰选择序列排序（非保护项 LRU 在前，保护项排末尾）
                var sortedItems = OrderForEviction();
                
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
                        UiDispatcher.Post(() => CacheStatusChanged?.Invoke(key, false));
                        
                        // 在UI线程中释放位图
                        UiDispatcher.Post(() => item.Image.Dispose());
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
                UiDispatcher.Post(() =>
                {
                    foreach (var filePath in filePaths)
                    {
                        CacheStatusChanged?.Invoke(filePath, false);
                    }
                });
                
                // 在UI线程中释放所有位图
                UiDispatcher.Post(() =>
                {
                    foreach (var item in items)
                    {
                        item.Image.Dispose();
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
            UiDispatcher.Post(() => CacheStatusChanged?.Invoke(filePath, false));
            
            UiDispatcher.Post(() => item.Image.Dispose());
        }
    }
    
    /// <summary>
    /// 预加载图片（不会阻塞UI）
    /// </summary>
    public static async Task PreloadBitmapAsync(IFileRef file, CancellationToken ct = default)
    {
        try
        {
            await GetBitmapAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to preload image ({file.Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// 批量顺序预取（如某张失败不中断，支持取消）
    /// </summary>
    public static async Task PreloadBitmapsSequentiallyAsync(IEnumerable<IFileRef> files, CancellationToken token)
    {
        foreach (var f in files)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!IsInCache(f.PathKey))
                    await PreloadBitmapAsync(f, token);
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
    /// 公共 LRU 清理：按淘汰选择序列（非保护项 LRU 在前，保护项排末尾）移除，直到当前大小 &lt;= targetBytes。
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

            var sortedItems = OrderForEviction();

            var toRemove = new List<string>();
            long willFree = 0;
            foreach (var kv in sortedItems)
            {
                toRemove.Add(kv.Key);
                willFree += kv.Value.Size;
                if (before - willFree <= targetBytes) break;
            }

            var removedItems = new List<(string key, DecodedImage img)>();
            foreach (var key in toRemove)
            {
                if (_cache.TryRemove(key, out var item))
                    removedItems.Add((key, item.Image));
            }

            if (removedItems.Count > 0)
            {
                var removedKeys = removedItems.Select(t => t.key).ToList();

                // 通知缓存状态变化（UI 线程）
                UiDispatcher.Post(() =>
                {
                    foreach (var k in removedKeys)
                        CacheStatusChanged?.Invoke(k, false);
                });

                // 释放位图（UI 线程）
                UiDispatcher.Post(() =>
                {
                    foreach (var t in removedItems)
                        t.img.Dispose();
                });
            }

            var after = CurrentCacheSize;
            return (before, beforeCount, after);
        }
    }
}
