using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 取样绘制入口，对应上游 <c>Modifier.drawBackdrop</c> 中「按效果链把背景画到画布上」的最小部分：
/// 有 <c>RenderEffect</c> 时经离屏 <c>RenderNode</c> 走效果链，否则直接画取样结果。
/// </summary>
/// <remarks>
/// 阶段 2 的栏底、带衬底的 EXIF 块由产品组件在自己的 <c>OnDraw</c> 里调用本方法；
/// 高光、阴影、内阴影、折射（<c>Highlight</c> / <c>Shadow</c> / <c>InnerShadow</c> / <c>Lens</c>）
/// 与 <c>layerBlock</c> 层变换属于阶段 3，不在本模块。
/// </remarks>
public static class BackdropDrawer
{
    /// <summary>把 <paramref name="backdrop"/> 的取样按 <paramref name="effect"/> 画到画布上。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="backdrop">取样源；为 <c>null</c> 时不画。</param>
    /// <param name="effect">效果链快照；<see cref="BackdropEffect.None"/> 表示无效果。</param>
    /// <param name="context">取样上下文。</param>
    /// <param name="clipPath">可选裁切形状（取样层坐标）。层尺寸变化由实现自动检测并重建离屏层；只有替换了这个裁切形状才需要调用 <see cref="LayerBackdrop.Invalidate"/>。</param>
    public static void Draw(
        Gfx.Canvas canvas,
        IBackdrop? backdrop,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (backdrop is LayerBackdrop layer)
        {
            layer.DrawWithEffect(canvas, effect, context, clipPath);
        }
        else
        {
            backdrop?.DrawBackdrop(canvas, context);
        }
    }
}
