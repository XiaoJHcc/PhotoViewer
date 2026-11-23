using System;
using System.Runtime.Versioning;
using Android.Content;
using Android.Util;
using Android.Views;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 单层录制容器，对应上游 <c>Modifier.layerBackdrop</c>：子视图照常上屏，同时整棵子树录进
/// <see cref="Backdrop"/> 的 <c>RenderNode</c>，供别处的玻璃控件取样。
/// </summary>
/// <remarks>
/// <para>
/// 布局沿用 <see cref="FrameLayout"/>（即上游 <c>layerBackdrop</c> 不改布局的行为）：本容器只加录制，
/// 不改变子视图的量测与摆放。
/// </para>
/// <para>
/// 取样方必须位于本容器**之外**且在层之后绘制（上游「玻璃元素不得位于自己取样的子树内」）；
/// 子树内容变化时可订阅 <see cref="DescendantInvalidated"/> 将取样方标脏，在同次遍历按该顺序绘制。
/// </para>
/// <para>
/// 软件画布（<c>View.draw(Canvas)</c> 离屏回读等）不录制：<c>Canvas.drawRenderNode</c> 只支持硬件画布。
/// </para>
/// </remarks>
public class LayerBackdropLayout : FrameLayout
{
    private readonly int[] _location = new int[2];
    private bool _disposed;

    /// <summary>建一个录制容器。</summary>
    /// <param name="context">宿主上下文。</param>
    public LayerBackdropLayout(Context context) : this(context, null)
    {
    }

    /// <summary>建一个录制容器。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="attrs">XML 属性。</param>
    public LayerBackdropLayout(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        Backdrop = new LayerBackdrop();
    }

    public LayerBackdropLayout(IntPtr javaReference, global::Android.Runtime.JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        Backdrop = new LayerBackdrop();
    }

    /// <summary>本容器的取样源。</summary>
    public LayerBackdrop Backdrop { get; }

    /// <summary>是否录制（默认录制；不取样时可关掉省掉一半绘制）。</summary>
    public bool RecordsBackdrop { get; set; } = true;

    /// <summary>本帧重录完成后触发。</summary>
    public event EventHandler? Recorded;

    /// <summary>硬件渲染子树失效时触发，不依赖本容器是否重录。</summary>
    public event Action? DescendantInvalidated;

    [SupportedOSPlatform("android26.0")]
    public override void OnDescendantInvalidated(View child, View target)
    {
        base.OnDescendantInvalidated(child, target);
        if (RecordsBackdrop) DescendantInvalidated?.Invoke();
    }

    /// <summary>
    /// 释放取样源。<see cref="Backdrop"/> 的 <c>RenderNode</c> 归本容器所有，
    /// 视图正常销毁（移出窗口后被释放）时随之回收，不需要宿主另外记账。
    /// </summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Backdrop.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void DispatchDraw(Gfx.Canvas? canvas)
    {
        if (canvas is null || _disposed || !RecordsBackdrop
            || !OperatingSystem.IsAndroidVersionAtLeast(29) || !canvas.IsHardwareAccelerated)
        {
            base.DispatchDraw(canvas!);
            return;
        }

        RecordLayer(canvas);
    }

    [SupportedOSPlatform("android29.0")]
    private void RecordLayer(Gfx.Canvas canvas)
    {
        var node = Backdrop.Node;
        if (node is null) return;

        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0)
        {
            base.DispatchDraw(canvas);
            return;
        }

        node.SetPosition(0, 0, width, height);
        var recording = node.BeginRecording(width, height);
        try
        {
            Background?.Draw(recording);
            base.DispatchDraw(recording);
        }
        finally
        {
            node.EndRecording();
            recording.Dispose();
        }

        GetLocationInWindow(_location);
        Backdrop.SetRecordedBounds(_location[0], _location[1], width, height);
        canvas.DrawRenderNode(node);
        Recorded?.Invoke(this, EventArgs.Empty);
    }
}
