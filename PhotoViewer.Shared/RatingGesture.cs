namespace PhotoViewer.Shared;

/// <summary>一次星级手势的结束语义：不写、写回、打开弹窗、保持弹窗。</summary>
public enum RatingGestureOutcome
{
    /// <summary>不写回（点按原值、取消或越界离开）。</summary>
    None,

    /// <summary>把 <see cref="RatingGestureResult.Value"/> 写回。</summary>
    Commit,

    /// <summary>打开来源弹窗（落在 0 号冲突三角上）。</summary>
    OpenPopup,

    /// <summary>未越过点按阈值即抬手：弹窗保持打开，等待直接点选。</summary>
    KeepPopup,
}

/// <summary>手势结束结果：语义与对应星级值。</summary>
/// <param name="Outcome">结束语义。</param>
/// <param name="Value">要写回或展示的值；<see cref="RatingGestureOutcome.None"/> 与
/// <see cref="RatingGestureOutcome.KeepPopup"/> 时回落到原值。</param>
public readonly record struct RatingGestureResult(RatingGestureOutcome Outcome, int Value);

/// <summary>
/// 星级控件的一次手势状态机（RATING-07）：起手预览、沿星排拖动、抬手提交、取消回滚，
/// 保证一次手势最多写回一次。紧凑态与卡片长按用延迟预览（越过点按阈值后才预览）、
/// 冲突用强制提交（同值写入也消解冲突）、0 号格子用开弹窗开关。
/// </summary>
public sealed class RatingGesture
{
    private readonly double _tapSlop;
    private double _startMain;
    private double _startCross;

    /// <summary>创建一次手势。</summary>
    /// <param name="original">起手前的星级值。</param>
    /// <param name="deferPreview">是否延迟预览：越过点按阈值前不显示预览。</param>
    /// <param name="forceCommit">是否强制提交：预览等于原值时也写回（冲突消解）。</param>
    /// <param name="zeroOpensPopup">0 号格子是否改为打开来源弹窗而不是清零。</param>
    /// <param name="tapSlop">点按阈值；null 取 <see cref="RatingControlGeometry.TapSlop"/>。</param>
    public RatingGesture(int original, bool deferPreview = false, bool forceCommit = false,
        bool zeroOpensPopup = false, double? tapSlop = null)
    {
        Original = original;
        DeferPreview = deferPreview;
        ForceCommit = forceCommit;
        ZeroOpensPopup = zeroOpensPopup;
        _tapSlop = tapSlop ?? RatingControlGeometry.TapSlop;
    }

    /// <summary>起手前的星级值。</summary>
    public int Original { get; }

    /// <summary>是否延迟预览。</summary>
    public bool DeferPreview { get; }

    /// <summary>是否强制提交。</summary>
    public bool ForceCommit { get; }

    /// <summary>0 号格子是否打开来源弹窗。</summary>
    public bool ZeroOpensPopup { get; }

    /// <summary>手势是否进行中。</summary>
    public bool IsActive { get; private set; }

    /// <summary>手指是否已越过点按阈值。</summary>
    public bool Moved { get; private set; }

    /// <summary>当前预览值；null 表示越界离开或尚未开始预览。</summary>
    public int? Preview { get; private set; }

    /// <summary>控件当前应显示的值：预览优先，否则回落到原值。</summary>
    public int DisplayValue => Preview ?? Original;

    /// <summary>起手：记录起点并给出首个预览（延迟预览时为空）。</summary>
    /// <param name="main">落点的主轴坐标。</param>
    /// <param name="cross">落点的交叉轴坐标。</param>
    /// <param name="value">落点对应的星级值。</param>
    /// <returns>是否立即产生预览。</returns>
    public bool Begin(double main, double cross, int value)
    {
        IsActive = true;
        Moved = false;
        _startMain = main;
        _startCross = cross;
        Preview = DeferPreview ? null : value;
        return Preview is not null;
    }

    /// <summary>移动：更新是否越阈值与预览；延迟预览在越阈值前不产生预览。</summary>
    /// <param name="main">当前点的主轴坐标。</param>
    /// <param name="cross">当前点的交叉轴坐标。</param>
    /// <param name="value">当前点对应的星级值。</param>
    /// <param name="inBand">当前点是否仍在取消带内。</param>
    /// <returns>预览是否发生变化；手势未进行时恒为 false。</returns>
    public bool Move(double main, double cross, int value, bool inBand)
    {
        if (!IsActive) return false;
        Track(main, cross);
        if (DeferPreview && !Moved) return false;
        int? next = inBand ? value : null;
        bool changed = Preview != next;
        Preview = next;
        return changed;
    }

