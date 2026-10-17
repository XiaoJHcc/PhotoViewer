using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.Core.Platform;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 分析栏瓦片容器:把全部子项排成等边长的正方形条带(旧壳 StackPanel + 均分宽度的语义)。
///
/// 边长完全由布局系统给出的可用尺寸决定(MeasureOverride/ArrangeOverride 里算),
/// 不再走"读 ActualWidth → 推迟到下一轮 → 回写子项 Width/Height"的老路 ——
/// 那条路只要偶发地赶在布局之前读到 0 尺寸,瓦片就会退回各自内容尺寸(观察到的"挤成一坨、大小错位")。
/// 主轴取可用尺寸(上下布局取宽,左右布局取高),另一向若有限则也参与钳制,保证瓦片不超出栏位。
/// </summary>
internal sealed class WindowsAnalysisTilePanel : Panel
{
    /// <summary>排布方向依赖属性。</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(WindowsAnalysisTilePanel),
        new PropertyMetadata(Orientation.Horizontal, OnLayoutPropertyChanged));

    /// <summary>相邻瓦片间距依赖属性。</summary>
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(WindowsAnalysisTilePanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    private double _side;

    /// <summary>排布方向;Horizontal 表示瓦片横排(上下布局),Vertical 表示竖排(左右布局)。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>相邻瓦片间距。</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>最近一次布局算出的瓦片边长(逻辑单位)。</summary>
    internal double Side => _side;

    /// <summary>边长变化回调(已推迟到布局之外,供细节预览按新边长重新裁剪)。</summary>
    internal Action<double>? SideChanged { get; set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            _side = 0;
            return new Size(0, 0);
        }

        double side = ResolveSide(availableSize, count);
        var constraint = new Size(side, side);
        foreach (var child in Children)
        {
            child.Measure(constraint);
        }

        NotifySide(side);
        double span = (side * count) + (ItemSpacing * (count - 1));
        return Orientation == Orientation.Horizontal ? new Size(span, side) : new Size(side, span);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            _side = 0;
            return finalSize;
        }

        double side = ResolveSide(finalSize, count);
        NotifySide(side);
        double span = (side * count) + (ItemSpacing * (count - 1));
        bool horizontal = Orientation == Orientation.Horizontal;
        double x = horizontal ? (finalSize.Width - span) / 2 : (finalSize.Width - side) / 2;
        double y = horizontal ? (finalSize.Height - side) / 2 : (finalSize.Height - span) / 2;

        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, y, side, side));
            if (horizontal)
            {
                x += side + ItemSpacing;
            }
            else
            {
                y += side + ItemSpacing;
            }
        }

        return finalSize;
    }

    /// <summary>按可用尺寸解出均分后的正方形边长。</summary>
    /// <param name="available">可用尺寸(主轴可能有限,另一向在自动尺寸布局里是无穷)</param>
    /// <param name="count">瓦片数量</param>
    /// <returns>边长;无法排布时为 0</returns>
    private double ResolveSide(Size available, int count)
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        double along = horizontal ? available.Width : available.Height;
        double across = horizontal ? available.Height : available.Width;

        if (!IsFinite(along))
        {
            along = IsFinite(across) ? across : 0;
        }

        if (along <= 0)
        {
            return 0;
        }

        double usable = along - (ItemSpacing * (count - 1));
        double side = Math.Floor(usable / count);
        if (IsFinite(across))
        {
            // 另一向有限时(如左右布局的栏宽、或已排定高度)瓦片不得超出该向。
            side = Math.Min(side, Math.Floor(across));
        }

        return side > 0 ? side : 0;
    }

    /// <summary>是否为有限的正数尺寸。</summary>
    /// <param name="value">待判断的尺寸</param>
    /// <returns>有限且非 NaN 时为 true</returns>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>边长变化时推迟到布局之外通知(P0:布局回调里不碰位图与裁剪)。</summary>
    /// <param name="side">新边长</param>
    private void NotifySide(double side)
    {
        if (Math.Abs(side - _side) < 0.5)
        {
            return;
        }

        _side = side;
        var handler = SideChanged;
        if (handler != null)
        {
            UiDispatcher.Post(() => handler(_side), UiPriority.Background);
        }
    }

    /// <summary>布局属性变化:重新测量。</summary>
    /// <param name="sender">属性所属对象</param>
    /// <param name="args">属性变化参数</param>
    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((WindowsAnalysisTilePanel)sender).InvalidateMeasure();
    }
}
