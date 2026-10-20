using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
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
    private readonly AndroidControlBarScrollHost _controlScrollHost;
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
    private string _lastFullError = string.Empty;
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
        // SHELL-04：栏位显隐从平台偏好恢复（分析栏仍为会话态）。
        _main.IsThumbnailViewVisible = AndroidShellPreferences.LoadShowFilm();
        _main.IsControlViewVisible = AndroidShellPreferences.LoadShowControls();
        SetBackgroundColor(AndroidUiTheme.Background);
        SetClipChildren(true);

        _photoPane = new FrameLayout(activity);
        _photoPane.SetClipChildren(true);
        _photoPane.SetBackgroundColor(AndroidUiTheme.PhotoBackground);
        _photo = new AndroidPhotoView(activity);
        _main.FitToViewRequested += OnFitToViewRequested;
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
        _controlScrollHost = new AndroidControlBarScrollHost(activity, _controlBar);
        AddView(_controlScrollHost);

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
            TextSize = (float)DesignTokens.ImageHintTitleFontSize,
            Gravity = GravityFlags.Center,
        };
        _hintLabel.SetTextColor(AndroidUiTheme.TextPrimary);
        _hintDetail = new TextView(activity)
        {
            Text = "点按选择文件夹",
            TextSize = (float)DesignTokens.ImageHintDetailFontSize,
            Gravity = GravityFlags.Center,
        };
        _hintDetail.SetTextColor(AndroidUiTheme.TextSecondary);
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
        _enhanceBusy.SetTextColor(FromToken(DesignTokens.ColorStatusToastForeground));
        var busyBackground = new GradientDrawable();
        busyBackground.SetColor(Color.Argb(217, 34, 34, 34)!);
        busyBackground.SetCornerRadius(AndroidUiTheme.Dp(activity, DesignTokens.ImageBusyCornerRadius));
        _enhanceBusy.Background = busyBackground;
        AddView(_enhanceBusy);

        _statusLabel = new TextView(activity)
        {
            TextSize = (float)DesignTokens.StatusToastFontSize,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone,
        };
        _statusLabel.SetTextColor(FromToken(DesignTokens.ColorStatusToastForeground));
        var statusBackground = new GradientDrawable();
        statusBackground.SetColor(WithAlpha(DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha));
        statusBackground.SetCornerRadius((float)AndroidUiTheme.Dp(activity, DesignTokens.StatusToastCornerRadius));
        _statusLabel.Background = statusBackground;
        int statusPaddingX = AndroidUiTheme.DpPx(this, DesignTokens.StatusToastPaddingX);
        int statusPaddingY = AndroidUiTheme.DpPx(this, DesignTokens.StatusToastPaddingY);
        _statusLabel.SetPadding(statusPaddingX, statusPaddingY, statusPaddingX, statusPaddingY);
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
        _session.Notice += OnSessionNotice;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += OnScrollToCurrentRequested;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
        _main.RatingWriteFailed += OnRatingWriteFailed;
        _main.OpenFailed += OnOpenFailed;
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
            var similarityPanel = _main.FileVM.SimilarityPanel;
            // SIM-07：停靠区只在列表态且动作区（未提取计数/按钮/进度/错误）实际显示时占宽。
            double similarityDock = similarityPanel.HasItems && similarityPanel.ShowActionArea
                ? _similarityPane.DockMeasureWidth()
                : 0;
            double similarityColumn = similarityOpen
                ? (row
                    ? SimilarityPanelGeometry.RowWidth(similarityPanel.HasItems,
                        similarityPanel.SimilarItems.Count, availableMain, similarityDock)
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

            _controlScrollHost.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
            if (controlVisible)
            {
                var content = _panes.ControlContent;
                double viewportCross = row ? content.Width : content.Height;
                // TOOLBAR-08：单组本身超出可用主轴时，控制栏沿主轴滚动而不是被分区裁切。
                bool scrollable = controlMeasure.Cross > viewportCross + .5;
                _controlBar.Arrange(row, scrollable ? controlMeasure.Cross : Math.Max(1, viewportCross));
                _controlScrollHost.Configure(row, (int)Math.Round(viewportCross * density),
                    (int)Math.Round(controlMeasure.Cross * density));
                LayoutChild(_controlScrollHost, content, density);
            }

            LayoutOverlays(density, row);
            UpdateDetailHighlight();
            UpdateNavigationIndicator();
        }
        finally { _layingOut = false; }
    }

    /// <summary>摆好空态提示、增强提示、状态提示与导航图；提示都落在主图分区内，不重复扣底部安全区。</summary>
    private void LayoutOverlays(double density, bool row)
    {
        var pane = _panes.Photo;
        double bottomInset = row ? 0 : Math.Max(0, _insets.Bottom);
        int hintWidthPx = Math.Max(1, (int)Math.Round(
            Math.Max(1, Math.Min(DesignTokens.ImageHintMaxWidth, pane.Width - 2 * MainScreenLayout.FilterMargin)) * density));
        int hintMaxHeightPx = Math.Max(1, (int)Math.Round(pane.Height * density));
        _hintLabel.Measure(MeasureSpec.MakeMeasureSpec(hintWidthPx, MeasureSpecMode.AtMost),
            MeasureSpec.MakeMeasureSpec(hintMaxHeightPx, MeasureSpecMode.AtMost));
        _hintDetail.Measure(MeasureSpec.MakeMeasureSpec(hintWidthPx, MeasureSpecMode.AtMost),
            MeasureSpec.MakeMeasureSpec(hintMaxHeightPx, MeasureSpecMode.AtMost));
        double hintWidth = hintWidthPx / density;
        double labelHeight = _hintLabel.MeasuredHeight / density;
        bool hasDetail = !string.IsNullOrEmpty(_hintDetail.Text);
        double detailHeight = hasDetail ? _hintDetail.MeasuredHeight / density : 0;
        double gap = DesignTokens.ImageHintDetailGapAndroid;
        double blockHeight = hasDetail ? Math.Max(labelHeight, gap + detailHeight) : labelHeight;
        double hintLeft = pane.X + (pane.Width - hintWidth) / 2;
        double hintTop = pane.Y + Math.Max(0, (pane.Height - blockHeight) / 2);
        LayoutChild(_hintLabel, new LayoutRect(hintLeft, hintTop, hintWidth, labelHeight), density);
        LayoutChild(_hintDetail, new LayoutRect(hintLeft, hintTop + gap, hintWidth, detailHeight), density);

        LayoutChild(_enhanceBusy, new LayoutRect(
            pane.X + (pane.Width - DesignTokens.ImageBusyWidth) / 2,
            pane.Y + pane.Height - DesignTokens.ImageBusyHeight - DesignTokens.ImageBusyBottomOffset - bottomInset,
            DesignTokens.ImageBusyWidth, DesignTokens.ImageBusyHeight), density);

        // STATE-03：单行或自动换行，最大宽 min(500, 宽−40)，高按实际文本量出。
        double statusMaxWidth = Math.Max(1, Math.Min(DesignTokens.StatusToastMaxWidth, pane.Width - 40));
        _statusLabel.Measure(
            MeasureSpec.MakeMeasureSpec((int)Math.Round(statusMaxWidth * density), MeasureSpecMode.AtMost),
            MeasureSpec.MakeMeasureSpec((int)Math.Round(pane.Height * density), MeasureSpecMode.AtMost));
        double statusWidth = _statusLabel.MeasuredWidth / density;
        double statusHeight = _statusLabel.MeasuredHeight / density;
        // STATE-03：药丸可见时提示条整体上移到药丸上方，药丸收起后回到距底 24。
        double statusBottom = _enhanceBusy.Visibility == ViewStates.Visible
            ? DesignTokens.ImageBusyBottomOffset + DesignTokens.ImageBusyHeight + DesignTokens.StatusToastStackGap
            : DesignTokens.StatusToastBottomMargin;
        LayoutChild(_statusLabel, new LayoutRect(
            pane.X + (pane.Width - statusWidth) / 2,
            pane.Y + pane.Height - statusHeight - statusBottom - bottomInset,
            statusWidth, statusHeight), density);

        bool show = _main.Settings.ShowZoomIndicator && !_photo.IsFit && _session.Image != null;
        _navigation.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (show)
        {
            var (navWidth, navHeight) = _photo.NavigationSize;
            var (hostWidth, hostHeight) = AndroidNavigationOverlay.HostSizeFor(navWidth, navHeight);
            double rightInset = Math.Max(0, _insets.Right);
            var rect = new LayoutRect(
                pane.MaxX - hostWidth - DesignTokens.NavMargin - rightInset,
                pane.MaxY - hostHeight - DesignTokens.NavMargin - bottomInset,
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
        _lastFullError = string.Empty;
        UpdateFocusPoint();
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
        // IMAGE-07：药丸只在增强计算持续超过共享门槛时显示。
        _enhanceBusy.Visibility = _session.IsEnhancingVisible ? ViewStates.Visible : ViewStates.Gone;
        // IMAGE-03：屏上已有代理时的 Full 失败只走提示条，不覆盖主图错误标题。
        if (_session.FullError != _lastFullError)
        {
            _lastFullError = _session.FullError;
            if (!string.IsNullOrEmpty(_lastFullError))
                ShowStatus(_lastFullError, DesignTokens.MotionStatusToastDuration);
        }
        UpdateNavigationIndicator();
    }

    /// <summary>SCOPE-02：共享会话的一次性提示（如内存压力自动关闭增强）走 STATE-03 提示条。</summary>
    /// <param name="text">提示文本。</param>
    private void OnSessionNotice(string text) => ShowStatus(text, DesignTokens.MotionStatusToastDuration);

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

        void AddItem(string label, bool? check, Action action, bool enabled = true)
        {
            var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding((int)Math.Round(12 * density), 0, (int)Math.Round(10 * density), 0);
            row.Clickable = enabled;
            row.Focusable = enabled;
            row.Enabled = enabled;
            row.ContentDescription = check == true ? $"{label}，已勾选" : label;
            var text = new TextView(activity)
            {
                Text = label,
                TextSize = 15,
            };
            text.SetTextColor(enabled ? AndroidUiTheme.TextPrimary : AndroidUiTheme.TextSecondary);
            text.Gravity = GravityFlags.CenterVertical;
            text.Enabled = enabled;
            row.AddView(text, new LinearLayout.LayoutParams(0, LayoutParams.MatchParent) { Weight = 1 });
            var mark = new TextView(activity)
            {
                Text = check == true ? "✓" : "",
                TextSize = 15,
                Gravity = GravityFlags.Center,
            };
            mark.SetTextColor(enabled ? AndroidUiTheme.Accent : AndroidUiTheme.TextSecondary);
            row.AddView(mark, new LinearLayout.LayoutParams((int)Math.Round(24 * density), LayoutParams.MatchParent));
            row.Click += (_, _) =>
            {
                if (!enabled) return;
                DismissPhotoMenu();
                action();
            };
            content.AddView(row, new LinearLayout.LayoutParams(LayoutParams.MatchParent, itemHeight));
        }

        // 与 iOS 分组一致：打开文件夹 | 栏位显隐 | 增强 | 工具/设置；不逐项加分隔线。
        AddItem("打开文件夹…", null, () => _ = _main.FolderVM.OpenFilePickerAsync());
        AddDivider();
        AddItem("胶片栏", _main.IsThumbnailViewVisible, () => _main.ToggleThumbnailView());
        AddItem("分析栏", _main.IsAnalysisViewVisible, () => _main.ToggleAnalysisView());
        AddItem("控制栏", _main.IsControlViewVisible, () => _main.ToggleControlView());
        AddDivider();
        // MENU-06/07：文案为「增强」；无当前照片时置灰，工具仍可打开并显示空态。
        AddItem("增强", _session.IsEnhanced, () => _session.ToggleEnhanced(), _main.CurrentFile != null);
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
            // IMAGE-02：Fit 命令在 EXIF 异步到达后也使用最新对焦点（无对焦点回退视口中心）。
            case "Fit": UpdateFocusPoint(); _photo.ToggleFit(); break;
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

    /// <summary>在已筛选列表中翻页，列表端点不环绕（FILTER-05：当前照片被筛掉时按重算前索引定位）。</summary>
    private void Move(int offset) => _main.FileVM.ThumbnailList.MoveCurrent(offset);

    /// <summary>按用户预设选择邻近倍率（IMAGE-04：端点或未命中时不动作）。</summary>
    private void ZoomPreset(int direction)
    {
        double? target = ZoomPresets.Next(
            _main.Settings.ScalePresets.Select(preset => preset.Value), _photo.PhysicalScale, direction);
        if (target is double value) _photo.ZoomToPhysical(value);
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
        => ShowStatus(StatusMessages.RatingWriteFailed(file.Name), DesignTokens.MotionStatusToastDuration);

    /// <summary>STATE-02：共享层上报的打开失败统一用提示条呈现，不弹模态。</summary>
    /// <param name="message">失败提示正文。</param>
    private void OnOpenFailed(string message)
        => ShowStatus(message, DesignTokens.MotionStatusToastDuration);

    /// <summary>短暂显示状态提示（与共享底部提示同义）。</summary>
    /// <param name="text">文本。</param>
    /// <param name="durationMs">时长（ms）。</param>
    internal void ShowStatus(string text, int durationMs)
    {
        _statusLabel.Text = text;
        _statusLabel.Visibility = ViewStates.Visible;
        RequestLayout();
        int token = ++_statusToken;
        PostDelayed(() =>
        {
            if (token != _statusToken) return;
            _statusLabel.Visibility = ViewStates.Gone;
            RequestLayout();
        }, durationMs);
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
                RequestLayout();
                break;
            case nameof(MainViewModel.IsThumbnailViewVisible):
                AndroidShellPreferences.SaveShowFilm(_main.IsThumbnailViewVisible);
                RequestLayout();
                break;
            case nameof(MainViewModel.IsControlViewVisible):
                AndroidShellPreferences.SaveShowControls(_main.IsControlViewVisible);
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
        if (args.PropertyName is nameof(ImageFile.ExifData))
        {
            // IMAGE-02：EXIF（Sony 对焦点）异步到达后同步到共享视口，下一次 Fit/切图即用新值。
            UpdateFocusPoint();
            _analysisHost.Raise();
        }
    }

    /// <summary>把当前照片的 EXIF 对焦点换算成显示朝向归一化坐标交给共享视口；无数据时清空。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        if (focus is { ImageWidth: > 0, ImageHeight: > 0 } position)
            _photo.SetFocus(position.FocusX / (double)position.ImageWidth, position.FocusY / (double)position.ImageHeight);
        else
            _photo.SetFocus(null, null);
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
        bool hasImage = _session.Image != null;
        bool hasFile = _main.CurrentFile != null;
        bool hasError = !string.IsNullOrEmpty(_session.Error);
        // STATE-01：加载提示由共享会话的 300ms 延迟态驱动，有文件但未到门槛时不显示任何文案。
        bool show = !hasImage && (hasError || _session.IsLoadingVisible || !hasFile);
        _hintLabel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        _hintDetail.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (!show) return;
        if (hasError)
        {
            _hintLabel.Text = "无法显示照片";
            _hintDetail.Text = _session.Error;
            return;
        }
        if (_session.IsLoadingVisible)
        {
            _hintLabel.Text = "正在载入…";
            _hintDetail.Text = _main.CurrentFile?.Name ?? string.Empty;
            return;
        }
        _hintLabel.Text = "PhotoViewer";
        _hintDetail.Text = "点按选择文件夹，或长按打开菜单";
    }

    /// <summary>导航图缩略图取当前展示产物（含增强），只在产物引用变化时重出；可见框与百分比每帧更新。</summary>
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
        var image = _session.Image;
        if (!ReferenceEquals(_navigationSource, image))
        {
            _navigationSource = image;
            var previous = _navigationBitmap;
            _navigationBitmap = null;
            previous?.Dispose();
            _navigation.Clear();
            int token = ++_navigationToken;
            if (image != null)
            {
                _ = AndroidNavigationDecoder.CreateFromDisplayAsync(image, AndroidUiTheme.Density(Context!), CancellationToken.None)
                    .ContinueWith(task =>
                    {
                        var bitmap = task.IsFaulted || task.IsCanceled ? null : task.Result;
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

    /// <summary>设计令牌 0xAARRGGBB → Android 颜色。</summary>
    private static Color FromToken(uint argb) => new(unchecked((int)argb));

    /// <summary>设计令牌颜色按给定不透明度叠加（0–1）。</summary>
    private static Color WithAlpha(uint argb, double alpha)
    {
        int value = unchecked((int)argb);
        return Color.Argb((int)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
            (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    /// <summary>OPEN-05：显式打开时主图回到适应态。</summary>
    private void OnFitToViewRequested() => _photo.FitToView();

    /// <summary>释放全部订阅与子视图资源。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _main.FitToViewRequested -= OnFitToViewRequested;
            _photo.SurfaceReady -= OnSurfaceReady;
            _photo.ViewportChanged -= OnPhotoViewportChanged;
            _session.Changed -= OnSessionChanged;
            _session.Notice -= OnSessionNotice;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Settings.PropertyChanged -= OnSettingsChanged;
            _main.ControlVM.PropertyChanged -= OnControlChanged;
            _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged;
            _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
            _main.RatingWriteFailed -= OnRatingWriteFailed;
            _main.OpenFailed -= OnOpenFailed;
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
