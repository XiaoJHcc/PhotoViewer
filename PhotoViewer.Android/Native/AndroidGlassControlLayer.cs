using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Android.Backdrop;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 主屏玻璃控件绘制层：位于屏幕层内、场景层之后，只从场景层取样；真实锚点视图仍留在原布局中
/// 负责布局、触摸、滚动、读屏与焦点，本层按锚点真实可见窗口矩形绘制材质、内容、按压与悬停效果。
/// </summary>
/// <remarks>
/// 每个控件由 <see cref="AndroidGlassControlMaterial"/> 持有独立的 scope、效果快照、轮廓与弹簧，
/// 不共享效果节点；控件被销毁时由创建方 <see cref="Unregister"/>，本层释放其全部绘制资源。
/// 本层不接收触摸或读屏焦点，也不截屏或回读窗口。
/// </remarks>
internal sealed class AndroidGlassControlLayer : View
{
    private readonly IBackdrop _source;
    private readonly object _stateGate = new();
    // 按引用比较：锚点被释放后其 Java 端已不可用，默认相等比较会触碰已释放对象。
    private readonly Dictionary<View, AndroidGlassControlMaterial> _states =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<AndroidGlassControlMaterial> _order = [];
    private readonly int[] _layerLocation = new int[2];
    private readonly int[] _location = new int[2];
    private readonly int[] _parentLocation = new int[2];
    private long _lastFrameMs;
    private bool _disposed;

