using System;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 玻璃按钮交互形变的换算结果：按压放大、随手指橡皮筋位移与方向拉伸。
/// </summary>
public readonly struct BackdropInteractiveTransform
{
    /// <summary>整体缩放。</summary>
    public float Scale { get; init; }

    /// <summary>X 轴缩放（含方向拉伸）。</summary>
    public float ScaleX { get; init; }

    /// <summary>Y 轴缩放（含方向拉伸）。</summary>
    public float ScaleY { get; init; }

    /// <summary>X 位移（px）。</summary>
    public float TranslationX { get; init; }

    /// <summary>Y 位移（px）。</summary>
    public float TranslationY { get; init; }

    /// <summary>
    /// 按上游示例 <c>LiquidButton</c> 的 <c>layerBlock</c> 公式计算形变。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按压进度决定交叉轴放大（<paramref name="pressGrowthPx"/> 对应上游 4dp）；位移按
    /// <c>maxOffset · tanh(initialDerivative · offset / maxOffset)</c>（默认导数 0.05）；
    /// 拉伸按手指方向把位移投影到两轴，并以 <c>width/height</c> 或 <c>height/width</c>
    /// 夹到 1 以内（<paramref name="dragGrowthPx"/> 同为上游 4dp）。
    /// </para>
    /// <para>
    /// 上游把结果写进 <c>graphicsLayer</c>；Views 侧可直接写 <c>View.ScaleX</c> / <c>View.ScaleY</c> /
    /// <c>View.TranslationX</c> / <c>View.TranslationY</c>，取样上下文会按缩放与旋转做逆变换。
    /// </para>
    /// </remarks>
    /// <param name="widthPx">控件宽（px）。</param>
    /// <param name="heightPx">控件高（px）。</param>
    /// <param name="pressProgress">按压进度 0–1。</param>
    /// <param name="offsetX">跟手位移 X（px）。</param>
    /// <param name="offsetY">跟手位移 Y（px）。</param>
    /// <param name="pressGrowthPx">按压时交叉轴增加的长度（px）。</param>
    /// <param name="dragGrowthPx">拖动方向拉伸的增加量（px）。</param>
    /// <param name="initialDerivative">位移曲线在 0 点的导数，默认 0.05。</param>
    /// <returns>形变结果。</returns>
    public static BackdropInteractiveTransform Compute(
        float widthPx,
        float heightPx,
        float pressProgress,
        float offsetX,
        float offsetY,
        float pressGrowthPx,
        float dragGrowthPx,
        float initialDerivative = 0.05f)
    {
        if (widthPx <= 0f || heightPx <= 0f)
        {
            return new BackdropInteractiveTransform
            {
                Scale = 1f,
                ScaleX = 1f,
                ScaleY = 1f,
            };
        }

        var scale = 1f + ((pressGrowthPx / heightPx) * pressProgress);
        var maxOffset = MathF.Min(widthPx, heightPx);
        var maxDimension = MathF.Max(widthPx, heightPx);
        var translationX = maxOffset * MathF.Tanh(initialDerivative * offsetX / maxOffset);
        var translationY = maxOffset * MathF.Tanh(initialDerivative * offsetY / maxOffset);

        var maxDragScale = dragGrowthPx / heightPx;
        var offsetAngle = MathF.Atan2(offsetY, offsetX);
        var scaleX = scale +
            (maxDragScale * MathF.Abs(MathF.Cos(offsetAngle) * offsetX / maxDimension) *
                MathF.Min(widthPx / heightPx, 1f));
        var scaleY = scale +
            (maxDragScale * MathF.Abs(MathF.Sin(offsetAngle) * offsetY / maxDimension) *
                MathF.Min(heightPx / widthPx, 1f));

        return new BackdropInteractiveTransform
        {
            Scale = scale,
            ScaleX = scaleX,
            ScaleY = scaleY,
            TranslationX = translationX,
            TranslationY = translationY,
        };
    }
}
