using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 效果链快照：一次配方求值的结果。不可变，可跨帧复用；参数变化时由调用方重新
/// <see cref="BackdropEffectScope.Build"/>，不必每帧重建 <c>RenderEffect</c>。
/// </summary>
/// <remarks>
/// 非空快照携带产生它的 <see cref="BackdropEffectScope"/> 的离屏效果层所有者；同一份
/// <see cref="BackdropEffectScope"/> 的所有快照共享同一张复用节点，节点不会逐帧新建。
/// 不同控件必须各自持有 scope，不能共用快照，否则同一帧内的本地取样会互相覆盖。
/// </remarks>
public readonly struct BackdropEffect
{
    /// <summary>无效果：直接按取样结果绘制。</summary>
    public static readonly BackdropEffect None = default;

    /// <summary>
    /// 手动建一个效果链快照。非空效果会新建一份离屏效果层；逐帧绘制请改用
    /// <see cref="BackdropEffectScope.Build"/> 复用 scope 持有的节点。
    /// </summary>
    /// <param name="renderEffect">链路末端效果，可为空。</param>
    /// <param name="paddingPx">录制区域四周的像素外扩量（模糊取样用）。</param>
    public BackdropEffect(Gfx.RenderEffect? renderEffect, float paddingPx)
        : this(
            renderEffect,
            paddingPx,
            renderEffect is null && paddingPx <= 0f ? null : new BackdropEffectLayer())
    {
    }

    internal BackdropEffect(
        Gfx.RenderEffect? renderEffect,
        float paddingPx,
        BackdropEffectLayer? layer)
    {
        RenderEffect = renderEffect;
        PaddingPx = paddingPx < 0f ? 0f : paddingPx;
        Layer = layer;
    }

    /// <summary>链路末端效果；低版本与无效果配方为 <c>null</c>。</summary>
    public Gfx.RenderEffect? RenderEffect { get; }

    /// <summary>录制区域四周的像素外扩量。</summary>
    public float PaddingPx { get; }

    /// <summary>既无效果也无外扩：调用方无需离屏层。</summary>
    public bool IsEmpty => RenderEffect is null && PaddingPx <= 0f;

    /// <summary>产生本快照的消费者效果层所有者；空效果或默认快照为 <c>null</c>。</summary>
    internal BackdropEffectLayer? Layer { get; }
}
