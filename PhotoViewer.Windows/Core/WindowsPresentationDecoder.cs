using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.Core.Image.Heif;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// Windows 原生主图解码器。
/// 与 macOS 的 MacPresentationDecoder 同契约:解出**未旋转**的 8 位/浮点像素,转成线性 RGBA16F,
/// 并把源尺寸与"未应用"的方向交给渲染层做顶点变换。
/// </summary>
/// <remarks>
/// HEIF 走 <see cref="FastHeifDecoder"/>(HEVC 瓦片并行解码),方向取容器的 <c>irot</c>/<c>imir</c>,
/// 按 ISO/IEC 23008-12 忽略 HEIF 的 EXIF Orientation 项;容器或色彩超出支持面时回落 libheif,
/// 后者已把像素按容器变换摆正,故回落路径的方向固定为 1。RAW 走系统 WIC RAW 引擎(见 <see cref="WindowsRawDecoder"/>)。
/// 色域正确性(ICC/ACM)不在本阶段范围,当前口径与旧壳一致(未打标签按 sRGB 解释)。
/// </remarks>
public sealed class WindowsPresentationDecoder : IPresentationImageDecoder
{
    /// <summary>本项目展示解码只产出线性 RGBA16F;其它编码显式拒绝,不做隐式降级。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限;null 表示全分辨率。</param>
    /// <param name="encoding">本次请求的像素编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        if (encoding != PixelEncoding.RgbaF16)
        {
            throw new ArgumentException($"Windows 主图解码只支持 {PixelEncoding.RgbaF16}，收到 {encoding}。", nameof(encoding));
        }

        if (HeifLoader.IsHeifFile(file))
        {
            return await DecodeHeifAsync(file, maxLongEdge, encoding, cancellationToken).ConfigureAwait(false);
        }

        if (RawLoader.IsRawFile(file))
        {
            return await DecodeRawAsync(file, maxLongEdge, cancellationToken).ConfigureAwait(false);
        }

        var exif = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var decoded = await DecodeSkiaAsync(file, maxLongEdge).ConfigureAwait(false);
        if (decoded == null)
        {
            return null;
        }

