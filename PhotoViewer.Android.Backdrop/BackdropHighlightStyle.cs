using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 高光样式，对应上游 <c>highlight/HighlightStyle.kt</c>：<c>Plain</c>、<c>Default</c> 与
/// <c>Ambient</c> 三种，颜色、混合模式与着色器 uniform 默认值照搬。
/// </summary>
public abstract class BackdropHighlightStyle
{
    /// <summary>默认白 50% 线性渐变高光（上游 <c>HighlightStyle.Default</c>）。</summary>
    public static BackdropDefaultHighlightStyle Default { get; } = new();

    /// <summary>环境高光（上游 <c>HighlightStyle.Ambient</c>）。</summary>
    public static BackdropAmbientHighlightStyle Ambient { get; } = new();

    /// <summary>纯色描边高光（上游 <c>HighlightStyle.Plain</c>）。</summary>
    public static BackdropPlainHighlightStyle Plain { get; } = new();

    /// <summary>描边颜色。</summary>
    public abstract Gfx.Color Color { get; }

    /// <summary>混合模式；默认高光是 <see cref="Gfx.PorterDuff.Mode.Add"/>（上游 <c>BlendMode.Plus</c>）。</summary>
    public abstract Gfx.PorterDuff.Mode BlendMode { get; }

    /// <summary>按当前尺寸与四角半径创建样式着色器；无着色器时返回 <c>null</c>。</summary>
    /// <param name="widthPx">高光区域宽（px）。</param>
    /// <param name="heightPx">高光区域高（px）。</param>
    /// <param name="cornerRadii">四角半径（px）。</param>
    /// <param name="shaderCache">绘制器持有的着色器缓存。</param>
    /// <returns>着色器，或 <c>null</c> 表示只用纯色描边。</returns>
    protected internal abstract BackdropRuntimeShader? CreateShader(
        float widthPx,
        float heightPx,
        BackdropCornerRadii cornerRadii,
        BackdropRuntimeShaderCache shaderCache);
}

/// <summary>
/// 纯色描边高光，对应上游 <c>HighlightStyle.Plain</c>：不带着色器，白 38%、Plus。
/// </summary>
public sealed class BackdropPlainHighlightStyle : BackdropHighlightStyle
{
    /// <summary>建一个纯色高光样式。</summary>
    /// <param name="color">描边颜色，默认白 38%。</param>
    /// <param name="blendMode">混合模式，默认 <see cref="Gfx.PorterDuff.Mode.Add"/>。</param>
    public BackdropPlainHighlightStyle(
        Gfx.Color? color = null,
        Gfx.PorterDuff.Mode? blendMode = null)
    {
        Color = color ?? Gfx.Color.Argb(97, 255, 255, 255);
        BlendMode = blendMode ?? Gfx.PorterDuff.Mode.Add!;
    }

    /// <inheritdoc />
    public override Gfx.Color Color { get; }

    /// <inheritdoc />
    public override Gfx.PorterDuff.Mode BlendMode { get; }

    protected internal override BackdropRuntimeShader? CreateShader(
        float widthPx,
        float heightPx,
        BackdropCornerRadii cornerRadii,
        BackdropRuntimeShaderCache shaderCache) => null;
}

/// <summary>
/// 默认线性高光，对应上游 <c>HighlightStyle.Default</c>：白 50%、Plus、45°、衰减 1，
/// 由 <c>DefaultHighlightShaderString</c> 按形状梯度出光。
/// </summary>
public sealed class BackdropDefaultHighlightStyle : BackdropHighlightStyle
{
    /// <summary>建一个默认高光样式。</summary>
    /// <param name="color">描边颜色，默认白 50%。</param>
    /// <param name="blendMode">混合模式，默认 <see cref="Gfx.PorterDuff.Mode.Add"/>。</param>
    /// <param name="angle">光源角度（度），默认 45。</param>
    /// <param name="falloff">衰减指数，默认 1。</param>
    public BackdropDefaultHighlightStyle(
        Gfx.Color? color = null,
        Gfx.PorterDuff.Mode? blendMode = null,
        float angle = 45f,
        float falloff = 1f)
    {
        Color = color ?? Gfx.Color.Argb(128, 255, 255, 255);
        BlendMode = blendMode ?? Gfx.PorterDuff.Mode.Add!;
        Angle = angle;
        Falloff = falloff;
    }

    /// <inheritdoc />
    public override Gfx.Color Color { get; }

    /// <inheritdoc />
    public override Gfx.PorterDuff.Mode BlendMode { get; }

    /// <summary>光源角度（度）。</summary>
    public float Angle { get; }

    /// <summary>衰减指数。</summary>
    public float Falloff { get; }

    protected internal override BackdropRuntimeShader? CreateShader(
        float widthPx,
        float heightPx,
        BackdropCornerRadii cornerRadii,
        BackdropRuntimeShaderCache shaderCache)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return null;

        var shader = shaderCache.Obtain("Default", BackdropShaders.DefaultHighlight);
        shader.SetFloatUniform("size", widthPx, heightPx);
        shader.SetFloatUniform("cornerRadii", cornerRadii.ToArray());
        shader.SetColorUniform("color", Gfx.Color.Rgb(Color.R, Color.G, Color.B));
        shader.SetFloatUniform("angle", Angle * (MathF.PI / 180f));
        shader.SetFloatUniform("falloff", Falloff);
        return shader;
    }
}

/// <summary>
/// 环境高光，对应上游 <c>HighlightStyle.Ambient</c>：强度决定白色透明度，默认混合模式，
/// 由 <c>AmbientHighlightShaderString</c> 只取朝向光源的一侧。
/// </summary>
public sealed class BackdropAmbientHighlightStyle : BackdropHighlightStyle
{
    /// <summary>建一个环境高光样式。</summary>
    /// <param name="intensity">强度 0–1，默认 0.38。</param>
    public BackdropAmbientHighlightStyle(float intensity = 0.38f)
    {
        Intensity = Math.Clamp(intensity, 0f, 1f);
        Color = Gfx.Color.Argb((int)MathF.Round(Intensity * 255f), 255, 255, 255);
        BlendMode = Gfx.PorterDuff.Mode.SrcOver!;
    }

    /// <summary>强度 0–1。</summary>
    public float Intensity { get; }

    /// <inheritdoc />
    public override Gfx.Color Color { get; }

    /// <inheritdoc />
    public override Gfx.PorterDuff.Mode BlendMode { get; }

    protected internal override BackdropRuntimeShader? CreateShader(
        float widthPx,
        float heightPx,
        BackdropCornerRadii cornerRadii,
        BackdropRuntimeShaderCache shaderCache)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return null;

        var shader = shaderCache.Obtain("Ambient", BackdropShaders.AmbientHighlight);
        shader.SetFloatUniform("size", widthPx, heightPx);
        shader.SetFloatUniform("cornerRadii", cornerRadii.ToArray());
        shader.SetFloatUniform("angle", 45f * (MathF.PI / 180f));
        shader.SetFloatUniform("falloff", 1f);
        return shader;
    }
}
