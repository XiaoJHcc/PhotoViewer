using System;
using System.Collections.Generic;
using Android.Animation;
using Android.App;
using Android.Graphics;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using PhotoViewer.Android.Backdrop;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 主窗口内的弹层宿主（MENU-03@android / FILTER-08@android / RATING-04@android）：
/// 根视图最上层的全窗透明拦截层，内含一个按窗口像素矩形落位的面板。
/// </summary>
/// <remarks>
/// <para>
/// 拦截层铺满整个窗口并消费面板外的按下与抬起：点面板外只关闭弹层，该次触摸不穿透到照片、栏位或玻璃按钮。
/// 面板内容是既有真实行视图（菜单行、筛选行、弹窗星排），不复制业务、不改几何与滚动。
/// </para>
/// <para>
/// 打开时把根视图其余子视图对读屏隐藏（<c>NoHideDescendants</c>）并阻断其键盘焦点，
/// 焦点移入面板；关闭时按原样恢复并把焦点还给仍然有效的入口。背景隔离与面板形状都由本宿主承担。
/// </para>
/// <para>
/// 出入场只做淡入淡出，时长取系统 <c>config_shortAnimTime</c>；<c>animator_duration_scale</c> 为 0 时直接到位。
/// 面板表面默认画正式色层（API 24–30 外观）；接入屏幕层玻璃配方时把
/// <see cref="Backdrop"/> 指向屏幕层取样源并设置 <see cref="Surface"/>，不另开一条弹窗逻辑。
/// </para>
/// </remarks>
internal sealed class AndroidGlassPopupLayer : FrameLayout
{
    private readonly PanelView _panel;
    private readonly List<HiddenChild> _hidden = [];
    private readonly int[] _location = new int[2];
    private AndroidGlassPopupSession? _session;
    private AndroidGlassPopupSession? _closing;
    private ValueAnimator? _animator;
    private bool _disposed;

