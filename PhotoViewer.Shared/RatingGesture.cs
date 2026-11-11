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
