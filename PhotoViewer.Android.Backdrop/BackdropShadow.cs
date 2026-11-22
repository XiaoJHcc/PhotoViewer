using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 外部阴影配方，对应上游 <c>shadow/Shadow.kt</c>：半径 24dp、下移半径/6、黑 10%、整体不透明度 1。
/// </summary>
/// <remarks>
/// 绘制入口是 <see cref="BackdropShadowRenderer.Draw"/>：先按模糊半径与偏移把形状画进离屏层，
/// 再用清除模式在层内挖掉形状本身，只留形状外的投影，与上游 <c>ShadowNode</c> 同序。
/// </remarks>
public sealed class BackdropShadow
{
    /// <summary>默认阴影（上游 <c>Shadow.Default</c>）。</summary>
    public static BackdropShadow Default { get; } = new();

    /// <summary>建一个阴影配方。</summary>
    /// <param name="radiusDp">模糊半径（dp），默认 24。</param>
    /// <param name="offsetXDp">水平偏移（dp），默认 0。</param>
    /// <param name="offsetYDp">垂直偏移（dp）；省略时取半径/6。</param>
    /// <param name="color">颜色，默认黑 10%。</param>
    /// <param name="alpha">整体不透明度 0–1，默认 1。</param>
    /// <param name="blendMode">混合模式，默认 <see cref="Gfx.PorterDuff.Mode.SrcOver"/>。</param>
    public BackdropShadow(
        float radiusDp = 24f,
        float offsetXDp = 0f,
        float? offsetYDp = null,
        Gfx.Color? color = null,
        float alpha = 1f,
        Gfx.PorterDuff.Mode? blendMode = null)
    {
        RadiusDp = radiusDp;
        OffsetXDp = offsetXDp;
        OffsetYDp = offsetYDp ?? (radiusDp / 6f);
        Color = color ?? Gfx.Color.Argb(26, 0, 0, 0);
        Alpha = alpha;
        BlendMode = blendMode ?? Gfx.PorterDuff.Mode.SrcOver!;
    }

    /// <summary>模糊半径（dp）。</summary>
    public float RadiusDp { get; }

    /// <summary>水平偏移（dp）。</summary>
    public float OffsetXDp { get; }

    /// <summary>垂直偏移（dp）。</summary>
    public float OffsetYDp { get; }

    /// <summary>颜色。</summary>
    public Gfx.Color Color { get; }

    /// <summary>整体不透明度 0–1。</summary>
    public float Alpha { get; }

    /// <summary>混合模式。</summary>
    public Gfx.PorterDuff.Mode BlendMode { get; }
}
