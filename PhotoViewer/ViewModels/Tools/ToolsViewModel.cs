using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>工具页 ViewModel。持有各工具的内容 VM，管理首屏列表与子页面之间的导航。</summary>
public class ToolsViewModel : ViewModelBase
{
    private ExifDetailViewModel? _exifDetail;
    private ReactiveObject? _currentTool;

    public ExifDetailViewModel? ExifDetail
    {
        get => _exifDetail;
        private set
        {
            this.RaiseAndSetIfChanged(ref _exifDetail, value);
            this.RaisePropertyChanged(nameof(IsExifDetailEnabled));
        }
    }

    public bool IsExifDetailEnabled => _exifDetail != null;
    public PhotoStatsViewModel PhotoStats { get; }
    public bool IsPhotoStatsAvailable => OperatingSystem.IsWindows();

#if PHOTOVIEWER_DINO
    public DinoDiagnosticPresenter DinoPresenter { get; }
    public DinoDebugViewModel DinoDebug { get; }
    public ReactiveObject DiagnosticDebug => DinoDebug;
#else
    public CvDiagnosticPresenter CvPresenter { get; }
    public CvDebugViewModel CvDebug { get; }
    public ReactiveObject DiagnosticDebug => CvDebug;
#endif

    public ReactiveObject? CurrentTool
    {
        get => _currentTool;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(CanGoBack));
        }
    }

    public bool CanGoBack => _currentTool != null;

    public ToolsViewModel(MainViewModel main)
    {
        PhotoStats = new PhotoStatsViewModel();
#if PHOTOVIEWER_DINO
        DinoPresenter = new DinoDiagnosticPresenter();
        DinoDebug = new DinoDebugViewModel(DinoPresenter);
#else
        CvPresenter = new CvDiagnosticPresenter();
        CvDebug = new CvDebugViewModel(CvPresenter);
#endif
        _main = main;
        SyncCurrentFile(main.CurrentFile);
    }

    private readonly MainViewModel _main;

    public void OpenExifDetail()
    {
        if (_exifDetail == null) return;
        if (ReferenceEquals(_currentTool, DiagnosticDebug)) Deactivate();
        CurrentTool = _exifDetail;
    }

    public void OpenPhotoStats() => CurrentTool = PhotoStats;

    public void OpenDiagnostic()
    {
        CurrentTool = DiagnosticDebug;
        SetDiagnosticSource(_main.CurrentFile);
    }

#if PHOTOVIEWER_DINO
    public void OpenDinoDebug() => OpenDiagnostic();
#else
    public void OpenCvDebug() => OpenDiagnostic();
#endif

    public void ShowList()
    {
        Deactivate();
        CurrentTool = null;
    }

    public void Deactivate()
    {
#if PHOTOVIEWER_DINO
        DinoPresenter.Cancel();
#else
        CvPresenter.Cancel();
#endif
    }

    public bool IsPresented { get; set; }

    public void SyncCurrentFile(ImageFile? imageFile)
    {
        if (imageFile != null && (imageFile.ExifData != null || !imageFile.IsExifLoaded))
        {
            if (ExifDetail == null) ExifDetail = new ExifDetailViewModel(imageFile);
            else ExifDetail.UpdateToFile(imageFile);
        }
        else
        {
            ExifDetail = null;
            if (_currentTool is ExifDetailViewModel) ShowList();
        }

        if (ReferenceEquals(_currentTool, DiagnosticDebug)) SetDiagnosticSource(imageFile);
    }

    private void SetDiagnosticSource(ImageFile? imageFile)
    {
#if PHOTOVIEWER_DINO
        DinoDebug.SetSource(imageFile);
#else
        CvDebug.SetSource(imageFile);
#endif
    }
}
