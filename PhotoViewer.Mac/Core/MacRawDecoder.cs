using System;
using System.Threading.Tasks;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Mac.Core;

/// <summary>
/// macOS RAW 解码器：复用 <see cref="MacHeifDecoder"/> 的 ImageIO 解码路径。
/// ImageIO 对 RAW 文件透明地走系统 RAW 引擎渲染（含 EXIF 方向），
/// 支持的机型/压缩格式以当前 macOS 版本为准；解码失败时记录原因到 <see cref="RawLoader.LastDecodeError"/>。
/// </summary>
public sealed class MacRawDecoder : IRawDecoder
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>ImageIO RAW 引擎输出即显示朝向。</summary>
    public bool OutputPreRotated => true;

    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
        => Task.Run(() => Decode(file, maxSize));

    private static DecodedImage? Decode(IFileRef file, int? maxSize)
    {
        // P1-M4:maxSize 非空走子采样代理路径(ImageIO 对 RAW 同样按目标尺寸渲染,省峰值)
        var bmp = maxSize.HasValue
            ? MacHeifDecoder.DecodeSubsampledWithImageIO(file.PathKey, maxSize.Value)
            : MacHeifDecoder.DecodeWithImageIO(file.PathKey, null);
        if (bmp == null)
            RawLoader.SetLastDecodeError("ImageIO 无法解码该 RAW 文件（当前 macOS 版本可能不支持该机型或压缩格式）");
        return bmp;
    }
}
