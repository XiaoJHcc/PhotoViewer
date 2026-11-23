using System;
using System.Collections.Generic;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 文件栏筛选入口（FILTER-07）：宽形态取共享 <see cref="MobileFilterLayout"/>——与大档卡片同宽的
/// 「筛选 / 更多」96 合体（行布局整组含 AI 开关自然宽居中、列布局按卡片内容末端对齐），
/// 手机形态取共享 <see cref="PhoneScreenLayout"/> 的竖屏 44 圆按钮或横屏 88 合体。
/// 全部入口都是 Material 按钮：圆按钮与合体共用同一套材质与状态层，合体是一个原生胶囊容器内的两段透明按钮；
/// 筛选段弹出与手机形态相同的三分组菜单，更多段交给宿主弹主图菜单。菜单承载在主窗口内的
/// <see cref="AndroidGlassPopupLayer"/> 上，沿用共享筛选业务。
/// </summary>
internal sealed class AndroidFilterBar : FrameLayout
{
    /// <summary>相似聚类 toggle 仅存在于 AI 版。</summary>
    internal static bool ShowsSimilarityToggle => AndroidAppCapabilities.HasDino;

    private readonly MainViewModel _main;
    private readonly AndroidGlassControlLayer? _glassLayer;
    private readonly AndroidGlassButton _filterButton;
    private readonly AndroidGlassCombo _combo;
    private readonly AndroidGlassButton? _toggle;
    private readonly Dictionary<View, LayoutRect> _viewRects = [];
    private MainScreenLayout.FilterFrames _frames;
    private bool _horizontal = true;
    private double _contentWidth = MainScreenLayout.FileBarContentWidth;
    private double _mainLength;
    private bool _phonePortrait;
    private bool _phoneGroup;
    private LayoutRect _control;
    private LayoutRect _filterSegment;
    private LayoutRect _moreSegment;
    private AndroidGlassPopupLayer? _popupLayer;
    private AndroidGlassPopupSession? _dropdown;
    private readonly List<FilterSelection> _dropdownRows = [];

    /// <summary>排序控件矩形（dp，验收读取）；宽形态与手机入口都是筛选段。</summary>
    internal LayoutRect SortFrame => _frames.Sort;
    /// <summary>更多段矩形（dp）。</summary>
    internal LayoutRect OrderFrame => _frames.Order;
    /// <summary>星级筛选控件矩形；合体入口不单独占用，返回空矩形。</summary>
    internal LayoutRect RatingFrame => _frames.Rating;
    /// <summary>共享筛选几何里的计数槽；Android 不提供计数槽，恒为空矩形（FILTER-04）。</summary>
    internal LayoutRect CountFrame => _frames.Count;
    /// <summary>相似聚类 toggle 矩形。</summary>
    internal LayoutRect SimilarityFrame => _frames.Toggle;
    /// <summary>筛选整块矩形（合体胶囊或手机圆按钮）。</summary>
    internal LayoutRect ControlFrame => _control;
    /// <summary>筛选段矩形（手机竖屏圆按钮、横屏与宽形态合体的筛选段）。</summary>
    internal LayoutRect PhoneFilterFrame => _filterSegment;
    /// <summary>更多段矩形（手机横屏合体与宽形态合体的更多段）。</summary>
    internal LayoutRect PhoneMoreFrame => _moreSegment;
    /// <summary>筛选入口矩形（与 <see cref="PhoneFilterFrame"/> 同值；验收点按入口）。</summary>
    internal LayoutRect EntryFrameForChecks => _filterSegment;
    /// <summary>当前是否宽形态行布局或手机入口（横排）。</summary>
    internal bool Horizontal => _horizontal;
    /// <summary>当前是否手机形态。</summary>
    internal bool IsPhone => _phonePortrait || _phoneGroup;
    /// <summary>宽形态入口是否为单一筛选入口（合体两段，验收选择分组菜单路径）。</summary>
    internal bool IsCapsuleForChecks => !IsPhone;
    /// <summary>筛选按钮真实视图（验收读取）。</summary>
    internal AndroidGlassButton FilterButtonForChecks => _filterButton;
    /// <summary>筛选/更多合体真实视图（验收读取）。</summary>
    internal AndroidGlassCombo ComboForChecks => _combo;
    /// <summary>相似聚类开关真实视图（标准版为 null）。</summary>
    internal AndroidGlassButton? ToggleForChecks => _toggle;
    /// <summary>宽形态入口自身的主轴长度（dp，含外边距）。</summary>
    internal double MainLengthForChecks => _mainLength;
    /// <summary>行布局下整组的自然宽度（dp）；超出时由滚动宿主承载。</summary>
    internal double RequiredWidthDp => !_horizontal || IsPhone ? _contentWidth : NaturalRowWidth();

