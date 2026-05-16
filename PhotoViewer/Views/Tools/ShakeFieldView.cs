using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhotoViewer.Core.AI;

namespace PhotoViewer.Views.Tools;

/// <summary>
/// v5 拖影矢量场可视化控件:把 ShakeField (32×32) 渲染成 Canvas 风格的短线段。
/// 线段长度固定(cellHalf × 0.85),方向 = drag_direction(拖影线方向,已 +π/2);
/// 颜色由 R_local(方向一致性,主导色相)× drag_r(边宽量级,亮度调整)共同决定;
/// 配色实现统一在 CvHeatmap.ColorForShake,View 与 CvDebugTool 同步。
/// 本控件不再自己做长宽比 letterbox(由父级 DiagnosticTile 提供已 letterbox 的矩形),
/// 直接铺满 Bounds 即可。背景透明,让 DiagnosticTile 的"压暗原图"层透出来。
/// </summary>
public sealed class ShakeFieldView : Control
{
    private const int Grid = CvGridResult.GridSize;

    /// <summary>抖动矢量场数据;null 时不绘制任何内容(背景透明)。</summary>
    public static readonly StyledProperty<ShakeField?> ShakeFieldProperty =
        AvaloniaProperty.Register<ShakeFieldView, ShakeField?>(nameof(ShakeField));

    /// <summary>抖动矢量场数据。</summary>
    public ShakeField? ShakeField
    {
        get => GetValue(ShakeFieldProperty);
        set => SetValue(ShakeFieldProperty, value);
    }

    /// <summary>构造控件,数据变化时强制重绘。</summary>
    public ShakeFieldView()
    {
        ClipToBounds = true;
        AffectsRender<ShakeFieldView>(ShakeFieldProperty);
    }

    /// <summary>渲染:按网格中心画矢量线段(长度固定,颜色由 CvHeatmap.ColorForShake 决定);不再绘制背景色。</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var field = ShakeField;
        if (field == null || field.Diagonal <= 0f) return;

        double cellW = size.Width / Grid;
        double cellH = size.Height / Grid;
        // 线段长度固定为格短边的 0.85×;颜色携带全部信息。
        double half = Math.Min(cellW, cellH) * 0.5 * 0.85;
        if (half < 0.5) return;

        float diag = field.Diagonal;
        for (int gy = 0; gy < Grid; gy++)
        {
            for (int gx = 0; gx < Grid; gx++)
            {
                int i = gy * Grid + gx;
                if (!field.Mask[i]) continue;
                float wpx = field.Width[i];
                float dir = field.Direction[i];
                if (float.IsNaN(wpx) || float.IsNaN(dir)) continue;

                float dragR = wpx / diag;
                if (dragR < CvHeatmap.DragRMinDisplay) continue;

                float rLocal = field.LocalConsistency.Length > i ? field.LocalConsistency[i] : float.NaN;
                float cf = field.Contrast.Length > i ? CvHeatmap.ContrastFactor(field.Contrast[i]) : 1f;
                var (cr, cg, cb) = CvHeatmap.ColorForShake(dragR, rLocal, cf);
                var stroke = Color.FromRgb(cr, cg, cb);

                double cx = (gx + 0.5) * cellW;
                double cy = (gy + 0.5) * cellH;
                double hx = Math.Cos(dir) * half;
                double hy = Math.Sin(dir) * half;

                var pen = new Pen(new SolidColorBrush(stroke), 1.4);
                context.DrawLine(pen, new Point(cx - hx, cy - hy), new Point(cx + hx, cy + hy));
            }
        }
    }
}
