using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 高光配方，对应上游 <c>highlight/Highlight.kt</c>：线宽、模糊半径、整体不透明度与样式。
/// </summary>
/// <remarks>
/// 默认线宽 0.5dp、模糊半径取线宽一半、整体不透明度 1，与上游数据类默认值一致；颜色与
/// 混合模式在 <see cref="BackdropHighlightStyle"/>。绘制入口是
/// <see cref="BackdropHighlightRenderer.Draw"/>。
/// </remarks>
public sealed class BackdropHighlight
{
    /// <summary>默认线宽（dp）。</summary>
    public const float DefaultWidthDp = 0.5f;

    /// <summary>白 50%、45° 的默认高光（上游 <c>Highlight.Default</c>）。</summary>
    public static BackdropHighlight Default { get; } = new();

    /// <summary>环境高光（上游 <c>Highlight.Ambient</c>）。</summary>
    public static BackdropHighlight Ambient { get; } = new(style: BackdropHighlightStyle.Ambient);

    /// <summary>纯色描边高光（上游 <c>Highlight.Plain</c>）。</summary>
    public static BackdropHighlight Plain { get; } = new(style: BackdropHighlightStyle.Plain);

    /// <summary>建一个高光配方。</summary>
    /// <param name="widthDp">线宽（dp），默认 0.5。</param>
    /// <param name="blurRadiusDp">模糊半径（dp）；省略时取线宽一半。</param>
    /// <param name="alpha">整体不透明度 0–1，默认 1。</param>
    /// <param name="style">样式；省略时用 <see cref="BackdropHighlightStyle.Default"/>。</param>
    public BackdropHighlight(
        float widthDp = DefaultWidthDp,
        float? blurRadiusDp = null,
        float alpha = 1f,
        BackdropHighlightStyle? style = null)
    {
        WidthDp = widthDp;
        BlurRadiusDp = blurRadiusDp ?? (widthDp / 2f);
        Alpha = alpha;
        Style = style ?? BackdropHighlightStyle.Default;
    }

    /// <summary>线宽（dp）。</summary>
    public float WidthDp { get; }

    /// <summary>模糊半径（dp）。</summary>
    public float BlurRadiusDp { get; }

    /// <summary>整体不透明度 0–1。</summary>
    public float Alpha { get; }

    /// <summary>颜色、混合模式与着色器样式。</summary>
    public BackdropHighlightStyle Style { get; }
}
