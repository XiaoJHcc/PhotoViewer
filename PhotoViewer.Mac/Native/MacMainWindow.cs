using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 主窗口；复用共享业务 VM，加载与展示通过 PhotoLoadSession/MetalPhotoView 分离。</summary>
public sealed class MacMainWindow : IDisposable
{
    private readonly MainViewModel _main;
    private readonly RootView _root;
    private readonly NSView _filePane;
    private readonly NSView _controlPane;
    private readonly NSScrollView _controlScroll;
    private readonly NSView _photoPane;
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
    private readonly NSTextField _hint;
    private readonly NSView _navigationHost;
    private readonly NSImageView _navigationImage;
    private readonly NSView _navigationFrame;
    private readonly NSTextField _zoomLabel;
    private NSImage? _navigationBitmap;
    private DecodedImage? _navigationSource;
    private readonly List<NSView> _controls = [];
    private readonly HashSet<NSView> _ratingControls = [];
    private readonly List<(NSMenuItem Item, Func<bool> Checked)> _menuChecks = [];
    private readonly NSObject _scrollObserver;
    private readonly NSObject _inputMonitor;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private ImageFile? _observedFile;
    private bool _layingOut;
    private bool _refreshPending;
    private bool _disposed;
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
    /// <summary>真实胶片条与滚动容器供布局回归读取，不另建模拟列表。</summary>
    internal NSCollectionView Filmstrip => _collection;
    /// <summary>真实胶片条滚动容器。</summary>
    internal NSScrollView FilmstripScroll => _scroll;

