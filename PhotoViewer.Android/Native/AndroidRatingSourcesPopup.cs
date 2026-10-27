using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using AndroidX.CustomView.Widget;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>来源浮窗的入口类型：卡片入口默认向下，横排控制栏向上、竖排控制栏向左（RATING-04）。</summary>
internal enum RatingSourcesAnchorKind
{
    /// <summary>胶片栏/相似面板卡片：0 号槽位矩形为锚点。</summary>
    Card,
    /// <summary>控制栏评分组：0 号按钮矩形为锚点。</summary>
    Control,
}

/// <summary>
/// RATING-04 星级来源浮窗：<see cref="PopupWindow"/> 承载一张自绘内容视图，纵向列出目标照片
/// 所属组内的全部实际来源；点击任一条目的任意值都把同一值写入全部来源（RATING-03），
/// 条目内容与开合状态直接取共享 <see cref="RatingSourcesViewModel"/>。
/// 每帧核对真实锚点矩形与宿主矩形：入口被移除、隐藏、移动或滚出可视区即关闭，
/// 不让浮窗停在已经移走的槽位上方。外观取 <c>popup.ratingSources.*</c> / <c>popup.*</c> 与浮窗颜色令牌。
/// </summary>
internal sealed class AndroidRatingSourcesPopup : IDisposable
{
    /// <summary>条目内槽位数（0 号清零叉 + 1–5 星）。</summary>
    internal const int StarCount = 6;
    /// <summary>信息行高：RATING-04 的纯排版布局值，不对应设计令牌。</summary>
    private const double InfoHeight = 16;
    /// <summary>浮窗描边宽度；内容宽之外的一层 1 点描边（与 Web 的 border-box 同口径）。</summary>
    private const double BorderWidth = 1;

    /// <summary>浮窗内容宽 = 六格星排 + 条目内边距 + 浮窗内边距（204dp）；描边另加。</summary>
    internal static double ContentWidthDp => DesignTokens.ToolbarRatingButtonSize * StarCount
        + DesignTokens.PopupRatingSourcesEntryPadding * 2 + DesignTokens.PopupPadding * 2;
    /// <summary>浮窗总宽（含 1 点描边）。</summary>
    internal static double WindowWidthDp => ContentWidthDp + BorderWidth * 2;

    private readonly View _host;
    private readonly Java.Lang.Runnable _pollRunnable;
    private readonly MainViewModel _main;
    private PopupWindow? _window;
    private SourcesContentView? _content;
    private Func<Rect?>? _anchorSource;
    private RatingSourcesAnchorKind _kind;
    private bool _controlHorizontal = true;
    private Rect _anchorRect = new();
    private Rect _hostRect = new();
    private int _x, _y, _width, _height;
    private string _direction = "down";
    private string _signature = string.Empty;
    private bool _pollScheduled;
    private bool _disposed;

    /// <summary>浮窗是否真正显示（与共享 <see cref="RatingSourcesViewModel.IsOpen"/> 同步）。</summary>
    internal bool IsOpen => _window?.IsShowing == true;
    /// <summary>本次展开方向（<c>up</c> / <c>down</c> / <c>left</c>，验收用）。</summary>
    internal string DirectionForChecks => _direction;
    /// <summary>本次锚点矩形（屏幕 px，验收用）。</summary>
    internal Rect AnchorRectForChecks => new(_anchorRect);
    /// <summary>浮窗宽高（px，验收用）。</summary>
    internal int WidthPxForChecks => _width;
    /// <summary>浮窗高度（px，验收用）。</summary>
    internal int HeightPxForChecks => _height;
    /// <summary>浮窗内容视图（验收派发触摸用）。</summary>
    internal View? ContentViewForChecks => _content;

