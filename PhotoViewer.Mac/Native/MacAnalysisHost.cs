using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生分析栏宿主状态：把主 VM 的可见性 / 布局 / 当前图与原生展示会话的位图上报给
/// <see cref="AnalysisPresenter"/>，与旧壳 <c>AnalysisViewModel</c> 使用同一份编排逻辑。
/// 原生展示图是 GPU 方向未应用的 F16 线性表面，presenter 按需转换到分析口径。
/// </summary>
internal sealed class MacAnalysisHost : IAnalysisHost
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;

    /// <summary>构造。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">原生展示加载会话。</param>
    public MacAnalysisHost(MainViewModel main, PhotoLoadSession session)
    {
        _main = main;
        _session = session;
    }

    /// <summary>分析栏是否可见。</summary>
    public bool AnalysisVisible => _main.IsAnalysisViewVisible;

    /// <summary>设置「即时计算DINO/CV」是否开启。</summary>
    public bool ImmediateCompute => _main.Settings.AnalysisImmediateCompute;

    /// <summary>当前照片。</summary>
    public ImageFile? CurrentFile => _main.CurrentFile;

    /// <summary>当前展示图（原片或增强图，代理期也返回）。</summary>
    public DecodedImage? DisplayImage => _session.Image;

    /// <summary>展示图是否已是增强图。</summary>
    public bool EnhancedDisplay => _session.EnhancedDisplay;

    /// <summary>缩略图通道是否忙。</summary>
    public bool ThumbnailsBusy => _main.FileVM.ThumbnailList.IsThumbnailLoadingBusy();

    /// <summary>任一宿主状态变化。</summary>
    public event Action? HostChanged;

    /// <summary>上报宿主状态变化。</summary>
    public void Raise() => HostChanged?.Invoke();
}
