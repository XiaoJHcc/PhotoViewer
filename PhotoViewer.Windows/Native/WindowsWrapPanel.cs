using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 可换行排布的容器,语义对齐旧壳的 <c>WrapPanel(ItemsAlignment=Center)</c>:
/// 一行放不下就整项换到下一行,行内居中,行间距与项间距分别可配。
/// WinUI 没有现成的 WrapPanel,控制栏三分组的换行/换列居中依赖这个容器。
/// </summary>
internal sealed class WindowsWrapPanel : Panel
{
    /// <summary>排布方向依赖属性。</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(WindowsWrapPanel),
        new PropertyMetadata(Orientation.Horizontal, OnLayoutPropertyChanged));

    /// <summary>项间距依赖属性。</summary>
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(WindowsWrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    /// <summary>行间距依赖属性。</summary>
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WindowsWrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    /// <summary>主轴换行上限依赖属性；<see cref="double.PositiveInfinity"/> 表示不限制。</summary>
    public static readonly DependencyProperty MainLimitProperty = DependencyProperty.Register(
        nameof(MainLimit), typeof(double), typeof(WindowsWrapPanel),
        new PropertyMetadata(double.PositiveInfinity, OnLayoutPropertyChanged));

    /// <summary>排布方向;Vertical 表示各组纵向换列(对应上下布局)。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>同一行(列)内相邻项的间距。</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>行(列)之间的间距。</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>
    /// 主轴换行上限（逻辑点）。滚动宿主测量内容时会给出无限主轴，
    /// 此时用视口主轴作上限，才能按组换行而不是把整条内容交给滚动。
    /// </summary>
    public double MainLimit
    {
        get => (double)GetValue(MainLimitProperty);
        set => SetValue(MainLimitProperty, value);
    }

    /// <summary>最近一次排布的行（列）数，供控制栏验收核对换行确实发生。</summary>
    internal int LineCount { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var mainLimit = EffectiveMainLimit(horizontal ? availableSize.Width : availableSize.Height);
        double mainUsed = 0;
        double crossUsed = 0;
        double lineMain = 0;
        double lineCross = 0;
        var first = true;

        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var childMain = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var childCross = horizontal ? child.DesiredSize.Height : child.DesiredSize.Width;

            var next = first ? childMain : lineMain + ItemSpacing + childMain;
            if (!first && !double.IsInfinity(mainLimit) && next > mainLimit)
            {
                mainUsed = Math.Max(mainUsed, lineMain);
                crossUsed += lineCross + LineSpacing;
                lineMain = childMain;
                lineCross = childCross;
            }
            else
            {
                lineMain = next;
                lineCross = Math.Max(lineCross, childCross);
                first = false;
            }
        }

        mainUsed = Math.Max(mainUsed, lineMain);
        crossUsed += lineCross;

        // 内容能换行容纳时把主轴铺满视口（行内容居中且不产生滚动）；
        // 单个组超长时报告内容长度，让滚动宿主有可滚范围。主轴上限未知时只报告内容长度。
        var reportedMain = double.IsInfinity(mainLimit) ? mainUsed : Math.Max(mainUsed, mainLimit);
        LineCount = CountLines(horizontal, mainLimit);

        return horizontal
            ? new Size(reportedMain, crossUsed)
            : new Size(crossUsed, reportedMain);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var mainLimit = EffectiveMainLimit(horizontal ? finalSize.Width : finalSize.Height);

        // 先按最终尺寸把项分行,再逐行居中排布(旧壳是"整行居中",不是整体居中)
        var lines = new List<List<UIElement>>();
        var lineMain = 0d;
        var current = new List<UIElement>();
        foreach (var child in Children)
        {
            var childMain = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var next = current.Count == 0 ? childMain : lineMain + ItemSpacing + childMain;
            if (current.Count > 0 && next > mainLimit)
            {
                lines.Add(current);
                current = new List<UIElement> { child };
                lineMain = childMain;
            }
            else
            {
                current.Add(child);
                lineMain = next;
            }
        }

        if (current.Count > 0)
        {
            lines.Add(current);
        }

        // 居中按视口主轴计算，不按可能被单个超长组撑大的文档主轴。
        var availableMain = horizontal ? finalSize.Width : finalSize.Height;
        var centerLimit = double.IsInfinity(mainLimit) ? availableMain : Math.Min(mainLimit, availableMain);

        var cross = 0d;
        foreach (var line in lines)
        {
            // 先量出本行(列)的主轴总长与交叉轴最大期望尺寸。
            // 交叉轴尺寸必须取行内最大值:若沿用"到最终尺寸末尾的剩余空间",
            // 换行后前一行的子项(如带底色的评分分组)会一直铺到后续行(列)上。
            double lineMainTotal = 0;
            double lineCross = 0;
            foreach (var child in line)
            {
                lineMainTotal += horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
                lineCross = Math.Max(lineCross, horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
            }

            lineMainTotal += ItemSpacing * (line.Count - 1);
            var main = Math.Max(0, (centerLimit - lineMainTotal) / 2);
            foreach (var child in line)
            {
                if (horizontal)
                {
                    var width = child.DesiredSize.Width;
                    child.Arrange(new Rect(main, cross, width, lineCross));
                    main += width + ItemSpacing;
                }
                else
                {
                    var height = child.DesiredSize.Height;
                    child.Arrange(new Rect(cross, main, lineCross, height));
                    main += height + ItemSpacing;
                }
            }

            cross += lineCross + LineSpacing;
        }

        return finalSize;
    }

    /// <summary>布局相关属性变化时重排。</summary>
    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WindowsWrapPanel)d).InvalidateMeasure();

    /// <summary>主轴换行上限：取可用主轴与视口上限的较小者；未知（≤0）时视为不限制。</summary>
    /// <param name="availableMain">宿主给出的可用主轴长度。</param>
    /// <returns>有效主轴上限。</returns>
    private double EffectiveMainLimit(double availableMain)
    {
        var limit = Math.Min(availableMain, MainLimit);
        if (double.IsInfinity(limit) || double.IsNaN(limit))
        {
            return double.PositiveInfinity;
        }

        return limit < 1 ? double.PositiveInfinity : limit;
    }

    /// <summary>按当前子项尺寸与间距统计行（列）数；子项尚未测量时返回 1。</summary>
    /// <param name="horizontal">是否横排。</param>
    /// <param name="limit">主轴上限。</param>
    /// <returns>行（列）数。</returns>
    private int CountLines(bool horizontal, double limit)
    {
        int lines = 1;
        double lineMain = 0;
        var first = true;
        foreach (var child in Children)
        {
            var childMain = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var next = first ? childMain : lineMain + ItemSpacing + childMain;
            if (!first && !double.IsInfinity(limit) && next > limit)
            {
                lines++;
                lineMain = childMain;
            }
            else
            {
                lineMain = next;
                first = false;
            }
        }

        return lines;
    }
}
