using System;
using System.Collections.Generic;
using System.Threading;
using Android.Graphics;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 共享照片层材质（SHELL-12@android）：主屏栏底与带衬底 EXIF 共用同一份照片层取样与配方。
/// </summary>
/// <remarks>
/// <para>
/// 取样源只有一份——根视图里包住主图的那个 <see cref="LayerBackdropLayout"/>。API 31 及以上按
/// <c>shell.glass.android.bar.vibrancy.saturation</c> 提饱和再按 <c>shell.bar.blur.radius@android</c> 模糊；
/// API 24–30 不取样，调用方画正式色层（层也不录制，省掉一半绘制）。
/// </para>
/// <para>
/// 每个绘制视图通过 <see cref="CreateConsumer"/> 持有自己的 <see cref="Consumer"/>：一份
/// <see cref="BackdropEffectScope"/>、一份效果快照与一张按本视图 <c>context</c> 尺寸录制的离屏效果层。
/// 栏底消费者另应用统一 sRGB gamma，EXIF 使用默认线性响应；效果快照按本视图的密度与
/// <c>WidthPx / HeightPx</c> 求值，不能用源层录制尺寸。同一源层上的不同控件因此各有本地取样结果，
/// 不会在同一帧互相覆盖。
/// </para>
/// <para>
/// 取样画的是整层，调用方先裁到自身边界或形状，<c>save</c> / <c>restore</c> 成对；圆角也由各视图自己裁。
/// </para>
/// </remarks>
internal sealed class AndroidPhotoMaterial
{
    private readonly LayerBackdropLayout _layer;
    private readonly float _saturation = (float)DesignTokens.ShellGlassAndroidBarVibrancySaturation;
    private readonly float _barGamma = (float)DesignTokens.ShellGlassAndroidBarToneGamma;
    private readonly float _blurDp = (float)DesignTokens.ShellBarBlurRadiusAndroid;
    private readonly object _consumersGate = new();
    private readonly List<Consumer> _consumers = [];
    private BackdropEffect _representativeEffect = BackdropEffect.None;
    private int _drawCount;

