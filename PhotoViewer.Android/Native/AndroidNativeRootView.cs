using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生主屏根视图（A3）：按共享 <see cref="MainScreenLayout"/> 与
/// <see cref="AndroidMainScreenLayout"/> 拼合文件栏（筛选条 + 胶片栏）、主图区与控制栏；
/// 栏位背景铺到屏幕边缘、内容收在系统栏/刘海安全区内，行/列/自动布局只看窗口方向。
/// 主图由 <see cref="AndroidPhotoView"/> 渲染，物理视口上报共享 <see cref="PhotoLoadSession"/>。
/// </summary>
internal sealed class AndroidNativeRootView : FrameLayout
{
    /// <summary>星级写回失败提示的展示时长（ms）。</summary>
    private const int StatusDurationMs = 4000;

    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly AndroidPhotoView _photo;
    private readonly FrameLayout _photoPane;
    private readonly View _fileBar;
    private readonly View _fileDivider;
    private readonly View _controlBackground;
    private readonly View _controlDivider;
    private readonly AndroidFilterBar _filterBar;
    private readonly AndroidFilmstripView _filmstrip;
    private readonly AndroidControlBar _controlBar;
    private readonly TextView _hintLabel;
    private readonly TextView _hintDetail;
    private readonly TextView _enhanceBusy;
    private readonly TextView _statusLabel;
    private readonly AndroidNavigationOverlay _navigation;
    private AndroidInsets _insets;
    private AndroidPaneFrames _panes;
    private ImageFile? _observedFile;
    private DecodedImage? _navigationSource;
    private Bitmap? _navigationBitmap;
    private int _navigationToken;
    private bool _photoReady;
    private bool _startPending;
    private bool _layingOut;
    private bool _disposed;

    /// <summary>主图宿主（验收读取视口/手势状态）。</summary>
    internal AndroidPhotoView Photo => _photo;
    /// <summary>胶片栏（验收读取卡片与偏移）。</summary>
    internal AndroidFilmstripView Filmstrip => _filmstrip;
    /// <summary>控制栏（验收读取分组与命令）。</summary>
    internal AndroidControlBar ControlBar => _controlBar;
    /// <summary>筛选条（验收读取控件几何）。</summary>
    internal AndroidFilterBar FilterBar => _filterBar;
    /// <summary>最近一次布局算出的分区矩形（dp）。</summary>
    internal AndroidPaneFrames Panes => _panes;
    /// <summary>导航图宿主。</summary>
    internal AndroidNavigationOverlay Navigation => _navigation;
    /// <summary>空态提示所在的主图分区。</summary>
    internal FrameLayout PhotoPane => _photoPane;
    /// <summary>当前安全区（dp）。</summary>
    internal AndroidInsets SafeInsets => _insets;

