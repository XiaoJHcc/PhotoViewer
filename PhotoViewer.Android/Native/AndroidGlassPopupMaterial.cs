using System;
using Android.Graphics;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 窗口内弹层玻璃表面（MENU-03@android / FILTER-08@android / RATING-04@android）：
/// 在 <see cref="AndroidGlassPopupLayer"/> 的面板上按正式配方画取样、折射、表面色与边缘高光。
/// </summary>
/// <remarks>
/// <para>
/// 配方逐项取 <see cref="DesignTokens"/>：colorControls 饱和度
/// <c>popup.glass.android.colorControls.saturation</c> → blur
/// <c>popup.glass.android.blur.radius</c> → lens <c>popup.glass.android.lens.height</c> /
/// <c>.lens.amount</c>（含景深）→ 表面 <c>popup.glass.android.surface.color</c> ×
/// <c>.surface.opacity</c> → <c>popup.glass.android.highlight.width</c> / <c>.color</c> 的
/// <c>Highlight.Plain</c>。形状是请求圆角的 G2 连续曲率圆角矩形；不加背景压暗与阴影。
/// </para>
/// <para>
/// 分级：API 33 及以上为完整配方；API 31–32 只有 colorControls、模糊与表面色，无折射与高光；
/// API 24–30、取样源为空或画布不是硬件加速时画请求里的正式 fill / border。
/// </para>
/// <para>
/// 每个面板持有独立的 <see cref="BackdropEffectScope"/>、效果快照、轮廓缓存与高光绘制器：
/// 效果快照只在密度、面板尺寸或圆角变化时重建，轮廓路径与画笔跨帧复用，不逐帧新建节点、着色器或 Path。
/// 由 <see cref="AndroidGlassPopupLayer.Surface"/> 接管所有权，替换或宿主释放时调用 <see cref="Dispose"/>。
/// </para>
/// </remarks>
internal sealed class AndroidGlassPopupMaterial : IGlassPopupSurface, IDisposable
{
    private readonly BackdropEffectScope _scope = new();
    private readonly BackdropHighlightRenderer _highlightRenderer = new();
    private readonly BackdropHighlight _highlight;
    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _borderPaint = new(PaintFlags.AntiAlias);
    private readonly Color _surfaceColor;
    private readonly float _saturation;
    private readonly float _blurRadiusDp;
    private readonly float _lensHeightDp;
    private readonly float _lensAmountDp;
    private readonly BackdropShapeOutline _outline;
    private BackdropRoundedRectangle _shape;
    private float _shapeRadiusDp = float.NaN;
    private BackdropEffect _effect = BackdropEffect.None;
    private EffectKey _effectKey = new(float.NaN, 0f, 0f, float.NaN);
    private long _effectBuildCount;
    private bool _disposed;

    /// <summary>按设计令牌建立一份面板材质；每个弹层面板各持一份。</summary>
    internal AndroidGlassPopupMaterial()
    {
        _saturation = (float)DesignTokens.PopupGlassAndroidColorControlsSaturation;
        _blurRadiusDp = (float)DesignTokens.PopupGlassAndroidBlurRadius;
        _lensHeightDp = (float)DesignTokens.PopupGlassAndroidLensHeight;
        _lensAmountDp = (float)DesignTokens.PopupGlassAndroidLensAmount;
        _surfaceColor = AndroidUiTheme.Layer(DesignTokens.PopupGlassAndroidSurfaceColor,
            DesignTokens.PopupGlassAndroidSurfaceOpacity);
        _highlight = new BackdropHighlight(
            widthDp: (float)DesignTokens.PopupGlassAndroidHighlightWidth,
            style: new BackdropPlainHighlightStyle(
                AndroidUiTheme.FromToken(DesignTokens.PopupGlassAndroidHighlightColor)));

        _shape = new BackdropRoundedRectangle(0f, BackdropCornerStyle.Continuous);
        _outline = new BackdropShapeOutline(_shape);
        _fillPaint.SetStyle(Paint.Style.Fill);
        _borderPaint.SetStyle(Paint.Style.Stroke);
    }

