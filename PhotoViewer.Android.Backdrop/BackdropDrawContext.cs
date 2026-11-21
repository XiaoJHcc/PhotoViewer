using Android.Views;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 一次取样绘制的上下文：绘制密度、绘制区域尺寸，以及**当前画布原点在窗口中的位置**。
/// </summary>
/// <remarks>
/// 上游以 Compose 的 <c>LayoutCoordinates.localPositionOf</c> 把背景层对齐到绘制节点；
/// Views 侧同义信息就是「谁在画、画布原点落在窗口哪里」，因此取样只依赖
/// <see cref="CanvasOriginX"/> / <see cref="CanvasOriginY"/> 与被取样层的窗口位置之差。
/// 视图自身带渲染变换（缩放/旋转/枢轴）时如何求逆变换属于阶段 3，本模块不实现。
/// </remarks>
public readonly struct BackdropDrawContext
{
    /// <summary>显式给定取样上下文。</summary>
    /// <param name="density">绘制密度。</param>
    /// <param name="fontScale">字体缩放。</param>
    /// <param name="widthPx">绘制区域宽。</param>
    /// <param name="heightPx">绘制区域高。</param>
    /// <param name="canvasOriginX">画布原点在窗口中的 X。</param>
    /// <param name="canvasOriginY">画布原点在窗口中的 Y。</param>
    public BackdropDrawContext(
        float density,
        float fontScale,
        float widthPx,
        float heightPx,
        float canvasOriginX,
        float canvasOriginY)
    {
        Density = density;
        FontScale = fontScale;
        WidthPx = widthPx;
        HeightPx = heightPx;
        CanvasOriginX = canvasOriginX;
        CanvasOriginY = canvasOriginY;
    }

    /// <summary>绘制密度（px/dp）。</summary>
    public float Density { get; }

    /// <summary>字体缩放。</summary>
    public float FontScale { get; }

    /// <summary>绘制区域宽（px）。</summary>
    public float WidthPx { get; }

    /// <summary>绘制区域高（px）。</summary>
    public float HeightPx { get; }

    /// <summary>画布原点在窗口中的 X。</summary>
    public float CanvasOriginX { get; }

    /// <summary>画布原点在窗口中的 Y。</summary>
    public float CanvasOriginY { get; }

    /// <summary>
    /// 以视图自身 <c>OnDraw</c> 的画布建上下文：密度取资源 <c>DisplayMetrics</c>，
    /// 画布原点取视图在窗口中的位置（滚动容器内的偏移已由 <c>GetLocationInWindow</c> 反映）。
    /// </summary>
    /// <param name="view">正在绘制的视图。</param>
    public static BackdropDrawContext ForView(View view) => ForView(view, new int[2]);

    /// <summary>同上，但复用调用方的位置缓冲，避免逐帧分配。</summary>
    /// <param name="view">正在绘制的视图。</param>
    /// <param name="locationBuffer">长度 ≥ 2 的位置缓冲。</param>
    public static BackdropDrawContext ForView(View view, int[] locationBuffer)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(locationBuffer);
        if (locationBuffer.Length < 2)
        {
            throw new ArgumentException("位置缓冲至少需要 2 个 int。", nameof(locationBuffer));
        }

        var resources = view.Resources;
        var density = resources?.DisplayMetrics?.Density ?? 1f;
        var fontScale = resources?.Configuration?.FontScale ?? 1f;

        view.GetLocationInWindow(locationBuffer);

        return new BackdropDrawContext(
            density,
            fontScale,
            view.Width,
            view.Height,
            locationBuffer[0],
            locationBuffer[1]);
    }
}