    /// <summary>
    /// 抬手：先按落点更新一次预览，再按「延迟预览未越阈值 → 保持弹窗」「越界离开 → 不写」
    /// 「0 号开弹窗」「同值且不强制 → 不写」「其余提交」的顺序给出结果。
    /// </summary>
    /// <param name="main">抬手点的主轴坐标。</param>
    /// <param name="cross">抬手点的交叉轴坐标。</param>
    /// <param name="value">抬手点对应的星级值。</param>
    /// <param name="inBand">抬手点是否仍在取消带内。</param>
    /// <returns>结束语义与对应值。</returns>
    public RatingGestureResult End(double main, double cross, int value, bool inBand)
    {
        if (!IsActive) return new RatingGestureResult(RatingGestureOutcome.None, Original);
        Track(main, cross);
        if (!(DeferPreview && !Moved)) Preview = inBand ? value : null;
        IsActive = false;

        if (DeferPreview && !Moved) return new RatingGestureResult(RatingGestureOutcome.KeepPopup, Original);
        if (Preview is null) return new RatingGestureResult(RatingGestureOutcome.None, Original);
        int committed = Preview.Value;
        if (ZeroOpensPopup && committed == 0) return new RatingGestureResult(RatingGestureOutcome.OpenPopup, 0);
        if (committed == Original && !ForceCommit) return new RatingGestureResult(RatingGestureOutcome.None, Original);
        return new RatingGestureResult(RatingGestureOutcome.Commit, committed);
    }

    /// <summary>取消（系统打断、越出取消区）：结束手势并清空预览，不写回。</summary>
    public void Cancel()
    {
        IsActive = false;
        Preview = null;
    }

    private void Track(double main, double cross)
    {
        if (Moved) return;
        double dx = main - _startMain;
        double dy = cross - _startCross;
        if (dx * dx + dy * dy >= _tapSlop * _tapSlop) Moved = true;
    }
}

/// <summary>
/// 紧凑态星级控件的一次三区手势（RATING-07）：起手打开星级弹窗后，同一次触摸沿
/// 「控件 → 弹窗 → 主图内侧」划分滑动区、拖放区与取消区；滑动区按 20 点相对步进增减，
/// 拖放区按弹窗星排的绝对位置选星，取消区清空预览且抬手不写。一次手势最多提交一次。
/// 纯 C# 状态机：触点与三个矩形须在同一坐标空间（Apple 为点，Android 为 dp）。
/// </summary>
public sealed class CompactRatingDragGesture
{
    private readonly bool _horizontalRow;
    private readonly LayoutRect _anchor;
    private readonly LayoutRect _panel;
    private readonly LayoutRect _row;
    private readonly RatingPopupDirection _direction;
    private readonly double _tapSlop;
    private readonly double _slideStep;
    private double _startX;
    private double _startY;
    private double _slideAnchor;
    private int _slideValue;
    private int? _preview;
    private RatingDragZone? _zone;
    private bool _moved;
    private bool _enteredDropOrCancel;
    private bool _active;

    /// <summary>创建一次紧凑态三区手势。</summary>
    /// <param name="original">起手前的星级值（0–5）。</param>
    /// <param name="horizontalRow">弹窗星排主轴是否为水平方向。</param>
    /// <param name="anchor">星级控件的入口矩形。</param>
    /// <param name="panel">星级弹窗面板矩形。</param>
    /// <param name="row">弹窗星排矩形（宿主坐标；主轴长度除以六即一格步长）。</param>
    /// <param name="direction">弹窗展开方向。</param>
    /// <param name="forceCommit">是否强制提交（同值也写回）。</param>
    /// <param name="tapSlop">点按阈值；null 取 <see cref="RatingControlGeometry.TapSlop"/>。</param>
    /// <param name="slideStep">每星相对拖动距离；null 取 <see cref="RatingControlGeometry.SlideStep"/>。</param>
    public CompactRatingDragGesture(int original, bool horizontalRow, LayoutRect anchor, LayoutRect panel,
        LayoutRect row, RatingPopupDirection direction, bool forceCommit = false, double? tapSlop = null,
        double? slideStep = null)
    {
        Original = Math.Clamp(original, 0, RatingControlGeometry.SlotCount - 1);
        _horizontalRow = horizontalRow;
        _anchor = anchor;
        _panel = panel;
        _row = row;
        _direction = direction;
        ForceCommit = forceCommit;
        _tapSlop = tapSlop ?? RatingControlGeometry.TapSlop;
        _slideStep = Math.Max(0, slideStep ?? RatingControlGeometry.SlideStep);
    }

