using ReactiveUI;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
    //////////////
    /// 星级设置
    //////////////

    private bool _showRating = true;
    public bool ShowRating
    {
        get => _showRating;
        set => this.RaiseAndSetIfChanged(ref _showRating, value);
    }

    private bool _safeSetRating = true;
    public bool SafeSetRating
    {
        get => _safeSetRating;
        set => this.RaiseAndSetIfChanged(ref _safeSetRating, value);
    }
}