    /// <summary>建立铺满窗口的弹层宿主；未打开时不可见、不拦截触摸。</summary>
    /// <param name="activity">宿主 Activity。</param>
    public AndroidGlassPopupLayer(Activity activity) : base(activity)
    {
        SetWillNotDraw(true);
        Visibility = ViewStates.Gone;
        Clickable = true;
        Focusable = true;
        FocusableInTouchMode = true;
        DescendantFocusability = DescendantFocusability.AfterDescendants;
        _panel = new PanelView(activity);
        _panel.Visibility = ViewStates.Gone;
        _panel.Clickable = true;
        _panel.Focusable = true;
        _panel.FocusableInTouchMode = true;
        _panel.SetWillNotDraw(false);
        _panel.ClipToOutline = true;
        _panel.OutlineProvider = new RoundedOutline();
        AddView(_panel, new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));
    }

    private IBackdrop? _backdrop;
    private IGlassPopupSurface? _surface;

    /// <summary>
    /// 屏幕层取样源；为空表示只画正式色层，不取样（API 24–30 口径）。
    /// 弹层本身位于该取样层之外，满足「玻璃元素不得在自取样子树内」的取样规则。
    /// </summary>
    internal IBackdrop? Backdrop
    {
        get => _backdrop;
        set
        {
            _backdrop = value;
            _panel.Invalidate();
        }
    }

    /// <summary>面板表面材质；为空时宿主自画正式色层。</summary>
    internal IGlassPopupSurface? Surface
    {
        get => _surface;
        set
        {
            _surface = value;
            _panel.Invalidate();
        }
    }

    /// <summary>当前是否有逻辑上打开的弹层（淡出期间已算关闭）。</summary>
    internal bool IsOpen => _session is { IsOpen: true };

    /// <summary>当前打开的弹层实例（验收读取；关闭后为空）。</summary>
    internal AndroidGlassPopupSession? SessionForChecks => _session;

    /// <summary>本机是否开启减少动态效果（系统动画时长缩放为 0）。</summary>
    internal bool ReduceMotionForChecks => ReduceMotion(Context);

    /// <summary>系统 <c>config_shortAnimTime</c>（毫秒，验收读取）。</summary>
    internal int ShortAnimTimeForChecks => ShortAnimTime();

    /// <summary>把屏幕像素矩形换成弹层坐标（弹层铺满窗口且位于根视图原点）。</summary>
    /// <param name="screen">屏幕矩形。</param>
    /// <returns>窗口矩形。</returns>
    internal Rect ToWindow(Rect screen)
    {
        GetLocationOnScreen(_location);
        return new Rect(screen.Left - _location[0], screen.Top - _location[1],
            screen.Right - _location[0], screen.Bottom - _location[1]);
    }

    /// <summary>打开一个窗口内面板；已有弹层会立即收起后再显示新面板。</summary>
    /// <param name="request">内容、窗口矩形与色层参数。</param>
    /// <returns>打开的弹层实例；宿主尚未布局时返回空。</returns>
    internal AndroidGlassPopupSession? Show(AndroidGlassPopupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed || Width < 1 || Height < 1) return null;
        FinishClosing();
        if (_session != null) Close(_session, immediate: true);

        var session = new AndroidGlassPopupSession(request, _panel);
        _session = session;
        _panel.Bind(request);
        Visibility = ViewStates.Visible;
        HideBackground();
        LayoutPanel(request.Frame);
        Alpha = ReduceMotion(Context) ? 1f : 0f;
        if (Alpha < 1f) Animate(1f, null);
        ViewCompat.SetAccessibilityPaneTitle(_panel, request.PaneTitle);
        FocusFirst();
        return session;
    }

    /// <summary>更新已打开面板的位置或尺寸（行状态与实例保持不变）。</summary>
    /// <param name="session">仍打开的弹层实例。</param>
    /// <param name="frame">新的窗口像素矩形。</param>
    /// <returns>更新生效返回 true。</returns>
    internal bool Update(AndroidGlassPopupSession session, Rect frame)
    {
        if (!ReferenceEquals(session, _session) || !session.IsOpen) return false;
        session.Frame = new Rect(frame);
        LayoutPanel(frame);
        return true;
    }

    /// <summary>关闭指定弹层；逻辑关闭立即生效，视觉淡出随后结束。</summary>
    /// <param name="session">要关闭的实例；为空或已关闭返回 false。</param>
    /// <returns>确实关闭了返回 true。</returns>
    internal bool Dismiss(AndroidGlassPopupSession? session)
    {
        if (session == null || !ReferenceEquals(session, _session) || !session.IsOpen) return false;
        Close(session, immediate: false);
        return true;
    }

    /// <summary>关闭当前弹层（返回键与各入口统一调用）。</summary>
    /// <returns>确实关闭了返回 true。</returns>
    internal bool Dismiss()
    {
        var session = _session;
        return session != null && Dismiss(session);
    }

    /// <summary>立即结束淡出（根视图释放或打开新弹层前使用）。</summary>
    internal void FinishClosing()
    {
        CancelAnimation();
        if (_closing == null) return;
        _panel.RemoveAllViews();
        _panel.Visibility = ViewStates.Gone;
        _closing = null;
        Visibility = ViewStates.Gone;
        Alpha = 1f;
    }

    /// <summary>面板外点按在抬起时关闭；整个手势都由本层消费，不穿透。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>恒为 true（拦截层消费该次手势）。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return true;
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel) Dismiss();
        return true;
    }

    /// <summary>淡出期间由拦截层接管触摸，避免关闭途中误触菜单项或星排。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>淡出中返回 true；打开期间返回 false，让面板内容照常接收。</returns>
    public override bool OnInterceptTouchEvent(MotionEvent? e)
        => _session == null && _closing != null;

    /// <summary>面板内焦点不越出面板：背景已阻断，键盘导航留在弹层内。</summary>
    /// <param name="focused">当前焦点视图。</param>
    /// <param name="direction">方向。</param>
    /// <returns>面板内的下一个焦点；无则 null。</returns>
    public override View? FocusSearch(View? focused, FocusSearchDirection direction)
    {
        var next = base.FocusSearch(focused, direction);
        if (next == null) return null;
        return ReferenceEquals(next, _panel) || IsDescendant(_panel, next) ? next : null;
    }

    /// <summary>Esc 由弹层消费并关闭；其余按键照常交给面板内容。</summary>
    /// <param name="e">按键事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e == null || e.KeyCode != Keycode.Escape) return base.DispatchKeyEvent(e);
        if (e.Action == KeyEventActions.Up) Dismiss();
        return true;
    }

    /// <summary>弹层铺满窗口；面板按自身窗口矩形精确测量。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), MeasureSpec.GetSize(heightMeasureSpec));
        var session = _session ?? _closing;
        if (session != null) MeasurePanel(session.Frame);
    }

    /// <summary>把面板摆到窗口矩形上。</summary>
    /// <param name="changed">布局是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        var session = _session ?? _closing;
        if (session == null) return;
        var frame = session.Frame;
        _panel.Layout(frame.Left, frame.Top, frame.Left + Math.Max(0, frame.Width()),
            frame.Top + Math.Max(0, frame.Height()));
    }

    /// <summary>释放动画、弹层与读屏隐藏登记。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CancelAnimation();
            var session = _session;
            _session = null;
            if (session != null)
            {
                session.IsOpen = false;
                session.OnClosed?.Invoke();
            }
            RestoreBackground();
            FinishClosing();
        }
        base.Dispose(disposing);
    }

    private void Close(AndroidGlassPopupSession session, bool immediate)
    {
        _session = null;
        session.IsOpen = false;
        _closing = session;
        RestoreBackground();
        RestoreFocus(session.Entry);
        // 回调只清空调用方状态；放在恢复背景之后，重入打开新弹层也不会被旧面板的淡出牵连。
        session.OnClosed?.Invoke();
        if (!ReferenceEquals(_closing, session)) return;
        if (immediate || ReduceMotion(Context))
        {
            FinishClosing();
            return;
        }
        Animate(0f, FinishClosing);
    }

    private void Animate(float target, Action? completed)
    {
        CancelAnimation();
        var animator = ValueAnimator.OfFloat(Alpha, target)!;
        animator.SetDuration(Math.Max(1, ShortAnimTime()));
        animator.Update += (_, args) => Alpha = (float)(args.Animation?.AnimatedValue ?? target);
        animator.AnimationEnd += (_, _) =>
        {
            if (!ReferenceEquals(_animator, animator)) return;
            _animator = null;
            completed?.Invoke();
        };
        _animator = animator;
        animator.Start();
    }

    private void CancelAnimation()
    {
        var animator = _animator;
        _animator = null;
        animator?.Cancel();
        animator?.Dispose();
    }

    private void LayoutPanel(Rect frame)
    {
        MeasurePanel(frame);
        _panel.Layout(frame.Left, frame.Top, frame.Left + Math.Max(0, frame.Width()),
            frame.Top + Math.Max(0, frame.Height()));
        RequestLayout();
    }

    private void MeasurePanel(Rect frame)
    {
        _panel.Measure(MeasureSpec.MakeMeasureSpec(Math.Max(0, frame.Width()), MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(Math.Max(0, frame.Height()), MeasureSpecMode.Exactly));
    }

    private void FocusFirst()
    {
        var first = FirstFocusable(_panel);
        if (first != null) Focus(first);
        else Focus(_panel);
    }

    private static void Focus(View view)
    {
        if (!view.RequestFocus()) view.RequestFocusFromTouch();
    }

    private static View? FirstFocusable(View view)
    {
        if (view.Visibility != ViewStates.Visible || !view.Enabled) return null;
        if (view is ViewGroup group)
        {
            for (int index = 0; index < group.ChildCount; index++)
            {
                if (group.GetChildAt(index) is not { } child) continue;
                if (FirstFocusable(child) is { } found) return found;
            }
        }
        return view.Focusable && view.Width > 0 && view.Height > 0 ? view : null;
    }

    private static bool IsDescendant(ViewGroup ancestor, View view)
    {
        for (var parent = view.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private void HideBackground()
    {
        if (Parent is not ViewGroup root) return;
        for (int index = 0; index < root.ChildCount; index++)
        {
            if (root.GetChildAt(index) is not { } child || ReferenceEquals(child, this)) continue;
            var focus = child is ViewGroup group ? group.DescendantFocusability : (DescendantFocusability?)null;
            _hidden.Add(new HiddenChild(child, child.ImportantForAccessibility, focus,
                child.Focusable, child.FocusableInTouchMode));
            child.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
            if (child is ViewGroup blockable) blockable.DescendantFocusability = DescendantFocusability.BlockDescendants;
            child.Focusable = false;
            child.FocusableInTouchMode = false;
        }
    }

    private void RestoreBackground()
    {
        foreach (var hidden in _hidden)
        {
            hidden.Child.ImportantForAccessibility = hidden.Accessibility;
            if (hidden.Focus is { } focus && hidden.Child is ViewGroup group) group.DescendantFocusability = focus;
            hidden.Child.Focusable = hidden.Focusable;
            hidden.Child.FocusableInTouchMode = hidden.FocusableInTouchMode;
        }
        _hidden.Clear();
    }

    private static void RestoreFocus(View? entry)
    {
        if (entry == null || !entry.IsAttachedToWindow || !entry.IsShown) return;
        Focus(entry);
    }

    private int ShortAnimTime() => Resources?.GetInteger(global::Android.Resource.Integer.ConfigShortAnimTime) ?? 150;

    private static bool ReduceMotion(global::Android.Content.Context? context)
    {
        if (context?.ContentResolver == null) return false;
        try
        {
            return Settings.Global.GetFloat(context.ContentResolver, Settings.Global.AnimatorDurationScale, 1f) == 0f;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct HiddenChild(View Child, ImportantForAccessibility Accessibility,
        DescendantFocusability? Focus, bool Focusable, bool FocusableInTouchMode);

    /// <summary>面板容器：画正式色层（或接入的玻璃材质），并按圆角裁切内容。</summary>
    private sealed class PanelView : FrameLayout
    {
        private readonly int[] _location = new int[2];
        private Color _fill;
        private Color _border;
        private float _borderWidth;
        private float _density = 1f;

        public PanelView(global::Android.Content.Context context) : base(context)
        {
        }

        /// <summary>圆角半径（px）。</summary>
        internal float RadiusPx { get; set; }

        internal void Bind(AndroidGlassPopupRequest request)
        {
            RemoveAllViews();
            if (request.Content.Parent is ViewGroup old) old.RemoveView(request.Content);
            AddView(request.Content, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
            _fill = request.Fill;
            _border = request.Border;
            _borderWidth = (float)(request.BorderWidthDp * (Resources?.DisplayMetrics?.Density ?? 1));
            _density = Resources?.DisplayMetrics?.Density ?? 1f;
            RadiusPx = (float)(request.CornerRadiusDp * _density);
            Visibility = ViewStates.Visible;
            InvalidateOutline();
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (Width < 1 || Height < 1) return;
            var surface = Layer?.Surface;
            if (surface != null)
            {
                var context = new GlassPopupSurfaceContext(this, canvas, Layer?.Backdrop,
                    BackdropDrawContext.ForView(this, _location), Width, Height, RadiusPx, _density,
                    _fill, _border, _borderWidth);
                surface.Draw(context);
                return;
            }
            DrawColorLayer(canvas);
        }

        private AndroidGlassPopupLayer? Layer => Parent as AndroidGlassPopupLayer;

        private void DrawColorLayer(Canvas canvas)
        {
            using var paint = new Paint(PaintFlags.AntiAlias);
            var rect = new RectF(0, 0, Width, Height);
            paint.SetStyle(Paint.Style.Fill);
            paint.Color = _fill;
            canvas.DrawRoundRect(rect, RadiusPx, RadiusPx, paint);
            if (_borderWidth <= 0 || _border.A == 0) return;
            paint.SetStyle(Paint.Style.Stroke);
            paint.StrokeWidth = _borderWidth;
            paint.Color = _border;
            float inset = _borderWidth / 2;
            rect.Inset(inset, inset);
            canvas.DrawRoundRect(rect, RadiusPx, RadiusPx, paint);
        }
    }

    /// <summary>面板圆角轮廓：裁切内容与后续玻璃材质都取同一形状。</summary>
    private sealed class RoundedOutline : ViewOutlineProvider
    {
        public override void GetOutline(View? view, Outline? outline)
        {
            if (view == null || outline == null) return;
            float radius = view is PanelView panel ? panel.RadiusPx : 0;
            outline.SetRoundRect(0, 0, view.Width, view.Height, radius);
        }
    }
}

/// <summary>一次窗口内弹层的请求（窗口像素矩形 + 正式色层参数 + 入口）。</summary>
internal sealed class AndroidGlassPopupRequest
{
    /// <summary>建立请求。</summary>
    /// <param name="content">既有真实内容视图。</param>
    /// <param name="frame">窗口像素矩形。</param>
    /// <param name="cornerRadiusDp">圆角（dp）。</param>
    /// <param name="fill">正式色层填充。</param>
    /// <param name="border">正式色层描边。</param>
    /// <param name="borderWidthDp">描边线宽（dp）。</param>
    /// <param name="paneTitle">读屏面板标题。</param>
    /// <param name="entry">关闭后归还焦点的入口。</param>
    /// <param name="onClosed">逻辑关闭回调（清空调用方持有的弹层状态）。</param>
    public AndroidGlassPopupRequest(View content, Rect frame, double cornerRadiusDp,
        Color fill, Color border, double borderWidthDp, string paneTitle, View? entry, Action? onClosed)
    {
        Content = content;
        Frame = frame;
        CornerRadiusDp = cornerRadiusDp;
        Fill = fill;
        Border = border;
        BorderWidthDp = borderWidthDp;
        PaneTitle = paneTitle;
        Entry = entry;
        OnClosed = onClosed;
    }

    internal View Content { get; }
    internal Rect Frame { get; }
    internal double CornerRadiusDp { get; }
    internal Color Fill { get; }
    internal Color Border { get; }
    internal double BorderWidthDp { get; }
    internal string PaneTitle { get; }
    internal View? Entry { get; }
    internal Action? OnClosed { get; }
}

/// <summary>已打开弹层的实例：内容视图与当前窗口矩形在存活期间保持不变。</summary>
internal sealed class AndroidGlassPopupSession
{
    internal AndroidGlassPopupSession(AndroidGlassPopupRequest request, View panel)
    {
        Content = request.Content;
        Panel = panel;
        Entry = request.Entry;
        OnClosed = request.OnClosed;
        Frame = new Rect(request.Frame);
    }

    internal View Content { get; }
    internal View Panel { get; }
    internal View? Entry { get; }
    internal Action? OnClosed { get; }
    internal Rect Frame { get; set; }
    internal bool IsOpen { get; set; } = true;
}

/// <summary>
/// 弹层面板表面材质钩子：接入屏幕层玻璃配方时由宿主注入，默认路径画正式色层。
/// </summary>
internal interface IGlassPopupSurface
{
    /// <summary>
    /// 画面板表面；画布已按面板圆角裁切，坐标以面板左上角为原点。
    /// 取样源更新后由实现调用 <c>context.Panel.Invalidate()</c> 请求重绘。
    /// </summary>
    /// <param name="context">面板、取样源与几何。</param>
    void Draw(in GlassPopupSurfaceContext context);
}

/// <summary>一次面板表面绘制所需的取样源与几何（供玻璃材质接入）。</summary>
internal readonly struct GlassPopupSurfaceContext
{
    internal GlassPopupSurfaceContext(View panel, Canvas canvas, IBackdrop? backdrop,
        BackdropDrawContext draw, int widthPx, int heightPx, float cornerRadiusPx, float density,
        Color fill, Color border, float borderWidthPx)
    {
        Panel = panel;
        Canvas = canvas;
        Backdrop = backdrop;
        Draw = draw;
        WidthPx = widthPx;
        HeightPx = heightPx;
        CornerRadiusPx = cornerRadiusPx;
        Density = density;
        Fill = fill;
        Border = border;
        BorderWidthPx = borderWidthPx;
    }

    /// <summary>正在绘制的面板视图（取样上下文与失效重绘的宿主）。</summary>
    internal View Panel { get; }
    /// <summary>面板画布（原点在面板左上角）。</summary>
    internal Canvas Canvas { get; }
    /// <summary>屏幕层取样源；为空时只能画正式色层。</summary>
    internal IBackdrop? Backdrop { get; }
    /// <summary>取样上下文（画布原点是面板在窗口中的位置）。</summary>
    internal BackdropDrawContext Draw { get; }
    internal int WidthPx { get; }
    internal int HeightPx { get; }
    /// <summary>面板圆角半径（px）。</summary>
    internal float CornerRadiusPx { get; }
    internal float Density { get; }
    /// <summary>正式色层填充（材质接入前的外观）。</summary>
    internal Color Fill { get; }
    /// <summary>正式色层描边。</summary>
    internal Color Border { get; }
    internal float BorderWidthPx { get; }
}
