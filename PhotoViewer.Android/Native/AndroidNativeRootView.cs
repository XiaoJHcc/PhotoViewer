using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生主屏根视图：按共享 <see cref="ScreenFormResolver"/> 分宽形态与手机形态。
/// 宽形态的分区交给共享 <see cref="ShellPaneLayout"/>（本端只提供真实安全区与各栏主轴长度）；
/// 手机形态的槽位全部由共享 <see cref="PhoneScreenLayout"/> 给出（竖屏顶行/分析/胶片/底行，
/// 横屏刘海侧列/分析/控制列）。主图视图在所有形态下铺满窗口，照片落在
/// <see cref="PhotoViewport.SetFitInsets"/> 给出的适应区内，放大后可越过栏缘。
/// 取样链路逐层嵌套：根视图 → 屏幕层 → 场景层 → 照片层。照片层录主图供栏底、带衬底 EXIF 与
/// 栏底与带衬底 EXIF 取样（SHELL-12@android），玻璃控件层改从场景层取样，窗口内弹层在屏幕层之外。
/// </summary>
internal sealed class AndroidNativeRootView : FrameLayout, ICardRatingPopupHost
{
    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session;
    private readonly AndroidPhotoView _photo;
    private readonly FrameLayout _photoPane;
    private readonly LayerBackdropLayout _screenLayer;
    private readonly LayerBackdropLayout _sceneLayer;
    private readonly LayerBackdropLayout _photoLayer;
    private readonly AndroidGlassControlLayer _glassLayer;
    private readonly AndroidPhotoMaterial _photoMaterial;
    /// <summary>上一次让材质失效时的主图渲染代际；用于避免材质重绘与照片层重录互相触发。</summary>
    private int _materialFrames = -1;
    private readonly AndroidBarScrim _fileBar;
    private readonly View _fileDivider;
    private readonly AndroidBarScrim _controlBackground;
    private readonly View _controlDivider;
    private readonly AndroidFilterBar _filterBar;
    private readonly AndroidControlBarScrollHost _filterScrollHost;
    private readonly AndroidFilmstripView _filmstrip;
    private readonly AndroidControlBar _controlBar;
    private readonly AndroidControlBarScrollHost _controlScrollHost;
    private readonly AndroidAnalysisHost _analysisHost;
    private readonly AnalysisPresenter _analysisPresenter;
    private readonly AndroidAnalysisPanel _analysisPane;
#if PHOTOVIEWER_DINO
    private readonly AndroidSimilarityPanel _similarityPane;
#endif
    private readonly AndroidRatingPopup _ratingPopup;
    private readonly AndroidGlassPopupLayer _popupLayer;
    private readonly DetailFrameView _detailHighlight;
    private LayoutRect _detailRect;
    private bool _detailVisible;
    private readonly TextView _hintLabel;
    private readonly TextView _hintDetail;
    private readonly TextView _enhanceBusy;
    private readonly TextView _statusLabel;
    private readonly LinearLayout _folderStatus;
    private readonly TextView _folderStatusLabel;
    private readonly TextView _folderReopenButton;
    private LayoutRect _statusRect;
    private LayoutRect _folderStatusRect;
    private bool _folderUnavailable;
    private readonly AndroidNavigationOverlay _navigation;
    // 手机形态槽位：栏背景、筛选/更多、上下张、星级、可选命令组与 EXIF 块。
    private readonly AndroidBarScrim _phoneTopBar;
    private readonly AndroidBarScrim _phoneBottomBar;
    private readonly AndroidBarScrim _phoneLeadingBar;
    private readonly AndroidBarScrim _phoneTrailingBar;
    private readonly AndroidGlassButton _phoneMoreButton;
    private readonly AndroidGlassCombo _phoneNavigation;
    private readonly AndroidGlassCombo _phoneCommands;
    private readonly AndroidRatingControl _phoneRating;
    private readonly AndroidExifBlock _phoneExif;
    private AndroidGlassPopupSession? _photoMenu;
    private readonly List<(string Text, bool Checked, bool Enabled, string Icon)> _photoMenuItems = [];
    private AndroidWindowInsets _windowInsets;
    private AndroidInsets _insets;
    private AndroidPaneFrames _panes;
    private ScreenForm _form = ScreenForm.Wide;
    private PhoneLayout _phoneSlots;
    private PhoneLayoutInput _phoneSlotsInput;
    private AndroidScreenForm.Metrics _formMetrics;
    private int _formMetricsWidth = -1;
    private int _formMetricsHeight = -1;
    private double _formMetricsDensity = -1;
    private bool _notchOnLeft = true;
    private EdgeInsets _fitInsets;
    private ImageFile? _observedFile;
    private DecodedImage? _navigationSource;
    private Bitmap? _navigationBitmap;
    private int _navigationToken;
    private string _lastFullError = string.Empty;
    private bool _photoReady;
    private bool _startPending;
    private bool _layingOut;
    private int _layoutWidth = -1;
    private int _layoutHeight = -1;
    private bool _disposed;

    /// <summary>主图宿主（验收读取视口/手势状态）。</summary>
    internal AndroidPhotoView Photo => _photo;
    /// <summary>胶片栏（验收读取卡片与偏移）。</summary>
    internal AndroidFilmstripView Filmstrip => _filmstrip;
    /// <summary>控制栏（验收读取分组与命令）。</summary>
    internal AndroidControlBar ControlBar => _controlBar;
    /// <summary>控制栏滚动宿主（验收读取文档尺寸与滚动范围）。</summary>
    internal AndroidControlBarScrollHost ControlScrollHostForChecks => _controlScrollHost;
    /// <summary>筛选控件（验收读取几何）。</summary>
    internal AndroidFilterBar FilterBar => _filterBar;
    /// <summary>筛选条是否因窄屏进入横向滚动（验收读取）。</summary>
    internal bool FilterScrollableForChecks => _filterScrollHost.IsScrollable;
    /// <summary>筛选条当前横向滚动偏移（px，验收读取）。</summary>
    internal int FilterScrollOffsetForChecks => _filterScrollHost.ScrollOffset;
    /// <summary>筛选条滚动宿主（验收用真实手势滚动；产品路径不读取）。</summary>
    internal AndroidControlBarScrollHost FilterScrollHostForChecks => _filterScrollHost;
    /// <summary>最近一次布局算出的分区矩形（dp；手机形态为空栏位 + 适应区分区）。</summary>
    internal AndroidPaneFrames Panes => _panes;
    /// <summary>导航图宿主。</summary>
    internal AndroidNavigationOverlay Navigation => _navigation;
    /// <summary>空态提示所在的主图分区。</summary>
    internal FrameLayout PhotoPane => _photoPane;
    /// <summary>屏幕层录制容器（验收读取：根视图→屏幕层→场景层结构）。</summary>
    internal LayerBackdropLayout ScreenLayer => _screenLayer;
    /// <summary>场景层录制容器（玻璃控件层的唯一取样源）。</summary>
    internal LayerBackdropLayout SceneLayer => _sceneLayer;
    /// <summary>照片层录制容器（验收读取：取样源身份与录制状态）。</summary>
    internal LayerBackdropLayout PhotoLayer => _photoLayer;
    /// <summary>主屏玻璃控件绘制层（验收读取：锚点、配方与状态快照）。</summary>
    internal AndroidGlassControlLayer GlassControlLayer => _glassLayer;
    /// <summary>共享照片层材质（验收读取：分级、配方、效果快照、取样源与绘制计数）。</summary>
    internal AndroidPhotoMaterial PhotoMaterial => _photoMaterial;
    /// <summary>当前布局安全区（dp）；横屏已忽略手势导航条底部。</summary>
    internal AndroidInsets SafeInsets => _insets;
    /// <summary>最近一次收到的原始窗口 Insets（dp，验收用：核对旋转后由原始值重新派生）。</summary>
    internal AndroidWindowInsets WindowInsetsForChecks => _windowInsets;
    /// <summary>当前主屏形态（验收读取）。</summary>
    internal ScreenForm Form => _form;
    /// <summary>最近一次形态判定输入（窗口 dp、尺寸类别、手机设备标记）。</summary>
    internal AndroidScreenForm.Metrics FormMetrics => _formMetrics;
    /// <summary>手机形态槽位结果（非手机形态时为零值）。</summary>
    internal PhoneLayout PhoneSlots => _phoneSlots;
    /// <summary>手机形态槽位输入（验收逐项核对开关与尺寸来源）。</summary>
    internal PhoneLayoutInput PhoneSlotsInput => _phoneSlotsInput;
    /// <summary>手机横屏刘海是否在左。</summary>
    internal bool NotchOnLeft => _notchOnLeft;
    /// <summary>主图适应区四边内边距（dp）。</summary>
    internal EdgeInsets FitInsets => _fitInsets;
    /// <summary>手机竖屏「更多」按钮（验收读取矩形）。</summary>
    internal AndroidGlassButton PhoneMoreButton => _phoneMoreButton;
    /// <summary>手机上下张合体。</summary>
    internal AndroidGlassCombo PhoneNavigation => _phoneNavigation;
    /// <summary>手机可选命令组（按设置生效顺序的 1–3 段独立按钮）。</summary>
    internal AndroidGlassCombo PhoneCommands => _phoneCommands;
    /// <summary>手机星级控件。</summary>
    internal AndroidRatingControl PhoneRating => _phoneRating;
    /// <summary>手机 EXIF 块。</summary>
    internal AndroidExifBlock PhoneExif => _phoneExif;
    /// <summary>分析栏（验收读取瓦片/可见性）。</summary>
    internal AndroidAnalysisPanel AnalysisPane => _analysisPane;
#if PHOTOVIEWER_DINO
    /// <summary>相似聚类面板（验收读取可见性/列表）。</summary>
    internal AndroidSimilarityPanel SimilarityPane => _similarityPane;
#endif
    /// <summary>星级弹窗（验收读取方向/锚点/条目）。</summary>
    internal AndroidRatingPopup RatingPopup => _ratingPopup;
    /// <summary>窗口内弹层宿主（验收读取面板实例、模态与出入场；产品路径只经入口使用）。</summary>
    internal AndroidGlassPopupLayer PopupLayer => _popupLayer;
    /// <summary>当前是否有窗口内弹层（菜单、筛选下拉或星级弹窗）打开。</summary>
    internal bool IsGlassPopupOpen => _popupLayer.IsOpen;
    /// <summary>活动细节绿框视图。</summary>
    internal View DetailHighlightView => _detailHighlight;
    /// <summary>主图细节绿框当前是否可见（验收读取活动目标联动）。</summary>
    internal bool DetailFrameVisible => _detailVisible;
    /// <summary>共享分析编排（验收读取准星）。</summary>
    internal AnalysisPresenter AnalysisPresenterForChecks => _analysisPresenter;
    /// <summary>分析宿主（验收用：订阅 HostChanged 核对纯平移零通知、真实内容变化有通知）。</summary>
    internal AndroidAnalysisHost AnalysisHostForChecks => _analysisHost;
    /// <summary>状态提示条正文（验收用）。</summary>
    internal string? StatusTextForChecks => _statusLabel.Text;
    /// <summary>状态提示条是否可见（验收用）。</summary>
    internal bool StatusVisibleForChecks => _statusLabel.Visibility == ViewStates.Visible;
    /// <summary>短提示条矩形（dp，验收用：核对叠放顺序）。</summary>
    internal LayoutRect StatusRectForChecks => _statusRect;
    /// <summary>STATE-05 持续状态是否可见（验收用）。</summary>
    internal bool FolderStatusVisibleForChecks => _folderStatus.Visibility == ViewStates.Visible;
    /// <summary>STATE-05 持续状态正文（验收用）。</summary>
    internal string? FolderStatusTextForChecks => _folderStatusLabel.Text;
    /// <summary>STATE-05 文字按钮文案（验收用）。</summary>
    internal string? FolderReopenTextForChecks => _folderReopenButton.Text;
    /// <summary>STATE-05 文字按钮是否可点按且可被读屏聚焦（验收用）。</summary>
    internal bool FolderReopenClickableForChecks => _folderReopenButton.Clickable && _folderReopenButton.Focusable;
    /// <summary>STATE-05 持续状态外框是否不接收焦点（验收用：呈现时不抢焦点）。</summary>
    internal bool FolderStatusContainerFocusableForChecks => _folderStatus.Focusable;
    /// <summary>STATE-05 持续状态矩形（dp，验收用）。</summary>
    internal LayoutRect FolderStatusRectForChecks => _folderStatusRect;
    /// <summary>当前形态下实际生效的评分输入是否可用（验收用）。</summary>
    internal bool RatingInputEnabledForChecks => _form == ScreenForm.Wide
        ? _controlBar.RatingInputEnabledForChecks
        : _phoneRating.EnabledForChecks;
    /// <summary>主屏栏底（验收读取实际色层）：宽形态文件栏/工具区与手机四侧栏底。</summary>
    internal IReadOnlyList<(string Name, AndroidBarScrim Bar)> BarScrimsForChecks =>
    [
        ("fileBar", _fileBar), ("controlBackground", _controlBackground),
        ("phoneTop", _phoneTopBar), ("phoneBottom", _phoneBottomBar),
        ("phoneLeading", _phoneLeadingBar), ("phoneTrailing", _phoneTrailingBar),
    ];
    /// <summary>最近一次主图菜单构建出的项（文本/勾选/可用/实际绘制的图标字形，验收用只读快照；产品路径不读取）。</summary>
    internal IReadOnlyList<(string Text, bool Checked, bool Enabled, string Icon)> PhotoMenuItemsForChecks => _photoMenuItems;

