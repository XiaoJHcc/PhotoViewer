using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using LibHeifSharp;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;
using Bitmap = Android.Graphics.Bitmap;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android HEIF 解码器（胶片缩略图与兜底链，纯 libheif 出口，无系统解码段）。
/// 顺序：小尺寸请求先解容器自带的 <c>thmb</c> 缩略图项（约 5 ms，不动主图），
/// 再走共享快速路径（容器自解析 + <c>pvhevc</c> 并行解 HEVC 瓦片），
/// 最后共享 libheif 软解（覆盖 YUV 4:2:2、索尼 .HIF 等快速路径解不了的容器；色彩与位深口径见
/// <see cref="LibHeifColorDecoder"/>）。系统 <see cref="BitmapFactory"/> 的 HEIF 输出即使读到 P3 ICC
/// 也有灰阶偏差，已不作为色彩出口。
/// 契约：产物一律是**显示朝向**（方向 1）的 sRGB8，<see cref="OutputPreRotated"/> 为 true，
/// <see cref="ThumbnailService"/> / <c>BitmapLoader</c> 不再按容器或 EXIF 方向二次旋转；
/// 本链不读 HEIF 内的 EXIF Orientation 项（ISO/IEC 23008-12 规定忽略）。
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



    /// <summary>
    /// 走容器 <c>thmb</c> 缩略图项的请求上限（目标长边，像素）。
    /// </summary>
    /// <remarks>
    /// 胶片栏请求经 <c>ThumbnailService</c> 传进来的是目标短边（列表档 120），
    /// DINO 特征请求是 560 且必须保持“整幅主图”语义。上限取 512：覆盖列表档，把 560 挡在外面。
    /// 命中时输出尺寸仍按主图画布折算，与主图路径同一口径。
    /// </remarks>
    private const int ContainerThumbnailMaxLongEdge = 512;

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
    /// 是否支持 HEIF 解码。Android 上恒为 true；libheif 缺失时 <see cref="DecodeAsync"/> 会记下缺失原因并返回 null。
    /// </summary>
    public bool IsSupported => OperatingSystem.IsAndroid();

    /// <summary>产物一律已按容器 <c>irot</c>/<c>imir</c> 摆到显示朝向,方向恒为 1,调用方不得再旋转。</summary>
    public bool OutputPreRotated => true;

    /// <summary>
    /// 解码 HEIF：小尺寸请求先解容器 <c>thmb</c> 缩略图项，再走共享快速路径，最后共享 libheif 软解。
    /// 产物统一摆到显示朝向（方向 1）。
    /// </summary>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        var reasons = new System.Text.StringBuilder();
        var bytes = await FastHeifDecoder.ReadAllBytesAsync(file).ConfigureAwait(false);
        // 容器方向：null = 读不了（此时容器内嵌 JPEG item 的存储方向无法证明，不能当已摆正使用）。
        int? containerOrientation = HeifContainer.TryParse(bytes, out _)?.ExifOrientation;
        var identity = containerOrientation == 1;

        // 1) 胶片缩略图/导航图直接解相机写进容器的小图，不动主图。
        if (maxSize is int thumbnailLimit && thumbnailLimit <= ContainerThumbnailMaxLongEdge)
        {
            string? thumbnailReason = null;
            var thumbnail = await Task.Run(() => FastHeifDecoder.TryDecodeContainerThumbnail(
                bytes, thumbnailLimit, PixelEncoding.Bgra32, CancellationToken.None, out thumbnailReason))
                .ConfigureAwait(false);
            if (thumbnail != null)
            {
                var display = ToDisplayOriented(thumbnail);
                if (display != null) return display;
                reasons.AppendLine("① 容器缩略图项: 方向变换失败");
            }
            else
            {
                reasons.AppendLine("① 容器缩略图项: " + (thumbnailReason ?? "没有够大的 thmb 项"));
                if (thumbnailReason != null)
                    Console.WriteLine($"[AndroidLibHeifDecoder] 缩略图项不可用（{file.Name}）：{thumbnailReason}");
            }
        }

        // 2) 共享快速路径：容器自解析 + pvhevc 并行解 HEVC 瓦片（产物未旋转，按自身方向摆正）。
        string? fastReason = null;
        var fast = await Task.Run(() => FastHeifDecoder.TryDecode(
            bytes, maxSize, PixelEncoding.Bgra32, CancellationToken.None, out fastReason)).ConfigureAwait(false);
        if (fast != null)
        {
            var display = ToDisplayOriented(fast);
            if (display != null) return display;
            reasons.AppendLine("② 快速解码: 方向变换失败");
        }
        else
        {
            reasons.AppendLine($"② 快速解码: {fastReason ?? "未知原因"}");
            Console.WriteLine($"[AndroidLibHeifDecoder] 快速路径不可用（{file.Name}）：{fastReason ?? "未知原因"}");
        }

        // 3) 快速路径不可用时走共享 libheif 软解（产物已显示朝向）。
        //    共享出口按文件真实声明（ICC 优先，其次 nclx）打标签再转 sRGB8，高位深源不先量化；
        //    PQ/HLG 由它如实拒绝并写 HeifLoader.LastDecodeError，不按 SDR 输出。
        if (!_libHeifAvailable)
        {
            var reason = string.IsNullOrEmpty(_libHeifUnavailableReason)
                ? "libheif.so 未能加载（原因未知）"
                : _libHeifUnavailableReason;
            reasons.AppendLine($"④ libheif 软件解码器: {reason}");
            HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
            return null;
        }

        try
        {
            var result = await HeifLoader.RunCpuDecodeAsync(() => Task.Run(() =>
            {
                // 缩略图请求优先用容器内的 JPEG item（相机自己渲染的小图），避免全图软解；
                // 该 item 的存储方向未知，只在容器方向恒等时才用。
                if (identity && maxSize is <= ContainerThumbnailMaxLongEdge)
                {
                    var thumb = LoadEmbeddedJpegThumbnail(bytes);
                    if (thumb != null) return thumb;
                }
                return LibHeifColorDecoder.Decode(bytes, maxSize, LibHeifOutput.Bgra32Srgb,
                    CancellationToken.None, out _);
            }));
            if (result == null)
            {
                reasons.AppendLine("④ libheif 软件解码器: "
                    + (HeifLoader.LastDecodeError ?? "解码返回 null（格式或色彩声明超出支持面）"));
                HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
            }
            return result;
        }
        catch (Exception ex)
        {
            reasons.AppendLine($"④ libheif 软件解码器: 解码失败（{ex.GetType().Name}: {ex.Message}）");
            HeifLoader.SetLastDecodeError(reasons.ToString().TrimEnd());
            Console.WriteLine($"[AndroidLibHeifDecoder] libheif 解码也失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>把未旋转产物按自身方向摆到显示朝向（方向 1）并统一成 sRGB8；接管并释放源租约，失败返回 null。</summary>
    private static DecodedImage? ToDisplayOriented(DecodedImage source)
    {
        if (source.Orientation == 1) return source;
        try
        {
            return ColorEngine.ToAnalysisSrgb8(source, source.Orientation);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 方向变换失败（方向 {source.Orientation}）：{exception.Message}");
            return null;
        }
        finally
        {
            source.Release();
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 内部实现：容器内嵌 JPEG item（相机渲染的小图），出口恒 sRGB8
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解容器内嵌的 JPEG 缩略图 item（索尼 HIF 的 160×120 等）；没有该项或解不了返回 null。
    /// 位图自报色域按真实值读取后显式规范到 sRGB8，不用 <see cref="Bitmap.GetPixels"/> 的打包值。
    /// </summary>
    private static DecodedImage? LoadEmbeddedJpegThumbnail(byte[] data)
    {
        var jpeg = HeifJpegThumbnailExtractor.TryExtract(data);
        if (jpeg is null) return null;
        try
        {
            var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
            using var bitmap = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, options);
            if (bitmap == null) return null;
            using var normalized = AndroidBitmapPixels.ToRgba8(bitmap, ImageColorSpace.Srgb,
                bitmap.Width, bitmap.Height, 1);
            if (normalized == null) return null;
            Console.WriteLine($"[AndroidLibHeifDecoder] 缩略图 item: {bitmap.Width}x{bitmap.Height}，跳过全图解码");
            return AndroidBitmapPixels.ToBgra32(normalized);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[AndroidLibHeifDecoder] 缩略图 item 解码失败: {exception.Message}");
            return null;
        }
    }
}