    /// <summary>起手前的星级值。</summary>
    public int Original { get; }

    /// <summary>是否强制提交。</summary>
    public bool ForceCommit { get; }

    /// <summary>手势是否进行中。</summary>
    public bool IsActive => _active;

    /// <summary>手指是否已越点按阈值或进入过拖放区/取消区（进入后不再回到点按语义）。</summary>
    public bool Moved => _moved;

    /// <summary>当前预览值；null 表示点按阈值内、取消区或尚未产生预览。</summary>
    public int? Preview => _preview;

    /// <summary>控件当前应显示的值：预览优先，否则回落到原值。</summary>
    public int DisplayValue => _preview ?? Original;

    /// <summary>当前所在区域；尚未起手时为 null。</summary>
    public RatingDragZone? Zone => _zone;

    /// <summary>拖放区命中的星格（0–5）；仅拖放区有值，用于浮窗内的圆形 hover 底衬。</summary>
    public int? DropHoverSlot => _zone == RatingDragZone.Drop ? _preview : null;

    /// <summary>需要按 <c>motion.rating.press.scale</c> 放大的星格；取消区与无预览时为 null。</summary>
    public int? ScaledSlot => _zone == RatingDragZone.Cancel ? null : _preview;

    /// <summary>滑动区当前累计值（验收可读；与预览一致）。</summary>
    public int SlideValue => _slideValue;

    /// <summary>起手：记录起点与所在区域；起手就在拖放区时立即产生预览。</summary>
    /// <param name="x">触点横坐标。</param>
    /// <param name="y">触点纵坐标。</param>
    /// <returns>是否立即产生预览。</returns>
    public bool Begin(double x, double y)
    {
        _active = true;
        _moved = false;
        _enteredDropOrCancel = false;
        _preview = null;
        _slideValue = Original;
        _startX = x;
        _startY = y;
        _slideAnchor = RatingControlGeometry.IncreaseAxis(x, y, _horizontalRow);
        _zone = RatingControlGeometry.ResolveCompactDragZone(x, y, _anchor, _panel, _direction);
        if (_zone == RatingDragZone.Drop)
        {
            _enteredDropOrCancel = true;
            _preview = DropValue(x, y);
        }
        else if (_zone == RatingDragZone.Cancel)
        {
            _enteredDropOrCancel = true;
        }
        _moved = _enteredDropOrCancel;
        return _preview is not null;
    }

    /// <summary>
    /// 移动：更新区域与预览。滑动区越过点按阈值后先保持原值，按 20 点相对步进增减；
    /// 0 / 5 边界继续外拉时锚点跟手，反向拉回一个步距即立刻降档，无回拉死区。
    /// </summary>
    /// <param name="x">触点横坐标。</param>
    /// <param name="y">触点纵坐标。</param>
    /// <returns>预览是否发生变化；手势未进行时恒为 false。</returns>
    public bool Move(double x, double y)
    {
        if (!_active) return false;
        int? before = _preview;
        Track(x, y);
        var zone = RatingControlGeometry.ResolveCompactDragZone(x, y, _anchor, _panel, _direction);
        ApplyZone(zone, x, y, before);
        return _preview != before;
    }

