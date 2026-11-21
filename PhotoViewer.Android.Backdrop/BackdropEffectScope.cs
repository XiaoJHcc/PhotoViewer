using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 模糊边缘处理，逐项对应上游 Compose <c>TileMode</c>（<c>effects/Blur.kt</c> 的
/// <c>edgeTreatment</c> 参数），默认 <see cref="Clamp"/>。
/// </summary>
public enum BackdropTileMode
{
    /// <summary>边缘像素外延（上游默认）。</summary>
    Clamp = 0,

    /// <summary>平铺重复。</summary>
    Repeat = 1,

    /// <summary>镜像平铺。</summary>
    Mirror = 2,

    /// <summary>边缘透明衰减。</summary>
    Decal = 3,
}

/// <summary>
/// 效果链构建器，对应上游 <c>BackdropEffectScope</c> 与
/// <c>com.kyant.backdrop.effects</c> 的 <c>blur</c> / <c>vibrancy</c> / <c>colorControls</c> /
/// <c>opacity</c> / <c>colorFilter</c> / <c>effect</c>。
/// </summary>
/// <remarks>
/// <para>
/// 链序与上游一致：先调用的效果先执行。上游 <c>blur</c> 走 <c>BlurEffect(renderEffect, …)</c>
/// （等价 <c>createChainEffect(blur, previous)</c>），<c>colorFilter</c> 走
/// <c>createColorFilterEffect(filter, previous)</c>，两者都把已有链当输入。
/// </para>
/// <para>
/// 本最小链**不含 AGSL**：模糊与饱和度都由平台 <c>RenderEffect</c> + <c>ColorMatrix</c> 完成。
/// 上游用到 AGSL 的是 <c>effects/Lens.kt</c>、<c>internal/Shaders.kt</c> 与 <c>highlight/</c>（阶段 3）。
/// </para>
/// <para>
/// 低于 API 31 时全部方法静默跳过（与上游 <c>if (!isRenderEffectSupported()) return</c> 相同），
/// 配方代码因此可以各版本共用，由 <see cref="BackdropTier"/> 决定是否绘制色层。
/// </para>
/// </remarks>
public sealed class BackdropEffectScope
{
    /// <summary>上游 <c>vibrancy</c> 的饱和度常量。</summary>
    public const float VibrancySaturation = 1.5f;

    /// <summary>绘制密度（px/dp），供 <see cref="DpToPx"/> 换算。</summary>
    public float Density { get; set; } = 1f;

    /// <summary>字体缩放，随上游 <c>Density</c> 一并透传；本模块不使用。</summary>
    public float FontScale { get; set; } = 1f;

    /// <summary>取样绘制区域宽（px）。</summary>
    public float WidthPx { get; set; }

    /// <summary>取样绘制区域高（px）。</summary>
    public float HeightPx { get; set; }

    /// <summary>录制区域四周的外扩量（px），链中出现非 <see cref="BackdropTileMode.Clamp"/> 边缘或已有前序效果时按最大模糊半径取值。</summary>
    public float PaddingPx { get; private set; }

    /// <summary>链路末端效果。</summary>
    public Gfx.RenderEffect? RenderEffect { get; private set; }

    /// <summary>dp → px。</summary>
    public float DpToPx(float dp) => dp * Density;

    /// <summary>回到初始状态（对应上游 <c>reset()</c>）。</summary>
    public void Reset()
    {
        Density = 1f;
        FontScale = 1f;
        WidthPx = 0f;
        HeightPx = 0f;
        PaddingPx = 0f;
        RenderEffect = null;
    }

    /// <summary>
    /// 模糊（上游 <c>effects/Blur.kt</c>）。半径是像素：dp 口径由调用方经 <see cref="DpToPx"/> 换算，
    /// 与上游直接吃 <c>Dp.toPx()</c> 相同。
    /// </summary>
    /// <param name="radiusPx">模糊半径（px）。≤ 0 时不入链。</param>
    /// <param name="edgeTreatment">边缘处理，默认 <see cref="BackdropTileMode.Clamp"/>。</param>
    public void Blur(float radiusPx, BackdropTileMode edgeTreatment = BackdropTileMode.Clamp)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return;
        if (radiusPx <= 0f) return;

        if (edgeTreatment != BackdropTileMode.Clamp || RenderEffect is not null)
        {
            if (radiusPx > PaddingPx) PaddingPx = radiusPx;
        }

