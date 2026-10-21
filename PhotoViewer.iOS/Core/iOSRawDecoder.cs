using System;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS RAW 解码器：复用 <see cref="iOSHeifDecoder"/> 的 ImageIO 解码路径。
/// ImageIO 对 RAW 文件透明地走系统 RAW 引擎渲染（含 EXIF 方向），
/// 支持的机型/压缩格式以当前 iOS 版本为准；解码失败时记录原因到 <see cref="RawLoader.LastDecodeError"/>。
/// </summary>
[Preserve(AllMembers = true)]
public sealed class iOSRawDecoder : IRawDecoder
{
    public bool IsSupported => OperatingSystem.IsIOS();

    /// <summary>ImageIO RAW 引擎输出即显示朝向。</summary>
    public bool OutputPreRotated => true;

    public async Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxSize)
    {
        var (path, data) = await iOSHeifDecoder.PreparePathOrDataAsync(file);
        // maxSize 非空走子采样代理路径(ImageIO 对 RAW 同样按目标尺寸渲染,省峰值)
        var bmp = maxSize.HasValue
            ? iOSHeifDecoder.DecodeSubsampledWithImageIO(path, maxSize.Value, data)
            : iOSHeifDecoder.DecodeWithImageIO(path, null, data);
        if (bmp == null && !maxSize.HasValue)
            RawLoader.SetLastDecodeError("ImageIO 无法解码该 RAW 文件（当前 iOS 版本可能不支持该机型或压缩格式）");
        return bmp;
    }
}
