using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Service
{
    public static class BitmapCacheService
    {
        private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = 
            new ConcurrentDictionary<string, WeakReference<Bitmap>>();
        
        public static async Task<Bitmap?> GetBitmapAsync(IStorageFile file, int? maxWidth = null)
        {
            var path = file.Path.AbsolutePath;
            
            // 尝试从缓存获取
            if (_cache.TryGetValue(path, out var weakRef) && weakRef.TryGetTarget(out var cachedBitmap))
            {
                return cachedBitmap;
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
                }
                
                // 更新缓存
                _cache[path] = new WeakReference<Bitmap>(bitmap);
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
            _cache.Clear();
        }
    }
}