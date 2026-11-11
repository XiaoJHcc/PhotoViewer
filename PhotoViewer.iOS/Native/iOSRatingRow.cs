using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 星级触感：手指沿星排跨档时一次选择触感，抬手提交时一次轻冲击。
/// 计数器只服务验收（每个手势的触发次数可读），不参与业务判定。
/// </summary>
internal static class iOSRatingHaptics
{
    private static UISelectionFeedbackGenerator? _selection;

    /// <summary>预览跨档的选择触感次数。</summary>
    internal static int SelectionCount { get; private set; }

    /// <summary>提交冲击触感次数。</summary>
    internal static int CommitCount { get; private set; }

    /// <summary>提前唤醒选择触感引擎，避免起手第一档延迟。</summary>
    internal static void Prepare()
    {
        _selection ??= new UISelectionFeedbackGenerator();
        _selection.Prepare();
    }

    /// <summary>预览跨一档。</summary>
    internal static void Selection()
    {
        SelectionCount++;
        Prepare();
        _selection!.SelectionChanged();
    }

    /// <summary>一次提交：轻冲击挂在发起视图上（新 API 以视图为归属，不再持有全局生成器）。</summary>
    /// <param name="source">发起提交的星排视图。</param>
    internal static void Commit(UIView source)
    {
        CommitCount++;
        UIImpactFeedbackGenerator.GetFeedbackGenerator(UIImpactFeedbackStyle.Light, source).ImpactOccurred();
    }
}

/// <summary>
/// 六格星排（RATING-07）：0 号清零叉（冲突时为红色警示三角）、1–5 号星，横排 0→5 自左向右、
/// 竖排 5→0 自上而下。控件展开态与星级弹窗共用同一实例类型，几何全部取共享
/// <see cref="RatingControlGeometry"/>，手势状态机取共享 <see cref="RatingGesture"/>。
/// 触摸由零时长长按识别器交给 <c>Track*</c> 接缝，外层需要接管时关掉 <see cref="HandlesOwnTouches"/>。
/// </summary>
internal sealed class iOSRatingRow : UIView
{
    private readonly UIImageView[] _slots = new UIImageView[RatingControlGeometry.SlotCount];
    private readonly UILongPressGestureRecognizer _press;
    private readonly double _pitch;
    private readonly double _glyphSize;
    private readonly double _clearGlyphSize;
    private RatingGesture? _gesture;
    private UIImageView? _pressed;
    private int _value;
    private bool _conflict;
    private int? _preview;
    private bool _pending;
    private bool _enabled = true;
    private bool _horizontal = true;
    private bool _handlesOwnTouches = true;

    /// <summary>建立星排。</summary>
    /// <param name="pitch">每格步长。</param>
    /// <param name="glyphSize">星星字形字号。</param>
    /// <param name="clearGlyphSize">0 号清零叉字号。</param>
    internal iOSRatingRow(double pitch, double glyphSize, double clearGlyphSize) : base(CGRect.Empty)
    {
        _pitch = pitch;
        _glyphSize = glyphSize;
        _clearGlyphSize = clearGlyphSize;
        BackgroundColor = UIColor.Clear;
        for (int value = 0; value < _slots.Length; value++)
        {
            var slot = new UIImageView(CGRect.Empty)
            {
                ContentMode = UIViewContentMode.Center,
                UserInteractionEnabled = false,
                BackgroundColor = UIColor.Clear,
            };
            _slots[value] = slot;
            AddSubview(slot);
        }
        _press = new UILongPressGestureRecognizer(OnPress)
        {
            MinimumPressDuration = 0,
            AllowableMovement = float.MaxValue,
            CancelsTouchesInView = false,
        };
        AddGestureRecognizer(_press);
        AddInteraction(new UIPointerInteraction(new PointerDelegate(this)));
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Adjustable;
        AccessibilityLabel = "星级";
        Render();
    }

    /// <summary>六格视图，索引即星级值（0 为清零/冲突槽）。</summary>
    internal IReadOnlyList<UIImageView> SlotViews => _slots;

    /// <summary>每格沿主轴的步长。</summary>
    internal double Pitch => _pitch;

    /// <summary>当前是否一次手势进行中（弹窗据此避免中途重建星排）。</summary>
    internal bool IsTracking => _gesture is { IsActive: true };

