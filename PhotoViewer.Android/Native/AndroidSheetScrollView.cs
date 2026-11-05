using Android.Content;
using Android.Views;
using Android.Widget;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 设置与工具半屏共用的内容滚动宿主（SETTINGS-04）：内容区上下滑只滚动内容。
/// 内容区按下时禁止祖先拦截，并退出嵌套滚动协议，BottomSheetBehavior 不会把内容区的拖动或惯性
/// 当成收起；顶部把手与标题不在本视图内，拖动关闭仍由 sheet 容器承担。
/// </summary>
internal sealed class AndroidSheetScrollView : ScrollView
{
    /// <summary>建立内容滚动宿主；滚动交给内容，容器只负责布局。</summary>
    /// <param name="context">宿主上下文。</param>
    internal AndroidSheetScrollView(Context context) : base(context)
    {
        FillViewport = true;
        // 退出嵌套滚动：内容到达顶端后的继续下拉不会被 sheet 收下成收起动作。
        NestedScrollingEnabled = false;
        OverScrollMode = OverScrollMode.Never;
    }

    /// <summary>内容区手势不交给祖先拦截（BottomSheetBehavior 的拖动在内容区被隔断）。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e?.ActionMasked == MotionEventActions.Down)
            Parent?.RequestDisallowInterceptTouchEvent(true);
        return base.DispatchTouchEvent(e);
    }
}