    /// <summary>绑定宿主根视图与共享主 VM。</summary>
    /// <param name="host">宿主根视图（屏幕坐标基准与 ShowAtLocation 的锚）。</param>
    /// <param name="main">共享主 VM。</param>
    internal AndroidRatingSourcesPopup(View host, MainViewModel main)
    {
        _host = host;
        _main = main;
        _pollRunnable = new Java.Lang.Runnable(Poll);
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    /// <summary>
    /// 打开浮窗：先取最新来源状态，再按真实锚点矩形决定方向与位置。
    /// </summary>
    /// <param name="target">来源所属照片（可以是列表里的非当前卡片）。</param>
    /// <param name="kind">入口类型。</param>
    /// <param name="controlHorizontal">控制栏入口是否为横排（底部横排向上、右侧竖排向左）。</param>
    /// <param name="anchorSource">返回入口 0 号槽位当前屏幕矩形的委托；入口隐藏/移除时返回 null。</param>
    /// <returns>成功展开返回 true。</returns>
    internal bool Show(ImageFile target, RatingSourcesAnchorKind kind, bool controlHorizontal, Func<Rect?> anchorSource)
    {
        if (_disposed || _host.Width < 1 || _host.Height < 1) return false;
        Close();
        if (anchorSource() is not { } anchor || anchor.Width() <= 0 || anchor.Height() <= 0) return false;
        _main.ShowRatingSources(target);
        if (_main.RatingSources.Items.Count == 0)
        {
            _main.RatingSources.Close();
            return false;
        }

        _kind = kind;
        _controlHorizontal = controlHorizontal;
        _anchorSource = anchorSource;
        _anchorRect = new Rect(anchor);
        _hostRect = HostScreenRect();
        _signature = SignatureOf(_main.RatingSources.Items);
        var content = new SourcesContentView(_host.Context!, _main.RatingSources.Items)
        {
            RatingChosen = Choose,
            CloseRequested = Close,
        };
        _content = content;
        double density = AndroidUiTheme.Density(_host.Context!);
        int width = (int)Math.Round(WindowWidthDp * density);
        int height = Math.Max(1, content.ContentHeightPx);
        var window = new PopupWindow(content, width, height, true)
        {
            OutsideTouchable = true,
            Focusable = true,
            Elevation = (float)(12 * density),
        };
        window.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        window.DismissEvent += (_, _) => OnWindowDismissed(window);
        _window = window;
        Place(width, height);
        window.ShowAtLocation(_host, GravityFlags.Top | GravityFlags.Start, _x, _y);
        content.RequestFocus();
        Console.WriteLine($"[Native] 来源浮窗 direction={_direction} anchor=({_anchorRect.Left},{_anchorRect.Top},"
            + $"{_anchorRect.Width()}x{_anchorRect.Height()}) show=({_x},{_y}) size={width}x{height} "
            + $"items={_main.RatingSources.Items.Count}");
        SchedulePoll();
        return true;
    }

    /// <summary>收起浮窗：先收原生窗口，再同步共享状态。</summary>
    internal void Close()
    {
        bool open = IsOpen;
        Dismiss();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        if (open) Console.WriteLine("[Native] 来源浮窗关闭");
    }

    /// <summary>
    /// 每帧核对：共享浮窗状态、目标组是否仍冲突、入口 0 号槽位的真实屏幕矩形与宿主矩形。
    /// 任一项变化即关闭；条目内容变化只重建内容并重新定位。
    /// </summary>
    internal void Update()
    {
        if (!IsOpen) return;
        if (!_main.RatingSources.IsOpen)
        {
            Dismiss();
            return;
        }
        var anchor = _anchorSource?.Invoke();
        var host = HostScreenRect();
        bool conflict = _main.RatingSources.Target?.HasRatingConflict == true;
        if (!conflict || anchor is not { } current || !SameRect(current, _anchorRect)
            || !SameRect(host, _hostRect))
        {
            Console.WriteLine($"[Native] 来源浮窗关闭 conflict={conflict} "
                + $"anchor={(anchor is { } value ? value.ToString() : "null")} expected={_anchorRect}");
            Close();
            return;
        }
        Refresh();
    }

    /// <summary>条目内某一槽位的内容视图坐标（px，验收点按用）；越界返回空矩形。</summary>
    internal Rect SlotRectPx(int entryIndex, int rating)
        => _content?.SlotRect(entryIndex, rating) ?? new Rect();

    /// <summary>共享状态变化：条目重建；共享侧收起时原生窗口同步 dismiss。</summary>
    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(RatingSourcesViewModel.Items))
        {
            Refresh();
            return;
        }
        if (args.PropertyName == nameof(RatingSourcesViewModel.IsOpen) && !_main.RatingSources.IsOpen && IsOpen)
            Dismiss();
    }

    /// <summary>按共享条目重建内容；签名未变时不打断当前绘制。</summary>
    private void Refresh()
    {
        if (!IsOpen || _content == null || _window == null) return;
        var items = _main.RatingSources.Items;
        string signature = SignatureOf(items);
        if (signature == _signature) return;
        _signature = signature;
        // PopupWindow 显示期间 setContentView 是空操作：直接改写现有内容视图的条目，再按新高度重排。
        _content.SetItems(items);
        int width = _width;
        int height = Math.Max(1, _content.ContentHeightPx);
        Place(width, height);
        _window.Update(_x, _y, width, height);
        SchedulePoll();
    }

    /// <summary>任一条目的任意值都整组写入；关闭与否由共享 VM 按写入结果与冲突是否解除决定。</summary>
    /// <param name="rating">目标星级 0–5。</param>
    private void Choose(int rating)
    {
        if (!IsOpen) return;
        Console.WriteLine($"[Native] 来源浮窗整组写入 {rating}");
        _ = ChooseAsync(rating);
    }

    private async Task ChooseAsync(int rating)
    {
        await _main.RatingSources.SetRatingAsync(rating);
        // 共享 VM 在成功且冲突解除时已自行收起；失败或仍冲突时补一次刷新，保留浮窗并显示最新来源。
        _host.Post(Update);
    }

    /// <summary>按入口类型与实际空间决定展开方向，并把浮窗夹在宿主屏幕范围内。</summary>
    private void Place(int width, int height)
    {
        var host = HostScreenRect();
        _hostRect = host;
        int gap = (int)Math.Round(DesignTokens.PopupRatingSourcesArrowMargin * AndroidUiTheme.Density(_host.Context!));
        string direction;
        if (_kind == RatingSourcesAnchorKind.Control)
        {
            direction = _controlHorizontal ? "up" : "left";
        }
        else
        {
            int below = host.Bottom - _anchorRect.Bottom;
            int above = _anchorRect.Top - host.Top;
            direction = below < height + gap && above >= height + gap ? "up" : "down";
        }
        _direction = direction;
        int x, y;
        switch (direction)
        {
            case "up":
                x = _anchorRect.CenterX() - width / 2;
                y = _anchorRect.Top - gap - height;
                break;
            case "left":
                x = _anchorRect.Left - gap - width;
                y = _anchorRect.CenterY() - height / 2;
                break;
            default:
                x = _anchorRect.CenterX() - width / 2;
                y = _anchorRect.Bottom + gap;
                break;
        }
        _x = Math.Clamp(x, host.Left, Math.Max(host.Left, host.Right - width));
        _y = Math.Clamp(y, host.Top, Math.Max(host.Top, host.Bottom - height));
        _width = width;
        _height = height;
    }

    /// <summary>宿主根视图的屏幕矩形（含系统栏，浮窗夹取用它而不是整屏）。</summary>
    private Rect HostScreenRect()
    {
        var location = new int[2];
        _host.GetLocationOnScreen(location);
        return new Rect(location[0], location[1], location[0] + _host.Width, location[1] + _host.Height);
    }

    private void SchedulePoll()
    {
        if (_pollScheduled || _disposed) return;
        _pollScheduled = true;
        _host.PostOnAnimation(_pollRunnable);
    }

    private void Poll()
    {
        _pollScheduled = false;
        if (_disposed) return;
        Update();
        if (IsOpen) SchedulePoll();
    }

    /// <summary>外部（外部点击、返回键、窗口销毁）收起原生窗口时同步共享状态。</summary>
    private void OnWindowDismissed(PopupWindow window)
    {
        if (!ReferenceEquals(window, _window)) return;
        _window = null;
        _content = null;
        _anchorSource = null;
        _signature = string.Empty;
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    private void Dismiss()
    {
        var window = _window;
        _window = null;
        _content = null;
        _anchorSource = null;
        _signature = string.Empty;
        if (window != null && window.IsShowing) window.Dismiss();
    }

    private static bool SameRect(Rect left, Rect right)
        => left.Left == right.Left && left.Top == right.Top
            && left.Right == right.Right && left.Bottom == right.Bottom;

    private static string SignatureOf(IReadOnlyList<RatingSourceItem> items)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            builder.Append(item.Name).Append('|').Append(item.Kind).Append('|').Append(item.Status)
                .Append('|').Append((int)item.State.State).Append('|').Append(item.State.Value).Append("||");
        }
        return builder.ToString();
    }

    /// <summary>释放订阅与原生窗口。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        Dismiss();
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    /// <summary>单个条目的排版结果（px，内容视图坐标）。</summary>
    private readonly record struct EntryLayout(
        bool TwoLines,
        float Height,
        float NameLeft,
        float NameBaseline,
        float NameLimit,
        float BadgeLeft,
        float BadgeWidth,
        float BadgeTop,
        float BadgeBaseline,
        float StatusRight,
        float StatusBaseline,
        float StatusLimit,
        float StarsLeft,
        float StarsTop);

    /// <summary>
    /// 自绘内容：纵向条目 + 条目间分隔线，条目内信息行（名称 / 来源标签 / 状态）与 0–5 星排。
    /// 星排与控制栏评分组共用槽位（30dp）、字形与悬停/按下底色；文本不参与撑宽。
    /// </summary>
    private sealed class SourcesContentView : View
    {
        private IReadOnlyList<RatingSourceItem> _items;
        private readonly List<EntryLayout> _entries = [];
        private readonly float _density;
        private readonly Paint _namePaint;
        private readonly Paint _metaPaint;
        private readonly Paint _dividerPaint;
        private readonly Paint _starOnPaint;
        private readonly Paint _starOffPaint;
        private readonly Paint _clearPaint;
        private readonly Paint _badgePaint;
        private readonly SourcesTouchHelper _accessibility;
        private int _hoveredSlot = -1;
        private int _pressedSlot = -1;

        /// <summary>槽位点击（0–5）；写入与关闭由浮窗统一处理。</summary>
        internal Action<int>? RatingChosen { get; set; }

        /// <summary>Esc 请求收起（RATING-04：Esc 与返回键同级）。</summary>
        internal Action? CloseRequested { get; set; }

        /// <summary>内容高度（px）；浮窗窗口高度即取此值。</summary>
        internal int ContentHeightPx { get; private set; }

        /// <summary>建立内容视图并排好条目。</summary>
        /// <param name="context">宿主上下文。</param>
        /// <param name="items">共享来源条目。</param>
        internal SourcesContentView(Context context, IReadOnlyList<RatingSourceItem> items) : base(context)
        {
            _items = items;
            _density = (float)AndroidUiTheme.Density(context);
            _namePaint = TextPaint(context, DesignTokens.PopupRatingSourcesNameFontSize, AndroidUiTheme.TextPrimary);
            _metaPaint = TextPaint(context, DesignTokens.PopupRatingSourcesMetaFontSize, AndroidUiTheme.TextSecondary);
            _dividerPaint = AndroidUiTheme.Paint(AndroidUiTheme.FromToken(DesignTokens.ColorMenuSeparator));
            _starOnPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
            _starOffPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
            _clearPaint = AndroidUiTheme.Paint(AndroidUiTheme.StarClear, Math.Max(1, Px(1)));
            _clearPaint.SetStyle(Paint.Style.Stroke);
            _badgePaint = AndroidUiTheme.Paint(AndroidUiTheme.FromToken(DesignTokens.ColorMenuItemPressed));
            ContentDescription = "评分来源";
            // 浮窗自身持有焦点，硬件键盘的 Esc 才会送到这里（返回键由 PopupWindow 处理）。
            Focusable = true;
            FocusableInTouchMode = true;
            _accessibility = new SourcesTouchHelper(this);
            ViewCompat.SetAccessibilityDelegate(this, _accessibility);
            SetItems(items);
        }

        /// <summary>替换条目并重排（来源在浮窗打开期间被外部刷新时使用）。</summary>
        /// <param name="items">新的共享来源条目。</param>
        internal void SetItems(IReadOnlyList<RatingSourceItem> items)
        {
            _items = items;
            BuildLayout();
            _accessibility.InvalidateRoot();
            Invalidate();
        }

        /// <summary>某一槽位在内容视图内的矩形（px）。</summary>
        /// <param name="entryIndex">条目索引。</param>
        /// <param name="rating">0–5。</param>
        /// <returns>矩形；越界返回空矩形。</returns>
        internal Rect SlotRect(int entryIndex, int rating)
        {
            if (entryIndex < 0 || entryIndex >= _entries.Count || rating < 0 || rating >= StarCount) return new Rect();
            var layout = _entries[entryIndex];
            float size = Px(DesignTokens.ToolbarRatingButtonSize);
            float left = layout.StarsLeft + rating * size;
            return new Rect((int)Math.Round(left), (int)Math.Round(layout.StarsTop),
                (int)Math.Round(left + size), (int)Math.Round(layout.StarsTop + size));
        }

        /// <summary>按字号/颜色创建左对齐文本画笔。</summary>
        private static Paint TextPaint(Context context, double sizeSp, Color color)
        {
            var paint = AndroidUiTheme.TextPaint(context, sizeSp, color);
            paint.TextAlign = Paint.Align.Left;
            return paint;
        }

        /// <summary>按令牌与文本实测宽度排出各条目：名称放不下时改为双行，星排始终为满宽星槽。</summary>
        private void BuildLayout()
        {
            _entries.Clear();
            float border = Px(BorderWidth);
            float padding = Px(DesignTokens.PopupPadding);
            float entryPadding = Px(DesignTokens.PopupRatingSourcesEntryPadding);
            float labelGap = Px(DesignTokens.PopupRatingSourcesLabelGap);
            float metaLineGap = Px(DesignTokens.PopupRatingSourcesMetaLineGap);
            float infoHeight = Px(InfoHeight);
            float starSize = Px(DesignTokens.ToolbarRatingButtonSize);
            float spacing = Px(DesignTokens.PopupRatingSourcesEntrySpacing);
            float badgePadding = Px(DesignTokens.PopupRatingSourcesBadgePaddingX);
            float entryLeft = border + padding;
            float entryWidth = Px(WindowWidthDp) - 2 * (border + padding);
            float available = entryWidth - 2 * entryPadding;
            float y = border + padding;
            for (int index = 0; index < _items.Count; index++)
            {
                var item = _items[index];
                float badgeWidth = _metaPaint.MeasureText(item.Kind) + badgePadding * 2;
                float statusWidth = Math.Min(_metaPaint.MeasureText(item.Status),
                    Math.Max(0, available - badgeWidth - labelGap));
                float metaWidth = badgeWidth + labelGap + statusWidth;
                bool twoLines = _namePaint.MeasureText(item.Name) + labelGap + metaWidth > available + .5f;
                float nameLimit = twoLines ? available : Math.Max(0, available - labelGap - metaWidth);
                float infoTop = y + entryPadding;
                float metaTop = twoLines ? infoTop + infoHeight + metaLineGap : infoTop;
                float starsTop = metaTop + infoHeight + labelGap;
                float height = starsTop + starSize + entryPadding - y;
                float statusRight = entryLeft + entryWidth - entryPadding;
                _entries.Add(new EntryLayout(
                    twoLines,
                    height,
                    entryLeft + entryPadding,
                    TextBaseline(_namePaint, infoTop, infoHeight),
                    nameLimit,
                    statusRight - statusWidth - labelGap - badgeWidth,
                    badgeWidth,
                    metaTop,
                    TextBaseline(_metaPaint, metaTop, infoHeight),
                    statusRight,
                    TextBaseline(_metaPaint, metaTop, infoHeight),
                    statusWidth,
                    entryLeft + entryPadding,
                    starsTop));
                y += height;
                if (index < _items.Count - 1) y += spacing;
            }
            ContentHeightPx = (int)Math.Ceiling(y + padding + border);
        }

        /// <summary>在给定行带内垂直居中的文本基线。</summary>
        private static float TextBaseline(Paint paint, float bandTop, float bandHeight)
        {
            if (paint.GetFontMetrics() is not { } metrics) return bandTop + bandHeight;
            return bandTop + (bandHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
        }

        /// <summary>绘制浮窗底板、分隔线与全部条目。</summary>
        /// <param name="canvas">画布。</param>
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float border = Px(BorderWidth);
            var outer = new RectF(0, 0, Width, Height);
            AndroidUiTheme.DrawRounded(canvas, outer,
                (float)(DesignTokens.PopupRatingSourcesCornerRadius * _density),
                AndroidUiTheme.FromToken(DesignTokens.ColorPopupBackground),
                AndroidUiTheme.FromToken(DesignTokens.ColorPopupBorder), Math.Max(1f, border));
            float entryLeft = Px(BorderWidth + DesignTokens.PopupPadding);
            float entryWidth = Px(ContentWidthDp - DesignTokens.PopupPadding * 2);
            float dividerHeight = Math.Max(1f, Px(DesignTokens.PopupRatingSourcesDividerHeight));
            float spacing = Px(DesignTokens.PopupRatingSourcesEntrySpacing);
            float y = Px(BorderWidth + DesignTokens.PopupPadding);
            for (int index = 0; index < _entries.Count; index++)
            {
                var layout = _entries[index];
                DrawEntry(canvas, index, layout);
                y += layout.Height;
                if (index < _entries.Count - 1)
                {
                    // 分隔线只占条目间距的中间，条目间距合计仍为 entrySpacing。
                    float top = y + (spacing - dividerHeight) / 2;
                    canvas.DrawRect(entryLeft, top, entryLeft + entryWidth, top + dividerHeight, _dividerPaint);
                    y += spacing;
                }
            }
        }

        /// <summary>绘制单个来源条目：文件名、来源标签、状态与 0–5 星排。</summary>
        private void DrawEntry(Canvas canvas, int index, EntryLayout layout)
        {
            var item = _items[index];
            float radius = (float)(DesignTokens.PopupRatingSourcesBadgeCornerRadius * _density);
            _namePaint.TextAlign = Paint.Align.Left;
            canvas.DrawText(MiddleEllipsize(item.Name, _namePaint, layout.NameLimit),
                layout.NameLeft, layout.NameBaseline, _namePaint);
            var badge = new RectF(layout.BadgeLeft, layout.BadgeTop,
                layout.BadgeLeft + layout.BadgeWidth, layout.BadgeTop + Px(InfoHeight));
            canvas.DrawRoundRect(badge, radius, radius, _badgePaint);
            _metaPaint.TextAlign = Paint.Align.Left;
            canvas.DrawText(item.Kind, badge.Left + Px(DesignTokens.PopupRatingSourcesBadgePaddingX),
                layout.BadgeBaseline, _metaPaint);
            _metaPaint.TextAlign = Paint.Align.Right;
            canvas.DrawText(MiddleEllipsize(item.Status, _metaPaint, layout.StatusLimit),
                layout.StatusRight, layout.StatusBaseline, _metaPaint);
            _metaPaint.TextAlign = Paint.Align.Left;

            float starSize = Px(DesignTokens.ToolbarRatingButtonSize);
            int? rated = item.State.State == XmpRatingState.Rated ? item.State.Value : null;
            for (int rating = 0; rating < StarCount; rating++)
            {
                float left = layout.StarsLeft + rating * starSize;
                var slot = new RectF(left, layout.StarsTop, left + starSize, layout.StarsTop + starSize);
                int flat = index * StarCount + rating;
                if (flat == _pressedSlot || flat == _hoveredSlot)
                {
                    using var feedback = AndroidUiTheme.Paint(AndroidUiTheme.FromToken(flat == _pressedSlot
                        ? DesignTokens.ColorToolbarRatingPressedBackground
                        : DesignTokens.ColorToolbarRatingHoverBackground));
                    canvas.DrawRoundRect(slot, 4 * _density, 4 * _density, feedback);
                }
                float cx = slot.CenterX(), cy = slot.CenterY();
                if (rating == 0)
                {
                    float arm = 4.5f * _density;
                    canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, _clearPaint);
                    canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, _clearPaint);
                    continue;
                }
                // 只有 Rated 才点亮：Missing / 无效 / 不可读 / 冲突 / 已拒绝都不冒充 0 星。
                using var star = AndroidUiTheme.StarPath(cx, cy, 7 * _density, 3.2f * _density);
                canvas.DrawPath(star, rating <= (rated ?? 0) ? _starOnPaint : _starOffPaint);
            }
        }

        /// <summary>触点落在哪个槽位：返回 <c>entryIndex * 6 + rating</c>，未命中返回 -1。</summary>
        private int SlotAt(float x, float y)
        {
            float starSize = Px(DesignTokens.ToolbarRatingButtonSize);
            for (int index = 0; index < _entries.Count; index++)
            {
                var layout = _entries[index];
                if (y < layout.StarsTop || y > layout.StarsTop + starSize) continue;
                float relative = x - layout.StarsLeft;
                if (relative < 0 || relative >= starSize * StarCount) continue;
                return index * StarCount + Math.Clamp((int)(relative / starSize), 0, StarCount - 1);
            }
            return -1;
        }

        /// <summary>点按槽位：按下即高亮，抬手命中同一槽位才写入整组。</summary>
        /// <param name="e">事件。</param>
        /// <returns>消费。</returns>
        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _pressedSlot = SlotAt(e.GetX(), e.GetY());
                    Invalidate();
                    return true;
                case MotionEventActions.Move:
                    if (_pressedSlot >= 0 && SlotAt(e.GetX(), e.GetY()) != _pressedSlot)
                    {
                        _pressedSlot = -1;
                        Invalidate();
                    }
                    return true;
                case MotionEventActions.Up:
                    int slot = SlotAt(e.GetX(), e.GetY());
                    int pressed = _pressedSlot;
                    _pressedSlot = -1;
                    Invalidate();
                    if (slot >= 0 && slot == pressed) RatingChosen?.Invoke(slot % StarCount);
                    return true;
                case MotionEventActions.Cancel:
                    _pressedSlot = -1;
                    Invalidate();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>Esc 收起浮窗；其余按键交回系统。</summary>
        /// <param name="keyCode">键码。</param>
        /// <param name="e">事件。</param>
        /// <returns>是否消费。</returns>
        public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Escape)
            {
                CloseRequested?.Invoke();
                return true;
            }
            return base.OnKeyUp(keyCode, e);
        }

        /// <summary>真实鼠标/触控板悬停反馈（触屏不合成悬停）。</summary>
        /// <param name="e">事件。</param>
        /// <returns>处理结果。</returns>
        public override bool OnHoverEvent(MotionEvent? e)
        {
            if (e == null) return base.OnHoverEvent(e);
            switch (e.ActionMasked)
            {
                case MotionEventActions.HoverEnter:
                case MotionEventActions.HoverMove:
                    int slot = SlotAt(e.GetX(), e.GetY());
                    if (slot != _hoveredSlot)
                    {
                        _hoveredSlot = slot;
                        Invalidate();
                    }
                    break;
                case MotionEventActions.HoverExit:
                    if (_hoveredSlot >= 0)
                    {
                        _hoveredSlot = -1;
                        Invalidate();
                    }
                    break;
            }
            return base.OnHoverEvent(e);
        }

        /// <summary>中部省略：保留首尾，避免丢掉扩展名与关键区分部分。</summary>
        private static string MiddleEllipsize(string? text, Paint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return string.Empty;
            if (paint.MeasureText(text) <= maxWidth) return text;
            const string ellipsis = "…";
            string Candidate(int kept)
            {
                int head = (kept + 1) / 2;
                int tail = kept - head;
                return head <= 0 ? ellipsis
                    : string.Concat(text.AsSpan(0, head), ellipsis, text.AsSpan(text.Length - tail));
            }
            int low = 0, high = text.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (paint.MeasureText(Candidate(middle)) <= maxWidth) low = middle;
                else high = middle - 1;
            }
            return Candidate(low);
        }

        /// <summary>dp → px。</summary>
        private float Px(double dp) => (float)(dp * _density);

        /// <summary>读屏虚拟节点：每个条目的 0–5 槽位各一个按钮，点按与触摸走同一写入入口。</summary>
        private sealed class SourcesTouchHelper : ExploreByTouchHelper
        {
            private readonly SourcesContentView _owner;

            /// <summary>绑定内容视图。</summary>
            /// <param name="owner">内容视图。</param>
            public SourcesTouchHelper(SourcesContentView owner) : base(owner) => _owner = owner;

            /// <summary>按本地像素坐标找槽位。</summary>
            protected override int GetVirtualViewAt(float x, float y) => _owner.SlotAt(x, y);

            /// <summary>按条目与槽位顺序列出全部虚拟节点。</summary>
            protected override void GetVisibleVirtualViews(IList<Java.Lang.Integer>? virtualViewIds)
            {
                if (virtualViewIds == null) return;
                for (int index = 0; index < _owner._entries.Count * StarCount; index++)
                    virtualViewIds.Add(Java.Lang.Integer.ValueOf(index));
            }

            /// <summary>填充名称、选中态与屏幕边界。</summary>
            protected override void OnPopulateNodeForVirtualView(int virtualViewId, AccessibilityNodeInfoCompat? node)
            {
                if (node == null) return;
                int entryIndex = virtualViewId / StarCount, rating = virtualViewId % StarCount;
                if (entryIndex < 0 || entryIndex >= _owner._items.Count)
                {
                    node.ContentDescription = "";
                    node.SetBoundsInScreen(new Rect());
                    return;
                }
                var item = _owner._items[entryIndex];
                node.ContentDescription = $"{item.Name} {(rating == 0 ? "无星级" : $"{rating} 星")}";
                node.ClassName = "android.widget.Button";
                node.Focusable = true;
                node.Enabled = true;
                node.Clickable = true;
                node.VisibleToUser = true;
                node.Selected = rating > 0 && item.State.State == XmpRatingState.Rated && item.State.Value == rating;
                node.AddAction((int)global::Android.Views.Accessibility.Action.Click);
                var rect = _owner.SlotRect(entryIndex, rating);
                var location = new int[2];
                _owner.GetLocationOnScreen(location);
                node.SetBoundsInScreen(new Rect(location[0] + rect.Left, location[1] + rect.Top,
                    location[0] + rect.Right, location[1] + rect.Bottom));
            }

            /// <summary>读屏点按：与触摸同一写入入口。</summary>
            protected override bool OnPerformActionForVirtualView(int virtualViewId, int action, Bundle? arguments)
            {
                if (action != (int)global::Android.Views.Accessibility.Action.Click) return false;
                _owner.RatingChosen?.Invoke(virtualViewId % StarCount);
                return true;
            }
        }
    }
}