    /// <summary>星排主轴是否为水平方向（横排 0 在最左、竖排 5 在最上）。</summary>
    internal bool Horizontal
    {
        get => _horizontal;
        set
        {
            if (_horizontal == value) return;
            _horizontal = value;
            SetNeedsLayout();
        }
    }

    /// <summary>显示值；小于 0 时一颗星都不点亮。</summary>
    internal int Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Render();
        }
    }

    /// <summary>组级来源冲突：0 号槽位换成警示三角，普通状态下是清零叉。</summary>
    internal bool Conflict
    {
        get => _conflict;
        set
        {
            if (_conflict == value) return;
            _conflict = value;
            Render();
        }
    }

    /// <summary>0 号槽位当前字形名称（验收核对 RATING-04：冲突为警示三角，否则清零叉）。</summary>
    internal string ClearSymbol => _conflict ? "exclamationmark.triangle.fill" : "xmark";

    /// <summary>预览值；非空时点亮 1..Preview 而不是 <see cref="Value"/>。</summary>
    internal int? Preview
    {
        get => _preview;
        set
        {
            if (_preview == value) return;
            _preview = value;
            Render();
        }
    }

    /// <summary>写回期间把点亮的星降到半透明，等待模型真值接管。</summary>
    internal bool Pending
    {
        get => _pending;
        set
        {
            if (_pending == value) return;
            _pending = value;
            Render();
        }
    }

    /// <summary>禁用态：几何与命中区不变，内容降到禁用透明度且忽略输入。</summary>
    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _press.Enabled = _enabled && _handlesOwnTouches;
            Render();
        }
    }

    /// <summary>同值也提交（冲突消解：落在任何星上都写）。</summary>
    internal bool ForceCommit { get; set; }

    /// <summary>0 号槽位改为打开弹窗而不是清零。</summary>
    internal bool ZeroOpensPopup { get; set; }

    /// <summary>验收缝：减少动态效果时不做按压缩放。</summary>
    internal Func<bool> ReduceMotionProvider { get; set; } = () => UIAccessibility.IsReduceMotionEnabled;

    /// <summary>是否由星排自己接收触摸；外层接管滑动时设 false。</summary>
    internal bool HandlesOwnTouches
    {
        get => _handlesOwnTouches;
        set
        {
            if (_handlesOwnTouches == value) return;
            _handlesOwnTouches = value;
            _press.Enabled = _enabled && _handlesOwnTouches;
        }
    }

    /// <summary>提交（抬手落在星上）：值为本次写入值。</summary>
    internal event Action<int>? Committed;

    /// <summary>落在 0 号冲突槽上：打开弹窗。</summary>
    internal event Action? ZeroTapped;

    /// <summary>清空在途的预览与待确认态（写回结束或宿主刷新真值）。</summary>
    internal void ClearPending()
    {
        _preview = null;
        _pending = false;
        Render();
    }

    /// <summary>起手：建立手势并记下起点；延迟预览时起手不点亮。</summary>
    /// <param name="pointInRow">星排坐标中的触点。</param>
    /// <param name="deferPreview">越过点按阈值前是否不预览（紧凑态/长按）。</param>
    internal void TrackBegan(CGPoint pointInRow, bool deferPreview = false)
    {
        if (!_enabled) return;
        _gesture = new RatingGesture(_value, deferPreview, ForceCommit, ZeroOpensPopup);
        var (main, cross) = MainCross(pointInRow);
        int value = ValueAt(main);
        _gesture.Begin(main, cross, value);
        Preview = _gesture.Preview;
        PressSlot(value);
    }

    /// <summary>移动：预览跟随手指，跨档时一次选择触感。</summary>
    /// <param name="pointInRow">星排坐标中的触点。</param>
    /// <param name="inBand">是否仍在取消带内；null 取星排自身的取消带。</param>
    internal void TrackMoved(CGPoint pointInRow, bool? inBand = null)
    {
        if (_gesture is not { IsActive: true } gesture) return;
        var (main, cross) = MainCross(pointInRow);
        bool changed = gesture.Move(main, cross, ValueAt(main), inBand ?? InBand(cross));
        int? preview = gesture.Preview;
        Preview = preview;
        if (preview is { } value) PressSlot(value);
        else ReleasePress();
        if (changed) iOSRatingHaptics.Selection();
    }

    /// <summary>抬手：按共享手势状态机给出结束语义并触发提交/开弹窗回调。</summary>
    /// <param name="pointInRow">星排坐标中的触点。</param>
    /// <param name="inBand">是否仍在取消带内；null 取星排自身的取消带。</param>
    /// <returns>结束语义与对应值。</returns>
    internal RatingGestureResult TrackEnded(CGPoint pointInRow, bool? inBand = null)
    {
        var gesture = _gesture;
        _gesture = null;
        ReleasePress();
        if (gesture is not { IsActive: true })
            return new RatingGestureResult(RatingGestureOutcome.None, _value);
        var (main, cross) = MainCross(pointInRow);
        var result = gesture.End(main, cross, ValueAt(main), inBand ?? InBand(cross));
        switch (result.Outcome)
        {
            case RatingGestureOutcome.Commit:
                Preview = result.Value;
                Pending = true;
                iOSRatingHaptics.Commit(this);
                Committed?.Invoke(result.Value);
                break;
            case RatingGestureOutcome.OpenPopup:
                Preview = null;
                ZeroTapped?.Invoke();
                break;
            default:
                Preview = null;
                break;
        }
        return result;
    }

    /// <summary>取消（系统打断、越出取消区）：清空预览，不写回。</summary>
    internal void TrackCancelled()
    {
        _gesture?.Cancel();
        _gesture = null;
        ReleasePress();
        Preview = null;
    }

    /// <summary>按共享几何把六格摆进自身 bounds，格子交叉轴占满、字形居中。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        double main = _horizontal ? (double)Bounds.Width : (double)Bounds.Height;
        double cross = _horizontal ? (double)Bounds.Height : (double)Bounds.Width;
        for (int value = 0; value < _slots.Length; value++)
        {
            var rect = RatingControlGeometry.SlotRect(value, main, cross, _horizontal, _pitch);
            _slots[value].Frame = new CGRect(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    /// <summary>读屏增减：直接提交相邻星级（与点选同一写回路径）。</summary>
    public override void AccessibilityIncrement()
        => Committed?.Invoke(Math.Clamp(Math.Max(0, DisplayValue) + 1, 0, RatingControlGeometry.SlotCount - 1));

    /// <summary>读屏减量：直接提交相邻星级。</summary>
    public override void AccessibilityDecrement()
        => Committed?.Invoke(Math.Clamp(Math.Max(0, DisplayValue) - 1, 0, RatingControlGeometry.SlotCount - 1));

    /// <summary>冲突槽上激活即打开弹窗，其余情况沿用默认语义。</summary>
    /// <returns>是否处理了激活。</returns>
    public override bool AccessibilityActivate()
    {
        if (!_conflict) return base.AccessibilityActivate();
        ZeroTapped?.Invoke();
        return true;
    }

    private int DisplayValue => _preview ?? _value;

    /// <summary>主轴/交叉轴分量：横排取 x/y，竖排取 y/x。</summary>
    /// <param name="point">星排坐标中的触点。</param>
    /// <returns>主轴与交叉轴坐标。</returns>
    private (double Main, double Cross) MainCross(CGPoint point)
        => _horizontal ? ((double)point.X, (double)point.Y) : ((double)point.Y, (double)point.X);

    private double MainLength => _horizontal ? (double)Bounds.Width : (double)Bounds.Height;

    private double CrossLength => _horizontal ? (double)Bounds.Height : (double)Bounds.Width;

    private int ValueAt(double main) => RatingControlGeometry.ValueAt(main, MainLength, _horizontal, _pitch);

    private bool InBand(double cross) => RatingControlGeometry.InBand(cross, 0, CrossLength);

    /// <summary>重画字形与透明度：值/冲突/预览/待确认/禁用任一变化都走这里。</summary>
    private void Render()
    {
        int display = DisplayValue;
        var clear = _slots[0];
        clear.Image = iOSSymbols.Symbol(_conflict ? "exclamationmark.triangle.fill" : "xmark",
            _conflict ? _glyphSize : _clearGlyphSize);
        clear.TintColor = _conflict ? iOSColors.FromToken(DesignTokens.ColorStarConflict) : UIColor.SecondaryLabel;
        double contentAlpha = _enabled ? 1 : DesignTokens.ColorToolbarDisabledContentAlpha;
        clear.Alpha = (nfloat)contentAlpha;
        for (int value = 1; value < _slots.Length; value++)
        {
            bool lit = value <= display;
            var slot = _slots[value];
            slot.Image = iOSSymbols.Symbol(lit ? "star.fill" : "star", _glyphSize);
            slot.TintColor = lit ? iOSColors.FromToken(DesignTokens.ColorStarOn) : UIColor.SecondaryLabel;
            slot.Alpha = (nfloat)((lit && _pending ? .5 : 1) * contentAlpha);
        }
        AccessibilityValue = _conflict ? "星级冲突" : display <= 0 ? "无星级" : $"{display} 星";
    }

    /// <summary>被按住的格放大到令牌比例，松手回弹；减少动态效果时不做缩放。</summary>
    /// <param name="value">要放大的格（0–5）。</param>
    private void PressSlot(int value)
    {
        var slot = _slots[Math.Clamp(value, 0, _slots.Length - 1)];
        if (ReferenceEquals(slot, _pressed)) return;
        ReleasePress();
        if (ReduceMotionProvider()) return;
        _pressed = slot;
        double scale = DesignTokens.MotionRatingPressScale;
        UIView.AnimateNotify(.25, 0, (nfloat).6, 0, UIViewAnimationOptions.BeginFromCurrentState,
            () => slot.Transform = CGAffineTransform.MakeScale((nfloat)scale, (nfloat)scale), null);
    }

    /// <summary>恢复被放大格的原始比例。</summary>
    private void ReleasePress()
    {
        if (_pressed is not { } slot) return;
        _pressed = null;
        if (ReduceMotionProvider())
        {
            slot.Transform = CGAffineTransform.MakeIdentity();
            return;
        }
        UIView.AnimateNotify(.25, 0, (nfloat).6, 0, UIViewAnimationOptions.BeginFromCurrentState,
            () => slot.Transform = CGAffineTransform.MakeIdentity(), null);
    }

    /// <summary>零时长长按识别器：按下即开始跟踪，移动/抬手/取消映射到跟踪接缝。</summary>
    /// <param name="recognizer">星排自己的识别器。</param>
    private void OnPress(UILongPressGestureRecognizer recognizer)
    {
        if (!_handlesOwnTouches) return;
        var point = recognizer.LocationInView(this);
        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began: TrackBegan(point); break;
            case UIGestureRecognizerState.Changed: TrackMoved(point); break;
            case UIGestureRecognizerState.Ended: TrackEnded(point); break;
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed: TrackCancelled(); break;
        }
    }

    /// <summary>指针逐格高亮：区域取指针下的格子，高亮取该格视图。</summary>
    private sealed class PointerDelegate(iOSRatingRow owner) : UIPointerInteractionDelegate
    {
        /// <summary>指针所在的格子区域（标识为该格星级值）。</summary>
        /// <param name="interaction">指针交互。</param>
        /// <param name="request">指针请求（位置在星排坐标系中）。</param>
        /// <param name="suggestedRegion">系统建议区域。</param>
        /// <returns>格子区域。</returns>
        public override UIPointerRegion GetRegionForRequest(
            UIPointerInteraction interaction, UIPointerRegionRequest request, UIPointerRegion suggestedRegion)
        {
            int value = owner.ValueAt(owner._horizontal ? (double)request.Location.X : (double)request.Location.Y);
            double main = owner.MainLength;
            double cross = owner.CrossLength;
            var rect = RatingControlGeometry.SlotRect(value, main, cross, owner._horizontal, owner._pitch);
            return UIPointerRegion.Create(new CGRect(rect.X, rect.Y, rect.Width, rect.Height), NSNumber.FromInt32(value));
        }

        /// <summary>高亮指针下的格子视图。</summary>
        /// <param name="interaction">指针交互。</param>
        /// <param name="region">指针区域。</param>
        /// <returns>系统指针样式。</returns>
        public override UIPointerStyle GetStyleForRegion(UIPointerInteraction interaction, UIPointerRegion region)
        {
            int value = region.Identifier is NSNumber number ? number.Int32Value : 0;
            var slot = owner._slots[Math.Clamp(value, 0, owner._slots.Length - 1)];
            return UIPointerStyle.Create(UIPointerEffect.Create(new UITargetedPreview(slot)), null!);
        }
    }
}
