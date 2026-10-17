using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Main.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 文件栏筛选条（A3，[FILTER-01]）：排序 / 升降序 / 星级筛选三个自绘下拉控件 +
/// 计数文本 + 相似聚类 toggle。几何全部取共享 <see cref="MainScreenLayout.FilterBarFrames"/>
/// （90×32、间距 5、外边距 10；行布局从安全区前导边起排），点按弹原生 PopupMenu 写回共享筛选状态。
/// </summary>
internal sealed class AndroidFilterBar : View
{
    /// <summary>相似聚类 toggle 是否参与布局（A5 面板落地前置 false，避免出现点了无反应的入口）。</summary>
    internal static bool ShowsSimilarityToggle { get; set; } = true;

    private readonly MainViewModel _main;
    private readonly Paint _textPaint;
    private readonly Paint _countPaint;
    private readonly Paint _togglePaint;
    private MainScreenLayout.FilterFrames _frames;
    private bool _horizontal = true;
    private double _contentWidth = MainScreenLayout.FileBarContentWidth;
    private double _leadingInset;
    private double _countWidth = 60;
    private bool _menuOpen;
    private bool _compact;

    /// <summary>排序控件矩形（dp，验收读取）。</summary>
    internal LayoutRect SortFrame => _frames.Sort;
    /// <summary>升降序控件矩形。</summary>
    internal LayoutRect OrderFrame => _frames.Order;
    /// <summary>星级筛选控件矩形。</summary>
    internal LayoutRect RatingFrame => _frames.Rating;
    /// <summary>计数文本槽矩形。</summary>
    internal LayoutRect CountFrame => _frames.Count;
    /// <summary>相似聚类 toggle 矩形。</summary>
    internal LayoutRect SimilarityFrame => _frames.Toggle;
    /// <summary>计数文本自然宽度（dp，布局用）。</summary>
    internal double CountWidthDp => _countWidth;
    /// <summary>当前是否行布局。</summary>
    internal bool Horizontal => _horizontal;

