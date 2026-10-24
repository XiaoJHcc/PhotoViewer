using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 主窗口；复用共享业务 VM，加载与展示通过 PhotoLoadSession/MetalPhotoView 分离。</summary>
public sealed class MacMainWindow : IDisposable
{
    /// <summary>列布局下筛选条顶边:为隐藏标题栏的交通灯让位(旧壳平台标题栏设置);其余几何取共享 <see cref="MainScreenLayout"/>。</summary>
    private const double FilterTopInset = 34;
    /// <summary>系统“双击窗口标题栏”偏好键(NSUserDefaults 全局域，取值 Maximize / Minimize / None)。</summary>
    internal const string TitlebarDoubleClickPreference = "AppleActionOnDoubleClick";
    /// <summary>AppKit 全屏样式位 <c>NSWindowStyleMaskFullScreen</c>（.NET 绑定的 <see cref="NSWindowStyle"/> 未导出该成员，按位取值）。</summary>
    private const NSWindowStyle FullScreenStyleBit = (NSWindowStyle)(1 << 14);
    private readonly MainViewModel _main;
    private readonly RootView _root;
    private readonly NSView _filePane;
    private readonly NSView _fileDivider = Pane(NSColor.FromWhite(.2f, 1));
    private readonly NSView _controlDivider = Pane(NSColor.FromWhite(.2f, 1));
    private readonly NSTextField _hintDetail = Label("", 11);
    private readonly MacControlBar _controlPane;
    private readonly NSScrollView _controlScroll;
    private readonly NSView _photoPane;
    private readonly MacAnalysisPanel _analysisPane;
    private readonly NSView _detailHighlight;
    private readonly DragZone _dragLeft;
    private readonly DragZone _dragRight;
    private readonly MacFilmstripScrollView _scroll;
    private readonly NSCollectionView _collection;
    private readonly MacFilmstripLayout _filmstripLayout;
    private readonly CollectionSource _source;
    private readonly NSPopUpButton _sort;
    private readonly NSPopUpButton _order;
    private readonly NSPopUpButton _ratingFilter;
    private readonly NSTextField _count;
    private readonly FilterToggleButton _similarityToggle;
    private readonly MacSimilarityPanel _similarityPane;
    private readonly NSTextField _hint;
    private readonly NSTextField _busyPill;
    private readonly MacStatusToast _toast = new();
    private readonly NavigatorHostView _navigationHost;
    private readonly NSImageView _navigationImage;
    private readonly NSView _navigationFrame;
    private readonly NSTextField _zoomLabel;
    private NSImage? _navigationBitmap;
    private DecodedImage? _navigationSource;
    private readonly List<(NSMenuItem Item, Func<bool> Checked, Func<bool>? Enabled)> _menuChecks = [];
    private readonly List<(NSMenuItem Item, Func<bool> Enabled)> _menuAvailability = [];
    private NSMenu? _actionsMenu;
    private NSMenuDelegate? _menuDelegate;
    private readonly NSObject _scrollObserver;
    private readonly NSObject _liveScrollObserver;
    private readonly NSObject _inputMonitor;
    private ImageFile? _observedFile;
    private ImageFile[] _displayedFiles = [];
    private string? _lastFullError;
    private bool _layingOut;
    private bool _refreshPending;
    private bool _disposed;
    private int _scrollRequestVersion;
    private readonly bool _automatedValidation;
    private MacNativeSettingsPresenter? _settingsPresenter;
    private (double U, double V, double? Width, double? Height)? _activeDetail;
    private readonly NSObject _deactivationObserver;

    /// <summary>系统窗口，外部打开和验收工具可据此定位原生界面。</summary>
    public NSWindow Window { get; }
    /// <summary>主图 Metal 表面。</summary>
    public MetalPhotoView Photo { get; }
    /// <summary>共享展示加载会话。</summary>
    public PhotoLoadSession Session { get; } = new();
    /// <summary>共享主 VM。</summary>
    public MainViewModel Main => _main;
    /// <summary>真实胶片栏与滚动容器供布局回归读取，不另建模拟列表。</summary>
    internal NSCollectionView Filmstrip => _collection;
    /// <summary>真实胶片栏滚动容器。</summary>
    internal NSScrollView FilmstripScroll => _scroll;
    /// <summary>实际分组控制栏供操作与几何回归读取。</summary>
    internal MacControlBar ControlBar => _controlPane;
    /// <summary>原生分析栏供几何与交互回归读取。</summary>
    internal MacAnalysisPanel Analysis => _analysisPane;
    /// <summary>是否处于自动验收会话（验收工具据此避免写用户偏好与弹窗）。</summary>
    internal bool IsAutomatedValidation => _automatedValidation;
    /// <summary>控制栏系统滚动容器供溢出与边缘验收读取。</summary>
    internal NSScrollView ControlScroll => _controlScroll;
    /// <summary>原生相似聚类面板供几何与交互回归读取。</summary>
    internal MacSimilarityPanel Similarity => _similarityPane;
    /// <summary>筛选条相似聚类 toggle 供原生布局验收读取。</summary>
    internal NSButton SimilarityToggle => _similarityToggle;
    /// <summary>相似聚类 toggle 的勾选态（按钮自持，验收读取）。</summary>
    internal bool SimilarityToggleIsOn => _similarityToggle.IsOn;
    /// <summary>增强"计算中"药丸，供验收核对 500ms 门槛后的可见性。</summary>
    internal NSTextField EnhanceBusyPill => _busyPill;
    /// <summary>主图区底部提示条，供验收核对非模态反馈。</summary>
    internal MacStatusToast StatusToast => _toast;
    /// <summary>顶边悬停拖动区供验收核对它们是否绕开了筛选条控件。</summary>
    internal IReadOnlyList<NSView> DragZones => [_dragLeft, _dragRight];
    /// <summary>主图上的细节预览绿框（ANALYSIS-04/06），供验收核对它框住的源像素范围等于裁剪窗口。</summary>
    internal CGRect DetailHighlightFrame => _detailHighlight.Frame;
    /// <summary>细节预览绿框是否可见。</summary>
    internal bool DetailHighlightVisible => !_detailHighlight.Hidden;

