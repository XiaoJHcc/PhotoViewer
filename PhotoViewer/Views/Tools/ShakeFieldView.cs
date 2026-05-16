using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhotoViewer.Core.AI;

namespace PhotoViewer.Views.Tools;

/// <summary>
/// v4 拖影矢量场可视化控件:把 ShakeField (32×32) 渲染成 Canvas 风格的短线段。
/// 线段长度固定(cellHalf × 0.85),方向 = drag_direction(拖影线方向,已 +π/2);
/// 颜色按 drag_r = drag_width / Diagonal 取 6 段,亮度峰值落在 sweet spot(~15 px @ 6000)。
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

    /// <summary>渲染:按网格中心画矢量线段(长度固定,颜色按 drag_r 分段);不再绘制背景色。</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var field = ShakeField;
        if (field == null || field.Diagonal <= 0f) return;

        double cellW = size.Width / Grid;
        double cellH = size.Height / Grid;
        // 线段长度固定为格短边的 0.85×(不再随 spread 变化);颜色携带全部信息。
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

                Color stroke = ColorForDragR(dragR);

                double cx = (gx + 0.5) * cellW;
                double cy = (gy + 0.5) * cellH;
                double hx = Math.Cos(dir) * half;
                double hy = Math.Sin(dir) * half;

                var pen = new Pen(new SolidColorBrush(stroke), 1.4);
                context.DrawLine(pen, new Point(cx - hx, cy - hy), new Point(cx + hx, cy + hy));
            }
        }
    }

    /// <summary>
    /// drag_r → 颜色映射,三色渐变:黑(无信号)→ 橙红(疑似抖动峰值)→ 白(拖影过长 / 建筑结构)。
    /// 单标量做不到"抖 vs 结构"的判别,颜色只表达 drag_width 数量级;真要分类需要叠加残差对齐。
    /// </summary>
    private static Color ColorForDragR(float dragR)
    {
        // 段定义(r2 三色 2026-05-16):
        // [0.033%, 0.06%) 黑 → 暗红              亮度上升
        // [0.06%,  0.10%) 暗红 → 橙红            **亮度峰值**(疑似抖动)
        // [0.10%,  0.18%) 橙红 → 浅橙             亮度回落
        // [0.18%,  0.30%) 浅橙 → 接近白           走向"结构"
        // [0.30%,  ∞)     纯白                    长结构线 / 建筑本身
        const float B0 = 0.00033f;  // 不画下限(外面已挡,这里保护)
        const float B1 = 0.0006f;   // 暗红
        const float B2 = 0.0010f;   // 橙红峰
        const float B3 = 0.0018f;   // 浅橙
        const float B4 = 0.0030f;   // 接近白
        // > B4 → 纯白

        var c0 = (R: 0,   G: 0,   B: 0);     // 黑
        var c1 = (R: 90,  G: 25,  B: 15);    // 暗红
        var c2 = (R: 255, G: 80,  B: 30);    // 橙红(亮度峰)
        var c3 = (R: 255, G: 170, B: 130);   // 浅橙
        var c4 = (R: 240, G: 230, B: 220);   // 接近白
        var c5 = (R: 255, G: 255, B: 255);   // 纯白

        if (dragR < B1) return Lerp(c0, c1, (dragR - B0) / (B1 - B0));
        if (dragR < B2) return Lerp(c1, c2, (dragR - B1) / (B2 - B1));
        if (dragR < B3) return Lerp(c2, c3, (dragR - B2) / (B3 - B2));
        if (dragR < B4) return Lerp(c3, c4, (dragR - B3) / (B4 - B3));
        return ToColor(c5);
    }

    private static Color Lerp((int R, int G, int B) a, (int R, int G, int B) b, float t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bb = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bb);
    }

    private static Color ToColor((int R, int G, int B) c)
        => Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B);
}
