using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core
{
    public static class BitmapCacheService
    {
        private static readonly ConcurrentDictionary<string, (Bitmap, DateTime)> _cache = 
            new ConcurrentDictionary<string, (Bitmap, DateTime)>();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        
        public static async Task<Bitmap?> GetBitmapAsync(IStorageFile file, int? maxWidth = null)
        {
            var path = file.Path.AbsolutePath;
            
            // 尝试从缓存获取
            if (_cache.TryGetValue(path, out var cached) && 
                DateTime.UtcNow - cached.Item2 < CacheDuration)
            {
                return cached.Item1;
            }
            
            try
            {
                await using var stream = await file.OpenReadAsync();
                var bitmap = new Bitmap(stream);
                
                // 如果需要缩放
                if (maxWidth.HasValue && bitmap.PixelSize.Width > maxWidth.Value)
                {
                    var scale = (double)maxWidth.Value / bitmap.PixelSize.Width;
                    var newSize = new PixelSize(
                        (int)(bitmap.PixelSize.Width * scale),
                        (int)(bitmap.PixelSize.Height * scale)
                    );
                    
                    bitmap = bitmap.CreateScaledBitmap(newSize);
                    
                    // 使用更高效的缩放方法
                    // using var scaledBitmap = bitmap.CreateScaledBitmap(newSize);
                    // bitmap = new Bitmap(scaledBitmap.PlatformImpl);
                }
                
                // 更新缓存
                _cache[path] = (bitmap, DateTime.UtcNow);
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载图片失败: {ex.Message}");
                return null;
            }
        }
        
        public static void ClearCache()
        {
            foreach (var item in _cache.Values)
            {
                item.Item1.Dispose();
            }
            _cache.Clear();
        }
    }
}