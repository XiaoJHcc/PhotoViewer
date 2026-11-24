using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Graphics.Drawables.Shapes;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using Google.Android.Material.Button;
using Google.Android.Material.Shape;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// RATING-04/07：窗口内玻璃面板承载星级选择器与横排条目。布局取共享几何，控件与反馈由 Android 承载；
/// 紧凑态纯星级入口的滑动/拖放/取消三区由共享 <see cref="CompactRatingDragGesture"/> 驱动，
/// 卡片长按仍走 <see cref="RatingGesture"/>。面板宿主是根视图最上层的
/// <see cref="AndroidGlassPopupLayer"/>，与菜单、筛选下拉共用同一宿主。
/// </summary>
internal sealed class AndroidRatingPopup : IDisposable
{
    private const int StarCount = RatingControlGeometry.SlotCount;
    private const double TouchTarget = 48; // Android 推荐的触摸目标；不改变已冻结的主屏胶囊。
    private readonly View _host;
    private readonly MainViewModel _main;
    private readonly AndroidGlassPopupLayer _popupLayer;
    private readonly Func<AndroidInsets> _safeInsets;
    private readonly Java.Lang.Runnable _poll;
    private AndroidGlassPopupSession? _session;
    private RatingPanel? _panel;
    private View? _entry;
    private Func<bool>? _entryValid;
    private Rect? _anchor;
    private Rect? _root;
    private LayoutRect _bounds;
    private RatingPopupDirection _direction;
    private RatingPopupAnchor _kind;
    private RatingPopupMode _mode;
    private bool _horizontal;
    private bool _disposed;
    private ImageFile? _target;
    private Entry[] _entries = [];
    private RatingGesture? _gesture;
    private CompactRatingDragGesture? _compactGesture;
    private View? _gestureOwner;
    private long _downTime;
    private bool _busy;
    private double Density => AndroidUiTheme.Density(_host.Context!);

