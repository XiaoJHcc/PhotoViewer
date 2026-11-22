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
/// <c>translate(层原点 - 画布原点)</c>；玻璃元素自身带缩放/旋转时，先按
/// <see cref="BackdropDrawContext.LayerTransform"/> 追加逆变换（上游 <c>InverseLayerScope</c>）。
/// </para>
/// <para>
/// 本类只持有源层录制节点，**不持有离屏效果层**。效果层按绘制消费者归属，由
/// <see cref="BackdropEffectScope"/> 持有并经 <see cref="BackdropEffect"/> 快照带入
/// <see cref="BackdropDrawer"/>；效果层按消费者自己的 <c>context.WidthPx / HeightPx</c> 录制。
/// 这样同一源层上的不同位置/尺寸控件各有独立取样结果，Lens 的 <c>size</c> 与本地取样一致。
/// </para>
/// <para>
/// 源层每次完成新录制时递增 <see cref="IBackdropContentVersion.ContentVersion"/>，消费者效果层据此
/// 决定是否重录显示列表；重录复用同一张效果节点，不新建节点。离屏效果节点在创建时由
/// <see cref="BackdropEffectLayer"/> 强制开启合成层。
/// </para>
/// </remarks>
public sealed class LayerBackdrop : IBackdrop, IBackdropContentVersion, IDisposable
{
    private readonly Gfx.RenderNode? _node;
    private int _originX;
    private int _originY;
    private int _width;
    private int _height;
    private long _contentVersion;
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

    /// <summary>内容代际；消费者效果层据此判断是否需要重录本地取样。</summary>
    long IBackdropContentVersion.ContentVersion => _contentVersion;

    /// <summary>
    /// 让消费者效果层在下一次绘制时重录本地取样。源层每次完成新录制已自动递增内容代际；
    /// 只有外部改动了源层内容而没走 <see cref="SetRecordedBounds"/> 时才需要显式调用。
    /// </summary>
    public void Invalidate() => _contentVersion++;

    /// <summary>
    /// 记录层在窗口中的位置与尺寸；由 <see cref="LayerBackdropLayout"/> 在每帧录制后调用。
    /// </summary>
    /// <param name="originX">层在窗口中的 X。</param>
    /// <param name="originY">层在窗口中的 Y。</param>
    /// <param name="width">录制宽（px）。</param>
    /// <param name="height">录制高（px）。</param>
    internal void SetRecordedBounds(int originX, int originY, int width, int height)
    {
        _originX = originX;
        _originY = originY;
        _width = width;
        _height = height;
        _contentVersion++;
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
            context.LayerTransform.ApplyInverse(canvas);
            canvas.Translate(_originX - context.CanvasOriginX, _originY - context.CanvasOriginY);
            canvas.DrawRenderNode(node);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    /// <summary>释放源层录制节点的显示列表；离屏效果节点归消费者所有。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _node?.DiscardDisplayList();
        }

        _width = 0;
        _height = 0;
        _contentVersion++;
    }
}
