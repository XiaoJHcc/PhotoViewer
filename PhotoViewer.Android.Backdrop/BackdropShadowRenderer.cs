using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 外部阴影绘制器，对应上游 <c>shadow/ShadowModifier.kt</c> 的 <c>ShadowNode</c>。
/// </summary>
/// <remarks>
/// <para>
/// 上游把图层按 <c>(ceil(w + 4r + dx), ceil(h + 4r + dy))</c> 录好，在层内把形状画在
/// <c>(2r + dx, 2r + dy)</c>，再用清除模式在 <c>(2r, 2r)</c> 挖掉形状本体，最后把图层画在
/// <c>(−2r, −2r)</c>。这里用 <c>Canvas.saveLayer</c> 复刻同一顺序，投影落在形状外。
/// </para>
/// <para>
/// 阴影超出视图边界时由宿主提供不裁切的绘制范围；本绘制器只按给定画布裁剪。
/// </para>
/// </remarks>
public sealed class BackdropShadowRenderer : IDisposable
{
    private readonly Gfx.Paint _paint = new() { AntiAlias = true };
    private readonly Gfx.Paint _layerPaint = new();
    private readonly Gfx.Paint _maskPaint = new() { AntiAlias = true };
    private readonly Gfx.PorterDuffXfermode _maskXfermode;
    private Gfx.MaskFilter? _maskFilter;
    private Gfx.PorterDuff.Mode? _blendMode;
    private Gfx.PorterDuffXfermode? _xfermode;
    private float _blurRadiusPx = float.NaN;
    private bool _disposed;

    /// <summary>建一个阴影绘制器。</summary>
    public BackdropShadowRenderer()
    {
        _maskXfermode = new Gfx.PorterDuffXfermode(Gfx.PorterDuff.Mode.Clear);
        _maskPaint.SetXfermode(_maskXfermode);
    }

    /// <summary>按轮廓绘制阴影；<paramref name="shadow"/> 为空时不画。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="outline">已 <see cref="BackdropShapeOutline.Update"/> 的形状轮廓。</param>
    /// <param name="shadow">阴影配方。</param>
    public void Draw(
        Gfx.Canvas canvas,
        BackdropShapeOutline outline,
        BackdropShadow? shadow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(outline);

        if (shadow is null || shadow.Alpha <= 0f) return;
        if (outline.WidthPx <= 0f || outline.HeightPx <= 0f) return;

        var density = outline.Density;
        var radiusPx = shadow.RadiusDp * density;
        var offsetX = shadow.OffsetXDp * density;
        var offsetY = shadow.OffsetYDp * density;
        var shadowWidth = (int)MathF.Ceiling(outline.WidthPx + (radiusPx * 4f) + offsetX);
        var shadowHeight = (int)MathF.Ceiling(outline.HeightPx + (radiusPx * 4f) + offsetY);
        if (shadowWidth <= 0 || shadowHeight <= 0) return;

        _paint.Color = shadow.Color;
        if (radiusPx != _blurRadiusPx)
        {
            _maskFilter?.Dispose();
            _maskFilter = radiusPx > 0f
                ? new Gfx.BlurMaskFilter(radiusPx, Gfx.BlurMaskFilter.Blur.Normal)
                : null;
            _paint.SetMaskFilter(_maskFilter);
            _blurRadiusPx = radiusPx;
        }

        _layerPaint.Alpha = (int)MathF.Round(Math.Clamp(shadow.Alpha, 0f, 1f) * 255f);
        BackdropPaint.ApplyBlendMode(_layerPaint, shadow.BlendMode, ref _blendMode, ref _xfermode);

        var save = canvas.Save();
        try
        {
            canvas.Translate(-radiusPx * 2f, -radiusPx * 2f);
            var layer = canvas.SaveLayer(0f, 0f, shadowWidth, shadowHeight, _layerPaint);
            canvas.Translate((radiusPx * 2f) + offsetX, (radiusPx * 2f) + offsetY);
            canvas.DrawPath(outline.Path, _paint);
            canvas.Translate(-offsetX, -offsetY);
            canvas.DrawPath(outline.Path, _maskPaint);
            canvas.RestoreToCount(layer);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放画笔、滤镜与 xfermode。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _paint.Dispose();
        _layerPaint.Dispose();
        _maskPaint.Dispose();
        _maskFilter?.Dispose();
        _xfermode?.Dispose();
        _maskXfermode.Dispose();
    }
}
