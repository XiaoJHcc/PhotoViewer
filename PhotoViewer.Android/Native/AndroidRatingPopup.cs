using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
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

internal enum RatingPopupMode { Single, Sources }

/// <summary>RATING-04/07：同一个模态 PopupWindow 承载普通评分和冲突来源。布局取共享几何，控件与反馈由 Android 承载。</summary>
internal sealed class AndroidRatingPopup : IDisposable
{
    private const int StarCount = RatingControlGeometry.SlotCount;
    private const double TouchTarget = 48; // Android 推荐的触摸目标；不改变已冻结的主屏胶囊。
    private readonly View _host;
    private readonly MainViewModel _main;
    private readonly Func<AndroidInsets> _safeInsets;
    private readonly Java.Lang.Runnable _poll;
    private PopupWindow? _window;
    private RatingPanel? _panel;
    private View? _entry;
    private Func<bool>? _entryValid;
    private Rect? _anchor;
    private Rect? _root;
    private LayoutRect _bounds;
    private RatingPopupDirection _direction;
    private RatingPopupAnchor _kind;
    private bool _horizontal;
    private bool _restoreFocus;
    private bool _disposed;
    private ImageFile? _target;
    private Entry[] _entries = [];
    private RatingGesture? _gesture;
    private View? _gestureOwner;
    private long _downTime;
    private bool _busy;
    private double Density => AndroidUiTheme.Density(_host.Context!);

    internal AndroidRatingPopup(View host, MainViewModel main, Func<AndroidInsets>? safeInsets = null)
    {
        _host = host;
        _main = main;
        _safeInsets = safeInsets ?? (() => default);
        _poll = new Java.Lang.Runnable(Poll);
        _main.RatingSources.PropertyChanged += OnSourcesChanged;
    }

