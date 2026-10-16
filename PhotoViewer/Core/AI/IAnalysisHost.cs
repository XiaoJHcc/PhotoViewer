using PhotoViewer.Core.Image;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 分析栏宿主状态。平台展示层把可见性、布局、当前图与展示位图上报给 <see cref="AnalysisPresenter"/>;
/// 任一状态变化后触发 <see cref="HostChanged"/>,presenter 据此重新路由当前照片。
/// </summary>
public interface IAnalysisHost
{
    /// <summary>分析栏是否可见;不可见时 presenter 取消在途任务并清空诊断瓦片。</summary>
    bool AnalysisVisible { get; }

    /// <summary>设置"分析诊断 / 即时计算DINO/CV"是否开启。</summary>
    bool ImmediateCompute { get; }

    /// <summary>当前照片。</summary>
    ImageFile? CurrentFile { get; }

    /// <summary>当前展示图(原片或增强图,代理期也返回);无图时为 null,直方图与未提取回退使用。</summary>
    DecodedImage? DisplayImage { get; }

    /// <summary><see cref="DisplayImage"/> 当前是否已是增强图;增强联动重算的判据。</summary>
    bool EnhancedDisplay { get; }

    /// <summary>缩略图通道是否忙;未提取回退让位判断使用。</summary>
    bool ThumbnailsBusy { get; }

    /// <summary>上述任一状态变化后在 UI 线程触发。</summary>
    event Action? HostChanged;
}