    internal AndroidRatingPopup(View host, MainViewModel main, AndroidGlassPopupLayer popupLayer,
        Func<AndroidInsets>? safeInsets = null)
    {
        _host = host;
        _main = main;
        _popupLayer = popupLayer;
        _safeInsets = safeInsets ?? (() => default);
        _poll = new Java.Lang.Runnable(Poll);
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    internal bool IsOpen => _session is { IsOpen: true };
    internal string DirectionForChecks => _direction.ToString().ToLowerInvariant();
    internal Rect AnchorRectForChecks => _anchor == null ? new Rect() : new Rect(_anchor);
    internal int WidthPxForChecks => _session?.Frame.Width() ?? 0;
    internal int HeightPxForChecks => _session?.Frame.Height() ?? 0;
    internal View? ContentViewForChecks => _panel;
    internal double PitchDpForChecks => _panel?.Pitch ?? 0;
    internal RatingPopupMode ModeForChecks => _mode;
    internal RatingPopupAnchor AnchorKindForChecks => _kind;
    internal Rect SlotRectPx(int entryIndex, int rating) => _panel?.SlotRect(entryIndex, rating) ?? new Rect();
    internal int? PreviewForChecks => _compactGesture?.Preview ?? _gesture?.Preview;
    internal int? CompactPreviewForChecks => _compactGesture?.Preview;
    internal int? DropHoveredSlotForChecks => _panel?.DropHoveredSlotForChecks;
    internal int? ScaledSlotForChecks => _panel?.ScaledSlotForChecks;
    internal float ScaledScaleForChecks => _panel?.ScaledScaleForChecks ?? 1f;
    internal string CompactZoneForChecks => _compactGesture?.Zone?.ToString().ToLowerInvariant() ?? "";
    internal Rect PanelRectForChecks => _panel == null ? new Rect() : ScreenRect(_panel);

    internal bool Show(ImageFile target, RatingPopupAnchor kind, RatingPopupDirection direction,
        bool horizontal, View entry, Func<bool>? entryValid = null)
    {
        if (_disposed || _busy || !_host.IsAttachedToWindow || entryValid?.Invoke() == false
            || VisibleScreenRect(entry) is not { } anchor)
            return false;
        Close();
        _target = target;
        _entry = entry;
        _entryValid = entryValid ?? (() => entry.IsAttachedToWindow && entry.IsShown);
        _anchor = anchor;
        _root = VisibleScreenRect(_host);
        _bounds = EdgeBounds();
        _kind = kind;
        _direction = direction;
        _horizontal = horizontal;
        _main.ShowRatingSources(target);
        var panel = new RatingPanel(_host.Context!, Choose, Close);
        _panel = panel;
        var frame = BuildPanel(RatingPopupLayout.ResolveMode(kind, target.HasRatingConflict));
        AndroidGlassPopupSession? opened = null;
        opened = _popupLayer.Show(new AndroidGlassPopupRequest(panel, frame,
            CornerRadius(frame), AndroidUiTheme.FromToken(DesignTokens.ColorPopupBackground),
            Color.Transparent!, 0, "星级", entry, () => OnClosed(opened)));
        if (opened == null)
        {
            _panel = null;
            _target = null;
            _entry = null;
            _entryValid = null;
            _anchor = null;
            _root = null;
            _entries = [];
            if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
            return false;
        }
        _session = opened;
        _host.PostOnAnimation(_poll);
        return true;
    }

    internal void Close()
    {
        var session = _session;
        if (session != null) _popupLayer.Dismiss(session);
    }

    private void OnClosed(AndroidGlassPopupSession? closed)
    {
        if (closed == null || !ReferenceEquals(closed, _session)) return;
        _host.RemoveCallbacks(_poll);
        _session = null;
        _panel = null;
        _entry = _gestureOwner = null;
        _entryValid = null;
        _target = null;
        _gesture = null;
        _compactGesture = null;
        _entries = [];
        _busy = false;
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
    }

    private void Poll()
    {
        if (!IsOpen) return;
        if (!_main.RatingSources.IsOpen || _entry == null || _entryValid?.Invoke() != true
            || !SameRect(VisibleScreenRect(_entry), _anchor) || !SameRect(VisibleScreenRect(_host), _root)
            || EdgeBounds() != _bounds || AndroidNativeSettingsPresenter.IsOpen || AndroidToolsPresenter.IsOpen)
        {
            Close();
            return;
        }
        Refresh();
        if (IsOpen) _host.PostOnAnimation(_poll);
    }

    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(RatingSourcesViewModel.IsOpen) || _main.RatingSources.IsOpen) return;
        var session = _session;
        if (session == null) return;
        if (UiDispatcher.CheckAccess()) _popupLayer.Dismiss(session);
        else _host.Post(() => { if (ReferenceEquals(_session, session)) _popupLayer.Dismiss(session); });
    }

    private Entry[] ReadEntries(RatingPopupMode mode)
    {
        var file = _target!;
        if (mode == RatingPopupMode.Selector)
            return [new(null, null, null, null, file.Rating >= 0 ? file.Rating : null)];
        if (mode == RatingPopupMode.Sources && _main.RatingSources.Items.Count > 0)
            return _main.RatingSources.Items.Select(item => new Entry(item.Name, null, item.Kind, item.Status,
                item.State.State == XmpRatingState.Rated ? item.State.Value : null)).ToArray();
        return [new(file.DisplayName, ShotTimeText.FormatShortShotTime(file.PhotoDate), null, null,
            file.Rating >= 0 ? file.Rating : null)];
    }

    private void Refresh()
    {
        if (_target == null || _session == null || _gesture != null || _compactGesture != null) return;
        var mode = RatingPopupLayout.ResolveMode(_kind, _target.HasRatingConflict);
        var entries = ReadEntries(mode);
        if (_mode == mode && _entries.SequenceEqual(entries)) return;
        var frame = BuildPanel(mode, entries);
        _popupLayer.Update(_session, frame, CornerRadius(frame));
    }

    private double CornerRadius(Rect frame) => _mode == RatingPopupMode.Selector
        ? Math.Min(frame.Width(), frame.Height()) / (2 * Density)
        : DesignTokens.RatingPopupCornerRadius;

    private Rect BuildPanel(RatingPopupMode mode, Entry[]? entries = null)
    {
        _mode = mode;
        _entries = entries ?? ReadEntries(mode);
        var panel = _panel!;
        bool selector = mode == RatingPopupMode.Selector;
        var size = panel.Bind(_entries, selector, !selector || _horizontal, _bounds.Width, _bounds.Height);
        var entry = _anchor!;
        var frame = RatingControlGeometry.PlacePopup(ToDp(entry), _bounds, size.Width, size.Height,
            _direction, DesignTokens.RatingPopupGap);
        int x = Px(frame.X), y = Px(frame.Y);
        // 面板在主窗口内呈现：屏幕定位统一换成窗口坐标（低版本同样入窗）。
        var screen = new Rect(x, y, x + Px(frame.Width), y + Px(frame.Height));
        return _popupLayer.ToWindow(screen);
    }

    private async void Choose(int rating)
    {
        if (!IsOpen || _busy || _panel is not { } panel || _target is not { } target) return;
        panel.PerformHapticFeedback(OperatingSystem.IsAndroidVersionAtLeast(30)
            ? FeedbackConstants.Confirm : FeedbackConstants.ContextClick);
        if (_mode != RatingPopupMode.Sources)
        {
            // RATING-07：非冲突弹窗（纯星级胶囊 / 卡片）提交后立即收起，不让用户等落盘；
            // 后台继续写盘，失败仍由 STATE-03 提示条报告。
            Close();
            await _main.SetRatingAsync(target, rating);
            return;
        }
        _busy = true;
        try { await _main.RatingSources.SetRatingAsync(rating); }
        finally
        {
            if (ReferenceEquals(panel, _panel))
            {
                _busy = false;
                panel.ShowCompactState(null, null, null);
                Refresh();
            }
        }
    }

    internal bool BeginExternal(View source, MotionEvent e)
    {
        if (!IsOpen || !ReferenceEquals(_entry, source) || _target?.HasRatingConflict != false) return false;
        _gestureOwner = source;
        _downTime = e.DownTime;
        if (_kind == RatingPopupAnchor.Control && _mode == RatingPopupMode.Selector && _panel is { } panel)
        {
            _compactGesture = new CompactRatingDragGesture(Math.Max(0, _target!.Rating), _horizontal,
                ToDp(_anchor!), ToDp(ScreenRect(panel)), ToDp(panel.RowOnScreen()), _direction);
            _compactGesture.Begin(e.RawX / Density, e.RawY / Density);
            ApplyCompactState(panel, _compactGesture);
            return true;
        }
        _gesture = new RatingGesture(Math.Max(0, _target.Rating), deferPreview: true);
        _gesture.Begin(e.RawX / Density, e.RawY / Density, 0);
        return true;
    }

    internal void MoveExternal(View source, MotionEvent e)
    {
        if (!OwnsGesture(source, e) || _panel is not { } panel) return;
        if (_compactGesture is { IsActive: true } compact)
        {
            bool changed = compact.Move(e.RawX / Density, e.RawY / Density);
            ApplyCompactState(panel, compact);
            if (changed && compact.Preview.HasValue) SelectionHaptic(source);
            return;
        }
        if (_gesture is not { } gesture) return;
        var (x, y, value, inBand) = Project(e, panel);
        if (!gesture.Move(x, y, value, inBand)) return;
        panel.ShowPreview(gesture.Preview);
        if (gesture.Preview.HasValue) SelectionHaptic(source);
    }

    internal void EndExternal(View source, MotionEvent e)
    {
        if (!OwnsGesture(source, e) || _panel is not { } panel) return;
        if (_compactGesture is { } compact)
        {
            _compactGesture = null;
            var compactResult = compact.End(e.RawX / Density, e.RawY / Density);
            if (compactResult.Outcome == RatingGestureOutcome.Commit)
            {
                panel.ShowCompactState(compactResult.Value, null, null);
                Choose(compactResult.Value);
            }
            else
            {
                panel.ShowCompactState(null, null, null);
                if (compactResult.Outcome == RatingGestureOutcome.None) Close();
            }
            _gestureOwner = null;
            return;
        }
        if (_gesture is not { } gesture) return;
        var (x, y, value, inBand) = Project(e, panel);
        var result = gesture.End(x, y, value, inBand);
        ClearGesture();
        if (result.Outcome == RatingGestureOutcome.Commit)
        {
            panel.ShowPreview(result.Value);
            Choose(result.Value);
        }
        else if (result.Outcome == RatingGestureOutcome.None && !inBand) Close();
    }

    internal void CancelExternal(View source, MotionEvent e)
    {
        if (!OwnsGesture(source, e)) return;
        _compactGesture?.Cancel();
        _compactGesture = null;
        _panel?.ShowCompactState(null, null, null);
        ClearGesture();
        Close();
    }

    private bool OwnsGesture(View source, MotionEvent e) => ReferenceEquals(source, _gestureOwner) && _downTime == e.DownTime;

    private void ClearGesture()
    {
        _gesture = null;
        _compactGesture = null;
        _gestureOwner = null;
        _panel?.ShowPreview(null);
    }

    /// <summary>把紧凑态三区手势的预览、放大槽与 hover 槽同步到弹窗星排。</summary>
    /// <param name="panel">弹窗面板。</param>
    /// <param name="gesture">紧凑态手势。</param>
    private static void ApplyCompactState(RatingPanel panel, CompactRatingDragGesture gesture)
        => panel.ShowCompactState(gesture.Preview, gesture.ScaledSlot, gesture.DropHoverSlot);

    /// <summary>预览跨档的选择触感（Android 34+ 分段刻度，低版本时钟刻度）。</summary>
    /// <param name="source">发起入口。</param>
    private static void SelectionHaptic(View source)
        => source.PerformHapticFeedback(OperatingSystem.IsAndroidVersionAtLeast(34)
            ? FeedbackConstants.SegmentTick : FeedbackConstants.ClockTick);

    private (double X, double Y, int Value, bool InBand) Project(MotionEvent e, RatingPanel panel)
    {
        double x = e.RawX / Density, y = e.RawY / Density;
        var row = ToDp(panel.RowOnScreen());
        var frame = ToDp(ScreenRect(panel));
        bool inBand = row.Width > 0 && row.Height > 0
            && RatingControlGeometry.ExternalInBand(x, y, ToDp(_anchor!), frame, row, _horizontal);
        int value = RatingControlGeometry.ValueAt(_horizontal ? x - row.X : y - row.Y,
            _horizontal ? row.Width : row.Height, _horizontal, panel.Pitch);
        return (x, y, value, inBand);
    }

    private static bool SameRect(Rect? left, Rect? right)
    {
        using var rect = left;
        return rect != null && right != null && rect.Left == right.Left && rect.Top == right.Top
            && rect.Right == right.Right && rect.Bottom == right.Bottom;
    }

    internal static Rect? VisibleScreenRect(View view)
    {
        using var visible = new Rect();
        if (!view.IsAttachedToWindow || !view.IsShown || !view.GetGlobalVisibleRect(visible)) return null;
        return ScreenRect(view);
    }

    private static Rect ScreenRect(View view)
    {
        var pos = new int[2];
        view.GetLocationOnScreen(pos);
        return new Rect(pos[0], pos[1], pos[0] + view.Width, pos[1] + view.Height);
    }

    private LayoutRect EdgeBounds()
    {
        var root = ToDp(ScreenRect(_host));
        var safe = _safeInsets();
        double edge = DesignTokens.PopupRatingSourcesEdgeMargin;
        return new LayoutRect(root.X + safe.Left + edge, root.Y + safe.Top + edge,
            Math.Max(1, root.Width - safe.Left - safe.Right - 2 * edge),
            Math.Max(1, root.Height - safe.Top - safe.Bottom - 2 * edge));
    }

    private int Px(double dp) => (int)Math.Round(dp * Density);
    private LayoutRect ToDp(Rect rect) => new(rect.Left / Density, rect.Top / Density,
        rect.Width() / Density, rect.Height() / Density);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.RatingSources.PropertyChanged -= OnSourcesChanged;
        Close();
        _host.RemoveCallbacks(_poll);
        _poll.Dispose();
    }

    private static MaterialShapeDrawable Surface(Context context, double radius, uint color)
        => new(new ShapeAppearanceModel.Builder().SetAllCornerSizes(AndroidUiTheme.Dp(context, radius)).Build())
        { FillColor = ColorStateList.ValueOf(AndroidUiTheme.FromToken(color)) };

    private sealed record Entry(string? Name, string? Time, string? Kind, string? Status, int? Rating);

    private sealed class RatingPanel : ScrollView
    {
        private readonly FrameLayout _content;
        private readonly View _dropHover;
        private readonly ColorStateList _stateLayer;
        private readonly List<MaterialButton[]> _rows = [];
        private readonly List<LayoutRect> _stars = [];
        private readonly Action<int> _choose;
        private readonly Action _close;
        private Entry[] _entries = [];
        private bool _horizontal;
        private int? _dropHoverSlot;
        private int? _scaledSlot;
        internal double Pitch { get; private set; }
        private double Density => AndroidUiTheme.Density(Context!);

        internal RatingPanel(Context context, Action<int> choose, Action close) : base(context)
        {
            _choose = choose;
            _close = close;
            _content = new FrameLayout(context);
            // 星级数值是产品坐标，RTL 环境不翻转 0→5。
            _content.LayoutDirection = global::Android.Views.LayoutDirection.Ltr;
            _stateLayer = AndroidX.Core.Content.ContextCompat.GetColorStateList(context,
                Resource.Color.pv_glass_state_layer) ?? ColorStateList.ValueOf(Color.Transparent);
            // RATING-07：中区 hover 底衬由本层显式绘制，不依赖触摸模式下的系统 Hovered 状态。
            var hoverFill = new ShapeDrawable(new OvalShape());
            hoverFill.Paint!.Color = new Color(_stateLayer.GetColorForState(
                [global::Android.Resource.Attribute.StateHovered], AndroidUiTheme.TextPrimary));
            _dropHover = new View(context)
            {
                Background = hoverFill,
                Clickable = false,
                Focusable = false,
                ImportantForAccessibility = ImportantForAccessibility.No,
                Visibility = ViewStates.Gone,
            };
            AddView(_content);
            ClipToOutline = true;
            ViewCompat.SetAccessibilityPaneTitle(this, "星级");
        }

        internal (double Width, double Height) Bind(Entry[] entries, bool selector, bool horizontal, double maxWidth, double maxHeight)
        {
            _content.RemoveAllViews();
            _content.AddView(_dropHover, new FrameLayout.LayoutParams(0, 0)
            {
                Gravity = GravityFlags.Left | GravityFlags.Top,
            });
            _dropHover.Visibility = ViewStates.Gone;
            _dropHoverSlot = null;
            _scaledSlot = null;
            _rows.Clear();
            _stars.Clear();
            _entries = entries;
            _horizontal = horizontal;
            double available = horizontal ? maxWidth : maxHeight;
            double pitch = Math.Floor((available - RatingPopupLayout.MainAxisInsets) / StarCount);
            if (selector)
            {
                double cross = (horizontal ? maxHeight : maxWidth) - RatingPopupLayout.MainAxisInsets;
                pitch = Math.Min(pitch, cross);
            }
            Pitch = Math.Clamp(pitch, 1, TouchTarget);
            double pad = RatingPopupLayout.Padding;
            if (selector)
            {
                var layout = RatingPopupLayout.Selector(Pitch, horizontal);
                AddRow(layout.Stars with { X = pad + layout.Stars.X, Y = pad + layout.Stars.Y }, entries[0].Rating);
                return SetSize(layout.Width + 2 * pad, layout.Height + 2 * pad);
            }

            var nameProbe = Label("", true);
            var metaProbe = Label("", false);
            double lineHeight = Math.Max(RatingPopupLayout.InfoHeight,
                Math.Max(nameProbe.LineHeight, metaProbe.LineHeight) / Density);
            double extraLine = lineHeight - RatingPopupLayout.InfoHeight;
            double y = pad;
            double Measure(TextView probe, string? text) => text == null ? 0 : Math.Ceiling(probe.Paint!.MeasureText(text) / Density);
            for (int i = 0; i < entries.Length; i++)
            {
                var item = entries[i];
                double name = Measure(nameProbe, item.Name), time = Measure(metaProbe, item.Time);
                double badgePad = DesignTokens.PopupRatingSourcesBadgePaddingX;
                var layout = item.Kind is { } kind
                    ? RatingPopupLayout.Source(name, Math.Min(Pitch * StarCount - RatingPopupLayout.LabelGap,
                        Measure(metaProbe, kind) + 2 * badgePad), Measure(metaProbe, item.Status), Pitch)
                    : RatingPopupLayout.SingleRow(name, time, Pitch);
                double second = layout.TwoLines ? extraLine : 0;
                AddLabel(item.Name, layout.Name, y, true, 0);
                AddLabel(item.Time, layout.Time, y, false, second, end: true);
                AddLabel(item.Kind, layout.Badge, y, false, second, badge: true);
                AddLabel(item.Status, layout.Status, y, false, second, end: true);
                var stars = layout.Stars;
                AddRow(stars with { X = pad + stars.X, Y = y + stars.Y + extraLine + second }, item.Rating);
                y += layout.Height + extraLine + second;
                if (i == entries.Length - 1) continue;
                var separator = new View(Context!);
                separator.SetBackgroundColor(AndroidUiTheme.FromToken(DesignTokens.ColorMenuSeparator));
                Add(separator, new LayoutRect(pad, y + (RatingPopupLayout.EntrySpacing - RatingPopupLayout.DividerHeight) / 2,
                    layout.Width, RatingPopupLayout.DividerHeight));
                y += RatingPopupLayout.EntrySpacing;
            }
            nameProbe.Dispose();
            metaProbe.Dispose();
            return SetSize(RatingPopupLayout.EntryWidth(Pitch) + 2 * pad, y + pad);

            (double Width, double Height) SetSize(double width, double height)
            {
                _content.LayoutParameters = new FrameLayout.LayoutParams(Px(width), Px(height));
                return (width, Math.Min(maxHeight, height));
            }

            void AddLabel(string? text, LayoutRect rect, double top, bool primary, double shift, bool end = false, bool badge = false)
            {
                if (text == null || rect.Width <= 0) return;
                var label = Label(text, primary);
                if (end) label.Gravity = GravityFlags.Right | GravityFlags.CenterVertical;
                if (badge)
                {
                    label.Background = Surface(Context!, DesignTokens.PopupRatingSourcesBadgeCornerRadius, DesignTokens.ColorMenuItemPressed);
                    label.SetPadding(Px(DesignTokens.PopupRatingSourcesBadgePaddingX), 0, Px(DesignTokens.PopupRatingSourcesBadgePaddingX), 0);
                }
                Add(label, rect with { X = rect.X + pad, Y = rect.Y + top + shift, Height = lineHeight });
            }
        }

        private void AddRow(LayoutRect row, int? rating)
        {
            _stars.Add(row);
            var buttons = new MaterialButton[StarCount];
            for (int j = 0; j < StarCount; j++)
            {
                int value = _horizontal ? j : StarCount - 1 - j;
                var button = new MaterialButton(Context!, null, Resource.Attribute.pvGlassButtonStyle)
                {
                    ContentDescription = value == 0 ? "清除星级" : $"{value} 星",
                    BackgroundTintList = ColorStateList.ValueOf(Color.Transparent),
                    CornerRadius = Px(Pitch / 2),
                };
                button.Click += (_, _) => _choose(value);
                buttons[value] = button;
                var slot = RatingControlGeometry.SlotRect(value, Pitch * StarCount, Pitch, _horizontal, Pitch);
                Add(button, slot with { X = slot.X + row.X, Y = slot.Y + row.Y });
            }
            int index = _rows.Count;
            _rows.Add(buttons);
            RenderRow(index, rating);
        }

        private TextView Label(string text, bool primary)
        {
            var label = new TextView(Context!) { Text = text, Gravity = GravityFlags.CenterVertical | GravityFlags.Left };
            label.SetSingleLine(true);
            label.SetIncludeFontPadding(false);
            label.Ellipsize = primary ? TextUtils.TruncateAt.Middle : TextUtils.TruncateAt.End;
            label.SetTextSize(ComplexUnitType.Sp, (float)(primary ? DesignTokens.PopupRatingSourcesNameFontSize : DesignTokens.PopupRatingSourcesMetaFontSize));
            label.SetTextColor(primary ? AndroidUiTheme.TextPrimary : AndroidUiTheme.TextSecondary);
            return label;
        }

        private void Add(View view, LayoutRect rect) => _content.AddView(view,
            new FrameLayout.LayoutParams(Math.Max(0, Px(rect.MaxX) - Px(rect.X)), Math.Max(0, Px(rect.MaxY) - Px(rect.Y)))
            { LeftMargin = Px(rect.X), TopMargin = Px(rect.Y), Gravity = GravityFlags.Left | GravityFlags.Top });

        private void RenderRow(int row, int? rating)
        {
            for (int value = 0; value < StarCount; value++)
            {
                var button = _rows[row][value];
                bool active = value > 0 && value <= rating;
                string id = value == 0 ? IconCatalog.RatingClear : active ? IconCatalog.RatingStarFilled : IconCatalog.RatingStar;
                double size = value == 0 ? DesignTokens.RatingPopupClearGlyphFontSize : DesignTokens.RatingPopupGlyphFontSize;
                var color = value == 0 ? AndroidUiTheme.StarClear : active ? AndroidUiTheme.StarActive : AndroidUiTheme.StarInactive;
                var icon = new AndroidGlyphDrawable(Context!, AndroidIcons.Glyph(id), AndroidIcons.Filled(id), null,
                    size, clipTo: button);
                // 图标槽只决定占位与绘制中心：字号的精确像素不因槽位收窄而缩小，字形超出槽位时由按钮矩形裁切。
                button.IconSize = Math.Min(icon.IntrinsicWidth, Math.Max(1, Px(Pitch)));
                button.Icon = icon;
                button.IconTint = ColorStateList.ValueOf(color);
                button.Selected = value == _entries[row].Rating;
            }
        }

        internal void ShowPreview(int? value)
        {
            if (_rows.Count != 0) RenderRow(0, value ?? _entries[0].Rating);
        }

        /// <summary>拖放区 hover 命中的星格（验收读取；仅真实绘制出圆形底衬时有值）。</summary>
        internal int? DropHoveredSlotForChecks
            => _dropHover.Visibility == ViewStates.Visible ? _dropHoverSlot : null;

        /// <summary>当前按 <c>motion.rating.press.scale</c> 放大的星格（验收读取）。</summary>
        internal int? ScaledSlotForChecks => _scaledSlot;

        /// <summary>当前放大星格的实际缩放（验收读取；无放大时为 1）。</summary>
        internal float ScaledScaleForChecks
            => _scaledSlot is { } value && _rows.Count != 0 ? _rows[0][value].ScaleX : 1f;

        /// <summary>
        /// 紧凑态三区手势的浮窗表现：预览值、放大槽与拖放区圆形 hover。
        /// 进入滑动区、取消区或抬手时 hover 与放大都立即清除。
        /// </summary>
        /// <param name="preview">预览星级；null 清空。</param>
        /// <param name="scaledSlot">要放大的星格；null 恢复全部。</param>
        /// <param name="hoverSlot">拖放区命中的星格；null 清除。</param>
        internal void ShowCompactState(int? preview, int? scaledSlot, int? hoverSlot)
        {
            ShowPreview(preview);
            ScaleSlot(scaledSlot);
            ShowDropHover(hoverSlot);
        }

        /// <summary>预览对应星格按令牌放大；系统动画缩放为 0 或减少动态效果时不缩放。</summary>
        /// <param name="slot">星格 0–5；null 恢复。</param>
        private void ScaleSlot(int? slot)
        {
            int? clamped = slot is >= 0 and < StarCount ? slot : null;
            if (_scaledSlot == clamped)
            {
                if (clamped is not null && ReduceMotion())
                {
                    _scaledSlot = null;
                    ResetScaleButtons();
                }
                return;
            }
            ResetScaleButtons();
            _scaledSlot = null;
            if (clamped is not { } value || _rows.Count == 0 || ReduceMotion()) return;
            _scaledSlot = value;
            var button = _rows[0][value];
            float scale = (float)DesignTokens.MotionRatingPressScale;
            button.Animate()!.Cancel();
            button.Animate()!.ScaleX(scale).ScaleY(scale).SetDuration(180).Start();
        }

        /// <summary>恢复所有星格的 1.0×（减少动态效果时直接写入）。</summary>
        private void ResetScaleButtons()
        {
            if (_rows.Count == 0) return;
            foreach (var button in _rows[0])
            {
                button.Animate()!.Cancel();
                if (ReduceMotion())
                {
                    button.ScaleX = 1f;
                    button.ScaleY = 1f;
                }
                else
                {
                    button.Animate()!.ScaleX(1f).ScaleY(1f).SetDuration(120).Start();
                }
            }
        }

        /// <summary>把圆形 hover 底衬落到命中星格的内切正圆；离开中区或抬手立即隐藏。</summary>
        /// <param name="slot">星格 0–5；null 清除。</param>
        private void ShowDropHover(int? slot)
        {
            _dropHoverSlot = slot is >= 0 and < StarCount ? slot : null;
            if (_dropHoverSlot is not { } value || _rows.Count == 0 || _stars.Count == 0)
            {
                _dropHover.Visibility = ViewStates.Gone;
                return;
            }
            var row = _stars[0];
            var local = RatingControlGeometry.SlotRect(value, Pitch * StarCount, Pitch, _horizontal, Pitch);
            int side = Math.Max(1, Px(Pitch));
            int centerX = Px(row.X + local.X + local.Width / 2);
            int centerY = Px(row.Y + local.Y + local.Height / 2);
            var parameters = _dropHover.LayoutParameters as FrameLayout.LayoutParams
                ?? new FrameLayout.LayoutParams(0, 0) { Gravity = GravityFlags.Left | GravityFlags.Top };
            parameters.Width = side;
            parameters.Height = side;
            parameters.LeftMargin = centerX - side / 2;
            parameters.TopMargin = centerY - side / 2;
            parameters.Gravity = GravityFlags.Left | GravityFlags.Top;
            _dropHover.LayoutParameters = parameters;
            _dropHover.Visibility = ViewStates.Visible;
        }

        /// <summary>系统动画时长缩放为 0 视为减少动态效果。</summary>
        private bool ReduceMotion()
        {
            try
            {
                return Settings.Global.GetFloat(Context!.ContentResolver!,
                    Settings.Global.AnimatorDurationScale, 1f) == 0f;
            }
            catch
            {
                return false;
            }
        }

        internal Rect SlotRect(int entry, int value) => entry >= 0 && entry < _rows.Count && value is >= 0 and < StarCount
            ? new Rect(_rows[entry][value].Left, _rows[entry][value].Top - ScrollY,
                _rows[entry][value].Right, _rows[entry][value].Bottom - ScrollY) : new Rect();
        internal Rect RowOnScreen()
        {
            var rect = ScreenRect(_content);
            var row = _stars[0];
            return new Rect(rect.Left + Px(row.X), rect.Top + Px(row.Y), rect.Left + Px(row.MaxX), rect.Top + Px(row.MaxY));
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e?.KeyCode != Keycode.Escape) return base.DispatchKeyEvent(e);
            if (e.Action == KeyEventActions.Up) _close();
            return true;
        }

        private int Px(double dp) => (int)Math.Round(dp * Density);
    }
}