    /// <summary>
    /// 抬手：按最终区域与预览给出结束语义。未越点按阈值且未进入中区/上区 → 保持弹窗；
    /// 取消区或同值 → 不写；否则提交一次。结束后的移动与再次抬手不再产生提交。
    /// </summary>
    /// <param name="x">抬手点横坐标。</param>
    /// <param name="y">抬手点纵坐标。</param>
    /// <returns>结束语义与对应值。</returns>
    public RatingGestureResult End(double x, double y)
    {
        if (!_active) return new RatingGestureResult(RatingGestureOutcome.None, Original);
        int? before = _preview;
        Track(x, y);
        var zone = RatingControlGeometry.ResolveCompactDragZone(x, y, _anchor, _panel, _direction);
        ApplyZone(zone, x, y, before);
        _active = false;

        if (zone == RatingDragZone.Cancel)
        {
            _preview = null;
            return new RatingGestureResult(RatingGestureOutcome.None, Original);
        }
        if (!_moved)
        {
            _preview = null;
            return new RatingGestureResult(RatingGestureOutcome.KeepPopup, Original);
        }
        if (_preview is not { } value) return new RatingGestureResult(RatingGestureOutcome.None, Original);
        if (value == Original && !ForceCommit)
        {
            _preview = null;
            return new RatingGestureResult(RatingGestureOutcome.None, Original);
        }
        return new RatingGestureResult(RatingGestureOutcome.Commit, value);
    }

    /// <summary>取消（系统打断）：结束手势并清空预览，不写回。</summary>
    public void Cancel()
    {
        _active = false;
        _preview = null;
    }

    /// <summary>点按阈值：起点到当前点的欧氏距离，或进入过中区/上区后不再回到点按语义。</summary>
    /// <param name="x">触点横坐标。</param>
    /// <param name="y">触点纵坐标。</param>
    private void Track(double x, double y)
    {
        if (_moved) return;
        if (_enteredDropOrCancel)
        {
            _moved = true;
            return;
        }
        double dx = x - _startX;
        double dy = y - _startY;
        if (dx * dx + dy * dy >= _tapSlop * _tapSlop) _moved = true;
    }

    /// <summary>按区域更新预览与跨区锚点。</summary>
    /// <param name="zone">当前区域。</param>
    /// <param name="x">触点横坐标。</param>
    /// <param name="y">触点纵坐标。</param>
    /// <param name="previousPreview">本次移动前的预览。</param>
    private void ApplyZone(RatingDragZone zone, double x, double y, int? previousPreview)
    {
        var previous = _zone;
        if (zone == RatingDragZone.Drop)
        {
            _enteredDropOrCancel = true;
            _moved = true;
            _preview = DropValue(x, y);
            _zone = zone;
            return;
        }
        if (zone == RatingDragZone.Cancel)
        {
            _enteredDropOrCancel = true;
            _moved = true;
            _preview = null;
            _zone = zone;
            return;
        }

        if (previous != RatingDragZone.Slide)
        {
            // RATING-07：中区 → 下区以当前预览为基值、取消区 → 下区以原星级为基值，锚点都取当前触点。
            _slideValue = previous == RatingDragZone.Drop ? previousPreview ?? Original : Original;
            _slideAnchor = RatingControlGeometry.IncreaseAxis(x, y, _horizontalRow);
            _zone = RatingDragZone.Slide;
            _preview = _moved ? _slideValue : null;
            return;
        }

        _zone = RatingDragZone.Slide;
        if (!_moved) return;
        double s = RatingControlGeometry.IncreaseAxis(x, y, _horizontalRow);
        while (s - _slideAnchor >= _slideStep && _slideValue < RatingControlGeometry.SlotCount - 1)
        {
            _slideValue++;
            _slideAnchor += _slideStep;
        }
        while (_slideAnchor - s >= _slideStep && _slideValue > 0)
        {
            _slideValue--;
            _slideAnchor -= _slideStep;
        }
        if (_slideValue >= RatingControlGeometry.SlotCount - 1 && s > _slideAnchor) _slideAnchor = s;
        else if (_slideValue <= 0 && s < _slideAnchor) _slideAnchor = s;
        _preview = _slideValue;
    }

    /// <summary>把触点投影到弹窗星排主轴并映射为 0–5（越出两端夹取）。</summary>
    /// <param name="x">触点横坐标。</param>
    /// <param name="y">触点纵坐标。</param>
    /// <returns>星级值（0–5）。</returns>
    private int DropValue(double x, double y)
    {
        double length = _horizontalRow ? _row.Width : _row.Height;
        if (length <= 0) return Original;
        double main = _horizontalRow ? x - _row.X : y - _row.Y;
        double pitch = length / RatingControlGeometry.SlotCount;
        return RatingControlGeometry.ValueAt(main, length, _horizontalRow, pitch);
    }
}
