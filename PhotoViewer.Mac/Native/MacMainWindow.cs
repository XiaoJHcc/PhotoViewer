using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 主窗口；复用共享业务 VM，加载与展示通过 PhotoLoadSession/MetalPhotoView 分离。</summary>
public sealed class MacMainWindow : IDisposable
{
    private const double FilterWidth = 90;
    private const double FilterHeight = 32;
    /// <summary>筛选条内容间距(旧壳 <c>StackPanel Spacing=5</c>)。</summary>
    private const double FilterGap = 5;
    /// <summary>相似聚类 toggle 高度(旧壳 12 点字 + 6,4 内边距的实际量得值)。</summary>
    private const double FilterToggleHeight = 27;
    /// <summary>行布局下筛选条四周外边距(旧壳 <c>Margin=10</c>)。</summary>
    private const double FilterMargin = 10;
    /// <summary>列布局下筛选条顶边:为隐藏标题栏的交通灯让位(旧壳平台标题栏设置)。</summary>
    private const double FilterTopInset = 34;
    /// <summary>相似面板 MinWidth(旧壳 <c>SimilarityListView</c>)。</summary>
    private const double SimilarityMinWidth = 106;
    /// <summary>相似面板右侧外边距(旧壳 <c>Margin="0,0,4,0"</c>)。</summary>
    private const double SimilarityMargin = 4;
    /// <summary>相似列表两侧内边距合计(旧壳行布局 ItemsPanel <c>Margin="4,4,4,4"</c>)。</summary>
    private const double SimilarityListPadding = 8;
    /// <summary>相似面板左右各 1 点描边。</summary>
    private const double SimilarityBorderWidth = 1;
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
    private readonly NSView _navigationHost;
    private readonly NSImageView _navigationImage;
    private readonly NSView _navigationFrame;
    private readonly NSTextField _zoomLabel;
    private NSImage? _navigationBitmap;
    private DecodedImage? _navigationSource;
    private readonly List<(NSMenuItem Item, Func<bool> Checked)> _menuChecks = [];
    private readonly NSObject _scrollObserver;
    private readonly NSObject _liveScrollObserver;
    private readonly NSObject _inputMonitor;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private ImageFile? _observedFile;
    private ImageFile[] _displayedFiles = [];
    private bool _layingOut;
    private bool _refreshPending;
    private bool _disposed;
    private int _scrollRequestVersion;
    private readonly bool _automatedValidation;
    private MacNativeSettingsPresenter? _settingsPresenter;

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
    /// <summary>顶边悬停拖动区供验收核对它们是否绕开了筛选条控件。</summary>
    internal IReadOnlyList<NSView> DragZones => [_dragLeft, _dragRight];

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
        _controlScroll = new NSScrollView { DocumentView = _controlPane, HasVerticalScroller = true, DrawsBackground = true, BackgroundColor = NSColor.FromWhite(.07f, 1), BorderType = NSBorderType.NoBorder, ScrollerStyle = NSScrollerStyle.Overlay, AutomaticallyAdjustsContentInsets = false };
        _root.AddSubview(_filePane); _root.AddSubview(_photoPane); _root.AddSubview(_controlScroll);
        _root.AddSubview(_fileDivider); _root.AddSubview(_controlDivider);
        _dragLeft = new DragZone(); _dragRight = new DragZone();
        _root.AddSubview(_dragLeft); _root.AddSubview(_dragRight);
        Photo = new MetalPhotoView(new CGRect(0, 0, 1000, 750));
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
        _navigationHost = Pane(NSColor.FromWhite(2f / 15, .6f));
        _navigationHost.Layer!.CornerRadius = 6;
        _navigationImage = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        _navigationFrame = Pane(NSColor.Clear); _navigationFrame.Layer!.BorderColor = NSColor.White.CGColor; _navigationFrame.Layer.BorderWidth = 1;
        _zoomLabel = Label("", 12); _zoomLabel.Alignment = NSTextAlignment.Center;
        _zoomLabel.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold) ?? throw new InvalidOperationException("系统字体不可用");
        _navigationHost.AddSubview(_navigationImage); _navigationHost.AddSubview(_navigationFrame); _navigationHost.AddSubview(_zoomLabel);
        _photoPane.AddSubview(_navigationHost); _navigationHost.Hidden = true;
        _sort = Popup(_main.FileVM.FilterBar.SortModes.Select(item => item.DisplayName).ToArray());
        _order = Popup(_main.FileVM.FilterBar.SortOrders.Select(item => item.DisplayName).ToArray());
        _ratingFilter = Popup(_main.FileVM.FilterBar.RatingFilters.Select(item => item.DisplayName).ToArray());
        _count = Label("共 0 张", 12); _count.Alignment = NSTextAlignment.Center;
        _similarityToggle = new FilterToggleButton("相似聚类", "展开/关闭相似聚类面板");
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
        _inputMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown | NSEventMask.LeftMouseDown | NSEventMask.MouseMoved | NSEventMask.MouseExited, nativeEvent =>
        {
            if (nativeEvent.Type is NSEventType.MouseMoved or NSEventType.MouseExited)
            {
                var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
                bool show = nativeEvent.Window == Window && Window.IsKeyWindow && nativeEvent.Type == NSEventType.MouseMoved && point.Y >= 0 && point.Y <= 50;
                _dragLeft.Hidden = !show; _dragRight.Hidden = !show;
                return nativeEvent;
            }
            if (HandleTitlebarDoubleClick(nativeEvent)) return null!;
            if (nativeEvent.Window != Window || Window.FirstResponder is NSTextView or NSTextField) return nativeEvent;
            return HandleInput(nativeEvent) ? null! : nativeEvent;
        });
        Photo.InputHandler = HandleInput;
        Photo.ViewportChanged += UpdateViewport;
        Window.DidChangeBackingProperties += (_, _) => Photo.SetFrameSize(Photo.Frame.Size);
        Window.DidEnterFullScreen += (_, _) => SaveFullscreen(true);
        Window.DidExitFullScreen += (_, _) => SaveFullscreen(false);
        Window.DidResignKey += (_, _) => { _dragLeft.Hidden = true; _dragRight.Hidden = true; };
        Session.Changed += OnImageChanged;
        _root.Resized = Layout;
        _root.OpenDroppedPaths = paths => _ = OpenPathsAsync(paths);
        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += ScrollToCurrent;
        BuildControls();
        BuildMenus();
        _root.Menu = ContextMenu();
        Photo.Menu = _root.Menu;
        Layout();
        ExternalOpenService.RegisterHandler(HandleExternalOpenAsync);
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
    /// <summary>顶边拖动区内的标题栏双击：命中即执行系统偏好动作并吞掉事件(否则会落到主图/筛选控件上)。
    /// 判定挂在窗口级事件监视器上而不是拖动区视图自身 —— 拖动区只在悬停时显示，若靠命中测试，
    /// 未先移动鼠标就直接双击会落到主图上。返回 true 表示事件已消费。</summary>
    /// <param name="nativeEvent">进入窗口派发链的原生事件。</param>
    internal bool HandleTitlebarDoubleClick(NSEvent nativeEvent)
    {
        if (nativeEvent.Type != NSEventType.LeftMouseDown || nativeEvent.ClickCount < 2 || nativeEvent.Window != Window) return false;
        // 全屏没有标题栏：不接管双击，把它留给主图自己的双击语义。
        if (Window.StyleMask.HasFlag(FullScreenStyleBit)) return false;
        var point = _root.ConvertPointFromView(nativeEvent.LocationInWindow, null);
        if (!_dragLeft.Frame.Contains(point) && !_dragRight.Frame.Contains(point)) return false;
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
        var popup = new FilterPopUpButton(new CGRect(0, 0, FilterWidth, FilterHeight))
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
    /// 勾选态按旧壳 <c>ToggleButton.SimToggle</c> 换成 <c>#1F3A5F</c> 底 / <c>#3A6FA8</c> 边 / <c>#AECBEE</c> 字，
    /// 未勾选为透明底 / <c>#555</c> 边 / <c>#AAA</c> 字。
    /// 勾选状态由本类自己持有（不用 <c>NSButtonType.Toggle</c>）：旧写法的 getter 读私有字段而点击翻转的是 cell 状态，
    /// 导致点一次读回旧值、按钮表现为"点不动"；同时 Toggle 态会取 alternateTitle，只设 AttributedTitle 会露出系统占位串 "Button"。</summary>
    private sealed class FilterToggleButton : NSButton
    {
        private const double CornerRadius = 6;
        private static readonly NSColor BorderOff = NSColor.FromRgb(0x55, 0x55, 0x55);
        private static readonly NSColor TextOff = NSColor.FromRgb(0xAA, 0xAA, 0xAA);
        private static readonly NSColor FillOn = NSColor.FromRgb(0x1F, 0x3A, 0x5F);
        private static readonly NSColor BorderOn = NSColor.FromRgb(0x3A, 0x6F, 0xA8);
        private static readonly NSColor TextOn = NSColor.FromRgb(0xAE, 0xCB, 0xEE);
        private static readonly NSColor FillOffHover = NSColor.FromRgb(0x33, 0x33, 0x33);
        private static readonly NSColor BorderOffHover = NSColor.FromRgb(0x66, 0x66, 0x66);
        private static readonly NSColor TextOffHover = NSColor.FromRgb(0xCC, 0xCC, 0xCC);
        private static readonly NSColor FillOnHover = NSColor.FromRgb(0x26, 0x47, 0x7A);
        private static readonly NSColor BorderOnHover = NSColor.FromRgb(0x4A, 0x85, 0xC4);
        private static readonly NSColor TextOnHover = NSColor.FromRgb(0xCF, 0xE0, 0xF5);
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
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            var previousViewport = _scroll.ContentView.Bounds.Size;
            double width = (double)_root.Bounds.Width, height = (double)_root.Bounds.Height;
            _main.UpdateScreenOrientation(width, height);
            bool row = _main.IsRowLayout;
            _count.SizeToFit();
            double countWidth = Math.Ceiling((double)_count.Frame.Width);
            const double countHeight = 17;
            double filterContentWidth = FilterWidth * 4 + FilterGap * 4 + countWidth;
            double filterHeight = row
                ? FilterHeight + FilterMargin * 2
                : FilterTopInset + FilterHeight * 3 + FilterGap * 3 + countHeight + FilterGap + FilterToggleHeight + FilterMargin;
            double filmstripHeight = FilmstripGeometry.CardHeight + FilmstripGeometry.TopPadding + FilmstripGeometry.BottomPadding;
            // 相似聚类面板：列布局由面板自定的宽度扩宽文件栏，行布局按旧壳 MaxWidth 规则从胶片栏切出右侧一段。
            bool similarityOpen = _main.IsThumbnailViewVisible && _main.FileVM.FilterBar.IsSimilarityPanelOpen;
            if (similarityOpen) _similarityPane.SetLayout(row, row ? filmstripHeight : width);
            double similarityColumn = similarityOpen ? (row ? SimilarityRowWidth(width) : (double)_similarityPane.CrossSize + SimilarityMargin) : 0;
            _similarityPane.Hidden = !similarityOpen;
            double fileSize = _main.IsThumbnailViewVisible ? (row ? filterHeight + filmstripHeight + 1 : 117 + similarityColumn) : 0;
            var controlMeasure = _controlPane.Measure(row, row ? width : height);
            double controlSize = _main.IsControlViewVisible ? (row ? (double)controlMeasure.Height : (double)controlMeasure.Width) + 1 : 0;
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
            _filePane.Frame = row ? new CGRect(0, 0, width, Math.Max(0, fileSize - 1)) : new CGRect(0, 0, Math.Max(0, fileSize - 1), height);
            _fileDivider.Frame = row ? new CGRect(0, fileSize - 1, width, 1) : new CGRect(fileSize - 1, 0, 1, height);
            _controlScroll.Frame = row ? new CGRect(0, height - controlSize + 1, width, Math.Max(0, controlSize - 1)) : new CGRect(width - controlSize + 1, 0, Math.Max(0, controlSize - 1), height);
            _controlDivider.Frame = row ? new CGRect(0, height - controlSize, width, 1) : new CGRect(width - controlSize, 0, 1, height);
            double photoWidth = row ? width : Math.Max(1, width - fileSize - analysisSize - controlSize);
            double photoHeight = row ? Math.Max(1, height - fileSize - analysisSize - controlSize) : height;
            _photoPane.Frame = row ? new CGRect(0, fileSize, photoWidth, photoHeight) : new CGRect(fileSize, 0, photoWidth, photoHeight);
            _analysisPane.Frame = row
                ? new CGRect(0, fileSize + photoHeight, width, analysisSize)
                : new CGRect(fileSize + photoWidth, 0, analysisSize, photoHeight);
            if (analysisVisible) _analysisPane.Arrange(row, analysisMain);
            Photo.Frame = _photoPane.Bounds;
            double hintWidth = Math.Max(1, Math.Min(500, (double)_photoPane.Bounds.Width - 20));
            double hintLeft = ((double)_photoPane.Bounds.Width - hintWidth) / 2;
            double hintTop = Math.Max(0, ((double)_photoPane.Bounds.Height - (_hintDetail.StringValue.Length > 0 ? 80 : 20)) / 2);
            _hint.Frame = new CGRect(hintLeft, hintTop, hintWidth, 20);
            _hintDetail.Frame = new CGRect(hintLeft, hintTop + 28, hintWidth, 52);
            double filterTop = row ? FilterMargin : FilterTopInset;
            if (row)
            {
                double start = (width - filterContentWidth) / 2;
                _sort.Frame = new CGRect(start, filterTop, FilterWidth, FilterHeight);
                _order.Frame = new CGRect(start + FilterWidth + FilterGap, filterTop, FilterWidth, FilterHeight);
                _ratingFilter.Frame = new CGRect(start + (FilterWidth + FilterGap) * 2, filterTop, FilterWidth, FilterHeight);
                _count.Frame = new CGRect(start + (FilterWidth + FilterGap) * 3, filterTop + (FilterHeight - countHeight) / 2, countWidth, countHeight);
                _similarityToggle.Frame = new CGRect(start + (FilterWidth + FilterGap) * 3 + countWidth + FilterGap, filterTop + (FilterHeight - FilterToggleHeight) / 2, FilterWidth, FilterToggleHeight);
                _scroll.Frame = new CGRect(0, filterHeight, Math.Max(1, width - similarityColumn), filmstripHeight);
            }
            else
            {
                double filterLeft = (116 - FilterWidth) / 2;
                _sort.Frame = new CGRect(filterLeft, filterTop, FilterWidth, FilterHeight);
                _order.Frame = new CGRect(filterLeft, filterTop + FilterHeight + FilterGap, FilterWidth, FilterHeight);
                _ratingFilter.Frame = new CGRect(filterLeft, filterTop + (FilterHeight + FilterGap) * 2, FilterWidth, FilterHeight);
                _count.Frame = new CGRect((116 - countWidth) / 2, filterTop + (FilterHeight + FilterGap) * 3, countWidth, countHeight);
                _similarityToggle.Frame = new CGRect(filterLeft, filterTop + (FilterHeight + FilterGap) * 3 + countHeight + FilterGap, FilterWidth, FilterToggleHeight);
                _scroll.Frame = new CGRect(0, filterHeight, 116, Math.Max(1, height - filterHeight));
            }
            if (similarityOpen)
            {
                _similarityPane.Frame = row
                    ? new CGRect(width - similarityColumn, filterHeight, Math.Max(1, similarityColumn - SimilarityMargin), (nfloat)_similarityPane.CrossSize)
                    : new CGRect(116, filterHeight, (nfloat)_similarityPane.CrossSize, Math.Max(1, height - filterHeight));
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
            _controlPane.Arrange(_controlScroll.ContentView.Bounds.Size);
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

    /// <summary>行布局下相似面板占用的水平长度:与旧壳 <c>SimilarityListView</c> 的 MaxWidth 规则一致 ——
    /// 内容宽(卡片数 × 96 + 列表两侧内边距 8 + 面板描边 2,下限 MinWidth 106)与文件栏一半取小,再叠加面板右侧 4 点外边距。</summary>
    /// <param name="paneWidth">文件栏可用宽度。</param>
    private double SimilarityRowWidth(double paneWidth)
    {
        var panel = _main.FileVM.SimilarityPanel;
        double content = panel.HasItems
            ? panel.SimilarItems.Count * FilmstripGeometry.CardWidth + SimilarityListPadding + SimilarityBorderWidth * 2
            : SimilarityMinWidth;
        return Math.Min(Math.Max(SimilarityMinWidth, content), paneWidth / 2) + SimilarityMargin;
    }

    /// <summary>共享设置/EXIF/评级生成控制区；原生符号按钮仍由用户显隐和顺序驱动。</summary>
    private void BuildControls()
    {
        _controlPane.Rebuild(_main, Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); }, ShowTools);
        Layout();
    }

    /// <summary>评分经共享 XMP/同步逻辑写回，失败保留原星级并给出可见错误。</summary>
    public async Task RateAsync(ImageFile file, int rating)
    {
        await _main.SetRatingAsync(file, rating);
        if (file.Rating != rating) ShowError("无法写入星级，请检查文件权限或文件格式");
        BuildControls();
    }
    /// <summary>执行本阶段可用命令，缩放命令直接作用于原生视口。</summary>
    public void Execute(string command)
    {
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
    /// <summary>在已筛选列表中翻页，列表端点不环绕。</summary>
    public void Move(int offset)
    {
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        int index = _main.CurrentFile == null ? -1 : files.IndexOf(_main.CurrentFile);
        int target = index + offset;
        if (target >= 0 && target < files.Count) { _main.CurrentFile = files[target]; _main.FileVM.ThumbnailList.ScrollToCurrent(); }
    }
    /// <summary>按用户预设选择邻近倍率。</summary>
    private void ZoomPreset(int direction)
    {
        var values = _main.Settings.ScalePresets.Select(preset => preset.Value).Order().ToArray();
        double? target = direction > 0 ? values.Where(value => value > Photo.PhysicalScale + .001).Cast<double?>().FirstOrDefault()
            : values.Where(value => value < Photo.PhysicalScale - .001).Reverse().Cast<double?>().FirstOrDefault();
        if (target.HasValue) Photo.ZoomToPhysical(target.Value);
    }
    /// <summary>匹配原生事件；系统设置入口及 Escape 保留，普通事件按用户手势表，无图时左键打开文件。</summary>
    private bool HandleInput(NSEvent nativeEvent)
    {
        if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask) && nativeEvent.CharactersIgnoringModifiers == ",") { ShowSettings(); return true; }
        string? command = MacInput.ResolveCommand(nativeEvent, _main.Settings);
        if (command != null && AppleSymbolNames.ForCommand(command) != null) { Execute(command); return true; }
        if (nativeEvent.Type == NSEventType.LeftMouseDown && Session.Image == null && (nativeEvent.ModifierFlags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.ControlKeyMask | NSEventModifierMask.ShiftKeyMask)) == 0)
        {
            _ = OpenPickerAsync(false);
            return true;
        }
        return false;
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
            BuildControls(); _collection.ReloadData(); UpdateFilmstripLayout(); ReportVisible(); UpdateMenuChecks();
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
    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        var filter = _main.FileVM.FilterBar;
        _sort.SelectItem(filter.SortModes.FindIndex(item => (SortMode)item.Value == filter.SortMode));
        _order.SelectItem(filter.SortOrders.FindIndex(item => (SortOrder)item.Value == filter.SortOrder));
        _ratingFilter.SelectItem(filter.RatingFilters.FindIndex(item => item.Key == filter.SelectedRatingFilter));
        _similarityToggle.IsOn = filter.IsSimilarityPanelOpen;
        if (args.PropertyName == nameof(FilterBarViewModel.IsSimilarityPanelOpen)) Layout();
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
        Photo.SetImage(Session.Image);
        _hint.StringValue = Session.Error.Length > 0 ? "无法打开该图片" : _main.CurrentFile == null ? "点击打开文件 或 拖拽图片到此处" : "";
        _hintDetail.StringValue = Session.Error;
        _hint.Hidden = _hint.StringValue.Length == 0;
        _hintDetail.Hidden = Session.Error.Length == 0;
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
        _zoomLabel.StringValue = $"{Photo.PhysicalScale * 100:0.##}%";
    }
    /// <summary>细节预览高亮：在主图上叠加 3 点绿框（对焦框优先，否则为裁剪窗口）。</summary>
    /// <param name="visible">是否显示绿框。</param>
    /// <param name="u">裁剪中心归一化横坐标。</param>
    /// <param name="v">裁剪中心归一化纵坐标。</param>
    /// <param name="frameWidth">对焦框归一化宽；null 表示用裁剪区。</param>
    /// <param name="frameHeight">对焦框归一化高。</param>
    private void UpdateDetailHighlight(bool visible, double u, double v, double? frameWidth, double? frameHeight)
    {
        if (!visible || !_main.IsAnalysisViewVisible || Session.Image == null)
        {
            _detailHighlight.Hidden = true;
            return;
        }
        double scale = Photo.PointScale;
        double boxWidth, boxHeight;
        if (frameWidth is { } width && frameHeight is { } height)
        {
            boxWidth = width * Photo.DisplayWidth * scale;
            boxHeight = height * Photo.DisplayHeight * scale;
        }
        else
        {
            boxWidth = boxHeight = _analysisPane.CropPixels * scale;
        }
        var center = Photo.ViewPoint(u, v);
        _detailHighlight.Frame = new CGRect(center.X - boxWidth / 2, center.Y - boxHeight / 2, boxWidth, boxHeight);
        _detailHighlight.Hidden = false;
    }

    /// <summary>收起主图绿框（分析栏隐藏或切换照片时）。</summary>
    private void HideDetailHighlight() => _detailHighlight.Hidden = true;

    /// <summary>细节预览双击：把主图定位到该归一化点并放大到物理 100%（与旧壳 FocusOnImagePoint 同义）。</summary>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    private void FocusDetailPoint(double u, double v) => Photo.FocusOnPoint(u, v, 1);

    /// <summary>打开/前置原生工具窗口，与旧壳右键菜单「工具」同一入口。</summary>
    public void ShowTools() => NativeWindowPresenter.ShowOrActivateToolsWindow(_main.Tools);

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
            double horizontal = _filmstripLayout.Horizontal ? (double)(frame.X + (frame.Width - clip.Width) / 2) : (double)clip.X;
            double vertical = _filmstripLayout.Horizontal ? (double)clip.Y : (double)(frame.Y + (frame.Height - clip.Height) / 2);
            var document = _filmstripLayout.CollectionViewContentSize;
            _scroll.AnimateTo(new CGPoint(
                Math.Clamp(horizontal, 0, Math.Max(0, (double)(document.Width - clip.Width))),
                Math.Clamp(vertical, 0, Math.Max(0, (double)(document.Height - clip.Height)))));
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
        catch (Exception exception) { Console.Error.WriteLine(exception); ShowError(exception.Message); }
    }
    /// <summary>串行接收 Finder/命令行/拖入路径，确保最后选中的是请求目标。</summary>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        await _openGate.WaitAsync();
        try
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) { Photo.FitToView(); await _main.FolderVM.OpenFolderAsync(new MacFolderRef(path)); break; }
                if (File.Exists(path) && _main.FolderVM.IsImageFile(System.IO.Path.GetFileName(path))) { Photo.FitToView(); await _main.FolderVM.OpenImageAsync(new MacFileRef(path)); break; }
            }
            Window.MakeKeyAndOrderFront(null);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); ShowError(exception.Message); }
        finally { _openGate.Release(); }
    }
    /// <summary>共享外部打开桥接消费入口。</summary>
    private Task HandleExternalOpenAsync(ExternalOpenRequest request)
        => UiDispatcher.InvokeAsync(() => OpenPathsAsync(request.Items.Select(item => item.Path.LocalPath))).Unwrap();
    /// <summary>简短原生错误警告。</summary>
    private void ShowError(string message)
    {
        Console.Error.WriteLine("[Native] " + message);
        if (_automatedValidation) throw new InvalidOperationException(message);
        using var alert = new NSAlert { MessageText = "PhotoViewer", InformativeText = message, AlertStyle = NSAlertStyle.Warning }; alert.RunModal();
    }

    /// <summary>系统应用菜单，所有查看操作共享与按钮相同的执行入口。</summary>
    private void BuildMenus()
    {
        var menu = new NSMenu();
        var app = new NSMenu("PhotoViewer"); app.AddItem(new NSMenuItem("设置…", ",", (_, _) => ShowSettings()));
        app.AddItem(NSMenuItem.SeparatorItem); app.AddItem(new NSMenuItem("退出 PhotoViewer", "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));
        menu.AddItem(new NSMenuItem("PhotoViewer") { Submenu = app });
        var file = new NSMenu("文件"); file.AddItem(new NSMenuItem("打开文件…", (_, _) => _ = OpenPickerAsync(false))); file.AddItem(new NSMenuItem("打开文件夹…", (_, _) => _ = OpenPickerAsync(true)));
        menu.AddItem(new NSMenuItem("文件") { Submenu = file });
        var view = new NSMenu("显示");
        AddCheck(view, "胶片栏", () => _main.IsThumbnailViewVisible, _main.ToggleThumbnailView);
        AddCheck(view, "控制栏", () => _main.IsControlViewVisible, _main.ToggleControlView);
        foreach (var layout in _main.Settings.LayoutModes) AddCheck(view, layout.DisplayName, () => _main.Settings.LayoutMode == layout.Value, () => _main.Settings.LayoutMode = layout.Value);
        view.AddItem(new NSMenuItem("全屏", (_, _) => Window.ToggleFullScreen(null)));
        menu.AddItem(new NSMenuItem("显示") { Submenu = view });
        NSApplication.SharedApplication.MainMenu = menu;
        UpdateMenuChecks();
    }
    /// <summary>原生显示菜单勾选状态复用同一业务属性。</summary>
    private void AddCheck(NSMenu menu, string title, Func<bool> isChecked, Action action)
    {
        var item = new NSMenuItem(title, (_, _) => action()); menu.AddItem(item); _menuChecks.Add((item, isChecked));
    }
    /// <summary>同步菜单当前勾选，避免按钮、菜单与布局状态分叉。</summary>
    private void UpdateMenuChecks() { foreach (var check in _menuChecks) check.Item.State = check.Checked() ? NSCellStateValue.On : NSCellStateValue.Off; }
    /// <summary>照片原生右键菜单，顺序与旧壳一致：打开、栏位显隐（胶片/分析/控制）、工具、设置。</summary>
    private NSMenu ContextMenu()
    {
        var menu = new NSMenu();
        menu.AddItem(new NSMenuItem("打开文件…", (_, _) => _ = OpenPickerAsync(false)));
        menu.AddItem(new NSMenuItem("打开文件夹…", (_, _) => _ = OpenPickerAsync(true)));
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddCheck(menu, "胶片栏", () => _main.IsThumbnailViewVisible, _main.ToggleThumbnailView);
        AddCheck(menu, "分析栏", () => _main.IsAnalysisViewVisible, _main.ToggleAnalysisView);
        AddCheck(menu, "控制栏", () => _main.IsControlViewVisible, _main.ToggleControlView);
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("工具", (_, _) => ShowTools()));
        menu.AddItem(new NSMenuItem("设置…", (_, _) => ShowSettings()));
        UpdateMenuChecks();
        return menu;
    }

    /// <summary>释放窗口观察者与渲染器，取消所有在途展示任务。</summary>
    public void Dispose()
    {
        _disposed = true;
        CancelFilmstripScroll();
        NSNotificationCenter.DefaultCenter.RemoveObserver(_scrollObserver); _scrollObserver.Dispose(); NSEvent.RemoveMonitor(_inputMonitor); _inputMonitor.Dispose();
        NSNotificationCenter.DefaultCenter.RemoveObserver(_liveScrollObserver); _liveScrollObserver.Dispose();
        _main.PropertyChanged -= OnMainChanged; _main.Settings.PropertyChanged -= OnSettingsChanged; _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged; _main.ControlVM.PropertyChanged -= OnControlChanged;
        _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= ScrollToCurrent;
        if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
        Session.Dispose(); _navigationBitmap?.Dispose(); _analysisPane.Dispose(); _controlPane.Dispose(); _similarityPane.Dispose(); Photo.Dispose(); Window.Dispose();
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
        /// <summary>拖动交由系统窗口移动，保留原生吸附与屏幕行为；双击由窗口级监视器接管(见 HandleTitlebarDoubleClick)。</summary>
        public override void MouseDown(NSEvent theEvent) => Window?.PerformWindowDrag(theEvent);
    }
}
