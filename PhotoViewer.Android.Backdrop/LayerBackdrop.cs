using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 单层取样源，对应上游 <c>backdrops/LayerBackdrop.kt</c>：把一棵子树录进 <c>RenderNode</c>，
/// 之后按「层窗口位置 → 绘制方画布原点」的差值平移画出，供任意视图取样。
/// </summary>
/// <remarks>
/// <para>
/// 上游用 Compose 的 <c>GraphicsLayer</c>（Android 侧同样是 <c>RenderNode</c>），取样时
/// <c>translate(-localPositionOf(coordinates))</c>。这里保留同一算法：
/// <c>translate(层原点 - 画布原点)</c>。
/// </para>
/// <para>
/// 有 <c>RenderEffect</c> 时经一张离屏 <c>RenderNode</c> 走效果链：离屏节点按
/// <see cref="BackdropEffect.PaddingPx"/> 四周外扩、把层节点画到外扩内偏移处，效果挂在离屏节点上，
/// 因此模糊能取到边界外的像素（对应上游把 <c>renderEffect</c> 挂在加大后的
/// <c>GraphicsLayer</c> 上）。离屏录制只在尺寸或裁切变化时重建，层内容靠节点引用自动跟随本帧。
/// </para>
/// </remarks>
public sealed class LayerBackdrop : IBackdrop, IDisposable
{
    private readonly Gfx.RenderNode? _node;
    private Gfx.RenderNode? _effectNode;
    private Gfx.RenderEffect? _effectNodeEffect;
    private int _originX;
    private int _originY;
    private int _width;
    private int _height;
    private int _effectWidth = -1;
    private int _effectHeight = -1;
    private bool _effectDirty = true;
    private bool _disposed;

    /// <summary>建一个取样源；API 29 以下没有可用的 <c>RenderNode</c>，此时恒为不支持。</summary>
    /// <param name="name">调试名，出现在渲染调试工具里。</param>
    public LayerBackdrop(string name = "LayerBackdrop")
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _node = new Gfx.RenderNode(name);
        }
    }

    /// <inheritdoc />
    public bool IsCoordinatesDependent => true;

    /// <summary>本机是否支持层录制（API 29+）。</summary>
    public bool IsSupported => _node is not null;

    /// <summary>本帧是否已录到内容。</summary>
    public bool HasContent =>
        OperatingSystem.IsAndroidVersionAtLeast(29) &&
        _node is { HasDisplayList: true } &&
        _width > 0 &&
        _height > 0;

    /// <summary>已录内容的宽（px）。</summary>
    public int RecordedWidth => _width;

    /// <summary>已录内容的高（px）。</summary>
    public int RecordedHeight => _height;

    /// <summary>供同程序集内的容器把子树录进本层。</summary>
    internal Gfx.RenderNode? Node => _node;

    /// <summary>
    /// 让下次取样重建离屏效果层。只有替换了传给取样的裁切形状才需要调用：
    /// 层尺寸变化由实现按当前录制尺寸自动检测重建，层内容变化靠节点引用自动跟随，两者都不需要调用。
    /// </summary>
    public void Invalidate() => _effectDirty = true;

    /// <summary>记录层在窗口中的位置与尺寸；由 <see cref="LayerBackdropLayout"/> 在每帧录制后调用。</summary>
    internal void SetRecordedBounds(int originX, int originY, int width, int height)
    {
        _originX = originX;
        _originY = originY;
        _width = width;
        _height = height;
    }

    /// <inheritdoc />
    public void DrawBackdrop(Gfx.Canvas canvas, in BackdropDrawContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        var node = _node;
        if (node is null || !HasContent || !canvas.IsHardwareAccelerated) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;

        var save = canvas.Save();
        try
        {
            canvas.Translate(_originX - context.CanvasOriginX, _originY - context.CanvasOriginY);
            canvas.DrawRenderNode(node);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>
    /// 按效果链取样绘制。无效果时等价 <see cref="DrawBackdrop"/>；API 31 以下同样退化为直接绘制
    /// （此时配方本就为空链）。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="effect">效果链快照。</param>
    /// <param name="context">取样上下文。</param>
    /// <param name="clipPath">可选的裁切形状（层坐标），与上游 <c>GraphicsLayer.shape</c> 同义。</param>
    internal void DrawWithEffect(
        Gfx.Canvas canvas,
        in BackdropEffect effect,
        in BackdropDrawContext context,
        Gfx.Path? clipPath)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || effect.IsEmpty)
        {
            DrawBackdrop(canvas, context);
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        var node = _node;
        if (node is null || !HasContent || !canvas.IsHardwareAccelerated) return;

        var padding = (int)MathF.Ceiling(effect.PaddingPx);
        var effectWidth = _width + (padding * 2);
        var effectHeight = _height + (padding * 2);

        var effectNode = _effectNode;
        if (effectNode is null)
        {
            effectNode = new Gfx.RenderNode($"{nameof(LayerBackdrop)}.effect");
            _effectNode = effectNode;
            _effectWidth = -1;
            _effectHeight = -1;
        }

        if (_effectDirty || effectWidth != _effectWidth || effectHeight != _effectHeight)
        {
            var recording = effectNode.BeginRecording(effectWidth, effectHeight);
            try
            {
                if (padding > 0)
                {
                    recording.Translate(padding, padding);
                }

                if (clipPath is not null)
                {
                    recording.ClipPath(clipPath);
                }

                recording.DrawRenderNode(node);
            }
            finally
            {
                effectNode.EndRecording();
                recording.Dispose();
            }

            effectNode.SetPosition(0, 0, effectWidth, effectHeight);
            _effectWidth = effectWidth;
            _effectHeight = effectHeight;
            _effectDirty = false;
        }

        if (!ReferenceEquals(_effectNodeEffect, effect.RenderEffect))
        {
            effectNode.SetRenderEffect(effect.RenderEffect);
            _effectNodeEffect = effect.RenderEffect;
        }

        var save = canvas.Save();
        try
        {
            canvas.Translate(
                _originX - padding - context.CanvasOriginX,
                _originY - padding - context.CanvasOriginY);
            canvas.DrawRenderNode(effectNode);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放层与离屏效果层的显示列表。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _node?.DiscardDisplayList();
            _effectNode?.DiscardDisplayList();
        }

        _effectNodeEffect = null;
        _effectNode = null;
        _width = 0;
        _height = 0;
    }
}
