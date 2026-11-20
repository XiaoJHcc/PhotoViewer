using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using Google.Android.Material.Button;
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
/// 筛选段弹出与手机形态相同的三分组菜单，更多段交给宿主弹主图菜单。菜单沿用既有 <see cref="PopupWindow"/> 与共享筛选业务。
/// </summary>
internal sealed class AndroidFilterBar : FrameLayout
{
    /// <summary>相似聚类 toggle 仅存在于 AI 版。</summary>
    internal static bool ShowsSimilarityToggle => AndroidAppCapabilities.HasDino;

    private readonly MainViewModel _main;
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
    private PopupWindow? _dropdown;
    private FilterMenu? _dropdownKind;
    private int _dropdownItemCount;
    private int _dropdownX;
    private int _dropdownY;
    private int _dropdownWidth;
    private int _dropdownHeight;
    private readonly List<FilterMenuRow> _dropdownRows = [];
    private readonly string _checkGlyph = AndroidUiTheme.IconGlyph(DesignTokens.IconCheck);

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

    /// <summary>当前是否有下拉在显示（验收读取，浮层锚点协调也用它）。</summary>
    internal bool IsDropdownOpen => _dropdown?.IsShowing == true;
    /// <summary>当前下拉的 <see cref="PopupWindow"/> 实例（验收比对跨周期是不是同一个）。</summary>
    internal PopupWindow? DropdownInstanceForChecks => _dropdown;
    /// <summary>当前下拉类型（验收读取）。</summary>
    internal string? DropdownKindForChecks => _dropdownKind?.ToString();
    /// <summary>当前下拉的屏幕定位与尺寸（px，验收读取）。</summary>
    internal (int X, int Y, int Width, int Height) DropdownAnchorForChecks => (_dropdownX, _dropdownY, _dropdownWidth, _dropdownHeight);
    /// <summary>当前下拉的内容视图（验收用真实行视图点选用）。</summary>
    internal View? DropdownContentViewForChecks => _dropdown?.ContentView;
    /// <summary>当前下拉的行状态（验收读取；关闭时为空）。</summary>
    internal IReadOnlyList<FilterMenuRowForChecks> DropdownRowsForChecks
    {
        get
        {
            if (_dropdown?.IsShowing != true) return Array.Empty<FilterMenuRowForChecks>();
            var rows = new List<FilterMenuRowForChecks>(_dropdownRows.Count);
            foreach (var row in _dropdownRows)
                rows.Add(new FilterMenuRowForChecks(row.Label, row.Kind.ToString(), row.IsSelected(),
                    row.Marked, row.HasIcon, row.IconDescription));
            return rows;
        }
    }

    /// <summary>冲突三角之外的浮层协调：下拉打开前通知宿主收起其它临时浮窗（RATING-04）。</summary>
    internal event Action? DropdownOpening;

    /// <summary>「更多」段点按：交给宿主弹出与长按相同的主图菜单（菜单内容由共享目录给出）。</summary>
    internal event Action<LayoutRect>? MoreRequested;

    /// <summary>
    /// 筛选入口在屏幕矩形处弹出同一套三组菜单（排序/方向单选、星级多选；点选立即应用并保持展开）。
    /// </summary>
    /// <param name="anchor">入口的屏幕矩形（px）。</param>
    internal void OpenGroupedMenuAtScreen(Rect anchor)
    {
        if (Context is not Activity) return;
        DropdownOpening?.Invoke();
        DismissDropdown();
        var filter = _main.FileVM.FilterBar;
        var labels = new List<string>();
        var values = new List<object>();
        var kinds = new List<FilterMenu>();
        foreach (var option in filter.SortModes)
        {
            labels.Add(option.DisplayName);
            values.Add((SortMode)option.Value);
            kinds.Add(FilterMenu.Sort);
        }
        foreach (var option in filter.SortOrders)
        {
            labels.Add(option.DisplayName);
            values.Add((SortOrder)option.Value);
            kinds.Add(FilterMenu.Order);
        }
        foreach (var option in filter.RatingFilters)
        {
            labels.Add(option.DisplayName);
            values.Add(option.Key);
            kinds.Add(FilterMenu.Rating);
        }
        ShowGroupedMenu(labels, values, kinds, PopupGeometryForScreen(anchor, labels.Count));
    }