    /// <summary>创建窗口、筛选、虚拟化胶片栏与控制栏，并连接共享通知。</summary>
    public MacMainWindow(MainViewModel main, bool automatedValidation = false)
    {
        _main = main;
        _automatedValidation = automatedValidation;
        if (!automatedValidation)
        {
            var preferences = NSUserDefaults.StandardUserDefaults;
            if (preferences["PhotoViewer.Native.ShowFilm"] != null) _main.IsThumbnailViewVisible = preferences.BoolForKey("PhotoViewer.Native.ShowFilm");
            if (preferences["PhotoViewer.Native.ShowControls"] != null) _main.IsControlViewVisible = preferences.BoolForKey("PhotoViewer.Native.ShowControls");
        }
        Window = new NSWindow(new CGRect(100, 100, 1200, 850), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView, NSBackingStore.Buffered, false)
        {
            Title = "PhotoViewer", TitleVisibility = NSWindowTitleVisibility.Hidden, TitlebarAppearsTransparent = true,
            BackgroundColor = NSColor.Black, MinSize = new CGSize(560, 480), AcceptsMouseMovedEvents = true,
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
        };
        Window.FrameAutosaveName = "PhotoViewer.Native.MainWindow";
        _root = new RootView(new CGRect(0, 0, 1200, 850));
        Window.ContentView = _root;
        _filePane = Pane(); _controlPane = new MacControlBar(ScheduleControlsRefresh); _photoPane = Pane(NSColor.Black);
        _controlScroll = new MacControlScrollView { DocumentView = _controlPane, HasVerticalScroller = false, DrawsBackground = true, BackgroundColor = NSColor.FromWhite(.07f, 1), BorderType = NSBorderType.NoBorder, ScrollerStyle = NSScrollerStyle.Overlay, AutomaticallyAdjustsContentInsets = false };
        _root.AddSubview(_filePane); _root.AddSubview(_photoPane); _root.AddSubview(_controlScroll);
        _root.AddSubview(_fileDivider); _root.AddSubview(_controlDivider);
        _dragLeft = new DragZone(); _dragRight = new DragZone();
        _root.AddSubview(_dragLeft); _root.AddSubview(_dragRight);
        Photo = new MetalPhotoView(new CGRect(0, 0, 1000, 750));
        _main.FitToViewRequested += OnFitToViewRequested;
        _main.OpenFailed += ShowToast;
        _photoPane.AddSubview(Photo);
        _detailHighlight = new PassThroughPane();
        _detailHighlight.Layer!.BorderColor = NSColor.FromRgb(0, 255, 0).CGColor;
        _detailHighlight.Layer.BorderWidth = 3;
        _detailHighlight.Hidden = true;
        _photoPane.AddSubview(_detailHighlight);
        _analysisPane = new MacAnalysisPanel(_main, Session) { Hidden = true };
        _analysisPane.DetailHighlight = UpdateDetailHighlight;
        _analysisPane.DetailFocusRequested = FocusDetailPoint;
        _root.AddSubview(_analysisPane);
        _hint = Label("点击打开文件 或 拖拽图片到此处", 16);
        _hint.Alignment = NSTextAlignment.Center;
        _hint.TextColor = NSColor.SecondaryLabel;
        _hintDetail.TextColor = NSColor.SecondaryLabel;
        _hintDetail.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        _hintDetail.MaximumNumberOfLines = 4;
        _photoPane.AddSubview(_hint); _photoPane.AddSubview(_hintDetail);
        _navigationHost = new NavigatorHostView { WantsLayer = true, CenterRequested = OnNavigatorCenterRequested };
        _navigationHost.Layer!.BackgroundColor = NSColor.FromWhite(2f / 15, .6f).CGColor;
        _navigationHost.Layer!.CornerRadius = 6;
        _navigationImage = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        _navigationFrame = Pane(NSColor.Clear); _navigationFrame.Layer!.BorderColor = NSColor.White.CGColor; _navigationFrame.Layer.BorderWidth = 1;
        _zoomLabel = Label("", 12); _zoomLabel.Alignment = NSTextAlignment.Center;
        _zoomLabel.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold) ?? throw new InvalidOperationException("系统字体不可用");
        _navigationHost.AddSubview(_navigationImage); _navigationHost.AddSubview(_navigationFrame); _navigationHost.AddSubview(_zoomLabel);
        _photoPane.AddSubview(_navigationHost); _navigationHost.Hidden = true;
        _busyPill = Label("计算中…", 13);
        _busyPill.Alignment = NSTextAlignment.Center;
        _busyPill.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium) ?? throw new InvalidOperationException("系统字体不可用");
        _busyPill.TextColor = NSColor.White;
        _busyPill.WantsLayer = true;
        _busyPill.Layer!.BackgroundColor = NSColor.FromWhite(.13f, .85f).CGColor;
        _busyPill.Layer.CornerRadius = (nfloat)DesignTokens.ImageBusyCornerRadius;
        _busyPill.Layer.MasksToBounds = true;
        _busyPill.Hidden = true;
        _photoPane.AddSubview(_busyPill);
        _photoPane.AddSubview(_toast);
        _sort = Popup(_main.FileVM.FilterBar.SortModes.Select(item => item.DisplayName).ToArray());
        _order = Popup(_main.FileVM.FilterBar.SortOrders.Select(item => item.DisplayName).ToArray());
        _ratingFilter = Popup(_main.FileVM.FilterBar.RatingFilters.Select(item => item.DisplayName).ToArray());
        _count = Label("共 0 张", 12); _count.Alignment = NSTextAlignment.Center;
        _similarityToggle = new FilterToggleButton("相似聚类", "展开/关闭相似聚类面板")
        {
            IsOn = _main.FileVM.FilterBar.IsSimilarityPanelOpen,
        };
        _sort.Activated += (_, _) => _main.FileVM.FilterBar.SortMode = (SortMode)_main.FileVM.FilterBar.SortModes[(int)_sort.IndexOfSelectedItem].Value;
        _order.Activated += (_, _) => _main.FileVM.FilterBar.SortOrder = (SortOrder)_main.FileVM.FilterBar.SortOrders[(int)_order.IndexOfSelectedItem].Value;
        _ratingFilter.Activated += (_, _) => _main.FileVM.FilterBar.SelectedRatingFilter = _main.FileVM.FilterBar.RatingFilters[(int)_ratingFilter.IndexOfSelectedItem].Key;
        // 勾选态由按钮自持并回写共享状态（不回读按钮，避免与 AppKit cell 状态耦合）。
        _similarityToggle.Toggled = open => _main.FileVM.FilterBar.IsSimilarityPanelOpen = open;
        foreach (var control in new NSView[] { _sort, _order, _ratingFilter, _count, _similarityToggle }) _filePane.AddSubview(control);
        _filmstripLayout = new MacFilmstripLayout();
        _collection = new NSCollectionView(new CGRect(0, 0, 116, 680)) { CollectionViewLayout = _filmstripLayout, Selectable = false, BackgroundColors = [NSColor.FromWhite(.07f, 1)] };
        _collection.RegisterClassForItem(typeof(MacThumbnailItem), "PhotoCard");
        _source = new CollectionSource(this); _collection.DataSource = _source;
        _scroll = new MacFilmstripScrollView { DocumentView = _collection, HasVerticalScroller = true, DrawsBackground = false, BorderType = NSBorderType.NoBorder, ScrollerStyle = NSScrollerStyle.Overlay, AutomaticallyAdjustsContentInsets = false };
        _scroll.ContentView.PostsBoundsChangedNotifications = true;
        _scroll.UserScrollStarted = CancelFilmstripScroll;
        _liveScrollObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSScrollView.WillStartLiveScrollNotification, _ => CancelFilmstripScroll(), _scroll);
        _filePane.AddSubview(_scroll);
        _similarityPane = new MacSimilarityPanel(_main) { Hidden = true };
        _filePane.AddSubview(_similarityPane);
        _scrollObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => ReportVisible(), _scroll.ContentView);
        _inputMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown | NSEventMask.MouseMoved | NSEventMask.MouseExited, nativeEvent =>
        {
            if (nativeEvent.Type is NSEventType.MouseMoved or NSEventType.MouseExited)
            {
                var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
                bool show = nativeEvent.Window == Window && Window.IsKeyWindow && nativeEvent.Type == NSEventType.MouseMoved && point.Y >= 0 && point.Y <= 50;
                _dragLeft.Hidden = !show; _dragRight.Hidden = !show;
                return nativeEvent;
            }
            if (nativeEvent.Type != NSEventType.KeyDown || nativeEvent.Window != Window || Window.FirstResponder is NSTextView or NSTextField) return nativeEvent;
            return HandleInput(nativeEvent) ? null! : nativeEvent;
        });
        Photo.InputHandler = HandleInput;
        Photo.WindowInputHandler = HandlePhotoWindowInput;
        _dragLeft.DoubleClick = HandleTitlebarDoubleClick;
        _dragRight.DoubleClick = HandleTitlebarDoubleClick;
        Photo.ViewportChanged += UpdateViewport;
        Window.DidChangeBackingProperties += (_, _) => { Photo.SetFrameSize(Photo.Frame.Size); _analysisPane.RefreshDetailCrops(); RefreshDetailHighlight(); };
        Window.DidEnterFullScreen += (_, _) => SaveFullscreen(true);
        Window.DidExitFullScreen += (_, _) => SaveFullscreen(false);
        Window.DidResignKey += (_, _) => { _dragLeft.Hidden = true; _dragRight.Hidden = true; HideDetailHighlight(); };
        _deactivationObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSApplication.DidResignActiveNotification, _ => HideDetailHighlight());
        Session.Changed += OnImageChanged;
        Session.Notice += ShowToast;
        _root.Resized = Layout;
        _root.OpenDroppedPaths = paths => _ = OpenPathsAsync(paths, OpenRequestSource.DragDrop);
        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += ScrollToCurrent;
        BuildControls();
        BuildMenus();
        _root.Menu = ContextMenu();
        Photo.Menu = _root.Menu;
        Layout();
        // 重复外部激活（再次打开当前目录/当前照片）只前置窗口；协调器不重新打开、不重新适应。
        _main.OpenCoordinator.RepeatActivation += BringToFront;
        Window.MakeKeyAndOrderFront(null);
        if (!automatedValidation && NSUserDefaults.StandardUserDefaults.BoolForKey("PhotoViewer.Native.Fullscreen"))
            UiDispatcher.Post(() => Window.ToggleFullScreen(null));
    }

    /// <summary>安装原生设置展示器。</summary>
    public void SetSettingsPresenter(MacNativeSettingsPresenter presenter) => _settingsPresenter = presenter;
    /// <summary>打开/前置唯一的原生设置窗口。</summary>
    public void ShowSettings() => _settingsPresenter?.TryPresent(_main.Settings);
    /// <summary>全屏进入/退出时保存系统状态，自动验收不改用户窗口偏好。</summary>
    private void SaveFullscreen(bool fullscreen)
    {
        if (!_automatedValidation) NSUserDefaults.StandardUserDefaults.SetBool(fullscreen, "PhotoViewer.Native.Fullscreen");
    }
    /// <summary>按系统“双击窗口标题栏”偏好执行双击动作：隐藏式标题栏(FullSizeContentView)的窗口
    /// 不会自带系统行为，须由顶边拖动区自己分派；取值缺省即系统默认“最大化”(缩放切换)。</summary>
    private static void PerformTitlebarDoubleClickAction(NSWindow? window)
    {
        if (window == null) return;
        switch (NSUserDefaults.StandardUserDefaults.StringForKey(TitlebarDoubleClickPreference))
        {
            case "Minimize": window.Miniaturize(null); break;
            case "None": break;
            default: window.Zoom(null); break;
        }
    }
    private bool HandlePhotoWindowInput(NSEvent nativeEvent)
    {
        if (nativeEvent.Type != NSEventType.LeftMouseDown || nativeEvent.Window != Window || Window.StyleMask.HasFlag(FullScreenStyleBit)) return false;
        var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
        if (!_dragLeft.Frame.Contains(point) && !_dragRight.Frame.Contains(point)) return false;
        if (HandleTitlebarDoubleClick(nativeEvent)) return true;
        Window.PerformWindowDrag(nativeEvent);
        return true;
    }

    /// <summary>原生拖动区或主图收到的标题栏双击，按系统偏好执行；系统装饰与 resize 优先由 NSWindow 派发。</summary>
    /// <param name="nativeEvent">进入窗口派发链的原生事件。</param>
    internal bool HandleTitlebarDoubleClick(NSEvent nativeEvent)
    {
        if (nativeEvent.Type != NSEventType.LeftMouseDown || nativeEvent.ClickCount < 2 || nativeEvent.Window != Window) return false;
        // 全屏没有标题栏：不接管双击，把它留给主图自己的双击语义。
        if (Window.StyleMask.HasFlag(FullScreenStyleBit)) return false;
        var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
        if (!_dragLeft.Frame.Contains(point) && !_dragRight.Frame.Contains(point)) return false;
        var frameView = _root.Superview;
        var hit = frameView?.HitTest(frameView.ConvertPointFromView(nativeEvent.LocationInWindow, null));
        if (hit is NSControl) return false;
        PerformTitlebarDoubleClickAction(Window);
        return true;
    }
    /// <summary>创建深色观片分区，使用 layer 而不重绘大位图。</summary>
    private static NSView Pane(NSColor? color = null)
    {
        var pane = new FlippedPane { WantsLayer = true };
        pane.Layer!.BackgroundColor = (color ?? NSColor.FromWhite(.07f, 1)).CGColor;
        return pane;
    }
    /// <summary>创建只读且不拦截鼠标点击的原生标签。</summary>
    private static NSTextField Label(string text, double size)
        => new PassThroughLabel { StringValue = text, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false, TextColor = NSColor.LightGray, Font = NSFont.SystemFontOfSize((nfloat)size) ?? throw new InvalidOperationException("系统字体不可用") };
    /// <summary>创建固定位置的原生下拉框。</summary>
    private static NSPopUpButton Popup(string[] titles)
    {
        var popup = new FilterPopUpButton(new CGRect(0, 0, MainScreenLayout.FilterControlWidth, MainScreenLayout.FilterControlHeight))
        {
            Cell = new FilterPopUpCell(),
            ControlSize = NSControlSize.Regular,
            Font = NSFont.SystemFontOfSize(13) ?? throw new InvalidOperationException("系统字体不可用"),
        };
        popup.AddItems(titles);
        return popup;
    }

    /// <summary>鼠标入口与键盘点击共用原生单元格的菜单定位，避免按钮另启默认菜单跟踪。</summary>
    private sealed class FilterPopUpButton(CGRect frame) : NSPopUpButton(frame, false)
    {
        /// <summary>将鼠标按下交给同一弹出菜单路径，菜单负责选择、释放与取消。</summary>
        public override void MouseDown(NSEvent nativeEvent) => Cell.PerformClick(Bounds, this);
    }

    /// <summary>筛选条相似聚类原生 toggle：无边框自绘药丸，12 点居中标题、圆角 6、1 点描边；
    /// 勾选态取 <c>color.filter.toggle.checked.*</c>（#1F3A5F 底 / #3A6FA8 边 / #AECBEE 字），
    /// 未勾选为透明底 / #555 边 / #AAA 字；悬停色同样取令牌（FILTER-01/FILTER-06）。
    /// 勾选状态由本类自己持有（不用 <c>NSButtonType.Toggle</c>）：旧写法的 getter 读私有字段而点击翻转的是 cell 状态，
    /// 导致点一次读回旧值、按钮表现为"点不动"；同时 Toggle 态会取 alternateTitle，只设 AttributedTitle 会露出系统占位串 "Button"。</summary>
    private sealed class FilterToggleButton : NSButton
    {
        private static readonly double CornerRadius = DesignTokens.FilterToggleCornerRadius;
        private static readonly NSColor BorderOff = MacDesignColors.Argb(DesignTokens.ColorFilterToggleBorder);
        private static readonly NSColor TextOff = MacDesignColors.Argb(DesignTokens.ColorFilterToggleForeground);
        private static readonly NSColor FillOn = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedBackground);
        private static readonly NSColor BorderOn = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedBorder);
        private static readonly NSColor TextOn = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedForeground);
        private static readonly NSColor FillOffHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleHover);
        private static readonly NSColor BorderOffHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleHoverBorder);
        private static readonly NSColor TextOffHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleHoverForeground);
        private static readonly NSColor FillOnHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedHoverBackground);
        private static readonly NSColor BorderOnHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedHoverBorder);
        private static readonly NSColor TextOnHover = MacDesignColors.Argb(DesignTokens.ColorFilterToggleCheckedHoverForeground);
        private readonly string _title;
        private bool _isOn;
        private bool _hover;
        private (bool On, bool Hover)? _styled;
        private NSTrackingArea? _tracking;

        /// <summary>点击切换回调，参数为切换后的勾选态；调用方据此回写共享状态。</summary>
        public Action<bool>? Toggled { get; set; }

        /// <summary>构造并立即应用未勾选样式（不依赖外部赋值，避免漏掉首次上色）。</summary>
        /// <param name="title">按钮文案。</param>
        /// <param name="tooltip">鼠标悬停提示。</param>
        public FilterToggleButton(string title, string tooltip)
        {
            _title = title;
            ToolTip = tooltip;
            SetButtonType(NSButtonType.MomentaryPushIn);
            Bordered = false;
            WantsLayer = true;
            Alignment = NSTextAlignment.Center;
            Layer!.CornerRadius = (nfloat)CornerRadius;
            Layer.BorderWidth = 1;
            Layer.MasksToBounds = true;
            Activated += (_, _) =>
            {
                _isOn = !_isOn;
                ApplyStyle(_isOn);
                Toggled?.Invoke(_isOn);
            };
            ApplyStyle(false);
        }

        /// <summary>勾选态；样式只在与已应用组合不同时重绘，避免筛选条每次属性变化都重画。</summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                _isOn = value;
                ApplyStyle(value);
            }
        }

        /// <summary>新增/更新鼠标跟踪区，用于悬停反馈（旧壳 ToggleButton 有 pointerover 态）。</summary>
        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking != null) RemoveTrackingArea(_tracking);
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow, this, null);
            AddTrackingArea(_tracking);
        }

        /// <summary>鼠标进入：应用悬停配色。</summary>
        public override void MouseEntered(NSEvent theEvent)
        {
            _hover = true;
            ApplyStyle(_isOn);
        }

        /// <summary>鼠标离开：恢复常态配色。</summary>
        public override void MouseExited(NSEvent theEvent)
        {
            _hover = false;
            ApplyStyle(_isOn);
        }

        /// <summary>
        /// 应用勾选/悬停组合的配色；普通与替代标题都按业务文案设置，杜漏系统占位串。
        /// </summary>
        /// <param name="on">是否勾选。</param>
        private void ApplyStyle(bool on)
        {
            if (_styled == (on, _hover)) return;
            _styled = (on, _hover);
            var (fill, border, text) = (on, _hover) switch
            {
                (true, true) => (FillOnHover, BorderOnHover, TextOnHover),
                (true, false) => (FillOn, BorderOn, TextOn),
                (false, true) => (FillOffHover, BorderOffHover, TextOffHover),
                (false, false) => (NSColor.Clear, BorderOff, TextOff),
            };
            Layer!.BackgroundColor = fill.CGColor;
            Layer.BorderColor = border.CGColor;
            using var paragraph = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
            using var attributes = new NSDictionary(
                NSStringAttributeKey.Font, NSFont.SystemFontOfSize(12) ?? throw new InvalidOperationException("系统字体不可用"),
                NSStringAttributeKey.ForegroundColor, text,
                NSStringAttributeKey.ParagraphStyle, paragraph);
            var attributed = new NSAttributedString(_title, attributes);
            Title = _title;
            AlternateTitle = _title;
            AttributedTitle = attributed;
            AttributedAlternateTitle = attributed;
        }
    }

    /// <summary>保留原生筛选菜单和选择事件，以按钮左下角定位菜单，避免选中标记使菜单向左偏移。</summary>
    private sealed class FilterPopUpCell : NSPopUpButtonCell
    {
        private const double MenuAnchorGap = 8;
        /// <summary>初始化常规尺寸的原生弹出按钮单元格。</summary>
        public FilterPopUpCell() : base(string.Empty, false) => BezelStyle = NSBezelStyle.Rounded;

        /// <summary>键盘、辅助功能与程序化点击采用同一菜单定位路径。</summary>
        public override void PerformClick(CGRect cellFrame, NSView controlView) => ShowMenu(cellFrame, controlView);

        /// <summary>以按钮左下角定位菜单内容并预留原生菜单外框空间，返回是否选中菜单项。</summary>
        private bool ShowMenu(CGRect cellFrame, NSView controlView)
        {
            var menu = Menu ?? throw new InvalidOperationException("筛选菜单未初始化");
            menu.MinimumWidth = cellFrame.Width;
            Highlight(true, cellFrame, controlView);
            try
            {
                double anchorY = controlView.IsFlipped ? (double)(cellFrame.Y + cellFrame.Height) + MenuAnchorGap : (double)cellFrame.Y - MenuAnchorGap;
                return menu.PopUpMenu(null, new CGPoint(cellFrame.X, anchorY), controlView);
            }
            finally
            {
                Highlight(false, cellFrame, controlView);
                controlView.NeedsDisplay = true;
            }
        }
    }

    /// <summary>依据共享上下/左右/自动布局排列；主图延伸到顶部，局部避让交通灯。</summary>
    private void Layout()
    {
        if (_disposed || _layingOut) return;
        _layingOut = true;
        try
        {
            var previousViewport = _scroll.ContentView.Bounds.Size;
            double width = (double)_root.Bounds.Width, height = (double)_root.Bounds.Height;
            _main.UpdateScreenOrientation(width, height);
            bool row = _main.IsRowLayout;
            _count.SizeToFit();
            double countWidth = Math.Ceiling((double)_count.Frame.Width);
            double filterHeight = MainScreenLayout.FilterBarMainLength(row, FilterTopInset);
            double filmstripHeight = MainScreenLayout.FilmstripBand;
            // 相似聚类面板：列布局由面板自定的宽度扩宽文件栏，行布局按旧壳 MaxWidth 规则从胶片栏切出右侧一段。
            bool similarityOpen = _main.IsThumbnailViewVisible && _main.FileVM.FilterBar.IsSimilarityPanelOpen;
            if (similarityOpen) _similarityPane.SetLayout(row, row ? filmstripHeight : width);
            double similarityColumn = similarityOpen ? (row ? SimilarityRowWidth(width) : (double)_similarityPane.CrossSize + SimilarityPanelGeometry.Margin) : 0;
            _similarityPane.Hidden = !similarityOpen;
            double fileSize = _main.IsThumbnailViewVisible
                ? MainScreenLayout.FileBarMainLength(row, filterHeight, similarityColumn)
                : 0;
            var controlMeasure = _controlPane.Measure(row, row ? width : height);
            double controlSize = _main.IsControlViewVisible
                ? MainScreenLayout.ControlBarMainLength(row ? (double)controlMeasure.Height : (double)controlMeasure.Width)
                : 0;
            // 分析栏占主图与相邻栏位之间的一条：行布局在主图下方、列布局在主图右侧，
            // 主轴可用长度与交叉轴尺寸（瓦片边长 + 20）都和旧壳 AnalysisView 同口径。
            bool analysisVisible = _main.IsAnalysisViewVisible;
            double analysisMain = row ? width : height;
            double analysisSize = analysisVisible ? _analysisPane.CrossSize(analysisMain) : 0;
            _filePane.Hidden = _fileDivider.Hidden = fileSize == 0;
            _controlScroll.Hidden = _controlDivider.Hidden = controlSize == 0;
            _analysisPane.Hidden = !analysisVisible;
            if (!analysisVisible) HideDetailHighlight();
            _filePane.Layer!.BackgroundColor = NSColor.FromWhite(.067f, 1).CGColor;
            _photoPane.Layer!.BackgroundColor = NSColor.Black.CGColor;
            _controlScroll.BackgroundColor = NSColor.FromWhite(.067f, 1);
            // 两向分区与分隔线由共享 MainScreenLayout 计算（与 iPad 原生壳同一份数学），本壳只落原生 frame。
            var panes = MainScreenLayout.Partition(row, width, height, fileSize, controlSize, analysisSize);
            _filePane.Frame = ToFrame(panes.FileBar);
            _fileDivider.Frame = ToFrame(panes.FileDivider);
            _controlScroll.Frame = ToFrame(panes.ControlBar);
            _controlDivider.Frame = ToFrame(panes.ControlDivider);
            _photoPane.Frame = ToFrame(panes.Photo);
            _analysisPane.Frame = ToFrame(panes.Analysis);
            if (analysisVisible) _analysisPane.Arrange(row, analysisMain);
            Photo.Frame = _photoPane.Bounds;
            _busyPill.Frame = new CGRect(
                Math.Max(0, ((double)_photoPane.Bounds.Width - DesignTokens.ImageBusyWidth) / 2),
                Math.Max(0, (double)_photoPane.Bounds.Height - DesignTokens.ImageBusyBottomOffset - DesignTokens.ImageBusyHeight),
                DesignTokens.ImageBusyWidth,
                DesignTokens.ImageBusyHeight);
            _toast.StackOverBusyPill = !_busyPill.Hidden;
            _toast.Layout(_photoPane.Bounds);
            double hintWidth = Math.Max(1, Math.Min(500, (double)_photoPane.Bounds.Width - 20));
            double hintLeft = ((double)_photoPane.Bounds.Width - hintWidth) / 2;
            double hintTop = Math.Max(0, ((double)_photoPane.Bounds.Height - (_hintDetail.StringValue.Length > 0 ? 80 : 20)) / 2);
            _hint.Frame = new CGRect(hintLeft, hintTop, hintWidth, 20);
            _hintDetail.Frame = new CGRect(hintLeft, hintTop + 28, hintWidth, 52);
            var filterFrames = MainScreenLayout.FilterBarFrames(row, row ? width : MainScreenLayout.FileBarContentWidth, FilterTopInset, countWidth, includeSimilarityToggle: true);
            _sort.Frame = ToFrame(filterFrames.Sort);
            _order.Frame = ToFrame(filterFrames.Order);
            _ratingFilter.Frame = ToFrame(filterFrames.Rating);
            _count.Frame = ToFrame(filterFrames.Count);
            _similarityToggle.Frame = ToFrame(filterFrames.Toggle);
            _scroll.Frame = row
                ? new CGRect(0, filterHeight, Math.Max(1, width - similarityColumn), filmstripHeight)
                : new CGRect(0, filterHeight, MainScreenLayout.FileBarContentWidth, Math.Max(1, height - filterHeight));
            if (similarityOpen)
            {
                _similarityPane.Frame = row
                    ? new CGRect(width - similarityColumn, filterHeight, Math.Max(1, similarityColumn - SimilarityPanelGeometry.Margin), (nfloat)_similarityPane.CrossSize)
                    : new CGRect(MainScreenLayout.FileBarContentWidth, filterHeight, (nfloat)_similarityPane.CrossSize, Math.Max(1, height - filterHeight));
            }
            bool axisChanged = _filmstripLayout.Horizontal != row;
            if (axisChanged || previousViewport != _scroll.ContentView.Bounds.Size) CancelFilmstripScroll();
            _filmstripLayout.Horizontal = row;
            _scroll.Horizontal = row;
            _scroll.HasVerticalScroller = !row; _scroll.HasHorizontalScroller = row;
            if (axisChanged || previousViewport != _scroll.ContentView.Bounds.Size || _collection.Frame.Size != _filmstripLayout.CollectionViewContentSize)
                UpdateFilmstripLayout();
            if (axisChanged) ScrollToCurrent();
            _controlScroll.HasHorizontalScroller = row && controlMeasure.Width > width;
            // TOOLBAR-08：只有列布局且整条文档在竖轴溢出时才开竖向滚动条；行布局不需要竖向滚动，
            // 开着它会让系统在 legacy 滚动条偏好下常驻 17 点竖槽并挤窄裁剪区。
            _controlScroll.HasVerticalScroller = !row && controlMeasure.Height > height;
            _controlPane.Arrange(_controlScroll.ContentView.Bounds.Size);
            // TOOLBAR-08：重建/换向只夹取到合法范围，不把用户滚动位置重置回起点。
            var controlOffset = _controlScroll.ContentView.Bounds.Location;
            var controlDocument = _controlPane.Frame.Size;
            var controlViewport = _controlScroll.ContentView.Bounds.Size;
            var clampedOffset = new CGPoint(
                Math.Clamp((double)controlOffset.X, 0, Math.Max(0, (double)(controlDocument.Width - controlViewport.Width))),
                Math.Clamp((double)controlOffset.Y, 0, Math.Max(0, (double)(controlDocument.Height - controlViewport.Height))));
            if (clampedOffset != controlOffset)
            {
                _controlScroll.ContentView.ScrollToPoint(clampedOffset);
                _controlScroll.ReflectScrolledClipView(_controlScroll.ContentView);
            }
            double dragHeight = row ? 44 : 24;
            // 拖动区必须绕开筛选条的全部可点控件：右区从最后一个控件（相似聚类 toggle）右边缘起，
            // 否则悬停显隐的拖动层会盖住 toggle，让它"有时可点、有时不可点"。
            double dragStart = row && fileSize > 0 ? (double)_sort.Frame.X : width;
            double dragEnd = row && fileSize > 0
                ? (double)_similarityToggle.Frame.X + (double)_similarityToggle.Frame.Width
                : width;
            _dragLeft.Frame = new CGRect(0, 0, dragStart, dragHeight);
            _dragRight.Frame = new CGRect(dragEnd, 0, Math.Max(0, width - dragEnd), dragHeight);
            UpdateViewport();
            ReportVisible();
        }
        finally { _layingOut = false; }
    }

    /// <summary>把共享布局矩形落成原生 frame（两个原生壳共用同一份几何数学）。</summary>
    /// <param name="rect">共享布局矩形。</param>
    /// <returns>原生 frame。</returns>
    private static CGRect ToFrame(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>行布局下相似面板占用的水平长度（SIM-07）：内容宽 = 卡片数 × 96 + 列表两侧内边距 8 + 面板描边 2
    /// + 列表态右侧停靠区宽，下限 MinWidth 106 且不超过可用宽度一半，最后叠加面板右侧 4 点外边距。
    /// 停靠区（未提取计数 / 按钮 / 进度 / 错误）不预留就会把 1–2 项列表挤成零宽。</summary>
    /// <param name="paneWidth">文件栏可用宽度。</param>
    private double SimilarityRowWidth(double paneWidth)
    {
        var panel = _main.FileVM.SimilarityPanel;
        // 停靠区只在列表态且动作区（未提取计数/按钮/进度/错误）真的显示时占宽；全部已入库且无错误时没有停靠区。
        double dockWidth = panel.HasItems && panel.ShowActionArea ? _similarityPane.DockMeasureWidth() : 0;
        return SimilarityPanelGeometry.RowWidth(panel.HasItems, panel.SimilarItems.Count, paneWidth, dockWidth);
    }

    /// <summary>共享设置/EXIF/评级生成控制区；原生符号按钮仍由用户显隐和顺序驱动。</summary>
    private void BuildControls()
    {
        _controlPane.Rebuild(_main, Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); }, ShowTools);
        Layout();
    }

    /// <summary>评分经共享 XMP/同步逻辑写回，失败保留原星级并给出非模态提示（STATE-03）。</summary>
    public async Task RateAsync(ImageFile file, int rating)
    {
        await _main.SetRatingAsync(file, rating);
        if (file.Rating != rating)
        {
            string message = StatusMessages.RatingWriteFailed(file.Name, _main.LastRatingWriteError);
            Console.Error.WriteLine($"[Native] {message}");
            ShowToast(message);
        }
        BuildControls();
    }

    /// <summary>在主图区底部显示一条 STATE-03 提示；新提示替换旧提示并重置计时。</summary>
    /// <param name="message">提示正文。</param>
    internal void ShowToast(string message) => _toast.Show(message);
    /// <summary>执行本阶段可用命令，缩放命令直接作用于原生视口。</summary>
    public void Execute(string command)
    {
        if (command != "Open" && _main.CurrentFile == null) return;
        switch (command)
        {
            case "Open": _ = OpenPickerAsync(false); break;
            case "Previous": Move(-1); break;
            case "Next": Move(1); break;
            case "Exchange": if (_main.LastFile != null) _main.CurrentFile = _main.LastFile; break;
            case "Fit": Photo.ToggleFit(); break;
            case "ZoomInScale": Photo.ZoomBy(1.25); break;
            case "ZoomOutScale": Photo.ZoomBy(.8); break;
            case "ZoomInPreset": ZoomPreset(1); break;
            case "ZoomOutPreset": ZoomPreset(-1); break;
            case "ToggleAnalysisView": _main.ToggleAnalysisView(); break;
            case "ToggleEnhance": Session.ToggleEnhanced(); break;
            default:
                if (command.StartsWith("SetRating", StringComparison.Ordinal) && int.TryParse(command[9..], out int rating) && _main.CurrentFile != null)
                    _ = RateAsync(_main.CurrentFile, rating);
                break;
        }
    }
    /// <summary>在已筛选列表中翻页，列表端点不环绕（FILTER-05：当前照片被筛掉时按重算前索引定位）。</summary>
    public void Move(int offset) => _main.FileVM.ThumbnailList.MoveCurrent(offset);
    /// <summary>按用户预设选择邻近倍率（IMAGE-04：端点或未命中时不动作）。</summary>
    private void ZoomPreset(int direction)
    {
        double? target = ZoomPresets.Next(
            _main.Settings.ScalePresets.Select(preset => preset.Value), Photo.PhysicalScale, direction);
        if (target is double value) Photo.ZoomToPhysical(value);
    }
    /// <summary>匹配原生事件；系统设置入口及 Escape 保留，普通事件按用户手势表，无图时左键打开文件。</summary>
    private bool HandleInput(NSEvent nativeEvent)
    {
        if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask) && nativeEvent.CharactersIgnoringModifiers == ",") { ShowSettings(); return true; }
        string? command = MacInput.ResolveCommand(nativeEvent, _main.Settings);
        // INPUT-05：按住重复只放行可连续执行的命令；其余重复事件消费掉但不执行。
        if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.IsARepeat && command != null && !InputCommandMap.IsRepeatable(command)) return true;
        if (InputCommandMap.IsCommandKnown(command)) { Execute(command!); return true; }
        if (TryOpenEmptyStatePicker(nativeEvent)) return true;
        return false;
    }

    /// <summary>
    /// INPUT-03/10：无照片时只有主图分区内的左键打开文件选择器。命中视图必须就是主图本身——
    /// 空态提示是透传标签（命中落到主图上），导航图、提示条、文件栏、控制栏、分析栏、顶边拖动区与
    /// 窗口按钮（交通灯）都按各自控件处理；带修饰键的左键也不接管。
    /// </summary>
    /// <param name="nativeEvent">进入窗口派发链的原生事件。</param>
    /// <returns>事件是否已被消费。</returns>
    internal bool TryOpenEmptyStatePicker(NSEvent nativeEvent)
    {
        if (nativeEvent.Type != NSEventType.LeftMouseDown || Session.Image != null || nativeEvent.Window != Window) return false;
        if ((nativeEvent.ModifierFlags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask
            | NSEventModifierMask.ControlKeyMask | NSEventModifierMask.ShiftKeyMask)) != 0) return false;
        var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
        if (!_photoPane.Frame.Contains(point)) return false;
        if (!Window.StyleMask.HasFlag(FullScreenStyleBit) && (_dragLeft.Frame.Contains(point) || _dragRight.Frame.Contains(point))) return false;
        var frameView = _root.Superview;
        if (frameView == null || !ReferenceEquals(frameView.HitTest(frameView.ConvertPointFromView(nativeEvent.LocationInWindow, null)), Photo)) return false;
        _ = OpenPickerAsync(false);
        return true;
    }

    /// <summary>当前照片变更时只重置加载代际，不改变原生缩放状态。</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            CancelFilmstripScroll();
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _observedFile = _main.CurrentFile;
            if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
            Session.SetFile(_main.CurrentFile?.File);
            Window.Title = _main.CurrentFile?.Name ?? "PhotoViewer";
            HideDetailHighlight();
            BuildControls();
            UpdateMenuChecks();
        }
        else if (args.PropertyName is nameof(MainViewModel.IsRowLayout) or nameof(MainViewModel.IsThumbnailViewVisible) or nameof(MainViewModel.IsControlViewVisible) or nameof(MainViewModel.IsAnalysisViewVisible))
        {
            if (!_main.IsThumbnailViewVisible) CancelFilmstripScroll();
            Layout(); UpdateMenuChecks();
            if (args.PropertyName == nameof(MainViewModel.IsThumbnailViewVisible) && _main.IsThumbnailViewVisible) ScrollToCurrent();
            if (!_automatedValidation)
            {
                NSUserDefaults.StandardUserDefaults.SetBool(_main.IsThumbnailViewVisible, "PhotoViewer.Native.ShowFilm");
                NSUserDefaults.StandardUserDefaults.SetBool(_main.IsControlViewVisible, "PhotoViewer.Native.ShowControls");
            }
        }
    }
    /// <summary>当前文件 EXIF/星级就绪后刷新原生摘要。</summary>
    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(() =>
    {
        if (_disposed || !ReferenceEquals(sender, _observedFile)) return;
        if (args.PropertyName == nameof(ImageFile.ExifData)) UpdateFocusPoint();
        if (args.PropertyName is nameof(ImageFile.Rating) or nameof(ImageFile.ExifData)) BuildControls();
        UpdateMenuChecks();
    });
    /// <summary>设置回写后重建按钮并重新布局。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args) => ScheduleControlsRefresh();
    /// <summary>共享控制摘要变化通知。</summary>
    private void OnControlChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(_main.ControlVM.EnabledExifItems) or nameof(_main.ControlVM.EnabledControls)) ScheduleControlsRefresh();
    }
    /// <summary>合并快照回写产生的通知，避免同一轮设置改变重复创建全部原生按钮。</summary>
    private void ScheduleControlsRefresh()
    {
        if (_refreshPending || _disposed) return;
        _refreshPending = true;
        UiDispatcher.Post(() =>
        {
            _refreshPending = false;
            if (_disposed) return;
            RebuildActionMenu(); BuildControls(); _collection.ReloadData(); UpdateFilmstripLayout(); ReportVisible(); UpdateMenuChecks();
        });
    }
    /// <summary>列表原子替换后刷新 CollectionView 和计数；后台星级扫描未改变列表顺序时不打断滚动或重建卡片。</summary>
    private void OnFilesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(_main.FileVM.ThumbnailList.FilteredFiles)) return;
        var currentFiles = _main.FileVM.ThumbnailList.FilteredFiles;
        if (_displayedFiles.Length != currentFiles.Count || !_displayedFiles.SequenceEqual(currentFiles))
            CancelFilmstripScroll();
        UiDispatcher.Post(() =>
        {
            if (_disposed) return;
            var files = _main.FileVM.ThumbnailList.FilteredFiles;
            bool sameSequence = _displayedFiles.Length == files.Count && _displayedFiles.SequenceEqual(files);
            _displayedFiles = files.ToArray();
            if (!sameSequence)
            {
                _collection.ReloadData();
                UpdateFilmstripLayout();
            }
            _count.StringValue = $"共 {files.Count} 张";
            Layout();
            ReportVisible();
        });
    }
    /// <summary>业务排序/筛选状态同步回原生控件，程序化或文件源刷新不会留下旧选中项；相似聚类开关同时驱动面板显隐与重排。</summary>
    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args) => UiDispatcher.Post(() =>
    {
        if (_disposed) return;
        var filter = _main.FileVM.FilterBar;
        _sort.SelectItem(filter.SortModes.FindIndex(item => (SortMode)item.Value == filter.SortMode));
        _order.SelectItem(filter.SortOrders.FindIndex(item => (SortOrder)item.Value == filter.SortOrder));
        _ratingFilter.SelectItem(filter.RatingFilters.FindIndex(item => item.Key == filter.SelectedRatingFilter));
        _similarityToggle.IsOn = filter.IsSimilarityPanelOpen;
        if (args.PropertyName == nameof(FilterBarViewModel.IsSimilarityPanelOpen)) Layout();
    });
    /// <summary>项目数与停靠区内容变化时重算行布局面板宽度（SIM-07 要求这两者变化都重算）。</summary>
    private void OnSimilarityPanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SimilarityPanelViewModel.HasItems) or nameof(SimilarityPanelViewModel.SimilarItems)
            or nameof(SimilarityPanelViewModel.UnindexedCount) or nameof(SimilarityPanelViewModel.ShowActionArea)
            or nameof(SimilarityPanelViewModel.ShowActionAreaError))
            UiDispatcher.Post(Layout);
    }
    /// <summary>项目数与视口共同决定文档尺寸，重排不把滚动主轴截短成窗口高度。</summary>
    internal void UpdateFilmstripLayout()
    {
        var position = _scroll.ContentView.Bounds.Location;
        _filmstripLayout.InvalidateLayout();
        var contentSize = _filmstripLayout.CollectionViewContentSize;
        if (_collection.Frame.Size != contentSize) _collection.Frame = new CGRect(CGPoint.Empty, contentSize);
        _collection.LayoutSubtreeIfNeeded();
        var document = _filmstripLayout.CollectionViewContentSize;
        var viewport = _scroll.ContentView.Bounds.Size;
        _scroll.ContentView.ScrollToPoint(new CGPoint(
            Math.Clamp((double)position.X, 0, Math.Max(0, (double)(document.Width - viewport.Width))),
            Math.Clamp((double)position.Y, 0, Math.Max(0, (double)(document.Height - viewport.Height)))));
        _scroll.ReflectScrolledClipView(_scroll.ContentView);
    }
    /// <summary>将会话产物直接交 Metal 上传，避免 WriteableBitmap 的第二份主图缓冲。</summary>
    private void OnImageChanged()
    {
        UpdateFocusPoint();
        Photo.SetImage(Session.Image, Session.Generation);
        // STATE-01：有照片但图未就绪时只在共享 300ms 延迟态可见后提示；STATE-02 的解码失败仍优先。
        _hint.StringValue = Session.Error.Length > 0 ? "无法打开该图片"
            : _main.CurrentFile == null ? "点击打开文件 或 拖拽图片到此处"
            : Session.IsLoadingVisible ? "正在载入…" : "";
        _hintDetail.StringValue = Session.Error;
        _hint.Hidden = _hint.StringValue.Length == 0;
        _hintDetail.Hidden = Session.Error.Length == 0;
        // IMAGE-07：只有共享会话越过 500ms 门槛才显示"计算中…"。
        _busyPill.Hidden = !Session.IsEnhancingVisible;
        // IMAGE-03：屏上已有代理时 Full 升级失败只报非阻塞提示，不覆盖主图错误标题。
        string? fullError = Session.FullError.Length > 0 ? Session.FullError : null;
        if (fullError != _lastFullError)
        {
            _lastFullError = fullError;
            if (fullError != null) ShowToast(fullError);
        }
        UpdateMenuChecks();
        Layout();
    }
    /// <summary>EXIF 异步到达后同步对焦位置，避免主图先完成时 F 仍使用过期空值。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        Photo.FocusPoint = focus is { ImageWidth: > 0, ImageHeight: > 0 } position
            ? new CGPoint(position.FocusX / (double)position.ImageWidth, position.FocusY / (double)position.ImageHeight) : null;
    }
    /// <summary>上报物理视口需求；full/proxy 热替换保持原生变换，分析栏可见时细节预览需要全分辨率源。</summary>
    private void UpdateViewport()
    {
        Session.UpdateViewport((double)Photo.Bounds.Width * (double)Window.BackingScaleFactor, (double)Photo.Bounds.Height * (double)Window.BackingScaleFactor, Photo.NeedsFull || _main.IsAnalysisViewVisible);
        if (_analysisPane != null) RefreshDetailHighlight();
        if (_navigationHost == null) return;
        bool show = _main.Settings.ShowZoomIndicator && !Photo.IsFit && Session.Image != null;
        _navigationHost.Hidden = !show;
        if (!show) return;
        var size = Photo.NavigationSize;
        double width = (double)size.Width, height = (double)size.Height;
        double hostWidth = width + 16, hostHeight = height + 16 + 8 + 15 + 2;
        _navigationHost.Frame = new CGRect(Math.Max(0, (double)_photoPane.Bounds.Width - hostWidth - 12), Math.Max(0, (double)_photoPane.Bounds.Height - hostHeight - 12), hostWidth, hostHeight);
        _navigationImage.Frame = new CGRect(8, 8, width, height);
        _zoomLabel.Frame = new CGRect(8, height + 16, width, 15);
        if (!ReferenceEquals(_navigationSource, Session.Image))
        {
            using var frame = Photo.CaptureLinearFrame(navigation: true);
            using var srgb = PhotoViewer.Core.Color.ColorEngine.ToSrgb8(frame);
            var previous = _navigationBitmap;
            _navigationBitmap = MacSymbols.CreateThumbnail(srgb); _navigationImage.Image = _navigationBitmap;
            previous?.Dispose(); _navigationSource = Session.Image;
        }
        var rectangle = Photo.NavigationViewport;
        _navigationFrame.Frame = new CGRect(rectangle.X + 8, rectangle.Y + 8, rectangle.Width, rectangle.Height);
        _navigationHost.ContentSize = new CGSize(width, height);
        _navigationHost.FrameRect = _navigationFrame.Frame;
        _zoomLabel.StringValue = $"{Photo.PhysicalScale * 100:0.##}%";
    }

    /// <summary>导航图拖动定位（IMAGE-06）：保持当前缩放，把框心移到指针下的归一化图像点。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    private void OnNavigatorCenterRequested(double u, double v) => Photo.CenterOnNormalizedPoint(u, v);
    /// <summary>细节预览高亮：在主图上叠加 3 点绿框（对焦框优先，否则为裁剪窗口）。</summary>
    /// <param name="visible">是否显示绿框。</param>
    /// <param name="u">裁剪中心归一化横坐标。</param>
    /// <param name="v">裁剪中心归一化纵坐标。</param>
    /// <param name="frameWidth">对焦框归一化宽；null 表示用裁剪区。</param>
    /// <param name="frameHeight">对焦框归一化高。</param>
    private void UpdateDetailHighlight(bool visible, double u, double v, double? frameWidth, double? frameHeight)
    {
        _activeDetail = visible ? (u, v, frameWidth, frameHeight) : null;
        RefreshDetailHighlight();
    }

    private void RefreshDetailHighlight()
    {
        if (_activeDetail is not { } source || !_main.IsAnalysisViewVisible || Session.Image == null)
        {
            _detailHighlight.Hidden = true;
            return;
        }
        var image = Session.Image;
        bool swapped = image.Orientation is >= 5 and <= 8;
        double imageWidth = swapped ? image.Height : image.Width;
        double imageHeight = swapped ? image.Width : image.Height;
        double cropSide = Math.Min(_analysisPane.CropPixels, Math.Min(imageWidth, imageHeight));
        double width = source.Width ?? cropSide / imageWidth;
        double height = source.Height ?? cropSide / imageHeight;
        double u = source.Width.HasValue ? source.U : Math.Clamp(source.U, width / 2, 1 - width / 2);
        double v = source.Height.HasValue ? source.V : Math.Clamp(source.V, height / 2, 1 - height / 2);
        double boxWidth = width * Photo.DisplayWidth * Photo.PointScale;
        double boxHeight = height * Photo.DisplayHeight * Photo.PointScale;
        var center = Photo.ViewPoint(u, v);
        _detailHighlight.Frame = new CGRect(center.X - boxWidth / 2, center.Y - boxHeight / 2, boxWidth, boxHeight);
        _detailHighlight.Hidden = false;
    }

    /// <summary>收起主图绿框并清除瓦片活动目标。</summary>
    private void HideDetailHighlight()
    {
        _activeDetail = null;
        _detailHighlight.Hidden = true;
        _analysisPane.ClearDetailHighlight();
    }

    /// <summary>细节预览双击：把主图定位到该归一化点并放大到物理 100%（与旧壳 FocusOnImagePoint 同义）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    private void FocusDetailPoint(double u, double v) => Photo.FocusOnPoint(u, v, 1);

    /// <summary>打开/前置原生工具窗口；未打开时选择默认可用工具，已打开时只前置并保留当前页（TOOLS-01）。</summary>
    public void ShowTools() => _main.OpenToolsWindow();

    /// <summary>系统工具菜单入口：打开同一个窗口并直达指定分类。</summary>
    public void ShowTools(int category)
    {
        ShowTools();
        MacToolsWindowRegistry.Current?.SelectCategory(category);
    }

    /// <summary>把原生可见卡片交共享缩略图调度器，以 P1 优先级加载。</summary>
    private void ReportVisible()
    {
        if (!_layingOut) _collection.LayoutSubtreeIfNeeded();
        var indexes = _collection.IndexPathsForVisibleItems.ToArray<NSIndexPath>().Select(path => (int)path.Item).Order().ToArray();
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        _main.FileVM.ThumbnailList.LoadVisibleThumbnails(indexes.Where(index => index >= 0 && index < files.Count).Select(index => files[index]));
        if (indexes.Length > 0) _main.FileVM.ThumbnailList.ReportVisibleRange(indexes[0], indexes[^1]);
    }
    /// <summary>取消尚未执行的定位请求及正在播放的动画，保持当前可见位置。</summary>
    private void CancelFilmstripScroll()
    {
        _scrollRequestVersion++;
        _scroll.CancelAnimation();
    }

    /// <summary>显式定位请求沿主轴动画居中，首尾受边界约束；普通切图与列表刷新不触发。</summary>
    private void ScrollToCurrent()
    {
        int request = ++_scrollRequestVersion;
        UiDispatcher.Post(() =>
        {
            if (_disposed || request != _scrollRequestVersion || !_main.IsThumbnailViewVisible) return;
            int index = _main.CurrentFile == null ? -1 : _main.FileVM.ThumbnailList.FilteredFiles.IndexOf(_main.CurrentFile);
            if (index < 0) return;
            _collection.LayoutSubtreeIfNeeded();
            using var path = NSIndexPath.FromItemSection(index, 0);
            var attributes = _filmstripLayout.GetLayoutAttributesForItem(path);
            if (attributes == null) return;
            var frame = attributes.Frame;
            var clip = _scroll.ContentView.Bounds;
            var document = _filmstripLayout.CollectionViewContentSize;
            bool horizontal = _filmstripLayout.Horizontal;
            // 主轴目标由共享 FilmstripGeometry 给出（与 iPad 原生壳同一份 FILM-02 几何）。
            double main = FilmstripGeometry.CenterOffset(
                horizontal ? (double)frame.X : (double)frame.Y,
                horizontal ? (double)frame.Width : (double)frame.Height,
                horizontal ? (double)clip.Width : (double)clip.Height,
                horizontal ? (double)document.Width : (double)document.Height);
            _scroll.AnimateTo(horizontal ? new CGPoint(main, clip.Y) : new CGPoint(clip.X, main));
        });
    }

    /// <summary>原生文件/目录选择与共享文件源全链路。</summary>
    public async Task OpenPickerAsync(bool folder)
    {
        try
        {
            if (folder) { var selected = await FilePicker.PickFolderAsync("选择图片文件夹"); if (selected != null) { Photo.FitToView(); await _main.FolderVM.OpenFolderAsync(selected); } }
            else { Photo.FitToView(); await _main.FolderVM.OpenFilePickerAsync(); }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (_automatedValidation) throw new InvalidOperationException(exception.Message);
            ShowToast(StatusMessages.OpenFailed(null, exception.Message));
        }
    }
    /// <summary>命令行等桌面入口按路径打开；一批只取首个可打开目标（OPEN-01）。</summary>
    public Task OpenPathsAsync(IEnumerable<string> paths) => OpenPathsAsync(paths, OpenRequestSource.CommandLine);

    /// <summary>把路径转成本地引用后交共享协调器；等待本批处理完成，供命令行与验收断言。</summary>
    /// <param name="paths">候选路径。</param>
    /// <param name="source">请求来源（命令行 / 拖放 / 系统打开）。</param>
    internal Task OpenPathsAsync(IEnumerable<string> paths, OpenRequestSource source)
    {
        var items = MacOpenPaths.FromPaths(paths);
        return items.Count == 0
            ? Task.CompletedTask
            : _main.OpenCoordinator.PublishAndWaitAsync(items, source);
    }

    /// <summary>重复激活当前目标（再次打开同一目录/照片）：只把已有窗口前置，不重新打开（OPEN-06）。</summary>
    internal void BringToFront()
    {
        if (Window.IsMiniaturized) Window.Deminiaturize(null);
        Window.MakeKeyAndOrderFront(null);
    }

    /// <summary>系统应用菜单，所有查看操作共享与按钮相同的执行入口。</summary>
    private void BuildMenus()
    {
        var menu = CreateMenu();
        var app = CreateMenu("PhotoViewer");
        app.AddItem(Item("设置…", AppleSymbolNames.Menu.Settings, ShowSettings, ","));
        app.AddItem(NSMenuItem.SeparatorItem);
        app.AddItem(Item("退出 PhotoViewer", AppleSymbolNames.Menu.Quit, () => NSApplication.SharedApplication.Terminate(null), "q"));
        menu.AddItem(new NSMenuItem("PhotoViewer") { Submenu = app });
        var file = CreateMenu("文件");
        file.AddItem(Item("打开文件…", AppleSymbolNames.Menu.OpenFile, () => _ = OpenPickerAsync(false)));
        file.AddItem(Item("打开文件夹…", AppleSymbolNames.Menu.OpenFolder, () => _ = OpenPickerAsync(true)));
        menu.AddItem(new NSMenuItem("文件") { Submenu = file });
        var view = CreateMenu("显示");
        AddCheck(view, "胶片栏", () => _main.IsThumbnailViewVisible, _main.ToggleThumbnailView, symbol: AppleSymbolNames.Menu.Filmstrip);
        AddCheck(view, "分析栏", () => _main.IsAnalysisViewVisible, _main.ToggleAnalysisView, symbol: AppleSymbolNames.Menu.Analysis);
        AddCheck(view, "控制栏", () => _main.IsControlViewVisible, _main.ToggleControlView, symbol: AppleSymbolNames.Menu.Control);
        view.AddItem(NSMenuItem.SeparatorItem);
        AddCheck(view, "雾天增强", () => Session.IsEnhanced, () => Execute("ToggleEnhance"), () => _main.CurrentFile != null, AppleSymbolNames.Menu.Enhance);
        menu.AddItem(new NSMenuItem("显示") { Submenu = view });
        var tools = CreateMenu("工具");
        var exif = Item("EXIF 详情…", AppleSymbolNames.Menu.Tools, () => ShowTools(0));
        tools.AddItem(exif);
        _menuAvailability.Add((exif, () => _main.Tools.IsExifDetailEnabled));
        tools.AddItem(Item("DINO 诊断…", AppleSymbolNames.Menu.Analysis, () => ShowTools(1)));
        menu.AddItem(new NSMenuItem("工具") { Submenu = tools });
        _actionsMenu = CreateMenu("操作");
        menu.AddItem(new NSMenuItem("操作") { Submenu = _actionsMenu });
        RebuildActionMenu();
        NSApplication.SharedApplication.MainMenu = menu;
        UpdateMenuChecks();
    }
    /// <summary>AppKit 自动启用会在打开菜单时覆盖业务禁用态；统一关闭，并在每次打开前同步。</summary>
    private NSMenu CreateMenu(string title = "")
    {
        var menu = new NSMenu(title) { AutoEnablesItems = false };
        _menuDelegate ??= new MenuDelegate(this);
        menu.Delegate = _menuDelegate;
        return menu;
    }

    private sealed class MenuDelegate(MacMainWindow owner) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu) => owner.UpdateMenuChecks();
    }

    /// <summary>操作菜单始终列出完整设置命令，控制栏显隐只影响按钮。</summary>
    private void RebuildActionMenu()
    {
        if (_actionsMenu == null) return;
        _menuAvailability.RemoveAll(entry => ReferenceEquals(entry.Item.Menu, _actionsMenu));
        _actionsMenu.RemoveAllItems();
        foreach (var hotkey in _main.Settings.Hotkeys)
        {
            string command = hotkey.Command;
            var item = Item(hotkey.Name, AppleSymbolNames.ForCommand(command), () => Execute(command));
            _actionsMenu.AddItem(item);
            _menuAvailability.Add((item, () => command == "Open" || _main.CurrentFile != null));
        }
        UpdateMenuChecks();
    }
    /// <summary>原生菜单项勾选与可用性复用同一业务属性（MENU-07：无照片时雾天增强置灰）。</summary>
    /// <param name="menu">目标菜单。</param>
    /// <param name="title">菜单项文案。</param>
    /// <param name="isChecked">勾选态来源。</param>
    /// <param name="action">执行入口。</param>
    /// <param name="isEnabled">可用性来源；null 表示始终可用。</param>
    /// <param name="symbol">SF Symbols 名称；null 表示不显示图标。</param>
    private void AddCheck(NSMenu menu, string title, Func<bool> isChecked, Action action, Func<bool>? isEnabled = null, string? symbol = null)
    {
        var item = Item(title, symbol, action); menu.AddItem(item); _menuChecks.Add((item, isChecked, isEnabled));
    }
    /// <summary>同步菜单当前勾选与可用性，避免按钮、菜单与布局状态分叉。</summary>
    private void UpdateMenuChecks()
    {
        foreach (var check in _menuChecks)
        {
            check.Item.State = check.Checked() ? NSCellStateValue.On : NSCellStateValue.Off;
            check.Item.Enabled = check.Enabled?.Invoke() ?? true;
        }
        foreach (var entry in _menuAvailability) entry.Item.Enabled = entry.Enabled();
    }
    private static NSMenuItem Item(string title, string? symbol, Action action, string? shortcut = null)
    {
        var item = shortcut == null
            ? new NSMenuItem(title, (_, _) => action())
            : new NSMenuItem(title, shortcut, (_, _) => action());
        if (symbol != null) item.Image = MacSymbols.Get(symbol);
        return item;
    }
    /// <summary>照片原生右键菜单，顺序与设计一致：打开、栏位显隐、雾天增强、工具、设置。</summary>
    private NSMenu ContextMenu()
    {
        var menu = CreateMenu();
        menu.AddItem(Item("打开文件…", AppleSymbolNames.Menu.OpenFile, () => _ = OpenPickerAsync(false)));
        menu.AddItem(Item("打开文件夹…", AppleSymbolNames.Menu.OpenFolder, () => _ = OpenPickerAsync(true)));
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddCheck(menu, "胶片栏", () => _main.IsThumbnailViewVisible, _main.ToggleThumbnailView, symbol: AppleSymbolNames.Menu.Filmstrip);
        AddCheck(menu, "分析栏", () => _main.IsAnalysisViewVisible, _main.ToggleAnalysisView, symbol: AppleSymbolNames.Menu.Analysis);
        AddCheck(menu, "控制栏", () => _main.IsControlViewVisible, _main.ToggleControlView, symbol: AppleSymbolNames.Menu.Control);
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddCheck(menu, "雾天增强", () => Session.IsEnhanced, () => Execute("ToggleEnhance"), () => _main.CurrentFile != null, AppleSymbolNames.Menu.Enhance);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item("工具…", AppleSymbolNames.Menu.Tools, ShowTools));
        menu.AddItem(Item("设置…", AppleSymbolNames.Menu.Settings, ShowSettings));
        UpdateMenuChecks();
        return menu;
    }

    /// <summary>OPEN-05：显式打开时主图回到适应态。</summary>
    private void OnFitToViewRequested() => Photo.FitToView();

    /// <summary>释放窗口观察者与渲染器，取消所有在途展示任务；重复调用安全。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.OpenCoordinator.RepeatActivation -= BringToFront;
        CancelFilmstripScroll();
        NSNotificationCenter.DefaultCenter.RemoveObserver(_scrollObserver); _scrollObserver.Dispose(); NSEvent.RemoveMonitor(_inputMonitor); _inputMonitor.Dispose();
        NSNotificationCenter.DefaultCenter.RemoveObserver(_liveScrollObserver); _liveScrollObserver.Dispose();
        NSNotificationCenter.DefaultCenter.RemoveObserver(_deactivationObserver); _deactivationObserver.Dispose();
        _main.FitToViewRequested -= OnFitToViewRequested;
        _main.OpenFailed -= ShowToast;
        _main.PropertyChanged -= OnMainChanged; _main.Settings.PropertyChanged -= OnSettingsChanged; _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged; _main.ControlVM.PropertyChanged -= OnControlChanged;
        _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
        _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= ScrollToCurrent;
        if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
        Session.Dispose(); _navigationBitmap?.Dispose(); _analysisPane.Dispose(); _controlPane.Dispose(); _similarityPane.Dispose(); Photo.Dispose(); Window.Dispose();
        Session.Notice -= ShowToast;
        _toast.Dispose(); _busyPill.Dispose();
    }

    /// <summary>CollectionView 按需创建卡片，不为全部文件创建视图。</summary>
    private sealed class CollectionSource(MacMainWindow owner) : NSCollectionViewDataSource
    {
        /// <summary>胶片栏始终是单个 section，集合更新后先布局再滚动。</summary>
        public override nint GetNumberOfSections(NSCollectionView collectionView) => 1;
        /// <summary>返回筛选后项目数。</summary>
        public override nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner._main.FileVM.ThumbnailList.FilteredFiles.Count;
        /// <summary>复用原生卡片并安排可见缩略图加载。</summary>
        public override NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (MacThumbnailItem)collectionView.MakeItem("PhotoCard", indexPath);
            if (owner._filmstripLayout.GetLayoutAttributesForItem(indexPath) is { } attributes && item.View.Frame != attributes.Frame)
                item.View.Frame = attributes.Frame;
            item.View.Hidden = false;
            var file = owner._main.FileVM.ThumbnailList.FilteredFiles[(int)indexPath.Item];
            item.Bind(file, owner._main.Settings.ShowRating, selected =>
            {
                owner.CancelFilmstripScroll();
                owner._main.FileVM.ThumbnailList.SelectImageCommand(selected);
            }, (selected, rating) => _ = owner.RateAsync(selected, rating));
            owner._main.FileVM.ThumbnailList.QueueThumbnailLoad(file, true);
            return item;
        }
    }

    /// <summary>主窗口全内容区承载，拖入照片/文件夹交共享打开逻辑。</summary>
    private sealed class RootView : NSView
    {
        public Action? Resized { get; set; }
        public Action<string[]>? OpenDroppedPaths { get; set; }
        public override bool IsFlipped => true;
        /// <summary>注册本地 URL 拖放。</summary>
        public RootView(CGRect frame) : base(frame) { RegisterForDraggedTypes(["public.file-url"]); }
        /// <summary>窗口尺寸变化驱动原生布局。</summary>
        public override void SetFrameSize(CGSize newSize) { base.SetFrameSize(newSize); Resized?.Invoke(); }
        /// <summary>声明可读取的文件 URL 拖放。</summary>
        public override NSDragOperation DraggingEntered(INSDraggingInfo sender) => NSDragOperation.Copy;
        /// <summary>从系统 pasteboard 读取本地 URL。</summary>
        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            var objects = sender.DraggingPasteboard.ReadObjectsForClasses([new ObjCRuntime.Class(typeof(NSUrl))], null);
            var paths = objects?.OfType<NSUrl>().Select(url => url.Path).Where(path => path != null).Cast<string>().ToArray() ?? [];
            if (paths.Length == 0) return false; OpenDroppedPaths?.Invoke(paths); return true;
        }
    }

    /// <summary>文件与控制分区采用顶部为原点的布局坐标。</summary>
    private sealed class FlippedPane : NSView { public override bool IsFlipped => true; }

    /// <summary>
    /// 缩放指示器宿主（IMAGE-06）：白框可拖动定位。
    /// 按下框内保留抓取偏移，按在缩略图其他位置先把框心移到该点；拖动中把框心换算成归一化图像点回传。
    /// </summary>
    private sealed class NavigatorHostView : NSView
    {
        /// <summary>白框拖动请求（归一化图像坐标）。</summary>
        public Action<double, double>? CenterRequested { get; set; }

        /// <summary>白框在宿主坐标内的 frame。</summary>
        public CGRect FrameRect { get; set; }

        /// <summary>缩略图逻辑尺寸。</summary>
        public CGSize ContentSize { get; set; }

        /// <summary>缩略图距宿主左上角的内边距（nav.padding）。</summary>
        private const double ContentInset = 8;

        private bool _dragging;
        private double _grabX;
        private double _grabY;

        /// <summary>顶部为原点，与导航图几何一致。</summary>
        public override bool IsFlipped => true;

        /// <inheritdoc/>
        public override void MouseDown(NSEvent e)
        {
            var point = ConvertPointFromView(e.LocationInWindow, null);
            Begin(point.X - ContentInset, point.Y - ContentInset);
        }

        /// <inheritdoc/>
        public override void MouseDragged(NSEvent e)
        {
            if (!_dragging) return;
            var point = ConvertPointFromView(e.LocationInWindow, null);
            Emit(point.X - ContentInset, point.Y - ContentInset);
        }

        /// <inheritdoc/>
        public override void MouseUp(NSEvent e) => _dragging = false;

        private void Begin(double x, double y)
        {
            double contentWidth = ContentSize.Width, contentHeight = ContentSize.Height;
            if (x < 0 || y < 0 || x > contentWidth || y > contentHeight) return;
            double left = FrameRect.X - ContentInset, top = FrameRect.Y - ContentInset;
            double width = FrameRect.Width, height = FrameRect.Height;
            if (width <= 0 || height <= 0) return;
            bool inside = x >= left && x <= left + width && y >= top && y <= top + height;
            _grabX = inside ? x - left : width / 2;
            _grabY = inside ? y - top : height / 2;
            _dragging = true;
            if (!inside) Emit(x, y);
        }

        private void Emit(double x, double y)
        {
            double contentWidth = ContentSize.Width, contentHeight = ContentSize.Height;
            double frameWidth = FrameRect.Width, frameHeight = FrameRect.Height;
            if (contentWidth <= 0 || contentHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return;
            double left = Math.Clamp(x - _grabX, 0, Math.Max(0, contentWidth - frameWidth));
            double top = Math.Clamp(y - _grabY, 0, Math.Max(0, contentHeight - frameHeight));
            CenterRequested?.Invoke((left + frameWidth / 2) / contentWidth, (top + frameHeight / 2) / contentHeight);
        }
    }

    /// <summary>透明叠加层（细节预览绿框）：顶边为原点且不拦截鼠标，事件透传给下层主图。</summary>
    private sealed class PassThroughPane : NSView
    {
        /// <summary>构造透明图层容器。</summary>
        public PassThroughPane()
        {
            WantsLayer = true;
            Layer!.BackgroundColor = NSColor.Clear.CGColor;
        }
        /// <summary>顶边为原点的布局坐标。</summary>
        public override bool IsFlipped => true;
        /// <summary>不参与命中测试，鼠标事件交给主图。</summary>
        public override NSView? HitTest(CGPoint aPoint) => null;
    }

    /// <summary>只读展示标签，鼠标命中透传给下层视图。</summary>
    private sealed class PassThroughLabel : NSTextField
    {
        /// <summary>不拦截鼠标事件，使主图空状态提示可穿透点击。</summary>
        public override NSView? HitTest(CGPoint aPoint) => null;
    }

    /// <summary>顶边悬停才显示的透明拖动区，上下布局中间留空给筛选控件。</summary>
    private sealed class DragZone : NSView
    {
        /// <summary>默认收起，不占用布局高度。</summary>
        public DragZone() { Hidden = true; WantsLayer = true; Layer!.BackgroundColor = NSColor.FromWhite(.08f, .55f).CGColor; }
        internal Func<NSEvent, bool>? DoubleClick { get; set; }
        /// <summary>真实原生命中后处理拖动与标题栏双击，不在窗口事件监视器中抢占系统命中。</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            if (DoubleClick?.Invoke(theEvent) != true) Window?.PerformWindowDrag(theEvent);
        }
    }
}