    /// <summary>把共享材质接到照片层上。</summary>
    /// <param name="layer">包住主图的录制容器。</param>
    public AndroidPhotoMaterial(LayerBackdropLayout layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
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

    /// <summary>配方：栏底统一 sRGB gamma（<c>shell.glass.android.bar.tone.gamma</c>）。</summary>
    internal float BarToneGamma => _barGamma;

    /// <summary>配方：模糊半径（dp，<c>shell.bar.blur.radius@android</c>）。</summary>
    internal float BlurRadiusDp => _blurDp;

    /// <summary>最近一次绘制的代表效果快照（验收用）。</summary>
    internal BackdropEffect Effect
    {
        get
        {
            lock (_consumersGate)
            {
                return _representativeEffect;
            }
        }
    }

    /// <summary>所有消费者真实材质取样绘制次数之和（验收用）。</summary>
    internal int MaterialDrawCount => Volatile.Read(ref _drawCount);

    /// <summary>
    /// 为每个取样视图创建一个消费者：独立的 scope、效果快照与离屏效果层。
    /// API 24–30 返回 <c>null</c>。视图销毁时必须 <see cref="Consumer.Dispose"/>。
    /// </summary>
    /// <param name="gamma">取样响应 gamma；栏底传校准值，EXIF 使用默认 1。</param>
    /// <returns>消费者；低版本为 <c>null</c>。</returns>
    internal Consumer? CreateConsumer(float gamma = 1f)
    {
        if (!IsRealMaterial) return null;

        var consumer = new Consumer(this, gamma);
        lock (_consumersGate)
        {
            _consumers.Add(consumer);
        }

        return consumer;
    }

    /// <summary>
    /// 所有已创建消费者的离屏效果层估算占用之和：逐视图
    /// <c>ceil(WidthPx + 2 × padding) × ceil(HeightPx + 2 × padding) × 4</c>。
    /// 尚未绘制或尺寸未知的消费者计 0；低版本恒为 0。主图纹理由
    /// <see cref="AndroidPhotoView"/> 单独记账，这里不重复计入照片像素。
    /// </summary>
    internal long FootprintBytes
    {
        get
        {
            if (!IsRealMaterial || !_layer.RecordsBackdrop) return 0;

            lock (_consumersGate)
            {
                long total = 0;
                foreach (var consumer in _consumers)
                {
                    total += consumer.FootprintBytes;
                }

                return total;
            }
        }
    }

    private void OnConsumerDrawn(Consumer consumer, in BackdropEffect effect, in BackdropDrawContext context)
    {
        long bytes = 0;
        if (!effect.IsEmpty && context.WidthPx > 0f && context.HeightPx > 0f)
        {
            long width = (long)Math.Ceiling(context.WidthPx + (2.0 * effect.PaddingPx));
            long height = (long)Math.Ceiling(context.HeightPx + (2.0 * effect.PaddingPx));
            if (width > 0 && height > 0)
            {
                bytes = width * height * 4;
            }
        }

        lock (_consumersGate)
        {
            _representativeEffect = effect;
            consumer.SetFootprint(bytes);
        }

        Interlocked.Increment(ref _drawCount);
    }

    private void Unregister(Consumer consumer)
    {
        lock (_consumersGate)
        {
            _consumers.Remove(consumer);
        }
    }

    /// <summary>
    /// 单个取样视图持有的材质消费者：独立的效果 scope、效果快照与离屏效果层。
    /// </summary>
    internal sealed class Consumer : IDisposable
    {
        private readonly AndroidPhotoMaterial _owner;
        private readonly float _gamma;
        private readonly BackdropEffectScope _scope = new();
        private BackdropEffect _effect = BackdropEffect.None;
        private (float Density, float Width, float Height) _effectKey = (-1f, -1f, -1f);
        private long _footprintBytes;
        private bool _disposed;

        internal Consumer(AndroidPhotoMaterial owner, float gamma)
        {
            _owner = owner;
            _gamma = gamma;
        }

        /// <summary>本消费者效果层的估算占用；尚未绘制为 0。</summary>
        internal long FootprintBytes => _footprintBytes;

        /// <summary>
        /// 按本视图画布与取样上下文绘制共享配方。调用方先裁到自身边界或形状。
        /// </summary>
        /// <param name="canvas">目标画布。</param>
        /// <param name="context">取样上下文（画布原点取自本视图自身）。</param>
        internal void Draw(Canvas canvas, in BackdropDrawContext context)
        {
            if (_disposed) return;

            var source = _owner._layer.Backdrop;
            if (!_owner.IsRealMaterial || !_owner._layer.RecordsBackdrop || !source.HasContent) return;
            if (!canvas.IsHardwareAccelerated) return;

            var effect = EffectFor(context);
            BackdropDrawer.Draw(canvas, source, effect, context);
            _owner.OnConsumerDrawn(this, effect, context);
        }

        /// <summary>取本视图的效果链快照；只在本视图密度或尺寸变化时重建。</summary>
        private BackdropEffect EffectFor(in BackdropDrawContext context)
        {
            var key = (Density: context.Density, Width: context.WidthPx, Height: context.HeightPx);
            if (key == _effectKey) return _effect;

            _effectKey = key;
            _scope.Apply(scope =>
            {
                scope.ColorControls(saturation: _owner._saturation);
                if (_gamma > 0f && Math.Abs(_gamma - 1f) > 1e-6 && OperatingSystem.IsAndroidVersionAtLeast(33))
                    scope.Effect(AndroidGammaToneMapping.CreateEffect(_gamma));
                scope.Blur(scope.DpToPx(_owner._blurDp));
            }, key.Density, key.Width, key.Height);
            // 矩形栏底在自身边界 Clamp，避免透明外扩层稀释屏幕边缘。
            _effect = _scope.Build(paddingOverridePx: 0f);
            return _effect;
        }

        internal void SetFootprint(long bytes) => _footprintBytes = bytes;

        /// <summary>注销占用并释放本视图的效果 scope；之后本消费者不再绘制。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _owner.Unregister(this);
            _scope.Dispose();
            _effect = BackdropEffect.None;
            _footprintBytes = 0;
        }
    }
}
