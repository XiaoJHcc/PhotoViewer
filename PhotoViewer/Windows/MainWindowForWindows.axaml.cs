using System;
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
    private bool _enableCustomTitleBar;
    private WindowState _prevStateBeforeFullScreen = WindowState.Normal;

    public MainWindowForWindows()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            _enableCustomTitleBar = true;
            PointerMoved += Window_PointerMoved;
            Deactivated += (_, _) => HideTitleBar();
            this.GetObservable(WindowStateProperty).Subscribe(_ => UpdateClientPadding());
            this.GetObservable(OffScreenMarginProperty).Subscribe(_ => UpdateClientPadding());

            // 绑定布局/尺寸变化，动态更新标题栏
            Opened += (_, _) =>
            {
                SetupTitleBarLayoutHooks();
                UpdateClientPadding();
            };
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
        var rootMainView = RootMainView;
        var rightButtonsHost = RightButtonsHost;
        if (rootMainView is null)
            return;

        var vm = rootMainView.DataContext as MainViewModel;
        bool isHorizontal = vm?.IsHorizontalLayout ?? false;
        var rightButtonsWidth = (rightButtonsHost?.Bounds.Width ?? 0) + (rightButtonsHost?.Margin.Right ?? 0);

        var leftHost   = rootMainView.FindControl<Border>("LeftThumbHost");
        // var topHost    = RootMainView.FindControl<Border>("TopThumbHost");
        // var leftThumb  = RootMainView.FindControl<ThumbnailView>("LeftThumbnailView");
        var topThumb   = rootMainView.FindControl<ThumbnailView>("TopThumbnailView");
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

            // 右侧拖拽区：避开真正可点击的按钮区域
            RightTitleBar.Margin = new Thickness(leftEffective, 0, rightButtonsWidth, 0);
            RightTitleBar.Width = double.NaN;
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

            // 右侧拖拽区：从中间空隙右边界到按钮左侧
            RightTitleBar.Margin = new Thickness(centerRight, 0, rightButtonsWidth, 0);
            RightTitleBar.Width = double.NaN;
        }
    }

    /// <summary>
    /// 使用 Avalonia 12 提供的屏外边距，避免 Windows 最大化时内容贴到被系统吞掉的边框区域。
    /// </summary>
    private void UpdateClientPadding()
    {
        Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default;
    }

    // 鼠标移动控制显示/隐藏
    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_enableCustomTitleBar) return;
        var p = e.GetPosition(this);

        // 标题栏显示/隐藏（使用动态热区高度）
        if (p.Y <= HotZoneHeight)
            ShowTitleBar();
        else
            HideTitleBar();
    }

    private void ShowTitleBar()
    {
        // 同时显示左右覆盖区
        if (LeftTitleBar.Opacity < 1 || RightTitleBar.Opacity < 1 || RightButtonsHost.Opacity < 1)
        {
            LeftTitleBar.IsHitTestVisible = true;
            RightTitleBar.IsHitTestVisible = true;
            LeftTitleBar.Opacity = 1;
            RightTitleBar.Opacity = 1;
            RightButtonsHost.Opacity = 1;
        }
    }

    private void HideTitleBar()
    {
        // 同时隐藏左右覆盖区
        if (LeftTitleBar.Opacity > 0 || RightTitleBar.Opacity > 0 || RightButtonsHost.Opacity > 0)
        {
            LeftTitleBar.IsHitTestVisible = false;
            RightTitleBar.IsHitTestVisible = false;
            LeftTitleBar.Opacity = 0;
            RightTitleBar.Opacity = 0;
            RightButtonsHost.Opacity = 0;
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

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}