    /// <summary>构建筛选条并订阅共享筛选状态。</summary>
    /// <param name="context">宿主上下文（Activity）。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidFilterBar(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _textPaint = AndroidUiTheme.TextPaint(context, 13, AndroidUiTheme.TextPrimary);
        _countPaint = AndroidUiTheme.TextPaint(context, 12, Color.ParseColor("#DDDDDD")!);
        _togglePaint = AndroidUiTheme.TextPaint(context, 12, AndroidUiTheme.TextPrimary);
        SetWillNotDraw(false);
        Clickable = true;
        Refresh();
    }

    /// <summary>同步共享筛选状态到控件文本与计数（程序化改动与文件源刷新都不留旧选中项）。</summary>
    internal void Refresh()
    {
        var filter = _main.FileVM.FilterBar;
        string count = _compact ? $"{filter.FilteredCount} 张" : $"共 {filter.FilteredCount} 张";
        _countWidth = Math.Ceiling((_countPaint.MeasureText(count) + 2) / (float)AndroidUiTheme.Density(Context!)) + 2;
        Invalidate();
    }

    /// <summary>按共享几何落位五个控件（父布局在行/列方向变化时调用）。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="contentWidth">筛选条可用内容宽度（dp）。</param>
    /// <param name="leadingInset">列布局首控件的前导让位（dp）。</param>
    internal void Arrange(bool horizontal, double contentWidth, double leadingInset)
    {
        _horizontal = horizontal;
        _contentWidth = Math.Max(1, contentWidth);
        _leadingInset = leadingInset;
        // 手机窄屏：三个下拉与 toggle 按紧凑宽度排布，避免 toggle 被挤出屏幕（其他平台保持 90 点默认）。
        _compact = horizontal && _contentWidth < 600;
        Refresh();
        _frames = MainScreenLayout.FilterBarFrames(horizontal, _contentWidth, leadingInset, _countWidth,
            ShowsSimilarityToggle, alignLeading: true,
            controlWidth: _compact ? 84 : MainScreenLayout.FilterControlWidth,
            toggleWidth: _compact ? 76 : MainScreenLayout.FilterControlWidth);
        Invalidate();
    }

    /// <summary>自绘三个下拉控件、计数与 toggle。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float density = (float)AndroidUiTheme.Density(Context!);
        var filter = _main.FileVM.FilterBar;
        DrawControl(canvas, _frames.Sort, TitleForSort(filter.SortMode), density);
        DrawControl(canvas, _frames.Order, TitleForOrder(filter.SortOrder), density);
        DrawControl(canvas, _frames.Rating, TitleForRating(filter.SelectedRatingFilter), density);
        float countCenterX = (float)((_frames.Count.X + _frames.Count.Width / 2) * density);
        float countBaseline = (float)((_frames.Count.Y + _frames.Count.Height * .78) * density);
        string countText = _compact ? $"{filter.FilteredCount} 张" : $"共 {filter.FilteredCount} 张";
        AndroidUiTheme.DrawCentered(canvas, countText, _countPaint, countCenterX, countBaseline,
            (float)(_frames.Count.Width * density) + 4);
        if (ShowsSimilarityToggle)
        {
            bool open = filter.IsSimilarityPanelOpen;
            var rect = ToRectF(_frames.Toggle, density);
            AndroidUiTheme.DrawRounded(canvas, rect, 6 * density,
                open ? AndroidUiTheme.Accent : AndroidUiTheme.ControlFill,
                open ? AndroidUiTheme.Accent : AndroidUiTheme.ControlBorder, Math.Max(1, density));
            _togglePaint.Color = open ? Color.White : AndroidUiTheme.TextPrimary;
            AndroidUiTheme.DrawCentered(canvas, "相似聚类", _togglePaint, rect.CenterX(),
                rect.CenterY() + _togglePaint.TextSize * .36f, rect.Width() - 6 * density);
        }
    }

    /// <summary>绘制一个 90×32 下拉控件。</summary>
    /// <param name="canvas">画布。</param>
    /// <param name="frame">dp 矩形。</param>
    /// <param name="title">文本。</param>
    /// <param name="density">密度。</param>
    private void DrawControl(Canvas canvas, LayoutRect frame, string? title, float density)
    {
        var rect = ToRectF(frame, density);
        AndroidUiTheme.DrawRounded(canvas, rect, 6 * density, AndroidUiTheme.ControlFill,
            AndroidUiTheme.ControlBorder, Math.Max(1, density));
        AndroidUiTheme.DrawCentered(canvas, title, _textPaint, rect.CenterX(),
            rect.CenterY() + _textPaint.TextSize * .36f, rect.Width() - 8 * density);
    }

    /// <summary>点按命中：三个下拉各自弹菜单，toggle 切换共享展开状态。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Up && !_menuOpen)
        {
            double density = AndroidUiTheme.Density(Context!);
            double x = e.GetX() / density, y = e.GetY() / density;
            if (Contains(_frames.Sort, x, y)) ShowMenu(FilterMenu.Sort);
            else if (Contains(_frames.Order, x, y)) ShowMenu(FilterMenu.Order);
            else if (Contains(_frames.Rating, x, y)) ShowMenu(FilterMenu.Rating);
            else if (ShowsSimilarityToggle && Contains(_frames.Toggle, x, y))
            {
                var filter = _main.FileVM.FilterBar;
                Console.WriteLine($"[Native] 相似聚类 toggle 点击：{filter.IsSimilarityPanelOpen} → {!filter.IsSimilarityPanelOpen}（local {x:0.#},{y:0.#} frame={_frames.Toggle}）");
                filter.IsSimilarityPanelOpen = !filter.IsSimilarityPanelOpen;
                Invalidate();
            }
        }
        return true;
    }

    /// <summary>弹原生下拉菜单，当前项带勾选标记。</summary>
    /// <param name="kind">菜单类型。</param>
    private void ShowMenu(FilterMenu kind)
    {
        var filter = _main.FileVM.FilterBar;
        var menu = new PopupMenu(Context!, this);
        var menuItems = menu.Menu!;
        var values = new List<object>();
        if (kind == FilterMenu.Sort)
        {
            foreach (var option in filter.SortModes)
            {
                var item = menuItems.Add(option.DisplayName)!;
                item.SetCheckable(true);
                item.SetChecked(Equals((SortMode)option.Value, filter.SortMode));
                values.Add((SortMode)option.Value);
            }
        }
        else if (kind == FilterMenu.Order)
        {
            foreach (var option in filter.SortOrders)
            {
                var item = menuItems.Add(option.DisplayName)!;
                item.SetCheckable(true);
                item.SetChecked(Equals((SortOrder)option.Value, filter.SortOrder));
                values.Add((SortOrder)option.Value);
            }
        }
        else
        {
            foreach (var option in filter.RatingFilters)
            {
                var item = menuItems.Add(option.DisplayName)!;
                item.SetCheckable(true);
                item.SetChecked(Equals(option.Key, filter.SelectedRatingFilter));
                values.Add(option.Key);
            }
        }
        _menuOpen = true;
        menu.DismissEvent += (_, _) => { _menuOpen = false; Refresh(); };
        menu.MenuItemClick += (_, args) =>
        {
            int index = args.Item?.ItemId ?? -1;
            if (index < 0 || index >= values.Count) return;
            if (kind == FilterMenu.Sort) filter.SortMode = (SortMode)values[index];
            else if (kind == FilterMenu.Order) filter.SortOrder = (SortOrder)values[index];
            else filter.SelectedRatingFilter = (string)values[index];
        };
        menu.Show();
    }

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
        "Conflict" => "星级冲突",
        _ => "全部",
    };

    private static RectF ToRectF(LayoutRect rect, float density) => new(
        (float)rect.X * density, (float)rect.Y * density,
        (float)rect.MaxX * density, (float)rect.MaxY * density);

    private static bool Contains(LayoutRect rect, double x, double y)
        => x >= rect.X && x <= rect.MaxX && y >= rect.Y && y <= rect.MaxY;

    /// <summary>筛选菜单类型。</summary>
    private enum FilterMenu { Sort, Order, Rating }
}
