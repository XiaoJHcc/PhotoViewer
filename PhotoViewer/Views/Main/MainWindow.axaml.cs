using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PhotoViewer.Views;

public partial class MainWindow : Window
{
    private const int HotZoneHeight = 50;
    private const int ResizeBorderThickness = 6;
    private bool _enableCustomTitleBar;
    private WindowState _prevStateBeforeFullScreen = WindowState.Normal;
    private Thickness _normalPadding = new Thickness(0);
    private WindowEdge? _currentResizeEdge;
    private CancellationTokenSource? _snapHoverCts;

    public MainWindow()
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
        }
    }

    private void OnWindowStateChanged(WindowState state)
    {
        if (state == WindowState.Maximized)
            Padding = new Thickness(0); // 去除间隙
        else if (state == WindowState.Normal)
            Padding = _normalPadding;
    }

    // 鼠标移动控制显示/隐藏 (进入 50 像素显示, 离开立即隐藏)
    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        var p = e.GetPosition(this);

        // 先处理缩放光标
        UpdateResizeCursor(p);

        // 标题栏显示/隐藏
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
        if (CustomTitleBar.Opacity < 1)
        {
            CustomTitleBar.IsHitTestVisible = true;
            CustomTitleBar.Opacity = 1;
        }
    }

    private void HideTitleBar()
    {
        if (CustomTitleBar.Opacity > 0)
        {
            CustomTitleBar.IsHitTestVisible = false;
            CustomTitleBar.Opacity = 0;
        }
    }

    // 拖拽窗口（非缩放状态且左键）
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        if (_currentResizeEdge != null) return; // 正在缩放边缘
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
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