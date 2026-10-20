using AppKit;
using CoreGraphics;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 原生细节预览瓦片（中心 / 对焦点）。外观完全复用 <see cref="MacDiagnosticTile"/> 的外框、内层圆角裁切、
/// 左上角药丸角标与 1:1 内容绘制，只额外加上：鼠标进入/离开切换绿色高亮描边并把绿框位置回报给主图、
/// 双击请求主图定位到裁剪中心并放大到 100%。
/// 缩放按物理 1:1 呈现（裁剪边长 = 瓦片逻辑边长 × 窗口物理倍率，见 ANALYSIS-06）。
/// 位图由调用方传入并持有，本视图只引用不释放。
/// </summary>
internal sealed class MacDetailPreview : NSView
{
    private readonly MacDiagnosticTile _tile = new();
    private readonly HighlightOverlay _overlay = new();
    private double _centerU = .5, _centerV = .5;
    private double? _frameWidth, _frameHeight;
    private bool _highlighted;
    private NSTrackingArea? _tracking;

    /// <summary>双击内容区：请求主图定位到裁剪中心并放大（参数为归一化中心）。</summary>
    public Action<double, double>? FocusRequested { get; set; }

    /// <summary>高亮状态变化：参数依次为是否高亮、归一化中心、对焦框归一化宽高（无对焦框时为 null）。</summary>
    public Action<bool, double, double, double?, double?>? HighlightChanged { get; set; }

    /// <summary>顶边为原点的布局坐标。</summary>
    public override bool IsFlipped => true;

    /// <summary>构造：方形内容按 1:1 呈现，叠加层只画高亮描边。</summary>
    public MacDetailPreview()
    {
        _tile.AspectRatio = 1;
        _overlay.Hidden = true;
        AddSubview(_tile);
        AddSubview(_overlay);
    }

    /// <summary>左上角药丸角标；空则隐藏。</summary>
    public string? CornerLabel
    {
        get => _tile.CornerLabel;
        set => _tile.CornerLabel = value;
    }

    /// <summary>设置裁剪内容（已按显示朝向裁好的 1:1 方块）；null 清空。引用比较，相同实例不重绘。</summary>
    /// <param name="image">由调用方持有的位图。</param>
    public void SetContent(NSImage? image) => _tile.Source = image;

    /// <summary>当前是否已贴上裁剪内容（代理期按旧壳口径留空）。</summary>
    public bool HasContent => _tile.Source != null;

    /// <summary>已贴上的裁剪位图的源像素边长（供验收核对物理 1:1）；无内容时为 0。</summary>
    internal int ContentPixels => (int)(_tile.Source?.CGImage?.Width ?? 0);

    /// <summary>绿框是否使用裁剪窗口（该瓦片不是对焦点瓦片，没有独立对焦框）。</summary>
    internal bool UsesCropWindow => _frameWidth == null;

    /// <summary>设置裁剪中心与对焦框（归一化）；用于双击定位与主图绿框。</summary>
    /// <param name="u">归一化中心横坐标。</param>
    /// <param name="v">归一化中心纵坐标。</param>
    /// <param name="frameWidth">对焦框归一化宽；null 表示使用裁剪区。</param>
    /// <param name="frameHeight">对焦框归一化高。</param>
    public void SetCenter(double u, double v, double? frameWidth, double? frameHeight)
    {
        _centerU = u;
        _centerV = v;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
    }

    /// <summary>分析栏隐藏时清除高亮（同时清掉主图绿框）。</summary>
    public void ClearHighlight()
    {
        if (!_highlighted) return;
        ApplyHighlight(false);
        HighlightChanged?.Invoke(false, _centerU, _centerV, _frameWidth, _frameHeight);
    }

    /// <summary>尺寸变化时让内部瓦片铺满本视图。</summary>
    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        _tile.Frame = new CGRect(0, 0, newSize.Width, newSize.Height);
        _overlay.Frame = new CGRect(0, 0, newSize.Width, newSize.Height);
    }

    /// <summary>本视图整体参与命中测试，内层瓦片不单独接收事件，保证 hover/双击判定一致。</summary>
    public override NSView? HitTest(CGPoint point) => Frame.Contains(point) ? this : null;

    /// <summary>新增/更新鼠标跟踪区，用于 hover 高亮。</summary>
    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking != null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow, this, null);
        AddTrackingArea(_tracking);
    }

    /// <summary>鼠标进入：绿色高亮描边 + 主图绿框。</summary>
    public override void MouseEntered(NSEvent theEvent)
    {
        ApplyHighlight(true);
        HighlightChanged?.Invoke(true, _centerU, _centerV, _frameWidth, _frameHeight);
    }

    /// <summary>鼠标离开：恢复默认描边并清除主图绿框。</summary>
    public override void MouseExited(NSEvent theEvent)
    {
        if (!_highlighted) return;
        ApplyHighlight(false);
        HighlightChanged?.Invoke(false, _centerU, _centerV, _frameWidth, _frameHeight);
    }

    /// <summary>双击：请求主图按裁剪中心放大到 100%。</summary>
    public override void MouseDown(NSEvent theEvent)
    {
        if (theEvent.ClickCount >= 2) FocusRequested?.Invoke(_centerU, _centerV);
    }

    /// <summary>切换高亮描边并重绘。</summary>
    private void ApplyHighlight(bool highlighted)
    {
        if (_highlighted == highlighted) return;
        _highlighted = highlighted;
        _overlay.Hidden = !highlighted;
    }

    /// <summary>高亮描边层：只画一条 <c>#00FF00</c> 圆角描边，不拦截鼠标。</summary>
    private sealed class HighlightOverlay : NSView
    {
        /// <summary>顶边为原点的布局坐标。</summary>
        public override bool IsFlipped => true;

        /// <summary>不参与命中测试，鼠标由外层细节预览处理。</summary>
        public override NSView? HitTest(CGPoint point) => null;

        /// <summary>按外框同样的圆角半径画绿色描边。</summary>
        public override void DrawRect(CGRect dirtyRect)
        {
            double side = Math.Min((double)Bounds.Width, (double)Bounds.Height);
            if (side <= 0) return;
            var square = new CGRect(.5f, .5f, (nfloat)side - 1, (nfloat)side - 1);
            using var path = NSBezierPath.FromRoundedRect(square, 6, 6);
            path.LineWidth = 1;
            NSColor.FromRgb(0, 255, 0).SetStroke();
            path.Stroke();
        }
    }
}
