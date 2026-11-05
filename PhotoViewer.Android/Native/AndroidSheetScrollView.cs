using System;
using Android.Content;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using PhotoViewer.Core.Design;

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

/// <summary>
/// 设置与工具半屏共用的初始展示（SETTINGS-04）：在 <c>Show()</c> 之前把 BottomSheetBehavior 配成
/// 屏高一半的收起态，用户从把手/标题上拖扩大到内容高度（<see cref="DesignTokens.SettingsAndroidSheetHeightRatio"/>），
/// 下滑关闭。先配置再展示，避免先按满高展开再闪回收起。
/// </summary>
internal static class AndroidSheetPresentation
{
    /// <summary>浮层内容高度：屏高 × settings.android.sheet.heightRatio。</summary>
    /// <param name="screenHeight">屏幕高度（px）。</param>
    /// <returns>内容高度（px）。</returns>
    internal static int ContentHeight(int screenHeight)
        => (int)(screenHeight * DesignTokens.SettingsAndroidSheetHeightRatio);

    /// <summary>把已交 SetContentView 的浮层配成初始收起半屏；找不到浮层容器时返回 false。</summary>
    /// <param name="content">浮层内容视图。</param>
    /// <param name="screenHeight">屏幕高度（px）。</param>
    /// <returns>找到并配置 BottomSheetBehavior 时为 true。</returns>
    internal static bool ConfigureInitialCollapsed(View content, int screenHeight)
    {
        if (content.Parent is not View parent) return false;
        var behavior = BottomSheetBehavior.From(parent);
        if (behavior == null) return false;
        behavior.PeekHeight = (int)(screenHeight * DesignTokens.SettingsAndroidSheetPeekRatio);
        behavior.SkipCollapsed = false;
        behavior.FitToContents = true;
        behavior.State = BottomSheetBehavior.StateCollapsed;
        return true;
    }
}

/// <summary>
/// 半屏内容可见高度同步（SETTINGS-04）：外层面板仍按 <see cref="DesignTokens.SettingsAndroidSheetHeightRatio"/>
/// 量测与拖动（expandedOffset 不变），只把内层「把手 + 标题 + 滚动区」根布局的高度改成面板当前可见高度，
/// 让 ScrollView 的量测与滚动范围与屏内实际可见区域一致。
/// 收起、拖动/惯性、展开/重新收起、窗口尺寸及系统栏 insets 变化都在下一帧绘制前同步；高度不变时不触发布局。
/// </summary>
internal sealed class AndroidSheetViewport : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
{
    private readonly View _content;
    private readonly View _root;
    private bool _attached;

    private AndroidSheetViewport(View content, View root)
    {
        _content = content;
        _root = root;
    }

    /// <summary>为浮层内容与内层根布局建立同步关系（Attach 之后开始按帧同步）。</summary>
    /// <param name="content">交给 SetContentView 的固定高度浮层视图。</param>
    /// <param name="root">浮层内的纵向根布局（把手 + 标题 + 滚动区）。</param>
    internal static AndroidSheetViewport Create(View content, View root) => new(content, root);

    /// <summary>挂到当前窗口的绘制前回调；重复调用无副作用。</summary>
    internal void Attach()
    {
        if (_attached) return;
        var observer = _content.ViewTreeObserver;
        if (observer?.IsAlive != true) return;
        observer.AddOnPreDrawListener(this);
        _attached = true;
    }

    /// <summary>解除绘制前回调（视图分离或浮层关闭时调用）。</summary>
    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        var observer = _content.ViewTreeObserver;
        if (observer?.IsAlive == true) observer.RemoveOnPreDrawListener(this);
    }

    /// <summary>绘制前同步内层根布局高度；面板几何本身不参与，避免 expandedOffset 跳变或循环布局。</summary>
    /// <returns>始终放行本次绘制。</returns>
    public bool OnPreDraw()
    {
        if (!_attached) return true;
        int height = VisibleHeight();
        if (height > 0 && _root.LayoutParameters is { } parameters && parameters.Height != height)
        {
            parameters.Height = height;
            _root.LayoutParameters = parameters;
        }
        return true;
    }

    /// <summary>面板当前可见高度：内容顶端到浮层宿主可用底边，扣除容器底部内边距。</summary>
    /// <returns>可见高度（px）；尚未布局时返回 0。</returns>
    private int VisibleHeight()
    {
        if (_content.Height <= 0) return 0;
        int[] contentLocation = new int[2];
        _content.GetLocationInWindow(contentLocation);
        int bottom = _content.RootView?.Height ?? 0;
        if (_content.Parent is View container && container.Height > 0)
        {
            int[] containerLocation = new int[2];
            container.GetLocationInWindow(containerLocation);
            int containerBottom = containerLocation[1] + container.Height - container.PaddingBottom;
            bottom = bottom > 0 ? Math.Min(bottom, containerBottom) : containerBottom;
        }
        // CoordinatorLayout 已按系统栏内缩，真正可用底边以它为准。
        if (_content.Parent is View sheet && sheet.Parent is View host && host.Height > 0)
        {
            int[] hostLocation = new int[2];
            host.GetLocationInWindow(hostLocation);
            int hostBottom = hostLocation[1] + host.Height - host.PaddingBottom;
            bottom = bottom > 0 ? Math.Min(bottom, hostBottom) : hostBottom;
        }
        return Math.Clamp(bottom - contentLocation[1], 0, _content.Height);
    }
}
