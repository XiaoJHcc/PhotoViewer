using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PhotoViewer.ViewModels;
using PhotoViewer.Views;

namespace PhotoViewer.Windows;

public partial class MainWindowForMac : Window
{
    private const double TitlebarRevealThreshold = 50;
    private const double TitlebarHeightVertical = 44;   // 上中下高度（可按需微调以获得三键上下等距的观感）
    private const double TitlebarHeightHorizontal = 24; // 左中右高度（同上）

    public MainWindowForMac()
    {
        InitializeComponent();

        // 左/右拖拽区
        var dragLeft = this.FindControl<Border>("DragZoneLeft");
        var dragRight = this.FindControl<Border>("DragZoneRight");

        void AttachDrag(Border? zone)
        {
            if (zone is null) return;
            zone.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(zone).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
        }

        AttachDrag(dragLeft);
        AttachDrag(dragRight);

        var titleBar = this.FindControl<Grid>("CustomTitleBar");
        if (titleBar is not null)
        {
            PointerMoved += (_, e) =>
            {
                var p = e.GetPosition(this);
                titleBar.IsVisible = p.Y <= TitlebarRevealThreshold;
                UpdateTitlebarLayout();
            };

            PointerExited += (_, _) => titleBar.IsVisible = false;
            Deactivated   += (_, _) => titleBar.IsVisible = false;

            this.GetObservable(BoundsProperty).Subscribe(_ => UpdateTitlebarLayout());

            Opened += (_, _) =>
            {
                TrySubscribeLayoutChanged();
                UpdateTitlebarLayout();
            };
        }
    }

    private void TrySubscribeLayoutChanged()
    {
        if (RootMainView?.DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsHorizontalLayout))
        {
            UpdateTitlebarLayout();
        }
    }

    // 核心：切换标题栏高度与避让策略
    private void UpdateTitlebarLayout()
    {
        var titleBar = this.FindControl<Grid>("CustomTitleBar");
        if (titleBar is null) return;

        var vm = RootMainView?.DataContext as MainViewModel;
        bool isHorizontal = vm?.IsHorizontalLayout ?? false;

        // 1) 动态标题栏高度（等距观感由高度控制）
        double targetHeight = isHorizontal ? TitlebarHeightHorizontal : TitlebarHeightVertical;
        if (Math.Abs(titleBar.Height - targetHeight) > 0.5)
        {
            titleBar.Height = targetHeight;
            ExtendClientAreaTitleBarHeightHint = targetHeight;
        }

        // 2) 中间避让策略
        if (isHorizontal)
        {
            // 左中右：不使用中间避让，通过调整左侧筛选条顶部边距避免与 24px 标题栏重叠
            var leftThumb  = RootMainView?.FindControl<ThumbnailView>("LeftThumbnailView");
            var filterBarL = leftThumb?.FindControl<StackPanel>("FilterBarPanel");
            if (filterBarL != null)
            {
                var m = filterBarL.Margin;
                double targetTop = 10 + TitlebarHeightHorizontal;
                if (Math.Abs(m.Top - targetTop) > 0.1)
                    filterBarL.Margin = new Thickness(m.Left, targetTop, m.Right, m.Bottom);
            }

            // 列宽：左拖拽 * | 中间 0 | 右拖拽 *
            CenterGap.Width = 0;
        }
        else
        {
            // 上中下：恢复顶部边距为 10，并启用中间避让（覆盖 TopThumbnailView 的筛选条）
            var topThumb  = RootMainView?.FindControl<ThumbnailView>("TopThumbnailView");
            var filterBarT = topThumb?.FindControl<StackPanel>("FilterBarPanel");
            if (filterBarT != null)
            {
                var m = filterBarT.Margin;
                if (Math.Abs(m.Top - 10) > 0.1)
                    filterBarT.Margin = new Thickness(m.Left, 10, m.Right, m.Bottom);
            }

            // 计算筛选条窗口坐标与宽度，设置列宽：左(=X) | 中(=Width) | 右(*)
            double gapWidth = 0;
            double leftX = 0;

            if (filterBarT != null && filterBarT.IsEffectivelyVisible)
            {
                var p = filterBarT.TranslatePoint(new Point(0, 0), this);
                if (p.HasValue)
                {
                    gapWidth = Math.Max(0, filterBarT.Bounds.Width);
                    leftX = Math.Max(0, p.Value.X);
                }
            }

            CenterGap.Width = gapWidth;
        }
    }
}