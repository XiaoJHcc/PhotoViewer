using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 星级控件（RATING-07）：手机形态与宽形态控制栏共用的常驻实例。
/// 槽位主轴放得下固有长度时展开为交互式玻璃胶囊内的六格星排（自绘、逐格命中），否则压成紧凑胶囊
/// 并把五颗星沿胶囊均匀排开（胶囊短时互相叠压）；紧凑态按下即由宿主朝主图内侧弹出星级弹窗，同一次手势的落点继续在弹窗星排上取值。
/// 形态、胶囊矩形、格子矩形与紧凑簇偏移全部取共享 <see cref="RatingControlGeometry"/>。
/// </summary>
internal sealed class iOSRatingControl : UIView
{
    private readonly UIVisualEffectView _capsule;
    private readonly iOSRatingRow _row;
    private readonly iOSRatingCluster _cluster = new();
    private readonly UILongPressGestureRecognizer _press;
    private readonly UIPointerInteraction _capsulePointer;
    private RatingControlForm _form = RatingControlForm.Compact;
    private bool _horizontal = true;
    private int _value;
    private bool _conflict;
    private bool _enabled = true;
    private iOSRatingPopup? _forwardingPopup;

    /// <summary>建立玻璃胶囊、展开态星排与紧凑星簇。</summary>
    internal iOSRatingControl() : base(CGRect.Empty)
    {
        BackgroundColor = UIColor.Clear;
        IsAccessibilityElement = true;
        AccessibilityLabel = "星级";
        AccessibilityTraits = UIAccessibilityTrait.Adjustable;

        _capsule = new UIVisualEffectView(new UIGlassEffect { Interactive = true })
        {
            CornerConfiguration = UICornerConfiguration.CreateCapsule(),
            BackgroundColor = UIColor.Clear,
        };
        AddSubview(_capsule);

        _row = new iOSRatingRow(DesignTokens.RatingControlSlot, DesignTokens.RatingControlGlyphFontSize,
            DesignTokens.RatingControlClearGlyphFontSize);
        // 展开态星排只是控件的自绘内容：控件自身是读屏元素，星排不再单独暴露。
        _row.IsAccessibilityElement = false;
        _row.AccessibilityElementsHidden = true;
        _row.Committed += value => CommitRequested?.Invoke(value);
        _row.ZeroTapped += () => PopupRequested?.Invoke();
        // 星排与星簇都放在交互式玻璃胶囊的内容区：两种形态按压时都随玻璃放大、回弹、高亮，并与相邻玻璃融合。
        _capsule.ContentView.AddSubview(_row);
        _capsule.ContentView.AddSubview(_cluster);

        _press = new UILongPressGestureRecognizer(OnPress)
        {
            MinimumPressDuration = 0,
            AllowableMovement = float.MaxValue,
            CancelsTouchesInView = false,
        };
        AddGestureRecognizer(_press);
        _capsulePointer = new UIPointerInteraction(new CapsulePointerDelegate(this));
        Render();
        ApplyForm();
    }

    /// <summary>提交请求（点星、拖动抬手、读屏增减）：值为本次写入值。</summary>
    internal Action<int>? CommitRequested { get; set; }

    /// <summary>打开弹窗请求；返回宿主是否真的打开了（无当前照片时不打开）。</summary>
    internal Func<bool>? PopupRequested { get; set; }

    /// <summary>宿主注入的星级弹窗（紧凑态滑动在同一手势内继续落到弹窗星排上）。</summary>
    internal iOSRatingPopup? Popup { get; set; }

    /// <summary>展开态星排（验收读取格子与手势状态）。</summary>
    internal iOSRatingRow Row => _row;

    /// <summary>紧凑星簇（玻璃胶囊内容区内）。</summary>
    internal iOSRatingCluster Cluster => _cluster;

    /// <summary>玻璃胶囊（当前锚点、紧凑态指针高亮）。</summary>
    internal UIVisualEffectView Capsule => _capsule;

    /// <summary>槽位主轴是否为水平方向（手机竖屏底行与宽形态行布局为水平）。</summary>
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