    internal bool IsOpen => _window?.IsShowing == true;
    internal string DirectionForChecks => _direction.ToString().ToLowerInvariant();
    internal Rect AnchorRectForChecks => _anchor == null ? new Rect() : new Rect(_anchor);
    internal int WidthPxForChecks => _window?.Width ?? 0;
    internal int HeightPxForChecks => _window?.Height ?? 0;
    internal View? ContentViewForChecks => _panel;
    internal double PitchDpForChecks => _panel?.Pitch ?? 0;
    internal RatingPopupMode ModeForChecks => _target?.HasRatingConflict == true ? RatingPopupMode.Sources : RatingPopupMode.Single;
    internal RatingPopupAnchor AnchorKindForChecks => _kind;
    internal Rect SlotRectPx(int entryIndex, int rating) => _panel?.SlotRect(entryIndex, rating) ?? new Rect();
    internal int? PreviewForChecks => _gesture?.Preview;

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
        _restoreFocus = entry.IsFocused;
        _main.ShowRatingSources(target);
        var panel = new RatingPanel(_host.Context!, Choose, Close);
        _panel = panel;
        var window = new PopupWindow(panel, 1, 1, true) { OutsideTouchable = true };
        // 非空原生形状同时提供窗口外点击与阴影轮廓；只保留设计令牌的边界和底色。
        window.SetBackgroundDrawable(Surface(_host.Context!, DesignTokens.RatingPopupCornerRadius, DesignTokens.ColorPopupBackground));
        window.Elevation = AndroidUiTheme.Dp(_host.Context!, 6); // Material 3 elevation level 3
        window.InputMethodMode = InputMethod.NotNeeded;
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) window.IsLaidOutInScreen = true;
        _window = window;
        window.DismissEvent += (_, _) => OnDismiss(window);
        var frame = BuildPanel();
        window.Width = frame.Width();
        window.Height = frame.Height();
        window.ShowAtLocation(_host, GravityFlags.Top | GravityFlags.Left, frame.Left, frame.Top);
        panel.FocusFirst();
        _host.PostOnAnimation(_poll);
        return window.IsShowing;
    }

    internal void Close() => _window?.Dismiss();

    private void OnDismiss(PopupWindow window)
    {
        if (!ReferenceEquals(window, _window)) return;
        var entry = _entry;
        bool restore = _restoreFocus && _entryValid?.Invoke() == true;
        _host.RemoveCallbacks(_poll);
        _window = null;
        _panel = null;
        _entry = _gestureOwner = null;
        _entryValid = null;
        _target = null;
        _gesture = null;
        _entries = [];
        _busy = false;
        if (_main.RatingSources.IsOpen) _main.RatingSources.Close();
        if (restore && entry != null && VisibleScreenRect(entry) != null) entry.RequestFocus();
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
        var window = _window;
        if (UiDispatcher.CheckAccess()) window?.Dismiss();
        else _host.Post(() => { if (ReferenceEquals(_window, window)) window?.Dismiss(); });
    }

    private Entry[] ReadEntries()
    {
        var file = _target!;
        if (file.HasRatingConflict && _main.RatingSources.Items.Count > 0)
            return _main.RatingSources.Items.Select(item => new Entry(item.Name, null, item.Kind, item.Status,
                item.State.State == XmpRatingState.Rated ? item.State.Value : null)).ToArray();
        return [new(file.DisplayName, ShotTimeText.FormatShortShotTime(file.PhotoDate), null, null,
            file.Rating >= 0 ? file.Rating : null)];
    }

    private void Refresh()
    {
        if (_target == null || _window == null || _gesture != null) return;
        var entries = ReadEntries();
        if (_entries.SequenceEqual(entries)) return;
        var frame = BuildPanel(entries);
        _window.Update(frame.Left, frame.Top, frame.Width(), frame.Height());
    }

    private Rect BuildPanel(Entry[]? entries = null)
    {
        _entries = entries ?? ReadEntries();
        var panel = _panel!;
        bool horizontal = _horizontal || _entries[0].Kind != null;
        var size = panel.Bind(_entries, horizontal, _bounds.Width, _bounds.Height);
        var entry = _anchor!;
        var frame = RatingControlGeometry.PlacePopup(ToDp(entry), _bounds, size.Width, size.Height,
            _direction, DesignTokens.RatingPopupGap);
        int x = Px(frame.X), y = Px(frame.Y);
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var screen = new int[2];
            var window = new int[2];
            _host.GetLocationOnScreen(screen);
            _host.GetLocationInWindow(window);
            x -= screen[0] - window[0];
            y -= screen[1] - window[1];
        }
        return new Rect(x, y, x + Px(frame.Width), y + Px(frame.Height));
    }

    private async void Choose(int rating)
    {
        if (!IsOpen || _busy || _panel is not { } panel) return;
        _busy = true;
        panel.PerformHapticFeedback(OperatingSystem.IsAndroidVersionAtLeast(30)
            ? FeedbackConstants.Confirm : FeedbackConstants.ContextClick);
        try { await _main.RatingSources.SetRatingAsync(rating); }
        finally
        {
            if (ReferenceEquals(panel, _panel))
            {
                _busy = false;
                panel.ShowPreview(null);
                Refresh();
            }
        }
    }

    internal bool BeginExternal(View source, MotionEvent e)
    {
        if (!IsOpen || !ReferenceEquals(_entry, source) || _target?.HasRatingConflict != false) return false;
        _gestureOwner = source;
        _downTime = e.DownTime;
        _gesture = new RatingGesture(Math.Max(0, _target.Rating), deferPreview: true);
        _gesture.Begin(e.RawX / Density, e.RawY / Density, 0);
        return true;
    }

    internal void MoveExternal(View source, MotionEvent e)
    {
        if (!OwnsGesture(source, e) || _gesture is not { } gesture || _panel is not { } panel) return;
        var (x, y, value, inBand) = Project(e, panel);
        if (!gesture.Move(x, y, value, inBand)) return;
        panel.ShowPreview(gesture.Preview);
        if (gesture.Preview.HasValue)
            source.PerformHapticFeedback(OperatingSystem.IsAndroidVersionAtLeast(34)
                ? FeedbackConstants.SegmentTick : FeedbackConstants.ClockTick);
    }

    internal void EndExternal(View source, MotionEvent e)
    {
        if (!OwnsGesture(source, e) || _gesture is not { } gesture || _panel is not { } panel) return;
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
        ClearGesture();
        Close();
    }

    private bool OwnsGesture(View source, MotionEvent e) => ReferenceEquals(source, _gestureOwner) && _downTime == e.DownTime;

    private void ClearGesture()
    {
        _gesture = null;
        _gestureOwner = null;
        _panel?.ShowPreview(null);
    }

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

    private sealed record Entry(string Name, string? Time, string? Kind, string? Status, int? Rating);

    private sealed class RatingPanel : ScrollView
    {
        private readonly FrameLayout _content;
        private readonly List<MaterialButton[]> _rows = [];
        private readonly List<LayoutRect> _stars = [];
        private readonly Action<int> _choose;
        private readonly Action _close;
        private Entry[] _entries = [];
        private bool _horizontal;
        internal double Pitch { get; private set; }
        private double Density => AndroidUiTheme.Density(Context!);

        internal RatingPanel(Context context, Action<int> choose, Action close) : base(context)
        {
            _choose = choose;
            _close = close;
            _content = new FrameLayout(context);
            // 星级数值是产品坐标，RTL 环境不翻转 0→5。
            _content.LayoutDirection = global::Android.Views.LayoutDirection.Ltr;
            AddView(_content);
            ClipToOutline = true;
            ViewCompat.SetAccessibilityPaneTitle(this, "星级");
        }

        internal (double Width, double Height) Bind(Entry[] entries, bool horizontal, double maxWidth, double maxHeight)
        {
            _content.RemoveAllViews();
            _rows.Clear();
            _stars.Clear();
            _entries = entries;
            _horizontal = horizontal;
            var nameProbe = Label("", true);
            var metaProbe = Label("", false);
            double lineHeight = Math.Max(RatingPopupLayout.InfoHeight,
                Math.Max(nameProbe.LineHeight, metaProbe.LineHeight) / Density);
            double extraLine = lineHeight - RatingPopupLayout.InfoHeight;
            double chrome = horizontal ? RatingPopupLayout.HorizontalChrome
                : RatingPopupLayout.ColumnChrome(entries[0].Time != null) + extraLine * (entries[0].Time != null ? 2 : 1);
            double available = horizontal ? maxWidth : maxHeight;
            Pitch = Math.Max(1, Math.Min(TouchTarget, Math.Floor((available - chrome) / StarCount)));
            double columnWidth = Math.Min(maxWidth, RatingPopupLayout.ColumnWidth);
            if (!horizontal) Pitch = Math.Min(Pitch, columnWidth);
            double pad = RatingPopupLayout.Padding;
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
                    : horizontal ? RatingPopupLayout.SingleRow(name, time, Pitch)
                    : RatingPopupLayout.SingleColumn(name, time, Pitch);
                double second = layout.TwoLines ? extraLine : 0;
                var nameRect = layout.Name;
                var timeRect = layout.Time;
                var stars = layout.Stars;
                if (!horizontal && columnWidth < RatingPopupLayout.ColumnWidth)
                {
                    nameRect = nameRect with { Width = Math.Max(0, columnWidth - 2 * (pad + RatingPopupLayout.EntryPadding)) };
                    timeRect = timeRect with { Width = time <= nameRect.Width ? nameRect.Width : 0 };
                    stars = stars with { X = (columnWidth - 2 * pad - Pitch) / 2 };
                }
                AddLabel(item.Name, nameRect, y, true, 0);
                AddLabel(item.Time, timeRect, y, false, second, end: true);
                AddLabel(item.Kind, layout.Badge, y, false, second, badge: true);
                AddLabel(item.Status, layout.Status, y, false, second, end: true);
                var row = stars with { X = pad + stars.X, Y = y + stars.Y + extraLine + second };
                _stars.Add(row);
                var buttons = new MaterialButton[StarCount];
                for (int j = 0; j < StarCount; j++)
                {
                    int value = horizontal ? j : StarCount - 1 - j;
                    var button = new MaterialButton(Context!, null, Resource.Attribute.pvGlassButtonStyle)
                    {
                        ContentDescription = value == 0 ? "清除星级" : $"{value} 星",
                        BackgroundTintList = ColorStateList.ValueOf(Color.Transparent),
                        CornerRadius = Px(Pitch / 2),
                    };
                    button.Click += (_, _) => _choose(value);
                    buttons[value] = button;
                    var slot = RatingControlGeometry.SlotRect(value, Pitch * StarCount, Pitch, horizontal, Pitch);
                    Add(button, slot with { X = slot.X + row.X, Y = slot.Y + row.Y });
                }
                _rows.Add(buttons);
                RenderRow(i, item.Rating);
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
            double width = horizontal ? RatingPopupLayout.EntryWidth(Pitch) + 2 * pad : columnWidth;
            double height = y + pad;
            _content.LayoutParameters = new FrameLayout.LayoutParams(Px(width), Px(height));
            return (width, Math.Min(maxHeight, height));

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
                var glyph = value == 0 ? DesignTokens.IconClear : active ? DesignTokens.IconStarFilled : DesignTokens.IconStar;
                double size = value == 0 ? DesignTokens.RatingPopupClearGlyphFontSize : DesignTokens.RatingPopupGlyphFontSize;
                var color = value == 0 ? AndroidUiTheme.StarClear : active ? AndroidUiTheme.StarActive : AndroidUiTheme.StarInactive;
                int iconSize = Math.Max(1, Math.Min(Px(Pitch), (int)Math.Ceiling(AndroidUiTheme.Sp(Context!, size))));
                button.IconSize = iconSize;
                button.Icon = new AndroidGlyphDrawable(Context!, AndroidUiTheme.IconGlyph(glyph), active, null, iconSize);
                button.IconTint = ColorStateList.ValueOf(color);
                button.Selected = value == _entries[row].Rating;
            }
        }

        internal void ShowPreview(int? value)
        {
            if (_rows.Count != 0) RenderRow(0, value ?? _entries[0].Rating);
        }

        internal void FocusFirst() => _rows[0][_horizontal ? 0 : StarCount - 1].RequestFocus();
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
