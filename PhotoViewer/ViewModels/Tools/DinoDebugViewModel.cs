using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>
/// DINO 诊断工具页 VM。计算与数据来源在平台中立的 <see cref="DinoDiagnosticPresenter"/>；
/// 本类只作为工具导航的页面标识，并把原生壳需要的操作转发给 presenter。
/// </summary>
public sealed class DinoDebugViewModel : ReactiveObject
{
    /// <summary>CV 网格可视化像素边长(16)。</summary>
    public const int CvGridPixels = DinoDiagnosticPresenter.CvGridPixels;

    /// <summary>DINO patch 图像素边长(32)。</summary>
    public const int PatchGridPixels = DinoDiagnosticPresenter.PatchGridPixels;

    private readonly DinoDiagnosticPresenter _presenter;

    /// <summary>是否正在后台计算。</summary>
    public bool IsBusy => _presenter.IsBusy;

    /// <summary>状态文本。</summary>
    public string Status => _presenter.Status;

    /// <summary>当前图片长宽比(宽/高)。</summary>
    public double AspectRatio => _presenter.AspectRatio;

    /// <summary>抖动矢量场(32×32 拖影线方向 / 绝对边宽 / 掩膜 + 图像对角线 D)。</summary>
    public ShakeField? ShakeField => _presenter.ShakeTile.ShakeField;

    /// <summary>刚体拟合的多行文本结果。</summary>
    public string RigidMotionText => _presenter.RigidMotionText;

    /// <summary>cosine 参考 patch 的 x(0..31)。</summary>
    public int RefGridX => _presenter.RefGridX;

    /// <summary>cosine 参考 patch 的 y(0..31)。</summary>
    public int RefGridY => _presenter.RefGridY;

    /// <summary>构造并桥接 presenter 变化。</summary>
    /// <param name="presenter">共享的 DINO 诊断 presenter（由工具壳持有，原生工具窗口共用同一实例）。</param>
    public DinoDebugViewModel(DinoDiagnosticPresenter presenter)
    {
        _presenter = presenter;
        _presenter.Changed += () =>
        {
            this.RaisePropertyChanged(nameof(IsBusy));
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(AspectRatio));
            this.RaisePropertyChanged(nameof(ShakeField));
            this.RaisePropertyChanged(nameof(RigidMotionText));
            this.RaisePropertyChanged(nameof(RefGridX));
            this.RaisePropertyChanged(nameof(RefGridY));
        };
    }

    /// <summary>切换源图片;计算由 presenter 在后台完成。</summary>
    /// <param name="file">目标照片。</param>
    public void SetSource(ImageFile? file) => _presenter.SetSource(file);

    /// <summary>点击任意瓦片:落十字准星 + 以点击位置作 cosine 参考点(映射到 32×32)。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    public void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);

    /// <summary>空闲区域点击:清空十字准星。</summary>
    public void ClearCrosshair() => _presenter.ClearCrosshair();
}