    /// <summary>收起状态提示条并作废在途自动隐藏（验收用，避免上一条提示冒充本次结果）。</summary>
    internal void ResetStatusForChecks()
    {
        _statusToken++;
        _statusLabel.Visibility = ViewStates.Gone;
        RequestLayout();
    }

    /// <summary>建立主屏视图树并接线共享 VM 与会话。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="session">展示加载会话。</param>
    public AndroidNativeRootView(Activity activity, MainViewModel main, PhotoLoadSession session) : base(activity)
    {
        _main = main;
        _session = session;
        // SHELL-04：栏位显隐从平台偏好恢复（分析栏仍为会话态；手机形态 EXIF 开关同一存储、默认开）。
        _main.IsThumbnailViewVisible = AndroidShellPreferences.LoadShowFilm();
        _main.IsControlViewVisible = AndroidShellPreferences.LoadShowControls();
        _main.IsExifViewVisible = AndroidShellPreferences.LoadShowExif();
        SetBackgroundColor(AndroidUiTheme.Background);
        SetClipChildren(true);

        _photoPane = new FrameLayout(activity);
        _photoPane.SetClipChildren(true);
        _photoPane.SetBackgroundColor(AndroidUiTheme.PhotoBackground);
        _photo = new AndroidPhotoView(activity);
        _main.FitToViewRequested += OnFitToViewRequested;
        _photoPane.AddView(_photo, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        // SHELL-12@android：照片层录主图（TextureView）的 RenderNode，铺满窗口、在场景层最底层；
        // 栏底、带衬底 EXIF 与玻璃控件层都从这一层取样，栏内容与取样方都不进这一层（上游取样规则）。
        _photoLayer = new LayerBackdropLayout(activity);
        _photoLayer.SetClipChildren(true);
        _photoLayer.AddView(_photoPane, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));

        // 取样层级：根视图 → 屏幕层 → 场景层 → 照片层。主屏现有内容与照片都进场景层；
        // 玻璃控件层在屏幕层内、场景层之后，只从场景层取样；弹层宿主仍在屏幕层之外的根视图最上层。
        _sceneLayer = new LayerBackdropLayout(activity);
        _sceneLayer.SetClipChildren(true);
        _sceneLayer.RecordsBackdrop = BackdropCapabilities.IsRenderEffectSupported;
        _sceneLayer.AddView(_photoLayer, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        _screenLayer = new LayerBackdropLayout(activity);
        _screenLayer.SetClipChildren(true);
        _screenLayer.RecordsBackdrop = BackdropCapabilities.IsRenderEffectSupported;
        _screenLayer.AddView(_sceneLayer, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        AddView(_screenLayer, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        _glassLayer = new AndroidGlassControlLayer(activity, _sceneLayer);
        _screenLayer.AddView(_glassLayer, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));

        _photoMaterial = new AndroidPhotoMaterial(_photoLayer);
        _photoLayer.Recorded += OnPhotoLayerRecorded;
        // 共享效果层的离屏缓冲与主图纹理同属这一进程的 GPU 外部占用，一起记进预算源。
        _photo.AdditionalFootprint = () => _photoMaterial.FootprintBytes + _glassLayer.FootprintBytes;

        _fileBar = new AndroidBarScrim(activity, _photoMaterial);
        _sceneLayer.AddView(_fileBar);
        _fileDivider = new View(activity);
        _fileDivider.SetBackgroundColor(AndroidUiTheme.Divider);
        _sceneLayer.AddView(_fileDivider);
        _controlBackground = new AndroidBarScrim(activity, _photoMaterial);
        _sceneLayer.AddView(_controlBackground);
        _controlDivider = new View(activity);
        _controlDivider.SetBackgroundColor(AndroidUiTheme.Divider);
        _sceneLayer.AddView(_controlDivider);

        _popupLayer = new AndroidGlassPopupLayer(activity);
        _filterBar = new AndroidFilterBar(activity, main, _glassLayer);
        _filterBar.PopupLayer = _popupLayer;
        _filterScrollHost = new AndroidControlBarScrollHost(activity, _filterBar);
        // 筛选条滚动时锚点跟着移走，已打开的下拉不能停在旧位置。
        _filterScrollHost.ScrollChanged += OnFilterScrollChanged;
        _sceneLayer.AddView(_filterScrollHost);
        _filmstrip = new AndroidFilmstripView(activity, main);
        _sceneLayer.AddView(_filmstrip);
        _filmstrip.RateRequested += OnRateRequested;
        _filmstrip.PopupHost = this;
        _controlBar = new AndroidControlBar(activity, main, _photoMaterial, _glassLayer);
        _controlBar.Wire(Execute, rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); },
            ShowExifTools, OpenRatingPopupFromControlBar);
        _controlScrollHost = new AndroidControlBarScrollHost(activity, _controlBar);
        _sceneLayer.AddView(_controlScrollHost);
        _ratingPopup = new AndroidRatingPopup(this, main, _popupLayer,
            () => _windowInsets.DeriveSafe(_main.IsScreenLandscape));
        _controlBar.RatingControl.Popup = _ratingPopup;
        _filterBar.DropdownOpening += CloseRatingPopup;

        _analysisHost = new AndroidAnalysisHost(main, session);
        _analysisPresenter = new AnalysisPresenter(_analysisHost);
        _analysisPane = new AndroidAnalysisPanel(activity, _analysisPresenter, session);
        _analysisPane.TileCountChanged += RequestLayout;
        _analysisPane.DetailChanged += UpdateDetailHighlight;
        _analysisPane.DetailFocusRequested += (u, v) => _photo.FocusOnPoint(u, v, 1);
        _analysisPane.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_analysisPane);
#if PHOTOVIEWER_DINO
        _similarityPane = new AndroidSimilarityPanel(activity, main);
        _similarityPane.PopupHost = this;
        _similarityPane.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_similarityPane);
#endif

        _phoneTopBar = new AndroidBarScrim(activity, _photoMaterial);
        _phoneBottomBar = new AndroidBarScrim(activity, _photoMaterial);
        _phoneLeadingBar = new AndroidBarScrim(activity, _photoMaterial);
        _phoneTrailingBar = new AndroidBarScrim(activity, _photoMaterial);
        // 栏背景压在照片之上、所有内容之下：手机形态的筛选、胶片条、分析栏与按钮都在它们上层。
        foreach (var bar in new[] { _phoneTopBar, _phoneBottomBar, _phoneLeadingBar, _phoneTrailingBar })
        {
            bar.Visibility = ViewStates.Gone;
            _sceneLayer.AddView(bar, 1);
        }
        _phoneMoreButton = new AndroidGlassButton(activity, AndroidIcons.More, "更多");
        _phoneMoreButton.Click += (_, _) => ShowPhotoMenuAt(_phoneMoreButton);
        _phoneMoreButton.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_phoneMoreButton);
        _glassLayer.Register(_phoneMoreButton);
        _phoneNavigation = new AndroidGlassCombo(activity, AndroidIcons.Previous, "上一张",
            AndroidIcons.Next, "下一张");
        _phoneNavigation.First.Click += (_, _) => Execute("Previous");
        _phoneNavigation.Second.Click += (_, _) => Execute("Next");
        _phoneNavigation.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_phoneNavigation);
        _glassLayer.Register(_phoneNavigation);
        _phoneCommands = new AndroidGlassCombo(activity);
        _phoneCommands.CommandActivated += Execute;
        _phoneCommands.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_phoneCommands);
        _glassLayer.Register(_phoneCommands);
        _phoneRating = new AndroidRatingControl(activity);
        _phoneRating.RateRequested += rating => { if (_main.CurrentFile != null) _ = RateAsync(_main.CurrentFile, rating); };
        _phoneRating.PopupRequested += OpenRatingPopupFromPhone;
        _phoneRating.Popup = _ratingPopup;
        _phoneRating.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_phoneRating);
        _glassLayer.Register(_phoneRating);
        _phoneExif = new AndroidExifBlock(activity, _photoMaterial);
        _phoneExif.ExifActivated += ShowExifTools;
        _phoneExif.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_phoneExif);
        _filterBar.MoreRequested += rect =>
        {
            var location = new int[2];
            _filterBar.GetLocationOnScreen(location);
            var photoLocation = new int[2];
            _photo.GetLocationOnScreen(photoLocation);
            double density = AndroidUiTheme.Density(Context!);
            ShowPhotoMenu(
                (float)(location[0] - photoLocation[0] + (rect.X + rect.Width / 2) * density),
                (float)(location[1] - photoLocation[1] + (rect.Y + rect.Height) * density));
        };

        _detailHighlight = new DetailFrameView(activity);
        _detailHighlight.Visibility = ViewStates.Gone;
        _detailHighlight.Clickable = false;
        _sceneLayer.AddView(_detailHighlight);

        // STATE-01：首屏空态两行提示只做展示，命中交给主图点按；提示层不拦截点按与拖入。
        _hintLabel = new TextView(activity)
        {
            Text = PhotoHintText.EmptyProductTitle,
            TextSize = (float)DesignTokens.ImageHintTitleFontSize,
            Gravity = GravityFlags.Center,
            Clickable = false,
            Focusable = false,
        };
        _hintLabel.SetTextColor(AndroidUiTheme.TextPrimary);
        _hintLabel.SetTypeface(Typeface.Default, TypefaceStyle.Normal);
        _hintLabel.SetIncludeFontPadding(false);
        _hintDetail = new TextView(activity)
        {
            Text = string.Empty,
            TextSize = (float)DesignTokens.ImageHintDetailFontSize,
            Gravity = GravityFlags.Center,
            Clickable = false,
            Focusable = false,
        };
        _hintDetail.SetTextColor(AndroidUiTheme.TextSecondary);
        _hintDetail.SetTypeface(Typeface.Default, TypefaceStyle.Normal);
        _hintDetail.SetIncludeFontPadding(false);
        _sceneLayer.AddView(_hintLabel);
        _sceneLayer.AddView(_hintDetail);

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
        _sceneLayer.AddView(_enhanceBusy);

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
        _sceneLayer.AddView(_statusLabel);

        // STATE-05：已打开目录持续不可用时的非模态状态，沿用状态提示条的颜色、字号与圆角。
        _folderStatusLabel = new TextView(activity)
        {
            Text = StatusMessages.FolderUnavailable,
            TextSize = (float)DesignTokens.StatusToastFontSize,
            Gravity = GravityFlags.CenterVertical,
            Clickable = false,
            Focusable = false,
        };
        _folderStatusLabel.SetTextColor(FromToken(DesignTokens.ColorStatusToastForeground));
        _folderStatusLabel.SetTypeface(Typeface.Default, TypefaceStyle.Normal);
        _folderStatusLabel.SetIncludeFontPadding(false);
        _folderStatusLabel.SetPadding(0, 0, AndroidUiTheme.DpPx(this, DesignTokens.StatusToastStackGap), 0);
        _folderReopenButton = new TextView(activity)
        {
            Text = StatusMessages.ReopenFolder,
            TextSize = (float)DesignTokens.StatusToastFontSize,
            Gravity = GravityFlags.CenterVertical,
            Clickable = true,
            Focusable = true,
        };
        _folderReopenButton.SetTextColor(FromToken(DesignTokens.ColorStatusToastForeground));
        _folderReopenButton.SetTypeface(Typeface.Default, TypefaceStyle.Normal);
        _folderReopenButton.SetIncludeFontPadding(false);
        _folderReopenButton.SetPadding(0, 0, 0, 0);
        _folderReopenButton.Background = new RippleDrawable(
            AndroidX.Core.Content.ContextCompat.GetColorStateList(activity, Resource.Color.pv_glass_state_layer),
            null, null);
        _folderReopenButton.Click += (_, _) => _ = _main.FolderVM.ReopenFolderAsync();
        _folderStatus = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        _folderStatus.SetGravity(GravityFlags.CenterVertical);
        _folderStatus.SetPadding(statusPaddingX, statusPaddingY, statusPaddingX, statusPaddingY);
        _folderStatus.Focusable = false;
        var folderStatusBackground = new GradientDrawable();
        folderStatusBackground.SetColor(WithAlpha(
            DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha));
        folderStatusBackground.SetCornerRadius(
            (float)AndroidUiTheme.Dp(activity, DesignTokens.StatusToastCornerRadius));
        _folderStatus.Background = folderStatusBackground;
        _folderStatus.Visibility = ViewStates.Gone;
        _folderStatus.AddView(_folderStatusLabel,
            new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent));
        _folderStatus.AddView(_folderReopenButton,
            new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent));
        _sceneLayer.AddView(_folderStatus);

        _navigation = new AndroidNavigationOverlay(activity);
        _navigation.Wire((u, v) => _photo.CenterOnNormalizedPoint(u, v));
        _navigation.Visibility = ViewStates.Gone;
        _sceneLayer.AddView(_navigation);
        // MENU-03 / FILTER-08 / RATING-04：三处弹层共用主窗口内最上层宿主，取屏幕层最终画面。
        _popupLayer.Backdrop = BackdropCapabilities.IsRenderEffectSupported ? _screenLayer.Backdrop : null;
        _popupLayer.Surface = new AndroidGlassPopupMaterial();
        _sceneLayer.Recorded += OnSceneLayerRecorded;
        _screenLayer.Recorded += OnScreenLayerRecorded;
        AddView(_popupLayer);

        _photo.PresentationFailed += reason => ShowStatus(
            StatusMessages.PresentationFailed(_main.CurrentFile?.Name, reason), DesignTokens.MotionStatusToastDuration);
        _photo.SurfaceReady += OnSurfaceReady;
        _photo.PresentationFrameRendered += OnPresentationFrameRendered;
        _photo.ViewportChanged += OnPhotoViewportChanged;
        _photo.SwipeRequested += direction => Execute(direction < 0 ? "Previous" : "Next");
        _photo.TapRequested += OnPhotoTapped;
        _photo.LongPressRequested += OnPhotoLongPressed;
        _photo.WheelCommand = OnWheelCommand;

        _session.Changed += OnSessionChanged;
        _session.Notice += OnSessionNotice;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Settings.PresentationChanged += OnSettingsPresentationChanged;
        _main.ControlVM.PropertyChanged += OnControlChanged;
        _main.FileVM.ThumbnailList.PropertyChanged += OnFilesChanged;
        _main.FileVM.ThumbnailList.ScrollToCurrentRequested += OnScrollToCurrentRequested;
        _main.FileVM.FilterBar.PropertyChanged += OnFilterChanged;
        _main.FolderVM.AllFilesChanged += OnAllFilesChanged;
        _main.FolderVM.OpenStateChanged += OnOpenStateChanged;
        _main.FolderVM.FolderAvailabilityChanged += OnFolderAvailabilityChanged;
