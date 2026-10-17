using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using System.ComponentModel;
using UIKit;
using UniformTypeIdentifiers;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// iPad 原生主屏（I3/I4）：按共享 <see cref="MainScreenLayout"/> 与 <see cref="iOSMainScreenLayout"/> 拼合
/// 文件栏（筛选条 + 胶片栏）、主图区与控制栏，栏位可独立显隐并把空间让给主图；行/列/自动布局只看窗口方向，
/// 旋转、Split View、Stage Manager 的任意尺寸都重算几何。栏位背景铺到屏幕边缘、内容收在安全区内；
/// 主图仍由 <see cref="iOSPhotoView"/> 渲染，物理视口照旧上报共享展示会话。
/// 输入（I4）：主图长按/辅助点按上下文菜单、硬件键盘（HID → 共享手势 → 共享命令表，INPUT-01 守卫）、
/// 离散滚轮按用户绑定、适应态横向轻扫翻页、从"文件"拖入。
/// P3 功能（I5）：分析栏（细节预览 / 直方图 / DINO-CV 诊断，可见时主图升 Full）、相似聚类面板、
/// 增强预览（<see cref="PhotoLoadSession"/>）与工具页（sheet + 导航控制器）；细节预览手指点按定格主图绿框。
/// </summary>
internal sealed class iOSNativeRootViewController : UIViewController
{
    /// <summary>按住方向键的首次重复延迟（秒）；iOS 无读取系统键盘重复速率的公开接口。</summary>
    private const double KeyRepeatDelay = .35;
    /// <summary>按住方向键的重复间隔（秒）。</summary>
    private const double KeyRepeatInterval = .06;

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
    private readonly iOSAnalysisPanel _analysisPane;
    private readonly iOSSimilarityPanel _similarityPane;
    private readonly UIView _detailHighlight = new();
    private readonly UILabel _hintLabel = new();
    private readonly UILabel _hintDetail = new();
    private readonly iOSContextMenuDelegate _photoMenu;
    private readonly UIDropInteraction _drop;
    private readonly HashSet<NSObject> _editingTextInputs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, ulong> _heldKeys = [];
    private NSObject? _textEditBeginField;
    private NSObject? _textEditBeginView;
    private NSObject? _textEditEndField;
    private NSObject? _textEditEndView;
    private NSTimer? _repeatDelay;
    private NSTimer? _repeatTimer;
    private int _repeatUsage = -1;
    private ulong _repeatModifiers;
    private double _detailU = .5, _detailV = .5;
    private double? _detailFrameWidth, _detailFrameHeight;
    private bool _detailVisible;
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
    /// <summary>主图上下文菜单委托（验收经系统入口读取菜单内容与回调）。</summary>
    internal iOSContextMenuDelegate PhotoMenuDelegate => _photoMenu;
    /// <summary>拖放交互（验收核对委托在根视图上）。</summary>
    internal UIDropInteraction DropInteraction => _drop;
    /// <summary>分析栏（验收读取瓦片几何、presenter 状态与细节预览）。</summary>
    internal iOSAnalysisPanel AnalysisPane => _analysisPane;
    /// <summary>相似聚类面板（验收读取卡片、交叉轴与列表态）。</summary>
    internal iOSSimilarityPanel SimilarityPane => _similarityPane;
    /// <summary>主图绿框叠加层（细节预览 hover / 定格的联动目标）。</summary>
    internal UIView DetailHighlightView => _detailHighlight;
    /// <summary>主图绿框当前是否可见。</summary>
    internal bool DetailHighlightVisible => !_detailHighlight.Hidden;
    /// <summary>主图分区沿主轴的长度（行布局取高度，列布局取宽度），供验收核对栏位显隐与让位。</summary>
    /// <param name="row">是否行布局。</param>
    /// <returns>主轴长度。</returns>
    internal double PhotoMainLength(bool row) => row ? (double)_photoPane.Frame.Height : (double)_photoPane.Frame.Width;

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
        _analysisPane = new iOSAnalysisPanel(main, session);
        _analysisPane.DetailHighlight = UpdateDetailHighlight;
        _analysisPane.DetailFocusRequested = FocusDetailPoint;
        _similarityPane = new iOSSimilarityPanel(main) { Hidden = true };
        _controlBar = new iOSControlBar(ScheduleControlsRefresh);
        _photoMenu = new iOSContextMenuDelegate(_ => BuildPhotoMenu());
        _drop = new UIDropInteraction(new iOSDropDelegate(this));
        // 订阅在构造期完成：启动参数（--pv-open / 外部打开）可能在根控制器上屏前就选中了首图，
        // 若等到 ViewWillAppear 再订阅会漏掉那次 CurrentFile 变化而永不加载。
        _session.Changed += OnSessionChanged;
        _photo.ViewportChanged += ReportViewport;
        _photo.ViewportChanged += ApplyDetailHighlight;
        _photo.SwipeRequested += OnSwipeRequested;
        _photo.WheelScrolled += OnWheelScrolled;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += OnScrollToCurrentRequested;
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
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
        // 细节预览绿框：3 点绿色描边，纯展示、不拦触摸。
        _detailHighlight.UserInteractionEnabled = false;
        _detailHighlight.Layer.BorderColor = UIColor.FromRGB(0, 255, 0).CGColor;
        _detailHighlight.Layer.BorderWidth = 3;
        _detailHighlight.Hidden = true;
        _photoPane.AddSubview(_detailHighlight);
        _photoPane.AddGestureRecognizer(new UITapGestureRecognizer(OnPhotoTapped));
        // 主图上下文菜单：手指长按与触控板/鼠标辅助点按同一入口；滚动/捏合/双击由各自的识别器负责，互不抢占。
        _photoPane.AddInteraction(new UIContextMenuInteraction(_photoMenu));
        SubscribeTextEditing();
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
        root.AddSubview(_similarityPane);
        root.AddSubview(_analysisPane);
        // 拖入：整个主屏都接收"文件"里的文件/文件夹（与 Mac 根视图同一语义）。
        root.AddInteraction(_drop);
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

