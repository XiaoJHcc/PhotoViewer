using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 原生主窗口:隐藏式标题栏 + 四分区(文件栏 / 主图 / 控制栏)两向布局
/// (契约 SHELL-01/02)。几何常量取自 <see cref="WindowsLayout"/>,方向切换只重排既有控件实例。
/// </summary>
internal sealed partial class WindowsMainWindow : Window
{
    /// <summary>窗口标题(与旧壳一致,标题栏覆盖层与系统窗口标题共用)。</summary>
    private const string WindowTitle = "PhotoViewer 图片查看器";

    private readonly MainViewModel _main;
    private readonly PhotoLoadSession _session = new();
    private readonly WindowsPhotoView _photo;
    private readonly WindowsFilmstripView _filmstrip;
    private readonly WindowsFilterBar _filterBar;
    private readonly WindowsControlBar _controlBar;

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
    private readonly MenuFlyout _contextMenu;
    private readonly WindowsTitleBar _titleBar;

    private bool _isRowLayout = true;
    private bool _sized;
    private bool _isFullScreen;
    private XamlRoot? _xamlRoot;

    /// <summary>
    /// 创建主窗口。
    /// </summary>
    /// <param name="main">主视图模型(必须以 nativePresentation: true 构造)</param>
    public WindowsMainWindow(MainViewModel main)
    {
        _main = main;
        Title = WindowTitle;

        _photo = new WindowsPhotoView();
        _filmstrip = new WindowsFilmstripView(main, SelectFile, RateFile);
        _filterBar = new WindowsFilterBar(main.FileVM.FilterBar);
        _controlBar = new WindowsControlBar(main, Execute);

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

        // 空首屏:透明命中层盖住主图区,左键打开文件选择器;右键留给右键菜单(不能同时弹选择器)
        _emptyHitArea = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
        };
        _emptyHitArea.PointerPressed += OnEmptyAreaPressed;

        _photoHost = new Grid { Background = new SolidColorBrush(WindowsLayout.ViewerBackground) };
        _photoHost.Children.Add(_photo);
        _photoHost.Children.Add(_hintPanel);
        _photoHost.Children.Add(_emptyHitArea);

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
        _titleBar = new WindowsTitleBar(WindowTitle, MinimizeWindow, ToggleMaximizeWindow, ToggleFullScreenWindow, Close);

        _root = new Grid
        {
            Background = new SolidColorBrush(WindowsLayout.ViewerBackground),
            RequestedTheme = ElementTheme.Dark,
            IsTabStop = true,
            ContextFlyout = _contextMenu,
            AllowDrop = true,
        };
        _root.Children.Add(_rowHost);
        _root.Children.Add(_colHost);
        _root.Children.Add(_titleBar.Root);
        Content = _root;

        _root.Loaded += OnRootLoaded;
        _root.SizeChanged += OnRootSizeChanged;
        _root.KeyDown += OnKeyDown;
        _root.PointerMoved += OnRootPointerMoved;
        Activated += OnWindowActivated;
        _photoHost.PointerWheelChanged += OnPhotoWheel;
        _root.DragOver += OnDragOver;
        _root.Drop += OnDrop;

        _main.PropertyChanged += OnMainChanged;
        _session.Changed += OnImageChanged;
        _photo.ViewportChanged += OnViewportChanged;

        _session.SetFile(_main.CurrentFile?.File);
        ApplyLayout();
        ApplyVisibility();
        UpdateHint();