    /// <summary>窗口内弹层宿主（由根视图注入；为空时不会打开下拉）。</summary>
    internal AndroidGlassPopupLayer? PopupLayer
    {
        get => _popupLayer;
        set => _popupLayer = value;
    }

    /// <summary>当前是否有下拉在显示（验收读取，浮层锚点协调也用它）。</summary>
    internal bool IsDropdownOpen => _dropdown is { IsOpen: true };
    /// <summary>当前下拉的面板实例（验收比对跨周期是不是同一个）。</summary>
    internal AndroidGlassPopupSession? DropdownInstanceForChecks => _dropdown;
    /// <summary>当前下拉的位置与尺寸（窗口 px，验收读取）。</summary>
    internal (int X, int Y, int Width, int Height) DropdownAnchorForChecks => _dropdown is { Frame: var frame }
        ? (frame.Left, frame.Top, frame.Width(), frame.Height()) : default;
    /// <summary>当前下拉的内容视图（验收用真实行视图点选用）。</summary>
    internal View? DropdownContentViewForChecks => _dropdown?.Content;
    /// <summary>当前下拉的行状态（验收读取；关闭时为空）。</summary>
    internal IReadOnlyList<FilterMenuRowForChecks> DropdownRowsForChecks
    {
        get
        {
            if (_dropdown is not { IsOpen: true }) return Array.Empty<FilterMenuRowForChecks>();
            var rows = new List<FilterMenuRowForChecks>(_dropdownRows.Count);
            foreach (var row in _dropdownRows)
                rows.Add(new FilterMenuRowForChecks(row.Label, row.Kind.ToString(), row.IsSelected(),
                    row.Row.Marked, $"fluent {row.IconId}"));
            return rows;
        }
    }

    /// <summary>冲突三角之外的浮层协调：下拉打开前通知宿主收起其它临时浮窗（RATING-04）。</summary>
    internal event Action? DropdownOpening;

    /// <summary>「更多」段点按：由宿主按真实入口视图弹出主图菜单。</summary>
    internal event Action<View>? MoreRequested;

    private void OpenGroupedMenu(View entry)
    {
        if (_popupLayer is not { } layer || !entry.IsShown) return;
        DropdownOpening?.Invoke();
        DismissDropdown();
        var filter = _main.FileVM.FilterBar;
        using var anchor = AndroidGlassMenu.WindowAnchor(layer, entry);
        var menu = new AndroidGlassMenu(layer, anchor);

        void AddRow(List<AndroidGlassMenu.Row> group, FilterMenu kind, string label, string iconId,
            Func<bool> selected, Action activate)
        {
            var row = new AndroidGlassMenu.Row(Context!, label, AndroidIcons.Glyph(iconId), selected(), () =>
            {
                activate();
                RefreshDropdownSelection();
            });
            group.Add(row);
            _dropdownRows.Add(new FilterSelection(kind, label, iconId, row, selected));
        }

        // FILTER-08：顶部三列排序，下面是星级多选与方向单选。
        List<AndroidGlassMenu.Row> sort = [], rating = [], order = [];
        foreach (var option in filter.SortModes)
        {
            var value = (SortMode)option.Value;
            AddRow(sort, FilterMenu.Sort, option.DisplayName, AndroidIcons.SortModeId(value),
                () => filter.SortMode == value, () => filter.SortMode = value);
        }
        foreach (var option in filter.RatingFilters)
        {
            string value = option.Key;
            AddRow(rating, FilterMenu.Rating, option.DisplayName, AndroidIcons.RatingFilterId(value),
                () => filter.IsRatingFilterSelected(value), () => filter.ToggleRatingFilter(value));
        }
        foreach (var option in filter.SortOrders)
        {
            var value = (SortOrder)option.Value;
            AddRow(order, FilterMenu.Order, option.DisplayName,
                value == SortOrder.Ascending ? IconCatalog.FilterAscending : IconCatalog.FilterDescending,
                () => filter.SortOrder == value, () => filter.SortOrder = value);
        }
        menu.AddGroup(sort, preferCompact: true);
        menu.AddGroup(rating);
        menu.AddGroup(order);
        _dropdown = menu.Show(layer, anchor, "筛选", entry, OnDropdownClosed, anchoredToButton: true);
        if (_dropdown == null) _dropdownRows.Clear();
    }

