using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoViewer.Core.AI;
using Windows.UI;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// v5 拖影矢量场原生控件:把 <see cref="ShakeField"/>(32×32)渲染成短线段。
/// 线段长度固定为格短边的 0.85×,方向取 drag_direction;
/// 颜色由 R_local、drag_r 与对比度因子共同决定,配色统一走 <see cref="CvHeatmap.ColorForShake"/>。
/// 控件铺满诊断瓦片的内容矩形,透明背景让瓦片的压暗原图透出来。
/// </summary>
internal sealed class WindowsShakeFieldView : Grid
{
    /// <summary>CV 网格边长(与共享 <see cref="CvGridResult.GridSize"/> 同源)。</summary>
    private const int Grid = CvGridResult.GridSize;

    /// <summary>矢量线段笔画宽度(逻辑点)。</summary>
    private const double StrokeWidth = 1.4;

    /// <summary>承载线段的画布;不参与命中测试。</summary>
    private readonly Canvas _canvas = new();

    private ShakeField? _field;
    private bool _renderPending;

    /// <summary>创建矢量场控件。</summary>
    public WindowsShakeFieldView()
    {
        IsHitTestVisible = false;
        Children.Add(_canvas);
        SizeChanged += (_, _) => ScheduleRender();
    }

    /// <summary>是否已经载入矢量场数据(验收用)。</summary>
    public bool HasField => _field != null;

    /// <summary>抖动矢量场数据;null 时不绘制任何内容。</summary>
    public ShakeField? ShakeField
    {
        get => _field;
        set
        {
            if (ReferenceEquals(_field, value))
            {
                return;
            }

            _field = value;
            Render();
        }
    }

    /// <summary>尺寸变化时合并到一次 dispatcher turn 内重画,避免拖动窗口时反复创建上千条线。</summary>
    private void ScheduleRender()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;
        PhotoViewer.Core.Platform.UiDispatcher.Post(() =>
        {
            _renderPending = false;
            Render();
        });
    }

    /// <summary>
    /// 按网格中心绘制矢量线段:长度固定、颜色携带全部信息,背景不填充。
    /// 掩膜为假、读数 NaN、drag_r 低于显示阈值或线段过短时不画(与 macOS 同口径)。
    /// </summary>
    private void Render()
    {
        _canvas.Children.Clear();
        double width = ActualWidth;
        double height = ActualHeight;
        var field = _field;
        if (field == null || field.Diagonal <= 0f || width <= 0 || height <= 0)
        {
            return;
        }

        double cellWidth = width / Grid;
        double cellHeight = height / Grid;
        double half = Math.Min(cellWidth, cellHeight) * 0.5 * 0.85;
        if (half < 0.5)
        {
            return;
        }

        float diagonal = field.Diagonal;
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                int index = (gy * Grid) + gx;
                if (index >= field.Mask.Length || !field.Mask[index])
                {
                    continue;
                }

                float dragWidth = field.Width[index];
                float direction = field.Direction[index];
                if (float.IsNaN(dragWidth) || float.IsNaN(direction))
                {
                    continue;
                }

                float dragR = dragWidth / diagonal;
                if (dragR < CvHeatmap.DragRMinDisplay)
                {
                    continue;
                }

                float localConsistency = index < field.LocalConsistency.Length
                    ? field.LocalConsistency[index]
                    : float.NaN;
                float contrast = index < field.Contrast.Length
                    ? CvHeatmap.ContrastFactor(field.Contrast[index])
                    : 1f;
                var (red, green, blue) = CvHeatmap.ColorForShake(dragR, localConsistency, contrast);

                double centerX = (gx + 0.5) * cellWidth;
                double centerY = (gy + 0.5) * cellHeight;
                double offsetX = Math.Cos(direction) * half;
                double offsetY = Math.Sin(direction) * half;
                _canvas.Children.Add(new Line
                {
                    X1 = centerX - offsetX,
                    Y1 = centerY - offsetY,
                    X2 = centerX + offsetX,
                    Y2 = centerY + offsetY,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xFF, red, green, blue)),
                    StrokeThickness = StrokeWidth,
                });
            }
        }
    }
}
