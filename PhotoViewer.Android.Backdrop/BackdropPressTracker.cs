using System;
using Android.Views;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 按下即起手、无触控容差的按压/拖动跟踪，对应上游示例 <c>utils/DragGestureInspector.kt</c> 的
/// <c>inspectDragGestures</c>。
/// </summary>
/// <remarks>
/// <para>
/// 上游在 <c>Initial</c> 与主通道各取一次 Down，随后立刻回调起手与零位移，不检查系统触控容差；
/// 追踪中的手指抬起而其他手指仍按下时，改用另一根手指继续，不结束手势。Views 侧按
/// <see cref="MotionEvent"/> 的动作映射：Down 起手、Move 跟手、PointerUp 换手、Up 结束、Cancel 取消。
/// </para>
/// <para>
/// 祖先容器拦截触摸时系统会送 Cancel，本类按取消处理；是否在越过滚动容差后把手势交还滚动，
/// 属于接入层行为，不在这里判断。
/// </para>
/// <para>
/// 坐标是事件接收视图的局部坐标。接到承担布局、触摸与读屏的锚点视图上，不要接到带视觉
/// 缩放/平移的玻璃材质视图上：视图矩阵会反变换触点坐标，使跟手位移被自身形变抵消。
/// 上游指针坐标同样不受 <c>graphicsLayer</c> 影响。
/// </para>
/// </remarks>
public sealed class BackdropPressTracker
{
    private int _pointerId = -1;
    private bool _tracking;

    /// <summary>按下起手，参数为触点相对视图的 X / Y。</summary>
    public event Action<float, float>? PressStarted;

    /// <summary>跟手移动，参数为触点相对视图的 X / Y。</summary>
    public event Action<float, float>? Moved;

    /// <summary>抬手结束。</summary>
    public event Action? PressEnded;

    /// <summary>手势取消（被拦截、手指丢失或收到 Cancel）。</summary>
    public event Action? PressCancelled;

    /// <summary>是否正在跟踪一根手指。</summary>
    public bool IsTracking => _tracking;

    /// <summary>当前跟踪的手指 id；未跟踪时为 -1。</summary>
    public int PointerId => _pointerId;

    /// <summary>
    /// 处理一次触摸事件；返回是否由本跟踪器消费。起手后到抬手或取消前都消费。
    /// </summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                if (_tracking) Cancel();
                _pointerId = e.GetPointerId(0);
                _tracking = true;
                PressStarted?.Invoke(e.GetX(0), e.GetY(0));
                return true;

            case MotionEventActions.Move:
                if (!_tracking) return false;

                var moveIndex = e.FindPointerIndex(_pointerId);
                if (moveIndex < 0)
                {
                    Cancel();
                    return true;
                }

                Moved?.Invoke(e.GetX(moveIndex), e.GetY(moveIndex));
                return true;

            case MotionEventActions.PointerUp:
                if (!_tracking) return false;

                var upIndex = e.ActionIndex;
                if (upIndex >= 0 && upIndex < e.PointerCount &&
                    e.GetPointerId(upIndex) == _pointerId)
                {
                    var nextIndex = upIndex == 0 ? 1 : 0;
                    if (nextIndex < e.PointerCount)
                    {
                        _pointerId = e.GetPointerId(nextIndex);
                    }
                    else
                    {
                        Cancel();
                    }
                }

                return true;

            case MotionEventActions.Up:
                if (!_tracking) return false;

                if (e.GetPointerId(0) == _pointerId)
                {
                    _tracking = false;
                    _pointerId = -1;
                    PressEnded?.Invoke();
                }

                return true;

            case MotionEventActions.Cancel:
                if (!_tracking) return false;
                Cancel();
                return true;

            default:
                return _tracking;
        }
    }

    /// <summary>取消当前手势并回到未跟踪状态。</summary>
    public void Cancel()
    {
        if (!_tracking) return;

        _tracking = false;
        _pointerId = -1;
        PressCancelled?.Invoke();
    }
}