    /// <summary>构建筛选条并订阅共享筛选状态。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidFilterBar(Context context, MainViewModel main, AndroidGlassControlLayer? glassLayer = null)
        : base(context)
    {
        _main = main;
        _glassLayer = glassLayer;
        _filterButton = new AndroidGlassButton(context, AndroidIcons.Filter, "筛选");
        _filterButton.Click += (_, _) => OpenGroupedMenu(_filterButton);
        _filterButton.Visibility = ViewStates.Gone;
        AddView(_filterButton);
        _combo = new AndroidGlassCombo(context, AndroidIcons.Filter, "筛选", AndroidIcons.More, "更多");
        _combo.First.Click += (_, _) => OpenGroupedMenu(_combo.First);
        _combo.Second.Click += (_, _) => MoreRequested?.Invoke(_combo.Second);
        _combo.Visibility = ViewStates.Gone;
        AddView(_combo);
        _glassLayer?.Register(_filterButton);
        _glassLayer?.Register(_combo);
#if PHOTOVIEWER_DINO
        _toggle = new AndroidGlassButton(context, "相似聚类", "相似聚类", iconFont: false);
        ConfigureToggle(_toggle);
        _toggle.Click += (_, _) =>
        {
            bool open = !_main.FileVM.FilterBar.IsSimilarityPanelOpen;
            _main.FileVM.FilterBar.IsSimilarityPanelOpen = open;
            _toggle.Checked = open;
        };
        _toggle.Visibility = ViewStates.Gone;
        AddView(_toggle);
        _glassLayer?.Register(_toggle);
#else
        _toggle = null;
#endif
        SetClipChildren(true);
        Refresh();
    }

    /// <summary>
    /// AI 相似聚类开关：可勾选的 Material 按钮，未选中与其它按钮同底，选中由 <c>state_checked</c>
    /// 取 <c>color.filter.toggle.checked.*</c>（FILTER-06）。
    /// </summary>
    /// <param name="button">开关按钮。</param>
    private static void ConfigureToggle(AndroidGlassButton button)
    {
        var context = button.Context!;
        var checkedBackground = AndroidUiTheme.FromToken(DesignTokens.ColorFilterToggleCheckedBackground);
        var checkedBorder = AndroidUiTheme.FromToken(DesignTokens.ColorFilterToggleCheckedBorder);
        var checkedForeground = AndroidUiTheme.FromToken(DesignTokens.ColorFilterToggleCheckedForeground);
        var normalBackground = AndroidUiTheme.GlassButtonFill;
        var foreground = AndroidUiTheme.TextPrimary;
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        button.Checkable = true;
        button.SetPadding(AndroidUiTheme.DpPx(button, 12), 0, AndroidUiTheme.DpPx(button, 12), 0);
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetMinimumHeight(0);
        button.BackgroundTintList = new ColorStateList(
            [[global::Android.Resource.Attribute.StateChecked], []],
            [checkedBackground.ToArgb(), normalBackground.ToArgb()]);
        int alpha = (int)Math.Round(255 * DesignTokens.ColorToolbarDisabledContentAlpha);
        button.SetTextColor(new ColorStateList(
            [[global::Android.Resource.Attribute.StateChecked],
                [global::Android.Resource.Attribute.StateEnabled], []],
            [checkedForeground.ToArgb(), foreground.ToArgb(),
                Color.Argb(alpha, Color.GetRedComponent(foreground), Color.GetGreenComponent(foreground),
                    Color.GetBlueComponent(foreground)).ToArgb()]));
        button.StrokeWidth = Math.Max(1, AndroidUiTheme.DpPx(button, 1));
        button.StrokeColor = new ColorStateList(
            [[global::Android.Resource.Attribute.StateChecked], []],
            [checkedBorder.ToArgb(), Color.Transparent!.ToArgb()]);
        button.CornerRadius = AndroidUiTheme.DpPx(button, MobileFilterLayout.Segment / 2);
    }

    /// <summary>同步共享筛选状态到控件（程序化改动与文件源刷新都不留旧选中项）。</summary>
    internal void Refresh()
    {
        if (_toggle != null)
            _toggle.Checked = _main.FileVM.FilterBar.IsSimilarityPanelOpen;
        RefreshDropdownSelection();
        Invalidate();
    }

    /// <summary>外部状态变化就地更新已打开菜单的勾选与读屏勾选态，不重建面板、不重置滚动或焦点。</summary>
    private void RefreshDropdownSelection()
    {
        if (_dropdown is not { IsOpen: true }) return;
        foreach (var row in _dropdownRows) row.Row.SetChecked(row.IsSelected());
    }