    /// <summary>最近一次布局判定的形态。</summary>
    internal RatingControlForm Form => _form;

    /// <summary>控件在槽位内的视觉矩形（展开 224、紧凑铺满槽位主轴）。</summary>
    internal LayoutRect CapsuleFrame => RatingControlGeometry.ControlFrame(
        new LayoutRect(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height), _horizontal);

    /// <summary>当前显示星级。</summary>
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

    /// <summary>组级来源冲突：0 号槽位改为警示三角、只开弹窗不写值。</summary>
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

    /// <summary>禁用态：几何与命中区不变，内容降到禁用透明度。</summary>
    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Render();
            ApplyForm();
        }
    }

    /// <summary>
    /// 宿主刷新真值：一次写入显示值、冲突与可用态，并清掉待确认/预览。
    /// 只改内容与状态，不请求重排、不重写胶囊与星排几何：值刷新可能发生在按压/拖动途中，
    /// 重写交互中的 frame 会打断系统交互式玻璃的按压缩放与松手回弹。
    /// </summary>
    /// <param name="value">当前星级（负值按无星级显示）。</param>
    /// <param name="conflict">当前照片组内是否有评分冲突。</param>
    /// <param name="enabled">是否有当前照片。</param>
    internal void SetState(int value, bool conflict, bool enabled)
    {
        _value = value;
        _conflict = conflict;
        _enabled = enabled;
        _row.ClearPending();
        Render();
        ApplyForm();
    }

    /// <summary>紧凑态起手：先请宿主打开弹窗，再把同一次手势转给弹窗内的单条目星排。</summary>
    /// <param name="pointInControl">控件坐标中的触点。</param>
    internal void TrackBegan(CGPoint pointInControl)
    {
        _forwardingPopup = null;
        if (!_enabled || _form != RatingControlForm.Compact) return;
        PopupRequested?.Invoke();
        if (Popup is not { IsOpen: true } popup) return;
        if (popup.BeginExternal(this, pointInControl)) _forwardingPopup = popup;
    }

    /// <summary>紧凑态移动：按手势在弹窗坐标中的位置取值（手指不必移进弹窗）。</summary>
    /// <param name="pointInControl">控件坐标中的触点。</param>
    internal void TrackMoved(CGPoint pointInControl)
        => _forwardingPopup?.MoveExternal(this, pointInControl);

    /// <summary>紧凑态抬手：写回由弹窗接管；越界不写时收起弹窗，点按保持弹窗。</summary>
    /// <param name="pointInControl">控件坐标中的触点。</param>
    internal void TrackEnded(CGPoint pointInControl)
    {
        var popup = _forwardingPopup;
        _forwardingPopup = null;
        popup?.EndExternal(this, pointInControl);
    }

    /// <summary>紧凑态取消（系统打断）：清掉预览并收起弹窗。</summary>
    internal void TrackCancelled()
    {
        _forwardingPopup?.CancelExternal();
        _forwardingPopup = null;
    }

    /// <summary>按槽位主轴长度判定形态并落位胶囊、星排与紧凑星簇；几何未变时不写交互中的视图。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var form = RatingControlGeometry.ResolveForm(_horizontal ? (double)Bounds.Width : (double)Bounds.Height);
        if (form != _form)
        {
            _form = form;
            ApplyForm();
        }
        var capsule = CapsuleFrame;
        var capsuleRect = new CGRect(capsule.X, capsule.Y, capsule.Width, capsule.Height);
        iOSViewGeometry.SetRectIfNeeded(_capsule, capsuleRect);
        iOSViewGeometry.SetRectIfNeeded(_row, new CGRect(0, 0, capsule.Width, capsule.Height));
        _row.Horizontal = _horizontal;
        LayoutCluster(capsule.Width, capsule.Height);
    }

    /// <summary>读屏增量：直接提交相邻星级。</summary>
    public override void AccessibilityIncrement()
        => CommitRequested?.Invoke(Math.Clamp(Math.Max(0, _value) + 1, 0, RatingControlGeometry.SlotCount - 1));

    /// <summary>读屏减量：直接提交相邻星级。</summary>
    public override void AccessibilityDecrement()
        => CommitRequested?.Invoke(Math.Clamp(Math.Max(0, _value) - 1, 0, RatingControlGeometry.SlotCount - 1));

    /// <summary>紧凑态（或冲突三角）激活即打开弹窗，展开态沿用默认语义。</summary>
    /// <returns>是否处理了激活。</returns>
    public override bool AccessibilityActivate()
    {
        if (_form != RatingControlForm.Compact && !_conflict) return base.AccessibilityActivate();
        PopupRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// 紧凑星簇铺满玻璃胶囊内容区，字形位置由星簇按共享偏移自行计算；尺寸未变时不写视图。
    /// 尺寸取共享几何算出的未变换胶囊矩形，不读可能带系统按压变换的 <c>Frame</c>。
    /// </summary>
    /// <param name="width">胶囊宽（点）。</param>
    /// <param name="height">胶囊高（点）。</param>
    private void LayoutCluster(double width, double height)
    {
        iOSViewGeometry.SetRectIfNeeded(_cluster, new CGRect(0, 0, width, height));
        _cluster.Update(_value, _conflict, _horizontal);
    }

    /// <summary>刷新星簇与展开态星排的显示值、冲突与可用态。</summary>
    private void Render()
    {
        _cluster.Update(_value, _conflict, _horizontal);
        _cluster.Alpha = _enabled ? 1 : (nfloat)DesignTokens.ColorToolbarDisabledContentAlpha;
        _row.Value = _value;
        _row.Conflict = _conflict;
        _row.Enabled = _enabled;
        _row.ForceCommit = _conflict;
        _row.ZeroOpensPopup = _conflict;
        AccessibilityValue = _conflict ? "星级冲突" : _value <= 0 ? "无星级" : $"{_value} 星";
    }

    /// <summary>按形态切换展开态星排与紧凑星簇、接管方式与读屏语义。</summary>
    private void ApplyForm()
    {
        bool compact = _form == RatingControlForm.Compact;
        _row.Hidden = compact;
        _cluster.Hidden = !compact;
        _row.HandlesOwnTouches = !compact;
        _press.Enabled = compact && _enabled;
        bool attached = _capsule.Interactions.Contains(_capsulePointer);
        if (compact && !attached) _capsule.AddInteraction(_capsulePointer);
        else if (!compact && attached) _capsule.RemoveInteraction(_capsulePointer);
        AccessibilityTraits = compact
            ? UIAccessibilityTrait.Adjustable | UIAccessibilityTrait.Button
            : UIAccessibilityTrait.Adjustable;
    }

    /// <summary>紧凑态零时长长按识别器：按下打开弹窗并开始转发，移动/抬手/取消映射到跟踪接缝。</summary>
    /// <param name="recognizer">控件自己的识别器。</param>
    private void OnPress(UILongPressGestureRecognizer recognizer)
    {
        if (_form != RatingControlForm.Compact || !_enabled) return;
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

    /// <summary>紧凑胶囊整块指针高亮（展开态由星排逐格高亮）。</summary>
    private sealed class CapsulePointerDelegate(iOSRatingControl owner) : UIPointerInteractionDelegate
    {
        /// <summary>整胶囊一个指针区域。</summary>
        /// <param name="interaction">指针交互。</param>
        /// <param name="request">指针请求。</param>
        /// <param name="suggestedRegion">系统建议区域。</param>
        /// <returns>胶囊区域。</returns>
        public override UIPointerRegion GetRegionForRequest(
            UIPointerInteraction interaction, UIPointerRegionRequest request, UIPointerRegion suggestedRegion)
            => UIPointerRegion.Create(owner._capsule.Bounds, NSNumber.FromInt32(0));

        /// <summary>高亮整个胶囊。</summary>
        /// <param name="interaction">指针交互。</param>
        /// <param name="region">指针区域。</param>
        /// <returns>系统指针样式。</returns>
        public override UIPointerStyle GetStyleForRegion(UIPointerInteraction interaction, UIPointerRegion region)
            => UIPointerStyle.Create(UIPointerEffect.Create(new UITargetedPreview(owner._capsule)), null!);
    }
}
