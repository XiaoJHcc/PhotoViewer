using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using System.ComponentModel;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 原生主屏（I3）：按共享 <see cref="MainScreenLayout"/> 与 <see cref="iOSMainScreenLayout"/> 拼合
/// 文件栏（筛选条 + 胶片栏）、主图区与控制栏，栏位可独立显隐并把空间让给主图；行/列/自动布局只看窗口方向，
/// 旋转、Split View、Stage Manager 的任意尺寸都重算几何。栏位背景铺到屏幕边缘、内容收在安全区内；
/// 主图仍由 <see cref="iOSPhotoView"/> 渲染，物理视口照旧上报共享展示会话。
/// 分析栏（I5）、输入与工具页（I4/I5）不在此实现：控制栏不生成对应入口，布局数学里保留其槽位。
/// </summary>
internal sealed class iOSNativeRootViewController : UIViewController
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly bool _automatedValidation;
    private readonly iOSPhotoView _photo = new(CGRect.Empty);
    private readonly UIView _photoPane = new();
    private readonly UIView _fileBar = new();
    private readonly UIView _fileDivider = new();
    private readonly UIView _controlBackground = new();
    private readonly UIView _controlDivider = new();
    private readonly UIScrollView _controlScroll = new();
    private readonly iOSControlBar _controlBar;
    private readonly iOSFilterBar _filterBar;
    private readonly iOSFilmstripView _filmstrip;
    private readonly UILabel _hintLabel = new();
    private readonly UILabel _hintDetail = new();
    private readonly UIButton _settingsButton;
    private iOSPaneFrames _panes;
    private ImageFile? _observedFile;
    private ImageFile[] _displayedFiles = [];
    private bool _sessionStarted;
    private bool _refreshPending;
    private bool _layingOut;

    /// <summary>主图宿主（验收读取视口、抓帧与手势状态）。</summary>
    internal iOSPhotoView Photo => _photo;
    /// <summary>胶片栏（验收读取可见卡片、偏移与定位动画）。</summary>
    internal iOSFilmstripView Filmstrip => _filmstrip;
    /// <summary>控制栏（验收读取分组、命令与评分按钮）。</summary>
    internal iOSControlBar ControlBar => _controlBar;
    /// <summary>筛选条（验收读取筛选控件几何）。</summary>
    internal iOSFilterBar FilterBar => _filterBar;
    /// <summary>文件栏背景视图。</summary>
    internal UIView FileBarView => _fileBar;
    /// <summary>主图分区视图。</summary>
    internal UIView PhotoPane => _photoPane;
    /// <summary>文件栏分隔线。</summary>
    internal UIView FileDividerView => _fileDivider;
    /// <summary>控制栏分隔线。</summary>
    internal UIView ControlDividerView => _controlDivider;
    /// <summary>控制栏系统滚动容器。</summary>
    internal UIScrollView ControlScroll => _controlScroll;
    /// <summary>最近一次布局算出的分区矩形（验收按几何核对，不重建模拟布局）。</summary>
    internal iOSPaneFrames Panes => _panes;
    /// <summary>空态提示与错误详情所在的分区视图。</summary>
    internal UIView HintHost => _photoPane;

    /// <summary>用共享主 VM 与展示会话构建原生主屏。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">共享展示加载会话（所有权在壳层，生命周期与根控制器一致）。</param>
    /// <param name="automatedValidation">是否程序化验收会话（不读写用户窗口偏好）。</param>
    public iOSNativeRootViewController(MainViewModel main, PhotoLoadSession session, bool automatedValidation = false)
    {
        _main = main;
        _session = session;
        _automatedValidation = automatedValidation;
        _filterBar = new iOSFilterBar(main);
        _filmstrip = new iOSFilmstripView(main);
        _controlBar = new iOSControlBar(ScheduleControlsRefresh);
        _settingsButton = CreateSettingsButton();
        // 订阅在构造期完成：启动参数（--pv-open / 外部打开）可能在根控制器上屏前就选中了首图，
        // 若等到 ViewWillAppear 再订阅会漏掉那次 CurrentFile 变化而永不加载。
        _session.Changed += OnSessionChanged;
        _photo.ViewportChanged += ReportViewport;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += OnScrollToCurrentRequested;
        _filmstrip.RateRequested += OnRateRequested;
        _observedFile = _main.CurrentFile;
        if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
        RestoreWindowPreferences();
    }

    /// <summary>搭建主屏视图树：主图分区、文件栏（筛选条 + 胶片栏）、控制栏、分隔线与空态提示。</summary>
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var root = View!;
        root.BackgroundColor = UIColor.FromRGB(18, 18, 18);
        _photoPane.BackgroundColor = UIColor.Black;
        _photo.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
        _photo.Frame = _photoPane.Bounds;
        _photoPane.AddSubview(_photo);
        _hintLabel.Text = HintText();
        _hintLabel.Font = UIFont.SystemFontOfSize(16);
        _hintLabel.TextColor = UIColor.SecondaryLabel;
        _hintLabel.TextAlignment = UITextAlignment.Center;
        _hintLabel.Lines = 0;
        _hintDetail.Font = UIFont.SystemFontOfSize(13);
        _hintDetail.TextColor = UIColor.SecondaryLabel;
        _hintDetail.TextAlignment = UITextAlignment.Center;
        _hintDetail.Lines = 0;
        _photoPane.AddSubview(_hintLabel);
        _photoPane.AddSubview(_hintDetail);
        _photoPane.AddGestureRecognizer(new UITapGestureRecognizer(OnPhotoTapped));
        _fileBar.BackgroundColor = UIColor.FromWhiteAlpha(.067f, 1);
        _controlBackground.BackgroundColor = UIColor.FromWhiteAlpha(.067f, 1);
        _fileDivider.BackgroundColor = UIColor.FromWhiteAlpha(.2f, 1);
        _controlDivider.BackgroundColor = UIColor.FromWhiteAlpha(.2f, 1);
        _controlScroll.BackgroundColor = UIColor.Clear;
        _controlScroll.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        _controlScroll.AddSubview(_controlBar);
        root.AddSubview(_photoPane);
        root.AddSubview(_fileBar);
        root.AddSubview(_controlBackground);
        root.AddSubview(_fileDivider);
        root.AddSubview(_controlDivider);
        root.AddSubview(_controlScroll);
        root.AddSubview(_filterBar);
        root.AddSubview(_filmstrip);
        root.AddSubview(_settingsButton);
        BuildControls();
        OnSessionChanged();
        UpdateHint();
        RefreshScreen();
        BootTrace.Mark("native shell: main screen ready");
    }

    /// <summary>布局完成后（首次有效视口）启动展示会话；此前的切图只登记当前文件。</summary>
    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        Layout();
        StartSession();
    }

    /// <summary>安全区变化（旋转、分屏、Stage Manager 拖拽）后重排内容区。</summary>
    public override void ViewSafeAreaInsetsDidChange()
    {
        base.ViewSafeAreaInsetsDidChange();
        View!.SetNeedsLayout();
    }

    /// <summary>释放会话与所有原生订阅（壳退出路径）。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnSessionChanged;
            _photo.ViewportChanged -= ReportViewport;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged;
            _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
            _main.ControlVM.PropertyChanged -= OnControlChanged;
            _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            _filmstrip.RateRequested -= OnRateRequested;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _controlBar.Dispose();
            _photo.Dispose();
            _session.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>按共享几何与真实安全区拼合分区，并把筛选条/胶片栏/控制栏摆进各自的分区。</summary>
    private void Layout()
    {
        if (_layingOut || !IsViewLoaded) return;
        _layingOut = true;
        try
        {
            var root = View!;
            var bounds = root.Bounds;
            double width = (double)bounds.Width, height = (double)bounds.Height;
            var safe = root.SafeAreaInsets;
            _main.UpdateScreenOrientation(width, height);
            bool row = _main.IsRowLayout;
            double leadingInset = (double)safe.Top + MainScreenLayout.FilterMargin;
            double filterMain = MainScreenLayout.FilterBarMainLength(row, leadingInset);
            bool filmVisible = _main.IsThumbnailViewVisible;
            bool controlVisible = _main.IsControlViewVisible;
            double fileMain = filmVisible ? MainScreenLayout.FileBarMainLength(row, filterMain, row ? 0 : (double)safe.Left) : 0;
            double availableMain = row
                ? Math.Max(1, width - (double)safe.Left - (double)safe.Right)
                : Math.Max(1, height - (double)safe.Top - (double)safe.Bottom);
            var measure = controlVisible ? _controlBar.Measure(row, availableMain) : CGSize.Empty;
            double controlMain = controlVisible ? MainScreenLayout.ControlBarMainLength(row ? (double)measure.Height : (double)measure.Width) : 0;
            // 分析栏（I5）交叉轴恒为 0：槽位保留在共享分区数学里，空间归主图。
            _panes = iOSMainScreenLayout.Compute(row, width, height, safe, fileMain, controlMain, 0);

            _photoPane.Frame = ToFrame(_panes.Photo);
            _photo.Frame = _photoPane.Bounds;
            _fileBar.Frame = ToFrame(_panes.FileBar);
            _fileBar.Hidden = !filmVisible;
            _fileDivider.Frame = ToFrame(_panes.FileDivider);
            _fileDivider.Hidden = !filmVisible;
            _controlBackground.Frame = ToFrame(_panes.ControlBackground);
            _controlBackground.Hidden = !controlVisible;
            _controlDivider.Frame = ToFrame(_panes.ControlDivider);
            _controlDivider.Hidden = !controlVisible;
            _controlScroll.Frame = ToFrame(_panes.ControlContent);
            _controlScroll.Hidden = !controlVisible;
            _filterBar.Hidden = !filmVisible;
            _filmstrip.Hidden = !filmVisible;

            if (filmVisible)
            {
                var content = _panes.FileContent;
                double contentWidth = row ? content.Width : MainScreenLayout.FileBarContentWidth;
                _filterBar.Frame = new CGRect(content.X, content.Y, contentWidth, filterMain);
                _filterBar.Arrange(row, contentWidth, leadingInset);
                _filmstrip.Frame = new CGRect(content.X, content.Y + filterMain, contentWidth, Math.Max(1, content.Height - filterMain));
            }
            bool axisChanged = _filmstrip.Horizontal != row;
            _filmstrip.SetAxis(row);
            // 方向变化属于显式定位条件（[FILM-04] 登记的原生增强），普通尺寸变化只约束偏移。
            if (axisChanged) _filmstrip.ScrollToCurrent();

            if (controlVisible)
            {
                double measuredMain = row ? (double)measure.Width : (double)measure.Height;
                _controlScroll.ShowsHorizontalScrollIndicator = row && measuredMain > availableMain;
                _controlScroll.ShowsVerticalScrollIndicator = !row && measuredMain > availableMain;
                _controlBar.Arrange(_controlScroll.Bounds.Size);
            }
            LayoutOverlays();
        }
        finally { _layingOut = false; }
    }

    /// <summary>摆好主图区的空态提示/错误详情与临时设置按钮（I4 上下文菜单落地后移除按钮）。</summary>
    private void LayoutOverlays()
    {
        if (!IsViewLoaded || _panes.Photo.Width <= 0) return;
        var pane = _photoPane.Bounds;
        double hintWidth = Math.Max(1, Math.Min(500, (double)pane.Width - 20));
        double hintLeft = ((double)pane.Width - hintWidth) / 2;
        bool hasDetail = _hintDetail.Text?.Length > 0;
        double hintTop = Math.Max(0, ((double)pane.Height - (hasDetail ? 80 : 20)) / 2);
        _hintLabel.Frame = new CGRect(hintLeft, hintTop, hintWidth, 20);
        _hintDetail.Frame = new CGRect(hintLeft, hintTop + 28, hintWidth, 52);
        var photo = _panes.Photo;
        var safe = View!.SafeAreaInsets;
        double top = Math.Max(photo.Y + 12, (double)safe.Top + 12);
        double right = Math.Min(photo.MaxX, (double)View.Bounds.Width - (double)safe.Right) - 12;
        _settingsButton.Frame = new CGRect(Math.Max(photo.X, right - 40), top, 40, 40);
    }

    /// <summary>共享布局矩形 → UIKit frame。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit frame。</returns>
    private static CGRect ToFrame(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>临时设置入口：I4 的长按上下文菜单落地前保持设置可达（共享命令表中没有设置命令）。</summary>
    /// <returns>配置完成的齿轮按钮。</returns>
    private UIButton CreateSettingsButton()
    {
        var button = new UIButton(new CGRect(0, 0, 40, 40));
        button.SetImage(iOSSymbols.Symbol("gearshape", 18), UIControlState.Normal);
        button.TintColor = UIColor.Label;
        button.BackgroundColor = UIColor.FromWhiteAlpha(.08f, .6f);
        button.Layer.CornerRadius = 6;
        button.AccessibilityLabel = "设置";
        button.TouchUpInside += (_, _) => _main.OpenSettingWindow();
        return button;
    }

    /// <summary>按共享设置与当前照片重建控制栏。</summary>
    private void BuildControls()
    {
        _controlBar.Rebuild(_main, Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); });
        Layout();
    }
    /// <summary>执行共享命令；本阶段未接入的命令（缩放预设等）不生成按钮，故不会走到这里。</summary>
    /// <param name="command">共享命令标识。</param>
    private void Execute(string command)
    {
        switch (command)
        {
            case "Open": _ = OpenFolderPickerAsync(); break;
            case "Previous": Move(-1); break;
            case "Next": Move(1); break;
            case "Exchange": if (_main.LastFile != null) _main.CurrentFile = _main.LastFile; break;
            case "Fit": _photo.FitToView(); break;
            case "ZoomInScale": _photo.ZoomBy(1.25); break;
            case "ZoomOutScale": _photo.ZoomBy(.8); break;
            case "ZoomInPreset": ZoomPreset(1); break;
            case "ZoomOutPreset": ZoomPreset(-1); break;
            default:
                if (command.StartsWith("SetRating", StringComparison.Ordinal) && int.TryParse(command[9..], out int rating) && _main.CurrentFile != null)
                    _ = RateAsync(_main.CurrentFile, rating);
                break;
        }
    }

    /// <summary>在已筛选列表中翻页；选图后显式定位（[FILM-01] 上一张/下一张），列表端点不环绕。</summary>
    /// <param name="offset">±1 的方向偏移。</param>
    private void Move(int offset)
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        int index = _main.CurrentFile == null ? -1 : files.IndexOf(_main.CurrentFile);
        int target = index + offset;
        if (target >= 0 && target < files.Count)
        {
            _main.CurrentFile = files[target];
            _main.FileVM.ThumbnailList.ScrollToCurrent();
        }
    }

    /// <summary>按用户预设选择邻近倍率。</summary>
    /// <param name="direction">正数放大、负数缩小。</param>
    private void ZoomPreset(int direction)
    {
        var values = _main.Settings.ScalePresets.Select(preset => preset.Value).Order().ToArray();
        double current = _photo.PhysicalScale;
        double? target = direction > 0
            ? values.Where(value => value > current + .001).Cast<double?>().FirstOrDefault()
            : values.Where(value => value < current - .001).Reverse().Cast<double?>().FirstOrDefault();
        if (target.HasValue) _photo.ZoomToPhysical(target.Value);
    }

    /// <summary>评分写回经共享 XMP/同步逻辑；失败保留原星级并给出可见错误。</summary>
    /// <param name="file">目标照片。</param>
    /// <param name="rating">星级。</param>
    internal async Task RateAsync(ImageFile file, int rating)
    {
        await _main.SetRatingAsync(file, rating);
        if (file.Rating != rating) Console.Error.WriteLine($"[Native] 无法写入星级：{file.Name}");
        BuildControls();
    }

    /// <summary>卡片星级按钮写回（[FILM-05] 点星不选择父卡片，改星不切主图）。</summary>
    /// <param name="file">目标照片。</param>
    /// <param name="rating">星级。</param>
    private void OnRateRequested(ImageFile file, int rating) => _ = RateAsync(file, rating);

    /// <summary>主图区点击：空态时打开文件夹选择器（与 Mac 空首屏点开文件同义）。</summary>
    /// <param name="recognizer">点击识别器。</param>
    private void OnPhotoTapped(UITapGestureRecognizer recognizer)
    {
        if (_session.Image == null) _ = OpenFolderPickerAsync();
    }

    /// <summary>原生文件夹选择器 → 共享文件源。</summary>
    internal async Task OpenFolderPickerAsync()
    {
        try
        {
            var selected = await FilePicker.PickFolderAsync("选择图片文件夹");
            if (selected == null) return;
            _photo.FitToView();
            await _main.FolderVM.OpenFolderAsync(selected);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
    }

    /// <summary>文件夹加载完成后刷新计数与列表。</summary>
    private void OnAllFilesChanged() => UiDispatcher.Post(RefreshScreen);

    /// <summary>当前照片变更：切换会话文件、跟踪 EXIF 到达、重建控制栏并刷新提示。</summary>
    /// <param name="sender">事件源（主 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            // 同步登记"取消此刻之前的定位"，让切图后立刻发起的显式定位（上一张/下一张）胜出（[FILM-01/02]）。
            _filmstrip.CancelScrollUpToNow();
            UiDispatcher.Post(() =>
            {
                if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
                _observedFile = _main.CurrentFile;
                if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
                _session.SetFile(_main.CurrentFile?.File);
                BuildControls();
                UpdateHint();
            });
            return;
        }
        if (args.PropertyName is not (nameof(MainViewModel.IsRowLayout) or nameof(MainViewModel.IsThumbnailViewVisible)
            or nameof(MainViewModel.IsControlViewVisible) or nameof(MainViewModel.IsAnalysisViewVisible))) return;
        UiDispatcher.Post(() =>
        {
            if (!_main.IsThumbnailViewVisible) _filmstrip.CancelScrollUpToNow();
            Layout();
            SaveWindowPreferences();
            // 重新显示胶片栏是显式定位条件（Mac 已登记的增强，iPad 同口径）。
            if (args.PropertyName == nameof(MainViewModel.IsThumbnailViewVisible) && _main.IsThumbnailViewVisible) _filmstrip.ScrollToCurrent();
        });
    }

    /// <summary>当前文件 EXIF/星级就绪后同步对焦点、控制栏与提示。</summary>
    /// <param name="sender">事件源（当前文件）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(() =>
    {
        if (!ReferenceEquals(sender, _observedFile)) return;
        if (args.PropertyName == nameof(ImageFile.ExifData)) UpdateFocusPoint();
        if (args.PropertyName is nameof(ImageFile.Rating) or nameof(ImageFile.ExifData)) BuildControls();
    });

    /// <summary>设置回写后重建按钮并重排（显隐/顺序/快捷键都影响控制栏）。</summary>
    /// <param name="sender">事件源（设置 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args) => ScheduleControlsRefresh();

    /// <summary>共享控制摘要变化通知。</summary>
    /// <param name="sender">事件源（控制 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnControlChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(_main.ControlVM.EnabledExifItems) or nameof(_main.ControlVM.EnabledControls)) ScheduleControlsRefresh();
    }

    /// <summary>合并同一轮设置/EXIF 变化产生的通知，避免重复重建全部原生按钮。</summary>
    private void ScheduleControlsRefresh()
    {
        if (_refreshPending || !IsViewLoaded) return;
        _refreshPending = true;
        UiDispatcher.Post(() =>
        {
            _refreshPending = false;
            BuildControls();
            _filmstrip.ReloadData();
            Layout();
            _filmstrip.ReportVisibleRange();
        });
    }

    /// <summary>列表原子替换后重载胶片栏并刷新计数（不主动居中，[FILM-01]）。</summary>
    /// <param name="sender">事件源（缩略图列表 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnFilesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(_main.FileVM.ThumbnailList.FilteredFiles)) return;
        var currentFiles = _main.FileVM.ThumbnailList.FilteredFiles;
        if (_displayedFiles.Length != currentFiles.Count || !_displayedFiles.SequenceEqual(currentFiles)) _filmstrip.CancelScroll();
        UiDispatcher.Post(() =>
        {
            var files = _main.FileVM.ThumbnailList.FilteredFiles;
            bool sameSequence = _displayedFiles.Length == files.Count && _displayedFiles.SequenceEqual(files);
            _displayedFiles = files.ToArray();
            if (!sameSequence) _filmstrip.ReloadData();
            RefreshScreen();
        });
    }

    /// <summary>筛选状态变化：同步筛选控件与计数宽度（不触发定位）。</summary>
    /// <param name="sender">事件源（筛选条 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(RefreshScreen);

    /// <summary>显式定位请求（上一张/下一张、打开文件、外部打开）。</summary>
    private void OnScrollToCurrentRequested() => _filmstrip.ScrollToCurrent();

    /// <summary>刷新筛选控件、计数与几何（计数文本宽度参与筛选条布局）。</summary>
    private void RefreshScreen()
    {
        if (!IsViewLoaded) return;
        _filterBar.Refresh();
        Layout();
        _filmstrip.ReportVisibleRange();
    }

    /// <summary>
    /// 视口首次有效后启动展示会话（先上报视口再发起解码：代理目标必须来自真实视口，
    /// 否则会话会把目标钳成 1 像素而解码出 1×1 的"合格代理"）。
    /// </summary>
    private void StartSession()
    {
        if (_sessionStarted || _photo.Bounds.Width < 1 || _photo.Bounds.Height < 1) return;
        _sessionStarted = true;
        ReportViewport();
        _session.SetFile(_main.CurrentFile?.File);
    }

    /// <summary>会话产物变化：产物直接交 Metal 上传（不建第二份主图缓冲），并刷新空态/错误提示。</summary>
    private void OnSessionChanged()
    {
        UpdateFocusPoint();
        _photo.SetImage(_session.Image);
        UpdateHint();
        LayoutOverlays();
    }

    /// <summary>EXIF 异步到达后同步对焦位置，避免主图先完成时对焦点仍是空值。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        _photo.FocusPoint = focus is { ImageWidth: > 0, ImageHeight: > 0 } position
            ? new CGPoint(position.FocusX / (double)position.ImageWidth, position.FocusY / (double)position.ImageHeight) : null;
    }

    /// <summary>把主图物理视口与细节需求上报共享会话（代理目标 = 2× 物理视口，物理 100% 触发 Full 热替换）。</summary>
    private void ReportViewport()
        => _session.UpdateViewport(
            (double)_photo.Bounds.Width * _photo.TraitCollection.DisplayScale,
            (double)_photo.Bounds.Height * _photo.TraitCollection.DisplayScale,
            _photo.NeedsFull);

    /// <summary>空态提示文案（空目录、可打开文件夹或加载失败）。</summary>
    /// <returns>提示文本；有照片且无错误时为空串。</returns>
    private string HintText() => _session.Error.Length > 0 ? "无法打开该图片"
        : _main.CurrentFile == null ? "点击打开文件夹" : "";

    /// <summary>同步空态提示与错误详情（有主图且无错误时两者都收起）。</summary>
    private void UpdateHint()
    {
        _hintLabel.Text = HintText();
        _hintDetail.Text = _session.Error;
        _hintLabel.Hidden = _hintLabel.Text.Length == 0;
        _hintDetail.Hidden = _session.Error.Length == 0;
    }

    /// <summary>恢复栏位显隐偏好（验收会话不读写用户偏好）。</summary>
    private void RestoreWindowPreferences()
    {
        if (_automatedValidation) return;
        var defaults = NSUserDefaults.StandardUserDefaults;
        if (defaults["PhotoViewer.Native.ShowFilm"] != null) _main.IsThumbnailViewVisible = defaults.BoolForKey("PhotoViewer.Native.ShowFilm");
        if (defaults["PhotoViewer.Native.ShowControls"] != null) _main.IsControlViewVisible = defaults.BoolForKey("PhotoViewer.Native.ShowControls");
    }

    /// <summary>保存栏位显隐偏好（验收会话不写用户偏好）。</summary>
    private void SaveWindowPreferences()
    {
        if (_automatedValidation) return;
        var defaults = NSUserDefaults.StandardUserDefaults;
        defaults.SetBool(_main.IsThumbnailViewVisible, "PhotoViewer.Native.ShowFilm");
        defaults.SetBool(_main.IsControlViewVisible, "PhotoViewer.Native.ShowControls");
    }
}