    /// <summary>主屏是硬件键盘的默认第一响应者（设置页收起、文本输入结束后都重新取得焦点）。</summary>
    public override bool CanBecomeFirstResponder => true;

    /// <summary>重新可见时取回键盘焦点：设置 sheet 关闭后由系统调用，保证快捷键立即可用。</summary>
    /// <param name="animated">系统动画标记。</param>
    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        BecomeFirstResponder();
    }

    /// <summary>
    /// 硬件键盘按下：HID usage → 共享手势 → 用户主/次绑定 → 共享命令表（与 Mac 同一张表）。
    /// 已按住的键再次到来视为系统按键重复，忽略之——重复由本类计时器统一驱动，避免双倍步进。
    /// </summary>
    /// <param name="presses">本次按下的按键集合。</param>
    /// <param name="evt">按键事件。</param>
    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        bool consumed = false;
        foreach (var press in presses)
        {
            if (press.Key is not { } key) continue;
            int usage = (int)key.KeyCode;
            if (_heldKeys.ContainsKey(usage))
            {
                // 系统按键重复：已消费过，重复节奏由本类计时器统一驱动。
                consumed = true;
                continue;
            }
            ulong modifiers = (ulong)key.ModifierFlags;
            if (!TryExecuteKey(usage, modifiers)) continue;
            consumed = true;
            _heldKeys[usage] = modifiers;
            StartKeyRepeat(usage, modifiers);
        }
        if (!consumed) base.PressesBegan(presses, evt);
    }

    /// <summary>硬件键盘抬起：停止该键的重复计时；未登记的按键交还系统。</summary>
    /// <param name="presses">本次抬起的按键集合。</param>
    /// <param name="evt">按键事件。</param>
    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        bool handled = false;
        foreach (var press in presses)
        {
            if (press.Key is not { } key) continue;
            int usage = (int)key.KeyCode;
            if (!_heldKeys.Remove(usage)) continue;
            if (_repeatUsage == usage) StopKeyRepeat();
            handled = true;
        }
        if (!handled) base.PressesEnded(presses, evt);
    }

    /// <summary>系统取消按键（来电、切后台）：清空按住状态，避免重复计时器空转。</summary>
    /// <param name="presses">被取消的按键集合。</param>
    /// <param name="evt">按键事件。</param>
    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        ClearHeldKeys();
        base.PressesCancelled(presses, evt);
    }

    /// <summary>
    /// 键盘派发入口（<see cref="PressesBegan"/> 与验收共用）：INPUT-01 守卫之后按共享映射查用户绑定，
    /// 只执行本壳已实现（且控制栏可见性同一口径）的命令。
    /// </summary>
    /// <param name="usage">HID usage。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <returns>是否命中并执行了命令。</returns>
    internal bool TryExecuteKey(int usage, ulong modifierFlags)
    {
        if (KeyboardInputBlocked) return false;
        var gesture = AppleInputMapping.FromHidUsage(usage, modifierFlags, _main.Settings);
        string? command = AppleInputMapping.CommandForGesture(gesture, _main.Settings);
        if (command == null || !iOSControlBar.IsCommandAvailable(command) || AppleSymbolNames.ForCommand(command) == null) return false;
        Execute(command);
        return true;
    }

    /// <summary>
    /// INPUT-01：输入控件获焦或系统模态（设置页、文档选择器、上下文菜单提交页）展示期间，
    /// 照片快捷键一律不派发。
    /// </summary>
    internal bool KeyboardInputBlocked => PresentedViewController != null || _editingTextInputs.Count > 0;

    /// <summary>释放会话与所有原生订阅（壳退出路径）。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnSessionChanged;
            _photo.ViewportChanged -= ReportViewport;
            _photo.SwipeRequested -= OnSwipeRequested;
            _photo.WheelScrolled -= OnWheelScrolled;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged;
            _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
            _main.ControlVM.PropertyChanged -= OnControlChanged;
            _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
            _filmstrip.RateRequested -= OnRateRequested;
            _photo.ViewportChanged -= ApplyDetailHighlight;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            ClearHeldKeys();
            var center = NSNotificationCenter.DefaultCenter;
            if (_textEditBeginField != null) center.RemoveObserver(_textEditBeginField);
            if (_textEditBeginView != null) center.RemoveObserver(_textEditBeginView);
            if (_textEditEndField != null) center.RemoveObserver(_textEditEndField);
            if (_textEditEndView != null) center.RemoveObserver(_textEditEndView);
            _textEditBeginField = _textEditBeginView = _textEditEndField = _textEditEndView = null;
            _controlBar.Dispose();
            _analysisPane.Dispose();
            _similarityPane.Dispose();
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
            double availableMain = row
                ? Math.Max(1, width - (double)safe.Left - (double)safe.Right)
                : Math.Max(1, height - (double)safe.Top - (double)safe.Bottom);
            // 相似聚类：行布局从胶片栏右侧切出一段（含旧壳 MaxWidth 规则），列布局扩宽文件栏。
            bool similarityOpen = filmVisible && _main.FileVM.FilterBar.IsSimilarityPanelOpen;
            if (similarityOpen) _similarityPane.SetLayout(row, row ? MainScreenLayout.FilmstripBand : availableMain);
            double similarityColumn = similarityOpen
                ? (row
                    ? SimilarityPanelGeometry.RowWidth(_main.FileVM.SimilarityPanel.HasItems, _main.FileVM.SimilarityPanel.SimilarItems.Count, availableMain)
                    : _similarityPane.CrossSize + SimilarityPanelGeometry.Margin)
                : 0;
            _similarityPane.Hidden = !similarityOpen;
            double fileMain = filmVisible
                ? MainScreenLayout.FileBarMainLength(row, filterMain, row ? 0 : (double)safe.Left + similarityColumn)
                : 0;
            var measure = controlVisible ? _controlBar.Measure(row, availableMain) : CGSize.Empty;
            double controlMain = controlVisible ? MainScreenLayout.ControlBarMainLength(row ? (double)measure.Height : (double)measure.Width) : 0;
            // 分析栏占主图与相邻栏位之间的一条：行布局在主图下方、列布局在主图右侧，
            // 主轴可用长度取安全区内的内容长度，交叉轴尺寸（瓦片边长 + 20）与旧壳 AnalysisView 同口径。
            bool analysisVisible = _main.IsAnalysisViewVisible;
            double analysisSize = analysisVisible ? _analysisPane.CrossSize(availableMain) : 0;
            _panes = iOSMainScreenLayout.Compute(row, width, height, safe, fileMain, controlMain, analysisSize);

            _photoPane.Frame = ToFrame(_panes.Photo);
            _photo.Frame = _photoPane.Bounds;
            _analysisPane.Frame = ToFrame(_panes.Analysis);
            _analysisPane.Hidden = !analysisVisible;
            if (analysisVisible) _analysisPane.Arrange(row, availableMain, row ? (double)safe.Left : (double)safe.Top);
            else
            {
                _analysisPane.ClearDetailHighlight();
                HideDetailHighlight();
            }
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
                double filmWidth = row ? Math.Max(1, contentWidth - similarityColumn) : contentWidth;
                _filmstrip.Frame = new CGRect(content.X, content.Y + filterMain, filmWidth, Math.Max(1, content.Height - filterMain));
                if (similarityOpen)
                {
                    _similarityPane.Frame = row
                        ? new CGRect(content.X + contentWidth - similarityColumn, content.Y + filterMain,
                            Math.Max(1, similarityColumn - SimilarityPanelGeometry.Margin), (nfloat)_similarityPane.CrossSize)
                        : new CGRect(content.X + MainScreenLayout.FileBarContentWidth, content.Y + filterMain,
                            (nfloat)_similarityPane.CrossSize, Math.Max(1, content.Height - filterMain));
                    _similarityPane.LayoutIfNeeded();
                }
            }
            ApplyDetailHighlight();
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

    /// <summary>摆好主图区的空态提示与错误详情（设置入口已由长按上下文菜单承担）。</summary>
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
    }

    /// <summary>共享布局矩形 → UIKit frame。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>UIKit frame。</returns>
    private static CGRect ToFrame(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>按共享设置与当前照片重建控制栏（验收与设置回写共用同一入口）。</summary>
    internal void BuildControls()
    {
        _controlBar.Rebuild(_main, Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); }, ShowTools);
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
            case "ToggleAnalysisView": _main.ToggleAnalysisView(); break;
            case "ToggleEnhance": _session.ToggleEnhanced(); break;
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

    /// <summary>
    /// 主图区点击：细节预览定格绿框时先解除（"点空白清除"）；否则空态时打开文件夹选择器
    /// （与 Mac 空首屏点开文件同义）。
    /// </summary>
    /// <param name="recognizer">点击识别器。</param>
    private void OnPhotoTapped(UITapGestureRecognizer recognizer) => TapPhoto();

    /// <summary>主图点按入口（识别器与验收共用）：解除定格绿框优先，其次空态打开文件夹。</summary>
    internal void TapPhoto()
    {
        if (_detailVisible || !_detailHighlight.Hidden)
        {
            _analysisPane.ClearDetailHighlight();
            HideDetailHighlight();
            return;
        }
        if (_session.Image == null) _ = OpenFolderPickerAsync();
    }

    /// <summary>
    /// 主图上下文菜单（长按 / 辅助点按）：每次弹出时现读共享状态，勾选态与栏位显隐/增强开关一致；
    /// I5 起分析栏、增强预览与工具页都是真实入口。
    /// </summary>
    /// <returns>菜单构建产物。</returns>
    internal iOSContextMenus.MenuResult BuildPhotoMenu() => iOSContextMenus.Photo(
        _main.IsThumbnailViewVisible, _main.IsControlViewVisible, _main.IsAnalysisViewVisible, _session.IsEnhanced,
        () => _ = OpenFolderPickerAsync(), _main.ToggleThumbnailView, _main.ToggleControlView, _main.ToggleAnalysisView,
        () => _session.ToggleEnhanced(), ShowTools, _main.OpenSettingWindow);

    /// <summary>打开/前置原生工具页（主图菜单与 EXIF 信息块共用同一门面入口）。</summary>
    internal void ShowTools() => NativeWindowPresenter.ShowOrActivateToolsWindow(_main.Tools);

    /// <summary>适应态横向轻扫翻页：走共享命令表（上一张/下一张），由共享 Move 完成显式定位（[FILM-01]）。</summary>
    /// <param name="direction">-1 上一张、+1 下一张。</param>
    private void OnSwipeRequested(int direction) => Execute(direction < 0 ? "Previous" : "Next");

    /// <summary>
    /// 离散滚轮：按用户鼠标绑定执行命令（与 Mac 的非精确滚轮口径一致）；
    /// 无绑定时留在照片上不动作，触控板连续滚动仍由滚动视图平移。
    /// </summary>
    /// <param name="up">滚轮方向是否向上。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    private void OnWheelScrolled(bool up, ulong modifierFlags)
    {
        if (KeyboardInputBlocked) return;
        var gesture = AppleInputMapping.FromRaw(AppleInputMapping.ScrollWheel, 0, modifierFlags, 0, up ? 1 : -1, false, null, _main.Settings);
        string? command = AppleInputMapping.CommandForGesture(gesture, _main.Settings);
        if (command == null || !iOSControlBar.IsCommandAvailable(command) || AppleSymbolNames.ForCommand(command) == null) return;
        Execute(command);
    }

    /// <summary>订阅输入控件编辑通知，作为 INPUT-01 的"文本输入获焦"判据。</summary>
    private void SubscribeTextEditing()
    {
        var center = NSNotificationCenter.DefaultCenter;
        _textEditBeginField = center.AddObserver(UITextField.TextDidBeginEditingNotification,
            notification => { if (notification.Object is { } target) _editingTextInputs.Add(target); });
        _textEditBeginView = center.AddObserver(UITextView.TextDidBeginEditingNotification,
            notification => { if (notification.Object is { } target) _editingTextInputs.Add(target); });
        _textEditEndField = center.AddObserver(UITextField.TextDidEndEditingNotification,
            notification => OnTextEditingEnded(notification));
        _textEditEndView = center.AddObserver(UITextView.TextDidEndEditingNotification,
            notification => OnTextEditingEnded(notification));
    }

    /// <summary>文本输入结束：清焦点记录，并在没有模态展示时把键盘焦点还给主屏。</summary>
    /// <param name="notification">结束编辑通知。</param>
    private void OnTextEditingEnded(NSNotification notification)
    {
        if (notification.Object is { } target) _editingTextInputs.Remove(target);
        if (!KeyboardInputBlocked) BecomeFirstResponder();
    }

    /// <summary>按住键的首次延迟计时；到点后转入固定间隔的重复执行。</summary>
    /// <param name="usage">HID usage。</param>
    /// <param name="modifierFlags">按下时的修饰键掩码。</param>
    private void StartKeyRepeat(int usage, ulong modifierFlags)
    {
        StopKeyRepeat();
        _repeatUsage = usage;
        _repeatModifiers = modifierFlags;
        _repeatDelay = NSTimer.CreateScheduledTimer(KeyRepeatDelay, false, _ => StartRepeatTicks());
    }

    /// <summary>启动固定间隔重复：每次重新查共享绑定，设置变化或按键抬起即停。</summary>
    private void StartRepeatTicks()
    {
        _repeatDelay = null;
        if (_repeatUsage < 0) return;
        _repeatTimer = NSTimer.CreateScheduledTimer(KeyRepeatInterval, true, _ =>
        {
            if (_repeatUsage < 0 || !_heldKeys.ContainsKey(_repeatUsage) || KeyboardInputBlocked)
            {
                StopKeyRepeat();
                return;
            }
            TryExecuteKey(_repeatUsage, _repeatModifiers);
        });
    }

    /// <summary>停止重复计时并清空重复目标。</summary>
    private void StopKeyRepeat()
    {
        _repeatDelay?.Invalidate();
        _repeatDelay?.Dispose();
        _repeatDelay = null;
        _repeatTimer?.Invalidate();
        _repeatTimer?.Dispose();
        _repeatTimer = null;
        _repeatUsage = -1;
    }

    /// <summary>清空按住状态与重复计时（系统取消按键、设置回写后绑定可能已变）。</summary>
    private void ClearHeldKeys()
    {
        _heldKeys.Clear();
        StopKeyRepeat();
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
                // 切图后细节预览的定格/悬停都作废，绿框不得跟着旧裁剪中心留在新照片上。
                HideDetailHighlight();
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
            if (!_main.IsAnalysisViewVisible)
            {
                _analysisPane.ClearDetailHighlight();
                HideDetailHighlight();
            }
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

    /// <summary>设置回写后重建按钮并重排（显隐/顺序/快捷键/文件格式都影响主屏）。</summary>
    /// <param name="sender">事件源（设置 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 绑定可能已在设置页里改写：按住状态的重复目标立即作废，避免按旧命令继续翻页。
        ClearHeldKeys();
        ScheduleControlsRefresh();
    }

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
            // 筛选条文本/菜单、计数宽度与分区几何一并刷新；文件格式变化也在这里反映到胶片栏。
            RefreshScreen();
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

    /// <summary>
    /// 相似项集合变化后重排主屏：行布局面板宽度按卡片数从胶片栏右侧切出（旧壳 MaxWidth 规则），
    /// 集合由空变有/由有变空时必须重新计算，否则面板停在 106 点最小宽、列表区被停靠动作区挤成零宽。
    /// </summary>
    /// <param name="sender">事件源（相似面板 VM）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnSimilarityPanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SimilarityPanelViewModel.SimilarItems) or nameof(SimilarityPanelViewModel.HasItems))) return;
        UiDispatcher.Post(Layout);
    }

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
    /// 细节预览高亮：把命中的细节预览中心/对焦框换算成主图上的 3 点绿框。
    /// 来自触控板 hover 或手指定格（两者都经细节预览的 <c>HighlightChanged</c>）。
    /// </summary>
    /// <param name="visible">是否显示绿框。</param>
    /// <param name="u">裁剪中心归一化横坐标。</param>
    /// <param name="v">裁剪中心归一化纵坐标。</param>
    /// <param name="frameWidth">对焦框归一化宽；null 表示用裁剪区。</param>
    /// <param name="frameHeight">对焦框归一化高。</param>
    private void UpdateDetailHighlight(bool visible, double u, double v, double? frameWidth, double? frameHeight)
    {
        _detailU = u;
        _detailV = v;
        _detailFrameWidth = frameWidth;
        _detailFrameHeight = frameHeight;
        _detailVisible = visible;
        ApplyDetailHighlight();
    }

    /// <summary>按当前视口与细节中心重算绿框（缩放/平移、切图与栏位显隐后都调用）。</summary>
    private void ApplyDetailHighlight()
    {
        if (!_detailVisible || !_main.IsAnalysisViewVisible || _session.Image == null)
        {
            _detailHighlight.Hidden = true;
            return;
        }
        double scale = _photo.PointScale;
        double boxWidth, boxHeight;
        if (_detailFrameWidth is { } width && _detailFrameHeight is { } height)
        {
            boxWidth = width * _photo.DisplayWidth * scale;
            boxHeight = height * _photo.DisplayHeight * scale;
        }
        else
        {
            boxWidth = boxHeight = _analysisPane.CropPixels * scale;
        }
        var center = _photo.ViewPoint(_detailU, _detailV);
        _detailHighlight.Frame = new CGRect(center.X - boxWidth / 2, center.Y - boxHeight / 2, boxWidth, boxHeight);
        _detailHighlight.Hidden = false;
    }

    /// <summary>收起主图绿框（分析栏隐藏、切图或点空白时）。</summary>
    private void HideDetailHighlight()
    {
        _detailVisible = false;
        _detailHighlight.Hidden = true;
    }

    /// <summary>细节预览双击：把主图定位到该归一化点并放大到物理 100%（与旧壳 FocusOnImagePoint 同义）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    private void FocusDetailPoint(double u, double v) => _photo.FocusOnPoint(u, v, 1);

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
        ApplyDetailHighlight();
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
            _photo.NeedsFull || _main.IsAnalysisViewVisible);

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

    /// <summary>
    /// 拖入会话落地：逐项读取文件 URL（不可读项记录后跳过，属不可信外部输入），
    /// 再交原生外部打开路径——与 I1b 的 openURL / "打开方式"完全同一条共享链路。
    /// </summary>
    /// <param name="session">系统拖入会话。</param>
    private async Task HandleDropSessionAsync(IUIDropSession session)
    {
        var urls = new List<NSUrl>();
        foreach (var item in session.Items)
        {
            try
            {
                if (await item.ItemProvider.LoadObjectAsync(new ObjCRuntime.Class(typeof(NSUrl))) is NSUrl url) urls.Add(url);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Native] 拖入项读取失败：{exception.Message}");
            }
        }
        PublishDroppedUrlsAsync(urls);
    }

    /// <summary>把拖入的原生 URL 发布为外部打开请求（验收与 <see cref="HandleDropSessionAsync"/> 共用）。</summary>
    /// <param name="urls">文件/文件夹 URL。</param>
    /// <returns>是否成功投递。</returns>
    internal bool PublishDroppedUrlsAsync(IReadOnlyList<NSUrl> urls)
        => urls.Count > 0 && iOSExternalOpenBridge.PublishNativeFromUrls(urls, "iOS:Drop");

    /// <summary>拖放委托：只接受文件 URL，拖动中给"拷贝"指示，落地后交共享打开路径。</summary>
    private sealed class iOSDropDelegate(iOSNativeRootViewController owner) : UIDropInteractionDelegate
    {
        /// <summary>会话是否携带可读取的文件/文件夹 URL。</summary>
        /// <param name="interaction">拖放交互。</param>
        /// <param name="session">拖入会话。</param>
        /// <returns>可接收时为 true。</returns>
        public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
            => session.Items.Length > 0 && session.Items.All(item =>
                item.ItemProvider.HasItemConformingTo(UTTypes.FileUrl.Identifier) ||
                item.ItemProvider.CanLoadObject(new ObjCRuntime.Class(typeof(NSUrl))));

        /// <summary>拖动经过主屏时显示拷贝指示，不接受时给禁止态。</summary>
        /// <param name="interaction">拖放交互。</param>
        /// <param name="session">拖入会话。</param>
        /// <returns>系统拖放提案。</returns>
        public override UIDropProposal SessionDidUpdate(UIDropInteraction interaction, IUIDropSession session)
            => new(CanHandleSession(interaction, session) ? UIDropOperation.Copy : UIDropOperation.Forbidden);

        /// <summary>松手落地：读取 URL 并发布到共享外部打开链路。</summary>
        /// <param name="interaction">拖放交互。</param>
        /// <param name="session">拖入会话。</param>
        public override void PerformDrop(UIDropInteraction interaction, IUIDropSession session) => _ = owner.HandleDropSessionAsync(session);
    }
}
