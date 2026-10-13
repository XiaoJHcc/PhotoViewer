using System;
using System.IO;
using System.Threading.Tasks;
using LibHeifSharp;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using Buffer = System.Buffer;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android HEIF 解码器(系统路 + libheif 软解双路)。
/// 优先系统解码器(BitmapFactory,硬件加速);系统不支持(如 HEIF YUV 4:2:2、索尼 .HIF)时
/// 回退打包的 libheif.so + libde265.so 软件解码;libheif.so 未打包进 APK 时仅用系统路。
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

    /// <summary>libheif/系统解码均不应用 EXIF 方向,BitmapLoader 负责旋转。</summary>
    public bool OutputPreRotated => false;

    /// <summary>
    /// 解码 HEIF:优先系统解码器（硬件加速）；系统无法解码（返回 null）时回退 libheif 软件解码，
    /// 以支持 HEIF YUV 4:2:2 等系统不支持的格式。
    /// maxSize = null 全分辨率;非空时代理(libheif 无子采样解码,P1-M4 口径:全解码 + Skia Mitchell 精缩)。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        var reasons = new System.Text.StringBuilder();

        // 1) 优先系统解码器（硬件加速，速度快，覆盖常见 HEIF 4:2:0 格式）
        if (_systemFallback.IsSupported)
        {
            var systemResult = await _systemFallback.DecodeAsync(file, maxSize);
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
                var full = await LoadBitmapFromStreamAsync(stream);
                if (full == null || !maxSize.HasValue) return full;
                var scaled = SkiaImageCodec.ScaleToLongEdge(full, maxSize.Value);
                if (scaled == null) return full; // null = 不需缩放(源已小于目标)或缩放失败,用全图保证正确性
                full.Release();
                return scaled;
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

    // ────────────────────────────────────────────────────────────────────────────
    // 内部实现：LibHeifSharp 解码逻辑（与 Desktop/LibHeifDecoder 一致）
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从流中使用 LibHeifSharp 解码完整 HEIF 图片。
    /// 异常不在此方法内吞掉，由调用方捕获并格式化为用户可见的错误信息。
    /// </summary>
    private static async Task<DecodedImage?> LoadBitmapFromStreamAsync(Stream stream)
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
    /// 将 HeifImage 转换为完整尺寸的图像（RGB24 → BGRA8888）。
    /// </summary>
    private static DecodedImage? ConvertHeifImageToBitmap(HeifImage heifImage)
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

            var bgraData = DecodedImage.RentPixels(actualWidth * actualHeight * 4);

            unsafe
            {
                var sourcePtr = (byte*)plane.Scan0;
                if (sourcePtr == null)
                {
                    ImageBufferPool.Return(bgraData);
                    return null;
                }

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
    /// 从 BGRA 字节数组创建已解码图像(恒 Bgra32;IgnoreAlpha 的 24 位转换由 BitmapLoader 统一做)。
    /// </summary>
    /// <param name="bgraData">BGRA 原始像素数据</param>
    /// <param name="width">图像宽度（像素）</param>
    /// <param name="height">图像高度（像素）</param>
    private static DecodedImage? CreateBitmapFromBgraData(byte[] bgraData, int width, int height)
    {
        try
        {
            var expected = (long)width * height * 4;
            if (bgraData.Length < expected) return null;
            // 缓冲为池化租用(调用方经 RentPixels 取得),随图像租约归零归还池
            return DecodedImage.CreatePooled(bgraData, width, height, width * 4, PixelEncoding.Bgra32);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 创建图像失败: {ex.Message}");
            return null;
        }
    }
}


