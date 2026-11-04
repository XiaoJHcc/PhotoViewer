using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;
using PhotoViewer.ViewModels.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 原生主窗口:悬停显示的标题栏 + 四分区(文件栏 / 主图 / 控制栏)两向布局
/// (契约 SHELL-01/02)。几何常量取自 <see cref="WindowsLayout"/>,方向切换只重排既有控件实例。
/// </summary>
internal sealed partial class WindowsMainWindow : Window
{
    /// <summary>窗口标题的应用名(标题格式 <c>文件夹名 - 文件名 - PhotoViewer</c>,SHELL-02)。</summary>
    private const string AppTitle = "PhotoViewer";

    /// <summary>任务栏缩略浮窗图标:随输出复制的 icon.ico(与 EXE 图标同源)。</summary>
    private const string WindowIconFileName = "icon.ico";

    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session = new();
    private readonly WindowsPhotoView _photo;
    private readonly WindowsNavigator _navigator;
    private readonly WindowsFilmstripView _filmstrip;
    private readonly WindowsFilterBar _filterBar;
    private readonly WindowsControlBar _controlBar;
    private readonly WindowsSimilarityPanel _similarity;
    private readonly WindowsAnalysisPanel _analysis;
    private WindowsRatingSourcesFlyout _ratingSources = null!;

    private readonly Grid _root;
    private readonly Grid _rowHost;
    private readonly Grid _colHost;
    private readonly Grid _filePane;
    private readonly Border _filePaneFrame;
    private readonly Grid _photoHost;
    private readonly Border _emptyHitArea;
    private readonly StackPanel _hintPanel;
    private readonly TextBlock _hintTitle;
    private readonly TextBlock _hintDetail;
    private readonly Border _statusToast;
    private readonly TextBlock _statusText;
    private readonly Border _busyPill;
    private readonly MenuFlyout _contextMenu;
    private ToggleMenuFlyoutItem _thumbnailMenuToggle = null!;
    private ToggleMenuFlyoutItem _analysisMenuToggle = null!;
    private ToggleMenuFlyoutItem _controlMenuToggle = null!;
    private ToggleMenuFlyoutItem _enhanceMenuToggle = null!;
    private readonly Rectangle _detailHighlight;
    private readonly Canvas _highlightLayer;
    private readonly KeyEventHandler _previewKeyHandler;
    private readonly PointerEventHandler _rootPointerMoved;
    private readonly PointerEventHandler _rootPointerExited;
    private readonly WindowsTitleBar _titleBar;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private string _windowTitle = AppTitle;
    private bool _closed;
    private XamlRoot? _xamlRoot;
    private InputNonClientPointerSource? _nonClientSource;
    private bool _pointerInside;
    private double _pointerY = double.NaN;
    private bool _nonClientHover;
    private bool _moveSizeActive;
    private bool _windowActive = true;
    private string _captionKey = string.Empty;
    private Rect? _detailHighlightUv;
    private bool _isRowLayout = true;
    private bool _sized;
    private bool _isFullScreen;
    private bool _windowIconApplied;
    private readonly IViewStateStore _viewState;
    private bool _preferencesLoaded;
    private string _lastFullError = string.Empty;

    /// <summary>
    /// 创建主窗口。
    /// </summary>
    /// <param name="main">主视图模型</param>
    /// <param name="viewState">栏位显隐的界面状态存储(与设置文档分开)</param>
    public WindowsMainWindow(MainViewModel main, IViewStateStore viewState)
    {
        _main = main;
        _viewState = viewState;
        _previewKeyHandler = OnPreviewKeyDown;
        _rootPointerMoved = OnRootPointerMoved;
        _rootPointerExited = OnRootPointerExited;
        Title = AppTitle;

        _photo = new WindowsPhotoView();
        _main.FitToViewRequested += OnFitToViewRequested;
        _filmstrip = new WindowsFilmstripView(main, SelectFile, RateFile, ShowRatingSources);
        _filterBar = new WindowsFilterBar(main.FileVM.FilterBar);
        _controlBar = new WindowsControlBar(main, Execute, anchor => ShowRatingSources(main.CurrentFile!, anchor),
            main.OpenToolsWindow);

        _hintTitle = new TextBlock
        {
            FontSize = WindowsLayout.HintTitleFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.HintTitleForeground),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _hintDetail = new TextBlock
        {
            FontSize = WindowsLayout.HintDetailFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.HintDetailForeground),
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _hintPanel = new StackPanel
        {
            MaxWidth = WindowsLayout.HintMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _hintPanel.Children.Add(_hintTitle);
        _hintPanel.Children.Add(_hintDetail);

        _navigator = new WindowsNavigator
        {
            // 导航图拖动定位（IMAGE-06）：保持当前缩放，把框心移到指针下的归一化图像点。
            CenterRequested = (u, v) => _photo.CenterOnNormalizedPoint(u, v),
        };
        _statusText = new TextBlock
        {
            FontSize = WindowsLayout.StatusToastFontSize,
            Foreground = new SolidColorBrush(WindowsLayout.StatusToastForeground),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        _statusToast = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.StatusToastBackground),
            CornerRadius = new CornerRadius(WindowsLayout.StatusToastCornerRadius),
            Padding = new Thickness(WindowsLayout.StatusToastPaddingX, WindowsLayout.StatusToastPaddingY,
                WindowsLayout.StatusToastPaddingX, WindowsLayout.StatusToastPaddingY),
            MaxWidth = WindowsLayout.StatusToastMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, WindowsLayout.StatusToastBottomMargin),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _statusText,
        };
        // IMAGE-07：增强计算超过 500ms 门槛才显示“计算中…”药丸，状态由共享会话给出。
        _busyPill = new Border
        {
            Width = WindowsLayout.BusyPillWidth,
            Height = WindowsLayout.BusyPillHeight,
            Background = new SolidColorBrush(WindowsLayout.StatusToastBackground),
            CornerRadius = new CornerRadius(WindowsLayout.BusyPillCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, WindowsLayout.BusyPillBottomOffset),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "计算中…",
                FontSize = WindowsLayout.StatusToastFontSize,
                Foreground = new SolidColorBrush(WindowsLayout.StatusToastForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // 空首屏:透明命中层盖住主图区,左键打开文件选择器;右键留给右键菜单(不能同时弹选择器)
        _emptyHitArea = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
        };
        _emptyHitArea.PointerPressed += OnEmptyAreaPressed;

        _detailHighlight = new Rectangle
        {
            Stroke = new SolidColorBrush(WindowsLayout.AnalysisHighlightStroke),
            StrokeThickness = WindowsLayout.AnalysisHighlightThickness,
            Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _highlightLayer = new Canvas { IsHitTestVisible = false };
        _highlightLayer.Children.Add(_detailHighlight);
        _photoHost = new Grid { Background = new SolidColorBrush(WindowsLayout.ViewerBackground) };
        _photoHost.Children.Add(_photo);
        _photoHost.Children.Add(_highlightLayer);
        _photoHost.Children.Add(_navigator);
        _photoHost.Children.Add(_hintPanel);
        _photoHost.Children.Add(_busyPill);
        _photoHost.Children.Add(_statusToast);
        _photoHost.Children.Add(_emptyHitArea);
        // 主图区不裁剪时,分析栏 hover 绿框在放大/平移后会画到相邻栏位上。
        _photoHost.SizeChanged += (_, e) =>
        {
            _photoHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            UpdateStatusToastWidth();
        };

        _similarity = new WindowsSimilarityPanel(main, RateFile, ShowRatingSources);
        _analysis = new WindowsAnalysisPanel(main, _session,
            (u, v) => _photo.FocusOnImagePoint(u, v),
            uv =>
            {
                _detailHighlightUv = uv;
                UpdateDetailHighlight();
            });

        _filePane = BuildFilePane();
        _filePaneFrame = new Border
        {
            Background = new SolidColorBrush(WindowsLayout.BarBackground),
            BorderBrush = new SolidColorBrush(WindowsLayout.Divider),
            Child = _filePane,
        };

        _rowHost = new Grid();
        _colHost = new Grid();
        _contextMenu = BuildContextMenu();
        _titleBar = new WindowsTitleBar(BuildWindowTitle(), ToggleFullScreenWindow, MinimizeWindow, ToggleMaximizeWindow, Close);

        _root = new Grid
        {
            Background = new SolidColorBrush(WindowsLayout.ViewerBackground),
            RequestedTheme = ElementTheme.Dark,
            IsTabStop = true,
            ContextFlyout = _contextMenu,
            AllowDrop = true,
        };
        _ratingSources = new WindowsRatingSourcesFlyout(main, _root);
        _filterBar.PopupOpening += _ratingSources.Close;
        _root.Children.Add(_rowHost);
        _root.Children.Add(_colHost);
        _root.Children.Add(_titleBar.Root);
        Content = _root;

        _root.Loaded += OnRootLoaded;
        _root.SizeChanged += OnRootSizeChanged;
        // 主图视图自身的尺寸变化(栏位显隐/换向)也要重算标题栏背板区段。
        _photo.SizeChanged += OnPhotoSizeChanged;
        // 快捷键在根元素预处置:冒泡阶段的焦点路径(ScrollViewer/按钮先处理方向键等)不再决定快捷键是否生效。
        _root.AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyHandler, handledEventsToo: true);
        // 标题栏显隐只靠指针位置;主图/控件会吃掉 PointerMoved,因此在根元素用 handledEventsToo 收。
        _root.AddHandler(UIElement.PointerMovedEvent, _rootPointerMoved, handledEventsToo: true);
        _root.AddHandler(UIElement.PointerExitedEvent, _rootPointerExited, handledEventsToo: true);
        Activated += OnWindowActivated;
        _photoHost.PointerWheelChanged += OnPhotoWheel;
        _root.DragOver += OnDragOver;
        _root.Drop += OnDrop;

        _main.PropertyChanged += OnMainChanged;
        _main.Settings.PropertyChanged += OnSettingsChanged;
        _main.FolderVM.PropertyChanged += OnFolderChanged;
        _main.RatingWriteFailedDetailed += OnRatingWriteFailed;
        _main.OpenFailed += OnOpenFailed;
        _main.FileVM.SimilarityPanel.PropertyChanged += OnSimilarityPanelChanged;
        _session.Changed += OnImageChanged;
        _session.Notice += OnSessionNotice;
        _photo.ViewportChanged += OnViewportChanged;
        _photo.RenderStateChanged += OnPhotoRenderStateChanged;
        AppWindow.Changed += OnAppWindowChanged;

        LoadUiPreferences();
        _session.SetFile(_main.CurrentFile?.File);
        ApplyLayout();
        ApplyVisibility();
        UpdateHint();
        UpdateWindowTitle();

        ConfigureTitleBar();
    }