        var blur = Gfx.RenderEffect.CreateBlurEffect(radiusPx, radiusPx, ToPlatform(edgeTreatment));
        RenderEffect = RenderEffect is { } previous
            ? Gfx.RenderEffect.CreateChainEffect(blur!, previous)!
            : blur!;
    }

    /// <summary>提饱和（上游 <c>vibrancy()</c>：饱和度 1.5 的色彩矩阵）。</summary>
    public void Vibrancy() => ColorControls(saturation: VibrancySaturation);

    /// <summary>
    /// 亮度 / 对比度 / 饱和度（上游 <c>colorControls</c>）。三参数全为默认时不入链。
    /// </summary>
    /// <param name="brightness">亮度偏移，默认 0。</param>
    /// <param name="contrast">对比度，默认 1。</param>
    /// <param name="saturation">饱和度，默认 1。</param>
    public void ColorControls(float brightness = 0f, float contrast = 1f, float saturation = 1f)
    {
        if (brightness == 0f && contrast == 1f && saturation == 1f) return;

        ColorFilter(CreateColorControlsFilter(brightness, contrast, saturation));
    }

    /// <summary>整体不透明度（上游 <c>opacity</c>：alpha 行色彩矩阵）。</summary>
    /// <param name="alpha">0–1。</param>
    public void Opacity(float alpha)
    {
        var matrix = new Gfx.ColorMatrix(
        [
            1f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, alpha, 0f,
        ]);
        ColorFilter(new Gfx.ColorMatrixColorFilter(matrix));
    }

    /// <summary>任意色彩滤镜（上游 <c>colorFilter</c>）。</summary>
    /// <param name="colorFilter">滤镜。</param>
    public void ColorFilter(Gfx.ColorFilter colorFilter)
    {
        ArgumentNullException.ThrowIfNull(colorFilter);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return;

        RenderEffect = RenderEffect is { } previous
            ? Gfx.RenderEffect.CreateColorFilterEffect(colorFilter, previous)!
            : Gfx.RenderEffect.CreateColorFilterEffect(colorFilter)!;
    }

    /// <summary>直接追加平台效果（上游 <c>effect</c>；阶段 3 的着色器效果可复用）。</summary>
    /// <param name="renderEffect">效果。</param>
    public void Effect(Gfx.RenderEffect renderEffect)
    {
        ArgumentNullException.ThrowIfNull(renderEffect);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return;

        RenderEffect = RenderEffect is { } previous
            ? Gfx.RenderEffect.CreateChainEffect(renderEffect, previous)!
            : renderEffect;
    }

    /// <summary>
    /// 求值一次配方：重置链后按顺序执行 <paramref name="effects"/>（对应上游 <c>apply</c>）。
    /// </summary>
    /// <param name="effects">配方。</param>
    /// <param name="density">当前绘制密度。</param>
    /// <param name="widthPx">取样区域宽。</param>
    /// <param name="heightPx">取样区域高。</param>
    public void Apply(Action<BackdropEffectScope> effects, float density, float widthPx, float heightPx)
    {
        ArgumentNullException.ThrowIfNull(effects);

        Density = density;
        WidthPx = widthPx;
        HeightPx = heightPx;
        PaddingPx = 0f;
        RenderEffect = null;
        effects(this);
    }

    /// <summary>取当前链的快照。</summary>
    public BackdropEffect Build() => new(RenderEffect, PaddingPx);

    /// <summary>上游 <c>TileMode</c> → 平台枚举实例；<c>Decal</c> 自 API 31 起存在，只由已分级的模糊路径调用。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static Gfx.Shader.TileMode ToPlatform(BackdropTileMode mode) => mode switch
    {
        BackdropTileMode.Repeat => Gfx.Shader.TileMode.Repeat!,
        BackdropTileMode.Mirror => Gfx.Shader.TileMode.Mirror!,
        BackdropTileMode.Decal => Gfx.Shader.TileMode.Decal!,
        _ => Gfx.Shader.TileMode.Clamp!,
    };

    /// <summary>上游 <c>colorControlsColorFilter</c> 的逐项搬运（sRGB 亮度权重与中心点偏移）。</summary>
    private static Gfx.ColorFilter CreateColorControlsFilter(float brightness, float contrast, float saturation)
    {
        var invSat = 1f - saturation;
        var r = 0.213f * invSat;
        var g = 0.715f * invSat;
        var b = 0.072f * invSat;

        var c = contrast;
        var t = (0.5f - (c * 0.5f) + brightness) * 255f;
        var s = saturation;

        var cr = c * r;
        var cg = c * g;
        var cb = c * b;
        var cs = c * s;

        var matrix = new Gfx.ColorMatrix(
        [
            cr + cs, cg, cb, 0f, t,
            cr, cg + cs, cb, 0f, t,
            cr, cg, cb + cs, 0f, t,
            0f, 0f, 0f, 1f, 0f,
        ]);
        return new Gfx.ColorMatrixColorFilter(matrix);
    }
}
