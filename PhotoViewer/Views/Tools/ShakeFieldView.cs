using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhotoViewer.Core.AI;

namespace PhotoViewer.Views.Tools;

/// <summary>
/// v2 抖动矢量场可视化控件：把 ShakeField (16×16) 渲染成 Canvas 风格的短线段。
/// 方向 = shake_direction(rad)，长度 = shake_spread(px) 按 10 px 顶满 cellHalf 缩放，浓度也同步 spread。
/// Mask = false 的格子不绘，保持视觉干净。
/// </summary>
public sealed class ShakeFieldView : Control
{
    private const int Grid = CvGridResult.GridSize;
    private const float SpreadMaxForFullLength = 10f;

    /// <summary>抖动矢量场数据；null 时控件仅绘一个暗背景。</summary>
    public static readonly StyledProperty<ShakeField?> ShakeFieldProperty =
        AvaloniaProperty.Register<ShakeFieldView, ShakeField?>(nameof(ShakeField));

    /// <summary>源图长宽比 (宽/高)，用于让矢量场网格按图像比例铺开；默认 1。</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<ShakeFieldView, double>(nameof(AspectRatio), 1.0);

    /// <summary>抖动矢量场数据。</summary>
    public ShakeField? ShakeField
    {
        get => GetValue(ShakeFieldProperty);
        set => SetValue(ShakeFieldProperty, value);
    }

    /// <summary>源图长宽比。</summary>
    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>构造控件，数据变化时强制重绘。</summary>
    public ShakeFieldView()
    {
        ClipToBounds = true;
        AffectsRender<ShakeFieldView>(ShakeFieldProperty, AspectRatioProperty);
    }

    /// <summary>渲染：先铺深色背景，再按网格中心画矢量线段。</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var bg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        context.FillRectangle(bg, new Rect(0, 0, size.Width, size.Height));

        var field = ShakeField;
        if (field == null) return;

        double ratio = AspectRatio > 0 ? AspectRatio : 1.0;
        // 按长宽比把矢量场区域居中铺开。
        double boxW = size.Width;
        double boxH = size.Width / ratio;
        if (boxH > size.Height)
        {
            boxH = size.Height;
            boxW = boxH * ratio;
        }
        double ox = (size.Width - boxW) / 2;
        double oy = (size.Height - boxH) / 2;

        double cellW = boxW / Grid;
        double cellH = boxH / Grid;
        double half = Math.Min(cellW, cellH) * 0.5 * 0.9;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xF8, 0xE0, 0x60)), 1.4);
        for (int gy = 0; gy < Grid; gy++)
        {
            for (int gx = 0; gx < Grid; gx++)
            {
                int i = gy * Grid + gx;
                if (!field.Mask[i]) continue;
                float spread = field.Spread[i];
                float dir = field.Direction[i];
                if (float.IsNaN(spread) || float.IsNaN(dir)) continue;

                double cx = ox + (gx + 0.5) * cellW;
                double cy = oy + (gy + 0.5) * cellH;

                double scale = Math.Clamp(spread / SpreadMaxForFullLength, 0, 1);
                double len = half * scale;
                if (len < 1.5) continue;

                double hx = Math.Cos(dir) * len;
                double hy = Math.Sin(dir) * len;
                byte alpha = (byte)(60 + 195 * scale);
                var strokeColor = Color.FromArgb(alpha, 0xF8, 0xE0, 0x60);
                pen = new Pen(new SolidColorBrush(strokeColor), 1.4);
                context.DrawLine(pen, new Point(cx - hx, cy - hy), new Point(cx + hx, cy + hy));
            }
        }
    }
}
