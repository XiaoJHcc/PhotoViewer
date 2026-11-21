using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 效果链快照：一次配方求值的结果。不可变，可跨帧复用；参数变化时由调用方重新
/// <see cref="BackdropEffectScope.Build"/>，不必每帧重建 <c>RenderEffect</c>。
/// </summary>
public readonly struct BackdropEffect
{
    /// <summary>无效果：直接按取样结果绘制。</summary>
    public static readonly BackdropEffect None = default;

    /// <summary>建一个效果链快照。</summary>
    /// <param name="renderEffect">链路末端效果，可为空。</param>
    /// <param name="paddingPx">录制区域四周的像素外扩量（模糊取样用）。</param>
    public BackdropEffect(Gfx.RenderEffect? renderEffect, float paddingPx)
    {
        RenderEffect = renderEffect;
        PaddingPx = paddingPx < 0f ? 0f : paddingPx;
    }

    /// <summary>链路末端效果；低版本与无效果配方为 <c>null</c>。</summary>
    public Gfx.RenderEffect? RenderEffect { get; }

    /// <summary>录制区域四周的像素外扩量。</summary>
    public float PaddingPx { get; }

    /// <summary>既无效果也无外扩：调用方无需离屏层。</summary>
    public bool IsEmpty => RenderEffect is null && PaddingPx <= 0f;
}