        ConfigureTitleBar();
    }

    /// <summary>
    /// 释放订阅与 GPU 资源。
    /// </summary>
    public void Detach()
    {
        if (_xamlRoot != null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
        }

        _filterBar.SizeChanged -= OnFilterBarSizeChanged;
        _main.PropertyChanged -= OnMainChanged;
        _session.Changed -= OnImageChanged;
        _photo.ViewportChanged -= OnViewportChanged;
        _filmstrip.Detach();
        _filterBar.Detach();
        _controlBar.Detach();
        _session.Dispose();
        _photo.Dispose();
    }

    /// <summary>
    /// 执行命令(与旧壳同一份语义:可复用的命令交给共享 ControlViewModel,视口相关命令由壳实现)。
    /// </summary>
    /// <param name="command">命令标识</param>
    public void Execute(string command)
    {
        switch (command)
        {
            case "Fit":
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
        }

        // Open / Previous / Next / Exchange / SetRating0-5 与视口无关,直接复用共享命令实现
        var shared = _main.ControlVM.GetCommandByName(command)
            ?? throw new InvalidOperationException($"未知命令「{command}」;原生壳命令分发缺少该分支。");
        shared.Execute(null);
    }

    /// <summary>打开外部传入的路径(命令行/拖放/文件关联)。</summary>
    /// <param name="paths">路径集合</param>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        var refs = paths
            .Where(System.IO.File.Exists)
            .Select(path => (IFileRef)new LocalFileRef(path))
            .ToList();
        if (refs.Count == 0)
        {
            return;
        }

        await _main.FolderVM.OpenExternalFilesAsync(refs);
    }

    /// <summary>构建文件栏:筛选条在上,胶片条在下(两向共用同一实例)。</summary>
    private Grid BuildFilePane()
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_filterBar, 0);
        Grid.SetRow(_filmstrip, 1);
        pane.Children.Add(_filterBar);
        pane.Children.Add(_filmstrip);
        return pane;
    }

    /// <summary>构建右键菜单(设置入口在 W-M5 前不显示,P3 入口不显示)。</summary>
    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        var openFile = new MenuFlyoutItem { Text = "打开文件…" };
        openFile.Click += (_, _) => OpenFilePicker();
        var openFolder = new MenuFlyoutItem { Text = "打开文件夹…" };
        openFolder.Click += async (_, _) =>
        {
            var folder = await FilePicker.PickFolderAsync("选择图片文件夹");
            if (folder != null)
            {
                await _main.FolderVM.OpenFolderAsync(folder);
            }
        };

        var thumbnailToggle = new ToggleMenuFlyoutItem { Text = "胶片条", IsChecked = _main.IsThumbnailViewVisible };
        thumbnailToggle.Click += (_, _) => _main.ToggleThumbnailView();
        var controlToggle = new ToggleMenuFlyoutItem { Text = "控制栏", IsChecked = _main.IsControlViewVisible };
        controlToggle.Click += (_, _) => _main.ToggleControlView();

        menu.Items.Add(openFile);
        menu.Items.Add(openFolder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(thumbnailToggle);
        menu.Items.Add(controlToggle);
        return menu;
    }

    /// <summary>
    /// 去掉系统标题栏与系统最小/最大/关闭按钮,改用自绘按钮 + 自动显隐(SHELL-02)。
    /// <c>hasBorder: true</c> 保留窗口边框与拖拽改尺寸;<c>hasTitleBar: false</c> 让内容直接顶到窗口上边缘。
    /// </summary>
    private void ConfigureTitleBar()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            throw new InvalidOperationException("原生壳要求 OverlappedPresenter,无法配置隐藏式标题栏。");
        }

        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        NativeStartupLog.Write("title bar: 系统标题栏已移除,使用自绘自动显隐标题栏");
    }

    /// <summary>最小化窗口。</summary>
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
    }

    /// <summary>退出全屏,恢复有边框的可缩放窗口。</summary>
    /// <param name="presenter">窗口呈现器</param>
    private void ExitFullScreen(OverlappedPresenter presenter)
    {
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.Restore();
        _isFullScreen = false;
    }

    /// <summary>窗口首次布局完成后设定初始尺寸并让根元素获得键盘焦点。</summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot == null)
        {
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
        UpdateCaptionRegions();
    }

    /// <summary>窗口尺寸变化:同步方向判定与标题栏拖动区。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _main.UpdateScreenOrientation(e.NewSize.Width, e.NewSize.Height);
        UpdateCaptionRegions();
    }

    /// <summary>跨屏栅格化比例变化时重算物理标题栏区域，即使逻辑窗口尺寸不变。</summary>
    /// <param name="sender">当前 XAML 根。</param>
    /// <param name="args">根布局变化参数。</param>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateCaptionRegions();

    /// <summary>筛选条完成换向布局后更新拖动区，避免使用布局前的旧尺寸。</summary>
    /// <param name="sender">筛选条。</param>
    /// <param name="args">布局尺寸变化参数。</param>
    private void OnFilterBarSizeChanged(object sender, SizeChangedEventArgs args) => UpdateCaptionRegions();

    /// <summary>按当前方向重排三个分区。</summary>
    private void ApplyLayout()
    {
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

            Grid.SetRow(_filePaneFrame, 0);
            Grid.SetColumn(_filePaneFrame, 0);
            Grid.SetRow(_photoHost, 1);
            Grid.SetColumn(_photoHost, 0);
            Grid.SetRow(_controlBar, 2);
            Grid.SetColumn(_controlBar, 0);

            _rowHost.Children.Add(_filePaneFrame);
            _rowHost.Children.Add(_photoHost);
            _rowHost.Children.Add(_controlBar);

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

            Grid.SetColumn(_filePaneFrame, 0);
            Grid.SetRow(_filePaneFrame, 0);
            Grid.SetColumn(_photoHost, 1);
            Grid.SetRow(_photoHost, 0);
            Grid.SetColumn(_controlBar, 2);
            Grid.SetRow(_controlBar, 0);

            _colHost.Children.Add(_filePaneFrame);
            _colHost.Children.Add(_photoHost);
            _colHost.Children.Add(_controlBar);

            // 左右布局:文件栏内容宽 116,右侧一条分隔线
            _filePaneFrame.Width = WindowsLayout.RailFrameWidth;
            _filePaneFrame.BorderThickness = new Thickness(0, 0, WindowsLayout.DividerThickness, 0);
            _rowHost.Visibility = Visibility.Collapsed;
            _colHost.Visibility = Visibility.Visible;
        }

        _filmstrip.SetOrientation(_isRowLayout);
        _filterBar.SetOrientation(_isRowLayout);
        _controlBar.SetOrientation(_isRowLayout);
        _main.UpdateScreenOrientation(_root.ActualWidth, _root.ActualHeight);
        UpdateCaptionRegions();
    }

    /// <summary>栏位显隐(会话内状态,与旧壳一致)。</summary>
    private void ApplyVisibility()
    {
        _filePaneFrame.Visibility = _main.IsThumbnailViewVisible ? Visibility.Visible : Visibility.Collapsed;
        _controlBar.Visibility = _main.IsControlViewVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaptionRegions();
    }

    /// <summary>指针移动:进入顶部热区显示标题栏,离开隐藏(旧壳 HotZoneHeight=50)。</summary>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(_root).Position.Y;
        UpdateTitleBarVisibility(y <= WindowsLayout.CaptionHotZoneHeight);
    }

    /// <summary>窗口失活时隐藏标题栏(旧壳 Deactivated 同口径);激活不主动显示。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            UpdateTitleBarVisibility(false);
        }
    }

    /// <summary>
    /// 标题栏显隐:标题栏不可见时不登记系统 caption 区域,顶部因此不可拖动(旧壳同口径)。
    /// </summary>
    /// <param name="visible">是否显示</param>
    private void UpdateTitleBarVisibility(bool visible)
    {
        _titleBar.SetVisible(visible);
        UpdateCaptionRegions();
    }

    /// <summary>
    /// 隐藏式标题栏的拖动区:避让筛选条与文件栏,不吞掉控件手势(SHELL-02);
    /// 隐藏时清空全部区域。
    /// </summary>
    private void UpdateCaptionRegions()
    {
        if (_root.ActualWidth <= 0)
        {
            return;
        }

        var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Round(_root.ActualWidth * scale);
        var height = (int)Math.Round(Math.Min(WindowsLayout.CaptionHeight, _root.ActualHeight) * scale);
        var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        var rects = new List<RectInt32>();
        if (!_titleBar.IsVisible)
        {
            nonClient.SetRegionRects(NonClientRegionKind.Caption, rects.ToArray());
            return;
        }

        var buttonsWidth = _titleBar.ButtonsWidth;
        if (!_main.IsThumbnailViewVisible)
        {
            var right = (int)Math.Clamp(Math.Floor((_root.ActualWidth - buttonsWidth) * scale), 0, width);
            rects.Add(new RectInt32(0, 0, right, height));
            _titleBar.SetTitleInset(0);
        }
        else if (_isRowLayout)
        {
            var bounds = _filterBar.GetInteractiveBounds(_root);
            var left = (int)Math.Clamp(Math.Floor(bounds.X * scale), 0, width);
            var right = (int)Math.Clamp(Math.Ceiling((bounds.X + bounds.Width) * scale), left, width);
            var buttonLeft = (int)Math.Clamp(Math.Floor((_root.ActualWidth - buttonsWidth) * scale), 0, width);
            if (left > 0)
            {
                rects.Add(new RectInt32(0, 0, left, height));
            }

            if (right < buttonLeft)
            {
                rects.Add(new RectInt32(right, 0, buttonLeft - right, height));
            }

            _titleBar.SetTitleInset(0);
        }
        else
        {
            var left = (int)Math.Clamp(Math.Ceiling(_filePaneFrame.ActualWidth * scale), 0, width);
            var buttonLeft = (int)Math.Clamp(Math.Floor((_root.ActualWidth - buttonsWidth) * scale), left, width);
            if (left < buttonLeft)
            {
                rects.Add(new RectInt32(left, 0, buttonLeft - left, height));
            }

            _titleBar.SetTitleInset(_filePaneFrame.ActualWidth);
        }

        nonClient.SetRegionRects(NonClientRegionKind.Caption, rects.ToArray());
    }

    /// <summary>当前照片或布局变化:换图、换向、刷新提示。</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentFile):
                _session.SetFile(_main.CurrentFile?.File);
                UpdateHint();
                break;
            case nameof(MainViewModel.IsRowLayout):
                ApplyLayout();
                ApplyVisibility();
                break;
            case nameof(MainViewModel.IsThumbnailViewVisible):
            case nameof(MainViewModel.IsControlViewVisible):
                ApplyVisibility();
                break;
        }
    }

    /// <summary>解码会话变化:同步纹理与提示(回调内同步上传,不跨下一次 Changed 持引用)。</summary>
    private void OnImageChanged()
    {
        _photo.SetImage(_session.Image);
        UpdateHint();
    }

    /// <summary>视口物理像素或 LOD 需求变化:驱动代理/全分辨率决策。</summary>
    private void OnViewportChanged()
    {
        var viewport = _photo.PhysicalViewport;
        _session.UpdateViewport(viewport.Width, viewport.Height, _photo.NeedsFull);
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
        else
        {
            _hintTitle.Text = string.Empty;
            _hintDetail.Text = string.Empty;
        }

        var visible = _hintTitle.Text.Length > 0 || _hintDetail.Text.Length > 0;
        _hintPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _emptyHitArea.Visibility = file == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>键盘快捷键:走共享手势表,未映射或本阶段不可达的命令不消费按键(INPUT-01)。</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var gesture = WindowsInput.FromKey(e.Key);
        if (gesture == null)
        {
            return;
        }

        var command = _main.Settings.GetCommandByGesture(gesture);
        if (command == null || WindowsIcons.ForCommand(command) == null)
        {
            return;
        }

        e.Handled = true;
        Execute(command);
    }

    /// <summary>主图区滚轮:交给共享手势表(默认上下滚轮翻页、Ctrl+滚轮缩放),不在壳里自定语义。</summary>
    private void OnPhotoWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_photoHost).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var gesture = WindowsInput.FromWheel(delta);
        var command = _main.Settings.GetCommandByGesture(gesture);
        if (command == null || WindowsIcons.ForCommand(command) == null)
        {
            return;
        }

        e.Handled = true;
        Execute(command);
    }

    /// <summary>拖放进入窗口:接受复制。</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    /// <summary>拖放落下:统一走外部打开流程。</summary>
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

            await OpenPathsAsync(paths);
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
        }
    }

    /// <summary>胶片条卡片评分。</summary>
    private Task RateFile(ImageFile file, int rating) => _main.SetRatingAsync(file, rating);

    /// <summary>按预设档位缩放(相对当前物理缩放取上/下一档)。</summary>
    private void ZoomPreset(int direction)
    {
        var presets = _main.Settings.ScalePresets.Select(preset => preset.Value).OrderBy(value => value).ToList();
        if (presets.Count == 0)
        {
            throw new InvalidOperationException("缩放预设为空,无法按档位缩放。");
        }

        var current = _photo.ZoomRatio;
        var target = direction > 0
            ? presets.FirstOrDefault(value => value > current + 1e-6, presets[^1])
            : presets.LastOrDefault(value => value < current - 1e-6, presets[0]);
        if (Math.Abs(target - current) < 1e-6)
        {
            return;
        }

        _photo.ZoomBy(target / current);
    }
}
