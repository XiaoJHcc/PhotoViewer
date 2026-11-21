using System;
using Android.Graphics;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 共享照片层材质（SHELL-12@android）：主屏六处栏底与带衬底 EXIF 块共用的同一份照片层取样与效果链。
/// </summary>
/// <remarks>
/// <para>
/// 取样源只有一份——根视图里包住主图的那个 <see cref="LayerBackdropLayout"/>。API 31 及以上按
/// <c>shell.glass.android.bar.vibrancy.saturation</c> 提饱和再按 <c>shell.bar.blur.radius@android</c> 模糊；
/// API 24–30 不取样，调用方画正式色层（层也不录制，省掉一半绘制）。
/// </para>
/// <para>
/// 效果链快照与平台 <c>RenderEffect</c> 只在密度、照片层尺寸或配方变化时重建。所有取样方共用这一份快照，
/// 取样源内部那张离屏效果层因此不会被不同取样方反复改写，也不会逐帧新建效果对象。
/// </para>
/// <para>
/// 取样会把**整层**画出来，调用方必须先在画布上裁到自身边界或形状，且 <c>save</c> / <c>restore</c> 成对。
/// 圆角形状由取样方自己 <c>ClipPath</c>：共享取样源只有一个离屏效果层，把裁切形状传给
/// <see cref="BackdropDrawer.Draw"/> 会让不同取样方互相覆盖对方的效果层录制。
/// </para>
/// </remarks>
internal sealed class AndroidPhotoMaterial
{
    private readonly LayerBackdropLayout _layer;
    private readonly BackdropEffectScope _scope = new();
    private readonly float _density;
    private readonly float _saturation = (float)DesignTokens.ShellGlassAndroidBarVibrancySaturation;
    private readonly float _blurDp = (float)DesignTokens.ShellBarBlurRadiusAndroid;
    private BackdropEffect _effect = BackdropEffect.None;
    private (float Density, int Width, int Height) _effectKey = (-1f, -1, -1);
    private int _drawCount;

    /// <summary>把共享材质接到照片层上。</summary>
    /// <param name="layer">包住主图的录制容器。</param>
    public AndroidPhotoMaterial(LayerBackdropLayout layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
        _density = layer.Resources?.DisplayMetrics?.Density ?? 1f;
        // SHELL-12：API 24–30 不取样，外观由各栏位色层承担；层不必录制。
        layer.RecordsBackdrop = IsRealMaterial;
    }

    /// <summary>照片层录制容器。</summary>
    internal LayerBackdropLayout Layer => _layer;

    /// <summary>取样源身份（验收用）：只有真实材质级别才有源。</summary>
    internal IBackdrop? Source => IsRealMaterial ? _layer.Backdrop : null;

    /// <summary>本机照片层材质是否可用（API 31 及以上）。</summary>
    internal bool IsRealMaterial => BackdropCapabilities.IsRenderEffectSupported;

    /// <summary>配方：提饱和倍数，模糊之前应用（<c>shell.glass.android.bar.vibrancy.saturation</c>）。</summary>
    internal float VibrancySaturation => _saturation;

    /// <summary>配方：模糊半径（dp，<c>shell.bar.blur.radius@android</c>）。</summary>
    internal float BlurRadiusDp => _blurDp;

    /// <summary>当前效果链快照（验收用）。</summary>
    internal BackdropEffect Effect => _effect;

    /// <summary>真实材质取样绘制次数（验收用）。</summary>
    internal int MaterialDrawCount => _drawCount;

    /// <summary>
    /// 离屏效果层的估算占用：照片层物理尺寸加四边模糊外扩，按 RGBA8。
    /// 主图纹理已由 <see cref="AndroidPhotoView"/> 单独记账，这里只计这份共享效果层，不重复计入照片像素。
    /// 低版本不取样，恒为 0。
    /// </summary>
    internal long FootprintBytes
    {
        get
        {
            if (!IsRealMaterial || !_layer.RecordsBackdrop) return 0;
            int width = _layer.Width, height = _layer.Height;
            if (width <= 0 || height <= 0) return 0;
            long padding = (long)Math.Ceiling(_blurDp * _density);
            long paddedWidth = width + (padding * 2);
            long paddedHeight = height + (padding * 2);
            return paddedWidth * paddedHeight * 4;
        }
    }

    /// <summary>
    /// 把照片层取样按共享配方画到画布上。调用方先裁到自身边界或形状，并成对 <c>save</c> / <c>restore</c>。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="context">取样上下文（画布原点取自取样方自身）。</param>
    internal void Draw(Canvas canvas, in BackdropDrawContext context)
    {
        var source = _layer.Backdrop;
        if (!IsRealMaterial || !_layer.RecordsBackdrop || !source.HasContent) return;
        if (!canvas.IsHardwareAccelerated) return;

        BackdropDrawer.Draw(canvas, source, EffectFor(context), context);
        _drawCount++;
    }

    /// <summary>取当前效果链快照；只在密度、照片层录制尺寸或配方变化时重建。</summary>
    private BackdropEffect EffectFor(in BackdropDrawContext context)
    {
        var key = (context.Density, _layer.Backdrop.RecordedWidth, _layer.Backdrop.RecordedHeight);
        if (key == _effectKey) return _effect;

        _effectKey = key;
        _scope.Apply(scope =>
        {
            scope.ColorControls(saturation: _saturation);
            scope.Blur(scope.DpToPx(_blurDp));
        }, key.Density, key.Item2, key.Item3);
        _effect = _scope.Build();
        return _effect;
    }
}
