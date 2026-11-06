using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Media;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Image.Heif;
using PhotoViewer.Core.Storage;
using AndroidColorSpace = Android.Graphics.ColorSpace;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 原生主图解码器（A2）：RAW 走 LibRaw（<see cref="AndroidRawDecoder"/>，全分辨率开发 + 内嵌预览顶替）；
/// HEIF 先走共享快速路径（容器自解析 + <c>pvhevc</c> 并行解 HEVC 瓦片），不可用时直接回落共享
/// <see cref="LibHeifColorDecoder"/> 产出目标色域的 8 位 RGBA（ICC 优先、nclx、高位深不先量化），
/// 不经系统 <c>BitmapFactory</c>（其 HEIF 输出即使读到 P3 ICC 也有灰阶偏差，不能当色彩出口）；
/// 其余格式走系统解码链。
/// 代理用 <c>InSampleSize</c> + 精确缩放；快速路径把容器方向交顶点阶段，libheif 回落则已按容器变换摆正，
/// 不再应用 HEIF 内的 EXIF Orientation 项。
/// 色彩：目标色域在每次解码开始时**捕获一次**并贯穿解码选项与产物标签，位图自报的真实色域再经
/// <see cref="ColorEngine"/> 规范到该目标（见 <see cref="AndroidBitmapPixels.ToRgba8"/>）；不做隐式降级，也不按请求标签硬标。
/// 同时实现 <see cref="IQuickPreviewDecoder"/>：RAW 首次解码空窗期用内嵌预览顶替（STATE-01）。
/// </summary>
public sealed class AndroidPresentationDecoder : IPresentationImageDecoder, IQuickPreviewDecoder
{
    /// <summary>pvraw 是否可用：0 未知 / 1 可用 / 2 已确认不可用（只记一次日志）。</summary>
    private static int _rawAvailability;

    /// <summary>RAW 开发是否可用（libpvraw 加载失败后不再重试，也不会静默回退内嵌预览）。</summary>
    private static bool RawAvailable => Volatile.Read(ref _rawAvailability) != 2;