        try
        {
            return ColorEngine.ToScRgb16(
                decoded,
                decoded.SourceWidth,
                decoded.SourceHeight,
                exif?.OrientationValue ?? 1);
        }
        finally
        {
            decoded.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
    {
        var exif = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var targetLongEdge = TryComputeLongEdge(exif, minShortEdge);

        if (HeifLoader.IsHeifFile(file))
        {
            return await DecodeHeifThumbnailAsync(file, minShortEdge, targetLongEdge, cancellationToken)
                .ConfigureAwait(false);
        }

        var decoded = await DecodeSkiaAsync(file, targetLongEdge).ConfigureAwait(false);
        if (decoded == null)
        {
            return null;
        }

        DecodedImage? scaled = null;
        try
        {
            var effective = ShrinkToShortEdge(decoded, minShortEdge, targetLongEdge == null, out scaled);
            return ColorEngine.ToAnalysisSrgb8(effective, exif?.OrientationValue ?? 1);
        }
        finally
        {
            scaled?.Release();
            decoded.Release();
        }
    }

    /// <summary>
    /// HEIF 主图:快速路径(容器方向显式交给渲染层);不支持时回落 libheif(像素已摆正,方向 1)。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">长边上限;null 表示全分辨率。</param>
    /// <param name="encoding">目标编码(只接受 RgbaF16)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物;失败返回 null。</returns>
    private static async Task<DecodedImage?> DecodeHeifAsync(
        IFileRef file,
        int? maxLongEdge,
        PixelEncoding encoding,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadAllBytesAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string? reason = null;
        var fast = await Task.Run(
            () => FastHeifDecoder.TryDecode(bytes, maxLongEdge, encoding, cancellationToken, out reason),
            cancellationToken).ConfigureAwait(false);
        if (fast is not null)
        {
            return fast;
        }

        LogFallback(file, reason);
        var decoded = await HeifLoader.DecodeAsync(file, maxLongEdge).ConfigureAwait(false);
        if (decoded == null)
        {
            return null;
        }

        try
        {
            // libheif 默认已应用 irot/imir:像素为显示朝向,方向固定 1,源尺寸沿用其(已旋转)全尺寸。
            return ColorEngine.ToScRgb16(decoded, decoded.SourceWidth, decoded.SourceHeight, 1);
        }
        finally
        {
            decoded.Release();
        }
    }

    /// <summary>
    /// HEIF 缩略图:快速路径产出未旋转 sRGB8,按容器方向做到显示朝向;回落 libheif 时其产物已是显示朝向。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="minShortEdge">目标短边。</param>
    /// <param name="targetLongEdge">按 EXIF 尺寸折算的长边上限;null 表示尺寸未知。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>显示朝向的 sRGB8;失败返回 null。</returns>
    private static async Task<DecodedImage?> DecodeHeifThumbnailAsync(
        IFileRef file,
        int minShortEdge,
        int? targetLongEdge,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadAllBytesAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string? reason = null;
        var fast = await Task.Run(
            () => FastHeifDecoder.TryDecode(bytes, targetLongEdge, PixelEncoding.Bgra32, cancellationToken, out reason),
            cancellationToken).ConfigureAwait(false);

        if (fast is not null)
        {
            DecodedImage? fastScaled = null;
            try
            {
                var effective = ShrinkToShortEdge(fast, minShortEdge, targetLongEdge == null, out fastScaled);
                return ColorEngine.ToAnalysisSrgb8(effective, fast.Orientation);
            }
            finally
            {
                fastScaled?.Release();
                fast.Release();
            }
        }

        LogFallback(file, reason);
        var decoded = await HeifLoader.DecodeAsync(file, targetLongEdge).ConfigureAwait(false);
        if (decoded == null)
        {
            return null;
        }

        DecodedImage? scaled = null;
        try
        {
            var effective = ShrinkToShortEdge(decoded, minShortEdge, targetLongEdge == null, out scaled);
            return ColorEngine.ToAnalysisSrgb8(effective, 1);
        }
        finally
        {
            scaled?.Release();
            decoded.Release();
        }
    }

    /// <summary>
    /// RAW 主图:系统 WIC RAW 引擎全分辨率解码(按相机声明裁切),失败时退回相机写的整幅内嵌 JPEG;
    /// 两者都没有时如实返回 null(与 SCOPE-04 的"停在可得尺寸"口径一致,不拿小预览冒充全分辨率)。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">长边上限;null 表示全分辨率。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物;不可得返回 null。</returns>
    private static async Task<DecodedImage?> DecodeRawAsync(
        IFileRef file,
        int? maxLongEdge,
        CancellationToken cancellationToken)
    {
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine($"[WindowsPresentationDecoder] RAW 需要本地路径:{file.Name}");
            return null;
        }

        DecodedImage? source = null;
        int? orientation = null;
        var extension = Path.GetExtension(path);
        if (WindowsRawDecoder.IsAvailable(extension))
        {
            source = await Task.Run(() => WindowsRawDecoder.DecodeFull(path, cancellationToken, out orientation), cancellationToken)
                .ConfigureAwait(false);
        }

        if (source is null)
        {
            source = await Task.Run(() => WindowsRawDecoder.DecodeEmbeddedFullPreview(path), cancellationToken)
                .ConfigureAwait(false);
            if (source is null) return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (orientation is null)
        {
            var exif = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
            orientation = exif?.OrientationValue ?? 1;
        }

        DecodedImage? scaled = null;
        try
        {
            var effective = source;
            if (maxLongEdge is int target && Math.Max(source.Width, source.Height) > target)
            {
                // 精缩失败时用全分辨率产物:宁可多占内存,也不把低精度代理送上屏。
                scaled = SkiaImageCodec.ScaleToLongEdge(source, target);
                if (scaled is not null) effective = scaled;
            }

            return ColorEngine.ToScRgb16(effective, source.SourceWidth, source.SourceHeight, orientation.Value);
        }
        finally
        {
            scaled?.Release();
            source.Release();
        }
    }

    /// <summary>
    /// 解出未旋转的 8 位像素(非 HEIF/RAW):Skia 直接解码,代理走子采样 + Mitchell 精修。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">长边上限;null 表示全分辨率。</param>
    /// <returns>未旋转的解码产物;失败返回 null。</returns>
    private static async Task<DecodedImage?> DecodeSkiaAsync(IFileRef file, int? maxLongEdge)
    {
        await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
        return maxLongEdge is int target
            ? SkiaImageCodec.DecodeToLongEdge(stream, target)
            : SkiaImageCodec.Decode(stream);
    }

    /// <summary>
    /// 尺寸未知(EXIF 缺宽高)时把产物按短边精缩到目标;已知尺寸时解码阶段已按长边上限缩放,原样返回。
    /// </summary>
    /// <param name="decoded">未旋转的解码产物。</param>
    /// <param name="minShortEdge">目标短边。</param>
    /// <param name="sizeUnknown">是否走了"尺寸未知"分支。</param>
    /// <param name="scaled">调用方负责释放的精缩产物;未精缩时为 null。</param>
    /// <returns>实际应使用的图像。</returns>
    private static DecodedImage ShrinkToShortEdge(DecodedImage decoded, int minShortEdge, bool sizeUnknown, out DecodedImage? scaled)
    {
        scaled = null;
        if (!sizeUnknown) return decoded;

        // 不放大,也不把全尺寸图丢给分析层。
        var shortEdge = Math.Min(decoded.Width, decoded.Height);
        if (shortEdge <= minShortEdge) return decoded;

        var longEdge = (int)Math.Ceiling(Math.Max(decoded.Width, decoded.Height) * (double)minShortEdge / shortEdge);
        scaled = SkiaImageCodec.ScaleToLongEdge(decoded, longEdge)
            ?? throw new InvalidOperationException("缩略图精缩失败");
        return scaled;
    }

    /// <summary>读取整个文件的字节(HEIF 容器解析与瓦片码流都从这份缓冲切出)。</summary>
    /// <param name="file">文件引用。</param>
    /// <returns>文件字节。</returns>
    private static Task<byte[]> ReadAllBytesAsync(IFileRef file) => FastHeifDecoder.ReadAllBytesAsync(file);

    /// <summary>已记录过回落原因的文件名(限幅;回落原因只与容器能力相关,同一文件不必重复刷屏)。</summary>
    private static readonly HashSet<string> FallbackLogged = new(StringComparer.Ordinal);

    /// <summary>记录一次 HEIF 快速路径回落(每文件只记第一次,便于现场定位又不刷屏)。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="reason">回落原因。</param>
    private static void LogFallback(IFileRef file, string? reason)
    {
        var key = file.StorageId.Value;
        lock (FallbackLogged)
        {
            if (FallbackLogged.Contains(key)) return;
            if (FallbackLogged.Count >= 256) FallbackLogged.Clear();
            FallbackLogged.Add(key);
        }

        Console.WriteLine($"[WindowsPresentationDecoder] HEIF 回落 libheif({file.Name}):{reason ?? "未知原因"}");
    }

    /// <summary>
    /// 按 EXIF 尺寸把"短边 ≥ 目标"折算成长边上限;尺寸缺失时返回 null。
    /// </summary>
    /// <param name="exif">EXIF 数据</param>
    /// <param name="minShortEdge">目标短边</param>
    /// <returns>长边上限;null 表示尺寸未知</returns>
    private static int? TryComputeLongEdge(ExifData? exif, int minShortEdge)
    {
        if (exif?.ImageWidth is not int width || exif.ImageHeight is not int height || width <= 0 || height <= 0)
        {
            return null;
        }

        var shortEdge = Math.Min(width, height);
        return shortEdge <= minShortEdge
            ? Math.Max(width, height)
            : (int)Math.Ceiling(Math.Max(width, height) * (double)minShortEdge / shortEdge);
    }
}