    /// <summary>
    /// 宽形态：按共享 <see cref="MobileFilterLayout"/> 落位筛选/更多合体与相似聚类开关。
    /// 幂等：几何没有变化就什么都不做，也不收起已打开的下拉；锚点更新统一交给 <see cref="CommitLayout"/>。
    /// </summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">筛选条可用内容宽度（dp）。</param>
    /// <param name="leadingInset">列布局首控件的前导让位（dp，含该列顶部遮挡与一份外边距）。</param>
    /// <param name="contentTrailing">列布局的内容末端锚点（dp）；空表示取内容宽度减一份外边距。</param>
    internal void Arrange(bool horizontal, double contentWidth, double leadingInset, double? contentTrailing = null)
    {
        _phonePortrait = false;
        _phoneGroup = false;
        _horizontal = horizontal;
        _contentWidth = Math.Max(1, contentWidth);
        double layoutWidth = horizontal ? Math.Max(_contentWidth, NaturalRowWidth()) : _contentWidth;
        double toggleWidth = _toggle == null ? 0 : horizontal ? MeasureToggleWidth() : FilmCardGeometry.Width(FilmCardTier.Large);
        var frames = MobileFilterLayout.Compute(horizontal, layoutWidth, Math.Max(0, leadingInset), toggleWidth,
            horizontal ? null : contentTrailing);
        _mainLength = frames.MainLength;
        _control = frames.Control;
        _filterSegment = Offset(frames.Control, frames.Filter);
        _moreSegment = Offset(frames.Control, frames.More);
        _frames = new MainScreenLayout.FilterFrames(_filterSegment, _moreSegment, default, default, frames.Toggle);
        _combo.Visibility = ViewStates.Visible;
        _combo.SetVertical(false);
        _combo.SetMirrored(false);
        _viewRects[_combo] = _control;
        _viewRects.Remove(_filterButton);
        _filterButton.Visibility = ViewStates.Gone;
        if (_toggle != null)
        {
            bool showToggle = frames.Toggle.Width > 0 && frames.Toggle.Height > 0;
            _toggle.Visibility = showToggle ? ViewStates.Visible : ViewStates.Gone;
            if (showToggle) _viewRects[_toggle] = frames.Toggle;
            else _viewRects.Remove(_toggle);
        }
        Invalidate();
    }

    /// <summary>行布局 AI 相似开关的自然宽（验收读取）；列布局仍取卡片宽 96。</summary>
    internal double ToggleNaturalWidthForChecks => MeasureToggleWidth();

    /// <summary>宽形态筛选控件沿主轴的总长度（含外边框与 AI 开关），供分区直接使用。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容宽。</param>
    /// <param name="leadingInset">列布局前导让位。</param>
    /// <returns>筛选条主轴长度。</returns>
    internal double MainLengthForLayout(bool horizontal, double contentWidth, double leadingInset)
        => MobileFilterLayout.MainLength(horizontal, horizontal ? 0 : Math.Max(0, leadingInset), ShowsSimilarityToggle);

    /// <summary>宽形态筛选条文档宽（dp）：行布局取内容宽与自然宽的较大者，列布局取内容宽。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">文件栏内容宽。</param>
    /// <returns>文档宽度。</returns>
    internal double DocumentWidth(bool horizontal, double contentWidth)
        => horizontal ? Math.Max(Math.Max(1, contentWidth), NaturalRowWidth()) : Math.Max(1, contentWidth);

    /// <summary>行布局整组（合体 + AI 开关）的自然宽。</summary>
    private double NaturalRowWidth()
    {
        double toggle = _toggle == null ? 0 : MobileFilterLayout.Gap + MeasureToggleWidth();
        return MobileFilterLayout.GroupWidth + toggle + MobileFilterLayout.Margin * 2;
    }