#if PHOTOVIEWER_DINO
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
#endif
        _main.RatingWriteFailedDetailed += OnRatingWriteFailed;
        _main.OpenFailed += OnOpenFailed;
        _observedFile = _main.CurrentFile;
        if (_observedFile != null) _observedFile.PropertyChanged += OnCurrentFileChanged;
        ApplyFolderAvailability(_main.FolderVM.IsFolderUnavailable);
        UpdateHint();
    }

    /// <summary>系统栏/刘海/圆角 Insets（dp）；Android 15 强制 edge-to-edge 时必须走这条路径。</summary>
    /// <param name="insets">系统 Insets。</param>
    /// <returns>父类处理结果。</returns>
    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        if (insets == null) return base.OnApplyWindowInsets(insets);
        var compat = WindowInsetsCompat.ToWindowInsetsCompat(insets, this)!;
        var bars = compat.GetInsets(WindowInsetsCompat.Type.SystemBars())!;
        var cutout = compat.GetInsets(WindowInsetsCompat.Type.DisplayCutout())!;
        // 导航栏与可点元素底部同位即三键导航；手势导航只有导航栏底部，没有可点按钮。
        var navigation = compat.GetInsets(WindowInsetsCompat.Type.NavigationBars())!;
        var tappable = compat.GetInsets(WindowInsetsCompat.Type.TappableElement())!;
        double density = AndroidUiTheme.Density(Context!);
        _windowInsets = new AndroidWindowInsets(
            new AndroidInsets(bars.Left / density, bars.Top / density, bars.Right / density, bars.Bottom / density),
            new AndroidInsets(cutout.Left / density, cutout.Top / density, cutout.Right / density, cutout.Bottom / density),
            navigation.Bottom / density, tappable.Bottom / density,
            ReadCutoutRects(insets, density), ReadCornerRadii(insets, density));
        DeriveSafeInsets(LandscapeForInsets());
        // Insets 读数变化后一律重排：旋转期间系统栏可见性切换会让方向与读数先后到达，
        // 只按“安全区是否变化”决定会留下上一方向的缓存值。
        RequestLayout();
        return base.OnApplyWindowInsets(insets);
    }

    /// <summary>公开 <c>DisplayCutout</c> 的挖孔矩形（API 29+）；低版本返回空列表。</summary>
    private static IReadOnlyList<AndroidCutoutRect> ReadCutoutRects(WindowInsets insets, double density)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return Array.Empty<AndroidCutoutRect>();
        var displayCutout = insets.DisplayCutout;
        if (displayCutout?.BoundingRects is not { Count: > 0 } rects) return Array.Empty<AndroidCutoutRect>();
        var list = new List<AndroidCutoutRect>(rects.Count);
        foreach (var rect in rects)
            list.Add(new AndroidCutoutRect(rect.Left / density, rect.Top / density,
                rect.Right / density, rect.Bottom / density));
        return list;
    }

    /// <summary>公开 <c>WindowInsets.RoundedCorner</c>（API 31+）的四角半径；低版本或窗口无圆角时为 0。</summary>
    private static AndroidCornerRadii ReadCornerRadii(WindowInsets insets, double density)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return default;
        double Radius(int position)
        {
            var corner = insets.GetRoundedCorner(position);
            return corner == null ? 0 : corner.Radius / density;
        }
        return new AndroidCornerRadii(
            Radius((int)global::Android.Views.RoundedCornerPosition.TopLeft),
            Radius((int)global::Android.Views.RoundedCornerPosition.TopRight),
            Radius((int)global::Android.Views.RoundedCornerPosition.BottomRight),
            Radius((int)global::Android.Views.RoundedCornerPosition.BottomLeft));
    }

    /// <summary>
    /// 按方向从原始 Insets 重新派生布局安全区：横屏忽略手势导航条底部，竖屏保留底部系统栏；
    /// 旋转后旧值不会残留。返回是否发生变化。
    /// </summary>
    /// <param name="landscape">是否横屏。</param>
    private bool DeriveSafeInsets(bool landscape)
    {
        var next = _windowInsets.DeriveSafe(landscape);
        bool changed = Math.Abs(next.Left - _insets.Left) > .01 || Math.Abs(next.Top - _insets.Top) > .01
            || Math.Abs(next.Right - _insets.Right) > .01 || Math.Abs(next.Bottom - _insets.Bottom) > .01;
        // 每个布局阶段都以最新原始读数 + 当前方向重写缓存，旋转后不残留上一方向的值。
        _insets = next;
        return changed;
    }

    /// <summary>
    /// Insets 到达时的方向：窗口尺寸已落定则按实际宽高，尚未布局（0 尺寸）时用配置方向兜底；
    /// 布局阶段还会以真实宽高再派生一次。
    /// </summary>
    private bool LandscapeForInsets()
    {
        if (Width > 0 && Height > 0) return Width > Height;
        return Context?.Resources?.Configuration?.Orientation == global::Android.Content.Res.Orientation.Landscape;
    }

    /// <summary>挂上窗口后主动请求一次 Insets（TextureView 与其他子视图不消费，安全区只由根收口）。</summary>
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        RequestApplyInsets();
    }

    /// <summary>窗口失活（系统弹窗、模态页、切后台）时收起三处弹层，不在恢复前台后残留（MENU-07、RATING-04）。</summary>
    /// <param name="hasFocus">窗口是否获得焦点。</param>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) return;
        DismissPhotoMenu();
        _filterBar.DismissDropdown();
        CloseRatingPopup();
    }

    /// <summary>根 ViewGroup 只在布局阶段给子视图精确尺寸，Measure 直接吃满全屏。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), MeasureSpec.GetSize(heightMeasureSpec));
    }

    /// <summary>按形态分发布局：宽形态走共享分区，手机形态走共享槽位；旋转/尺寸变化只重排不重建会话。</summary>
    /// <param name="changed">布局是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        if (_layingOut || Width < 1 || Height < 1) return;
        long statsStart = AndroidFrameStats.Mark();
        _layingOut = true;
        try
        {
            var activity = Context as Activity;
            double density = AndroidUiTheme.Density(Context!);
            double widthDp = Width / density, heightDp = Height / density;
            // 窗口尺寸变化（旋转、分屏、折叠）后旧锚点已失效：三处弹层一并收起，不在新窗口里残留。
            if (Width != _layoutWidth || Height != _layoutHeight)
            {
                _layoutWidth = Width;
                _layoutHeight = Height;
                DismissPhotoMenu();
                _filterBar.DismissDropdown();
                CloseRatingPopup();
            }
            _main.UpdateScreenOrientation(widthDp, heightDp);
            // 旋转/分屏后按真实窗口方向重新派生安全区，不沿用上一方向的底部读数。
            DeriveSafeInsets(widthDp > heightDp);
            var previous = _form;
            if (activity != null)
            {
                // 窗口尺寸没变时复用尺寸类别；只有真实 resize/旋转才重新查询 WindowMetrics。
                double densityNow = AndroidUiTheme.Density(activity);
                if (Width != _formMetricsWidth || Height != _formMetricsHeight
                    || Math.Abs(densityNow - _formMetricsDensity) > .001 || _formMetricsWidth < 0)
                {
                    _formMetrics = AndroidScreenForm.Read(activity);
                    _formMetricsWidth = Width;
                    _formMetricsHeight = Height;
                    _formMetricsDensity = densityNow;
                }
                _form = ScreenFormResolver.Resolve(_formMetrics.HorizontalCompact, _formMetrics.VerticalCompact,
                    widthDp, heightDp, _formMetrics.PhoneDevice,
                    _main.Settings.LayoutMode, LayoutCapabilities.PhoneLayoutSupported);
            }
            else
            {
                _form = ScreenForm.Wide;
            }
            if (_form != previous)
            {
                // 形态切换只重排：收起临时浮层，主图/列表/缩放保持。
                DismissPhotoMenu();
                _filterBar.DismissDropdown();
                CloseRatingPopup();
                _phoneMoreButton.Visibility = ViewStates.Gone;
            }
            // 三层录制容器与玻璃控件层都铺满窗口；场景内容与照片仍按原有根坐标落位。
            var fullRect = new LayoutRect(0, 0, widthDp, heightDp);
            LayoutChild(_screenLayer, fullRect, density);
            LayoutChild(_sceneLayer, fullRect, density);
            LayoutChild(_glassLayer, fullRect, density);
            if (_form == ScreenForm.Wide) LayoutWide(widthDp, heightDp, density);
            else LayoutPhone(widthDp, heightDp, density);

            // 最终几何落定后再核对下拉锚点：窄屏一次 OnLayout 的两次 Arrange 都不会收起菜单。
            _filterBar.CommitLayout();
            LayoutOverlays(density);
            UpdateDetailHighlight();
            UpdateNavigationIndicator();
            // 弹层宿主始终铺满窗口，面板在其中按自身窗口矩形落位（与主屏布局解耦）。
            LayoutChild(_popupLayer, new LayoutRect(0, 0, widthDp, heightDp), density);
            // 筛选条滚动宿主的 BringToFront 会把它移到弹层之上；弹层仍须是最后一个子视图（绘制与命中最上层）。
            if (ChildCount > 0 && !ReferenceEquals(GetChildAt(ChildCount - 1), _popupLayer))
                _popupLayer.BringToFront();
            // 锚点几何与状态可能在本轮布局中变化；玻璃层与场景层分开绘制，显式重绘一次。
            _glassLayer.Invalidate();
        }
        finally
        {
            _layingOut = false;
            AndroidFrameStats.Span(FrameStatsKind.Layout, statsStart, AndroidFrameStats.Mark());
        }
    }

    /// <summary>宽形态：共享分区 + 大档胶片 + 流式控制栏；手机形态专属槽位全部隐藏。</summary>
    private void LayoutWide(double widthDp, double heightDp, double density)
    {
        // SHELL-11：主图视图在所有形态下铺满窗口，照片落在适应区内；照片层同样显式铺满窗口。
        LayoutChild(_photoLayer, new LayoutRect(0, 0, widthDp, heightDp), density);
        HidePhoneLayers();
        bool row = _main.IsRowLayout;
        bool filmVisible = _main.IsThumbnailViewVisible;
        bool controlVisible = _main.IsControlViewVisible;
        bool analysisVisible = _main.IsAnalysisViewVisible;
        _analysisPane.Visibility = analysisVisible ? ViewStates.Visible : ViewStates.Gone;
        var tier = FilmCardTier.Large;
        double bandCross = FilmCardGeometry.BandThickness(tier, false);
        var filmSide = FilmSideGeometry.Measure(_insets.Left, bandCross, FilmCardGeometry.CrossLead(tier, false),
            FilmCardGeometry.CrossTrail(tier, false), FilmCardGeometry.Width(tier));
        // FILTER-07：列布局的首控件让出该列位置上的顶部遮挡（此处为系统栏/状态栏）再留一份外边距；
        // 行布局的内容区已从安全区顶边开始，只留一份外边距。
        double leadingInset = (row ? 0 : _insets.Top) + MainScreenLayout.FilterMargin;
        double filterContentWidth = row
            ? Math.Max(1, widthDp - _insets.Left - _insets.Right)
            : FilmCardGeometry.BandThickness(tier, false);
        double filterMain = _filterBar.MainLengthForLayout(row, filterContentWidth, leadingInset);
        double availableMain = row
            ? Math.Max(1, widthDp - _insets.Left - _insets.Right)
            : Math.Max(1, heightDp - _insets.Top - _insets.Bottom);
#if PHOTOVIEWER_DINO
        bool similarityOpen = filmVisible && _main.FileVM.FilterBar.IsSimilarityPanelOpen;
        if (similarityOpen) _similarityPane.SetLayout(row);
        var similarityPanel = _main.FileVM.SimilarityPanel;
        double similarityDock = similarityPanel.HasItems && similarityPanel.ShowActionArea
            ? _similarityPane.DockMeasureWidth()
            : 0;
        double similarityColumn = similarityOpen
            ? (row
                ? SimilarityPanelGeometry.RowWidth(similarityPanel.HasItems,
                    similarityPanel.SimilarItems.Count, availableMain, similarityDock)
                : _similarityPane.CrossSize + SimilarityPanelGeometry.Margin)
            : 0;
#else
        const double similarityColumn = 0;
#endif
        double fileMain = filmVisible
            ? (row
                ? filterMain + FilmCardGeometry.BandThickness(tier, true) + MainScreenLayout.Divider
                : filmSide.Width + MainScreenLayout.Divider + similarityColumn)
            : 0;
        _controlBar.SetJoinedToAnalysis(analysisVisible);
        var controlMeasure = controlVisible
            ? _controlBar.MeasureDocument(row, availableMain)
            : (Main: 0.0, Cross: 0.0);
        // ToolbarFlowLayout 的 Main 是文档沿主轴长度、Cross 是交叉轴厚度；分区只要厚度。
        double controlMain = controlVisible
            ? MainScreenLayout.ControlBarMainLength(controlMeasure.Cross)
            : 0;
        _analysisHost.SetVisible(analysisVisible);
        double tileSize = AnalysisPanelGeometry.TileSize(availableMain, Math.Max(1, _analysisPane.TileCount));
        double analysisSize = analysisVisible
            ? ToolRegionGeometry.AnalysisWidth(tileSize, controlVisible)
            : 0;
        _panes = AndroidMainScreenLayout.Compute(row, widthDp, heightDp, _insets, fileMain, controlMain, analysisSize,
            row ? null : filmSide.FilmstripX, row ? null : filmSide.Width - filmSide.FilmstripX);

        ApplyFitInsets(new EdgeInsets(
            Math.Max(0, _panes.Photo.X), Math.Max(0, _panes.Photo.Y),
            Math.Max(0, widthDp - _panes.Photo.MaxX), Math.Max(0, heightDp - _panes.Photo.MaxY)));

        LayoutChild(_fileBar, _panes.FileBar, density);
        _fileBar.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
        LayoutChild(_fileDivider, _panes.FileDivider, density);
        _fileDivider.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
        if (analysisVisible)
        {
            LayoutChild(_analysisPane, _panes.Analysis, density);
            _analysisPane.Arrange(row, availableMain, row ? _insets.Left : _insets.Top);
        }
        bool showsToolBackground = analysisVisible || (controlVisible && _controlBar.HasExifGroup);
        LayoutChild(_controlBackground, showsToolBackground ? _panes.ControlBackground : default, density);
        _controlBackground.Visibility = showsToolBackground ? ViewStates.Visible : ViewStates.Gone;
        // SHELL-12：工具区只有一条朝主图的外边界；分析栏存在时它就是分析栏贴主图的一侧。
        var toolDivider = _panes.ControlDivider;
        if (analysisVisible)
            toolDivider = row
                ? new LayoutRect(0, _panes.Analysis.Y, widthDp, MainScreenLayout.Divider)
                : new LayoutRect(_panes.Analysis.X, 0, MainScreenLayout.Divider, heightDp);
        bool dividerVisible = analysisVisible || (controlVisible && showsToolBackground && Has(toolDivider));
        LayoutChild(_controlDivider, toolDivider, density);
        _controlDivider.Visibility = dividerVisible ? ViewStates.Visible : ViewStates.Gone;

        _filterScrollHost.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
        _filmstrip.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
#if PHOTOVIEWER_DINO
        _similarityPane.Visibility = similarityOpen ? ViewStates.Visible : ViewStates.Gone;
#endif
        if (filmVisible)
        {
            var content = _panes.FileContent;
            double contentWidth = row
                ? Math.Max(1, content.Width)
                : Math.Max(1, Math.Min(MainScreenLayout.FileBarContentWidth, content.Width));
            // 列布局的末端锚点是窗口坐标，筛选条自身从内容区左缘起排，换算到视图本地坐标。
            _filterBar.Arrange(row, contentWidth, leadingInset, row ? null : filmSide.ContentTrailing - content.X);
            var filterRect = new LayoutRect(content.X, content.Y, Math.Max(1, content.Width), filterMain);
            double filterDocument = row ? _filterBar.DocumentWidth(row, contentWidth) : filterMain;
            _filterScrollHost.Configure(row,
                (int)Math.Round((row ? content.Width : filterMain) * density),
                (int)Math.Round(filterDocument * density));
            // FILM-07：列布局胶片条纵贯内容全高，筛选组浮在列表上方，不能被列表覆盖。
            _filterScrollHost.BringToFront();
            LayoutChild(_filterScrollHost, filterRect, density);
            double filmWidth = row ? Math.Max(1, content.Width - similarityColumn) : Math.Max(1, content.Width);
            // FILM-07：宽形态列布局的胶片条纵贯文件栏内容全高，筛选组浮在列表上方；
            // 首卡用内容留白停在 filterMain（额外前导 = filterMain − 大档主轴内边距）。
            var filmFrame = row
                ? new LayoutRect(content.X, content.Y + filterMain, filmWidth, Math.Max(1, content.Height - filterMain))
                : new LayoutRect(content.X, content.Y, filmWidth, Math.Max(1, content.Height));
            LayoutChild(_filmstrip, filmFrame, density);
            bool axisChanged = _filmstrip.IsHorizontal != row;
            _filmstrip.SetAxis(row);
            _filmstrip.SetTier(tier);
            _filmstrip.SetEdgePadding(row ? 0 : Math.Max(0, filterMain - FilmCardGeometry.MainLead(tier, false)), 0);
            if (axisChanged) _filmstrip.ScrollToCurrent();
#if PHOTOVIEWER_DINO
            if (similarityOpen)
            {
                var similarityRect = row
                    ? new LayoutRect(content.X + content.Width - similarityColumn, content.Y + filterMain,
                        Math.Max(1, similarityColumn - SimilarityPanelGeometry.Margin), _similarityPane.CrossSize)
                    : new LayoutRect(content.X + filmSide.Width, content.Y + filterMain,
                        _similarityPane.CrossSize, Math.Max(1, content.Height - filterMain));
                LayoutChild(_similarityPane, similarityRect, density);
            }
#endif
        }

        _controlScrollHost.Visibility = controlVisible ? ViewStates.Visible : ViewStates.Gone;
        if (controlVisible)
        {
            var content = _panes.ControlContent;
            double viewportLength = row ? content.Width : content.Height;
            // 量测与排布共用同一可用主轴；滚动范围取排布后的最终文档尺寸，避免紧凑/展开二次切换。
            var arranged = _controlBar.Arrange(row, Math.Max(1, viewportLength));
            _controlScrollHost.Configure(row, (int)Math.Round(viewportLength * density),
                (int)Math.Round(arranged.Main * density));
            LayoutChild(_controlScrollHost, content, density);
        }
    }

    /// <summary>
    /// 手机形态：槽位全部取 <see cref="PhoneScreenLayout"/>；贴角带按公开 Insets（系统安全区、显示挖孔、
    /// RoundedCorner 半径）在本带实际位置派生，主图适应区取槽位给出的 <see cref="PhoneLayout.PhotoAvoidance"/>。
    /// </summary>
    private void LayoutPhone(double widthDp, double heightDp, double density)
    {
        LayoutChild(_photoLayer, new LayoutRect(0, 0, widthDp, heightDp), density);
        bool portrait = _form == ScreenForm.PhonePortrait;
        double button = DesignTokens.ShellGlassButtonSize;
        double rowInset = DesignTokens.ShellPhoneRowInset;
        var plain = new EdgeInsets(_insets.Left, _insets.Top, _insets.Right, _insets.Bottom);
        _notchOnLeft = ResolveNotchOnLeft(widthDp);
        var tier = FilmCardGeometry.ResolveTier(portrait ? widthDp : heightDp, portrait);
        double filmBand = FilmCardGeometry.BandThickness(tier, portrait);
        var enabledExif = _main.ControlVM.EnabledExifItems;
        var (exifWidth, exifHeight) = portrait
            ? ExifBlockGeometry.Size(enabledExif.Count, horizontal: true)
            : ExifBlockGeometry.Size(enabledExif.Count, horizontal: false, bare: true);
        var corners = _windowInsets.Corners;
        // SETTINGS-08 / TOOLBAR-04：手机可选命令组按用户勾选顺序跳过上下张后取前三，按钮 48dp。
        var commandSegments = BuildPhoneCommandSegments();
        PhoneLayoutInput Input(EdgeInsets lead, EdgeInsets control) => new(
            Landscape: !portrait,
            NotchOnLeft: _notchOnLeft,
            Width: widthDp,
            Height: heightDp,
            Safe: plain,
            LeadBand: lead,
            ControlBand: control,
            FilmVisible: _main.IsThumbnailViewVisible,
            ControlVisible: _main.IsControlViewVisible,
            ExifVisible: _main.IsExifViewVisible,
            AnalysisVisible: _main.IsAnalysisViewVisible,
            ShowNavigation: _main.Settings.ShowNavigationButtons,
            FilterWidth: button,
            FilmBand: filmBand,
            FilmOuterPad: FilmCardGeometry.CrossLead(tier, portrait),
            AnalysisCross: 0,
            ExifCount: enabledExif.Count,
            ExifWidth: exifWidth,
            ExifHeight: exifHeight,
            ExifItemSpacing: ExifBlockGeometry.ItemSpacing,
            RatingMinLength: RatingControlGeometry.CompactMinLength,
            ButtonSize: button,
            EdgeMargin: DesignTokens.ShellPhoneEdgeMargin,
            RowInset: rowInset,
            GroupGap: DesignTokens.ShellGlassGroupGap,
            ShowRating: _main.Settings.ShowRating,
            CommandCount: commandSegments.Count,
            CommandButtonSize: DesignTokens.ShellPhoneCommandSizeAndroid);

        // SHELL-10：系统安全区与屏幕圆角是两种独立避让。先只按安全区排出各贴角元素的实际位置，
        // 再按元素外形（44 高胶囊与圆按钮的半圆端）在该位置求圆角避让线，与安全区取较大者交回共享几何。
        var provisional = Input(plain, plain);
        var placed = PhoneScreenLayout.Compute(in provisional);
        double shape = button / 2;
        EdgeInsets lead, control;
        if (portrait)
        {
            double rowTop = Math.Max(0, placed.Filter.Y);
            lead = new EdgeInsets(
                Math.Max(plain.Left, AndroidWindowInsets.CornerInset(corners.TopLeft, rowTop, shape)),
                plain.Top,
                Math.Max(plain.Right, AndroidWindowInsets.CornerInset(corners.TopRight, rowTop, shape)), 0);
            var bottomRow = Has(placed.Commands) ? placed.Commands : placed.Rating;
            double rowBottom = Has(bottomRow)
                ? Math.Max(0, heightDp - bottomRow.MaxY)
                : Math.Max(plain.Bottom - rowInset, rowInset);
            control = new EdgeInsets(
                Math.Max(plain.Left, AndroidWindowInsets.CornerInset(corners.BottomLeft, rowBottom, shape)),
                0,
                Math.Max(plain.Right, AndroidWindowInsets.CornerInset(corners.BottomRight, rowBottom, shape)),
                plain.Bottom);
        }
        else
        {
            // 刘海侧列：按入口合体（上下张与它同一末端锚点、同宽）离刘海侧窗口缘的实际距离求上下缘。
            var group = placed.FilterMoreGroup;
            double leadEdge = Has(group)
                ? Math.Max(0, _notchOnLeft ? group.X : widthDp - group.MaxX)
                : 0;
            lead = new EdgeInsets(plain.Left,
                Math.Max(plain.Top, AndroidWindowInsets.CornerInset(
                    _notchOnLeft ? corners.TopLeft : corners.TopRight, leadEdge, shape)),
                plain.Right,
                Math.Max(plain.Bottom, AndroidWindowInsets.CornerInset(
                    _notchOnLeft ? corners.BottomLeft : corners.BottomRight, leadEdge, shape)));
            // 控制列：下巴侧系统对称内缩不参与落位；控制内容列（44 宽）距下巴侧窗口缘恒为一份工具区内边距。
            control = new EdgeInsets(0,
                Math.Max(plain.Top, AndroidWindowInsets.CornerInset(
                    _notchOnLeft ? corners.TopRight : corners.TopLeft, ToolRegionGeometry.Padding, shape)),
                0,
                Math.Max(plain.Bottom, AndroidWindowInsets.CornerInset(
                    _notchOnLeft ? corners.BottomRight : corners.BottomLeft, ToolRegionGeometry.Padding, shape)));
        }

        double analysisMain = portrait
            ? Math.Max(1, widthDp - plain.Left - plain.Right)
            : Math.Max(1, heightDp - plain.Top - plain.Bottom);
        double tile = AnalysisPanelGeometry.TileSize(analysisMain, Math.Max(1, _analysisPane.TileCount));
        double analysisCross = portrait
            ? tile + 2 * rowInset
            : ToolRegionGeometry.AnalysisWidth(tile, _main.IsControlViewVisible);
        var input = Input(lead, control) with { AnalysisCross = analysisCross };
        _phoneSlotsInput = input;
        var slots = PhoneScreenLayout.Compute(in input);
        slots = AndroidScreenForm.ApplyStatusBarTopRowShift(slots, input);
        _phoneSlots = slots;
        ApplyFitInsets(slots.PhotoAvoidance);

        // 宽形态专属层全部隐藏。
        _fileBar.Visibility = ViewStates.Gone;
        _fileDivider.Visibility = ViewStates.Gone;
        _controlBackground.Visibility = ViewStates.Gone;
        _controlDivider.Visibility = ViewStates.Gone;
        _controlScrollHost.Visibility = ViewStates.Gone;
#if PHOTOVIEWER_DINO
        _similarityPane.Visibility = ViewStates.Gone;
#endif

        ApplyBar(_phoneTopBar, slots.TopBar, density);
        ApplyBar(_phoneBottomBar, slots.BottomBar, density);
        ApplyBar(_phoneLeadingBar, slots.LeadingBar, density);
        ApplyBar(_phoneTrailingBar, slots.TrailingBar, density);

        bool filmVisible = _main.IsThumbnailViewVisible;
        _filmstrip.Visibility = filmVisible ? ViewStates.Visible : ViewStates.Gone;
        if (filmVisible)
        {
            LayoutChild(_filmstrip, slots.Filmstrip, density);
            bool axisChanged = _filmstrip.IsHorizontal != portrait;
            _filmstrip.SetAxis(portrait);
            _filmstrip.SetTier(tier);
            var padding = slots.FilmstripPadding;
            _filmstrip.SetEdgePadding(
                Math.Max(0, (portrait ? padding.Left : padding.Top) - FilmCardGeometry.MainLead(tier, portrait)),
                Math.Max(0, (portrait ? padding.Right : padding.Bottom) - FilmCardGeometry.MainTrail(tier, portrait)));
            if (axisChanged) _filmstrip.ScrollToCurrent();
        }

        bool analysisVisible = _main.IsAnalysisViewVisible;
        _analysisHost.SetVisible(analysisVisible);
        _analysisPane.Visibility = analysisVisible ? ViewStates.Visible : ViewStates.Gone;
        if (analysisVisible)
        {
            LayoutChild(_analysisPane, slots.Analysis, density);
            _analysisPane.Arrange(portrait, analysisMain, portrait ? plain.Left : plain.Top,
                portrait ? rowInset : null);
        }

        // 筛选：竖屏圆按钮由筛选控件自绘，更多为独立按钮；横屏 88 合体两段都由筛选控件绘制。
        _filterScrollHost.Visibility = Has(slots.Filter) ? ViewStates.Visible : ViewStates.Gone;
        if (Has(slots.Filter))
        {
            if (portrait)
            {
                _filterBar.ArrangePhone(true, new LayoutRect(0, 0, slots.Filter.Width, slots.Filter.Height), default);
                _filterScrollHost.Configure(true, (int)Math.Round(slots.Filter.Width * density),
                    (int)Math.Round(slots.Filter.Width * density));
                LayoutChild(_filterScrollHost, slots.Filter, density);
            }
            else
            {
                var group = slots.FilterMoreGroup;
                _filterBar.ArrangePhone(false,
                    new LayoutRect(slots.Filter.X - group.X, slots.Filter.Y - group.Y, slots.Filter.Width, slots.Filter.Height),
                    new LayoutRect(slots.More.X - group.X, slots.More.Y - group.Y, slots.More.Width, slots.More.Height));
                _filterScrollHost.Configure(true, (int)Math.Round(group.Width * density),
                    (int)Math.Round(group.Width * density));
                LayoutChild(_filterScrollHost, group, density);
            }
            // 横屏入口合体压在纵向胶片条上（SHELL-09）：列表从合体下方滚过，合体不能落在列表之下。
            _filterScrollHost.BringToFront();
        }

        ApplyButton(_phoneMoreButton, portrait ? slots.More : default, density);

        _phoneNavigation.Visibility = Has(slots.Navigation) ? ViewStates.Visible : ViewStates.Gone;
        if (Has(slots.Navigation)) LayoutChild(_phoneNavigation, slots.Navigation, density);

        _phoneCommands.SetVertical(!portrait);
        _phoneCommands.SetSegments(commandSegments);
        _phoneCommands.Visibility = Has(slots.Commands) ? ViewStates.Visible : ViewStates.Gone;
        if (Has(slots.Commands)) LayoutChild(_phoneCommands, slots.Commands, density);

        bool ratingShown = Has(slots.Rating) && _main.Settings.ShowRating;
        _phoneRating.Visibility = ratingShown ? ViewStates.Visible : ViewStates.Gone;
        if (ratingShown)
        {
            _phoneRating.SetOrientation(portrait);
            UpdateRatingControls();
            LayoutChild(_phoneRating, slots.Rating, density);
        }

        bool exifShown = Has(slots.Exif) && slots.ExifItems > 0;
        _phoneExif.Visibility = exifShown ? ViewStates.Visible : ViewStates.Gone;
        if (exifShown)
        {
            _phoneExif.SetContent(enabledExif, portrait, !portrait, slots.ExifItems);
            LayoutChild(_phoneExif, slots.Exif, density);
        }

        // 手机形态没有宽形态分区：把适应区与可见槽位放进同一结构，供浮层与验收读取。
        _panes = new AndroidPaneFrames(default, default, default,
            new LayoutRect(_fitInsets.Left, _fitInsets.Top,
                Math.Max(1, widthDp - _fitInsets.Left - _fitInsets.Right),
                Math.Max(1, heightDp - _fitInsets.Top - _fitInsets.Bottom)),
            slots.Analysis, default, default, default);
    }

    /// <summary>隐藏全部手机形态层（宽形态路径）。</summary>
    private void HidePhoneLayers()
    {
        _phoneTopBar.Visibility = ViewStates.Gone;
        _phoneBottomBar.Visibility = ViewStates.Gone;
        _phoneLeadingBar.Visibility = ViewStates.Gone;
        _phoneTrailingBar.Visibility = ViewStates.Gone;
        _phoneMoreButton.Visibility = ViewStates.Gone;
        _phoneNavigation.Visibility = ViewStates.Gone;
        _phoneCommands.Visibility = ViewStates.Gone;
        _phoneRating.Visibility = ViewStates.Gone;
        _phoneExif.Visibility = ViewStates.Gone;
    }

    /// <summary>刘海侧判定：优先用公开显示挖孔矩形；没有挖孔时按左右安全区比较，相等按在左。</summary>
    private bool ResolveNotchOnLeft(double widthDp)
    {
        if (_windowInsets.CutoutCenterX is { } center) return center < widthDp / 2;
        return _insets.Left >= _insets.Right;
    }

    /// <summary>把形态算出的适应区四边内边距交给主图视图。</summary>
    private void ApplyFitInsets(EdgeInsets insets)
    {
        var fit = new EdgeInsets(Math.Max(0, insets.Left), Math.Max(0, insets.Top),
            Math.Max(0, insets.Right), Math.Max(0, insets.Bottom));
        if (Math.Abs(fit.Left - _fitInsets.Left) < .01 && Math.Abs(fit.Top - _fitInsets.Top) < .01
            && Math.Abs(fit.Right - _fitInsets.Right) < .01 && Math.Abs(fit.Bottom - _fitInsets.Bottom) < .01)
            return;
        _fitInsets = fit;
        _photo.SetFitInsets(fit);
    }

    /// <summary>栏背景按槽位矩形落位；空矩形表示该栏不出现。</summary>
    private static void ApplyBar(View view, LayoutRect rect, double density)
    {
        bool visible = Has(rect);
        view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (visible) LayoutChild(view, rect, density);
    }

    /// <summary>手机入口按钮按槽位矩形落位；空矩形隐藏。</summary>
    private void ApplyButton(View view, LayoutRect rect, double density)
    {
        bool visible = Has(rect);
        view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (visible) LayoutChild(view, rect, density);
    }

    private static bool Has(LayoutRect rect) => rect.Width > 0 && rect.Height > 0;

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
            _photo.PresentImage(null, _session.Generation);
            UpdateHint();
            return;
        }
        RefreshControlBarHard();
        OnPhotoViewportChanged();
        Console.WriteLine($"[Native] 主图加载：{file.Name}（{file.File.StorageId.Value}）");
        _session.SetFile(file.File);
        _photo.PresentImage(_session.Image, _session.Generation);
        UpdateHint();
    }

    private void OnSurfaceReady()
    {
        _photoReady = true;
        if (_startPending || _main.CurrentFile != null) StartSession();
    }

    /// <summary>
    /// 主图真的出了一帧：让照片层自己失效，迫使下一次遍历执行
    /// <see cref="LayerBackdropLayout.DispatchDraw"/> 并重录子树，从而触发 <c>Recorded</c>；
    /// 材质失效仍统一由 <see cref="OnPhotoLayerRecorded"/> 按渲染代际门控。只由真实帧驱动，不引入定时重绘。
    /// </summary>
    private void OnPresentationFrameRendered()
    {
        if (_disposed) return;
        _photoLayer.Invalidate();
    }

    /// <summary>
    /// 照片层重录完成：只有主图真的换了一帧才让全部材质失效。切图、缩放拖动与表面重建后的新帧都会推进
    /// <see cref="AndroidPhotoView.RenderedFrames"/>，因此都能跟上；按代际去重则挡住「材质失效 → 材质重绘 →
    /// 再次遍历」的自触发重绘，不引入额外的连续重绘。
    /// </summary>
    private void OnPhotoLayerRecorded(object? sender, EventArgs e)
    {
        if (_disposed) return;
        int frames = _photo.RenderedFrames;
        if (frames == _materialFrames) return;
        _materialFrames = frames;
        _fileBar.Invalidate();
        _controlBackground.Invalidate();
        _phoneTopBar.Invalidate();
        _phoneBottomBar.Invalidate();
        _phoneLeadingBar.Invalidate();
        _phoneTrailingBar.Invalidate();
        _phoneExif.Invalidate();
        _controlBar.ExifBlock.Invalidate();
    }

    private void OnSceneLayerRecorded(object? sender, EventArgs e)
    {
        if (!_disposed) _glassLayer.Invalidate();
    }

    private void OnScreenLayerRecorded(object? sender, EventArgs e)
    {
        if (!_disposed) _popupLayer.InvalidateSurface();
    }

    private void OnPhotoViewportChanged()
    {
        long statsStart = AndroidFrameStats.Mark();
        try
        {
            if (_photo.Width < 1 || _photo.Height < 1) return;
            _session.UpdateViewport(_photo.Width, _photo.Height, _photo.NeedsFull || _main.IsAnalysisViewVisible);
            if (_main.Settings.ShowZoomIndicator) UpdateNavigationIndicator();
            UpdateDetailHighlight();
        }
        finally
        {
            AndroidFrameStats.Span(FrameStatsKind.Viewport, statsStart, AndroidFrameStats.Mark());
        }
    }

    private void OnSessionChanged()
    {
        long statsStart = AndroidFrameStats.Mark();
        try
        {
            // STATE-01：代际让视图区分「缓存命中切图」与「代理↔Full 热替换」，同一实例同代际只重绘。
            _photo.PresentImage(_session.Image, _session.Generation);
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
        finally
        {
            AndroidFrameStats.Span(FrameStatsKind.Session, statsStart, AndroidFrameStats.Mark());
        }
    }

    /// <summary>SCOPE-02：共享会话的一次性提示（如内存压力自动关闭增强）走 STATE-03 提示条。</summary>
    /// <param name="text">提示文本。</param>
    private void OnSessionNotice(string text) => ShowStatus(text, DesignTokens.MotionStatusToastDuration);

    /// <summary>主图单击：先清细节预览；只有首屏空态适应区内的点按才打开选择器（OPEN-01 / INPUT-10）。</summary>
    /// <param name="x">触点在本视图内的像素横坐标。</param>
    /// <param name="y">触点在本视图内的像素纵坐标。</param>
    private void OnPhotoTapped(float x, float y)
    {
        if (_analysisPane.DetailActive)
        {
            _analysisPane.ClearDetail();
            return;
        }
        if (_session.Image != null || _session.IsLoadingVisible || !string.IsNullOrWhiteSpace(_session.Error)) return;
        if (!_main.FolderVM.IsStartScreen || !IsInsideFitArea(x, y)) return;
        _ = _main.FolderVM.OpenFilePickerAsync();
    }

    /// <summary>触点是否落在实际主图适应区内（栏位与贴边控件都已在适应区外）。</summary>
    /// <param name="x">触点像素横坐标。</param>
    /// <param name="y">触点像素纵坐标。</param>
    private bool IsInsideFitArea(float x, float y)
    {
        var pane = FitArea;
        double density = AndroidUiTheme.Density(Context!);
        double pointX = x / density, pointY = y / density;
        return pointX >= pane.X && pointX <= pane.MaxX && pointY >= pane.Y && pointY <= pane.MaxY;
    }

    private void OnPhotoLongPressed(float x, float y) => ShowPhotoMenu(x, y);

    private bool OnWheelCommand(bool up, int metaState)
    {
        var gesture = AppGesture.FromMouse(new MouseGestureEx(up ? MouseAction.WheelUp : MouseAction.WheelDown,
            AndroidInputMapping.ModifiersOf(metaState)));
        string? command = InputCommandMap.CommandForGesture(gesture, _main.Settings);
        if (command == null || !InputCommandMap.IsCommandKnown(command)) return false;
        Execute(command);
        return true;
    }

    /// <summary>手机形态的「更多」按钮：在按钮下方打开与长按相同的主图菜单（MENU-08）。</summary>
    /// <param name="anchor">锚点视图。</param>
    internal void ShowPhotoMenuAt(View anchor)
    {
        var location = new int[2];
        anchor.GetLocationOnScreen(location);
        var photoLocation = new int[2];
        _photo.GetLocationOnScreen(photoLocation);
        ShowPhotoMenu(location[0] - photoLocation[0] + anchor.Width / 2f,
            location[1] - photoLocation[1] + anchor.Height, anchor);
    }

    /// <summary>主图长按菜单：在手指处弹出自绘面板；面板整体夹在屏幕内，超出高度时可滚动。</summary>
    /// <param name="localX">触点在本视图内的像素横坐标。</param>
    /// <param name="localY">触点在本视图内的像素纵坐标。</param>
    /// <param name="entry">关闭后归还焦点的入口（手机「更多」按钮；长按为空则回到主图）。</param>
    internal void ShowPhotoMenu(float localX, float localY, View? entry = null)
    {
        if (Context is not Activity activity) return;
        DismissPhotoMenu();
        CloseRatingPopup();
        _filterBar.DismissDropdown();
        _photoMenuItems.Clear();
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
        var state = new PhotoMenuState(
            _main.CurrentFile != null, _main.IsThumbnailViewVisible, _main.IsAnalysisViewVisible,
            _main.IsControlViewVisible, _session.IsEnhanced, _main.IsExifViewVisible);
        // MENU-01：触屏没有「打开文件…」；EXIF 开关只在手机形态出现。
        var menuItems = PhotoMenuCatalog.Build(state, includeOpenFile: false, includeExif: _form != ScreenForm.Wide);
        int groupCount = 1;
        for (int index = 1; index < menuItems.Count; index++)
            if (menuItems[index].Group != menuItems[index - 1].Group) groupCount++;
        int contentHeight = itemHeight * menuItems.Count + dividerHeight * (groupCount - 1) + padding * 2;
        int maxHeight = Math.Max(itemHeight * 2, screenHeight - (int)Math.Round(16 * density));
        int menuHeight = Math.Min(contentHeight, maxHeight);

        var content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        content.SetPadding(padding, padding, padding, padding);

        void AddDivider()
        {
            var divider = new View(activity);
            divider.SetBackgroundColor(AndroidUiTheme.Divider);
            content.AddView(divider, new LinearLayout.LayoutParams(LayoutParams.MatchParent, dividerHeight));
        }

        void AddItem(string label, bool? check, PhotoMenuItemId itemId, Action action, bool enabled = true)
        {
            // 验收快照：只记录本次构建出的项（含实际绘制的图标字形），不参与布局、命中或可点击性判断。
            string iconGlyph = AndroidIcons.ForMenu(itemId);
            _photoMenuItems.Add((label, check == true, enabled, iconGlyph));
            var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding((int)Math.Round(12 * density), 0, (int)Math.Round(10 * density), 0);
            row.Clickable = enabled;
            row.Focusable = enabled;
            row.Enabled = enabled;
            row.ContentDescription = check == true ? $"{label}，已勾选" : label;
            // MENU-03：勾选列在行首，命令图标紧随其后，文本占剩余宽度；勾号与图标都不发声，读屏只念整行名称。
            var mark = new TextView(activity)
            {
                Text = check == true ? AndroidIcons.Check : "",
                Gravity = GravityFlags.Center,
            };
            mark.SetTextSize(global::Android.Util.ComplexUnitType.Dip, 15);
            mark.SetTypeface(AndroidUiTheme.IconTypeface(activity), TypefaceStyle.Normal);
            mark.SetTextColor(enabled ? AndroidUiTheme.Accent : AndroidUiTheme.TextSecondary);
            mark.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(mark, new LinearLayout.LayoutParams((int)Math.Round(24 * density), LayoutParams.MatchParent));
            // MENU-03/MENU-06：命令图标走纯绘制图标槽，按排版框中心落在图标槽令牌内，不参与文本排版，
            // 因此不会被字体 advance 推宽、换行或静默缩小；槽位与间距仍是行几何令牌。
            var icon = new AndroidGlyphView(activity, iconGlyph,
                DesignTokens.MenuIconFontSize,
                enabled ? AndroidUiTheme.TextPrimary : AndroidUiTheme.TextSecondary,
                DesignTokens.MenuIconSize);
            icon.Enabled = enabled;
            var iconParams = new LinearLayout.LayoutParams(
                (int)Math.Round(DesignTokens.MenuIconSize * density), LayoutParams.MatchParent)
            {
                RightMargin = (int)Math.Round(DesignTokens.MenuIconGap * density),
            };
            row.AddView(icon, iconParams);
            var text = new TextView(activity)
            {
                Text = label,
                TextSize = 15,
            };
            text.SetTextColor(enabled ? AndroidUiTheme.TextPrimary : AndroidUiTheme.TextSecondary);
            text.Gravity = GravityFlags.CenterVertical;
            text.Enabled = enabled;
            row.AddView(text, new LinearLayout.LayoutParams(0, LayoutParams.MatchParent) { Weight = 1 });
            row.Click += (_, _) =>
            {
                if (!enabled) return;
                DismissPhotoMenu();
                action();
            };
            content.AddView(row, new LinearLayout.LayoutParams(LayoutParams.MatchParent, itemHeight));
        }

        // MENU-01/MENU-06：共享目录给出固定文案、分组、勾选与可用性；这里只落原生行、图标与执行入口。
        int group = -1;
        foreach (var item in menuItems)
        {
            if (item.Group != group)
            {
                if (group >= 0) AddDivider();
                group = item.Group;
            }
            Action action = item.Id switch
            {
                PhotoMenuItemId.OpenFile or PhotoMenuItemId.OpenFolder => () => _ = _main.FolderVM.OpenFilePickerAsync(),
                PhotoMenuItemId.Filmstrip => () => _main.ToggleThumbnailView(),
                PhotoMenuItemId.Analysis => () => _main.ToggleAnalysisView(),
                PhotoMenuItemId.ControlBar => () => _main.ToggleControlView(),
                PhotoMenuItemId.Exif => () => _main.ToggleExifView(),
                PhotoMenuItemId.Enhance => () => _session.ToggleEnhanced(),
                PhotoMenuItemId.Tools => ShowTools,
                _ => () => _main.OpenSettingWindow(),
            };
            AddItem(item.Text, item.IsCheck ? item.Checked : null, item.Id, action, item.Enabled);
        }

        View popupContent = content;
        if (contentHeight > maxHeight)
        {
            var scroll = new ScrollView(activity) { VerticalScrollBarEnabled = true };
            scroll.AddView(content);
            popupContent = scroll;
        }

        int margin = (int)Math.Round(8 * density);
        int x = Math.Clamp(touchX, margin, Math.Max(margin, screenWidth - menuWidth - margin));
        int y = Math.Clamp(touchY, margin, Math.Max(margin, screenHeight - menuHeight - margin));
        var screen = new Rect(x, y, x + menuWidth, y + menuHeight);
        AndroidGlassPopupSession? opened = null;
        opened = _popupLayer.Show(new AndroidGlassPopupRequest(popupContent, _popupLayer.ToWindow(screen),
            8, AndroidUiTheme.ControlFill, AndroidUiTheme.ControlBorder, 1, "菜单", entry ?? _photo,
            () => { if (ReferenceEquals(_photoMenu, opened)) _photoMenu = null; }));
        _photoMenu = opened;
    }

    /// <summary>Activity 恢复（SAF 返回）后恢复栏位内容与主图：选择器期间可能丢失一帧/一次重建。</summary>
    internal void RefreshAfterResume()
    {
        CloseRatingPopup();
        RefreshControlBarHard();
        _filterBar.Refresh();
        UpdateHint();
        RequestLayout();
        _photo.Post(_photo.Draw);
    }

    /// <summary>关闭筛选下拉（返回键优先消费）。</summary>
    /// <returns>关闭了返回 true。</returns>
    internal bool DismissFilterDropdown() => _filterBar.DismissDropdown();

    /// <summary>关闭星级弹窗（返回键优先消费，RATING-04）。</summary>
    /// <returns>关闭了弹窗返回 true。</returns>
    internal bool DismissRatingPopup()
    {
        if (!_ratingPopup.IsOpen) return false;
        _ratingPopup.Close();
        return true;
    }

    /// <summary>验收用：按当前形态经真实评分入口打开星级弹窗（产品路径不调用）。</summary>
    internal void OpenRatingPopupForChecks()
    {
        if (_form == ScreenForm.Wide) OpenRatingPopupFromControlBar();
        else OpenRatingPopupFromPhone();
    }

    /// <summary>
    /// 控制栏星级控件：紧凑态起手即由控件请求打开弹窗（展开态只有冲突三角走这里），
    /// 展开方向由形态、行列与刘海侧决定，锚点取控件真实屏幕矩形。
    /// </summary>
    private void OpenRatingPopupFromControlBar()
        => OpenRatingPopup(_main.CurrentFile, RatingPopupAnchor.Control, _controlBar.RatingControl);

    /// <summary>手机形态星级控件的弹窗入口（锚点同为控件真实屏幕矩形）。</summary>
    private void OpenRatingPopupFromPhone()
        => OpenRatingPopup(_main.CurrentFile, RatingPopupAnchor.Control, _phoneRating);

    private void OpenRatingPopup(ImageFile? file, RatingPopupAnchor anchorKind, AndroidRatingControl? control)
    {
        // STATE-05：目录不可用期间评分写入已冻结，不再打开只能失败的评分入口。
        if (file == null || control == null || _folderUnavailable) return;
        var direction = RatingControlGeometry.PopupDirection(_form, _main.IsRowLayout, _notchOnLeft, anchorKind);
        _ratingPopup.Show(file, anchorKind, direction, control.Horizontal, control, () => _main.Settings.ShowRating && ReferenceEquals(_main.CurrentFile, file));
    }

    /// <summary>卡片冲突三角：打开同一个弹窗，照片有冲突时自动为来源条目形态。</summary>
    /// <param name="card">被点的卡片。</param>
    void ICardRatingPopupHost.OpenCardRatingPopup(AndroidThumbnailCard card) => OpenRatingPopupForCard(card);

    /// <summary>卡片长按：起手打开弹窗并把同一次触摸交给它继续跟踪。</summary>
    /// <param name="card">发起长按的卡片。</param>
    /// <param name="e">卡片收到的触摸事件。</param>
    /// <param name="phase">手势阶段。</param>
    void ICardRatingPopupHost.TrackCardRatingPopup(AndroidThumbnailCard card, MotionEvent e, CardRatingPopupPhase phase)
    {
        switch (phase)
        {
            case CardRatingPopupPhase.Began:
                OpenRatingPopupForCard(card);
                _ratingPopup.BeginExternal(card, e);
                break;
            case CardRatingPopupPhase.Moved:
                _ratingPopup.MoveExternal(card, e);
                break;
            case CardRatingPopupPhase.Ended:
                _ratingPopup.EndExternal(card, e);
                break;
            case CardRatingPopupPhase.Cancelled:
                _ratingPopup.CancelExternal(card, e);
                break;
        }
    }

    private void OpenRatingPopupForCard(AndroidThumbnailCard card)
    {
        if (card.BoundFile is not { } file || _folderUnavailable) return;
        var direction = RatingControlGeometry.PopupDirection(_form, _main.IsRowLayout, _notchOnLeft,
            RatingPopupAnchor.Card);
        _ratingPopup.Show(file, RatingPopupAnchor.Card, direction, true, card, () => _main.Settings.ShowRating && ReferenceEquals(card.BoundFile, file));
    }

    /// <summary>收起星级弹窗（切图、栏位隐藏、菜单/下拉/设置工具打开、列表替换等入口统一调用）。</summary>
    private void CloseRatingPopup()
    {
        if (_ratingPopup.IsOpen) _ratingPopup.Close();
    }

    /// <summary>关闭主图长按菜单（返回键优先消费）。</summary>
    /// <returns>关闭了菜单返回 true；淡出中的旧面板不算打开。</returns>
    internal bool DismissPhotoMenu()
    {
        var session = _photoMenu;
        if (session == null) return false;
        _photoMenu = null;
        return _popupLayer.Dismiss(session);
    }

    /// <summary>打开/前置原生工具页（RATING-04：先收起星级弹窗）。</summary>
    internal void ShowTools()
    {
        CloseRatingPopup();
        _main.OpenToolsWindow();
    }

    /// <summary>控制栏 EXIF 整组入口：同一工具页内明确进入 EXIF 详情（TOOLBAR-06）。</summary>
    internal void ShowExifTools()
    {
        CloseRatingPopup();
        _main.OpenExifToolsWindow();
    }

    /// <summary>
    /// SETTINGS-08 / TOOLBAR-04：手机可选命令组按实际勾选顺序跳过上下张后取前三；
    /// 字形、读屏名称与可用性沿用控制栏同一命令语义，点按直接执行原命令。
    /// </summary>
    /// <returns>按生效顺序排列的胶囊段。</returns>
    private List<GlassComboSegment> BuildPhoneCommandSegments()
    {
        var enabled = _main.ControlVM.EnabledControls.ToList();
        var commands = PhoneControlCommands.Resolve(
            enabled.Select(item => item.Command), _main.Settings.ShowNavigationButtons);
        var segments = new List<GlassComboSegment>(commands.Length);
        foreach (var command in commands)
        {
            var hotkey = enabled.Find(item => item.Command == command);
            if (hotkey != null) segments.Add(AndroidControlBar.CommandSegment(_main, hotkey));
        }
        return segments;
    }

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
        UpdateRatingControls();
        RequestLayout();
    }

    /// <summary>当前照片的星级与冲突态；STATE-05 不可用期间评分输入禁用（写入由共享层拦截）。</summary>
    private void UpdateRatingControls()
        => _phoneRating.SetState(_main.CurrentFile?.Rating ?? 0,
            _main.CurrentFile?.HasRatingConflict == true, _main.CurrentFile != null && !_folderUnavailable);

    private void OnRateRequested(ImageFile file, int rating) => _ = RateAsync(file, rating);

    private void OnRatingWriteFailed(RatingWriteFailure failure)
        => ShowStatus(StatusMessages.RatingWriteFailed(
            failure.File.Name, failure.Reason, failure.IsPartial), DesignTokens.MotionStatusToastDuration);

    /// <summary>STATE-02：共享层上报的打开失败统一用提示条呈现，不弹模态。</summary>
    /// <param name="message">失败提示正文。</param>
    private void OnOpenFailed(string message)
        => ShowStatus(message, DesignTokens.MotionStatusToastDuration);

    /// <summary>STATE-05：共享层判定当前目录不可用后，切换持续状态并把访问冻结同步给加载会话。</summary>
    private void OnFolderAvailabilityChanged() => ApplyFolderAvailability(_main.FolderVM.IsFolderUnavailable);

    /// <summary>
    /// 应用当前目录可用性：显示/收起持续状态，暂停需要访问该目录的解码，
    /// 并收起属于上一状态且不可写的星级弹窗；不抢焦点、不弹模态。
    /// </summary>
    /// <param name="unavailable">目录是否不可用。</param>
    private void ApplyFolderAvailability(bool unavailable)
    {
        _folderUnavailable = unavailable;
        _session.SetStorageUnavailable(unavailable);
        _folderStatus.Visibility = unavailable ? ViewStates.Visible : ViewStates.Gone;
        if (unavailable) CloseRatingPopup();
        // 恢复可用后评分输入与命令可用态同样要回到正常，不只是不可用方向。
        _controlBar.MarkDirty();
        UpdateRatingControls();
        RequestLayout();
    }

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
                // RATING-04：浮窗属于上一张照片的临时视图，切图即关闭。
                CloseRatingPopup();
                _analysisPane.ClearDetail();
                _filmstrip.CancelScrollUpToNow();
                _controlBar.MarkDirty();
                UpdateRatingControls();
                StartSession();
                RequestLayout();
                break;
            case nameof(MainViewModel.IsRowLayout):
            case nameof(MainViewModel.IsScreenLandscape):
                // 旋转后筛选条锚点整体失效，下拉不能跟着旧坐标留在屏幕上。
                _filterBar.DismissDropdown();
                CloseRatingPopup();
                RequestLayout();
                break;
            case nameof(MainViewModel.IsThumbnailViewVisible):
                AndroidShellPreferences.SaveShowFilm(_main.IsThumbnailViewVisible);
                if (!_main.IsThumbnailViewVisible) CloseRatingPopup();
                RequestLayout();
                break;
            case nameof(MainViewModel.IsControlViewVisible):
                AndroidShellPreferences.SaveShowControls(_main.IsControlViewVisible);
                if (!_main.IsControlViewVisible) CloseRatingPopup();
                RequestLayout();
                break;
            case nameof(MainViewModel.IsExifViewVisible):
                // SHELL-04：手机形态 EXIF 开关与栏位显隐同一平台偏好存储，不进设置 JSON。
                AndroidShellPreferences.SaveShowExif(_main.IsExifViewVisible);
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

    /// <summary>
    /// 共享设置按“呈现影响”上报（<see cref="SettingsChangeImpact"/>）：只刷新该位集合真正涉及的部件，
    /// 不再对任意属性整条硬刷。诊断/持久化等不改变呈现的属性根本不会到达这里。
    /// </summary>
    /// <param name="impact">本次设置的呈现影响位集合。</param>
    private void OnSettingsPresentationChanged(SettingsChangeImpact impact) => ApplySettingsImpact(impact);

    private void ApplySettingsImpact(SettingsChangeImpact impact)
    {
        if (_disposed || impact == SettingsChangeImpact.None) return;
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => ApplySettingsImpact(impact));
            return;
        }
        if ((impact & SettingsChangeImpact.Analysis) != 0) _analysisHost.Raise();
        if ((impact & SettingsChangeImpact.Navigator) != 0) UpdateNavigationIndicator();