    /// <summary>建立主屏视图树并接线共享 VM 与会话。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示加载会话。</param>
    public AndroidNativeRootView(Activity activity, MainViewModel main, PhotoLoadSession session) : base(activity)
    {
        _main = main;
        _session = session;
        SetBackgroundColor(AndroidUiTheme.Background);
        SetClipChildren(true);

        _photoPane = new FrameLayout(activity);
        _photoPane.SetClipChildren(true);
        _photoPane.SetBackgroundColor(AndroidUiTheme.PhotoBackground);
        _photo = new AndroidPhotoView(activity);
        _photoPane.AddView(_photo, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        AddView(_photoPane, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));

        _fileBar = new View(activity);
        _fileBar.SetBackgroundColor(AndroidUiTheme.BarBackground);
        AddView(_fileBar);
        _fileDivider = new View(activity);
        _fileDivider.SetBackgroundColor(AndroidUiTheme.Divider);
        AddView(_fileDivider);
        _controlBackground = new View(activity);
        _controlBackground.SetBackgroundColor(AndroidUiTheme.BarBackground);
        AddView(_controlBackground);
        _controlDivider = new View(activity);
        _controlDivider.SetBackgroundColor(AndroidUiTheme.Divider);
        AddView(_controlDivider);

        _filterBar = new AndroidFilterBar(activity, main);
        AddView(_filterBar);
        _filmstrip = new AndroidFilmstripView(activity, main);
        AddView(_filmstrip);
        _filmstrip.RateRequested += OnRateRequested;
        _controlBar = new AndroidControlBar(activity, main);
        _controlBar.Wire(Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); }, ShowTools);
        AddView(_controlBar);

        _hintLabel = new TextView(activity)
        {
            Text = "PhotoViewer",
            TextSize = 17,
            Gravity = GravityFlags.Center,
        };
        _hintLabel.SetTextColor(AndroidUiTheme.TextPrimary);
        _hintDetail = new TextView(activity)
        {
            Text = "点按选择文件夹",
            TextSize = 13,
            Gravity = GravityFlags.Center,
        };
        _hintDetail.SetTextColor(AndroidUiTheme.TextSecondary);
        _hintDetail.SetPadding(0, (int)AndroidUiTheme.Dp(activity, 6), 0, 0);
        _hintLabel.Clickable = true;
        _hintLabel.Click += (_, _) => { if (_session.Image == null) _ = _main.FolderVM.OpenFilePickerAsync(); };
        AddView(_hintLabel);
        AddView(_hintDetail);

        _enhanceBusy = new TextView(activity)
        {
            Text = "计算中…",
            TextSize = 13,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone,
        };
        _enhanceBusy.SetTextColor(Color.White);
        _enhanceBusy.SetBackgroundColor(Color.Argb(217, 34, 34, 34)!);
        AddView(_enhanceBusy);

        _statusLabel = new TextView(activity)
        {
            TextSize = 13,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone,
        };
        _statusLabel.SetTextColor(Color.White);
        _statusLabel.SetBackgroundColor(Color.Argb(230, 40, 40, 40)!);
        AddView(_statusLabel);

        _navigation = new AndroidNavigationOverlay(activity);
        _navigation.Wire((u, v) => _photo.CenterOnNormalizedPoint(u, v));
        _navigation.Visibility = ViewStates.Gone;
        AddView(_navigation);

        _photo.SurfaceReady += OnSurfaceReady;
        _photo.ViewportChanged += OnPhotoViewportChanged;
        _photo.SwipeRequested += direction => Execute(direction < 0 ? "Previous" : "Next");
        _photo.TapRequested += OnPhotoTapped;
        _photo.LongPressRequested += OnPhotoLongPressed;
        _photo.WheelScrolled += OnWheelScrolled;

        _session.Changed += OnSessionChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += OnScrollToCurrentRequested;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.RatingWriteFailed += OnRatingWriteFailed;
        _observedFile = _main.CurrentFile;
        if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
        UpdateHint();
    }

    /// <summary>系统栏/刘海安全区（dp）；Android 15 强制 edge-to-edge 时必须走这条路径。</summary>
    /// <param name="insets">系统 Insets。</param>
    /// <returns>父类处理结果。</returns>
    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        if (insets == null) return base.OnApplyWindowInsets(insets);
        var compat = WindowInsetsCompat.ToWindowInsetsCompat(insets, this);
        var safe = compat.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
        double density = AndroidUiTheme.Density(Context!);
        var next = new AndroidInsets(safe.Left / density, safe.Top / density, safe.Right / density, safe.Bottom / density);
        if (Math.Abs(next.Left - _insets.Left) > .01 || Math.Abs(next.Top - _insets.Top) > .01
            || Math.Abs(next.Right - _insets.Right) > .01 || Math.Abs(next.Bottom - _insets.Bottom) > .01)
        {
            _insets = next;
            RequestLayout();
        }
        return base.OnApplyWindowInsets(insets);
    }

    /// <summary>挂上窗口后主动请求一次 Insets（SurfaceView 与其他子视图不消费，安全区只由根收口）。</summary>
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        RequestApplyInsets();
    }

    /// <summary>根 ViewGroup 只在布局阶段给子视图精确尺寸，Measure 直接吃满全屏。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), MeasureSpec.GetSize(heightMeasureSpec));
    }

    /// <summary>按共享几何摆放所有分区；栏位显隐与方向变化即时释放/让出空间。</summary>
    /// <param name="changed">布局是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        if (_layingOut || Width < 1 || Height < 1) return;
        _layingOut = true;
        try
        {
            double density = AndroidUiTheme.Density(Context!);
            double widthDp = Width / density, heightDp = Height / density;
            _main.UpdateScreenOrientation(widthDp, heightDp);
            bool row = _main.IsRowLayout;
            double leadingInset = _insets.Top + MainScreenLayout.FilterMargin;
            double filterMain = MainScreenLayout.FilterBarMainLength(row, leadingInset);
            bool filmVisible = _main.IsThumbnailViewVisible;
            bool controlVisible = _main.IsControlViewVisible;
            double availableMain = row
                ? Math.Max(1, widthDp - _insets.Left - _insets.Right)
                : Math.Max(1, heightDp - _insets.Top - _insets.Bottom);
            double fileMain = filmVisible
                ? MainScreenLayout.FileBarMainLength(row, filterMain, row ? 0 : _insets.Left)
                : 0;
            var controlMeasure = controlVisible
                ? _controlBar.MeasureDocument(row, availableMain)
                : (Main: 0.0, Cross: 0.0);
            double controlMain = controlVisible
                ? MainScreenLayout.ControlBarMainLength(controlMeasure.Main)
                : 0;
            _panes = AndroidMainScreenLayout.Compute(row, widthDp, heightDp, _insets, fileMain, controlMain, 0);

            LayoutChild(_photoPane, _panes.Photo, density);
            LayoutChild(_fileBar, _panes.FileBar, density);
            _fileBar.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            LayoutChild(_fileDivider, _panes.FileDivider, density);
            _fileDivider.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            LayoutChild(_controlBackground, _panes.ControlBackground, density);
            _controlBackground.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
            LayoutChild(_controlDivider, _panes.ControlDivider, density);
            _controlDivider.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;

            _filterBar.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            _filmstrip.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            if (filmVisible)
            {
                var content = _panes.FileContent;
                double contentWidth = row ? Math.Max(1, content.Width) : MainScreenLayout.FileBarContentWidth;
                LayoutChild(_filterBar, new LayoutRect(content.X, content.Y, contentWidth, filterMain), density);
                _filterBar.Arrange(row, contentWidth, leadingInset);
                LayoutChild(_filmstrip, new LayoutRect(content.X, content.Y + filterMain, contentWidth,
                    Math.Max(1, content.Height - filterMain)), density);
                bool axisChanged = _filmstrip.Horizontal != row;
                _filmstrip.SetAxis(row);
                if (axisChanged) _filmstrip.ScrollToCurrent();
            }

            _controlBar.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
            if (controlVisible)
            {
                _controlBar.Arrange(row, row ? _panes.ControlContent.Width : _panes.ControlContent.Height);
                LayoutChild(_controlBar, _panes.ControlContent, density);
            }

            LayoutOverlays(density, row);
            UpdateNavigationIndicator();
        }
        finally { _layingOut = false; }
    }

    /// <summary>摆好空态提示、增强提示、状态提示与导航图。</summary>
    private void LayoutOverlays(double density, bool row)
    {
        var pane = _panes.Photo;
        double hintWidth = Math.Max(1, Math.Min(500, pane.Width - 20));
        double hintLeft = pane.X + (pane.Width - hintWidth) / 2;
        double centerY = pane.Y + pane.Height / 2;
        bool hasDetail = !string.IsNullOrEmpty(_hintDetail.Text);
        double labelHeight = 24, detailHeight = hasDetail ? 20 : 0;
        double hintTop = centerY - (labelHeight + detailHeight) / 2;
        LayoutChild(_hintLabel, new LayoutRect(hintLeft, hintTop, hintWidth, labelHeight), density);
        LayoutChild(_hintDetail, new LayoutRect(hintLeft, hintTop + labelHeight, hintWidth, detailHeight), density);

        const double busyWidth = 96, busyHeight = 28;
        LayoutChild(_enhanceBusy, new LayoutRect(pane.X + (pane.Width - busyWidth) / 2,
            pane.Y + pane.Height - busyHeight - 20 - _insets.Bottom, busyWidth, busyHeight), density);

        double statusWidth = Math.Min(260, pane.Width - 40), statusHeight = 32;
        LayoutChild(_statusLabel, new LayoutRect(pane.X + (pane.Width - statusWidth) / 2,
            pane.Y + pane.Height - statusHeight - 24 - _insets.Bottom, statusWidth, statusHeight), density);

        bool show = _main.Settings.ShowZoomIndicator && !_photo.IsFit && _session.Image != null;
        _navigation.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (show)
        {
            var (navWidth, navHeight) = _photo.NavigationSize;
            double hostWidth = navWidth + 16;
            double hostHeight = navHeight + 16 + 8 + 15 + 2;
            double rightInset = Math.Max(0, _insets.Right);
            double bottomInset = Math.Max(0, _insets.Bottom - (Height - pane.MaxY));
            var rect = new LayoutRect(
                pane.X + pane.Width - hostWidth - 12 - rightInset,
                pane.Y + pane.Height - hostHeight - 12 - bottomInset,
                hostWidth, hostHeight);
            LayoutChild(_navigation, rect, density);
            _navigation.SetState(null, navWidth, navHeight, _photo.NavigationViewport,
                $"{_photo.PhysicalScale * 100:0.#}%");
        }
    }

    /// <summary>按 dp 矩形给子视图精确测量并落位。</summary>
    private static void LayoutChild(View view, LayoutRect rect, double density)
    {
        int left = (int)Math.Round(rect.X * density), top = (int)Math.Round(rect.Y * density);
        int width = Math.Max(0, (int)Math.Round(rect.Width * density));
        int height = Math.Max(0, (int)Math.Round(rect.Height * density));
        view.Measure(MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly));
        view.Layout(left, top, left + width, top + height);
    }

    /// <summary>当前照片变化：先上报有效视口再 SetFile（零尺寸守卫要求）。</summary>
    private void StartSession()
    {
        if (!_photoReady)
        {
            _startPending = true;
            return;
        }
        _startPending = false;
        var file = _main.CurrentFile;
        if (file == null)
        {
            _session.SetFile(null);
            _photo.SetImage(null);
            UpdateHint();
            return;
        }
        OnPhotoViewportChanged();
        Console.WriteLine($"[Native] 主图加载：{file.Name}（{file.File.PathKey}）");
        _session.SetFile(file.File);
        _photo.SetImage(_session.Image);
        UpdateHint();
    }

    private void OnSurfaceReady()
    {
        _photoReady = true;
        if (_startPending || _main.CurrentFile != null) StartSession();
    }

    private void OnPhotoViewportChanged()
    {
        if (_photo.Width < 1 || _photo.Height < 1) return;
        _session.UpdateViewport(_photo.Width, _photo.Height, _photo.NeedsFull);
        if (_main.Settings.ShowZoomIndicator) UpdateNavigationIndicator();
    }

    private void OnSessionChanged()
    {
        _photo.SetImage(_session.Image);
        UpdateHint();
        _enhanceBusy.Visibility = _session.IsEnhancing ? ViewStates.Visible : ViewStates.Gone;
        UpdateNavigationIndicator();
    }

    private void OnPhotoTapped()
    {
        if (_session.Image == null) _ = _main.FolderVM.OpenFilePickerAsync();
    }

    private void OnPhotoLongPressed() => ShowPhotoMenu();

    private void OnWheelScrolled(bool up, int metaState)
    {
        var gesture = AppGesture.FromMouse(new MouseGestureEx(up ? MouseAction.WheelUp : MouseAction.WheelDown,
            AndroidInputMapping.ModifiersOf(metaState)));
        string? command = InputCommandMap.CommandForGesture(gesture, _main.Settings);
        if (command != null && AndroidControlBar.IsCommandAvailable(command)) Execute(command);
    }

    /// <summary>主图长按菜单：打开文件夹 / 栏位显隐 / 增强 / 设置（勾选态每次现读共享状态）。</summary>
    internal void ShowPhotoMenu()
    {
        var activity = Context as Activity;
        if (activity == null) return;
        var menu = new PopupMenu(activity, _photo);
        menu.Menu.Add(0, 0, 0, "打开文件夹…");
        var film = menu.Menu.Add(0, 1, 1, "胶片栏");
        film.SetCheckable(true);
        film.SetChecked(_main.IsThumbnailViewVisible);
        var control = menu.Menu.Add(0, 2, 2, "控制栏");
        control.SetCheckable(true);
        control.SetChecked(_main.IsControlViewVisible);
        var enhance = menu.Menu.Add(0, 3, 3, "增强预览");
        enhance.SetCheckable(true);
        enhance.SetChecked(_session.IsEnhanced);
        menu.Menu.Add(0, 4, 4, "工具…");
        menu.Menu.Add(0, 5, 5, "设置…");
        menu.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId ?? -1)
            {
                case 0: _ = _main.FolderVM.OpenFilePickerAsync(); break;
                case 1: _main.ToggleThumbnailView(); break;
                case 2: _main.ToggleControlView(); break;
                case 3: _session.ToggleEnhanced(); break;
                case 4: ShowTools(); break;
                case 5: _main.OpenSettingWindow(); break;
            }
        };
        menu.Show();
    }

    /// <summary>打开/前置原生工具页（A5 前由窗口门面给出可见反馈）。</summary>
    internal void ShowTools() => NativeWindowPresenter.ShowOrActivateToolsWindow(_main.Tools);

    /// <summary>执行共享命令：主图相关走原生视口，其余走共享命令 VM。</summary>
    /// <param name="command">共享命令标识。</param>
    internal void Execute(string command)
    {
        switch (command)
        {
            case "Open": _ = _main.FolderVM.OpenFilePickerAsync(); break;
            case "Previous": Move(-1); break;
            case "Next": Move(1); break;
            case "Exchange": if (_main.LastFile != null) _main.CurrentFile = _main.LastFile; break;
            case "Fit": _photo.ToggleFit(); break;
            case "ZoomInScale": _photo.ZoomBy(1.25); break;
            case "ZoomOutScale": _photo.ZoomBy(.8); break;
            case "ZoomInPreset": ZoomPreset(1); break;
            case "ZoomOutPreset": ZoomPreset(-1); break;
            case "ToggleAnalysisView": _main.ToggleAnalysisView(); break;
            case "ToggleEnhance": _session.ToggleEnhanced(); break;
            default:
                if (command.StartsWith("SetRating", StringComparison.Ordinal)
                    && int.TryParse(command[9..], out int rating) && _main.CurrentFile != null)
                    _ = RateAsync(_main.CurrentFile, rating);
                break;
        }
    }

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

    private void ZoomPreset(int direction)
    {
        var values = new System.Collections.Generic.List<double>();
        foreach (var preset in _main.Settings.ScalePresets) values.Add(preset.Value);
        values.Sort();
        double current = _photo.PhysicalScale;
        double target = 0;
        bool found = false;
        if (direction > 0)
        {
            foreach (double value in values)
                if (value > current + .001) { target = value; found = true; break; }
        }
        else
        {
            for (int i = values.Count - 1; i >= 0; i--)
                if (values[i] < current - .001) { target = values[i]; found = true; break; }
        }
        if (found) _photo.ZoomToPhysical(target);
    }

    /// <summary>评分写回经共享 XMP/同步逻辑；失败保留原星级并给出可见错误。</summary>
    /// <param name="file">目标照片。</param>
    /// <param name="rating">星级。</param>
    internal async System.Threading.Tasks.Task RateAsync(ImageFile file, int rating)
    {
        await _main.SetRatingAsync(file, rating);
        _controlBar.MarkDirty();
        RequestLayout();
    }

    private void OnRateRequested(ImageFile file, int rating) => _ = RateAsync(file, rating);

    private void OnRatingWriteFailed(ImageFile file, int rating)
        => ShowStatus($"无法写入星级：{file.Name}", StatusDurationMs);

    /// <summary>短暂显示状态提示（与旧壳底部提示同义）。</summary>
    /// <param name="text">文本。</param>
    /// <param name="durationMs">时长（ms）。</param>
    internal void ShowStatus(string text, int durationMs)
    {
        _statusLabel.Text = text;
        _statusLabel.Visibility = ViewStates.Visible;
        int token = ++_statusToken;
        PostDelayed(() => { if (token == _statusToken) _statusLabel.Visibility = ViewStates.Gone; }, durationMs);
    }

    private int _statusToken;
    private bool _navigationShown;

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.CurrentFile):
                if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
                _observedFile = _main.CurrentFile;
                if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
                _filmstrip.CancelScrollUpToNow();
                _controlBar.MarkDirty();
                StartSession();
                RequestLayout();
                break;
            case nameof(MainViewModel.IsRowLayout):
            case nameof(MainViewModel.IsScreenLandscape):
            case nameof(MainViewModel.IsThumbnailViewVisible):
            case nameof(MainViewModel.IsControlViewVisible):
                RequestLayout();
                break;
            case nameof(MainViewModel.IsAnalysisViewVisible):
                OnPhotoViewportChanged();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        _controlBar.MarkDirty();
        _filterBar.Refresh();
        _controlBar.Invalidate();
        RequestLayout();
        UpdateHint();
    }

    private void OnControlChanged(object? sender, PropertyChangedEventArgs args)
    {
        _controlBar.MarkDirty();
        RequestLayout();
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        _filterBar.Refresh();
        RequestLayout();
    }

    private void OnFilesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ThumbnailListViewModel.FilteredFiles)) return;
        _filmstrip.ReloadData();
        _filterBar.Refresh();
        UpdateHint();
        RequestLayout();
    }

    private void OnAllFilesChanged()
    {
        _filterBar.Refresh();
        UpdateHint();
    }

    private void OnScrollToCurrentRequested() => _filmstrip.ScrollToCurrent();

    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ImageFile.ExifData) or nameof(ImageFile.Rating))
        {
            _controlBar.MarkDirty();
            _controlBar.Invalidate();
        }
    }

    /// <summary>更新空态/错误提示；有图时全部隐藏。</summary>
    private void UpdateHint()
    {
        bool show = _session.Image == null;
        _hintLabel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        _hintDetail.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (!show) return;
        if (!string.IsNullOrEmpty(_session.Error))
        {
            _hintLabel.Text = "无法显示照片";
            _hintDetail.Text = _session.Error;
            return;
        }
        if (_main.CurrentFile != null)
        {
            _hintLabel.Text = "正在载入…";
            _hintDetail.Text = _main.CurrentFile.Name;
            return;
        }
        _hintLabel.Text = "PhotoViewer";
        _hintDetail.Text = "点按选择文件夹，或长按打开菜单";
    }

    /// <summary>导航图缩略图只在展示图引用变化时重出；可见框与百分比每帧更新。</summary>
    private void UpdateNavigationIndicator()
    {
        bool show = _session.Image != null && _main.Settings.ShowZoomIndicator && !_photo.IsFit;
        if (_navigationShown != show)
        {
            _navigationShown = show;
            _navigation.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            RequestLayout();
        }
        if (!show) return;
        if (!ReferenceEquals(_navigationSource, _session.Image))
        {
            _navigationSource = _session.Image;
            var previous = _navigationBitmap;
            _navigationBitmap = null;
            previous?.Dispose();
            _navigation.Clear();
            var file = _main.CurrentFile?.File;
            var image = _session.Image;
            int token = ++_navigationToken;
            if (file != null && image != null)
            {
                int longEdge = (int)Math.Round(300 * AndroidUiTheme.Density(Context!));
                _ = AndroidNavigationDecoder.DecodeAsync(file, image.Orientation, longEdge, CancellationToken.None)
                    .ContinueWith(task =>
                    {
                        var bitmap = task.Result;
                        UiDispatcher.Post(() =>
                        {
                            if (token != _navigationToken || !ReferenceEquals(_navigationSource, image))
                            {
                                bitmap?.Dispose();
                                return;
                            }
                            _navigationBitmap = bitmap;
                            var (width, height) = _photo.NavigationSize;
                            if (bitmap != null)
                                _navigation.SetState(bitmap, width, height, _photo.NavigationViewport,
                                    $"{_photo.PhysicalScale * 100:0.#}%");
                            RequestLayout();
                        });
                    }, TaskScheduler.Default);
            }
        }
        var (navWidth, navHeight) = _photo.NavigationSize;
        _navigation.SetState(null, navWidth, navHeight, _photo.NavigationViewport,
            $"{_photo.PhysicalScale * 100:0.#}%");
    }

    /// <summary>释放全部订阅与子视图资源。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _photo.SurfaceReady -= OnSurfaceReady;
            _photo.ViewportChanged -= OnPhotoViewportChanged;
            _session.Changed -= OnSessionChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _main.ControlVM.PropertyChanged -= OnControlChanged;
            _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged;
            _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.RatingWriteFailed -= OnRatingWriteFailed;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _filmstrip.RateRequested -= OnRateRequested;
            _photo.Dispose();
            _navigationToken++;
            _navigationBitmap?.Dispose();
            _navigationBitmap = null;
            _navigationSource = null;
        }
        base.Dispose(disposing);
    }
}
