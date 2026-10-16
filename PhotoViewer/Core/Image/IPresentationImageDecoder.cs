using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>原生主图解码契约；由平台提供，缩略图和旧壳继续使用已有解码门面。</summary>
public interface IPresentationImageDecoder
{
    /// <summary>解码主图，非空长边表示代理；编码由 <see cref="IPresentationEncodingPolicy"/> 决定，不支持时显式抛出。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次必须产出的像素编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken);
    /// <summary>原生壳缺少内嵌预览时按短边子采样，返回已旋转的 sRGB8 小图，不解码全尺寸兜底。</summary>
    Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken);
}
