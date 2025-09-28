using System;
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
    private bool _enableCustomTitleBar;
    private CancellationTokenSource? _hideCts;
    private WindowState _prevStateBeforeFullScreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            _enableCustomTitleBar = true;
            PointerMoved += Window_PointerMoved;
            Deactivated += (_, _) => TryHideSoon(); // 失焦尽快隐藏
        }
    }

    // 鼠标移动控制显示/隐藏
    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        var p = e.GetPosition(this);
        // 靠近顶部
        if (p.Y <= 50)
        {
            ShowTitleBar();
        }
        else
        {
            // 超出标题栏 + 缓冲
            if (CustomTitleBar.Opacity > 0 && p.Y > CustomTitleBar.Bounds.Height + 10)
                TryHideSoon();
        }
    }

    private void ShowTitleBar()
    {
        _hideCts?.Cancel();
        if (CustomTitleBar.Opacity < 1)
        {
            CustomTitleBar.IsHitTestVisible = true;
            CustomTitleBar.Opacity = 1;
        }
    }

    private void TryHideSoon(int delayMs = 400)
    {
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        var token = _hideCts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    CustomTitleBar.IsHitTestVisible = false;
                    CustomTitleBar.Opacity = 0;
                });
            }
            catch (TaskCanceledException) { /* ignore */ }
        }, token);
    }

    // 拖拽窗口
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void BtnMin_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else if (WindowState == WindowState.Normal)
            WindowState = WindowState.Maximized;
        else if (WindowState == WindowState.FullScreen)
        {
            // 在全屏中点击最大化 => 退出全屏并还原/最大化逻辑
            WindowState = _prevStateBeforeFullScreen == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }
    }

    private void BtnFull_Click(object? sender, RoutedEventArgs e)
    {
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

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}