    /// <summary>创建窗口、筛选、虚拟化胶片条与控制栏，并连接共享通知。</summary>
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
        _filePane = Pane(); _controlPane = Pane(); _photoPane = Pane(NSColor.Black);
        _controlScroll = new NSScrollView { DocumentView = _controlPane, HasVerticalScroller = true, DrawsBackground = false, AutomaticallyAdjustsContentInsets = false };
        _root.AddSubview(_filePane); _root.AddSubview(_photoPane); _root.AddSubview(_controlScroll);
        _dragLeft = new DragZone(); _dragRight = new DragZone();
        _root.AddSubview(_dragLeft); _root.AddSubview(_dragRight);
        Photo = new MetalPhotoView(new CGRect(0, 0, 1000, 750));
        _photoPane.AddSubview(Photo);
        _hint = Label("点击打开文件 或 拖拽图片到此处", 14);
        _hint.Alignment = NSTextAlignment.Center;
        _photoPane.AddSubview(_hint);
        _navigationHost = Pane(NSColor.FromWhite(.12f, .9f));
        _navigationHost.Layer!.CornerRadius = 6;
        _navigationImage = new NSImageView(new CGRect(8, 8, 128, 128)) { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        _navigationFrame = Pane(NSColor.Clear); _navigationFrame.Layer!.BorderColor = NSColor.White.CGColor; _navigationFrame.Layer.BorderWidth = 1;
        _zoomLabel = Label("", 12); _zoomLabel.Alignment = NSTextAlignment.Center;
        _navigationHost.AddSubview(_navigationImage); _navigationHost.AddSubview(_navigationFrame); _navigationHost.AddSubview(_zoomLabel);
        _photoPane.AddSubview(_navigationHost); _navigationHost.Hidden = true;
        _sort = Popup(_main.FileVM.FilterBar.SortModes.Select(item => item.DisplayName).ToArray());
        _order = Popup(_main.FileVM.FilterBar.SortOrders.Select(item => item.DisplayName).ToArray());
        _ratingFilter = Popup(_main.FileVM.FilterBar.RatingFilters.Select(item => item.DisplayName).ToArray());
        _count = Label("共 0 张", 12); _count.Alignment = NSTextAlignment.Center;
        _sort.Activated += (_, _) => _main.FileVM.FilterBar.SortMode = (SortMode)_main.FileVM.FilterBar.SortModes[(int)_sort.IndexOfSelectedItem].Value;
        _order.Activated += (_, _) => _main.FileVM.FilterBar.SortOrder = (SortOrder)_main.FileVM.FilterBar.SortOrders[(int)_order.IndexOfSelectedItem].Value;
        _ratingFilter.Activated += (_, _) => _main.FileVM.FilterBar.SelectedRatingFilter = _main.FileVM.FilterBar.RatingFilters[(int)_ratingFilter.IndexOfSelectedItem].Key;
        foreach (var control in new NSView[] { _sort, _order, _ratingFilter, _count }) _filePane.AddSubview(control);
        _filmstripLayout = new MacFilmstripLayout(() => _main.FileVM.ThumbnailList.FilteredFiles.Count);
        _collection = new NSCollectionView(new CGRect(0, 0, 116, 680)) { CollectionViewLayout = _filmstripLayout, Selectable = false, BackgroundColors = [NSColor.FromWhite(.07f, 1)] };
        _collection.RegisterClassForItem(typeof(MacThumbnailItem), "PhotoCard");
        _source = new CollectionSource(this); _collection.DataSource = _source;
        _scroll = new MacFilmstripScrollView { DocumentView = _collection, HasVerticalScroller = true, DrawsBackground = false, AutomaticallyAdjustsContentInsets = false };
        _scroll.ContentView.PostsBoundsChangedNotifications = true;
        _filePane.AddSubview(_scroll);
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
    /// <summary>创建深色观片分区，使用 layer 而不重绘大位图。</summary>
    private static NSView Pane(NSColor? color = null)
    {
        var pane = new FlippedPane { WantsLayer = true };
        pane.Layer!.BackgroundColor = (color ?? NSColor.FromWhite(.07f, 1)).CGColor;
        return pane;
    }
    /// <summary>创建只读原生标签。</summary>
    private static NSTextField Label(string text, double size)
        => new() { StringValue = text, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false, TextColor = NSColor.LightGray, Font = NSFont.SystemFontOfSize((nfloat)size) ?? throw new InvalidOperationException("系统字体不可用") };
    /// <summary>创建固定位置的原生下拉框。</summary>
    private static NSPopUpButton Popup(string[] titles)
    {
        var popup = new NSPopUpButton(new CGRect(0, 0, 96, 26), false); popup.AddItems(titles); return popup;
    }

    /// <summary>依据共享上下/左右/自动布局排列；主图延伸到顶部，局部避让交通灯。</summary>
    private void Layout()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            double width = (double)_root.Bounds.Width, height = (double)_root.Bounds.Height;
            _main.UpdateScreenOrientation(width, height);
            bool row = _main.IsRowLayout;
            double fileSize = _main.IsThumbnailViewVisible ? (row ? 218 : 132) : 0;
            int controlItems = Math.Max(1, _controls.Count);
            double controlSize = _main.IsControlViewVisible ? (row ? Math.Max(64, Math.Ceiling(controlItems * 54.0 / Math.Max(300, width)) * 54 + 16) : 80) : 0;
            _filePane.Hidden = fileSize == 0; _controlScroll.Hidden = controlSize == 0;
            _filePane.Frame = row ? new CGRect(0, 0, width, fileSize) : new CGRect(0, 0, fileSize, height);
            _controlScroll.Frame = row ? new CGRect(0, height - controlSize, width, controlSize) : new CGRect(width - controlSize, 0, controlSize, height);
            _photoPane.Frame = row ? new CGRect(0, fileSize, width, Math.Max(1, height - fileSize - controlSize)) : new CGRect(fileSize, 0, Math.Max(1, width - fileSize - controlSize), height);
            Photo.Frame = _photoPane.Bounds;
            _hint.Frame = new CGRect(10, Math.Max(0, _photoPane.Bounds.Height / 2 - 20), Math.Max(1, _photoPane.Bounds.Width - 20), 80);
            _navigationHost.Frame = new CGRect(Math.Max(0, _photoPane.Bounds.Width - 156), Math.Max(0, _photoPane.Bounds.Height - 174), 144, 162);
            _zoomLabel.Frame = new CGRect(8, 138, 128, 20);
            double filterTop = row ? 9 : 30;
            if (row)
            {
                double start = Math.Max(90, (width - 400) / 2);
                _sort.Frame = new CGRect(start, filterTop, 96, 26); _order.Frame = new CGRect(start + 100, filterTop, 96, 26);
                _ratingFilter.Frame = new CGRect(start + 200, filterTop, 96, 26); _count.Frame = new CGRect(start + 300, filterTop + 3, 96, 22);
                _scroll.Frame = new CGRect(8, 42, Math.Max(1, width - 16), 172);
            }
            else
            {
                _sort.Frame = new CGRect(10, filterTop, 96, 26); _order.Frame = new CGRect(10, filterTop + 31, 96, 26);
                _ratingFilter.Frame = new CGRect(10, filterTop + 62, 96, 26); _count.Frame = new CGRect(10, filterTop + 94, 96, 20);
                _scroll.Frame = new CGRect(6, filterTop + 118, fileSize - 12, Math.Max(1, height - filterTop - 122));
            }
            bool axisChanged = _filmstripLayout.Horizontal != row;
            _filmstripLayout.Horizontal = row;
            _scroll.Horizontal = row;
            _scroll.HasVerticalScroller = !row; _scroll.HasHorizontalScroller = row;
            UpdateFilmstripLayout();
            if (axisChanged) ScrollToCurrent();
            double cursorX = row ? 10 : 15, cursorY = row ? 8 : 40;
            foreach (var control in _controls)
            {
                double itemWidth = control is NSTextField ? 70 : _ratingControls.Contains(control) ? 30 : 40;
                double itemHeight = _ratingControls.Contains(control) ? 30 : 40;
                if (row && cursorX + itemWidth > width - 10) { cursorX = 10; cursorY += 54; }
                control.Frame = new CGRect(row ? cursorX : (controlSize - itemWidth) / 2, cursorY, itemWidth, itemHeight);
                if (row) cursorX += itemWidth + 8;
                else cursorY += itemHeight + 8;
            }
            _controlPane.Frame = new CGRect(0, 0, row ? width : controlSize, Math.Max(row ? controlSize : height, cursorY + 48));
            double dragHeight = row ? 44 : 24;
            double gapStart = row && fileSize > 0 ? (double)_sort.Frame.X : width;
            double gapEnd = row && fileSize > 0 ? (double)(_count.Frame.X + _count.Frame.Width) : width;
            _dragLeft.Frame = new CGRect(0, 0, gapStart, dragHeight);
            _dragRight.Frame = new CGRect(gapEnd, 0, Math.Max(0, width - gapEnd), dragHeight);
            UpdateViewport();
            ReportVisible();
        }
        finally { _layingOut = false; }
    }

    /// <summary>共享设置/EXIF/评级生成控制区；原生符号按钮仍由用户显隐和顺序驱动。</summary>
    private void BuildControls()
    {
        foreach (var control in _controls) { control.RemoveFromSuperview(); control.Dispose(); }
        _controls.Clear();
        _ratingControls.Clear();
        foreach (var entry in _main.ControlVM.EnabledExifItems)
        {
            var label = Label(entry.DisplayName + "\n" + entry.DisplayValue, 11); label.Alignment = NSTextAlignment.Center;
            _controls.Add(label); _controlPane.AddSubview(label);
        }
        if (_main.Settings.ShowRating)
            for (int rating = 0; rating < 6; rating++)
            {
                int value = rating;
                bool active = rating > 0 && rating <= (_main.CurrentFile?.Rating ?? 0);
                var star = MacSymbols.Button(rating == 0 ? "xmark" : active ? "star.fill" : "star", rating + " 星", () => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, value); }, 30, false);
                star.ContentTintColor = active ? NSColor.FromRgb(255, 210, 20) : NSColor.Gray;
                _controls.Add(star); _controlPane.AddSubview(star);
                _ratingControls.Add(star);
            }
        foreach (var hotkey in _main.ControlVM.EnabledControls)
        {
            string? symbol = MacSymbols.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            var button = MacSymbols.Button(symbol, hotkey.Name + " · " + hotkey.PrimaryHotkeyText, () => Execute(hotkey.Command));
            _controls.Add(button); _controlPane.AddSubview(button);
        }
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
        if (target >= 0 && target < files.Count) { _main.CurrentFile = files[target]; ScrollToCurrent(); }
    }
    /// <summary>按用户预设选择邻近倍率。</summary>
    private void ZoomPreset(int direction)
    {
        var values = _main.Settings.ScalePresets.Select(preset => preset.Value).Order().ToArray();
        double? target = direction > 0 ? values.Where(value => value > Photo.PhysicalScale + .001).Cast<double?>().FirstOrDefault()
            : values.Where(value => value < Photo.PhysicalScale - .001).Reverse().Cast<double?>().FirstOrDefault();
        if (target.HasValue) Photo.ZoomToPhysical(target.Value);
    }
    /// <summary>匹配原生事件；系统设置入口及 Escape 保留，普通事件按用户手势表。</summary>
    private bool HandleInput(NSEvent nativeEvent)
    {
        if (nativeEvent.Type == NSEventType.KeyDown && nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask) && nativeEvent.CharactersIgnoringModifiers == ",") { ShowSettings(); return true; }
        string? command = MacInput.ResolveCommand(nativeEvent, _main.Settings);
        if (command == null || MacSymbols.ForCommand(command) == null) return false;
        Execute(command); return true;
    }

    /// <summary>当前照片变更时只重置加载代际，不改变原生缩放状态。</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentFile))
        {
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _observedFile = _main.CurrentFile;
            if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
            Session.SetFile(_main.CurrentFile?.File);
            ScrollToCurrent();
            Window.Title = _main.CurrentFile?.Name ?? "PhotoViewer";
            BuildControls();
        }
        else if (args.PropertyName is nameof(MainViewModel.IsRowLayout) or nameof(MainViewModel.IsThumbnailViewVisible) or nameof(MainViewModel.IsControlViewVisible))
        {
            Layout(); UpdateMenuChecks();
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
        if (args.PropertyName is nameof(ImageFile.Rating) or nameof(ImageFile.ExifData)) ScheduleControlsRefresh();
    });
    /// <summary>设置回写后重建按钮并重新布局。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args) => ScheduleControlsRefresh();
    /// <summary>共享控制摘要变化通知。</summary>
    private void OnControlChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(_main.ControlVM.EnabledExifItems)) ScheduleControlsRefresh(); }
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
    /// <summary>列表原子替换后刷新 CollectionView 和计数。</summary>
    private void OnFilesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(_main.FileVM.ThumbnailList.FilteredFiles)) return;
        UiDispatcher.Post(() =>
        {
            if (_disposed) return;
            _collection.ReloadData(); UpdateFilmstripLayout();
            _count.StringValue = $"共 {_main.FileVM.ThumbnailList.FilteredFiles.Count} 张";
            ScrollToCurrent(); ReportVisible();
        });
    }
    /// <summary>业务排序/筛选状态同步回原生控件，程序化或文件源刷新不会留下旧选中项。</summary>
    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        var filter = _main.FileVM.FilterBar;
        _sort.SelectItem(filter.SortModes.FindIndex(item => (SortMode)item.Value == filter.SortMode));
        _order.SelectItem(filter.SortOrders.FindIndex(item => (SortOrder)item.Value == filter.SortOrder));
        _ratingFilter.SelectItem(filter.RatingFilters.FindIndex(item => item.Key == filter.SelectedRatingFilter));
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
        _hint.StringValue = Session.Error.Length > 0 ? Session.Error : _main.CurrentFile == null ? "点击打开文件 或 拖拽图片到此处" : "";
        _hint.Hidden = Session.Image != null && Session.Error.Length == 0;
    }
    /// <summary>EXIF 异步到达后同步对焦位置，避免主图先完成时 F 仍使用过期空值。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        Photo.FocusPoint = focus is { ImageWidth: > 0, ImageHeight: > 0 } position
            ? new CGPoint(position.FocusX / (double)position.ImageWidth, position.FocusY / (double)position.ImageHeight) : null;
    }
    /// <summary>上报物理视口需求；full/proxy 热替换保持原生变换。</summary>
    private void UpdateViewport()
    {
        Session.UpdateViewport((double)Photo.Bounds.Width * (double)Window.BackingScaleFactor, (double)Photo.Bounds.Height * (double)Window.BackingScaleFactor, Photo.NeedsFull);
        if (_navigationHost == null) return;
        bool show = _main.Settings.ShowZoomIndicator && !Photo.IsFit && Session.Image != null;
        _navigationHost.Hidden = !show;
        if (!show) return;
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
    /// <summary>把原生可见卡片交共享缩略图调度器，以 P1 优先级加载。</summary>
    private void ReportVisible()
    {
        var indexes = _collection.IndexPathsForVisibleItems.ToArray<NSIndexPath>().Select(path => (int)path.Item).Order().ToArray();
        var files = _main.FileVM.ThumbnailList.FilteredFiles;
        _main.FileVM.ThumbnailList.LoadVisibleThumbnails(indexes.Where(index => index >= 0 && index < files.Count).Select(index => files[index]));
        if (indexes.Length > 0) _main.FileVM.ThumbnailList.ReportVisibleRange(indexes[0], indexes[^1]);
    }
    /// <summary>当前项自动滚到可见位置，保留原生滚动行为。</summary>
    private void ScrollToCurrent()
    {
        UiDispatcher.Post(() =>
        {
            if (_disposed) return;
            int index = _main.CurrentFile == null ? -1 : _main.FileVM.ThumbnailList.FilteredFiles.IndexOf(_main.CurrentFile);
            if (index < 0) return;
            _collection.LayoutSubtreeIfNeeded();
            using var path = NSIndexPath.FromItemSection(index, 0);
            var attributes = _filmstripLayout.GetLayoutAttributesForItem(path);
            if (attributes == null) return;
            var frame = attributes.Frame;
            var clip = _scroll.ContentView.Bounds;
            double horizontal = (double)clip.X, vertical = (double)clip.Y;
            if (frame.X < clip.X) horizontal = (double)frame.X;
            else if (frame.X + frame.Width > clip.X + clip.Width) horizontal = (double)(frame.X + frame.Width - clip.Width);
            if (frame.Y < clip.Y) vertical = (double)frame.Y;
            else if (frame.Y + frame.Height > clip.Y + clip.Height) vertical = (double)(frame.Y + frame.Height - clip.Height);
            var document = _filmstripLayout.CollectionViewContentSize;
            _scroll.ContentView.ScrollToPoint(new CGPoint(
                Math.Clamp(horizontal, 0, Math.Max(0, (double)(document.Width - clip.Width))),
                Math.Clamp(vertical, 0, Math.Max(0, (double)(document.Height - clip.Height)))));
            _scroll.ReflectScrolledClipView(_scroll.ContentView);
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
        AddCheck(view, "胶片条", () => _main.IsThumbnailViewVisible, _main.ToggleThumbnailView);
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
    /// <summary>照片原生右键菜单，顺延功能没有失效入口。</summary>
    private NSMenu ContextMenu()
    {
        var menu = new NSMenu();
        menu.AddItem(new NSMenuItem("打开文件…", (_, _) => _ = OpenPickerAsync(false)));
        menu.AddItem(new NSMenuItem("打开文件夹…", (_, _) => _ = OpenPickerAsync(true)));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("胶片条", (_, _) => _main.ToggleThumbnailView()));
        menu.AddItem(new NSMenuItem("控制栏", (_, _) => _main.ToggleControlView()));
        menu.AddItem(new NSMenuItem("设置…", (_, _) => ShowSettings())); return menu;
    }

    /// <summary>释放窗口观察者与渲染器，取消所有在途展示任务。</summary>
    public void Dispose()
    {
        _disposed = true;
        NSNotificationCenter.DefaultCenter.RemoveObserver(_scrollObserver); _scrollObserver.Dispose(); NSEvent.RemoveMonitor(_inputMonitor); _inputMonitor.Dispose();
        _main.PropertyChanged -= OnMainChanged; _main.Settings.PropertyChanged -= OnSettingsChanged; _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged; _main.ControlVM.PropertyChanged -= OnControlChanged;
        _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= ScrollToCurrent;
        if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
        Session.Dispose(); _navigationBitmap?.Dispose(); Photo.Dispose(); Window.Dispose();
    }

    /// <summary>CollectionView 按需创建卡片，不为全部文件创建视图。</summary>
    private sealed class CollectionSource(MacMainWindow owner) : NSCollectionViewDataSource
    {
        /// <summary>胶片条始终是单个 section，集合更新后先布局再滚动。</summary>
        public override nint GetNumberOfSections(NSCollectionView collectionView) => 1;
        /// <summary>返回筛选后项目数。</summary>
        public override nint GetNumberofItems(NSCollectionView collectionView, nint section) => owner._main.FileVM.ThumbnailList.FilteredFiles.Count;
        /// <summary>复用原生卡片并安排可见缩略图加载。</summary>
        public override NSCollectionViewItem GetItem(NSCollectionView collectionView, NSIndexPath indexPath)
        {
            var item = (MacThumbnailItem)collectionView.MakeItem("PhotoCard", indexPath);
            var file = owner._main.FileVM.ThumbnailList.FilteredFiles[(int)indexPath.Item];
            item.Bind(file, owner._main.Settings.ShowRating, selected => owner._main.CurrentFile = selected, (selected, rating) => _ = owner.RateAsync(selected, rating));
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

    /// <summary>顶边悬停才显示的透明拖动区，上下布局中间留空给筛选控件。</summary>
    private sealed class DragZone : NSView
    {
        /// <summary>默认收起，不占用布局高度。</summary>
        public DragZone() { Hidden = true; WantsLayer = true; Layer!.BackgroundColor = NSColor.FromWhite(.08f, .55f).CGColor; }
        /// <summary>拖动交由系统窗口移动，保留原生吸附与屏幕行为。</summary>
        public override void MouseDown(NSEvent theEvent) => Window?.PerformWindowDrag(theEvent);
    }
}
