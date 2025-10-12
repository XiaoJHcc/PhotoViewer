using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReactiveUI;
using PhotoViewer.Views;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Windows;

public partial class MainWindowForWindows : Window
{
    private const int HotZoneHeight = 50;
    private const int ResizeBorderThickness = 6;
    private bool _enableCustomTitleBar;
    private WindowState _prevStateBeforeFullScreen = WindowState.Normal;
    private Thickness _normalPadding = new Thickness(0);
    private WindowEdge? _currentResizeEdge;
    private CancellationTokenSource? _snapHoverCts;

    public MainWindowForWindows()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            _enableCustomTitleBar = true;
            PointerMoved += Window_PointerMoved;
            PointerPressed += Window_PointerPressedForResize;
            Deactivated += (_, _) => HideTitleBar();
            this.GetObservable(WindowStateProperty).Subscribe(OnWindowStateChanged);
            _normalPadding = Padding;

            // 绑定布局/尺寸变化，动态更新标题栏
            Opened += (_, _) => SetupTitleBarLayoutHooks();
        }
    }

    private void SetupTitleBarLayoutHooks()
    {
        try
        {
            if (RootMainView?.DataContext is MainViewModel vm)
            {
                // 监听布局切换
                vm.WhenAnyValue(x => x.IsHorizontalLayout)
                  .Subscribe(_ => Dispatcher.UIThread.Post(UpdateTitleBarLayout));

                // 监听左右/上下缩略图容器尺寸变化
                var leftHost = RootMainView.FindControl<Border>("LeftThumbHost");
                var topHost  = RootMainView.FindControl<Border>("TopThumbHost");
                leftHost?.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitleBarLayout());
                topHost?.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitleBarLayout());

                // 监听筛选栏尺寸变化（上下、左右各一套）
                var leftThumb = RootMainView.FindControl<ThumbnailView>("LeftThumbnailView");
                var topThumb  = RootMainView.FindControl<ThumbnailView>("TopThumbnailView");
                var leftFilter = leftThumb?.FindControl<StackPanel>("FilterBarPanel");
                var topFilter  = topThumb?.FindControl<StackPanel>("FilterBarPanel");

                leftFilter?.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitleBarLayout());
                topFilter?.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitleBarLayout());
            }

            // 监听窗口尺寸变化
            this.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitleBarLayout());

            UpdateTitleBarLayout();
        }
        catch
        {
            // 忽略装配期间的空引用
        }
    }

    private void UpdateTitleBarLayout()
    {
        var vm = RootMainView.DataContext as MainViewModel;
        bool isHorizontal = vm?.IsHorizontalLayout ?? false;

        var leftHost   = RootMainView.FindControl<Border>("LeftThumbHost");
        // var topHost    = RootMainView.FindControl<Border>("TopThumbHost");
        // var leftThumb  = RootMainView.FindControl<ThumbnailView>("LeftThumbnailView");
        var topThumb   = RootMainView.FindControl<ThumbnailView>("TopThumbnailView");
        // var leftFilter = leftThumb?.FindControl<StackPanel>("FilterBarPanel");
        var topFilter  = topThumb?.FindControl<StackPanel>("FilterBarPanel");

        double winWidth = Bounds.Width > 0 ? Bounds.Width : 0;

        if (isHorizontal)
        {
            // 左右布局：标题栏避开左侧缩略图宽度（扣除为边缘填充使用的左 Padding）
            var leftWidthRaw = leftHost?.Bounds.Width ?? 0;
            var leftPad = leftHost?.Padding.Left ?? 0;
            var leftEffective = Math.Max(0, leftWidthRaw - leftPad);

            // 左侧覆盖区：整体右移，覆盖剩余区域
            LeftTitleBar.Margin = new Thickness(leftEffective, 0, 0, 0);
            LeftTitleBar.Width = Math.Max(0, winWidth - leftEffective);

            // 右侧覆盖区：靠右，自动宽度（按钮）即可
            RightTitleBar.Margin = new Thickness(0);
            // 在左右布局下，不需要留中间空隙
            RightTitleBar.Width = double.NaN; // Auto
        }
        else
        {
            // 上下布局：标题栏高度 = 筛选栏高度；留出中间筛选栏的完整穿透空隙
            var filterWidth  = topFilter?.Bounds.Width  ?? 0;

            // 计算中间筛选栏在窗口中的左右边界（居中）
            var centerLeft = Math.Max(0, (winWidth - filterWidth) / 2);
            var centerRight = Math.Max(centerLeft, centerLeft + filterWidth);

            // 左侧覆盖区：从0到中间空隙左边界
            LeftTitleBar.Margin = new Thickness(0, 0, 0, 0);
            LeftTitleBar.Width = Math.Max(0, centerLeft);

            // 右侧覆盖区：从中间空隙右边界到窗口右侧
            RightTitleBar.Margin = new Thickness(0, 0, 0, 0);
            RightTitleBar.Width = Math.Max(0, winWidth - centerRight);
        }
    }

    private void OnWindowStateChanged(WindowState state)
    {
        if (state == WindowState.Maximized)
            Padding = new Thickness(0);
        else if (state == WindowState.Normal)
            Padding = _normalPadding;
    }

    // 鼠标移动控制显示/隐藏
    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        var p = e.GetPosition(this);

        // 先处理缩放光标
        UpdateResizeCursor(p);

        // 标题栏显示/隐藏（使用动态热区高度）
        if (p.Y <= HotZoneHeight)
            ShowTitleBar();
        else
            HideTitleBar();
    }

    private void UpdateResizeCursor(Point p)
    {
        if (WindowState != WindowState.Normal)
        {
            if (_currentResizeEdge != null)
            {
                _currentResizeEdge = null;
                Cursor = new Cursor(StandardCursorType.Arrow);
            }
            return;
        }

        bool left = p.X <= ResizeBorderThickness;
        bool right = p.X >= Bounds.Width - ResizeBorderThickness;
        bool top = p.Y <= ResizeBorderThickness;
        bool bottom = p.Y >= Bounds.Height - ResizeBorderThickness;

        WindowEdge? edge = null;
        StandardCursorType cursorType = StandardCursorType.Arrow;

        if (top && left) { edge = WindowEdge.NorthWest; cursorType = StandardCursorType.TopLeftCorner; }
        else if (top && right) { edge = WindowEdge.NorthEast; cursorType = StandardCursorType.TopRightCorner; }
        else if (bottom && left) { edge = WindowEdge.SouthWest; cursorType = StandardCursorType.BottomLeftCorner; }
        else if (bottom && right) { edge = WindowEdge.SouthEast; cursorType = StandardCursorType.BottomRightCorner; }
        else if (top) { edge = WindowEdge.North; cursorType = StandardCursorType.TopSide; }
        else if (bottom) { edge = WindowEdge.South; cursorType = StandardCursorType.BottomSide; }
        else if (left) { edge = WindowEdge.West; cursorType = StandardCursorType.LeftSide; }
        else if (right) { edge = WindowEdge.East; cursorType = StandardCursorType.RightSide; }

        _currentResizeEdge = edge;
        Cursor = edge == null ? new Cursor(StandardCursorType.Arrow) : new Cursor(cursorType);
    }

    private void Window_PointerPressedForResize(object? sender, PointerPressedEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            _currentResizeEdge.HasValue &&
            WindowState == WindowState.Normal)
        {
            BeginResizeDrag(_currentResizeEdge.Value, e);
        }
    }

    private void ShowTitleBar()
    {
        // 同时显示左右覆盖区
        if (LeftTitleBar.Opacity < 1 || RightTitleBar.Opacity < 1)
        {
            LeftTitleBar.IsHitTestVisible = true;
            RightTitleBar.IsHitTestVisible = true;
            LeftTitleBar.Opacity = 1;
            RightTitleBar.Opacity = 1;
        }
    }

    private void HideTitleBar()
    {
        // 同时隐藏左右覆盖区
        if (LeftTitleBar.Opacity > 0 || RightTitleBar.Opacity > 0)
        {
            LeftTitleBar.IsHitTestVisible = false;
            RightTitleBar.IsHitTestVisible = false;
            LeftTitleBar.Opacity = 0;
            RightTitleBar.Opacity = 0;
        }
    }

    // 拖拽窗口（非缩放状态且左键）；若命中按钮则不触发拖拽
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        if (_currentResizeEdge != null) return; // 正在缩放边缘
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        BeginMoveDrag(e);
    }

    private void BtnMin_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object? sender, RoutedEventArgs e)
    {
        _snapHoverCts?.Cancel();
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else if (WindowState == WindowState.Normal)
            WindowState = WindowState.Maximized;
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = _prevStateBeforeFullScreen == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }
    }

    private void BtnFull_Click(object? sender, RoutedEventArgs e)
    {
        _snapHoverCts?.Cancel();
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _prevStateBeforeFullScreen;
        }
        else
        {
            _prevStateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        _snapHoverCts?.Cancel();
        Close();
    }
}