#if PHOTOVIEWER_DINO
        if ((impact & SettingsChangeImpact.Similarity) != 0) _similarityPane.Refresh();
#endif
        if ((impact & SettingsChangeImpact.FileProjection) != 0)
        {
            _filterBar.Refresh();
            UpdateHint();
        }
        var control = impact & (SettingsChangeImpact.Commands | SettingsChangeImpact.Exif
            | SettingsChangeImpact.Rating | SettingsChangeImpact.InputBindings);
        if (control != 0)
        {
            // 命令/EXIF 项集合与评分组显隐会改分组或长度，需要重新量测；绑定文本只重绘。
            bool geometry = (control & (SettingsChangeImpact.Commands | SettingsChangeImpact.Exif
                | SettingsChangeImpact.Rating)) != 0;
            RefreshControlBarContent(geometry);
        }
        if ((impact & SettingsChangeImpact.Rating) != 0)
        {
            // RATING-04：入口几何/显隐变化由卡片自身重绘，不据此收起星级弹窗。
            _filmstrip.RefreshCardStates();
            UpdateRatingControls();
#if PHOTOVIEWER_DINO
            _similarityPane.RefreshCardStates();
#endif
        }
        if ((impact & (SettingsChangeImpact.Layout | SettingsChangeImpact.FileProjection
            | SettingsChangeImpact.Analysis | SettingsChangeImpact.Similarity)) != 0)
            RequestLayout();
    }

    /// <summary>控制栏内容刷新：重建分组并重绘；只有分组集合/长度变了才重新布局。</summary>
    /// <param name="geometry">是否影响分组几何（命令/EXIF 项、评分组显隐）。</param>
    private void RefreshControlBarContent(bool geometry)
    {
        if (_disposed) return;
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => RefreshControlBarContent(geometry));
            return;
        }
        _controlBar.MarkDirty();
        _controlBar.Invalidate();
        if (geometry) RequestLayout();
    }

    /// <summary>控制栏立即重建并在一帧后再补一次（文件/选择器返回等异步变更都走这里）。</summary>
    private void RefreshControlBarHard()
    {
        _controlBar.MarkDirty();
        _controlBar.Invalidate();
        RequestLayout();
        PostDelayed(() => { _controlBar.MarkDirty(); _controlBar.Invalidate(); RequestLayout(); }, 120);
    }

    /// <summary>
    /// 控制栏只按真实依赖刷新：EXIF 项/命令集合与评分组显隐改几何，星级透明度/写回模式只重绘。
    /// 其余属性（例如当前照片的 EXIF 值）由对应来源另行通知，不再触发整条硬刷。
    /// </summary>
    private void OnControlChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ControlViewModel.EnabledExifItems):
            case nameof(ControlViewModel.EnabledControls):
            case nameof(ControlViewModel.ShowRating):
                RefreshControlBarContent(geometry: true);
                break;
            case nameof(ControlViewModel.StarOpacity):
            case nameof(ControlViewModel.SafeSetRating):
                RefreshControlBarContent(geometry: false);
                break;
        }
    }

    /// <summary>筛选条滚动后已打开下拉的锚点失效，收起（未打开时无动作）。</summary>
    private void OnFilterScrollChanged()
    {
        if (_filterBar.IsDropdownOpen) _filterBar.DismissDropdown();
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        _filterBar.Refresh();
        RequestLayout();
    }

    private void OnFilesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ThumbnailListViewModel.FilteredFiles)) return;
        // RATING-04：列表替换会重新绑定卡片，静态锚点不再指向同一张照片。
        CloseRatingPopup();
        _filmstrip.ReloadData();
        _filterBar.Refresh();
        RefreshControlBarHard();
        UpdateHint();
        RequestLayout();
    }

    /// <summary>OPEN-01：选择器或文件源打开开始/结束时首屏空态切换，提示随之显示或撤下。</summary>
    private void OnOpenStateChanged()
    {
        UpdateHint();
        RequestLayout();
    }

    private void OnAllFilesChanged()
    {
        _filterBar.Refresh();
        _controlBar.MarkDirty();
        _controlBar.Invalidate();
        // 后台星级/来源补齐只改字段、不逐项发通知：卡片按当前模型重绘，冲突三角才会即时出现。
        _filmstrip.RefreshCardStates();
#if PHOTOVIEWER_DINO
        _similarityPane.RefreshCardStates();
#endif
        UpdateHint();
    }

