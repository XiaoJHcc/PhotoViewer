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

    /// <summary>
    /// libheif 不可用时的具体原因描述，供诊断使用。
    /// </summary>
    private static readonly string _libHeifUnavailableReason = GetLibHeifUnavailableReason();

    /// <summary>系统级回退解码器（BitmapFactory）</summary>
    private readonly AndroidHeifDecoder _systemFallback = new();

    /// <summary>
    /// 检测 libheif 原生库是否已成功加载。
    /// 探针：传入 1 字节数据以触发 P/Invoke（避免空数组被 LibHeifSharp 在进入 native 前就拒绝）；
    /// 若抛 DllNotFoundException 则库不存在；若抛 HeifException（数据无效）则库已就绪。
    /// </summary>
    private static bool CheckLibHeifAvailable()
    {
        try
        {
            // 注意：必须传入非空字节数组；Array.Empty<byte>() 会被 LibHeifSharp 在调用 native
            // 之前就以 ArgumentException 拒绝，导致误判为库不可用。
            using var ctx = new HeifContext(new byte[] { 0x00 });
            return true;
        }
        catch (HeifException)
        {
            // HeifException = libheif.so 已加载，只是数据无效（非 HEIF 格式）→ 库可用
            return true;
        }
        catch (DllNotFoundException ex)
        {
            // libheif.so 未在 APK native libs 中找到
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif.so 未找到: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif 初始化异常 ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 返回 libheif 不可用的原因描述（仅在 CheckLibHeifAvailable 返回 false 时有意义）。
    /// 设计为独立方法，确保 _libHeifAvailable 已先被赋值。
    /// </summary>
    private static string GetLibHeifUnavailableReason()
    {
        if (_libHeifAvailable) return string.Empty;
        try
        {
            using var ctx = new HeifContext(new byte[] { 0x00 });
            return string.Empty; // 不应进入此分支
        }
        catch (HeifException)
        {
            return string.Empty; // 可用，不应进入此分支
        }
        catch (DllNotFoundException ex)
        {
            return $"libheif.so 未找到（DllNotFoundException: {ex.Message}）";
        }
        catch (Exception ex)
        {
            return $"libheif 初始化异常（{ex.GetType().Name}: {ex.Message}）";
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
        var reasons = new System.Text.StringBuilder();

        // 1) 优先系统解码器（硬件加速，速度快，覆盖常见 HEIF 4:2:0 格式）
        if (_systemFallback.IsSupported)
        {
            var systemResult = await _systemFallback.LoadBitmapAsync(file);
            if (systemResult != null) return systemResult;

            reasons.AppendLine("① 系统解码器(BitmapFactory/API 28+): 返回 null（格式可能不受支持）");
            Console.WriteLine("[AndroidLibHeifDecoder] 系统解码器返回 null，尝试 libheif 软件解码。");
        }
        else
        {
            int apiLevel = (int)global::Android.OS.Build.VERSION.SdkInt;
            reasons.AppendLine($"① 系统解码器(BitmapFactory): 不支持（需 API 28+，当前 API {apiLevel}）");
        }

        // 2) 系统无法解码（如 HEIF 4:2:2），回退 libheif 软件解码
        if (!_libHeifAvailable)
        {
            var reason = string.IsNullOrEmpty(_libHeifUnavailableReason)
                ? "libheif.so 未能加载（原因未知）"
                : _libHeifUnavailableReason;
            reasons.AppendLine($"② libheif 软件解码器: {reason}");
            HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
            return null;
        }

        try
        {
            var result = await HeifLoader.RunCpuDecodeAsync(async () =>
            {
                await using var stream = await file.OpenReadAsync();
                return await LoadBitmapFromStreamAsync(stream);
            });
            if (result == null)
            {
                reasons.AppendLine("② libheif 软件解码器: 解码返回 null（HeifImage 无效或格式不兼容）");
                HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
            }
            return result;
        }
        catch (Exception ex)
        {
            reasons.AppendLine($"② libheif 软件解码器: 解码失败（{ex.GetType().Name}: {ex.Message}）");
            HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
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
            return await HeifLoader.RunCpuDecodeAsync(async () =>
            {
                await using var stream = await file.OpenReadAsync();
                return await LoadThumbnailFromStreamAsync(stream, maxSize);
            });
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
    /// 异常不在此方法内吞掉，由调用方捕获并格式化为用户可见的错误信息。
    /// </summary>
    private static async Task<Bitmap?> LoadBitmapFromStreamAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var data = ms.ToArray();

        Console.WriteLine($"[AndroidLibHeifDecoder] 开始 libheif 解码，数据大小: {data.Length} 字节");

        return await Task.Run(() =>
        {
            using var context = new HeifContext(data);
            Console.WriteLine("[AndroidLibHeifDecoder] HeifContext 创建成功");

            using var imageHandle = context.GetPrimaryImageHandle();
            if (imageHandle == null)
                throw new InvalidOperationException("GetPrimaryImageHandle 返回 null");

            Console.WriteLine($"[AndroidLibHeifDecoder] 主图句柄: {imageHandle.Width}x{imageHandle.Height}, " +
                              $"HasAlpha={imageHandle.HasAlphaChannel}, BitDepth={imageHandle.BitDepth}");

            using var image = imageHandle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);
            if (image == null)
                throw new InvalidOperationException("Decode 返回 null（HeifColorspace.Rgb, InterleavedRgb24）");

            Console.WriteLine($"[AndroidLibHeifDecoder] 解码成功: {image.Width}x{image.Height}");

            var result = ConvertHeifImageToBitmap(image);
            if (result == null)
                throw new InvalidOperationException("ConvertHeifImageToBitmap 返回 null（像素数据转换失败）");

            return result;
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


