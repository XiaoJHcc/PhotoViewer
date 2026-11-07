using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>标准版 CV 诊断工具页的中性转发层。</summary>
public sealed class CvDebugViewModel : ReactiveObject
{
    public const int CvGridPixels = CvDiagnosticPresenter.CvGridPixels;
    private readonly CvDiagnosticPresenter _presenter;

    public CvDebugViewModel(CvDiagnosticPresenter presenter)
    {
        _presenter = presenter;
        _presenter.Changed += () =>
        {
            this.RaisePropertyChanged(nameof(IsBusy));
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(AspectRatio));
            this.RaisePropertyChanged(nameof(ShakeField));
            this.RaisePropertyChanged(nameof(RigidMotionText));
        };
    }

    public bool IsBusy => _presenter.IsBusy;
    public string Status => _presenter.Status;
    public double AspectRatio => _presenter.AspectRatio;
    public ShakeField? ShakeField => _presenter.ShakeTile.ShakeField;
    public string RigidMotionText => _presenter.RigidMotionText;
    public void SetSource(ImageFile? file) => _presenter.SetSource(file);
    public void OnTileClicked(double u, double v) => _presenter.OnTileClicked(u, v);
    public void ClearCrosshair() => _presenter.ClearCrosshair();
}
