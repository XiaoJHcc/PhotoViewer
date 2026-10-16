using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 可换行排布的容器,语义对齐旧 Avalonia 的 <c>WrapPanel(ItemsAlignment=Center)</c>:
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

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var mainLimit = horizontal ? availableSize.Width : availableSize.Height;
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

        return horizontal
            ? new Size(mainUsed, crossUsed)
            : new Size(crossUsed, mainUsed);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var mainLimit = horizontal ? finalSize.Width : finalSize.Height;

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

        var cross = 0d;
        foreach (var line in lines)
        {
            double lineMainTotal = 0;
            double lineCross = 0;
            foreach (var child in line)
            {
                lineMainTotal += horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
                lineCross = Math.Max(lineCross, horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
            }

            lineMainTotal += ItemSpacing * (line.Count - 1);
            var main = Math.Max(0, (mainLimit - lineMainTotal) / 2);
            foreach (var child in line)
            {
                if (horizontal)
                {
                    var width = child.DesiredSize.Width;
                    child.Arrange(new Rect(main, cross, width, finalSize.Height - cross));
                    main += width + ItemSpacing;
                }
                else
                {
                    var height = child.DesiredSize.Height;
                    child.Arrange(new Rect(cross, main, finalSize.Width - cross, height));
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
}
