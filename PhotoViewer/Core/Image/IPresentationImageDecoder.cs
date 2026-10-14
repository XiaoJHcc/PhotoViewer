using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>原生主图解码契约；由平台提供，缩略图和旧壳继续使用已有解码门面。</summary>
public interface IPresentationImageDecoder
{
    /// <summary>解码产物每像素字节数，供准入估算使用。</summary>
    int BytesPerPixel { get; }
    /// <summary>解码主图，非空长边表示代理；产物携带源尺寸、色彩和待应用方向。</summary>
    Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, CancellationToken cancellationToken);
    /// <summary>原生壳缺少内嵌预览时按短边子采样，返回已旋转的 sRGB8 小图，不解码全尺寸兜底。</summary>
    Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken);
}