    /// <summary>本机材质分级（API 24–30 色层 / 31–32 模糊 / 33+ 完整）。</summary>
    internal BackdropTier Tier => BackdropCapabilities.Tier;

    /// <summary>当前效果链快照（验收读取；每帧复用同一份）。</summary>
    internal BackdropEffect EffectForChecks => _effect;

    /// <summary>效果链快照累计重建次数（验收读取；尺寸/圆角不变时不增长）。</summary>
    internal long EffectBuildCountForChecks => _effectBuildCount;

    /// <inheritdoc />
    public void Draw(in GlassPopupSurfaceContext context)
    {
        if (_disposed || context.WidthPx <= 0 || context.HeightPx <= 0) return;

        SyncShape(context);
        _outline.Update(context.WidthPx, context.HeightPx, context.Density);

        var canvas = context.Canvas;
        // Canvas 裁切与折射共用一个 G2 轮廓：面板的 ClipToOutline 与这里取同一圆角、尺寸与路径算法。
        int save = canvas.Save();
        try
        {
            canvas.ClipPath(_outline.Path);
            bool glass = context.Backdrop != null
                && BackdropCapabilities.IsRenderEffectSupported
                && canvas.IsHardwareAccelerated
                && context.Backdrop is not LayerBackdrop { HasContent: false };
            if (!glass)
            {
                DrawFormalLayer(context);
                return;
            }

            EnsureEffect(context);
            BackdropDrawer.Draw(canvas, context.Backdrop, _effect, context.Draw);
            _fillPaint.SetStyle(Paint.Style.Fill);
            _fillPaint.Color = _surfaceColor;
            canvas.DrawPath(_outline.Path, _fillPaint);
            if (BackdropCapabilities.IsRuntimeShaderSupported)
                _highlightRenderer.Draw(canvas, _outline, _highlight);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放效果层、着色器缓存、高光绘制器、画笔与轮廓路径。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _scope.Dispose();
        _highlightRenderer.Dispose();
        _fillPaint.Dispose();
        _borderPaint.Dispose();
        _outline.Path.Dispose();
        _effect = BackdropEffect.None;
        _shapeRadiusDp = float.NaN;
    }

    private void DrawFormalLayer(in GlassPopupSurfaceContext context)
    {
        _fillPaint.SetStyle(Paint.Style.Fill);
        _fillPaint.Color = context.Fill;
        context.Canvas.DrawPath(_outline.Path, _fillPaint);

        if (context.BorderWidthPx <= 0 || context.Border.A == 0) return;
        _borderPaint.StrokeWidth = MathF.Max(1f, context.BorderWidthPx * 2f);
        _borderPaint.Color = context.Border;
        context.Canvas.DrawPath(_outline.Path, _borderPaint);
    }

    private void SyncShape(in GlassPopupSurfaceContext context)
    {
        float radiusDp = context.CornerRadiusDp;
        if (!float.IsNaN(_shapeRadiusDp) && MathF.Abs(radiusDp - _shapeRadiusDp) <= 0.001f) return;
        _shapeRadiusDp = radiusDp;
        _shape = new BackdropRoundedRectangle(radiusDp, BackdropCornerStyle.Continuous);
        _outline.Shape = _shape;
    }

    private void EnsureEffect(in GlassPopupSurfaceContext context)
    {
        var key = new EffectKey(context.Density, context.WidthPx, context.HeightPx, context.CornerRadiusDp);
        if (key == _effectKey) return;
        _effectKey = key;

        _scope.Shape = _shape;
        _scope.Apply(scope =>
        {
            scope.ColorControls(saturation: _saturation);
            scope.Blur(scope.DpToPx(_blurRadiusDp));
            if (BackdropCapabilities.IsRuntimeShaderSupported)
                scope.Lens(scope.DpToPx(_lensHeightDp), scope.DpToPx(_lensAmountDp), depthEffect: true);
        }, context.Density, context.WidthPx, context.HeightPx);
        _effect = _scope.Build();
        _effectBuildCount++;
    }

    /// <summary>效果快照的重建键：密度、面板局部分辨率与请求圆角。</summary>
    private readonly record struct EffectKey(float Density, float WidthPx, float HeightPx, float CornerRadiusDp);
}