#if PHOTOVIEWER_DINO
    /// <summary>相似项集合/动作区变化后重排主屏（面板宽度随卡片数变化）。</summary>
    private void OnSimilarityPanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        _similarityPane.Refresh();
        if (args.PropertyName is nameof(SimilarityPanelViewModel.SimilarItems)
            or nameof(SimilarityPanelViewModel.HasItems)
            or nameof(SimilarityPanelViewModel.ShowActionArea)
            or nameof(SimilarityPanelViewModel.IsStateIndexing)
            or nameof(SimilarityPanelViewModel.UnindexedCount))
        {
            // RATING-04：相似列表重排/替换后锚点卡片不再指向原项。
            if (args.PropertyName is nameof(SimilarityPanelViewModel.SimilarItems) or nameof(SimilarityPanelViewModel.HasItems))
                CloseRatingPopup();
            RequestLayout();
        }
    }
#endif

    private void OnScrollToCurrentRequested() => _filmstrip.ScrollToCurrent();

    private void OnCurrentFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ImageFile.ExifData) or nameof(ImageFile.Rating)
            or nameof(ImageFile.HasRatingConflict) or nameof(ImageFile.RatingSources))
        {
            _controlBar.MarkDirty();
            _controlBar.Invalidate();
            UpdateRatingControls();
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

    /// <summary>细节预览有活动目标时在主图上画绿色方框：中心取瓦片中心，大小取对焦框或 1:1 裁剪方块在当前视口下的投影。</summary>
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
        if (!_detailVisible) return;
        _detailVisible = false;
        _detailHighlight.Visibility = ViewStates.Gone;
        Invalidate();
    }

    /// <summary>绿框画在根视图最上层：主图铺满窗口，坐标与主图视图一致。</summary>
    protected override void DispatchDraw(Canvas canvas)
    {
        base.DispatchDraw(canvas);
        if (!_detailVisible) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        var rect = new RectF(
            (float)(_detailRect.X * density),
            (float)(_detailRect.Y * density),
            (float)(_detailRect.MaxX * density),
            (float)(_detailRect.MaxY * density));
        using var paint = AndroidUiTheme.Paint(Color.Rgb(0, 255, 0)!, Math.Max(2, 3 * density));
        paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
        canvas.DrawRect(rect, paint);
    }

    private void UpdateHint()
    {
        bool hasImage = _session.Image != null;
        bool hasFile = _main.CurrentFile != null;
        // STATE-01 / OPEN-01：首屏空态只在文件源处于 OPEN-01 时显示；失败与延迟加载态照旧显示。
        var (title, detail) = PhotoHintText.Resolve(
            hasFile, _session.Error, _session.IsLoadingVisible, touch: true, showEmpty: _main.FolderVM.IsStartScreen);
        bool show = !hasImage && title.Length > 0;
        _hintLabel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        bool hasDetail = show && detail.Length > 0;
        _hintDetail.Visibility = hasDetail ? ViewStates.Visible : ViewStates.Gone;
        if (!show) return;
        _hintLabel.Text = title;
        // 只有首屏空态把副标题拆成三段并在「/」两侧留布局空白；失败/加载提示保持纯文本。
        bool empty = title == PhotoHintText.EmptyProductTitle;
        if (empty)
        {
            int gapPx = AndroidUiTheme.DpPx(this, DesignTokens.ImageHintSeparatorGap);
            _hintDetail.TextFormatted = EmptyDetailFormatted(gapPx);
        }
        else
        {
            _hintDetail.Text = detail;
        }
    }

    /// <summary>触屏空态副标题：分隔字取三级文字色，两侧各留一份布局空白（不在文案中插入空格）。</summary>
    /// <param name="gapPx">分隔字单侧空白（像素）。</param>
    private static SpannableStringBuilder EmptyDetailFormatted(int gapPx)
    {
        var parts = PhotoHintText.EmptyDetailParts(touch: true);
        var builder = new SpannableStringBuilder();
        builder.Append(parts.Prefix);
        AppendHintGap(builder, gapPx);
        int separatorStart = builder.Length();
        builder.Append(parts.Separator);
        builder.SetSpan(new ForegroundColorSpan(AndroidUiTheme.TextTertiary),
            separatorStart, builder.Length(), SpanTypes.ExclusiveExclusive);
        AppendHintGap(builder, gapPx);
        builder.Append(parts.Suffix);
        return builder;
    }

    /// <summary>以固定宽度占位符实现横向留白：字符本身不绘制，宽度由 <see cref="GapSpan"/> 给出。</summary>
    /// <param name="builder">目标文本。</param>
    /// <param name="gapPx">留白宽度（像素）。</param>
    private static void AppendHintGap(SpannableStringBuilder builder, int gapPx)
    {
        int start = builder.Length();
        builder.Append('\u200B');
        builder.SetSpan(new GapSpan(gapPx), start, start + 1, SpanTypes.ExclusiveExclusive);
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

    /// <summary>摆好空态提示、增强提示、状态提示与导航图；提示都落在主图适应区内。</summary>
    private void LayoutOverlays(double density)
    {
        var pane = FitArea;
        double bottomInset = 0;
        // STATE-01：两行提示左右至少留一份 inset，行盒高度与行间距取令牌，整块在适应区内垂直居中。
        int hintWidthPx = Math.Max(1, (int)Math.Round(
            Math.Max(1, Math.Min(DesignTokens.ImageHintMaxWidth, pane.Width - 2 * DesignTokens.ImageHintInset)) * density));
        int hintMaxHeightPx = Math.Max(1, (int)Math.Round(pane.Height * density));
        _hintLabel.Measure(MeasureSpec.MakeMeasureSpec(hintWidthPx, MeasureSpecMode.AtMost),
            MeasureSpec.MakeMeasureSpec(hintMaxHeightPx, MeasureSpecMode.AtMost));
        double hintWidth = hintWidthPx / density;
        double labelHeight = Math.Max(DesignTokens.ImageHintTitleLineHeight, _hintLabel.MeasuredHeight / density);
        bool hasDetail = _hintDetail.Visibility == ViewStates.Visible;
        double detailHeight = 0;
        if (hasDetail)
        {
            _hintDetail.Measure(MeasureSpec.MakeMeasureSpec(hintWidthPx, MeasureSpecMode.AtMost),
                MeasureSpec.MakeMeasureSpec(hintMaxHeightPx, MeasureSpecMode.AtMost));
            detailHeight = Math.Max(DesignTokens.ImageHintDetailLineHeight, _hintDetail.MeasuredHeight / density);
        }
        double blockHeight = hasDetail ? labelHeight + DesignTokens.ImageHintRowGap + detailHeight : labelHeight;
        double hintLeft = pane.X + (pane.Width - hintWidth) / 2;
        double hintTop = pane.Y + Math.Max(0, (pane.Height - blockHeight) / 2);
        LayoutChild(_hintLabel, new LayoutRect(hintLeft, hintTop, hintWidth, labelHeight), density);
        if (hasDetail)
        {
            LayoutChild(_hintDetail,
                new LayoutRect(hintLeft, hintTop + labelHeight + DesignTokens.ImageHintRowGap, hintWidth, detailHeight),
                density);
        }

        LayoutChild(_enhanceBusy, new LayoutRect(
            pane.X + (pane.Width - DesignTokens.ImageBusyWidth) / 2,
            pane.Y + pane.Height - DesignTokens.ImageBusyHeight - DesignTokens.ImageBusyBottomOffset - bottomInset,
            DesignTokens.ImageBusyWidth, DesignTokens.ImageBusyHeight), density);

        // STATE-03：单行或自动换行，最大宽 min(500, 宽−40)，高按实际文本量出。
        double statusMaxWidth = Math.Max(1, Math.Min(DesignTokens.StatusToastMaxWidth, pane.Width - 40));
        int statusMaxWidthPx = (int)Math.Round(statusMaxWidth * density);
        int paneHeightPx = Math.Max(1, (int)Math.Round(pane.Height * density));
        // STATE-03：药丸可见时提示条整体上移到药丸上方，药丸收起后回到距底 24。
        double statusBottom = _enhanceBusy.Visibility == ViewStates.Visible
            ? DesignTokens.ImageBusyBottomOffset + DesignTokens.ImageBusyHeight + DesignTokens.StatusToastStackGap
            : DesignTokens.StatusToastBottomMargin;

        // STATE-05：持续状态占最底一层，短提示叠放在它之上；两者都不挡周围手势。
        double folderStatusHeight = 0;
        if (_folderStatus.Visibility == ViewStates.Visible)
        {
            _folderStatus.Measure(
                MeasureSpec.MakeMeasureSpec(statusMaxWidthPx, MeasureSpecMode.AtMost),
                MeasureSpec.MakeMeasureSpec(paneHeightPx, MeasureSpecMode.AtMost));
            double folderStatusWidth = _folderStatus.MeasuredWidth / density;
            folderStatusHeight = _folderStatus.MeasuredHeight / density;
            _folderStatusRect = new LayoutRect(
                pane.X + (pane.Width - folderStatusWidth) / 2,
                pane.Y + pane.Height - folderStatusHeight - statusBottom - bottomInset,
                folderStatusWidth, folderStatusHeight);
            LayoutChild(_folderStatus, _folderStatusRect, density);
            statusBottom += folderStatusHeight + DesignTokens.StatusToastStackGap;
        }
        else
        {
            _folderStatusRect = default;
        }

        _statusLabel.Measure(
            MeasureSpec.MakeMeasureSpec(statusMaxWidthPx, MeasureSpecMode.AtMost),
            MeasureSpec.MakeMeasureSpec(paneHeightPx, MeasureSpecMode.AtMost));
        double statusWidth = _statusLabel.MeasuredWidth / density;
        double statusHeight = _statusLabel.MeasuredHeight / density;
        _statusRect = new LayoutRect(
            pane.X + (pane.Width - statusWidth) / 2,
            pane.Y + pane.Height - statusHeight - statusBottom - bottomInset,
            statusWidth, statusHeight);
        LayoutChild(_statusLabel, _statusRect, density);

        bool show = _main.Settings.ShowZoomIndicator && !_photo.IsFit && _session.Image != null;
        _navigation.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (show)
        {
            var (navWidth, navHeight) = _photo.NavigationSize;
            var (hostWidth, hostHeight) = AndroidNavigationOverlay.HostSizeFor(navWidth, navHeight);
            var rect = new LayoutRect(
                pane.MaxX - hostWidth - DesignTokens.NavMargin,
                pane.MaxY - hostHeight - DesignTokens.NavMargin,
                hostWidth, hostHeight);
            LayoutChild(_navigation, rect, density);
            _navigation.SetState(null, navWidth, navHeight, _photo.NavigationViewport,
                $"{_photo.PhysicalScale * 100:0.#}%");
        }
    }

    /// <summary>主图适应区（dp）：由主图当前尺寸与视口适应区内边距直接算出，早于视口就绪也可用。</summary>
    internal LayoutRect FitArea
    {
        get
        {
            double density = AndroidUiTheme.Density(Context!);
            double width = _photo.Width / density, height = _photo.Height / density;
            var insets = _photo.Viewport.FitInsets;
            return new LayoutRect(insets.Left, insets.Top,
                Math.Max(1, width - insets.Left - insets.Right),
                Math.Max(1, height - insets.Top - insets.Bottom));
        }
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
            _photo.PresentationFrameRendered -= OnPresentationFrameRendered;
            _photo.ViewportChanged -= OnPhotoViewportChanged;
            _session.Changed -= OnSessionChanged;
            _session.Notice -= OnSessionNotice;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Settings.PresentationChanged -= OnSettingsPresentationChanged;
            _main.ControlVM.PropertyChanged -= OnControlChanged;
            _main.FileVM.ThumbnailList.PropertyChanged -= OnFilesChanged;
            _main.FileVM.ThumbnailList.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            _main.FileVM.FilterBar.PropertyChanged -= OnFilterChanged;
            _main.FolderVM.AllFilesChanged -= OnAllFilesChanged;
            _main.FolderVM.OpenStateChanged -= OnOpenStateChanged;
            _main.FolderVM.FolderAvailabilityChanged -= OnFolderAvailabilityChanged;
#if PHOTOVIEWER_DINO
            _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
#endif
            _main.RatingWriteFailedDetailed -= OnRatingWriteFailed;
            _main.OpenFailed -= OnOpenFailed;
            if (_observedFile != null) _observedFile.PropertyChanged -= OnCurrentFileChanged;
            _filmstrip.RateRequested -= OnRateRequested;
            _filmstrip.PopupHost = null;
            _filterBar.DropdownOpening -= CloseRatingPopup;
            _filterScrollHost.ScrollChanged -= OnFilterScrollChanged;
#if PHOTOVIEWER_DINO
            _similarityPane.PopupHost = null;
#endif
            _ratingPopup.Dispose();
            _sceneLayer.Recorded -= OnSceneLayerRecorded;
            _screenLayer.Recorded -= OnScreenLayerRecorded;
            _popupLayer.Backdrop = null;
            _popupLayer.Dispose();
            _analysisPane.TileCountChanged -= RequestLayout;
            _analysisPane.DetailChanged -= UpdateDetailHighlight;
            _analysisPresenter.Dispose();
            _photoLayer.Recorded -= OnPhotoLayerRecorded;
            _photo.AdditionalFootprint = null;
            _glassLayer.Dispose();
            _photo.Dispose();
            _photoLayer.Dispose();
            _sceneLayer.Dispose();
            _screenLayer.Dispose();
            _navigationToken++;
            _navigationBitmap?.Dispose();
            _navigationBitmap = null;
            _navigationSource = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>固定宽度的不可见占位：为「/」两侧提供布局留白，不写入空格字符，也不绘制任何内容。</summary>
    private sealed class GapSpan : ReplacementSpan
    {
        private readonly int _width;

        /// <summary>按给定像素宽度创建占位。</summary>
        /// <param name="width">占位宽度（像素）。</param>
        public GapSpan(int width) => _width = width;

        /// <summary>行内占位宽度。</summary>
        public override int GetSize(Paint paint, Java.Lang.ICharSequence? text, int start, int end,
            Paint.FontMetricsInt? fm) => _width;

        /// <summary>占位本身不绘制。</summary>
        public override void Draw(Canvas canvas, Java.Lang.ICharSequence? text, int start, int end, float x, int top,
            int y, int bottom, Paint paint)
        {
        }
    }

    /// <summary>绿框的可见性标记；实际绘制在根视图 DispatchDraw，与主屏其它叠层共用统一顺序。</summary>
    private sealed class DetailFrameView : View
    {
        public DetailFrameView(global::Android.Content.Context context) : base(context)
        {
        }
    }
}
