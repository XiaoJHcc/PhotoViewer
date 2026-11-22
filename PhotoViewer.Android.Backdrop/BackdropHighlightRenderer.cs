using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 高光绘制器，对应上游 <c>highlight/HighlightModifier.kt</c> 的 <c>HighlightNode</c>：
/// 沿形状轮廓向内描边，裁在形状内，按样式叠加混合。
/// </summary>
/// <remarks>
/// <para>
/// 上游把描边录进一张 <c>GraphicsLayer</c>，用图层 alpha 与 blendMode 合成；这里用
/// <c>Canvas.saveLayer</c> 做同一件事。线宽为 <c>ceil(widthPx.coerceAtMost(minDimension / 2)) * 2</c>，
/// 再裁到形状内，因此可见线宽是配方线宽；模糊半径走 <c>BlurMaskFilter</c>。
/// </para>
/// <para>
/// 每个绘制器持有自己的着色器缓存：uniform 是着色器对象上的状态，多组件共用会让同一帧的
/// 高光位置互相覆盖。API 33 以下自动退回纯色描边（上游同样 <c>setRuntimeShader(null)</c>）。
/// </para>
/// </remarks>
public sealed class BackdropHighlightRenderer : IDisposable
{
    private readonly Gfx.Paint _paint = new();
    private readonly Gfx.Paint _layerPaint = new();
    private readonly BackdropRuntimeShaderCache _shaderCache = new();
    private Gfx.MaskFilter? _maskFilter;
    private Gfx.PorterDuff.Mode? _blendMode;
    private Gfx.PorterDuffXfermode? _xfermode;
    private float _blurRadiusPx = float.NaN;
    private bool _disposed;

    /// <summary>建一个高光绘制器。</summary>
    public BackdropHighlightRenderer()
    {
        _paint.SetStyle(Gfx.Paint.Style.Stroke);
        _paint.AntiAlias = true;
    }

    /// <summary>按轮廓绘制高光；<paramref name="highlight"/> 为空或线宽为 0 时不画。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="outline">已 <see cref="BackdropShapeOutline.Update"/> 的形状轮廓。</param>
    /// <param name="highlight">高光配方。</param>
    public void Draw(
        Gfx.Canvas canvas,
        BackdropShapeOutline outline,
        BackdropHighlight? highlight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(outline);

        if (highlight is null || highlight.WidthDp <= 0f || highlight.Alpha <= 0f) return;
        if (outline.WidthPx <= 0f || outline.HeightPx <= 0f) return;

        var density = outline.Density;
        var widthPx = highlight.WidthDp * density;
        var minDimension = MathF.Min(outline.WidthPx, outline.HeightPx);
        _paint.StrokeWidth = MathF.Ceiling(MathF.Min(widthPx, minDimension * 0.5f)) * 2f;
        _paint.Color = highlight.Style.Color;

        var blurRadiusPx = highlight.BlurRadiusDp * density;
        if (blurRadiusPx != _blurRadiusPx)
        {
            _maskFilter?.Dispose();
            _maskFilter = blurRadiusPx > 0f
                ? new Gfx.BlurMaskFilter(blurRadiusPx, Gfx.BlurMaskFilter.Blur.Normal)
                : null;
            _paint.SetMaskFilter(_maskFilter);
            _blurRadiusPx = blurRadiusPx;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var shader = highlight.Style.CreateShader(
                outline.WidthPx,
                outline.HeightPx,
                outline.CornerRadii,
                _shaderCache);
            _paint.SetShader(shader?.PlatformShader);
        }
        else
        {
            _paint.SetShader(null);
        }

        _layerPaint.Alpha = (int)MathF.Round(Math.Clamp(highlight.Alpha, 0f, 1f) * 255f);
        BackdropPaint.ApplyBlendMode(
            _layerPaint,
            highlight.Style.BlendMode,
            ref _blendMode,
            ref _xfermode);

        var save = canvas.Save();
        try
        {
            canvas.ClipPath(outline.Path);
            var layer = canvas.SaveLayer(
                -1f,
                -1f,
                outline.WidthPx + 1f,
                outline.HeightPx + 1f,
                _layerPaint);
            canvas.DrawPath(outline.Path, _paint);
            canvas.RestoreToCount(layer);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放画笔与模糊滤镜。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shaderCache.Clear();
        _paint.Dispose();
        _layerPaint.Dispose();
        _maskFilter?.Dispose();
        _xfermode?.Dispose();
    }
}
