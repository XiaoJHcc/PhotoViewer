using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Tools;

/// <summary>
/// 工具页 ViewModel。持有各工具的内容 VM，管理首屏列表与子页面之间的导航。
/// </summary>
public class ToolsViewModel : ViewModelBase
{
    private ExifDetailViewModel? _exifDetail;
    private ReactiveObject? _currentTool;

    /// <summary>EXIF 详情 VM；ExifData 未加载时为 null。</summary>
    public ExifDetailViewModel? ExifDetail
    {
        get => _exifDetail;
        private set
        {
            this.RaiseAndSetIfChanged(ref _exifDetail, value);
            this.RaisePropertyChanged(nameof(IsExifDetailEnabled));
        }
    }

    /// <summary>EXIF 详情工具当前是否可进入。</summary>
    public bool IsExifDetailEnabled => _exifDetail != null;

    /// <summary>照片数据统计工具 VM。</summary>
    public PhotoStatsViewModel PhotoStats { get; }

    /// <summary>照片数据统计工具是否在当前平台可用（仅 Windows）。</summary>
    public bool IsPhotoStatsAvailable => OperatingSystem.IsWindows();

    /// <summary>DINO 诊断的平台中立 presenter；旧壳 VM 与 macOS 原生工具窗口共用同一实例，避免重复推理。</summary>
    public DinoDiagnosticPresenter DinoPresenter { get; }

    /// <summary>DINO 诊断工具 VM。</summary>
    public DinoDebugViewModel DinoDebug { get; }

    /// <summary>当前显示的子工具 VM；null 表示显示工具列表首页。</summary>
    public ReactiveObject? CurrentTool
    {
        get => _currentTool;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(CanGoBack));
        }
    }

    /// <summary>是否处于子工具页面（可返回首页列表）。</summary>
    public bool CanGoBack => _currentTool != null;

    /// <summary>初始化工具页 ViewModel。</summary>
    /// <param name="main">共享主 VM。</param>
    public ToolsViewModel(MainViewModel main)
    {
        PhotoStats = new PhotoStatsViewModel();
        DinoPresenter = new DinoDiagnosticPresenter();
        DinoDebug = new DinoDebugViewModel(DinoPresenter);
        _main = main;
        SyncCurrentFile(main.CurrentFile);
    }

    private readonly MainViewModel _main;

    /// <summary>导航到 EXIF 详情子页面。</summary>
    public void OpenExifDetail()
    {
        if (_exifDetail != null)
            CurrentTool = _exifDetail;
    }

    /// <summary>导航到照片数据统计子页面。</summary>
    public void OpenPhotoStats() => CurrentTool = PhotoStats;

    /// <summary>导航到 DINO 诊断子页面,并用当前图片触发一次推理。</summary>
    public void OpenDinoDebug()
    {
        CurrentTool = DinoDebug;
        DinoDebug.SetSource(_main.CurrentFile);
    }

    /// <summary>返回工具列表首页;同时取消 DINO 诊断的在途任务(TOOLS-06)。</summary>
    public void ShowList()
    {
        DinoPresenter.Cancel();
        CurrentTool = null;
    }

    /// <summary>
    /// 工具窗口/浮层关闭或离开 DINO 诊断页时调用:取消在途 DINO 任务,不在后台续算(TOOLS-06)。
    /// </summary>
    public void Deactivate() => DinoPresenter.Cancel();

    /// <summary>
    /// 工具窗口/浮层当前是否已展示;由平台壳在显示/关闭时写入,供主窗口判断入口是否只做前置(TOOLS-01)。
    /// </summary>
    public bool IsPresented { get; set; }

    /// <summary>当前图片或其 ExifData / 加载状态变化时同步所有工具状态。</summary>
    public void SyncCurrentFile(ImageFile? imageFile)
    {
        // TOOLS-02：EXIF 未加载完不算“无 EXIF”，保留当前页并显示加载状态。
        if (imageFile != null && (imageFile.ExifData != null || !imageFile.IsExifLoaded))
        {
            if (ExifDetail == null)
                ExifDetail = new ExifDetailViewModel(imageFile);
            else
                ExifDetail.UpdateToFile(imageFile);
        }
        else
        {
            ExifDetail = null;
            if (_currentTool is ExifDetailViewModel)
                ShowList();
        }

        // 仅当 DINO 诊断页正在显示时联动重算,避免后台无谓推理。
        if (_currentTool is DinoDebugViewModel)
            DinoDebug.SetSource(imageFile);
    }
}