    /// <summary>建玻璃控件层。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="scene">场景层录制容器；本层只从场景层取样。</param>
    public AndroidGlassControlLayer(Context context, LayerBackdropLayout scene) : base(context)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _source = scene.Backdrop;
        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.No;
        SetWillNotDraw(false);
    }

    /// <summary>取样源（验收读取：与场景层同一份 <see cref="IBackdrop"/>）。</summary>
    internal IBackdrop Source => _source;

    /// <summary>当前已注册的控件数（验收读取）。</summary>
    internal int ControlCount => _states.Count;

    /// <summary>所有控件效果层的估算占用之和（RGBA8）；低版本或未绘制为 0。</summary>
    internal long FootprintBytes
    {
        get
        {
            long total = 0;
            lock (_stateGate)
            {
                foreach (var state in _order) total += state.FootprintBytes;
            }
            return total;
        }
    }

    /// <summary>
    /// 注册一个真实锚点。按钮、合体与星级控件分别按自身类型取内容；锚点随即改为由本层绘制，
    /// 材质与内容只在本层出现一次。
    /// </summary>
    /// <param name="anchor">锚点视图。</param>
    internal void Register(View anchor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(anchor);
        if (_states.ContainsKey(anchor)) return;

        GlassControlKind kind;
        switch (anchor)
        {
            case AndroidGlassButton button:
                button.UseGlassAnchor();
                kind = GlassControlKind.Button;
                break;
            case AndroidGlassCombo combo:
                combo.UseGlassAnchor();
                kind = GlassControlKind.Combo;
                break;
            case AndroidRatingControl rating:
                rating.UseGlassAnchor();
                kind = GlassControlKind.Rating;
                break;
            default:
                return;
        }

        var state = new AndroidGlassControlMaterial(this, anchor, kind);
        lock (_stateGate)
        {
            _states.Add(anchor, state);
            _order.Add(state);
        }
        if (anchor is IGlassAnchorContent content) content.GlassContentInvalidated += OnContentInvalidated;
        state.SyncTargets();
        Invalidate();
    }

    /// <summary>注销锚点并释放其材质、绘制器、监听器与离屏占用。</summary>
    /// <param name="anchor">锚点视图。</param>
    internal void Unregister(View anchor)
    {
        AndroidGlassControlMaterial? state;
        lock (_stateGate)
        {
            if (!_states.Remove(anchor, out state)) return;
            _order.Remove(state);
        }
        if (anchor is IGlassAnchorContent content) content.GlassContentInvalidated -= OnContentInvalidated;
        state.Dispose();
        Invalidate();
    }

    /// <summary>全部控件的验收快照（窗口 px）。</summary>
    internal IReadOnlyList<GlassControlSnapshot> ControlsForChecks()
    {
        var snapshots = new List<GlassControlSnapshot>(_order.Count);
        foreach (var state in _order) snapshots.Add(state.Snapshot());
        return snapshots;
    }

    /// <summary>验收用：直接设置锚点的悬停状态与位置，不经过系统 hover 派发。</summary>
    /// <param name="anchor">锚点视图。</param>
    /// <param name="hovered">是否悬停。</param>
    /// <param name="x">锚点本地 X（px）。</param>
    /// <param name="y">锚点本地 Y（px）。</param>
    internal void SetHoverForChecks(View anchor, bool hovered, float x, float y)
    {
        if (_states.TryGetValue(anchor, out var state)) state.SetHoverForChecks(hovered, x, y);
    }

    /// <summary>指定锚点的验收快照；未注册返回 false。</summary>
    /// <param name="anchor">锚点视图。</param>
    /// <param name="snapshot">快照。</param>
    /// <returns>已注册返回 true。</returns>
    internal bool TryGetControlForChecks(View anchor, out GlassControlSnapshot snapshot)
    {
        if (_states.TryGetValue(anchor, out var state))
        {
            snapshot = state.Snapshot();
            return true;
        }
        snapshot = default;
        return false;
    }

    /// <inheritdoc />
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(
            MeasureSpec.GetSize(widthMeasureSpec),
            MeasureSpec.GetSize(heightMeasureSpec));
    }

    /// <inheritdoc />
    protected override void OnDraw(Canvas? canvas)
    {
        if (canvas is null || _disposed || _order.Count == 0) return;

        long now = global::Android.OS.SystemClock.UptimeMillis();
        float deltaSeconds = _lastFrameMs == 0
            ? 0f
            : Math.Max(0f, (now - _lastFrameMs) / 1000f);
        _lastFrameMs = now;
        GetLocationInWindow(_layerLocation);
        float density = Resources?.DisplayMetrics?.Density ?? 1f;
        bool animating = false;

        foreach (var state in _order)
        {
            var anchor = state.Anchor;
            if (!anchor.IsShown || anchor.Visibility != ViewStates.Visible) continue;
            var clip = ComputeClip(anchor);
            if (clip.IsEmpty) continue;

            anchor.GetLocationInWindow(_location);
            var rect = new Rect(
                _location[0],
                _location[1],
                _location[0] + anchor.Width,
                _location[1] + anchor.Height);
            animating |= state.Advance(deltaSeconds);
            state.Draw(canvas, rect, clip, _layerLocation[0], _layerLocation[1], density);
        }

        if (animating) PostInvalidateOnAnimation();
        else _lastFrameMs = 0;
    }

    /// <summary>
    /// 锚点可见区域：从本层窗口矩形开始，与锚点上方真正裁切内容的滚动宿主窗口矩形相交；
    /// 不含锚点自身与纯布局容器，因此按压投影可以溢出控件边界，但不会越过滚动宿主裁切。
    /// </summary>
    private Rect ComputeClip(View anchor)
    {
        var clip = new Rect(
            _layerLocation[0],
            _layerLocation[1],
            _layerLocation[0] + Width,
            _layerLocation[1] + Height);
        View? parent = anchor.Parent as View;
        while (parent != null && !ReferenceEquals(parent, this))
        {
            if (!parent.IsShown || parent.Visibility != ViewStates.Visible) return new Rect();
            if (parent is AndroidControlBarScrollHost)
            {
                parent.GetLocationInWindow(_parentLocation);
                var bounds = new Rect(
                    _parentLocation[0],
                    _parentLocation[1],
                    _parentLocation[0] + parent.Width,
                    _parentLocation[1] + parent.Height);
                if (!clip.Intersect(bounds)) return new Rect();
            }
            parent = parent.Parent as View;
        }
        return clip;
    }

    private void OnContentInvalidated()
    {
        foreach (var state in _order) state.SyncTargets();
        Invalidate();
    }

    /// <summary>释放全部控件的绘制资源与监听器。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var state in _order)
            {
                if (state.Anchor is IGlassAnchorContent content) content.GlassContentInvalidated -= OnContentInvalidated;
                state.Dispose();
            }
            _order.Clear();
            _states.Clear();
        }
        base.Dispose(disposing);
    }
}
