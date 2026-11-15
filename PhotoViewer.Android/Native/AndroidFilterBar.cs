using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 文件栏筛选入口：宽形态取共享 <see cref="WideFilterLayout"/>（行布局三段合体或单胶囊、
/// 列布局 96 宽居中、AI 版开关同宽在下隔 8），手机形态取共享 <see cref="PhoneScreenLayout"/> 的
/// 竖屏 44 圆按钮或横屏 88「筛选 / 更多」合体。菜单沿用既有自绘 <see cref="PopupWindow"/> 与共享筛选业务。
/// </summary>
internal sealed class AndroidFilterBar : View
{
    /// <summary>相似聚类 toggle 仅存在于 AI 版。</summary>
    internal const bool ShowsSimilarityToggle = AndroidAppCapabilities.HasDino;

    /// <summary>手机竖屏单圆按钮的图标（uicons 滑块，与筛选的业务语义一致）。</summary>
    private const string FilterGlyphToken = DesignTokens.IconMenuControl;

    private readonly MainViewModel _main;
    private readonly Paint _textPaint;
    private readonly Paint _iconPaint;
    private readonly Paint _chevronPaint;
    private readonly Paint _togglePaint;
    private MainScreenLayout.FilterFrames _frames;
    private bool _horizontal = true;
    private double _contentWidth = MainScreenLayout.FileBarContentWidth;
    private double _leadingInset;
    private bool _menuOpen;
    private bool _capsule;
    private bool _phonePortrait;
    private bool _phoneGroup;
    private LayoutRect _control;
    private LayoutRect _columnToggle;
    private LayoutRect _phoneFilter;
    private LayoutRect _phoneMore;
    private PopupWindow? _dropdown;
    private FilterMenu? _dropdownKind;
    private int _dropdownItemCount;
    private int _dropdownX;
    private int _dropdownY;
    private int _dropdownWidth;
    private int _dropdownHeight;

    /// <summary>排序控件矩形（dp，验收读取）。</summary>
    internal LayoutRect SortFrame => _frames.Sort;
    /// <summary>升降序控件矩形。</summary>
    internal LayoutRect OrderFrame => _frames.Order;
    /// <summary>星级筛选控件矩形。</summary>
    internal LayoutRect RatingFrame => _frames.Rating;
    /// <summary>计数文本槽矩形；内部呈现未提供，返回空矩形。</summary>
    internal LayoutRect CountFrame => _frames.Count;
    /// <summary>相似聚类 toggle 矩形。</summary>
    internal LayoutRect SimilarityFrame => _frames.Toggle;
    /// <summary>筛选整块矩形。</summary>
    internal LayoutRect ControlFrame => _control;
    /// <summary>手机竖屏筛选按钮矩形。</summary>
    internal LayoutRect PhoneFilterFrame => _phoneFilter;
    /// <summary>手机横屏「更多」段矩形。</summary>
    internal LayoutRect PhoneMoreFrame => _phoneMore;
    /// <summary>计数文本自然宽度；内部呈现未提供，返回 0。</summary>
    internal double CountWidthDp => 0;
    /// <summary>当前是否行布局（宽形态）或手机入口。</summary>
    internal bool Horizontal => _horizontal;
    /// <summary>当前是否手机形态。</summary>
    internal bool IsPhone => _phonePortrait || _phoneGroup;
    /// <summary>宽形态是否为单胶囊（验收读取）。</summary>
    internal bool IsCapsuleForChecks => !IsPhone && _capsule;
    /// <summary>行布局下整组不压缩时的自然宽度（dp）；超出时由滚动宿主承载。</summary>
    internal double RequiredWidthDp
    {
        get
        {
            if (!_horizontal || IsPhone) return _contentWidth;
            double control = _capsule
                ? _frames.Sort.Width
                : Math.Max(0, _frames.Rating.MaxX - _frames.Sort.X);
            double toggle = ShowsSimilarityToggle ? WideFilterLayout.Gap + _frames.Toggle.Width : 0;
            return control + toggle + WideFilterLayout.Margin * 2;
        }
    }
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

