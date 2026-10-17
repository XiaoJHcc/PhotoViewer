using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Windows.Core;

/// <summary>
/// Windows 原生主图解码器。
/// 与 macOS 的 MacPresentationDecoder 同契约:复用既有 Skia/LibHeif 解出**未旋转**的 8 位像素,
/// 经 skcms 转成线性 RGBA16F,并把源尺寸与"未应用"的 EXIF 方向交给渲染层做顶点变换。
/// 色域正确性(ICC/ACM)不在本阶段范围,当前口径与旧壳一致(未打标签按 sRGB 解释)。
/// </summary>
public sealed class WindowsPresentationDecoder : IPresentationImageDecoder
{
    /// <summary>本项目展示解码只产出线性 RGBA16F；其它编码显式拒绝，不做隐式降级。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次请求的像素编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        if (encoding != PixelEncoding.RgbaF16)
        {
            throw new ArgumentException($"Windows 主图解码只支持 {PixelEncoding.RgbaF16}，收到 {encoding}。", nameof(encoding));
        }

        var exif = await ExifLoader.LoadExifDataAsync(file).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var decoded = await DecodeUnrotatedAsync(file, maxLongEdge).ConfigureAwait(false);
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
        var decoded = await DecodeUnrotatedAsync(file, targetLongEdge).ConfigureAwait(false);
        if (decoded == null)
        {
            return null;
        }

        DecodedImage? scaled = null;
        try
        {
            var effective = decoded;
            if (targetLongEdge == null)
            {
                // EXIF 缺尺寸时只能全解码后按实际像素精缩;不放大,也不把全尺寸图丢给分析层。
                var shortEdge = Math.Min(decoded.Width, decoded.Height);
                if (shortEdge > minShortEdge)
                {
                    var longEdge = (int)Math.Ceiling(Math.Max(decoded.Width, decoded.Height) * (double)minShortEdge / shortEdge);
                    scaled = SkiaImageCodec.ScaleToLongEdge(decoded, longEdge)
                        ?? throw new InvalidOperationException($"{file.Name} 缩略图精缩失败");
                    effective = scaled;
                }
            }

            return ColorEngine.ToAnalysisSrgb8(effective, exif?.OrientationValue ?? 1);
        }
        finally
        {
            scaled?.Release();
            decoded.Release();
        }
    }

    /// <summary>
    /// 解出未旋转的 8 位像素:HEIF 走 libheif(无子采样,内部全解码 + Mitchell 精缩),其余走 Skia。
    /// </summary>
    /// <param name="file">文件引用</param>
    /// <param name="maxLongEdge">长边上限;null 表示全分辨率</param>
    /// <returns>未旋转的解码产物;失败返回 null</returns>
    private static async Task<DecodedImage?> DecodeUnrotatedAsync(IFileRef file, int? maxLongEdge)
    {
        if (HeifLoader.IsHeifFile(file))
        {
            return await HeifLoader.DecodeAsync(file, maxLongEdge).ConfigureAwait(false);
        }

        await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
        return maxLongEdge is int target
            ? SkiaImageCodec.DecodeToLongEdge(stream, target)
            : SkiaImageCodec.Decode(stream);
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