    /// <summary>构建筛选条并订阅共享筛选状态。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidFilterBar(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _filterButton = new AndroidGlassButton(context, DesignTokens.IconFilter, "筛选");
        _filterButton.Click += (_, _) => OpenGroupedMenu(ScreenRectOf(_filterSegment));
        _filterButton.Visibility = ViewStates.Gone;
        AddView(_filterButton);
        _combo = new AndroidGlassCombo(context, DesignTokens.IconFilter, "筛选", DesignTokens.IconMore, "更多");
        _combo.First.Click += (_, _) => OpenGroupedMenu(ScreenRectOf(_filterSegment));
        _combo.Second.Click += (_, _) => MoreRequested?.Invoke(_moreSegment);
        _combo.Visibility = ViewStates.Gone;
        AddView(_combo);
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

    /// <summary>外部状态变化就地更新已打开菜单的勾选与读屏勾选态，不重建浮窗、不重置滚动或焦点。</summary>
    private void RefreshDropdownSelection()
    {
        if (_dropdown?.IsShowing != true) return;
        foreach (var row in _dropdownRows) row.SyncSelection();
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

    /// <summary>
    /// 布局收尾：拿最终几何核对已打开下拉的锚点。锚点仍在可视区内且位置变了就跟着挪，
    /// 控件被隐藏/滚出可视区（真正失效）才收起。
    /// </summary>
    internal void CommitLayout()
    {
        if (_dropdown is not { } popup || _dropdownKind is not { } kind) return;
        if (!popup.IsShowing) return;
        if (!IsAttachedToWindow || Visibility != ViewStates.Visible || !IsShown)
        {
            DismissDropdown();
            return;
        }
        // 分组菜单的锚点与尺寸在打开时确定；重排不改变它，入口失效时才收起。
        if (kind == FilterMenu.Grouped) return;
        if (!IsFrameVisible(_filterSegment))
        {
            DismissDropdown();
            return;
        }
        var geometry = PopupGeometry(_filterSegment, _dropdownItemCount);
        if (geometry.X == _dropdownX && geometry.Y == _dropdownY
            && geometry.Width == _dropdownWidth && geometry.Height == _dropdownHeight) return;
        _dropdownX = geometry.X;
        _dropdownY = geometry.Y;
        _dropdownWidth = geometry.Width;
        _dropdownHeight = geometry.Height;
        if (popup.ContentView?.IsAttachedToWindow != true) return;
        popup.Update(geometry.X, geometry.Y, geometry.Width, geometry.Height);
    }

    /// <summary>控件当前是否还能作为锚点：中心点落在本视图（含父级裁剪）的可见区域内。</summary>
    /// <param name="frame">dp 矩形。</param>
    /// <returns>可见返回 true。</returns>
    private bool IsFrameVisible(LayoutRect frame)
    {
        var visible = new Rect();
        if (!GetLocalVisibleRect(visible)) return false;
        double density = AndroidUiTheme.Density(Context!);
        int x = (int)Math.Round((frame.X + frame.Width / 2) * density);
        int y = (int)Math.Round((frame.Y + frame.Height / 2) * density);
        return visible.Contains(x, y);
    }

    /// <summary>打开分组菜单（入口屏幕矩形由真实视图位置换算）。</summary>
    /// <param name="anchor">入口屏幕矩形（px）。</param>
    private void OpenGroupedMenu(Rect anchor) => OpenGroupedMenuAtScreen(anchor);

    /// <summary>本视图内 dp 矩形换算成屏幕 px。</summary>
    private Rect ScreenRectOf(LayoutRect frame)
    {
        double density = AndroidUiTheme.Density(Context!);
        var location = new int[2];
        GetLocationOnScreen(location);
        return new Rect(
            location[0] + (int)Math.Round(frame.X * density),
            location[1] + (int)Math.Round(frame.Y * density),
            location[0] + (int)Math.Round(frame.MaxX * density),
            location[1] + (int)Math.Round(frame.MaxY * density));
    }

    /// <summary>手机与宽形态共用的三组菜单：排序/方向单选、星级多选；点选立即应用并保持展开，就地更新勾选。</summary>
    private void ShowGroupedMenu(List<string> labels, List<object> values,
        List<FilterMenu> kinds, (int X, int Y, int Width, int Height) geometry)
    {
        if (Context is not Activity activity) return;
        double density = AndroidUiTheme.Density(activity);
        int itemHeight = (int)Math.Round(40 * density);
        int padding = (int)Math.Round(4 * density);
        int dividerHeight = Math.Max(1, (int)Math.Round(density));
        var content = new LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(AndroidUiTheme.ControlFill);
        background.SetCornerRadius((float)(8 * density));
        background.SetStroke(Math.Max(1, (int)Math.Round(density)), AndroidUiTheme.ControlBorder);
        content.Background = background;
        content.SetPadding(padding, padding, padding, padding);
        _dropdownRows.Clear();

        void AddDivider()
        {
            var divider = new View(activity);
            divider.SetBackgroundColor(AndroidUiTheme.Divider);
            content.AddView(divider, new global::Android.Widget.LinearLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.MatchParent, dividerHeight));
        }

        for (int index = 0; index < labels.Count; index++)
        {
            if (index > 0 && kinds[index] != kinds[index - 1]) AddDivider();
            var row = new FilterMenuRow(this, activity, kinds[index], values[index], labels[index], density,
                ApplyFilterMenuRow);
            _dropdownRows.Add(row);
            content.AddView(row.View, new global::Android.Widget.LinearLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.MatchParent, itemHeight));
        }

