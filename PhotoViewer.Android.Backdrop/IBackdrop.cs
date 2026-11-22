using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 取样源，对应上游 <c>com.kyant.backdrop.Backdrop</c>：只负责把背景画到画布上，
/// 效果、形状与光学都在绘制侧（<see cref="BackdropDrawer"/>）叠加。
/// </summary>
/// <remarks>
/// 与上游一致，玻璃元素不得位于它自己取样的层内：取样方的绘制必须晚于被取样层的本帧录制，
/// 否则读到上一帧内容。
/// </remarks>
public interface IBackdrop
{
    /// <summary>取样结果是否依赖绘制方位置（层取样为真，空背景为假）。</summary>
    bool IsCoordinatesDependent { get; }

    /// <summary>把取样结果画到 <paramref name="canvas"/> 上，坐标按 <paramref name="context"/> 对齐。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="context">取样上下文。</param>
    void DrawBackdrop(Gfx.Canvas canvas, in BackdropDrawContext context);
}

/// <summary>
/// 内部内容代际：消费者效果层据此判断源层是否需要重录。没有稳定代际的自定义取样源不实现本接口，
/// 效果层每次绘制都会重录。
/// </summary>
internal interface IBackdropContentVersion
{
    /// <summary>单调递增的内容代际；&lt; 0 表示不可缓存。</summary>
    long ContentVersion { get; }
}
