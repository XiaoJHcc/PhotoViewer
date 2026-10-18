using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

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
    private readonly AndroidAnalysisHost _analysisHost;
    private readonly AnalysisPresenter _analysisPresenter;
    private readonly AndroidAnalysisPanel _analysisPane;
    private readonly AndroidSimilarityPanel _similarityPane;
    private readonly DetailFrameView _detailHighlight;
    private LayoutRect _detailRect;
    private bool _detailVisible;
    private readonly TextView _hintLabel;
    private readonly TextView _hintDetail;
    private readonly TextView _enhanceBusy;
    private readonly TextView _statusLabel;
    private readonly AndroidNavigationOverlay _navigation;
    private PopupWindow? _photoMenu;
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
    /// <summary>分析栏（验收读取瓦片/可见性）。</summary>
    internal AndroidAnalysisPanel AnalysisPane => _analysisPane;
    /// <summary>相似聚类面板（验收读取可见性/列表）。</summary>
    internal AndroidSimilarityPanel SimilarityPane => _similarityPane;
    /// <summary>细节定格绿框视图。</summary>
    internal View DetailHighlightView => _detailHighlight;

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

        _analysisHost = new AndroidAnalysisHost(main, session);
        _analysisPresenter = new AnalysisPresenter(_analysisHost);
        _analysisPane = new AndroidAnalysisPanel(activity, _analysisPresenter, session);
        _analysisPane.TileCountChanged += RequestLayout;
        _analysisPane.DetailChanged += UpdateDetailHighlight;
        _analysisPane.DetailFocusRequested += (u, v) => _photo.FocusOnPoint(u, v, 1);
        _analysisPane.Visibility = ViewStates.Gone;
        AddView(_analysisPane);
        _similarityPane = new AndroidSimilarityPanel(activity, main);
        _similarityPane.Visibility = ViewStates.Gone;
        AddView(_similarityPane);
        _detailHighlight = new DetailFrameView(activity);
        _detailHighlight.Visibility = ViewStates.Gone;
        _detailHighlight.Clickable = false;
        AddView(_detailHighlight);

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
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
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
        var compat = WindowInsetsCompat.ToWindowInsetsCompat(insets, this)!;
        var safe = compat.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
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
            // FileContent 已从安全区顶部起排；筛选条内部只再加普通外边距，避免列布局把 safe.Top 计两次。
            double leadingInset = MainScreenLayout.FilterMargin;
            double filterMain = MainScreenLayout.FilterBarMainLength(row, leadingInset);
            bool filmVisible = _main.IsThumbnailViewVisible;
            bool controlVisible = _main.IsControlViewVisible;
            double availableMain = row
                ? Math.Max(1, widthDp - _insets.Left - _insets.Right)
                : Math.Max(1, heightDp - _insets.Top - _insets.Bottom);
            bool similarityOpen = filmVisible && _main.FileVM.FilterBar.IsSimilarityPanelOpen;
            if (similarityOpen) _similarityPane.SetLayout(row);
            double similarityColumn = similarityOpen
                ? (row
                    ? SimilarityPanelGeometry.RowWidth(_main.FileVM.SimilarityPanel.HasItems,
                        _main.FileVM.SimilarityPanel.SimilarItems.Count, availableMain)
                    : _similarityPane.CrossSize + SimilarityPanelGeometry.Margin)
                : 0;
            double fileMain = filmVisible
                ? MainScreenLayout.FileBarMainLength(row, filterMain, row ? 0 : _insets.Left + similarityColumn)
                : 0;
            var controlMeasure = controlVisible
                ? _controlBar.MeasureDocument(row, availableMain)
                : (Main: 0.0, Cross: 0.0);
            double controlMain = controlVisible
                ? MainScreenLayout.ControlBarMainLength(controlMeasure.Main)
                : 0;
            bool analysisVisible = _main.IsAnalysisViewVisible;
            _analysisHost.SetVisible(analysisVisible);
            double analysisSize = analysisVisible
                ? AnalysisPanelGeometry.CrossSize(availableMain, Math.Max(1, _analysisPane.TileCount))
                : 0;
            _panes = AndroidMainScreenLayout.Compute(row, widthDp, heightDp, _insets, fileMain, controlMain, analysisSize);

            LayoutChild(_photoPane, _panes.Photo, density);
            LayoutChild(_fileBar, _panes.FileBar, density);
            _fileBar.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            LayoutChild(_fileDivider, _panes.FileDivider, density);
            _fileDivider.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            _analysisPane.Visibility = analysisVisible ? ViewStates.Visible : ViewStates.Gone;
            if (analysisVisible)
            {
                LayoutChild(_analysisPane, _panes.Analysis, density);
                _analysisPane.Arrange(row, availableMain, row ? _insets.Left : _insets.Top);
            }
            LayoutChild(_controlBackground, _panes.ControlBackground, density);
            _controlBackground.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
            LayoutChild(_controlDivider, _panes.ControlDivider, density);
            _controlDivider.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;

            _filterBar.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            _filmstrip.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
            _similarityPane.Visibility = similarityOpen ? ViewStates.Visible : ViewStates.Gone;
            if (filmVisible)
            {
                var content = _panes.FileContent;
                double contentWidth = row ? Math.Max(1, content.Width) : MainScreenLayout.FileBarContentWidth;
                LayoutChild(_filterBar, new LayoutRect(content.X, content.Y, contentWidth, filterMain), density);
                _filterBar.Arrange(row, contentWidth, leadingInset);
                double filmWidth = row ? Math.Max(1, contentWidth - similarityColumn) : contentWidth;
                LayoutChild(_filmstrip, new LayoutRect(content.X, content.Y + filterMain, filmWidth,
                    Math.Max(1, content.Height - filterMain)), density);
                bool axisChanged = _filmstrip.IsHorizontal != row;
                _filmstrip.SetAxis(row);
                if (axisChanged) _filmstrip.ScrollToCurrent();
                if (similarityOpen)
                {
                    var similarityRect = row
                        ? new LayoutRect(content.X + contentWidth - similarityColumn, content.Y + filterMain,
                            Math.Max(1, similarityColumn - SimilarityPanelGeometry.Margin), _similarityPane.CrossSize)
                        : new LayoutRect(content.X + MainScreenLayout.FileBarContentWidth, content.Y + filterMain,
                            _similarityPane.CrossSize, Math.Max(1, content.Height - filterMain));
                    LayoutChild(_similarityPane, similarityRect, density);
                }
            }

            _controlBar.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
            if (controlVisible)
            {
                _controlBar.Arrange(row, row ? _panes.ControlContent.Width : _panes.ControlContent.Height);
                LayoutChild(_controlBar, _panes.ControlContent, density);
            }

            LayoutOverlays(density, row);
            UpdateDetailHighlight();
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
        RefreshControlBarHard();
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
        _session.UpdateViewport(_photo.Width, _photo.Height, _photo.NeedsFull || _main.IsAnalysisViewVisible);
        if (_main.Settings.ShowZoomIndicator) UpdateNavigationIndicator();
        UpdateDetailHighlight();
        _analysisHost.Raise();
    }

    private void OnSessionChanged()
    {
        _photo.SetImage(_session.Image);
        _analysisHost.Raise();
        UpdateDetailHighlight();
        UpdateHint();
        _enhanceBusy.Visibility = _session.IsEnhancing ? ViewStates.Visible : ViewStates.Gone;
        UpdateNavigationIndicator();
    }

    private void OnPhotoTapped()
    {
        if (_analysisPane.DetailActive)
        {
            _analysisPane.ClearDetail();
            return;
        }
        if (_session.Image == null) _ = _main.FolderVM.OpenFilePickerAsync();
    }

    private void OnPhotoLongPressed(float x, float y) => ShowPhotoMenu(x, y);

    private void OnWheelScrolled(bool up, int metaState)
    {
        var gesture = AppGesture.FromMouse(new MouseGestureEx(up ? MouseAction.WheelUp : MouseAction.WheelDown,
            AndroidInputMapping.ModifiersOf(metaState)));
        string? command = InputCommandMap.CommandForGesture(gesture, _main.Settings);
        if (command != null && AndroidControlBar.IsCommandAvailable(command)) Execute(command);
    }

    /// <summary>主图长按菜单：在手指处弹出自绘面板；面板整体夹在屏幕内，超出高度时可滚动。</summary>
    /// <param name="localX">触点在本视图内的像素横坐标。</param>
    /// <param name="localY">触点在本视图内的像素纵坐标。</param>
    internal void ShowPhotoMenu(float localX, float localY)
    {
        if (Context is not Activity activity) return;
        DismissPhotoMenu();
        var photoLocation = new int[2];
        _photo.GetLocationOnScreen(photoLocation);
        int touchX = photoLocation[0] + (int)localX;
        int touchY = photoLocation[1] + (int)localY;
        double density = AndroidUiTheme.Density(activity);
        var metrics = Resources?.DisplayMetrics;
        int screenWidth = metrics?.WidthPixels ?? Width;
        int screenHeight = metrics?.HeightPixels ?? Height;
        int menuWidth = (int)Math.Round(224 * density);
        int itemHeight = (int)Math.Round(44 * density);
        int padding = (int)Math.Round(4 * density);
        int dividerHeight = Math.Max(1, (int)Math.Round(density));
        int contentHeight = itemHeight * 7 + dividerHeight * 6 + padding * 2;
        int maxHeight = Math.Max(itemHeight * 2, screenHeight - (int)Math.Round(16 * density));
        int menuHeight = Math.Min(contentHeight, maxHeight);

        var content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(AndroidUiTheme.ControlFill);
        background.SetCornerRadius((float)(8 * density));
        background.SetStroke(Math.Max(1, (int)Math.Round(density)), AndroidUiTheme.ControlBorder);
        content.Background = background;
        content.SetPadding(padding, padding, padding, padding);
        content.Elevation = (float)(12 * density);

        void AddDivider()
        {
            var divider = new View(activity);
            divider.SetBackgroundColor(AndroidUiTheme.Divider);
            content.AddView(divider, new LinearLayout.LayoutParams(LayoutParams.MatchParent, dividerHeight));
        }

        void AddItem(string label, bool? check, Action action)
        {
            var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding((int)Math.Round(12 * density), 0, (int)Math.Round(10 * density), 0);
            row.Clickable = true;
            row.Focusable = true;
            var text = new TextView(activity)
            {
                Text = label,
                TextSize = 15,
            };
            text.SetTextColor(AndroidUiTheme.TextPrimary);
            text.Gravity = GravityFlags.CenterVertical;
            row.AddView(text, new LinearLayout.LayoutParams(0, LayoutParams.MatchParent) { Weight = 1 });
            var mark = new TextView(activity)
            {
                Text = check == true ? "✓" : "",
                TextSize = 15,
                Gravity = GravityFlags.Center,
            };
            mark.SetTextColor(AndroidUiTheme.Accent);
            row.AddView(mark, new LinearLayout.LayoutParams((int)Math.Round(24 * density), LayoutParams.MatchParent));
            row.Click += (_, _) =>
            {
                DismissPhotoMenu();
                action();
            };
            content.AddView(row, new LinearLayout.LayoutParams(LayoutParams.MatchParent, itemHeight));
        }

        // 与 iOS/旧壳分组一致：打开文件夹 | 栏位显隐 | 增强 | 工具/设置；不逐项加分隔线。
        AddItem("打开文件夹…", null, () => _ = _main.FolderVM.OpenFilePickerAsync());
        AddDivider();
        AddItem("胶片栏", _main.IsThumbnailViewVisible, () => _main.ToggleThumbnailView());
        AddItem("分析栏", _main.IsAnalysisViewVisible, () => _main.ToggleAnalysisView());
        AddItem("控制栏", _main.IsControlViewVisible, () => _main.ToggleControlView());
        AddDivider();
        AddItem("增强预览", _session.IsEnhanced, () => _session.ToggleEnhanced());
        AddDivider();
        AddItem("工具…", null, ShowTools);
        AddItem("设置…", null, () => _main.OpenSettingWindow());

        ScrollView? scroll = null;
        View popupContent = content;
        if (contentHeight > maxHeight)
        {
            scroll = new ScrollView(activity) { VerticalScrollBarEnabled = true };
            scroll.AddView(content);
            popupContent = scroll;
        }

        int margin = (int)Math.Round(8 * density);
        int x = Math.Clamp(touchX, margin, Math.Max(margin, screenWidth - menuWidth - margin));
        int y = Math.Clamp(touchY, margin, Math.Max(margin, screenHeight - menuHeight - margin));
        var popup = new PopupWindow(popupContent, menuWidth, menuHeight, true)
        {
            OutsideTouchable = true,
            Focusable = true,
            Elevation = (float)(12 * density),
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        popup.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_photoMenu, popup)) _photoMenu = null;
        };
        _photoMenu = popup;
        Console.WriteLine($"[Native] 主图菜单 touch=({touchX},{touchY}) show=({x},{y}) size={menuWidth}x{menuHeight} screen={screenWidth}x{screenHeight}");
        popup.ShowAtLocation(_photo, GravityFlags.Top | GravityFlags.Start, x, y);
    }

    /// <summary>Activity 恢复（SAF 返回）后恢复栏位内容与主图：选择器期间可能丢失一帧/一次重建。</summary>
    internal void RefreshAfterResume()
    {
        RefreshControlBarHard();
        _filterBar.Refresh();
        UpdateHint();
        RequestLayout();
        _photo.Post(_photo.Draw);
    }

    /// <summary>关闭筛选下拉（返回键优先消费）。</summary>
    /// <returns>关闭了返回 true。</returns>
    internal bool DismissFilterDropdown() => _filterBar.DismissDropdown();

    /// <summary>关闭主图长按菜单（返回键优先消费）。</summary>
    /// <returns>关闭了菜单返回 true。</returns>
    internal bool DismissPhotoMenu()
    {
        if (_photoMenu?.IsShowing != true) return false;
        _photoMenu.Dismiss();
        _photoMenu = null;
        return true;
    }

    /// <summary>打开/前置原生工具页（A5 前由窗口门面给出可见反馈）。</summary>
    internal void ShowTools() => _main.OpenToolsWindow();

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
                _analysisPane.ClearDetail();
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
                if (!_main.IsAnalysisViewVisible) _analysisPane.ClearDetail();
                _analysisHost.SetVisible(_main.IsAnalysisViewVisible);
                OnPhotoViewportChanged();
                RequestLayout();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.AnalysisImmediateCompute) or nameof(SettingsViewModel.ShowZoomIndicator))
            _analysisHost.Raise();
        _filterBar.Refresh();
        RefreshControlBarHard();
        UpdateHint();
    }

    /// <summary>控制栏立即重建并在一帧后再补一次（设置/文件/选择器返回等异步变更都走这里）。</summary>
    private void RefreshControlBarHard()
    {
        _controlBar.MarkDirty();
        _controlBar.Invalidate();
        RequestLayout();
        PostDelayed(() => { _controlBar.MarkDirty(); _controlBar.Invalidate(); RequestLayout(); }, 120);
    }

    private void OnControlChanged(object? sender, PropertyChangedEventArgs args) => RefreshControlBarHard();

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
        RefreshControlBarHard();
        UpdateHint();
        RequestLayout();
    }

    private void OnAllFilesChanged()
    {
        _filterBar.Refresh();
        _controlBar.MarkDirty();
        _controlBar.Invalidate();
        UpdateHint();
    }

    /// <summary>相似项集合/动作区变化后重排主屏（面板宽度随卡片数变化）。</summary>
    private void OnSimilarityPanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        _similarityPane.Refresh();
        if (args.PropertyName is nameof(SimilarityPanelViewModel.SimilarItems)
            or nameof(SimilarityPanelViewModel.HasItems)
            or nameof(SimilarityPanelViewModel.ShowActionArea)
            or nameof(SimilarityPanelViewModel.IsStateIndexing)
            or nameof(SimilarityPanelViewModel.UnindexedCount))
            RequestLayout();
    }

    private void OnScrollToCurrentRequested() => _filmstrip.ScrollToCurrent();

    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ImageFile.ExifData) or nameof(ImageFile.Rating))
        {
            _controlBar.MarkDirty();
            _controlBar.Invalidate();
        }
        if (args.PropertyName is nameof(ImageFile.ExifData)) _analysisHost.Raise();
    }

    /// <summary>更新空态/错误提示；有图时全部隐藏。</summary>
    /// <summary>细节预览定格时在主图上画绿色方框：中心取瓦片中心，大小取对焦框或 1:1 裁剪方块在当前视口下的投影。</summary>
    private void UpdateDetailHighlight()
    {
        if (!_analysisPane.DetailActive || !_main.IsAnalysisViewVisible || _session.Image == null || _session.IsProxy)
        {
            HideDetailFrame();
            return;
        }
        // 小图在适应态已经大于物理 100%，没有可对应的 1:1 放大细节；不显示无意义的绿框。
        var fitViewport = _photo.Viewport;
        if (fitViewport.IsFit && fitViewport.FitScale > 1 / fitViewport.BackingScale + 1e-9)
        {
            HideDetailFrame();
            return;
        }
        var (u, v) = _analysisPane.DetailPoint;
        var viewport = _photo.Viewport;
        double scale = viewport.Scale;
        double boxWidth, boxHeight;
        if (_analysisPane.DetailFrameWidth is { } frameWidth && _analysisPane.DetailFrameHeight is { } frameHeight)
        {
            boxWidth = frameWidth * viewport.ImageWidth * scale;
            boxHeight = frameHeight * viewport.ImageHeight * scale;
        }
        else
        {
            boxWidth = boxHeight = _analysisPane.CropPixels * scale;
        }
        boxWidth = Math.Max(6, boxWidth);
        boxHeight = Math.Max(6, boxHeight);
        double density = AndroidUiTheme.Density(Context!);
        double viewWidth = _photo.Width / density, viewHeight = _photo.Height / density;
        var (centerX, centerY) = _photo.ViewPoint(u, v);
        if (centerX < -boxWidth / 2 || centerY < -boxHeight / 2
            || centerX > viewWidth + boxWidth / 2 || centerY > viewHeight + boxHeight / 2)
        {
            HideDetailFrame();
            return;
        }
        _detailRect = new LayoutRect(centerX - boxWidth / 2, centerY - boxHeight / 2, boxWidth, boxHeight);
        _detailVisible = true;
        _detailHighlight.Visibility = ViewStates.Visible;
        Invalidate();
    }

    private void HideDetailFrame()
    {
        _detailVisible = false;
        _detailHighlight.Visibility = ViewStates.Gone;
        Invalidate();
    }

    /// <summary>绿框画在根视图最上层：SurfaceView 与子视图的裁剪/层级不该吃掉右边或下边。</summary>
    protected override void DispatchDraw(Canvas canvas)
    {
        base.DispatchDraw(canvas);
        if (!_detailVisible) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        var rect = new RectF(
            (float)((_panes.Photo.X + _detailRect.X) * density),
            (float)((_panes.Photo.Y + _detailRect.Y) * density),
            (float)((_panes.Photo.X + _detailRect.MaxX) * density),
            (float)((_panes.Photo.Y + _detailRect.MaxY) * density));
        using var paint = AndroidUiTheme.Paint(Color.Rgb(0, 255, 0)!, Math.Max(2, 3 * density));
        paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
        canvas.DrawRect(rect, paint);
    }

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
            _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
            _main.RatingWriteFailed -= OnRatingWriteFailed;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _filmstrip.RateRequested -= OnRateRequested;
            _analysisPane.TileCountChanged -= RequestLayout;
            _analysisPane.DetailChanged -= UpdateDetailHighlight;
            _analysisPresenter.Dispose();
            _photo.Dispose();
            _navigationToken++;
            _navigationBitmap?.Dispose();
            _navigationBitmap = null;
            _navigationSource = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>绿框的可见性标记；实际绘制在根视图 DispatchDraw，避免被 SurfaceView 子层裁剪。</summary>
    private sealed class DetailFrameView : View
    {
        public DetailFrameView(global::Android.Content.Context context) : base(context)
        {
        }
    }
}
