using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 展示解码器的可选能力：为解码代价高的格式（已登记范围：RAW 初次解码）提供低分辨率顶替预览。
/// 未实现本接口的平台保持"切图即清旧图 + 延迟加载提示"的黑屏加载，行为与引入顶替前一致；
/// 顶替预览只在真解码空窗期上屏，进入缓存与上屏信号的永远是真产物。
/// </summary>
public interface IQuickPreviewDecoder
{
    /// <summary>该文件是否有顶替预览（只做扩展名/格式判断，不做 I/O）。</summary>
    /// <param name="file">文件引用。</param>
    /// <returns>可提供顶替预览时为 true。</returns>
    bool HasQuickPreview(IFileRef file);

    /// <summary>
    /// 解出顶替预览：展示编码与正式产物一致；<see cref="DecodedImage.SourceWidth"/>/<see cref="DecodedImage.SourceHeight"/>
    /// 必须是全分辨率源尺寸（IMAGE-01 的几何基准），<see cref="DecodedImage.Orientation"/> 为待渲染器应用的方向；失败返回 null。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>低分辨率展示产物（调用方负责释放），失败为 null。</returns>
    Task<DecodedImage?> DecodeQuickPreviewAsync(IFileRef file, CancellationToken cancellationToken);
}
