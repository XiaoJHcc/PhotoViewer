using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 可滚动宿主（TOOLBAR-08 / FILTER-07）：文档沿主轴超出可用长度时可滚，
/// 普通重建/换向保留用户偏移并按新几何夹取；不显示滚动条时内容与原先一致。
/// </summary>
internal sealed class AndroidControlBarScrollHost : ViewGroup
{
    /// <summary>滚动条厚度（dp）。</summary>
    private const double ScrollBarThickness = 3;
    /// <summary>滚动条与宿主边缘的间距（dp）。</summary>
    private const double ScrollBarInset = 2;

    private readonly View _child;
    private readonly Scroller _scroller;
    private readonly int _touchSlop;
    private readonly Paint _scrollBarPaint;
    private int _contentMain;
    private int _scroll;
    private float _downX, _downY, _lastX, _lastY;
    private VelocityTracker? _velocity;
    private bool _horizontal = true;

    /// <summary>建立滚动宿主并挂载控制栏。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="child">控制栏视图。</param>
    public AndroidControlBarScrollHost(Context context, View child) : base(context)
    {
        _child = child;
        _scroller = new Scroller(context);
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        _scrollBarPaint = AndroidUiTheme.Paint(AndroidUiTheme.Divider);
        AddView(child);
    }

    /// <summary>当前是否可滚动（文档超出可用长度）。</summary>
    internal bool IsScrollable => _contentMain > ViewportMain + 1;

    /// <summary>当前滚动偏移（px，验收核对）。</summary>
    internal int ScrollOffset => _scroll;

    /// <summary>按布局方向与文档长度更新滚动范围；保留偏移并夹到合法区间。</summary>
    /// <param name="horizontal">分组是否沿水平轴排布（底部控制栏）；否则沿垂直轴（侧边控制栏）。</param>
    /// <param name="viewportMainPx">可见主轴长度（px）。</param>
    /// <param name="contentMainPx">文档主轴长度（px）。</param>
    internal void Configure(bool horizontal, int viewportMainPx, int contentMainPx)
    {
        _horizontal = horizontal;
        _contentMain = Math.Max(Math.Max(0, viewportMainPx), contentMainPx);
        ClampScroll();
        Invalidate();
    }

    private int ViewportMain => _horizontal ? Width : Height;

    private int MaxScroll => Math.Max(0, _contentMain - ViewportMain);

    /// <summary>精确给宿主与子视图落位；子视图尺寸取文档主轴长度与可用交叉轴长度。</summary>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec), height = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(width, height);
        int childWidth = _horizontal ? Math.Max(width, _contentMain) : width;
        int childHeight = _horizontal ? height : Math.Max(height, _contentMain);
        _child.Measure(MeasureSpec.MakeMeasureSpec(childWidth, MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(childHeight, MeasureSpecMode.Exactly));
    }

    /// <summary>按滚动偏移平移子视图。</summary>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        ClampScroll();
        int childWidth = _horizontal ? Math.Max(Width, _contentMain) : Width;
        int childHeight = _horizontal ? Height : Math.Max(Height, _contentMain);
        int left = _horizontal ? -_scroll : 0;
        int top = _horizontal ? 0 : -_scroll;
        _child.Layout(left, top, left + childWidth, top + childHeight);
    }

    /// <summary>主轴位移超过触摸阈值后接管手势，未拖动时仍由控制栏处理点按。</summary>
    public override bool OnInterceptTouchEvent(MotionEvent? e)
    {
        if (e == null || !IsScrollable) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = _lastX = e.GetX();
                _downY = _lastY = e.GetY();
                break;
            case MotionEventActions.Move:
                float primary = _horizontal ? e.GetX() - _downX : e.GetY() - _downY;
                float secondary = _horizontal ? Math.Abs(e.GetY() - _downY) : Math.Abs(e.GetX() - _downX);
                if (Math.Abs(primary) > _touchSlop && Math.Abs(primary) > secondary)
                {
                    _lastX = e.GetX();
                    _lastY = e.GetY();
                    BeginDrag(e);
                    return true;
                }
                break;
        }
        return false;
    }

    /// <summary>拖动滚动、松手按速度继续滑动。</summary>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastX = e.GetX();
                _lastY = e.GetY();
                BeginDrag(e);
                return true;
            case MotionEventActions.Move:
                float delta = _horizontal ? _lastX - e.GetX() : _lastY - e.GetY();
                _scroll += (int)Math.Round(delta);
                ClampScroll();
                _lastX = e.GetX();
                _lastY = e.GetY();
                _velocity?.AddMovement(e);
                Invalidate();
                return true;
            case MotionEventActions.Up:
                Fling(e);
                return true;
            case MotionEventActions.Cancel:
                EndDrag();
                return true;
            default:
                return true;
        }
    }

    /// <summary>滚动期间绘制仅在溢出时可见的细滚动条。</summary>
    protected override void DispatchDraw(Canvas canvas)
    {
        base.DispatchDraw(canvas);
        if (!IsScrollable || MaxScroll <= 0) return;
        float density = (float)AndroidUiTheme.Density(Context!);
        float thickness = (float)(ScrollBarThickness * density);
        float inset = (float)(ScrollBarInset * density);
        float ratio = (float)ViewportMain / _contentMain;
        float progress = (float)_scroll / MaxScroll;
        if (_horizontal)
        {
            float length = Math.Max(thickness * 4, Width * ratio);
            float left = (Width - length) * progress;
            canvas.DrawRoundRect(left, Height - thickness - inset, left + length, Height - inset,
                thickness / 2, thickness / 2, _scrollBarPaint);
        }
        else
        {
            float length = Math.Max(thickness * 4, Height * ratio);
            float top = (Height - length) * progress;
            canvas.DrawRoundRect(Width - thickness - inset, top, Width - inset, top + length,
                thickness / 2, thickness / 2, _scrollBarPaint);
        }
    }

    /// <summary>驱动惯性滚动。</summary>
    public override void ComputeScroll()
    {
        if (!_scroller.ComputeScrollOffset()) return;
        _scroll = _horizontal ? _scroller.CurrX : _scroller.CurrY;
        ClampScroll();
        Invalidate();
        PostInvalidateOnAnimation();
    }

    private void BeginDrag(MotionEvent e)
    {
        _scroller.ForceFinished(true);
        EndDrag();
        _velocity = VelocityTracker.Obtain();
        _velocity?.AddMovement(e);
        Parent?.RequestDisallowInterceptTouchEvent(true);
    }

    private void Fling(MotionEvent e)
    {
        var velocity = _velocity;
        if (velocity == null) return;
        velocity.AddMovement(e);
        velocity.ComputeCurrentVelocity(1000);
        float speed = _horizontal ? velocity.XVelocity : velocity.YVelocity;
        int max = MaxScroll;
        if (Math.Abs(speed) > 50)
        {
            if (_horizontal) _scroller.Fling(_scroll, 0, (int)-speed, 0, 0, max, 0, 0);
            else _scroller.Fling(0, _scroll, 0, (int)-speed, 0, 0, 0, max);
            PostInvalidateOnAnimation();
        }
        EndDrag();
    }

    private void EndDrag()
    {
        var velocity = _velocity;
        _velocity = null;
        velocity?.Recycle();
    }

    private void ClampScroll()
    {
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }
}