    /// <summary>在线程池解码；代理限制长边，不使用容器内的小缩略图。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次必须产出的编码（Android 只支持 Rgba8）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        if (encoding != PixelEncoding.Rgba8)
            throw new NotSupportedException($"Android 展示解码器不支持编码 {encoding}");
        return Task.Run(() => Decode(file, maxLongEdge, cancellationToken), cancellationToken);
    }

    /// <summary>无内嵌预览的用户图片按短边缩解码；返回已旋转的 sRGB8 小图，不回退原图全尺寸。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="minShortEdge">目标短边（至少）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已转正方向的 sRGB8 小图。</returns>
    public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() =>
        {
            int targetLongEdge = Math.Max(minShortEdge, 1) * 3;
            // RAW：只解内嵌预览（已转正的正确朝向），绝不为此全分辨率开发。
            if (RawLoader.IsRawFile(file))
            {
                using var preview = DecodeRawPreview(file, ImageColorSpace.Srgb, cancellationToken);
                return preview == null ? null : ColorEngine.ToAnalysisSrgb8(preview, preview.Orientation);
            }
            if (HeifLoader.IsHeifFile(file))
                return DecodeHeifThumbnail(file, targetLongEdge, cancellationToken);

            // 缩略图出口是 sRGB8 分析口径，直接解到 sRGB 省掉一次整图色域转换。
            using var decoded = DecodeLegacy(file, targetLongEdge, ImageColorSpace.Srgb, cancellationToken);
            if (decoded == null) return null;
            return ColorEngine.ToAnalysisSrgb8(decoded);
        }, cancellationToken);

    /// <summary>RAW 是否有内嵌预览顶替（只看扩展名，不做 I/O）。</summary>
    /// <param name="file">文件引用。</param>
    /// <returns>RAW 且 libpvraw 可用时为 true。</returns>
    public bool HasQuickPreview(IFileRef file) => RawLoader.IsRawFile(file) && RawAvailable;

    /// <summary>
    /// STATE-01 顶替预览：解 RAW 内嵌预览（Sony 1616x1080），在线程池执行。
    /// 产物与正式解码同一展示编码与方向；失败返回 null（顶替是可选优化，不报错）。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未旋转的 RGBA8 预览；不可用时为 null。</returns>
    public Task<DecodedImage?> DecodeQuickPreviewAsync(IFileRef file, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            // 顶替图要与随后上屏的正式产物同一色域标签，故在此捕获一次。
            var preview = DecodeRawPreview(file, AndroidDisplayCapabilities.PresentationColorSpace, cancellationToken);
            if (preview != null)
                Console.WriteLine($"[Native] RAW 顶替预览上屏（{file.Name}）：{preview.Width}x{preview.Height}，"
                    + $"{started.Elapsed.TotalMilliseconds:F0} ms");
            return preview;
        }, cancellationToken);

    /// <summary>解 RAW 内嵌预览（展示编码 + 方向），pvraw 不可用/无预览时返回 null。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="target">目标色域（调用方捕获一次）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未旋转的 RGBA8 预览；调用方负责释放。</returns>
    private static DecodedImage? DecodeRawPreview(IFileRef file, ImageColorSpace target, CancellationToken cancellationToken)
    {
        if (!RawAvailable) return null;
        try
        {
            var preview = AndroidRawDecoder.DecodeEmbeddedPreview(file, target, cancellationToken);
            Volatile.Write(ref _rawAvailability, 1);
            return preview;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MarkRawUnavailable(exception);
            return null;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] RAW 内嵌预览失败（{file.Name}）：{exception.Message}");
            return null;
        }
    }

    /// <summary>RAW 开发（全分辨率或代理），pvraw 不可用/失败返回 null 由会话如实报错（SCOPE-04）。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="target">目标色域（调用方捕获一次）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未旋转的 RGBA8；失败为 null。</returns>
    private static DecodedImage? DecodeRaw(IFileRef file, int? maxLongEdge, ImageColorSpace target, CancellationToken cancellationToken)
    {
        if (!RawAvailable) return null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var image = AndroidRawDecoder.Decode(file, maxLongEdge, target, cancellationToken);
            Volatile.Write(ref _rawAvailability, 1);
            // 与"顶替预览上屏"日志配对，可核对真图确实晚于顶替图到达。
            Console.WriteLine(image == null
                ? $"[Native] RAW 解码无产物（{file.Name}），{started.Elapsed.TotalMilliseconds:F0} ms"
                : $"[Native] RAW 解码完成（{file.Name}）：{image.Width}x{image.Height}，{started.Elapsed.TotalMilliseconds:F0} ms");
            return image;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MarkRawUnavailable(exception);
            return null;
        }
    }

    /// <summary>libpvraw 加载失败：只记一次，后续不再尝试 RAW 解码（也不静默改用内嵌预览）。</summary>
    private static void MarkRawUnavailable(Exception exception)
    {
        if (Interlocked.Exchange(ref _rawAvailability, 2) == 2) return;
        Console.WriteLine($"[Native] libpvraw 不可用，RAW 解码停用：{exception.GetType().Name}: {exception.Message}");
    }


    /// <summary>同步解码主体（在线程池执行）：HEIF 先走快速路径，再走共享 libheif 回落，其余格式用系统解码链。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物；失败返回 null。</returns>
    private static DecodedImage? Decode(IFileRef file, int? maxLongEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 目标色域只在这里捕获一次，贯穿解码选项与产物标签：解码期间渲染线程改写 SurfaceIsP3
        // 也不会让"按 P3 编码的字节"被标成 sRGB（或反之）。
        var target = AndroidDisplayCapabilities.PresentationColorSpace;
        // RAW 不落 BitmapFactory：系统只会给出内嵌预览，违反代理精度规则。失败返回 null，由会话如实报错。
        if (RawLoader.IsRawFile(file))
            return DecodeRaw(file, maxLongEdge, target, cancellationToken);
        if (HeifLoader.IsHeifFile(file))
            return DecodeHeif(file, maxLongEdge, target, cancellationToken);
        return DecodeLegacy(file, maxLongEdge, target, cancellationToken);
    }

    /// <summary>
    /// 验收用：强制走共享 libheif 主图回落（跳过快速路径），供色彩回归对照快速路径；产品路径不经过这里。
    /// </summary>
    /// <returns>RGBA8 产物；解不了返回 null。</returns>
    internal static Task<DecodedImage?> DecodeHeifFallbackForChecksAsync(IFileRef file, int? maxLongEdge,
        ImageColorSpace target, CancellationToken cancellationToken)
        => Task.Run(() => DecodeHeifLibHeif(file, maxLongEdge, target, cancellationToken), cancellationToken);

    /// <summary>
    /// HEIF 主图回落：共享 libheif 软解，产物为显示朝向（方向 1）的 8 位 RGBA、色彩按文件真实声明转成捕获的目标色域。
    /// libheif 默认应用容器 <c>irot</c>/<c>imir</c>，源几何是全幅变换后的尺寸；PQ/HLG 与超出支持面的声明由共享解码器
    /// 拒绝并写 <see cref="HeifLoader.LastDecodeError"/>，不按 SDR 输出。
    /// </summary>
    private static DecodedImage? DecodeHeifLibHeif(IFileRef file, int? maxLongEdge, ImageColorSpace target,
        CancellationToken cancellationToken, byte[]? bytes = null)
    {
        var output = target == ImageColorSpace.DisplayP3 ? LibHeifOutput.Rgba8DisplayP3 : LibHeifOutput.Rgba8Srgb;
        try
        {
            bytes ??= FastHeifDecoder.ReadAllBytesAsync(file, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            string? reason = null;
            var decoded = HeifLoader.RunCpuDecodeAsync(() => Task.Run(() =>
            {
                var image = LibHeifColorDecoder.Decode(bytes, maxLongEdge, output, cancellationToken, out var failure);
                reason = failure;
                return image;
            }, cancellationToken)).GetAwaiter().GetResult();
            if (decoded == null)
                LogHeifFallback(file, "共享 libheif 回落：" + (reason ?? HeifLoader.LastDecodeError ?? "未知原因"));
            return decoded;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            LogHeifFallback(file, $"共享 libheif 回落失败（{exception.GetType().Name}: {exception.Message}）");
            return null;
        }
    }

    /// <summary>用系统解码器解出位图并规范到捕获的目标色域（RAW 之外的兜底路径）。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限。</param>
    /// <param name="target">目标色域（与产物标签同一来源）。</param>
    /// <param name="orientation">写给渲染层的待应用方向。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>RGBA8 产物；系统解不了返回 null。</returns>
    private static DecodedImage? DecodeBitmapToTarget(IFileRef file, int? maxLongEdge, ImageColorSpace target,
        int orientation, CancellationToken cancellationToken)
    {
        int sourceWidth = 0, sourceHeight = 0;
        using var bitmap = DecodeBitmap(file, maxLongEdge, target, out sourceWidth, out sourceHeight, cancellationToken);
        if (bitmap == null) return null;
        var scaled = maxLongEdge is { } limit ? ScaleToLimit(bitmap, limit) : null;
        try
        {
            // 像素只从原始 ARGB_8888 存储读，位图实际色域显式规范到捕获的目标色域。
            return AndroidBitmapPixels.ToRgba8(scaled ?? bitmap, target, sourceWidth, sourceHeight, orientation);
        }
        finally { scaled?.Dispose(); }
    }

    private static DecodedImage? DecodeHeif(IFileRef file, int? maxLongEdge, ImageColorSpace target, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            bytes = FastHeifDecoder.ReadAllBytesAsync(file, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            var fast = FastHeifDecoder.TryDecode(bytes, maxLongEdge, PixelEncoding.Rgba8, cancellationToken,
                out var reason, target);
            if (fast != null) return fast;
            LogHeifFallback(file, reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            LogHeifFallback(file, $"{exception.GetType().Name}: {exception.Message}");
        }
        return bytes == null ? null : DecodeHeifLibHeif(file, maxLongEdge, target, cancellationToken, bytes);
    }

    private static DecodedImage? DecodeHeifThumbnail(IFileRef file, int targetLongEdge, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            bytes = FastHeifDecoder.ReadAllBytesAsync(file, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            using var fast = FastHeifDecoder.TryDecode(bytes, targetLongEdge, PixelEncoding.Bgra32, cancellationToken, out var reason);
            if (fast != null) return ColorEngine.ToAnalysisSrgb8(fast, fast.Orientation);
            LogHeifFallback(file, reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            LogHeifFallback(file, $"{exception.GetType().Name}: {exception.Message}");
        }
        if (bytes == null) return null;
        using var fallback = DecodeHeifLibHeif(file, targetLongEdge, ImageColorSpace.Srgb, cancellationToken, bytes);
        return fallback == null ? null : ColorEngine.ToAnalysisSrgb8(fallback, fallback.Orientation);
    }

    /// <summary>已记录过回落原因的文件（限幅；回落原因只与容器能力相关，同一文件不必重复刷屏）。</summary>
    private static readonly HashSet<string> HeifFallbackLogged = new(StringComparer.Ordinal);

    /// <summary>记录一次 HEIF 快速路径回落（每文件只记第一次）。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="reason">回落原因。</param>
    private static void LogHeifFallback(IFileRef file, string? reason)
    {
        var key = file.StorageId.Value;
        lock (HeifFallbackLogged)
        {
            if (!HeifFallbackLogged.Add(key)) return;
            if (HeifFallbackLogged.Count > 256) HeifFallbackLogged.Clear();
        }

        Console.WriteLine($"[Native] HEIF 快速路径回落（{file.Name}）：{reason ?? "未知原因"}");
    }

    /// <summary>已记录过"JPEG 容器色域改共享解码"的文件（每文件只记第一次）。</summary>
    private static readonly HashSet<string> JpegColorLogged = new(StringComparer.Ordinal);

    /// <summary>记录一次 JPEG 容器色域接管（无 ICC 却声明广色域，系统链会丢标签）。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="sourceColorSpace">共享解码器解析出的真实源色域。</param>
    private static void LogJpegColorFallback(IFileRef file, ImageColorSpace sourceColorSpace)
    {
        var key = file.StorageId.Value;
        lock (JpegColorLogged)
        {
            if (!JpegColorLogged.Add(key)) return;
            if (JpegColorLogged.Count > 256) JpegColorLogged.Clear();
        }

        Console.WriteLine($"[Native] JPEG 容器声明源色域 {sourceColorSpace}，改共享 Skia 解码（{file.Name}）");
    }

    /// <summary>JPEG 等非 HEIF 格式的系统解码链（在线程池执行）。</summary>
    private static DecodedImage? DecodeLegacy(IFileRef file, int? maxLongEdge, ImageColorSpace target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            int orientation = ReadOrientation(file);
            var container = DecodeJpegWithContainerColor(file, maxLongEdge, target, orientation, cancellationToken,
                out bool handled);
            return handled ? container : DecodeBitmapToTarget(file, maxLongEdge, target, orientation, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 展示解码失败（{file.Name}）：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 无 ICC 但容器声明广色域的 JPEG 走共享 Skia 解码（系统链只用 ICC，会把这些码值当 sRGB），
    /// 再经 <see cref="ColorEngine"/> 规范到捕获的展示目标；其余 JPEG 与格式仍走系统链。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="target">目标色域（调用方捕获一次）。</param>
    /// <param name="orientation">EXIF 方向（共享解码器不消费，仍按系统链同一口径写进产物）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="handled">容器要求共享色彩解码时为 true；此时失败不得回到忽略声明的系统路径。</param>
    /// <returns>目标色域的产物；失败返回 null。</returns>
    private static DecodedImage? DecodeJpegWithContainerColor(IFileRef file, int? maxLongEdge, ImageColorSpace target,
        int orientation, CancellationToken cancellationToken, out bool handled)
    {
        handled = false;
        try
        {
            using var stream = file.OpenReadAsync().GetAwaiter().GetResult();
            if (!DeclaresWideGamutWithoutIcc(stream)) return null;
            handled = true;
            cancellationToken.ThrowIfCancellationRequested();

            using var decoded = maxLongEdge is { } limit
                ? SkiaImageCodec.DecodeToLongEdge(stream, limit)
                : SkiaImageCodec.Decode(stream);
            if (decoded == null) return null;
            cancellationToken.ThrowIfCancellationRequested();

            LogJpegColorFallback(file, decoded.ColorSpace);
            using var converted = ColorEngine.ToDisplayEncoding(decoded, PixelEncoding.Rgba8, target);
            return AndroidBitmapPixels.CopyWithGeometry(converted, target,
                decoded.SourceWidth, decoded.SourceHeight, orientation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] JPEG 容器色域解码失败（{file.Name}）：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 流是否为"无 ICC 但声明广色域"的 JPEG：只看标记段（读完恢复位置），非 JPEG、不可寻址、
    /// 有 ICC（系统链会自己做色彩管理）时都返回 false。
    /// </summary>
    /// <param name="stream">文件流；调用后位置不变。</param>
    /// <returns>需要共享解码器接管色域时为 true。</returns>
    private static bool DeclaresWideGamutWithoutIcc(System.IO.Stream stream)
    {
        if (!stream.CanSeek) return false;
        var metadata = JpegColorMetadata.Read(stream);
        return !metadata.HasIccProfile && metadata.ResolveColorSpace() != ImageColorSpace.Srgb;
    }

    /// <summary>用 BitmapFactory 解码（代理时按 2 的幂采样），并返回未旋转源尺寸。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限。</param>
    /// <param name="target">目标色域（与产物标签同一来源）。</param>
    /// <param name="sourceWidth">未旋转源宽。</param>
    /// <param name="sourceHeight">未旋转源高。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码位图；系统解不了时为 null。</returns>
    private static Bitmap? DecodeBitmap(IFileRef file, int? maxLongEdge, ImageColorSpace target,
        out int sourceWidth, out int sourceHeight, CancellationToken cancellationToken)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var stream = file.OpenReadAsync().GetAwaiter().GetResult())
            BitmapFactory.DecodeStream(stream, null, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
        sourceWidth = bounds.OutWidth;
        sourceHeight = bounds.OutHeight;
        cancellationToken.ThrowIfCancellationRequested();

        int sample = 1;
        if (maxLongEdge is { } limit)
        {
            while (Math.Max(sourceWidth, sourceHeight) / (sample * 2) >= limit) sample *= 2;
        }
        var options = new BitmapFactory.Options
        {
            InPreferredConfig = Bitmap.Config.Argb8888,
            // 位图像素按预乘 alpha 读取（与 RGBA8 展示编码口径一致）。
            InPremultiplied = true,
            InSampleSize = sample,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var named = target == ImageColorSpace.DisplayP3
                ? AndroidColorSpace.Named.DisplayP3
                : AndroidColorSpace.Named.Srgb;
            options.InPreferredColorSpace = AndroidColorSpace.Get(named!)!;
        }
        using var read = file.OpenReadAsync().GetAwaiter().GetResult();
        var bitmap = BitmapFactory.DecodeStream(read, null, options);
        if (bitmap == null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    /// <summary>把位图缩到长边上限（采样后可能仍略大），保持比例。</summary>
    /// <param name="bitmap">源位图。</param>
    /// <param name="limit">长边上限。</param>
    /// <returns>缩放后的新位图；无需缩放时返回 null。</returns>
    private static Bitmap? ScaleToLimit(Bitmap bitmap, int limit)
    {
        int longEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (longEdge <= limit) return null;
        double ratio = (double)limit / longEdge;
        int width = Math.Max(1, (int)Math.Round(bitmap.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(bitmap.Height * ratio));
        return Bitmap.CreateScaledBitmap(bitmap, width, height, true);
    }

    /// <summary>读取 EXIF 方向；本地路径与 content URI 都支持，失败按 1（不旋转）。</summary>
    /// <param name="file">文件引用。</param>
    /// <returns>1–8 的方向码。</returns>
    private static int ReadOrientation(IFileRef file)
    {
        try
        {
            int orientation;
            string? local = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local))
            {
                using var exif = new ExifInterface(local);
                orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            }
            else if (file.Path is { IsAbsoluteUri: true } path
                && string.Equals(path.Scheme, "content", StringComparison.OrdinalIgnoreCase))
            {
                var resolver = Application.Context.ContentResolver;
                if (resolver == null) return 1;
                var contentUri = AndroidUri.Parse(path.AbsoluteUri);
                if (contentUri == null) return 1;
                using var descriptor = resolver.OpenFileDescriptor(contentUri, "r");
                if (descriptor?.FileDescriptor == null) return 1;
                using var exif = new ExifInterface(descriptor.FileDescriptor);
                orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            }
            else return 1;
            return orientation is >= 2 and <= 8 ? orientation : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取 EXIF 方向失败（{file.Name}）：{exception.Message}");
            return 1;
        }
    }
}