    /// <summary>主图上屏信号源(压测驱动器按它计时)。</summary>
    internal PhotoLoadSession Session => _session;

    /// <summary>
    /// 压测汇总附加指标(写进 stress-flip.jsonl 的 done 行):物理视口、代理目标口径与纹理上传耗时统计。
    /// 视口 = 主图区的物理像素,代理目标按画质红线为视口长边 × 2。
    /// </summary>
    internal string BuildStressSummary()
    {
        var viewport = _photo.PhysicalViewport;
        var longEdge = (int)Math.Max(viewport.Width, viewport.Height);
        return $"viewport={viewport.Width:0}x{viewport.Height:0} proxyTarget={longEdge * 2} " + WindowsUploadStats.Summary();
    }

    /// <summary>
    /// 释放订阅与 GPU 资源。
    /// </summary>
    public void Detach()
    {
        // 关窗后仍可能收到一次 Activated(失活):此时任何 Window 属性都会抛 COMException,统一由该标志挡住。
        _closed = true;
        Activated -= OnWindowActivated;
        if (_xamlRoot != null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
        }

        _root.RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyHandler);
        _root.RemoveHandler(UIElement.PointerMovedEvent, _rootPointerMoved);
        _root.RemoveHandler(UIElement.PointerExitedEvent, _rootPointerExited);
        _photo.SizeChanged -= OnPhotoSizeChanged;
        _filterBar.SizeChanged -= OnFilterBarSizeChanged;
        DetachNonClientPointerSource();
        _filterBar.PopupOpening -= _ratingSources.Close;
        _ratingSources.Dispose();
        _main.FitToViewRequested -= OnFitToViewRequested;
        _main.PropertyChanged -= OnMainChanged;
        _main.Settings.PropertyChanged -= OnSettingsChanged;
        _main.FolderVM.PropertyChanged -= OnFolderChanged;
        _main.RatingWriteFailedDetailed -= OnRatingWriteFailed;
        _main.OpenFailed -= OnOpenFailed;
        _main.FileVM.SimilarityPanel.PropertyChanged -= OnSimilarityPanelChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        _session.Changed -= OnImageChanged;
        _session.Notice -= OnSessionNotice;
        _photo.ViewportChanged -= OnViewportChanged;
        _photo.RenderStateChanged -= OnPhotoRenderStateChanged;
        _statusTimer?.Stop();
        _filmstrip.Detach();
        _similarity.Detach();
        _analysis.Detach();
        _filterBar.Detach();
        _controlBar.Detach();
        _session.Dispose();
        _photo.Dispose();
    }

    /// <summary>OPEN-05：显式打开时主图回到适应态。</summary>
    private void OnFitToViewRequested() => _photo.FitToView();

    /// <summary>
    /// 执行命令(与旧壳同一份语义:可复用的命令交给共享 ControlViewModel,视口相关命令由壳实现)。
    /// </summary>
    /// <param name="command">命令标识</param>
    public void Execute(string command)
    {
        switch (command)
        {
            case "Open":
                OpenFilePicker();
                return;
            case "Fit":
                // EXIF 可能异步晚到：执行适应/100% 前刷新一次对焦点。
                UpdateFocusPoint();
                _photo.ToggleFit();
                return;
            case "ZoomInPreset":
                ZoomPreset(+1);
                return;
            case "ZoomOutPreset":
                ZoomPreset(-1);
                return;
            case "ZoomInScale":
                _photo.ZoomBy(1.25);
                return;
            case "ZoomOutScale":
                _photo.ZoomBy(0.8);
                return;
            case "ToggleEnhance":
                ToggleEnhance();
                return;
        }

        // Exchange 由共享命令直接写 CurrentFile:上一张为空时会把当前照片置空,
        // 与其他三端一样按键不动作,而不是落进共享实现。
        if (command == "Exchange" && _main.LastFile == null)
        {
            return;
        }

        // Open / Previous / Next / Exchange / SetRating0-5 与视口无关,直接复用共享命令实现
        var shared = _main.ControlVM.GetCommandByName(command);
        if (shared == null)
        {
            // 命令标识来自可编辑设置;未知命令记录后忽略,不能把异常抛过 COM 边界。
            NativeStartupLog.Write($"命令分发缺少「{command}」,已忽略。");
            return;
        }

        shared.Execute(Unit.Default);
    }

    /// <summary>
    /// 打开外部传入的路径(命令行/验收):按 file/dir 构造本地引用后交给共享打开协调器,
    /// 一批只取首个可打开项(OPEN-01);等待打开完成,供验收与命令行使用。
    /// </summary>
    /// <param name="paths">路径集合</param>
    /// <returns>本批处理完成(成功、重复激活或失败提示已上报)后结束的任务。</returns>
    public Task OpenPathsAsync(IEnumerable<string> paths)
        => OpenPathsAsync(paths, OpenRequestSource.CommandLine);

    /// <summary>同上,由调用方给出请求来源(命令行 / 拖放)。</summary>
    /// <param name="paths">路径集合</param>
    /// <param name="source">请求来源</param>
    /// <returns>本批处理完成后的任务。</returns>
    internal Task OpenPathsAsync(IEnumerable<string> paths, OpenRequestSource source)
    {
        var items = WindowsOpenPaths.FromPaths(paths);
        // 等待处理完成:验收与命令行依赖"打开结束后再断言",协调器内部仍按共享规则串行。
        return items.Count == 0
            ? Task.CompletedTask
            : _main.OpenCoordinator.PublishAndWaitAsync(items, source);
    }

    /// <summary>把主窗口前置(重复激活 / 无路径的再次启动);最小化时先还原。</summary>
    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void ShowRatingSources(ImageFile file, Button anchor)
    {
        bool controlBarAnchor = ReferenceEquals(anchor, _controlBar.RatingZeroButton);
        _ratingSources.Show(file, anchor, controlBarAnchor, _isRowLayout);
    }

    /// <summary>
    /// 构建文件栏:筛选条 + 胶片条 + 相似聚类面板(旧壳 FileView 三分区同构)。
    /// 上下布局时筛选条横跨两列;左右布局时筛选条只占 116 主列,相似面板在主列右侧。
    /// </summary>
    private Grid BuildFilePane()
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pane.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(_filterBar, 0);
        Grid.SetColumn(_filterBar, 0);
        Grid.SetColumnSpan(_filterBar, 2);
        Grid.SetRow(_filmstrip, 1);
        Grid.SetColumn(_filmstrip, 0);
        Grid.SetRow(_similarity, 1);
        Grid.SetColumn(_similarity, 1);
        pane.Children.Add(_filterBar);
        pane.Children.Add(_filmstrip);
        pane.Children.Add(_similarity);
        return pane;
    }

    /// <summary>
    /// 构建右键菜单(工具/设置窗口入口)。MENU-06:每项带语义前置图标,
    /// 勾选项(ToggleMenuFlyoutItem)同样带图标 —— WinUI 1.8 的 ToggleMenuFlyoutItem 与本机模板
    /// 都支持 Icon(CheckedWithIcon/UncheckedWithIcon 状态),勾选列与图标列并存,勾选不只靠图标表达。
    /// </summary>
    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        var openFile = new MenuFlyoutItem { Text = "打开文件…", Icon = MenuGlyph(WindowsIcons.DocumentGlyph) };
        openFile.Click += (_, _) => OpenFilePicker();
        var openFolder = new MenuFlyoutItem { Text = "打开文件夹…", Icon = MenuGlyph(WindowsIcons.FolderGlyph) };
        openFolder.Click += async (_, _) =>
        {
            var folder = await FilePicker.PickFolderAsync("选择图片文件夹");
            if (folder != null)
            {
                await _main.FolderVM.OpenFolderAsync(folder);
            }
        };

        _thumbnailMenuToggle = new ToggleMenuFlyoutItem
        {
            Text = "胶片栏",
            IsChecked = _main.IsThumbnailViewVisible,
            Icon = MenuGlyph(WindowsIcons.PhotosGlyph),
        };
        _thumbnailMenuToggle.Click += (_, _) => _main.ToggleThumbnailView();
        _analysisMenuToggle = new ToggleMenuFlyoutItem
        {
            Text = "分析栏",
            IsChecked = _main.IsAnalysisViewVisible,
            Icon = MenuGlyph(WindowsIcons.AnalysisGridGlyph),
        };
        _analysisMenuToggle.Click += (_, _) => _main.ToggleAnalysisView();
        _controlMenuToggle = new ToggleMenuFlyoutItem
        {
            Text = "控制栏",
            IsChecked = _main.IsControlViewVisible,
            Icon = MenuGlyph(WindowsIcons.SliderGlyph),
        };
        _controlMenuToggle.Click += (_, _) => _main.ToggleControlView();
        _enhanceMenuToggle = new ToggleMenuFlyoutItem
        {
            Text = "雾天增强",
            IsChecked = _session.IsEnhanced,
            Icon = MenuGlyph(WindowsIcons.ContrastGlyph),
        };
        _enhanceMenuToggle.Click += (_, _) => ToggleEnhance();
        menu.Opening += (_, _) =>
        {
            _ratingSources.Close();
            _thumbnailMenuToggle.IsChecked = _main.IsThumbnailViewVisible;
            _analysisMenuToggle.IsChecked = _main.IsAnalysisViewVisible;
            _controlMenuToggle.IsChecked = _main.IsControlViewVisible;
            _enhanceMenuToggle.IsChecked = _session.IsEnhanced;
            // MENU-07：无当前照片时雾天增强置灰，其余项照常可用。
            _enhanceMenuToggle.IsEnabled = _main.CurrentFile != null;
        };

        menu.Items.Add(openFile);
        menu.Items.Add(openFolder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_thumbnailMenuToggle);
        menu.Items.Add(_analysisMenuToggle);
        menu.Items.Add(_controlMenuToggle);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_enhanceMenuToggle);
        menu.Items.Add(new MenuFlyoutSeparator());
        var tools = new MenuFlyoutItem { Text = "工具…", Icon = MenuGlyph(WindowsIcons.ToolsGlyph) };
        tools.Click += (_, _) => _main.OpenToolsWindow();
        menu.Items.Add(tools);
        var settings = new MenuFlyoutItem { Text = "设置…", Icon = MenuGlyph(WindowsIcons.SettingsGlyph) };
        settings.Click += (_, _) => _main.OpenSettingWindow();
        menu.Items.Add(settings);
        return menu;
    }

    /// <summary>MENU-06:菜单项的语义前置图标(系统 Fluent 字形,尺寸随原生菜单图标列)。</summary>
    /// <param name="glyph">字形码位。</param>
    /// <returns>可直接作为 <c>MenuFlyoutItem.Icon</c> 的图标元素。</returns>
    private static FontIcon MenuGlyph(string glyph) => new()
    {
        FontFamily = new FontFamily(WindowsIcons.FluentFontFamily),
        Glyph = glyph,
        FontSize = DesignTokens.MenuIconSize,
    };

    /// <summary>
    /// 去掉系统标题栏、保留系统边框与缩放边缘(SHELL-02):内容顶到窗口上边缘,
    /// 拖动区由登记的系统 caption 承担,窗口按钮自绘。全屏态由 <see cref="ToggleFullScreenWindow"/> 去掉边框。
    /// </summary>
    private void ConfigureTitleBar()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            throw new InvalidOperationException("原生壳要求 OverlappedPresenter,无法配置悬停标题栏。");
        }

        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        NativeStartupLog.Write("title bar: 系统标题栏已移除,使用悬停显示的自绘标题栏");
    }

    /// <summary>最小化窗口(自绘最小化按钮;全屏态保持全屏,由系统还原时同步)。</summary>
    private void MinimizeWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    /// <summary>最大化 / 还原窗口(全屏态下先退出全屏)。</summary>
    private void ToggleMaximizeWindow()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (_isFullScreen)
        {
            ExitFullScreen(presenter);
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
        }
    }

    /// <summary>全屏 / 退出全屏(旧壳 BtnFull 语义:进入前记住原窗口态)。</summary>
    private void ToggleFullScreenWindow()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (_isFullScreen)
        {
            ExitFullScreen(presenter);
            return;
        }

        // 全屏 = 去掉边框与标题栏后最大化(OverlappedPresenter 没有独立的全屏 API)
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.Maximize();
        _isFullScreen = true;
        SyncTitleBarState();
        UpdateTitleBarChrome();
    }

    /// <summary>退出全屏,恢复系统边框与可缩放窗口(系统标题栏仍保持移除)。</summary>
    /// <param name="presenter">窗口呈现器</param>
    private void ExitFullScreen(OverlappedPresenter presenter)
    {
        // 先落标志再动窗口:SetBorderAndTitleBar/Restore 会同步触发 AppWindow.Changed,
        // 标志还停在"全屏"时同步逻辑会重复设置边框。
        _isFullScreen = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.Restore();
        SyncTitleBarState();
        UpdateTitleBarChrome();
    }

    /// <summary>系统还原全屏窗口时同步恢复系统边框与缩放边缘。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange && !args.DidSizeChange && !args.DidVisibilityChange)
        {
            return;
        }

        SyncFullScreenWithPresenter();
        SyncTitleBarState();
        UpdateTitleBarChrome();
    }

    private void SyncFullScreenWithPresenter()
    {
        if (!_isFullScreen || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            return;
        }

        _isFullScreen = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
    }

    /// <summary>
    /// 设窗口图标(SHELL-02):Win11 任务栏悬停缩略浮窗读窗口图标,不走 EXE 资源,
    /// 因此用随输出复制的 icon.ico 显式设置一次;缺失时只记诊断,不阻塞启动。
    /// </summary>
    private void ApplyWindowIcon()
    {
        if (_windowIconApplied)
        {
            return;
        }

        _windowIconApplied = true;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, WindowIconFileName);
        if (!System.IO.File.Exists(path))
        {
            NativeStartupLog.Write($"window icon: 缺少 {path},任务栏缩略浮窗将回落到系统默认图标");
            return;
        }

        try
        {
            AppWindow.SetIcon(path);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("设置窗口图标失败", ex);
        }
    }

    /// <summary>窗口首次布局完成后设定初始尺寸并让根元素获得键盘焦点。</summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot == null)
        {
            // 跨屏/缩放变化时逻辑尺寸可能不变,只能从 XamlRoot.Changed 拿到新的物理倍率。
            _xamlRoot = _root.XamlRoot;
            if (_xamlRoot != null)
            {
                _xamlRoot.Changed += OnXamlRootChanged;
            }

            _filterBar.SizeChanged += OnFilterBarSizeChanged;
        }

        if (!_sized)
        {
            _sized = true;
            var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(WindowsLayout.DefaultWindowWidth * scale),
                (int)Math.Round(WindowsLayout.DefaultWindowHeight * scale)));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)Math.Round(WindowsLayout.MinWindowWidth * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(WindowsLayout.MinWindowHeight * scale);
            }
        }

        _root.Focus(FocusState.Programmatic);
        ApplyWindowIcon();
        SyncTitleBarState();
        UpdateTitleBarChrome();
    }

    /// <summary>窗口尺寸变化:同步方向判定、相似面板宽度与标题栏跨度/拖动区。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ratingSources.Close();
        _main.UpdateScreenOrientation(e.NewSize.Width, e.NewSize.Height);
        UpdateSimilarityWidth();
        UpdateTitleBarChrome();
    }

    /// <summary>主图视图尺寸变化:重算标题栏与其重叠的背板区段。</summary>
    /// <param name="sender">主图视图</param>
    /// <param name="e">尺寸变化参数</param>
    private void OnPhotoSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarChrome();

    /// <summary>跨屏栅格化比例变化时重算物理拖动区,即使逻辑窗口尺寸不变。</summary>
    /// <param name="sender">当前 XAML 根</param>
    /// <param name="args">根布局变化参数</param>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBarChrome();

    /// <summary>筛选条完成换向布局后重算筛选挖空与标题可用宽,避免使用布局前的旧尺寸。</summary>
    /// <param name="sender">筛选条</param>
    /// <param name="args">布局尺寸变化参数</param>
    private void OnFilterBarSizeChanged(object sender, SizeChangedEventArgs args) => UpdateTitleBarChrome();

    /// <summary>按当前方向重排三个分区。</summary>
    private void ApplyLayout()
    {
        _ratingSources.Close();
        _isRowLayout = _main.IsRowLayout;

        _rowHost.Children.Clear();
        _colHost.Children.Clear();
        _rowHost.RowDefinitions.Clear();
        _rowHost.ColumnDefinitions.Clear();
        _colHost.RowDefinitions.Clear();
        _colHost.ColumnDefinitions.Clear();

        if (_isRowLayout)
        {
            _rowHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rowHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rowHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rowHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_filePaneFrame, 0);
            Grid.SetColumn(_filePaneFrame, 0);
            Grid.SetRow(_photoHost, 1);
            Grid.SetColumn(_photoHost, 0);
            Grid.SetRow(_analysis, 2);
            Grid.SetColumn(_analysis, 0);
            Grid.SetRow(_controlBar, 3);
            Grid.SetColumn(_controlBar, 0);

            _rowHost.Children.Add(_filePaneFrame);
            _rowHost.Children.Add(_photoHost);
            _rowHost.Children.Add(_analysis);
            _rowHost.Children.Add(_controlBar);
            _filePane.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumnSpan(_filterBar, 2);

            // 文件栏底部一条分隔线,文件栏占内容宽度
            _filePaneFrame.Width = double.NaN;
            _filePaneFrame.BorderThickness = new Thickness(0, 0, 0, WindowsLayout.DividerThickness);
            _rowHost.Visibility = Visibility.Visible;
            _colHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            _colHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _colHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _colHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _colHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_filePaneFrame, 0);
            Grid.SetRow(_filePaneFrame, 0);
            Grid.SetColumn(_photoHost, 1);
            Grid.SetRow(_photoHost, 0);
            Grid.SetColumn(_analysis, 2);
            Grid.SetRow(_analysis, 0);
            Grid.SetColumn(_controlBar, 3);
            Grid.SetRow(_controlBar, 0);

            _colHost.Children.Add(_filePaneFrame);
            _colHost.Children.Add(_photoHost);
            _colHost.Children.Add(_analysis);
            _colHost.Children.Add(_controlBar);
            _filePane.ColumnDefinitions[0].Width = new GridLength(WindowsLayout.RailWidth, GridUnitType.Pixel);
            Grid.SetColumnSpan(_filterBar, 1);

            // 左右布局:文件栏内容宽 116(+相似面板自动宽),右侧一条分隔线 —— 宽度交给内容计算
            _filePaneFrame.Width = double.NaN;
            _filePaneFrame.BorderThickness = new Thickness(0, 0, WindowsLayout.DividerThickness, 0);
            _rowHost.Visibility = Visibility.Collapsed;
            _colHost.Visibility = Visibility.Visible;
        }

        _filmstrip.SetOrientation(_isRowLayout);
        _filterBar.SetOrientation(_isRowLayout);
        _controlBar.SetOrientation(_isRowLayout);
        _similarity.SetOrientation(_isRowLayout);
        _analysis.SetOrientation(_isRowLayout);
        UpdateSimilarityWidth();
        // 构造期 ActualWidth/Height 还是 0;此时别更新方向,否则 Auto 布局会先按竖屏建一遍再切列布局。
        if (_root.ActualWidth > 0 && _root.ActualHeight > 0)
        {
            _main.UpdateScreenOrientation(_root.ActualWidth, _root.ActualHeight);
        }

        UpdateTitleBarChrome();
    }

    /// <summary>栏位显隐(跨会话持久化 SHELL-04;分析栏仍为会话态)。</summary>
    private void ApplyVisibility()
    {
        // 由 ToggleButton.Checked / 设置变化直接调用,异常一旦跨 COM 边界就是进程级崩溃,这里兜住。
        try
        {
            _ratingSources.Close();
            _filePaneFrame.Visibility = _main.IsThumbnailViewVisible ? Visibility.Visible : Visibility.Collapsed;
            _controlBar.Visibility = _main.IsControlViewVisible ? Visibility.Visible : Visibility.Collapsed;
            _analysis.Visibility = _main.IsAnalysisViewVisible ? Visibility.Visible : Visibility.Collapsed;
            _similarity.Visibility = _main.FileVM.IsSimilarityPanelOpen ? Visibility.Visible : Visibility.Collapsed;

            UpdateDetailHighlight();
            if (!_main.IsThumbnailViewVisible)
            {
                // 隐藏胶片条即废弃未完成的定位目标(FILM-03)
                _filmstrip.CancelLocate();
            }

            UpdateSimilarityWidth();
            UpdateTitleBarChrome();
            SaveUiPreferences();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("apply visibility", ex);
        }
    }

    /// <summary>读取栏位显隐的平台偏好(SHELL-04);缺失时保留共享默认值。</summary>
    private void LoadUiPreferences()
    {
        if (_viewState.GetBool(WindowsNativePreferences.ShowFilmKey) is { } showFilm)
        {
            _main.IsThumbnailViewVisible = showFilm;
        }

        if (_viewState.GetBool(WindowsNativePreferences.ShowControlsKey) is { } showControls)
        {
            _main.IsControlViewVisible = showControls;
        }

        _preferencesLoaded = true;
    }

    /// <summary>写入栏位显隐的平台偏好;首帧读取前不写入。</summary>
    private void SaveUiPreferences()
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        _viewState.SetBool(WindowsNativePreferences.ShowFilmKey, _main.IsThumbnailViewVisible);
        _viewState.SetBool(WindowsNativePreferences.ShowControlsKey, _main.IsControlViewVisible);
    }

    /// <summary>按可用宽度落相似面板宽度(SIM-01/SIM-07):行布局取共享 RowWidth 并夹到一半。</summary>
    private void UpdateSimilarityWidth()
    {
        var panel = _main.FileVM.SimilarityPanel;
        _similarity.SetAvailableWidth(
            Math.Max(0, _root.ActualWidth),
            panel.HasItems,
            panel.SimilarItems.Count);
    }

    /// <summary>相似面板内容变化时同步宽度(项数会改变行布局切出的宽度)。</summary>
    private void OnSimilarityPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SimilarityPanelViewModel.HasItems)
            or nameof(SimilarityPanelViewModel.SimilarItems)
            or nameof(SimilarityPanelViewModel.ShowActionArea))
        {
            UpdateSimilarityWidth();
            UpdateTitleBarChrome();
        }
    }

    /// <summary>窗口失焦收起标题栏并撤掉分析栏绿框;重新激活后设真实窗口图标。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            HandleWindowDeactivated();
            return;
        }

        _windowActive = true;
        RefreshTitleBarVisibility();
        ApplyWindowIcon();
    }

    internal void SimulateWindowDeactivatedForAcceptance() => HandleWindowDeactivated();

    /// <summary>验收:恢复活动态(真实路径由 <c>Activated</c> 事件驱动)。</summary>
    internal void SimulateWindowActivatedForAcceptance()
    {
        _windowActive = true;
        RefreshTitleBarVisibility();
    }

    private void HandleWindowDeactivated()
    {
        if (_closed)
        {
            return;
        }

        _windowActive = false;
        _nonClientHover = false;
        _moveSizeActive = false;
        RefreshTitleBarVisibility();
        _analysis.ClearPreviewHighlight();
    }

    /// <summary>指针在内容区移动:进入顶部热区显示标题栏,移出热区收起。</summary>
    /// <param name="sender">根元素</param>
    /// <param name="e">指针事件参数</param>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(_root).Position;
        _pointerY = position.Y;
        _pointerInside = position.X >= 0 && position.Y >= 0
            && position.X <= _root.ActualWidth && position.Y <= _root.ActualHeight;
        // 内容区收到了该位置的移动 ⇒ 指针不在（高度只有一条标题栏的）caption 带里。
        if (_pointerY > WindowsLayout.TitleBarHeight)
        {
            _nonClientHover = false;
        }

        RefreshTitleBarVisibility();
    }

    /// <summary>
    /// 指针离开根元素:去往 caption 非客户区(位置仍在内容区内)不算离开窗口,
    /// 真正移出窗口才收起标题栏;位置落在热区内时保持显示。
    /// </summary>
    /// <param name="sender">根元素</param>
    /// <param name="e">指针事件参数</param>
    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(_root).Position;
        _pointerInside = position.X >= 0 && position.Y >= 0
            && position.X <= _root.ActualWidth && position.Y <= _root.ActualHeight;
        if (_pointerInside)
        {
            _pointerY = position.Y;
        }
        else
        {
            _nonClientHover = false;
        }

        RefreshTitleBarVisibility();
    }

    /// <summary>
    /// 主窗口的非客户区输入源:登记 caption 拖动区,并收非客户区的指针/拖动事件。
    /// </summary>
    private InputNonClientPointerSource? NonClientSource
    {
        get
        {
            if (_nonClientSource != null)
            {
                return _nonClientSource;
            }

            try
            {
                _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            }
            catch (Exception ex)
            {
                NativeStartupLog.Fail("取得非客户区输入源失败", ex);
                return null;
            }

            AttachNonClientPointerSource(_nonClientSource);
            return _nonClientSource;
        }
    }

    /// <summary>
    /// 非客户区指针事件:<paramref name="source"/> 上的进入/离开/移动在 caption 区域上替代 XAML 指针事件,
    /// 拖动或缩放循环期间也维持标题栏可见。
    /// </summary>
    /// <param name="source">非客户区输入源</param>
    private void AttachNonClientPointerSource(InputNonClientPointerSource source)
    {
        source.PointerEntered += OnNonClientPointerEntered;
        source.PointerMoved += OnNonClientPointerMoved;
        source.PointerExited += OnNonClientPointerExited;
        source.EnteredMoveSize += OnEnteredMoveSize;
        source.ExitedMoveSize += OnExitedMoveSize;
    }

    /// <summary>脱离非客户区输入源的事件订阅(关窗时调用)。</summary>
    private void DetachNonClientPointerSource()
    {
        if (_nonClientSource == null)
        {
            return;
        }

        _nonClientSource.PointerEntered -= OnNonClientPointerEntered;
        _nonClientSource.PointerMoved -= OnNonClientPointerMoved;
        _nonClientSource.PointerExited -= OnNonClientPointerExited;
        _nonClientSource.EnteredMoveSize -= OnEnteredMoveSize;
        _nonClientSource.ExitedMoveSize -= OnExitedMoveSize;
        _nonClientSource = null;
    }

    /// <summary>指针进入 caption 区域:继续显示标题栏(此时 XAML 可能已经报 PointerExited)。</summary>
    /// <param name="sender">非客户区输入源</param>
    /// <param name="args">非客户区指针参数</param>
    private void OnNonClientPointerEntered(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind != NonClientRegionKind.Caption)
        {
            return;
        }

        _nonClientHover = true;
        _pointerInside = true;
        RefreshTitleBarVisibility();
    }

    /// <summary>指针在 caption 区域内移动:只在还没记成"在拖动区上"时补一次状态。</summary>
    /// <param name="sender">非客户区输入源</param>
    /// <param name="args">非客户区指针参数</param>
    private void OnNonClientPointerMoved(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind != NonClientRegionKind.Caption || _nonClientHover)
        {
            return;
        }

        _nonClientHover = true;
        _pointerInside = true;
        RefreshTitleBarVisibility();
    }

    /// <summary>指针离开 caption 区域:按真实光标位置判定仍在窗口内还是已经移出。</summary>
    /// <param name="sender">非客户区输入源</param>
    /// <param name="args">非客户区指针参数</param>
    private void OnNonClientPointerExited(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind != NonClientRegionKind.Caption)
        {
            return;
        }

        _nonClientHover = false;
        SyncPointerFromCursor();
        RefreshTitleBarVisibility();
    }

    /// <summary>进入系统拖动/缩放循环:整个循环期间标题栏保持可见。</summary>
    /// <param name="sender">非客户区输入源</param>
    /// <param name="args">拖动循环参数</param>
    private void OnEnteredMoveSize(InputNonClientPointerSource sender, EnteredMoveSizeEventArgs args)
    {
        _moveSizeActive = true;
        RefreshTitleBarVisibility();
    }

    /// <summary>拖动/缩放循环结束:按真实光标位置重算显隐,并按新窗口几何重登记拖动区。</summary>
    /// <param name="sender">非客户区输入源</param>
    /// <param name="args">拖动循环参数</param>
    private void OnExitedMoveSize(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args)
    {
        _moveSizeActive = false;
        SyncPointerFromCursor();
        RefreshTitleBarVisibility();
        UpdateTitleBarChrome();
    }

    /// <summary>
    /// 用系统光标位置补正指针状态:指针在非客户区时 XAML 不再报移动,
    /// 只有光标位置能判定"仍在窗口内 / 已移出窗口"。
    /// </summary>
    private void SyncPointerFromCursor()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var point) || !ScreenToClient(hwnd, ref point))
        {
            _pointerInside = false;
            return;
        }

        var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        var x = point.X / scale;
        _pointerY = point.Y / scale;
        _pointerInside = x >= 0 && _pointerY >= 0 && x <= _root.ActualWidth && _pointerY <= _root.ActualHeight;
    }

    /// <summary>
    /// 标题栏显隐(SHELL-02):指针在顶部热区、停在已登记的拖动区上或正在拖动/缩放时可见;
    /// 移出热区、离开窗口或窗口失焦即收起,不播放过渡动画。显隐不变时只做一次比较(指针移动是热路径)。
    /// </summary>
    private void RefreshTitleBarVisibility()
    {
        if (_closed)
        {
            return;
        }

        var visible = _windowActive
            && (_moveSizeActive || _nonClientHover
                || (_pointerInside && _pointerY <= WindowsLayout.TitleBarHotZoneHeight));
        if (_titleBar.IsVisible == visible)
        {
            return;
        }

        _titleBar.SetVisible(visible);
        UpdateTitleBarChrome();
    }

    /// <summary>验收:按指针位置驱动同一条显隐判定(热区外、离开窗口都必须收起)。</summary>
    /// <param name="inside">指针是否在窗口内</param>
    /// <param name="y">指针在内容坐标内的 Y</param>
    internal void SimulateTitleBarPointerForAcceptance(bool inside, double y)
    {
        _windowActive = true;
        _moveSizeActive = false;
        _nonClientHover = false;
        _pointerInside = inside;
        _pointerY = y;
        RefreshTitleBarVisibility();
    }

    /// <summary>
    /// 标题栏跨度、背景与标题可用宽(SHELL-02):列布局从文件栏右缘(含相似面板扩宽)到窗口右缘并铺亚克力;
    /// 行布局整宽无背景、只挖空筛选条可点控件;文件栏隐藏时整宽带亚克力。
    /// </summary>
    private void UpdateTitleBarChrome()
    {
        if (_closed)
        {
            return;
        }

        _titleBar.SetTitle(_windowTitle);
        if (_root.ActualWidth <= 0 || _root.ActualHeight <= 0)
        {
            return;
        }

        var fileBarVisible = _main.IsThumbnailViewVisible;
        var left = fileBarVisible && !_isRowLayout ? FilePaneRightEdge() : 0;
        var width = Math.Max(0, _root.ActualWidth - left);
        var showBackground = !(fileBarVisible && _isRowLayout);

        // 标题栏与主图视图重叠的区段由主图渲染器在交换链内画同一份亚克力(SwapChainPanel 取不到
        // AcrylicBrush 背板),XAML 背景只铺其余区段。
        Rect? photoBand = null;
        if (_titleBar.IsVisible && showBackground && _titleBar.HasBackdropRecipe)
        {
            var bar = new Rect(left, 0, width, Math.Min(WindowsLayout.TitleBarHeight, _root.ActualHeight));
            if (TryGetPhotoBand(bar, out var bandInPhoto, out var bandInBar, out var edges, out var barOrigin))
            {
                _photo.SetTitleBarBackdrop(bandInPhoto, _titleBar.BackdropRecipe, edges, barOrigin);
                photoBand = bandInBar;
            }
        }

        if (photoBand == null)
        {
            _photo.SetTitleBarBackdrop(null, _titleBar.BackdropRecipe, default, default);
        }

        _titleBar.SetSpan(left, showBackground, photoBand, width);

        // 标题文字止于按钮组前(留出与前导间距同值的尾部间距);行布局再让出筛选条可点控件。
        var titleRightEdge = _titleBar.GetButtonsLeft(_root);
        if (fileBarVisible && _isRowLayout)
        {
            titleRightEdge = _filterBar.GetInteractiveBounds(_root).X;
        }

        _titleBar.SetTitleMaxWidth(
            titleRightEdge - left - WindowsLayout.TitleBarTitleInset * 2);
        UpdateCaptionRegions();
    }

    /// <summary>
    /// 标题栏与主图视图的重叠区段:分别折算到主图视图坐标(给渲染器)与覆盖层坐标(给 XAML 背景分段),
    /// 并给出区段两侧的邻接关系 —— 主图视图边不在窗口边上时外侧是 XAML 内容(按 FallbackColor 补),
    /// 标题栏在该侧还继续时紧接一段 XAML 亚克力(两边在交界处渐变到同一个值)。
    /// </summary>
    /// <param name="bar">标题栏矩形(窗口内容坐标)</param>
    /// <param name="bandInPhoto">重叠区段(主图视图坐标)</param>
    /// <param name="bandInBar">重叠区段(覆盖层坐标)</param>
    /// <param name="edges">区段两侧的邻接关系</param>
    /// <param name="barOrigin">整条标题栏左上角(主图视图坐标,噪点平铺原点)</param>
    /// <returns>有非空重叠时返回 true</returns>
    private bool TryGetPhotoBand(Rect bar, out Rect bandInPhoto, out Rect bandInBar,
        out TitleBarBackdropEdges edges, out Point barOrigin)
    {
        bandInPhoto = default;
        bandInBar = default;
        edges = default;
        barOrigin = default;
        if (_photo.ActualWidth <= 0 || _photo.ActualHeight <= 0)
        {
            return false;
        }

        Rect bounds;
        try
        {
            bounds = _photo.TransformToVisual(_root)
                .TransformBounds(new Rect(0, 0, _photo.ActualWidth, _photo.ActualHeight));
        }
        catch (Exception)
        {
            return false;
        }

        var overlap = bar;
        overlap.Intersect(bounds);
        if (overlap.IsEmpty || overlap.Width <= 0.5 || overlap.Height <= 0.5)
        {
            return false;
        }

        bandInPhoto = new Rect(overlap.X - bounds.X, overlap.Y - bounds.Y, overlap.Width, overlap.Height);
        bandInBar = new Rect(overlap.X - _titleBar.Left, overlap.Y, overlap.Width, overlap.Height);
        barOrigin = new Point(bar.X - bounds.X, bar.Y - bounds.Y);
        const double Slack = 0.5;
        var photoLeftInside = bounds.X > Slack;
        var photoRightInside = bounds.X + bounds.Width < _root.ActualWidth - Slack;
        edges = new TitleBarBackdropEdges(
            PadLeft: photoLeftInside,
            PadRight: photoRightInside,
            FadeLeft: photoLeftInside && bar.X < bounds.X - Slack,
            FadeRight: photoRightInside && bar.X + bar.Width > bounds.X + bounds.Width + Slack);
        return true;
    }

    /// <summary>列布局文件栏右缘(含相似面板扩宽与分隔线)在内容坐标内的位置。</summary>
    /// <returns>右缘 DIP;文件栏未布局时为 0</returns>
    private double FilePaneRightEdge()
    {
        if (_filePaneFrame.ActualWidth <= 0)
        {
            return 0;
        }

        try
        {
            return _filePaneFrame.TransformToVisual(_root).TransformPoint(new Point(0, 0)).X
                + _filePaneFrame.ActualWidth;
        }
        catch (Exception)
        {
            // 尚未进入可视树时退回零偏移:文件栏在主轴首位,ActualWidth 即右缘。
            return _filePaneFrame.ActualWidth;
        }
    }

    /// <summary>
    /// 拖动区(SHELL-02):只在标题栏可见且非全屏时登记 caption 非客户区,
    /// 排除窗口按钮与行布局的筛选挖空;隐藏或全屏时登记零面积区域,顶部不拦截点击与双击。
    /// </summary>
    private void UpdateCaptionRegions()
    {
        if (_closed)
        {
            return;
        }

        var source = NonClientSource;
        if (source == null)
        {
            return;
        }

        var rects = BuildCaptionRects(_root.XamlRoot?.RasterizationScale ?? 1.0);
        var key = CaptionKey(rects);
        if (key == _captionKey)
        {
            return;
        }

        _captionKey = key;
        source.SetRegionRects(NonClientRegionKind.Caption, rects);
    }

    /// <summary>
    /// 无拖动区时登记的零面积矩形:WinAppSDK 清空 caption 区域后会退回系统默认的整幅宽 caption 带
    /// (顶部又变成可拖动),因此隐藏与全屏态显式登记一条零面积 caption —— 命中面积为零,点击落回内容。
    /// </summary>
    private static readonly RectInt32[] NoCaptionRects = { new(0, 0, 0, 0) };

    /// <summary>按当前布局算出 caption 区域的物理像素矩形。</summary>
    /// <param name="scale">物理倍率</param>
    /// <returns>矩形集合;隐藏或全屏时为零面积矩形</returns>
    private RectInt32[] BuildCaptionRects(double scale)
    {
        if (!_titleBar.IsVisible || _isFullScreen || _root.ActualWidth <= 0 || _root.ActualHeight <= 0)
        {
            return NoCaptionRects;
        }

        var width = (int)Math.Round(_root.ActualWidth * scale);
        var height = (int)Math.Round(Math.Min(WindowsLayout.TitleBarHeight, _root.ActualHeight) * scale);
        if (width <= 0 || height <= 0)
        {
            return NoCaptionRects;
        }

        var leftEdge = (int)Math.Clamp(Math.Round(_titleBar.Left * scale), 0, width);
        var buttonLeft = (int)Math.Clamp(Math.Round(_titleBar.GetButtonsLeft(_root) * scale), 0, width);
        if (buttonLeft <= leftEdge)
        {
            return NoCaptionRects;
        }

        if (_main.IsThumbnailViewVisible && _isRowLayout)
        {
            var bounds = _filterBar.GetInteractiveBounds(_root);
            var cutLeft = (int)Math.Clamp(Math.Floor(bounds.X * scale), leftEdge, buttonLeft);
            var cutRight = (int)Math.Clamp(Math.Ceiling((bounds.X + bounds.Width) * scale), cutLeft, buttonLeft);
            var segments = new List<RectInt32>(2);
            if (cutLeft > leftEdge)
            {
                segments.Add(new RectInt32(leftEdge, 0, cutLeft - leftEdge, height));
            }

            if (buttonLeft > cutRight)
            {
                segments.Add(new RectInt32(cutRight, 0, buttonLeft - cutRight, height));
            }

            return segments.ToArray();
        }

        return new[] { new RectInt32(leftEdge, 0, buttonLeft - leftEdge, height) };
    }

    /// <summary>拖动区的比较键(指针移动热路径上避免重复登记)。</summary>
    /// <param name="rects">矩形集合</param>
    /// <returns>可比较的字符串</returns>
    private static string CaptionKey(RectInt32[] rects)
    {
        var builder = new System.Text.StringBuilder(rects.Length * 20);
        foreach (var rect in rects)
        {
            builder.Append(rect.X).Append(',').Append(rect.Y).Append(',')
                .Append(rect.Width).Append(',').Append(rect.Height).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>窗口按钮字形与提示跟随窗口态(最大化/还原、全屏/退出全屏)。</summary>
    private void SyncTitleBarState()
    {
        if (_closed)
        {
            return;
        }

        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _titleBar.SetWindowState(_isFullScreen, maximized);
    }

    /// <summary>光标位置(屏幕物理像素)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        /// <summary>屏幕 X。</summary>
        public int X;

        /// <summary>屏幕 Y。</summary>
        public int Y;
    }

    /// <summary>取系统光标位置。</summary>
    /// <param name="point">屏幕物理像素坐标</param>
    /// <returns>成功返回 true</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>屏幕坐标 → 客户区坐标(Per-Monitor-V2 下为物理像素)。</summary>
    /// <param name="hwnd">窗口句柄</param>
    /// <param name="point">待换算的点</param>
    /// <returns>成功返回 true</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    /// <summary>当前照片或布局变化:换图、换向、刷新提示与窗口标题。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentFile):
                _lastFullError = string.Empty;
                UpdateFocusPoint();
                _session.SetFile(_main.CurrentFile?.File);
                UpdateHint();
                UpdateWindowTitle();
                break;
            case nameof(MainViewModel.IsRowLayout):
                ApplyLayout();
                ApplyVisibility();
                break;
            case nameof(MainViewModel.IsThumbnailViewVisible):
            case nameof(MainViewModel.IsControlViewVisible):
            case nameof(MainViewModel.IsAnalysisViewVisible):
                ApplyVisibility();
                OnViewportChanged();
                break;
        }
    }

    /// <summary>文件源变化:目录名进入窗口标题(SHELL-02)。</summary>
    /// <param name="sender">文件源视图模型。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnFolderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderViewModel.FolderName))
        {
            UpdateWindowTitle();
        }
    }

    /// <summary>
    /// 窗口标题(SHELL-02):<c>文件夹名 - 文件名 - PhotoViewer</c>。标题栏文字与系统窗口标题
    /// (任务栏/窗口菜单/调度中心读它)始终是同一个字符串。
    /// 无当前文件时只显示应用名;文件夹段与文件段同名时只出现一次,
    /// 应用名始终保留为最后一段(文件夹恰好叫 PhotoViewer 时标题是
    /// <c>PhotoViewer - 文件名 - PhotoViewer</c>)。
    /// </summary>
    private void UpdateWindowTitle()
    {
        _windowTitle = BuildWindowTitle();
        if (!_closed)
        {
            Title = _windowTitle;
        }

        _titleBar.SetTitle(_windowTitle);
    }

    /// <summary>按当前目录与当前文件拼窗口标题。</summary>
    /// <returns>标题文案。</returns>
    private string BuildWindowTitle()
    {
        var file = _main.CurrentFile;
        if (file == null)
        {
            return AppTitle;
        }

        var parts = new List<string>(3);
        var folder = _main.FolderVM.FolderName?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            parts.Add(folder);
        }

        // 只去重文件夹段与文件段(单图/同名情况);应用名不参与去重,必须留在最后。
        var name = file.Name?.Trim();
        if (!string.IsNullOrEmpty(name)
            && !string.Equals(name, folder, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(name);
        }

        parts.Add(AppTitle);
        return string.Join(" - ", parts);
    }

    /// <summary>解码会话变化:同步展示帧、增强计算态与提示(回调内同步上传,不跨下一次 Changed 持引用)。</summary>
    private void OnImageChanged()
    {
        // EXIF 可能晚于主图到达;每次会话变化都重读对焦点,ToggleFit 才能在 F 上按对焦点放大。
        UpdateFocusPoint();
        // 会话自己管理增强态:Image 在增强就绪后返回增强图,切图/清空时立即回到新图或 null。
        // STATE-01：代际让视图区分「缓存命中切图」与「代理↔Full 热替换」,两者可能源尺寸相同。
        _photo.SetImage(_session.Image, _session.Generation);
        _busyPill.Visibility = _session.IsEnhancingVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusToastBottomMargin();
        // IMAGE-03：有代理时 Full 升级失败只走非阻塞提示，不覆盖主图错误标题；同一错误只提示一次。
        if (_session.FullError.Length > 0 && _session.FullError != _lastFullError)
        {
            ShowStatus(_session.FullError);
        }

        _lastFullError = _session.FullError;
        UpdateHint();
        UpdateNavigator();
        UpdateDetailHighlight();
    }

    /// <summary>SCOPE-02/STATE-03：会话的一次性提示(如内存压力自动关闭增强)走现有提示条。</summary>
    private void OnSessionNotice(string message) => ShowStatus(message);

    /// <summary>把当前文件的 Sony 对焦点同步给共享视口(归一化显示朝向)。</summary>
    private void UpdateFocusPoint()
    {
        var focus = _main.CurrentFile?.ExifData?.SonyFocusPosition;
        if (focus is { ImageWidth: > 0, ImageHeight: > 0 } position)
        {
            _photo.SetFocus(
                position.FocusX / (double)position.ImageWidth,
                position.FocusY / (double)position.ImageHeight);
            return;
        }

        _photo.SetFocus(null, null);
    }

    /// <summary>切换增强预览(增强态由共享 PhotoLoadSession 持有,模式跨切图持续;开关状态由菜单勾选表达)。</summary>
    private void ToggleEnhance()
    {
        _session.ToggleEnhanced();
        _enhanceMenuToggle.IsChecked = _session.IsEnhanced;
    }

    /// <summary>主图渲染状态变化:导航图与细节预览绿框一起跟随缩放/平移。</summary>
    private void OnPhotoRenderStateChanged()
    {
        UpdateNavigator();
        UpdateDetailHighlight();
    }

    /// <summary>把细节预览的 uv 矩形换算成主图上的绿框(无矩形则隐藏)。</summary>
    private void UpdateDetailHighlight()
    {
        if (_detailHighlightUv is not { } uv || !_main.IsAnalysisViewVisible || !_photo.TryGetImageRect(uv, out var rect))
        {
            _detailHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        _detailHighlight.Width = Math.Max(0, rect.Width);
        _detailHighlight.Height = Math.Max(0, rect.Height);
        Canvas.SetLeft(_detailHighlight, rect.X);
        Canvas.SetTop(_detailHighlight, rect.Y);
        _detailHighlight.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 刷新缩放指示器(导航图):仅在设置开启、非适应态且有照片时显示;
    /// 换图(含代理→Full 热替换)才重建缩略图,缩放/平移只移动可见框与百分比。
    /// </summary>
    private void UpdateNavigator()
    {
        bool show = _main.Settings.ShowZoomIndicator && !_photo.IsFit && _photo.HasImage && _session.Image != null;
        if (!show)
        {
            _navigator.Reset();
            return;
        }

        _navigator.Visibility = Visibility.Visible;
        _navigator.Update(_session.Image, _photo.NavigationSize, _photo.NavigationViewport, _photo.PhysicalScale);
    }

    /// <summary>星级写回失败:窗口内给可见提示,不保留看似成功的星色(FILM-06)。</summary>
    /// <param name="failure">本次失败的结果、对象与原因。</param>
    private void OnRatingWriteFailed(RatingWriteFailure failure)
        => ShowStatus(StatusMessages.RatingWriteFailed(
            failure.File.Name, failure.Reason, failure.IsPartial));

    /// <summary>STATE-02：共享层上报的打开失败统一用提示条呈现，不弹模态。</summary>
    /// <param name="message">失败提示正文</param>
    private void OnOpenFailed(string message) => ShowStatus(message);

    /// <summary>STATE-03：药丸可见时提示条整体上移到药丸上方，药丸收起后回到常规底边距。</summary>
    private void UpdateStatusToastBottomMargin()
        => _statusToast.Margin = new Thickness(0, 0, 0,
            _busyPill.Visibility == Visibility.Visible
                ? WindowsLayout.StatusToastStackMargin
                : WindowsLayout.StatusToastBottomMargin);

    /// <summary>在主图区底部显示一条短提示,数秒后自动消失。</summary>
    /// <param name="message">提示内容</param>
    private void ShowStatus(string message)
    {
        _statusText.Text = message;
        UpdateStatusToastWidth();
        _statusToast.Visibility = Visibility.Visible;

        _statusTimer ??= DispatcherQueue.CreateTimer();
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(WindowsLayout.StatusToastMilliseconds);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick -= OnStatusTimerTick;
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
    }

    /// <summary>STATE-03：提示条宽取 min(500, 主图区可用宽),不超出观片区。</summary>
    private void UpdateStatusToastWidth()
    {
        double available = _photoHost.ActualWidth;
        _statusToast.MaxWidth = available > 0
            ? Math.Min(WindowsLayout.StatusToastMaxWidth, available)
            : WindowsLayout.StatusToastMaxWidth;
    }

    /// <summary>状态提示到期后收起。</summary>
    /// <param name="sender">计时器</param>
    /// <param name="args">事件参数</param>
    private void OnStatusTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _statusToast.Visibility = Visibility.Collapsed;
    }

    /// <summary>设置变化:缩放指示器开关即时生效。</summary>
    /// <param name="sender">设置视图模型</param>
    /// <param name="e">属性变化参数</param>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ShowZoomIndicator))
        {
            UpdateNavigator();
        }

        if (e.PropertyName == nameof(SettingsViewModel.SimilarityPanelExpanded))
        {
            ApplyVisibility();
            OnViewportChanged();
        }
    }

    /// <summary>视口物理像素或 LOD 需求变化:驱动代理/全分辨率决策。</summary>
    private void OnViewportChanged()
    {
        var viewport = _photo.PhysicalViewport;
        // 分析栏可见时细节预览要 100% 源 → 一并驱动 Full 解码(根 AGENTS 5.1 同口径)
        _session.UpdateViewport(viewport.Width, viewport.Height, _photo.NeedsFull || _main.IsAnalysisViewVisible);
    }

    /// <summary>刷新空首屏提示与命中层;解码失败时给出可见原因(FILM-06 同口径)。</summary>
    private void UpdateHint()
    {
        var file = _main.CurrentFile;
        var error = file == null ? null : _session.Error;
        if (file == null)
        {
            _hintTitle.Text = "点击打开文件 或 拖拽图片到此处";
            _hintDetail.Text = string.Empty;
        }
        else if (!string.IsNullOrEmpty(error))
        {
            _hintTitle.Text = error;
            _hintDetail.Text = file.Name;
        }
        else if (_session.IsLoadingVisible)
        {
            // STATE-01：共享会话延迟 300ms 才暴露加载态，快速翻页不闪提示。
            _hintTitle.Text = "正在载入…";
            _hintDetail.Text = file.Name;
        }
        else
        {
            _hintTitle.Text = string.Empty;
            _hintDetail.Text = string.Empty;
        }

        var visible = _hintTitle.Text.Length > 0 || _hintDetail.Text.Length > 0;
        _hintPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _emptyHitArea.Visibility = file == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 主窗快捷键入口:根元素 PreviewKeyDown 与实际键盘共用,返回是否消费按键(INPUT-01)。
    /// 未映射或未绑定的按键放行;焦点在文本编辑控件内时整体放行;命令执行前先过重复设闸。
    /// </summary>
    /// <param name="key">虚拟键</param>
    /// <param name="wasKeyDown">是否为按住产生的重复按键</param>
    /// <returns>消费返回 true,放行返回 false</returns>
    internal bool HandleShortcutKey(VirtualKey key, bool wasKeyDown)
    {
        var route = WindowsKeyDispatch.Resolve(
            WindowsInput.FromKey(key),
            _main.Settings,
            IsTextEditingFocus(),
            wasKeyDown);
        if (!route.Consume)
        {
            return false;
        }

        if (route.Command is { } command)
        {
            Execute(command);
        }

        return true;
    }

    /// <summary>根元素 PreviewKeyDown:在焦点控件之前处置快捷键。</summary>
    /// <param name="sender">根元素</param>
    /// <param name="e">按键参数</param>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleShortcutKey(e.Key, e.KeyStatus.WasKeyDown))
        {
            e.Handled = true;
        }
    }

    /// <summary>焦点是否落在文本编辑控件内(主窗后续出现输入框时必须先放行)。</summary>
    /// <returns>文本编辑焦点返回 true</returns>
    private bool IsTextEditingFocus()
        => _root.XamlRoot is { } xamlRoot
            && WindowsKeyDispatch.IsTextEditingElement(FocusManager.GetFocusedElement(xamlRoot) as DependencyObject);

    /// <summary>
    /// 主图区滚轮(INPUT-04):精确滚动(触控板/高精度滚轮)平移照片;离散滚轮按格查共享手势表。
    /// 带用户绑定的修饰键手势优先，仍由同一张绑定表决定。
    /// </summary>
    private void OnPhotoWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_photoHost);
        var properties = point.Properties;
        var delta = properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var gesture = WindowsInput.FromWheel(delta);
        var command = _main.Settings.GetCommandByGesture(gesture);
        bool commandAvailable = command != null && InputCommandMap.IsCommandKnown(command);
        // INPUT-04：带修饰键的用户绑定优先于精确/离散分流（如 Ctrl+滚轮缩放）。
        if (commandAvailable && gesture.Mouse is { Modifiers: not GestureModifiers.None })
        {
            e.Handled = true;
            Execute(command!);
            return;
        }

        if (IsPreciseWheel(delta))
        {
            e.Handled = true;
            if (_photo.IsFit)
            {
                return;
            }

            double step = delta / 120.0 * 50;
            if (properties.IsHorizontalMouseWheel)
            {
                _photo.PanBy(step, 0);
            }
            else
            {
                _photo.PanBy(0, step);
            }

            return;
        }

        if (!commandAvailable)
        {
            return;
        }

        e.Handled = true;
        int notches = Math.Clamp(Math.Abs(delta) / 120, 1, 3);
        if (!InputCommandMap.IsRepeatable(command))
        {
            notches = 1;
        }

        for (var index = 0; index < notches; index++)
        {
            Execute(command!);
        }
    }

    /// <summary>精确滚动判据:Win32 精度触控板签名,或小于一格的增量(高精度滚轮)。</summary>
    /// <param name="delta">滚轮增量。</param>
    /// <returns>精确滚动返回 true。</returns>
    private static bool IsPreciseWheel(int delta)
    {
        long extra = GetMessageExtraInfo().ToInt64();
        bool precisionTouchpad = (extra & 0xFFFFFF00L) == PrecisionTouchpadSignature;
        return precisionTouchpad || Math.Abs(delta) < 120;
    }

    /// <summary>精度触控板滚轮消息的 extra-info 签名。</summary>
    private const long PrecisionTouchpadSignature = 0xFF515700L;

    /// <summary>读取当前窗口消息的 extra-info(Win32);用于区分精度触控板。</summary>
    /// <returns>消息 extra-info。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    /// <summary>拖放进入窗口:接受复制。</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    /// <summary>拖放落下:与命令行同一条协调器入口,来源记为拖放。</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
            if (paths.Count == 0)
            {
                return;
            }

            await OpenPathsAsync(paths, OpenRequestSource.DragDrop);
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("拖放打开失败", ex);
        }
    }

    /// <summary>选中胶片条卡片(FILM-01:不主动居中)。</summary>
    private void SelectFile(ImageFile file) => _main.FileVM.ThumbnailList.SelectImageCommand(file);

    /// <summary>
    /// 空首屏按下:鼠标只响应左键,右键交给窗口的右键菜单(旧壳 <c>ImageView</c> 同口径:
    /// <c>IsRightButtonPressed</c> 先弹菜单、<c>IsLeftButtonPressed</c> 才打开文件);触摸/笔直接打开。
    /// </summary>
    private void OnEmptyAreaPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_emptyHitArea);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        OpenFilePicker();
    }

    /// <summary>打开文件选择器;失败必须留痕,不能静默无响应。</summary>
    private async void OpenFilePicker()
    {
        try
        {
            await _main.FolderVM.OpenFilePickerAsync();
        }
        catch (Exception ex)
        {
            NativeStartupLog.Fail("打开文件选择器失败", ex);
            _main.ReportOpenFailure(StatusMessages.OpenFailed(null, StatusMessages.ReasonFor(ex)));
        }
    }

    /// <summary>胶片条卡片评分。</summary>
    private Task RateFile(ImageFile file, int rating) => _main.SetRatingAsync(file, rating);

    /// <summary>按预设档位缩放(相对当前物理缩放取上/下一档)。</summary>
    private void ZoomPreset(int direction)
    {
        var current = _photo.ZoomRatio;
        var target = ZoomPresets.Next(
            _main.Settings.ScalePresets.Select(preset => preset.Value), current, direction);
        if (target is not double preset)
        {
            return;
        }

        _photo.ZoomBy(preset / current);
    }
}