    /// <summary>冲突三角之外的浮层协调：下拉打开前通知宿主收起其它临时浮窗（RATING-04）。</summary>
    internal event Action? DropdownOpening;

    /// <summary>手机横屏「更多」段点按：交给宿主弹出与长按相同的主图菜单。</summary>
    internal event Action<LayoutRect>? MoreRequested;

    /// <summary>
    /// 手机形态的筛选入口（Material 圆按钮/合体段）在屏幕矩形处弹出同一套三组单选菜单。
    /// 入口外观由宿主用 Material 3 按钮承载，本视图只负责既有 PopupWindow 与共享筛选业务。
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
        ShowGroupedMenu(filter, labels, values, kinds, PopupGeometryForScreen(anchor, labels.Count));
    }

    /// <summary>构建筛选条并订阅共享筛选状态。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidFilterBar(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _textPaint = AndroidUiTheme.TextPaint(context, 13, AndroidUiTheme.TextPrimary);
        _iconPaint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _iconPaint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.ToolbarCommandGlyphFontSize);
        _iconPaint.TextAlign = Paint.Align.Center;
        if (AndroidUiTheme.IconTypeface(context) is { } typeface) _iconPaint.SetTypeface(typeface);
        _chevronPaint = AndroidUiTheme.Paint(AndroidUiTheme.TextSecondary);
        _chevronPaint.TextSize = AndroidUiTheme.Sp(context, 12);
        _chevronPaint.TextAlign = Paint.Align.Center;
        if (AndroidUiTheme.IconTypeface(context) is { } chevronTypeface) _chevronPaint.SetTypeface(chevronTypeface);
        _togglePaint = AndroidUiTheme.TextPaint(context, 12, AndroidUiTheme.TextPrimary);
        SetWillNotDraw(false);
        Clickable = true;
        Refresh();
    }

    /// <summary>同步共享筛选状态到控件（程序化改动与文件源刷新都不留旧选中项）。</summary>
    internal void Refresh() => Invalidate();

    /// <summary>
    /// 宽形态：按共享 <see cref="WideFilterLayout"/> 落位筛选整块、三段与相似聚类开关。
    /// 幂等：几何没有变化就什么都不做，也不收起已打开的下拉；锚点更新统一交给 <see cref="CommitLayout"/>。
    /// </summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">筛选条可用内容宽度（dp）。</param>
    /// <param name="leadingInset">列布局首控件的前导让位（dp）。</param>
    internal void Arrange(bool horizontal, double contentWidth, double leadingInset)
    {
        _phonePortrait = false;
        _phoneGroup = false;
        _horizontal = horizontal;
        _contentWidth = Math.Max(1, contentWidth);
        _leadingInset = Math.Max(0, leadingInset);
        var frames = ComputeWideFrames(horizontal, _contentWidth, _leadingInset);
        _capsule = frames.Mode == FilterControlMode.Capsule;
        _control = frames.Control;
        _columnToggle = frames.Toggle;
        _frames = new MainScreenLayout.FilterFrames(
            _capsule ? frames.Control : Offset(frames.Control, frames.Sort),
            _capsule ? default : Offset(frames.Control, frames.Order),
            _capsule ? default : Offset(frames.Control, frames.Rating),
            default,
            frames.Toggle);
        _phoneFilter = default;
        _phoneMore = default;
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
        => ComputeWideFrames(horizontal, contentWidth, leadingInset).MainLength;

    /// <summary>按当前文本自然宽度与共享 WideFilterLayout 计算宽形态外框。</summary>
    private WideFilterFrames ComputeWideFrames(bool horizontal, double contentWidth, double leadingInset)
    {
        var (sortWidth, orderWidth, ratingWidth) = NaturalWidths();
        double cardWidth = FilmCardGeometry.Width(FilmCardTier.Large);
        // 行布局按原生文字自然宽（不低于既有令牌 90）；列布局仍与卡片同宽 96。
        double toggleWidth = horizontal ? MeasureToggleWidth() : cardWidth;
        return WideFilterLayout.Compute(horizontal, Math.Max(1, contentWidth), cardWidth,
            horizontal ? 0 : Math.Max(WideFilterLayout.Margin, leadingInset),
            sortWidth, orderWidth, ratingWidth, cardWidth, ShowsSimilarityToggle, toggleWidth);
    }

    /// <summary>AI 开关的行布局自然宽：原生文字量测 + 左右内边距，且不低于现有令牌宽。</summary>
    private double MeasureToggleWidth()
    {
        double density = AndroidUiTheme.Density(Context!);
        double text = _togglePaint.MeasureText("相似聚类") / density;
        return Math.Max(DesignTokens.FilterToggleWidth, Math.Ceiling(text + 16));
    }

    /// <summary>三段标签的自然宽度：按当前字体量测文本并加左右内边距。</summary>
    private (double Sort, double Order, double Rating) NaturalWidths()
    {
        double density = AndroidUiTheme.Density(Context!);
        var filter = _main.FileVM.FilterBar;
        double sort = 0, order = 0, rating = 0;
        foreach (var option in filter.SortModes) sort = Math.Max(sort, _textPaint.MeasureText(option.DisplayName));
        foreach (var option in filter.SortOrders) order = Math.Max(order, _textPaint.MeasureText(option.DisplayName));
        foreach (var option in filter.RatingFilters) rating = Math.Max(rating, _textPaint.MeasureText(option.DisplayName));
        double padding = 24;
        return (Math.Max(44, sort / density + padding), Math.Max(44, order / density + padding),
            Math.Max(44, rating / density + padding));
    }

    /// <summary>
    /// 手机形态：竖屏取共享槽位的圆按钮（更多由宿主单独落位）；横屏取 88「筛选 / 更多」合体，
    /// 两段由本视图统一绘制与命中。
    /// </summary>
    /// <param name="portrait">是否竖屏排布。</param>
    /// <param name="filter">筛选段矩形（本视图坐标）。</param>
    /// <param name="more">更多段矩形（本视图坐标；竖屏由宿主另建按钮，传空）。</param>
    internal void ArrangePhone(bool portrait, LayoutRect filter, LayoutRect more)
    {
        _phonePortrait = portrait;
        _phoneGroup = !portrait && more.Width > 0 && more.Height > 0;
        _horizontal = true;
        _capsule = false;
        _phoneFilter = filter;
        _phoneMore = more;
        _control = _phoneGroup
            ? new LayoutRect(Math.Min(filter.X, more.X), filter.Y,
                Math.Max(filter.MaxX, more.MaxX) - Math.Min(filter.X, more.X), Math.Max(filter.Height, more.Height))
            : filter;
        _frames = new MainScreenLayout.FilterFrames(filter, default, default, default, default);
        _contentWidth = Math.Max(1, _control.Width);
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
        // 分组菜单在打开时用入口屏幕矩形定过锚点与尺寸；重排不得改宽或把它挪到 _control。
        if (kind == FilterMenu.Grouped) return;
        if (!TryGetFrame(kind, out var frame) || !IsFrameVisible(frame))
        {
            DismissDropdown();
            return;
        }
        var geometry = PopupGeometry(frame, _dropdownItemCount);
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

    /// <summary>取某一类下拉对应的控件矩形。</summary>
    /// <param name="kind">菜单类型。</param>
    /// <param name="frame">dp 矩形。</param>
    /// <returns>取到返回 true。</returns>
    private bool TryGetFrame(FilterMenu kind, out LayoutRect frame)
    {
        frame = kind switch
        {
            FilterMenu.Sort => _frames.Sort,
            FilterMenu.Order => _frames.Order,
            _ => _capsule || IsPhone ? _frames.Sort : _frames.Rating,
        };
        return frame.Width > 0 && frame.Height > 0;
    }

    /// <summary>自绘筛选整块、相似聚类开关与手机入口。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float density = (float)AndroidUiTheme.Density(Context!);
        var filter = _main.FileVM.FilterBar;
        if (IsPhone)
        {
            DrawPhone(canvas, density);
        }
        else if (_capsule)
        {
            DrawCapsule(canvas, _frames.Sort, true, density);
        }
        else
        {
            var rect = ToRectF(new LayoutRect(_frames.Sort.X, _frames.Sort.Y,
                _frames.Rating.MaxX - _frames.Sort.X, _frames.Sort.Height), density);
            AndroidUiTheme.DrawRounded(canvas, rect, (float)(WideFilterLayout.Height / 2 * density),
                AndroidUiTheme.ControlFill, AndroidUiTheme.ControlBorder, Math.Max(1, density));
            DrawSegment(canvas, _frames.Sort, TitleForSort(filter.SortMode), density, showChevron: true);
            DrawSegment(canvas, _frames.Order, TitleForOrder(filter.SortOrder), density, showChevron: false);
            DrawSegment(canvas, _frames.Rating, TitleForRating(filter.SelectedRatingFilter), density, showChevron: false);
            float dividerX1 = (float)(_frames.Order.X * density);
            float dividerX2 = (float)(_frames.Rating.X * density);
            using var divider = AndroidUiTheme.Paint(AndroidUiTheme.ControlBorder, Math.Max(1, density));
            canvas.DrawLine(dividerX1, rect.Top + 8 * density, dividerX1, rect.Bottom - 8 * density, divider);
            canvas.DrawLine(dividerX2, rect.Top + 8 * density, dividerX2, rect.Bottom - 8 * density, divider);
        }
#if PHOTOVIEWER_DINO
        if (ShowsSimilarityToggle && _columnToggle.Width > 0)
        {
            bool open = filter.IsSimilarityPanelOpen;
            var rect = ToRectF(_columnToggle, density);
            AndroidUiTheme.DrawRounded(canvas, rect,
                (float)(DesignTokens.FilterToggleCornerRadius * density),
                open ? AndroidUiTheme.FilterToggleCheckedBackground : AndroidUiTheme.ControlFill,
                open ? AndroidUiTheme.FilterToggleCheckedBorder : AndroidUiTheme.FilterToggleBorder,
                Math.Max(1, density));
            _togglePaint.Color = open
                ? AndroidUiTheme.FilterToggleCheckedForeground
                : AndroidUiTheme.FilterToggleForeground;
            AndroidUiTheme.DrawCentered(canvas, "相似聚类", _togglePaint, rect.CenterX(),
                rect.CenterY() + _togglePaint.TextSize * .36f, rect.Width() - 6 * density);
        }
#endif
    }

    /// <summary>手机竖屏圆按钮 / 横屏两段合体。</summary>
    private void DrawPhone(Canvas canvas, float density)
    {
        var filter = _main.FileVM.FilterBar;
        if (_phonePortrait)
        {
            DrawRoundEntry(canvas, _phoneFilter, density, selected: false);
            DrawGlyph(canvas, FilterGlyphToken, _iconPaint, _phoneFilter, density);
            return;
        }
        if (!_phoneGroup) return;
        var group = ToRectF(_control, density);
        AndroidUiTheme.DrawRounded(canvas, group, (float)(WideFilterLayout.Height / 2 * density),
            AndroidUiTheme.ControlFill, AndroidUiTheme.ControlBorder, Math.Max(1, density));
        DrawGlyph(canvas, FilterGlyphToken, _iconPaint, _phoneFilter, density);
        float dividerX = (float)((_phoneMore.X) * density);
        using var divider = AndroidUiTheme.Paint(AndroidUiTheme.ControlBorder, Math.Max(1, density));
        canvas.DrawLine(dividerX, group.Top + 10 * density, dividerX, group.Bottom - 10 * density, divider);
        DrawMoreGlyph(canvas, _phoneMore, density);
    }

    /// <summary>单胶囊：筛选图标 + 当前星级筛选文本（空间不足时只留图标）。</summary>
    private void DrawCapsule(Canvas canvas, LayoutRect frame, bool withLabel, float density)
    {
        var filter = _main.FileVM.FilterBar;
        DrawRoundEntry(canvas, frame, density, selected: false);
        float iconX = (float)((frame.X + WideFilterLayout.Height / 2) * density);
        AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(FilterGlyphToken), _iconPaint,
            iconX, (float)((frame.Y + frame.Height / 2) * density));
        string label = TitleForRating(filter.SelectedRatingFilter);
        float labelLeft = (float)((frame.X + WideFilterLayout.Height - 6) * density);
        float labelRight = (float)((frame.MaxX - 14) * density);
        if (withLabel && labelRight > labelLeft)
        {
            var paint = AndroidUiTheme.TextPaint(Context!, 12, AndroidUiTheme.TextPrimary);
            canvas.DrawText(AndroidUiTheme.Ellipsize(label, paint, labelRight - labelLeft),
                (labelLeft + labelRight) / 2, (float)((frame.Y + frame.Height / 2 + 4) * density), paint);
        }
    }

    private void DrawRoundEntry(Canvas canvas, LayoutRect frame, float density, bool selected)
    {
        var rect = ToRectF(frame, density);
        AndroidUiTheme.DrawRounded(canvas, rect, rect.Height() / 2,
            selected ? AndroidUiTheme.FilterToggleCheckedBackground : AndroidUiTheme.ControlFill,
            selected ? AndroidUiTheme.FilterToggleCheckedBorder : AndroidUiTheme.ControlBorder,
            Math.Max(1, density));
    }

    private void DrawSegment(Canvas canvas, LayoutRect frame, string? title, float density, bool showChevron)
    {
        var rect = ToRectF(frame, density);
        float textRight = showChevron ? rect.Right - 16 * density : rect.Right - 6 * density;
        AndroidUiTheme.DrawCentered(canvas, title, _textPaint, (rect.Left + textRight) / 2,
            rect.CenterY() + _textPaint.TextSize * .36f, textRight - rect.Left);
        if (showChevron)
            AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconChevronDown),
                _chevronPaint, rect.Right - 9 * density, rect.CenterY());
    }

    private void DrawGlyph(Canvas canvas, string token, Paint paint, LayoutRect frame, float density)
        => AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(token), paint,
            (float)((frame.X + frame.Width / 2) * density), (float)((frame.Y + frame.Height / 2) * density));

    /// <summary>「更多」段的三点字形（自绘，不引入第二套图标字体）。</summary>
    private void DrawMoreGlyph(Canvas canvas, LayoutRect frame, float density)
    {
        float cx = (float)((frame.X + frame.Width / 2) * density);
        float cy = (float)((frame.Y + frame.Height / 2) * density);
        float radius = 2 * density;
        float gap = 6 * density;
        using var paint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        canvas.DrawCircle(cx - gap, cy, radius, paint);
        canvas.DrawCircle(cx, cy, radius, paint);
        canvas.DrawCircle(cx + gap, cy, radius, paint);
    }

    /// <summary>点按命中：宽形态按三段或整块弹菜单，手机按筛选/更多分段。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Up && !_menuOpen)
        {
            double density = AndroidUiTheme.Density(Context!);
            double x = e.GetX() / density, y = e.GetY() / density;
            if (_phonePortrait && Contains(_phoneFilter, x, y))
            {
                OpenGroupedMenuAtScreen(ScreenRectOf(_frames.Sort));
                return true;
            }
            if (_phoneGroup && Contains(_phoneMore, x, y))
            {
                MoreRequested?.Invoke(ToScreenRect(_phoneMore));
                return true;
            }
            if (_phoneGroup && Contains(_phoneFilter, x, y))
            {
                OpenGroupedMenuAtScreen(ScreenRectOf(_frames.Sort));
                return true;
            }
            if (!IsPhone && _capsule && Contains(_frames.Sort, x, y))
            {
                OpenGroupedMenuAtScreen(ScreenRectOf(_frames.Sort));
                return true;
            }
            if (!IsPhone && !_capsule)
            {
                if (Contains(_frames.Sort, x, y)) ShowMenu(FilterMenu.Sort);
                else if (Contains(_frames.Order, x, y)) ShowMenu(FilterMenu.Order);
                else if (Contains(_frames.Rating, x, y)) ShowMenu(FilterMenu.Rating);
#if PHOTOVIEWER_DINO
                else if (ShowsSimilarityToggle && Contains(_frames.Toggle, x, y))
                {
                    var filter = _main.FileVM.FilterBar;
                    filter.IsSimilarityPanelOpen = !filter.IsSimilarityPanelOpen;
                    Invalidate();
                }
#endif
            }
#if PHOTOVIEWER_DINO
            else if (!IsPhone && _capsule && ShowsSimilarityToggle && Contains(_frames.Toggle, x, y))
            {
                var filter = _main.FileVM.FilterBar;
                filter.IsSimilarityPanelOpen = !filter.IsSimilarityPanelOpen;
                Invalidate();
            }
#endif
        }
        return true;
    }

    /// <summary>点按矩形换算成屏幕 px（手机「更多」交给宿主弹主图菜单）。</summary>
    private LayoutRect ToScreenRect(LayoutRect frame)
    {
        double density = AndroidUiTheme.Density(Context!);
        var location = new int[2];
        GetLocationOnScreen(location);
        return new LayoutRect(location[0] / density + frame.X, location[1] / density + frame.Y,
            frame.Width, frame.Height);
    }

    /// <summary>在点击的控件下方弹出自绘菜单（夹在屏幕内；空间不足时翻到上方）。</summary>
    /// <param name="kind">菜单类型。</param>
    private void ShowMenu(FilterMenu kind)
    {
        if (Context is not Activity activity) return;
        DropdownOpening?.Invoke();
        DismissDropdown();
        var filter = _main.FileVM.FilterBar;
        var labels = new List<string>();
        var values = new List<object>();
        var kinds = new List<FilterMenu>();
        var frame = kind switch
        {
            FilterMenu.Sort => _frames.Sort,
            FilterMenu.Order => _frames.Order,
            _ => _frames.Rating,
        };
        if (kind == FilterMenu.Sort)
        {
            foreach (var option in filter.SortModes)
            {
                labels.Add(option.DisplayName);
                values.Add((SortMode)option.Value);
                kinds.Add(FilterMenu.Sort);
            }
        }
        if (kind == FilterMenu.Order)
            foreach (var option in filter.SortOrders)
            {
                labels.Add(option.DisplayName);
                values.Add((SortOrder)option.Value);
                kinds.Add(FilterMenu.Order);
            }
        if (kind == FilterMenu.Rating)
            foreach (var option in filter.RatingFilters)
            {
                labels.Add(option.DisplayName);
                values.Add(option.Key);
                kinds.Add(FilterMenu.Rating);
            }
        if (labels.Count == 0) return;

        double density = AndroidUiTheme.Density(activity);
        var menuWidth = Math.Max((int)Math.Round(160 * density), (int)Math.Round(frame.Width * density));
        int itemHeight = (int)Math.Round(40 * density);
        int padding = (int)Math.Round(4 * density);
        int dividerHeight = Math.Max(1, (int)Math.Round(density));
        int groupBreaks = 0;
        for (int index = 1; index < kinds.Count; index++)
            if (kinds[index] != kinds[index - 1]) groupBreaks++;
        int contentHeight = labels.Count * itemHeight + groupBreaks * dividerHeight + padding * 2;
        var metrics = Resources?.DisplayMetrics;
        int screenHeight = metrics?.HeightPixels ?? Height;
        int maxHeight = Math.Max(itemHeight, screenHeight - (int)Math.Round(16 * density));

        var content = new global::Android.Widget.LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Vertical };
        var rowMarks = new List<(TextView Mark, FilterMenu Kind, object Value)>(labels.Count);
        var background = new GradientDrawable();
        background.SetColor(AndroidUiTheme.ControlFill);
        background.SetCornerRadius((float)(6 * density));
        background.SetStroke(Math.Max(1, (int)Math.Round(density)), AndroidUiTheme.ControlBorder);
        content.Background = background;
        content.SetPadding(padding, padding, padding, padding);

        void AddDivider()
        {
            var divider = new View(activity);
            divider.SetBackgroundColor(AndroidUiTheme.Divider);
            content.AddView(divider, new global::Android.Widget.LinearLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.MatchParent, dividerHeight));
        }

        for (int index = 0; index < labels.Count; index++)
        {
            if (index > 0 && kinds[index] != kinds[index - 1]) AddDivider();
            int captured = index;
            bool selected = kinds[index] switch
            {
                FilterMenu.Sort => Equals(values[index], filter.SortMode),
                FilterMenu.Order => Equals(values[index], filter.SortOrder),
                _ => Equals(values[index], filter.SelectedRatingFilter),
            };
            var row = new global::Android.Widget.LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding((int)Math.Round(12 * density), 0, (int)Math.Round(8 * density), 0);
            row.Clickable = true;
            row.Focusable = true;
            var text = new TextView(activity) { Text = labels[index], TextSize = 13 };
            text.SetTextColor(AndroidUiTheme.TextPrimary);
            text.Gravity = GravityFlags.CenterVertical;
            row.AddView(text, new global::Android.Widget.LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MatchParent) { Weight = 1 });
            var mark = new TextView(activity)
            {
                Text = selected ? "✓" : "",
                TextSize = 13,
                Gravity = GravityFlags.Center,
            };
            mark.SetTextColor(AndroidUiTheme.Accent);
            row.AddView(mark, new global::Android.Widget.LinearLayout.LayoutParams((int)Math.Round(20 * density), LinearLayout.LayoutParams.MatchParent));
            rowMarks.Add((mark, kinds[captured], values[captured]));
            row.Click += (_, _) =>
            {
                if (kinds[captured] == FilterMenu.Sort) filter.SortMode = (SortMode)values[captured];
                else if (kinds[captured] == FilterMenu.Order) filter.SortOrder = (SortOrder)values[captured];
                else filter.SelectedRatingFilter = (string)values[captured];
                // FILTER-08：单一入口的菜单选择后立即应用并保持展开，就地更新勾选。
                if (kind == FilterMenu.Grouped)
                {
                    Refresh();
                    foreach (var (mark, rowKind, value) in rowMarks)
                    {
                        bool isSelected = rowKind switch
                        {
                            FilterMenu.Sort => Equals(value, filter.SortMode),
                            FilterMenu.Order => Equals(value, filter.SortOrder),
                            _ => Equals(value, filter.SelectedRatingFilter),
                        };
                        mark.Text = isSelected ? "✓" : "";
                    }
                }
                else
                {
                    DismissDropdown();
                    Refresh();
                }
            };
            content.AddView(row, new global::Android.Widget.LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, itemHeight));
        }

        View popupContent = content;
        if (contentHeight > maxHeight)
        {
            var scroll = new ScrollView(activity);
            scroll.AddView(content);
            popupContent = scroll;
        }

        var geometry = PopupGeometry(kind == FilterMenu.Grouped ? _control : frame, labels.Count);
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
            }
            _menuOpen = false;
            Refresh();
        };
        _dropdown = popup;
        _dropdownKind = kind;
        _dropdownItemCount = labels.Count;
        _dropdownX = geometry.X;
        _dropdownY = geometry.Y;
        _dropdownWidth = geometry.Width;
        _dropdownHeight = geometry.Height;
        _menuOpen = true;
        popup.ShowAtLocation(this, GravityFlags.Top | GravityFlags.Start, geometry.X, geometry.Y);
    }

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

    /// <summary>手机形态的三组单选菜单：点选立即应用并保持展开，就地更新勾选。</summary>
    private void ShowGroupedMenu(FilterBarViewModel filter, List<string> labels, List<object> values,
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
        var rowMarks = new List<(TextView Mark, FilterMenu Kind, object Value)>(labels.Count);

        void AddDivider()
        {
            var divider = new View(activity);
            divider.SetBackgroundColor(AndroidUiTheme.Divider);
            content.AddView(divider, new global::Android.Widget.LinearLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.MatchParent, dividerHeight));
        }

        for (int index = 0; index < labels.Count; index++)
        {
            if (index > 0 && kinds[index] != kinds[index - 1]) AddDivider();
            int captured = index;
            bool selected = kinds[index] switch
            {
                FilterMenu.Sort => Equals(values[index], filter.SortMode),
                FilterMenu.Order => Equals(values[index], filter.SortOrder),
                _ => Equals(values[index], filter.SelectedRatingFilter),
            };
            var row = new LinearLayout(activity) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding((int)Math.Round(12 * density), 0, (int)Math.Round(8 * density), 0);
            row.Clickable = true;
            row.Focusable = true;
            var text = new TextView(activity) { Text = labels[index], TextSize = 13 };
            text.SetTextColor(AndroidUiTheme.TextPrimary);
            text.Gravity = GravityFlags.CenterVertical;
            row.AddView(text, new global::Android.Widget.LinearLayout.LayoutParams(0, global::Android.Views.ViewGroup.LayoutParams.MatchParent) { Weight = 1 });
            var mark = new TextView(activity)
            {
                Text = selected ? "✓" : "",
                TextSize = 13,
                Gravity = GravityFlags.Center,
            };
            mark.SetTextColor(AndroidUiTheme.Accent);
            row.AddView(mark, new global::Android.Widget.LinearLayout.LayoutParams((int)Math.Round(20 * density), global::Android.Views.ViewGroup.LayoutParams.MatchParent));
            rowMarks.Add((mark, kinds[captured], values[captured]));
            row.Click += (_, _) =>
            {
                if (kinds[captured] == FilterMenu.Sort) filter.SortMode = (SortMode)values[captured];
                else if (kinds[captured] == FilterMenu.Order) filter.SortOrder = (SortOrder)values[captured];
                else filter.SelectedRatingFilter = (string)values[captured];
                Refresh();
                foreach (var (rowMark, rowKind, value) in rowMarks)
                {
                    bool isSelected = rowKind switch
                    {
                        FilterMenu.Sort => Equals(value, filter.SortMode),
                        FilterMenu.Order => Equals(value, filter.SortOrder),
                        _ => Equals(value, filter.SelectedRatingFilter),
                    };
                    rowMark.Text = isSelected ? "✓" : "";
                }
            };
            content.AddView(row, new global::Android.Widget.LinearLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.MatchParent, itemHeight));
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
            }
            _menuOpen = false;
            Refresh();
        };
        _dropdown = popup;
        _dropdownKind = FilterMenu.Grouped;
        _dropdownItemCount = labels.Count;
        _dropdownX = geometry.X;
        _dropdownY = geometry.Y;
        _dropdownWidth = geometry.Width;
        _dropdownHeight = geometry.Height;
        _menuOpen = true;
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
        _menuOpen = false;
        if (popup?.IsShowing != true) return false;
        popup.Dismiss();
        return true;
    }

    private static LayoutRect Offset(LayoutRect origin, LayoutRect local)
        => new(origin.X + local.X, origin.Y + local.Y, local.Width, local.Height);

    private static string? TitleForSort(SortMode mode) => mode switch
    {
        SortMode.Name => "名称",
        SortMode.Date => "拍摄时间",
        SortMode.Size => "文件大小",
        _ => "名称",
    };

    private static string? TitleForOrder(SortOrder order) => order == SortOrder.Ascending ? "升序 ↑" : "降序 ↓";

    private static string TitleForRating(string key) => key switch
    {
        "All" => "全部",
        "None" => "无星级",
        "Eq1" => "一星",
        "Eq2" => "二星",
        "Eq3" => "三星",
        "Eq4" => "四星",
        "Eq5" => "五星",
        "Gt1" => "一星以上",
        "Gt2" => "二星以上",
        "Gt3" => "三星以上",
        "Gt4" => "四星以上",
        "Rejected" => "已拒绝",
        "Conflict" => "星级冲突",
        _ => "全部",
    };

    private static RectF ToRectF(LayoutRect rect, float density) => new(
        (float)rect.X * density, (float)rect.Y * density,
        (float)rect.MaxX * density, (float)rect.MaxY * density);

    private static bool Contains(LayoutRect rect, double x, double y)
        => rect.Width > 0 && rect.Height > 0
            && x >= rect.X && x <= rect.MaxX && y >= rect.Y && y <= rect.MaxY;

    /// <summary>筛选菜单类型。</summary>
    private enum FilterMenu { Sort, Order, Rating, Grouped }
}
