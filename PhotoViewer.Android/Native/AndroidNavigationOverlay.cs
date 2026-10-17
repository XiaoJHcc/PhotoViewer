using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Shared;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 缩放指示器（导航图，A3）：非适应态时显示在主图区右下角——半透明 #222 圆角宿主、GPU 小目标渲染的
/// 等比缩略图、白色 1 点可见框与物理缩放百分比；点按/拖动导航图把照片居中到对应位置（触屏增强）。
/// </summary>
internal sealed class AndroidNavigationOverlay : View
{
    private const double Padding = 8;
    private const double LabelHeight = 15;
    private readonly Paint _backgroundPaint;
    private readonly Paint _framePaint;
    private readonly Paint _labelPaint;
    private Bitmap? _bitmap;
    private double _navWidthDp, _navHeightDp;
    private (double X, double Y, double Width, double Height) _viewport;
    private string _zoomText = "100%";
    private Action<double, double>? _centerRequested;

    /// <summary>导航图当前位图（验收核对）。</summary>
    internal Bitmap? Bitmap => _bitmap;
    /// <summary>导航空白区域（dp）。</summary>
    internal (double Width, double Height) NavSize => (_navWidthDp, _navHeightDp);
    /// <summary>可见框矩形（dp，相对导航图）。</summary>
    internal (double X, double Y, double Width, double Height) ViewportRect => _viewport;
    /// <summary>百分比文本。</summary>
    internal string ZoomText => _zoomText;

    /// <summary>创建导航图视图。</summary>
    /// <param name="context">宿主上下文。</param>
    public AndroidNavigationOverlay(Context context) : base(context)
    {
        _backgroundPaint = AndroidUiTheme.Paint(Color.Argb(200, 34, 34, 34)!);
        _framePaint = AndroidUiTheme.Paint(Color.White, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _labelPaint = AndroidUiTheme.TextPaint(context, 12, Color.White, bold: true);
        SetWillNotDraw(false);
        Clickable = true;
    }

    /// <summary>绑定导航图定位回调。</summary>
    /// <param name="centerRequested">归一化点定位回调。</param>
    internal void Wire(Action<double, double> centerRequested) => _centerRequested = centerRequested;

    /// <summary>更新导航图内容与可见框。</summary>
    /// <param name="bitmap">导航图位图（可为 null，保持旧图）。</param>
    /// <param name="navWidth">导航图宽（dp）。</param>
    /// <param name="navHeight">导航图高（dp）。</param>
    /// <param name="viewport">可见区域（dp，导航图坐标）。</param>
    /// <param name="zoomText">百分比文本。</param>
    internal void SetState(Bitmap? bitmap, double navWidth, double navHeight,
        (double X, double Y, double Width, double Height) viewport, string zoomText)
    {
        if (bitmap != null) _bitmap = bitmap;
        _navWidthDp = navWidth;
        _navHeightDp = navHeight;
        _viewport = viewport;
        _zoomText = zoomText;
        Invalidate();
    }

    /// <summary>清空位图（无照片时释放引用，位图由 GC 处理）。</summary>
    internal void Clear()
    {
        _bitmap = null;
        Invalidate();
    }

    /// <summary>导航图宿主总尺寸（dp）。</summary>
    /// <returns>宽高。</returns>
    internal (double Width, double Height) HostSize => (_navWidthDp + Padding * 2,
        _navHeightDp + Padding * 2 + 8 + LabelHeight + 2);

    /// <summary>绘制底、缩略图、可见框与百分比。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas? canvas)
    {
        base.OnDraw(canvas);
        if (canvas == null) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        var host = new RectF(0, 0, Width, Height);
        AndroidUiTheme.DrawRounded(canvas, host, 6 * density, Color.Argb(200, 34, 34, 34)!);
        var imageRect = new RectF((float)(Padding * density), (float)(Padding * density),
            (float)((Padding + _navWidthDp) * density), (float)((Padding + _navHeightDp) * density));
        if (_bitmap != null && !_bitmap.IsRecycled)
            canvas.DrawBitmap(_bitmap, new Rect(0, 0, _bitmap.Width, _bitmap.Height), imageRect, null);
        var frame = new RectF(
            imageRect.Left + (float)(_viewport.X * density),
            imageRect.Top + (float)(_viewport.Y * density),
            imageRect.Left + (float)((_viewport.X + _viewport.Width) * density),
            imageRect.Top + (float)((_viewport.Y + _viewport.Height) * density));
        if (frame.Width() >= 1 && frame.Height() >= 1) canvas.DrawRect(frame, _framePaint);
        float labelTop = (float)((Padding + _navHeightDp + Padding) * density);
        AndroidUiTheme.DrawCentered(canvas, _zoomText, _labelPaint, host.CenterX(),
            labelTop + _labelPaint.TextSize, host.Width() - 4 * density);
    }

    /// <summary>点按/拖动导航图：把触点对应的归一化图像点居中到主图视口。</summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move)
        {
            double density = AndroidUiTheme.Density(Context!);
            double u = (e.GetX() / density - Padding) / Math.Max(1, _navWidthDp);
            double v = (e.GetY() / density - Padding) / Math.Max(1, _navHeightDp);
            _centerRequested?.Invoke(Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1));
            return true;
        }
        return e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel;
    }
}
