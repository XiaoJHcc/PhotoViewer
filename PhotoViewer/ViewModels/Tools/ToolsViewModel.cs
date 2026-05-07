using PhotoViewer.Core;
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
    public ToolsViewModel(MainViewModel main)
    {
        SyncCurrentFile(main.CurrentFile);
    }

    /// <summary>导航到 EXIF 详情子页面。</summary>
    public void OpenExifDetail()
    {
        if (_exifDetail != null)
            CurrentTool = _exifDetail;
    }

    /// <summary>返回工具列表首页。</summary>
    public void ShowList() => CurrentTool = null;

    /// <summary>当前图片或其 ExifData 变化时同步所有工具状态。</summary>
    public void SyncCurrentFile(ImageFile? imageFile)
    {
        if (imageFile?.ExifData == null)
        {
            ExifDetail = null;
            if (_currentTool is ExifDetailViewModel)
                ShowList();
            return;
        }

        if (ExifDetail == null)
            ExifDetail = new ExifDetailViewModel(imageFile);
        else
            ExifDetail.UpdateToFile(imageFile);
    }
}
