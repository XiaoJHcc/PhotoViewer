using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 取样绘制入口，对应上游 <c>Modifier.drawBackdrop</c> 中「按效果链把背景画到画布上」的部分：
/// 有 <c>RenderEffect</c> 时经离屏 <c>RenderNode</c> 走效果链，否则直接画取样结果。
/// </summary>
/// <remarks>
/// <para>
/// 高光、阴影与形状分别由 <see cref="BackdropHighlightRenderer"/>、
/// <see cref="BackdropShadowRenderer"/> 与 <see cref="BackdropShapeOutline"/> 提供；它们按上游
/// <c>drawBackdrop</c> 的节点顺序由接入层组合（阴影在内容之前、高光在内容之后）。
/// 玻璃元素自身的缩放/旋转经 <see cref="BackdropDrawContext.LayerTransform"/> 逆变换取样。
/// </para>
/// <para>
/// 非空效果快照携带产生它的 <see cref="BackdropEffectScope"/> 的离屏效果层；效果层按本次
/// <c>context</c> 的消费者尺寸录制，同一源层上的不同消费者各自持有一份 scope，
/// 互不覆盖。取样会把整层画出来，调用方必须先在画布上裁到自身边界或形状，并成对
/// <c>save</c> / <c>restore</c>。
/// </para>
/// </remarks>
public static class BackdropDrawer
{
    /// <summary>把 <paramref name="backdrop"/> 的取样按 <paramref name="effect"/> 画到画布上。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="backdrop">取样源；为 <c>null</c> 时不画。</param>
    /// <param name="effect">效果链快照；<see cref="BackdropEffect.None"/> 表示无效果。</param>
    /// <param name="context">取样上下文。</param>
    /// <param name="clipPath">可选裁切形状（玻璃元素坐标，录制时先加 padding 再裁切）。形状变化由消费者效果层在下一次绘制时按新路径重录。</param>
    public static void Draw(
        Gfx.Canvas canvas,
        IBackdrop? backdrop,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (backdrop is null) return;

        if (!effect.IsEmpty && effect.Layer is { } layer)
        {
            layer.Draw(canvas, backdrop, effect, context, clipPath);
            return;
        }

        backdrop.DrawBackdrop(canvas, context);
    }
}