    /// <summary>AI 开关的行布局自然宽：原生按钮量测，且不低于 Android 平台令牌宽。</summary>
    private double MeasureToggleWidth()
    {
        if (_toggle == null) return 0;
        double density = AndroidUiTheme.Density(Context!);
        int height = (int)Math.Round(MobileFilterLayout.Segment * density);
        _toggle.Measure(MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly));
        return Math.Max(DesignTokens.FilterToggleWidthAndroid, Math.Ceiling(_toggle.MeasuredWidth / density));
    }

    /// <summary>
    /// 手机形态：竖屏取共享槽位的圆按钮（更多由宿主单独落位）；横屏取 88「筛选 / 更多」合体，
    /// 两段由 <see cref="AndroidGlassCombo"/> 按镜像后的真实槽位排布。
    /// </summary>
    /// <param name="portrait">是否竖屏排布。</param>
    /// <param name="filter">筛选段矩形（本视图坐标）。</param>
    /// <param name="more">更多段矩形（本视图坐标；竖屏由宿主另建按钮，传空）。</param>
    internal void ArrangePhone(bool portrait, LayoutRect filter, LayoutRect more)
    {
        _phonePortrait = portrait;
        _phoneGroup = !portrait && more.Width > 0 && more.Height > 0;
        _horizontal = true;
        _filterSegment = filter;
        _moreSegment = more;
        _control = _phoneGroup
            ? new LayoutRect(Math.Min(filter.X, more.X), filter.Y,
                Math.Max(filter.MaxX, more.MaxX) - Math.Min(filter.X, more.X), Math.Max(filter.Height, more.Height))
            : filter;
        _frames = new MainScreenLayout.FilterFrames(filter, more, default, default, default);
        _contentWidth = Math.Max(1, _control.Width);
        _mainLength = _control.Height;
        if (_phonePortrait)
        {
            _viewRects[_filterButton] = _filterSegment;
            _filterButton.Visibility = ViewStates.Visible;
            _combo.Visibility = ViewStates.Gone;
            _viewRects.Remove(_combo);
        }
        else
        {
            _combo.Visibility = ViewStates.Visible;
            _combo.SetVertical(false);
            _combo.SetMirrored(filter.X > more.X);
            _viewRects[_combo] = _control;
            _filterButton.Visibility = ViewStates.Gone;
            _viewRects.Remove(_filterButton);
        }
        if (_toggle != null)
        {
            _toggle.Visibility = ViewStates.Gone;
            _viewRects.Remove(_toggle);
        }
        Invalidate();
    }

    /// <summary>锚点仍可见时保留打开时的菜单几何，否则收起。</summary>
    internal void CommitLayout()
    {
        if (_dropdown?.Entry is not { } entry) return;
        using var visible = new Rect();
        if (!entry.IsAttachedToWindow || !entry.IsShown || !entry.GetLocalVisibleRect(visible)
            || !visible.Contains(entry.Width / 2, entry.Height / 2)) DismissDropdown();
    }

    private void OnDropdownClosed()
    {
        _dropdown = null;
        _dropdownRows.Clear();
    }

    /// <summary>关闭当前筛选菜单；淡出中的面板不算打开。</summary>
    internal bool DismissDropdown() => _popupLayer?.Dismiss(_dropdown) == true;

    private sealed record FilterSelection(FilterMenu Kind, string Label, string IconId,
        AndroidGlassMenu.Row Row, Func<bool> IsSelected);

    /// <summary>筛选下拉行状态（验收用）。</summary>
    internal readonly record struct FilterMenuRowForChecks(
        string Label, string Group, bool Selected, bool Marked, string IconDescription);

    /// <summary>注销玻璃锚点并释放视图资源。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glassLayer?.Unregister(_filterButton);
            _glassLayer?.Unregister(_combo);
            if (_toggle != null) _glassLayer?.Unregister(_toggle);
        }
        base.Dispose(disposing);
    }

    /// <summary>子视图按共享矩形精确测量。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec), height = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(width, height);
        double density = AndroidUiTheme.Density(Context!);
        foreach (var (view, rect) in _viewRects)
            view.Measure(
                MeasureSpec.MakeMeasureSpec(Math.Max(0, (int)Math.Round(rect.Width * density)), MeasureSpecMode.Exactly),
                MeasureSpec.MakeMeasureSpec(Math.Max(0, (int)Math.Round(rect.Height * density)), MeasureSpecMode.Exactly));
    }

    /// <summary>把入口按钮摆到共享矩形上。</summary>
    /// <param name="changed">是否变化。</param>
    /// <param name="l">左。</param>
    /// <param name="t">上。</param>
    /// <param name="r">右。</param>
    /// <param name="b">下。</param>
    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        double density = AndroidUiTheme.Density(Context!);
        foreach (var (view, rect) in _viewRects)
        {
            int left = (int)Math.Round(rect.X * density), top = (int)Math.Round(rect.Y * density);
            int width = Math.Max(0, (int)Math.Round(rect.Width * density));
            int height = Math.Max(0, (int)Math.Round(rect.Height * density));
            view.Layout(left, top, left + width, top + height);
        }
    }

    private static LayoutRect Offset(LayoutRect origin, LayoutRect local)
        => new(origin.X + local.X, origin.Y + local.Y, local.Width, local.Height);

    private enum FilterMenu { Sort, Rating, Order }
}
