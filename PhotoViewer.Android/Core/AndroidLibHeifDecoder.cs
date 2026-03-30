using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LibHeifSharp;
using PhotoViewer.Core;
using Buffer = System.Buffer;

namespace PhotoViewer.Android.Core;

/// <summary>
/// 基于 libheif 软件解码的 Android HEIF 解码器。
/// 通过打包 libheif.so + libde265.so 原生库，支持 HEIF YUV 4:2:2（如索尼 .HIF 文件）
/// 等 Android 系统编解码器不支持的格式。
/// 若 libheif.so 未打包进 APK（native libs 目录中缺失），自动回退到系统解码器。
/// </summary>
public sealed class AndroidLibHeifDecoder : IHeifDecoder
{
    /// <summary>
    /// 程序启动时一次性检测 libheif 原生库是否可用，结果缓存。
    /// </summary>
    private static readonly bool _libHeifAvailable = CheckLibHeifAvailable();

    /// <summary>系统级回退解码器（BitmapFactory）</summary>
    private readonly AndroidHeifDecoder _systemFallback = new();

    /// <summary>
    /// 检测 libheif 原生库是否已成功加载。
    /// 通过触发一次 P/Invoke 调用：若抛 DllNotFoundException 则库不存在；
    /// 若抛 HeifException（数据无效的合法错误）则库已就绪。
    /// </summary>
    private static bool CheckLibHeifAvailable()
    {
        try
        {
            // 用空字节触发 libheif 初始化；空数据会抛 HeifException，这是正常的
            using var ctx = new HeifContext(Array.Empty<byte>());
            return true;
        }
        catch (HeifException)
        {
            // HeifException = libheif.so 已加载，只是数据无效 → 库可用
            return true;
        }
        catch (DllNotFoundException)
        {
            // libheif.so 未在 APK native libs 中找到 → 回退系统解码器
            Console.WriteLine("[AndroidLibHeifDecoder] libheif.so 未找到，将使用系统解码器回退。");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif 初始化异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 是否支持 HEIF 解码。libheif 可用时返回 true；否则取决于系统版本（API 28+）。
    /// </summary>
    public bool IsSupported => _libHeifAvailable || _systemFallback.IsSupported;

    /// <summary>
    /// 异步加载 HEIF 图片为 Avalonia Bitmap。
    /// 优先使用系统解码器（硬件加速）；系统无法解码（返回 null）时回退 libheif 软件解码，
    /// 以支持 HEIF YUV 4:2:2 等系统不支持的格式。
    /// </summary>
    /// <param name="file">要解码的 HEIF 图片文件</param>
    /// <returns>解码后的 Bitmap，失败返回 null</returns>
    public async Task<Bitmap?> LoadBitmapAsync(IStorageFile file)
    {
        // 1) 优先系统解码器（硬件加速，速度快，覆盖常见 HEIF 4:2:0 格式）
        if (_systemFallback.IsSupported)
        {
            var systemResult = await _systemFallback.LoadBitmapAsync(file);
            if (systemResult != null) return systemResult;

            Console.WriteLine("[AndroidLibHeifDecoder] 系统解码器返回 null，尝试 libheif 软件解码。");
        }

        // 2) 系统无法解码（如 HEIF 4:2:2），回退 libheif 软件解码
        if (!_libHeifAvailable) return null;

        try
        {
            await using var stream = await file.OpenReadAsync();
            return await LoadBitmapFromStreamAsync(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif 解码也失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 异步加载 HEIF 缩略图。
    /// 优先使用系统解码器；系统无法解码时回退 libheif 软件解码。
    /// </summary>
    /// <param name="file">要解码的 HEIF 图片文件</param>
    /// <param name="maxSize">缩略图长边最大像素数</param>
    /// <returns>解码后的缩略图 Bitmap，失败返回 null</returns>
    public async Task<Bitmap?> LoadThumbnailAsync(IStorageFile file, int maxSize)
    {
        // 1) 优先系统解码器
        if (_systemFallback.IsSupported)
        {
            var systemResult = await _systemFallback.LoadThumbnailAsync(file, maxSize);
            if (systemResult != null) return systemResult;

            Console.WriteLine("[AndroidLibHeifDecoder] 系统缩略图解码返回 null，尝试 libheif 软件解码。");
        }

        // 2) 回退 libheif 软件解码
        if (!_libHeifAvailable) return null;

        try
        {
            await using var stream = await file.OpenReadAsync();
            return await LoadThumbnailFromStreamAsync(stream, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif 缩略图也失败: {ex.Message}");
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 内部实现：LibHeifSharp 解码逻辑（与 Desktop/LibHeifDecoder 一致）
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从流中使用 LibHeifSharp 解码完整 HEIF 图片。
    /// </summary>
    private static async Task<Bitmap?> LoadBitmapFromStreamAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var data = ms.ToArray();

        return await Task.Run(() =>
        {
            try
            {
                using var context = new HeifContext(data);
                using var imageHandle = context.GetPrimaryImageHandle();
                if (imageHandle == null) return null;

                using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
                if (image == null) return null;

                return ConvertHeifImageToBitmap(image);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AndroidLibHeifDecoder] LibHeifSharp 解码失败: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// 从流中使用 LibHeifSharp 解码 HEIF 缩略图。
    /// 优先使用内嵌缩略图，无合适缩略图时从主图采样。
    /// </summary>
    private static async Task<Bitmap?> LoadThumbnailFromStreamAsync(Stream stream, int maxSize)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var data = ms.ToArray();

        return await Task.Run(() =>
        {
            try
            {
                using var context = new HeifContext(data);
                using var imageHandle = context.GetPrimaryImageHandle();
                if (imageHandle == null) return null;

                // 1) 尝试内嵌缩略图
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
                            var longSide = Math.Max((int)th.Width, (int)th.Height);
                            if (longSide >= maxSize && longSide < bestLongSide)
                            {
                                selectedId = id;
                                bestLongSide = longSide;
                            }
                        }

                        if (selectedId.HasValue)
                        {
                            using var thHandle = imageHandle.GetThumbnailImage(selectedId.Value);
                            if (thHandle != null)
                            {
                                var bmp = DecodeThumbnailHandle(thHandle, maxSize);
                                if (bmp != null) return bmp;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AndroidLibHeifDecoder] 内嵌缩略图解码失败: {ex.Message}");
                    }
                }

                // 2) 无合适内嵌缩略图，从主图采样生成
                return GenerateThumbnailFromMainImage(imageHandle, maxSize);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AndroidLibHeifDecoder] LibHeifSharp 缩略图失败: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// 解码 HEIF 缩略图句柄为 Bitmap。
    /// </summary>
    private static Bitmap? DecodeThumbnailHandle(HeifImageHandle thumbnailHandle, int maxSize)
    {
        try
        {
            using var thumbnail = thumbnailHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (thumbnail == null) return null;
            return ConvertHeifImageToBitmapWithResize(thumbnail, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 缩略图句柄解码失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从主图句柄解码并缩放为指定尺寸的缩略图。
    /// </summary>
    private static Bitmap? GenerateThumbnailFromMainImage(HeifImageHandle imageHandle, int maxSize)
    {
        try
        {
            using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (image == null) return null;
            return ConvertHeifImageToBitmapWithResize(image, maxSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 主图缩略图生成失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将 HeifImage 转换为完整尺寸的 Avalonia Bitmap（RGB24 → BGRA8888/BGR24）。
    /// </summary>
    private static Bitmap? ConvertHeifImageToBitmap(HeifImage heifImage)
    {
        try
        {
            var reportedWidth = (int)heifImage.Width;
            var reportedHeight = (int)heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            if (stride <= 0) return null;

            const int bytesPerPixel = 3;
            var pixelsPerRow = stride / bytesPerPixel;

            // 竖拍照片修正：如果每行像素数小于报告宽度且等于报告高度，说明宽高需交换
            int actualWidth, actualHeight;
            if (pixelsPerRow < reportedWidth && pixelsPerRow == reportedHeight)
            {
                actualWidth = reportedHeight;
                actualHeight = reportedWidth;
            }
            else
            {
                actualWidth = reportedWidth;
                actualHeight = reportedHeight;
            }

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

                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        // RGB → BGRA
                        bgraData[destPixelOffset]     = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];     // R
                        bgraData[destPixelOffset + 3] = 255;                              // A
                    }
                }
            }

            return CreateBitmapFromBgraData(bgraData, actualWidth, actualHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] HeifImage 转换失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将 HeifImage 转换为指定最大边长的缩放 Bitmap。
    /// </summary>
    private static Bitmap? ConvertHeifImageToBitmapWithResize(HeifImage heifImage, int maxSize)
    {
        try
        {
            var reportedWidth = (int)heifImage.Width;
            var reportedHeight = (int)heifImage.Height;
            if (reportedWidth <= 0 || reportedHeight <= 0) return null;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            if (plane == null) return null;

            var stride = (int)plane.Stride;
            if (stride <= 0) return null;

            const int bytesPerPixel = 3;
            var pixelsPerRow = stride / bytesPerPixel;
            var swapPortrait = pixelsPerRow < reportedWidth;

            int srcWidth  = swapPortrait ? reportedHeight : reportedWidth;
            int srcHeight = swapPortrait ? reportedWidth  : reportedHeight;

            var scale = Math.Min((double)maxSize / srcWidth, (double)maxSize / srcHeight);
            if (scale >= 1.0) scale = 1.0;

            int targetWidth  = Math.Max(1, (int)(srcWidth  * scale));
            int targetHeight = Math.Max(1, (int)(srcHeight * scale));

            var bgraData = new byte[targetWidth * targetHeight * 4];

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null) return null;

                for (int ty = 0; ty < targetHeight; ty++)
                {
                    for (int tx = 0; tx < targetWidth; tx++)
                    {
                        var sx = Math.Min((int)(tx / scale), Math.Min(srcWidth - 1, pixelsPerRow - 1));
                        var sy = Math.Min((int)(ty / scale), srcHeight - 1);

                        var sourceRowPtr  = sourcePtr + (sy * stride);
                        var srcPixelOffset  = sx * bytesPerPixel;
                        var destPixelOffset = (ty * targetWidth + tx) * 4;

                        if (srcPixelOffset + 2 >= stride) break;
                        if (destPixelOffset + 3 >= bgraData.Length) break;

                        bgraData[destPixelOffset]     = sourceRowPtr[srcPixelOffset + 2]; // B
                        bgraData[destPixelOffset + 1] = sourceRowPtr[srcPixelOffset + 1]; // G
                        bgraData[destPixelOffset + 2] = sourceRowPtr[srcPixelOffset];     // R
                        bgraData[destPixelOffset + 3] = 255;                              // A
                    }
                }
            }

            return CreateBitmapFromBgraData(bgraData, targetWidth, targetHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] HeifImage 缩放转换失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 BGRA 字节数组创建 Avalonia Bitmap。
    /// 根据 BitmapLoader.IgnoreAlpha 决定输出格式（Bgra8888 或 Bgr24）。
    /// </summary>
    /// <param name="bgraData">BGRA 原始像素数据</param>
    /// <param name="width">图像宽度（像素）</param>
    /// <param name="height">图像高度（像素）</param>
    private static Bitmap? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = width * height * 4;
            if (bgraData.Length < expected) return null;

            if (BitmapLoader.IgnoreAlpha)
            {
                // 输出 BGR24（节省内存）
                var bgr = new byte[width * height * 3];
                for (int i = 0, j = 0; i < expected; i += 4, j += 3)
                {
                    bgr[j]     = bgraData[i];     // B
                    bgr[j + 1] = bgraData[i + 1]; // G
                    bgr[j + 2] = bgraData[i + 2]; // R
                }

                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormats.Bgr24);
                using (var locked = bitmap.Lock())
                {
                    CopyRowByRow(bgr, width * 3, locked.Address, locked.RowBytes, height);
                }
                return bitmap;
            }
            else
            {
                // 输出 BGRA8888
                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormats.Bgra8888);
                using (var locked = bitmap.Lock())
                {
                    CopyRowByRow(bgraData, width * 4, locked.Address, locked.RowBytes, height);
                }
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 创建 Bitmap 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按行将 src 数据拷贝到目标地址，处理 stride 对齐差异。
    /// </summary>
    /// <param name="src">源像素字节数组</param>
    /// <param name="srcStride">源每行字节数</param>
    /// <param name="dest">目标内存地址</param>
    /// <param name="destStride">目标每行字节数</param>
    /// <param name="height">图像高度</param>
    private static void CopyRowByRow(byte[] src, int srcStride, nint dest, int destStride, int height)
    {
        unsafe
        {
            var destPtr = (byte*)dest;
            var copySize = Math.Min(srcStride, destStride);
            for (int y = 0; y < height; y++)
            {
                var srcOff = y * srcStride;
                var dstOff = y * destStride;
                if (srcOff + copySize > src.Length) break;
                fixed (byte* srcPtr = &src[srcOff])
                {
                    Buffer.MemoryCopy(srcPtr, destPtr + dstOff, copySize, copySize);
                }
            }
        }
    }
}


