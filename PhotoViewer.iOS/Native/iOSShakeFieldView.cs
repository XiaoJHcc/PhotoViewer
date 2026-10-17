using CoreGraphics;
using PhotoViewer.Core.AI;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// v5 拖影矢量场原生控件（UIKit 版）：把 <see cref="ShakeField"/>（32×32）渲染成短线段。
/// 线段长度固定为格短边的 0.85×，方向取 drag_direction（拖影线方向，已 +π/2）；
/// 颜色由 R_local（方向一致性，主导色相）、drag_r（边宽量级，亮度调整）与对比度因子共同决定，
/// 配色实现统一在 <see cref="CvHeatmap.ColorForShake"/>。
/// 本控件不做 letterbox，直接铺满 <see cref="UIView.Bounds"/>（由父级瓦片提供已 letterbox 的内容矩形）；
/// 背景透明，让瓦片的"压暗原图"层透出来。与 Mac 原生 <c>MacShakeFieldView</c> 逐格同源。
/// </summary>
internal sealed class iOSShakeFieldView : UIView
{
    /// <summary>CV 网格边长（与共享 <see cref="CvGridResult.GridSize"/> 同源）。</summary>
    private const int Grid = CvGridResult.GridSize;

    /// <summary>矢量线段笔画宽度（点）。</summary>
    private const double StrokeWidth = 1.4;

    private ShakeField? _field;

    /// <summary>建立透明背景的绘制视图。</summary>
    public iOSShakeFieldView()
    {
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        UserInteractionEnabled = false;
        // 尺寸变化时按新格边长重绘（默认 ScaleToFill 会拉伸首次小图）。
        ContentMode = UIViewContentMode.Redraw;
    }

    /// <summary>抖动矢量场数据；null 时不绘制任何内容（背景保持透明）。</summary>
    internal ShakeField? ShakeField
    {
        get => _field;
        set
        {
            if (ReferenceEquals(_field, value)) return;
            _field = value;
            SetNeedsDisplay();
        }
    }

    /// <summary>
    /// 按网格中心绘制矢量线段：长度固定、颜色携带全部信息，背景不填充（底色由父级图像层决定）。
    /// 掩膜为假、读数 NaN、drag_r 低于显示阈值或线段过短（瓦片太小时）均不画。
    /// </summary>
    /// <param name="rect">脏矩形。</param>
    public override void Draw(CGRect rect)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var field = _field;
        if (field == null || field.Diagonal <= 0f) return;

        double cellWidth = (double)size.Width / Grid;
        double cellHeight = (double)size.Height / Grid;
        // 线段长度固定为格短边的 0.85×；颜色携带全部信息。
        double half = Math.Min(cellWidth, cellHeight) * 0.5 * 0.85;
        if (half < 0.5) return;

        var context = UIGraphics.GetCurrentContext();
        if (context == null) return;
        context.SetLineWidth((nfloat)StrokeWidth);
        float diagonal = field.Diagonal;
        for (int gy = 0; gy < Grid; gy++)
        {
            for (int gx = 0; gx < Grid; gx++)
            {
                int index = gy * Grid + gx;
                if (!field.Mask[index]) continue;
                float width = field.Width[index];
                float direction = field.Direction[index];
                if (float.IsNaN(width) || float.IsNaN(direction)) continue;

                float dragR = width / diagonal;
                if (dragR < CvHeatmap.DragRMinDisplay) continue;

                float rLocal = field.LocalConsistency.Length > index ? field.LocalConsistency[index] : float.NaN;
                float contrast = field.Contrast.Length > index ? CvHeatmap.ContrastFactor(field.Contrast[index]) : 1f;
                var (red, green, blue) = CvHeatmap.ColorForShake(dragR, rLocal, contrast);

                double centerX = (gx + 0.5) * cellWidth;
                double centerY = (gy + 0.5) * cellHeight;
                double offsetX = Math.Cos(direction) * half;
                double offsetY = Math.Sin(direction) * half;

                context.SetStrokeColor(UIColor.FromRGB(red, green, blue).CGColor);
                context.MoveTo((nfloat)(centerX - offsetX), (nfloat)(centerY - offsetY));
                context.AddLineToPoint((nfloat)(centerX + offsetX), (nfloat)(centerY + offsetY));
                context.StrokePath();
            }
        }
    }
}
