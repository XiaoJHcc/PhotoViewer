using System;
using System.Runtime.Versioning;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 单个绘制消费者的离屏效果层，对应上游 <c>DrawBackdropModifier</c> 里属于玻璃元素自己的
/// <c>GraphicsLayer</c>。由 <see cref="BackdropEffectScope"/> 持有，经
/// <see cref="BackdropEffect"/> 快照交给 <see cref="BackdropDrawer"/> 使用。
/// </summary>
/// <remarks>
/// <para>
/// 节点尺寸只取消费者的 <c>context.WidthPx / HeightPx + 2 × padding</c>，与源层大小无关；源层按
/// 「源层窗口原点 − 消费者画布原点」画进节点，再把节点平移 <c>(−padding, −padding)</c> 上屏。
/// 元素的缩放/旋转只在录制背景取样时求逆，效果节点上屏仍跟随元素自身的画布变换。
/// 因此 Lens 的 <c>size</c>（消费者尺寸）与节点内的本地坐标一致，控件不在源层原点也不影响。
/// </para>
/// <para>
/// 同一源层上的每个消费者各自持有一份本类实例，绝不能共享：节点是显示列表引用，同一帧内两个
/// 消费者若共用节点，后录制的内容会覆盖先绘制的内容。
/// </para>
/// <para>
/// 源层内容变化（<see cref="IBackdropContentVersion"/> 代际变化）、消费者位置/尺寸、裁切形状或
/// 效果引用变化时重录显示列表；节点本身复用，不逐帧新建。带裁切形状时每次都重录，避免复用
/// 已被改写的 <see cref="Gfx.Path"/>。
/// </para>
/// </remarks>
internal sealed class BackdropEffectLayer : IDisposable
{
    private Gfx.RenderNode? _node;
    private IBackdrop? _backdrop;
    private long _contentVersion = long.MinValue;
    private int _width;
    private int _height;
    private float _padding;
    private float _canvasOriginX;
    private float _canvasOriginY;
    private float _scaleX;
    private float _scaleY;
    private float _rotationZ;
    private Gfx.RenderEffect? _renderEffect;
    private bool _hasRecorded;
    private bool _disposed;

    /// <summary>验收用：显示列表重录次数。</summary>
    internal int RecordCount { get; private set; }

    /// <summary>按消费者上下文绘制取样与效果。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="backdrop">取样源。</param>
    /// <param name="effect">效果链快照。</param>
    /// <param name="context">消费者取样上下文。</param>
    /// <param name="clipPath">可选的裁切形状（消费者坐标）。</param>
    public void Draw(
        Gfx.Canvas canvas,
        IBackdrop backdrop,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(backdrop);

        if (!OperatingSystem.IsAndroidVersionAtLeast(29) ||
            !OperatingSystem.IsAndroidVersionAtLeast(31) ||
            effect.IsEmpty ||
            !canvas.IsHardwareAccelerated)
        {
            backdrop.DrawBackdrop(canvas, context);
            return;
        }

        if (context.WidthPx <= 0f || context.HeightPx <= 0f) return;

        var padding = MathF.Max(effect.PaddingPx, 0f);
        var width = (int)MathF.Ceiling(context.WidthPx + (padding * 2f));
        var height = (int)MathF.Ceiling(context.HeightPx + (padding * 2f));
        if (width <= 0 || height <= 0) return;

        var contentVersion = backdrop is IBackdropContentVersion versioned
            ? versioned.ContentVersion
            : -1L;

        if (!CanReuse(backdrop, contentVersion, width, height, padding, effect, context, clipPath))
        {
            Record(backdrop, contentVersion, width, height, padding, effect, context, clipPath);
        }

        var node = _node!;
        var save = canvas.Save();
        try
        {
            canvas.Translate(-padding, -padding);
            canvas.DrawRenderNode(node);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>丢弃离屏节点，下次绘制时重新分配并录制。</summary>
    internal void Discard()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _node?.DiscardDisplayList();
        }

        _node = null;
        _backdrop = null;
        _renderEffect = null;
        _hasRecorded = false;
    }

    /// <summary>释放离屏节点的显示列表。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Discard();
    }

    private bool CanReuse(
        IBackdrop backdrop,
        long contentVersion,
        int width,
        int height,
        float padding,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath)
    {
        // 裁切形状是可变 Path，无法只靠引用判断内容；带裁切时一律重录。
        if (clipPath is not null || !_hasRecorded) return false;

        return contentVersion >= 0 &&
            ReferenceEquals(_backdrop, backdrop) &&
            _contentVersion == contentVersion &&
            _width == width &&
            _height == height &&
            _padding == padding &&
            _canvasOriginX == context.CanvasOriginX &&
            _canvasOriginY == context.CanvasOriginY &&
            _scaleX == context.LayerTransform.ScaleX &&
            _scaleY == context.LayerTransform.ScaleY &&
            _rotationZ == context.LayerTransform.RotationZ &&
            ReferenceEquals(_renderEffect, effect.RenderEffect);
    }

    [SupportedOSPlatform("android31.0")]
    private void Record(
        IBackdrop backdrop,
        long contentVersion,
        int width,
        int height,
        float padding,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath)
    {
        var node = _node;
        if (node is null)
        {
            node = new Gfx.RenderNode("Backdrop.effect");
            node.SetUseCompositingLayer(true, null);
            _node = node;
        }

        var recording = node.BeginRecording(width, height);
        try
        {
            if (padding > 0f)
            {
                recording.Translate(padding, padding);
            }

            if (clipPath is not null)
            {
                recording.ClipPath(clipPath);
            }

            // 与上游一致：逆变换只作用于背景取样录制；效果节点上屏仍跟随元素自身的画布变换。
            backdrop.DrawBackdrop(recording, context);
        }
        finally
        {
            node.EndRecording();
            recording.Dispose();
        }

        node.SetPosition(0, 0, width, height);
        // 重录之后重新设置效果：不假设 BeginRecording / SetPosition 会保留节点上的 RenderEffect。
        node.SetRenderEffect(effect.RenderEffect);

        _backdrop = backdrop;
        _contentVersion = contentVersion;
        _width = width;
        _height = height;
        _padding = padding;
        _canvasOriginX = context.CanvasOriginX;
        _canvasOriginY = context.CanvasOriginY;
        _scaleX = context.LayerTransform.ScaleX;
        _scaleY = context.LayerTransform.ScaleY;
        _rotationZ = context.LayerTransform.RotationZ;
        _renderEffect = effect.RenderEffect;
        RecordCount++;
        // 带裁切的录制只用于本帧，下一帧重录。
        _hasRecorded = clipPath is null;
    }
}