        var metrics = Resources?.DisplayMetrics;
        int screenHeight = metrics?.HeightPixels ?? Height;
        int maxHeight = Math.Max(itemHeight, screenHeight - (int)Math.Round(16 * density));
        int contentHeight = labels.Count * itemHeight + 2 * dividerHeight + padding * 2;
        View popupContent = content;
        if (contentHeight > maxHeight)
        {
            var scroll = new ScrollView(activity);
            scroll.AddView(content);
            popupContent = scroll;
        }
        var popup = new PopupWindow(popupContent, geometry.Width, geometry.Height, true)
        {
            OutsideTouchable = true,
            Focusable = true,
            Elevation = (float)(8 * density),
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        popup.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_dropdown, popup))
            {
                _dropdown = null;
                _dropdownKind = null;
                _dropdownRows.Clear();
            }
            Refresh();
        };
        _dropdown = popup;
        _dropdownKind = FilterMenu.Grouped;
        _dropdownItemCount = labels.Count;
        _dropdownX = geometry.X;
        _dropdownY = geometry.Y;
        _dropdownWidth = geometry.Width;
        _dropdownHeight = geometry.Height;
        popup.ShowAtLocation(this, GravityFlags.Top | GravityFlags.Start, geometry.X, geometry.Y);
    }

    /// <summary>手机入口的屏幕矩形算菜单位置；按钮下方不足时翻到上方。</summary>
    private (int X, int Y, int Width, int Height) PopupGeometryForScreen(Rect anchor, int itemCount)
    {
        double density = AndroidUiTheme.Density(Context!);
        var metrics = Resources?.DisplayMetrics;
        int screenWidth = metrics?.WidthPixels ?? Width;
        int screenHeight = metrics?.HeightPixels ?? Height;
        int menuWidth = Math.Max((int)Math.Round(180 * density), anchor.Width());
        int itemHeight = (int)Math.Round(40 * density);
        int padding = (int)Math.Round(4 * density);
        int groupBreaks = 2;
        int contentHeight = itemCount * itemHeight + groupBreaks * Math.Max(1, (int)Math.Round(density)) + padding * 2;
        int maxHeight = Math.Max(itemHeight, screenHeight - (int)Math.Round(16 * density));
        int menuHeight = Math.Min(contentHeight, maxHeight);
        int margin = (int)Math.Round(8 * density);
        int x = anchor.Left;
        int below = anchor.Bottom + (int)Math.Round(2 * density);
        int above = anchor.Top - menuHeight - (int)Math.Round(2 * density);
        int y = below + menuHeight > screenHeight - margin ? above : below;
        x = Math.Clamp(x, margin, Math.Max(margin, screenWidth - menuWidth - margin));
        y = Math.Clamp(y, margin, Math.Max(margin, screenHeight - menuHeight - margin));
        return (x, y, menuWidth, menuHeight);
    }

    /// <summary>按被点控件的真实屏幕位置算下拉窗口的位置与尺寸（夹在屏幕内；空间不足翻到上方）。</summary>
    /// <param name="frame">控件矩形（本视图 dp）。</param>
    /// <param name="itemCount">菜单项数。</param>
    /// <returns>屏幕 px 矩形。</returns>
    private (int X, int Y, int Width, int Height) PopupGeometry(LayoutRect frame, int itemCount)
    {
        double density = AndroidUiTheme.Density(Context!);
        var barLocation = new int[2];
        GetLocationOnScreen(barLocation);
        var metrics = Resources?.DisplayMetrics;
        int screenWidth = metrics?.WidthPixels ?? Width;
        int screenHeight = metrics?.HeightPixels ?? Height;
        int menuWidth = Math.Max((int)Math.Round(160 * density), (int)Math.Round(frame.Width * density));
        int itemHeight = (int)Math.Round(40 * density);
        int padding = (int)Math.Round(4 * density);
        int contentHeight = itemCount * itemHeight + padding * 2;
        int maxHeight = Math.Max(itemHeight, screenHeight - (int)Math.Round(16 * density));
        int menuHeight = Math.Min(contentHeight, maxHeight);

        int margin = (int)Math.Round(8 * density);
        int x = barLocation[0] + (int)Math.Round(frame.X * density);
        int below = barLocation[1] + (int)Math.Round(frame.MaxY * density) + (int)Math.Round(2 * density);
        int above = barLocation[1] + (int)Math.Round(frame.Y * density) - menuHeight - (int)Math.Round(2 * density);
        int y = below;
        if (y + menuHeight > screenHeight - margin) y = above;
        x = Math.Clamp(x, margin, Math.Max(margin, screenWidth - menuWidth - margin));
        y = Math.Clamp(y, margin, Math.Max(margin, screenHeight - menuHeight - margin));
        return (x, y, menuWidth, menuHeight);
    }

    /// <summary>关闭筛选下拉（返回键优先消费）。</summary>
    /// <returns>关闭了返回 true。</returns>
    internal bool DismissDropdown()
    {
        var popup = _dropdown;
        _dropdown = null;
        _dropdownKind = null;
        _dropdownRows.Clear();
        if (popup?.IsShowing != true) return false;
        popup.Dismiss();
        return true;
    }

    /// <summary>应用菜单行的选择；星级行按 FILTER-03 切换并联合集，排序/方向行单选。</summary>
    /// <param name="row">被点选的行。</param>
    private void ApplyFilterMenuRow(FilterMenuRow row)
    {
        var filter = _main.FileVM.FilterBar;
        switch (row.Kind)
        {
            case FilterMenu.Sort:
                filter.SortMode = (SortMode)row.Value;
                break;
            case FilterMenu.Order:
                filter.SortOrder = (SortOrder)row.Value;
                break;
            default:
                filter.ToggleRatingFilter((string)row.Value);
                break;
        }
        RefreshDropdownSelection();
    }

    /// <summary>读取菜单行对应的共享筛选状态（「全部」只在无条件时选中）。</summary>
    /// <param name="kind">行类型。</param>
    /// <param name="value">行取值。</param>
    /// <returns>已选中返回 true。</returns>
    private bool IsFilterMenuValueSelected(FilterMenu kind, object value)
    {
        var filter = _main.FileVM.FilterBar;
        return kind switch
        {
            FilterMenu.Sort => Equals(value, filter.SortMode),
            FilterMenu.Order => Equals(value, filter.SortOrder),
            _ => filter.IsRatingFilterSelected((string)value),
        };
    }

    /// <summary>菜单行语义图标：优先取 uicons 令牌，缺失的排序/方向图标用系统公开图标。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="kind">行类型。</param>
    /// <param name="value">行取值。</param>
    /// <returns>图标视图与验收可读的资源描述。</returns>
    private static (ImageView Icon, string Description) CreateMenuIcon(Activity activity, FilterMenu kind,
        object value)
    {
        if (kind == FilterMenu.Sort)
        {
            return (SortMode)value switch
            {
                SortMode.Name => GlyphIcon(activity, DesignTokens.IconMenuOpenFile, false, null,
                    AndroidUiTheme.TextSecondary),
                SortMode.Date => SystemIcon(activity, global::Android.Resource.Drawable.IcMenuToday, "ic_menu_today"),
                _ => SystemIcon(activity, global::Android.Resource.Drawable.IcMenuSortBySize, "ic_menu_sort_by_size"),
            };
        }
        if (kind == FilterMenu.Order)
            return (SortOrder)value == SortOrder.Ascending
                ? SystemIcon(activity, global::Android.Resource.Drawable.ArrowUpFloat, "arrow_up_float")
                : SystemIcon(activity, global::Android.Resource.Drawable.ArrowDownFloat, "arrow_down_float");

        string key = (string)value;
        return key switch
        {
            PhotoListProjection.AllRatings => GlyphIcon(activity, DesignTokens.IconMenuAnalysis, false, null,
                AndroidUiTheme.TextSecondary),
            "None" => GlyphIcon(activity, DesignTokens.IconStar, false, null, AndroidUiTheme.TextSecondary),
            "Eq1" or "Eq2" or "Eq3" or "Eq4" or "Eq5" => GlyphIcon(activity, DesignTokens.IconStarFilled, true,
                key[2..], AndroidUiTheme.TextSecondary),
            "Rejected" => GlyphIcon(activity, DesignTokens.IconClear, false, null, AndroidUiTheme.TextSecondary),
            "Conflict" => GlyphIcon(activity, DesignTokens.IconWarning, false, null, AndroidUiTheme.StarConflict),
            _ => GlyphIcon(activity, DesignTokens.IconFilter, false, null, AndroidUiTheme.TextSecondary),
        };
    }

    /// <summary>uicons 字形图标（星级行叠数字）。</summary>
    private static (ImageView Icon, string Description) GlyphIcon(Activity activity, string token, bool solid,
        string? digit, Color color)
    {
        int glyphSize = Math.Max(1, (int)Math.Round(AndroidUiTheme.Sp(activity, DesignTokens.MenuIconFontSize)));
        var drawable = new AndroidGlyphDrawable(activity, AndroidUiTheme.IconGlyph(token), solid, digit, glyphSize);
        drawable.SetTintList(ColorStateList.ValueOf(color));
        var icon = new ImageView(activity);
        icon.SetImageDrawable(drawable);
        icon.SetScaleType(ImageView.ScaleType.FitCenter);
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        return (icon, digit == null ? $"uicons {token}" : $"uicons {token}+{digit}");
    }

    /// <summary>系统公开图标（uicons 没有对应语义令牌的排序键与方向）。</summary>
    private static (ImageView Icon, string Description) SystemIcon(Activity activity, int resource, string name)
    {
        var icon = new ImageView(activity);
        icon.SetImageResource(resource);
        icon.ImageTintList = ColorStateList.ValueOf(AndroidUiTheme.TextSecondary);
        icon.SetScaleType(ImageView.ScaleType.FitCenter);
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        return (icon, $"system {name}");
    }

    /// <summary>dp → px（布局参数）。</summary>
    private static int Px(double density, double dp) => Math.Max(0, (int)Math.Round(dp * density));

    /// <summary>
    /// 分组菜单的一行：语义图标 + 文案 + 勾选字形。读屏把行作为可勾选控件，
    /// 勾选态直接读共享筛选 VM，外部状态变化时只需重绘勾选字形。
    /// </summary>
    private sealed class FilterMenuRow
    {
        private readonly AndroidFilterBar _owner;
        private readonly TextView _mark;

        internal FilterMenuRow(AndroidFilterBar owner, Activity activity, FilterMenu kind, object value,
            string label, double density, Action<FilterMenuRow> onApply)
        {
            _owner = owner;
            Kind = kind;
            Value = value;
            Label = label;
            var row = new LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Px(density, 12), 0, Px(density, 8), 0);
            row.Clickable = true;
            row.Focusable = true;
            var (icon, description) = CreateMenuIcon(activity, kind, value);
            IconDescription = description;
            HasIcon = icon.Drawable != null;
            row.AddView(icon, new global::Android.Widget.LinearLayout.LayoutParams(
                Math.Max(1, Px(density, DesignTokens.MenuIconSize)),
                global::Android.Views.ViewGroup.LayoutParams.MatchParent)
            {
                RightMargin = Px(density, DesignTokens.MenuIconGap),
            });
            var text = new TextView(activity) { Text = label, TextSize = (float)DesignTokens.DropdownFontSize };
            text.SetTextColor(AndroidUiTheme.TextPrimary);
            text.Gravity = GravityFlags.CenterVertical;
            row.AddView(text, new global::Android.Widget.LinearLayout.LayoutParams(
                0, global::Android.Views.ViewGroup.LayoutParams.MatchParent) { Weight = 1 });
            _mark = new TextView(activity)
            {
                TextSize = (float)DesignTokens.MenuIconFontSize,
                Gravity = GravityFlags.Center,
            };
            _mark.SetTypeface(AndroidUiTheme.IconTypeface(activity), TypefaceStyle.Normal);
            _mark.SetTextColor(AndroidUiTheme.Accent);
            _mark.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(_mark, new global::Android.Widget.LinearLayout.LayoutParams(
                Px(density, 20), global::Android.Views.ViewGroup.LayoutParams.MatchParent));
            ViewCompat.SetAccessibilityDelegate(row, new RowAccessibility(this));
            row.Click += (_, _) => onApply(this);
            View = row;
            SyncSelection();
        }

        internal View View { get; }
        internal FilterMenu Kind { get; }
        internal object Value { get; }
        internal string Label { get; }
        internal string IconDescription { get; }
        internal bool HasIcon { get; }
        internal bool Marked => !string.IsNullOrEmpty(_mark.Text);

        internal bool IsSelected() => _owner.IsFilterMenuValueSelected(Kind, Value);

        /// <summary>就地更新勾选字形；不重建行、不移动焦点或菜单滚动位置。</summary>
        internal void SyncSelection()
        {
            string text = IsSelected() ? _owner._checkGlyph : string.Empty;
            if (_mark.Text == text) return;
            _mark.Text = text;
        }

        /// <summary>读屏勾选态直接取共享筛选状态，外部变化无需重建节点。</summary>
        private sealed class RowAccessibility : AccessibilityDelegateCompat
        {
            private readonly FilterMenuRow _row;

            internal RowAccessibility(FilterMenuRow row) => _row = row;

            public override void OnInitializeAccessibilityNodeInfo(View? host, AccessibilityNodeInfoCompat? info)
            {
                base.OnInitializeAccessibilityNodeInfo(host, info);
                if (info == null) return;
                info.Checkable = true;
                info.SetChecked(_row.IsSelected());
            }
        }
    }

    /// <summary>筛选下拉行状态（验收用）：标签、分组、共享选中态、实际勾选字形与图标资源。</summary>
    internal readonly record struct FilterMenuRowForChecks(
        string Label, string Group, bool Selected, bool Marked, bool HasIcon, string IconDescription);

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

    /// <summary>筛选菜单类型（分组菜单内部行类型仍按三组区分）。</summary>
    private enum FilterMenu { Sort, Order, Rating, Grouped }
}
