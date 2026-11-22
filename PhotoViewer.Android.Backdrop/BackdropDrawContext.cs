using Android.Views;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 一次取样绘制的上下文：绘制密度、绘制区域尺寸，以及**当前画布原点在窗口中的位置**。
/// </summary>
/// <remarks>
/// <para>
/// 上游以 Compose 的 <c>LayoutCoordinates.localPositionOf</c> 把背景层对齐到绘制节点；
/// Views 侧同义信息就是「谁在画、画布原点落在窗口哪里」，因此取样只依赖
/// <see cref="CanvasOriginX"/> / <see cref="CanvasOriginY"/> 与被取样层的窗口位置之差。
/// </para>
/// <para>
/// 玻璃元素带按压缩放或旋转时，取样先按 <see cref="LayerTransform"/> 的逆变换还原背景
/// （上游 <c>InverseLayerScope</c>）。<see cref="ForView(View, int[])"/> 直接读视图自身的
/// <c>ScaleX</c> / <c>ScaleY</c> / <c>Rotation</c>；用画布矩阵自行做变换的调用方用
/// <see cref="WithLayerTransform"/> 显式给出。
/// </para>
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
    /// <param name="layerTransform">玻璃元素自身的渲染变换，默认不变换。</param>
    public BackdropDrawContext(
        float density,
        float fontScale,
        float widthPx,
        float heightPx,
        float canvasOriginX,
        float canvasOriginY,
        BackdropLayerTransform layerTransform = default)
    {
        Density = density;
        FontScale = fontScale;
        WidthPx = widthPx;
        HeightPx = heightPx;
        CanvasOriginX = canvasOriginX;
        CanvasOriginY = canvasOriginY;
        LayerTransform = layerTransform;
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

    /// <summary>玻璃元素自身的渲染变换；取样时按它做逆变换。</summary>
    public BackdropLayerTransform LayerTransform { get; }

    /// <summary>
    /// 复制上下文并替换层变换。调用方用画布矩阵（而不是视图属性）做按压缩放/旋转时使用。
    /// </summary>
    /// <param name="layerTransform">本次绘制实际施加在画布上的缩放与旋转。</param>
    /// <returns>替换后的上下文。</returns>
    public BackdropDrawContext WithLayerTransform(in BackdropLayerTransform layerTransform) => new(
        Density,
        FontScale,
        WidthPx,
        HeightPx,
        CanvasOriginX,
        CanvasOriginY,
        layerTransform);

    /// <summary>
    /// 以视图自身 <c>OnDraw</c> 的画布建上下文：密度取资源 <c>DisplayMetrics</c>，
    /// 画布原点取视图在窗口中的位置（滚动容器内的偏移已由 <c>GetLocationInWindow</c> 反映），
    /// 层变换取视图自身的缩放与旋转。
    /// </summary>
    /// <param name="view">正在绘制的视图。</param>
    public static BackdropDrawContext ForView(View view) => ForView(view, new int[2]);

    /// <summary>同上，但复用调用方的位置缓冲，避免逐帧分配。</summary>
    /// <param name="view">正在绘制的视图。</param>
    /// <param name="locationBuffer">长度 ≥ 2 的位置缓冲。</param>
    public static BackdropDrawContext ForView(View view, int[] locationBuffer) => ForView(
        view,
        locationBuffer,
        new BackdropLayerTransform(view.ScaleX, view.ScaleY, view.Rotation));

    /// <summary>
    /// 同上，但显式给定层变换。调用方用画布矩阵而不是视图属性做缩放/旋转时使用；
    /// 传 <see cref="BackdropLayerTransform.Identity"/> 表示取样不补偿任何变换。
    /// </summary>
    /// <param name="view">正在绘制的视图。</param>
    /// <param name="locationBuffer">长度 ≥ 2 的位置缓冲。</param>
    /// <param name="layerTransform">本次绘制实际施加的缩放与旋转。</param>
    public static BackdropDrawContext ForView(
        View view,
        int[] locationBuffer,
        in BackdropLayerTransform layerTransform)
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
            locationBuffer[1],
            layerTransform);
    }
}
