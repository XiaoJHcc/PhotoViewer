using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using PhotoViewer.Core.Design;
using static Android.Views.ViewGroup.LayoutParams;

namespace PhotoViewer.Android.Native;

/// <summary>MENU-03 / FILTER-08：窗口内玻璃菜单共用的行、分组和安全区定位。</summary>
internal sealed class AndroidGlassMenu : LinearLayout
{
    private readonly int _padding;
    private readonly Rect _available;
    private readonly int _menuWidth;

    internal AndroidGlassMenu(AndroidGlassPopupLayer layer, Rect anchor) : base(layer.Context!)
    {
        Orientation = Orientation.Vertical;
        ImportantForAccessibility = ImportantForAccessibility.No;
        _padding = Px(DesignTokens.MenuAndroidPadding);
        SetPadding(_padding, _padding, _padding, _padding);
        var safe = layer.MenuSafeInsets;
        int margin = Px(DesignTokens.MenuAndroidEdgeMargin);
        _available = new Rect(Px(safe.Left) + margin, Px(safe.Top) + margin,
            layer.Width - Px(safe.Right) - margin, layer.Height - Px(safe.Bottom) - margin);
        _menuWidth = Math.Min(Math.Max(Px(DesignTokens.MenuAndroidWidth), anchor.Width()), _available.Width());
    }

    internal void AddGroup(IReadOnlyList<Row> rows, bool preferCompact = false)
    {
        if (rows.Count == 0) return;
        if (ChildCount > 0)
        {
            int inset = Math.Max(0, Px(DesignTokens.MenuAndroidSeparatorInset) - _padding);
            int gap = Px(DesignTokens.MenuAndroidSeparatorPaddingY);
            var line = new View(Context!) { ImportantForAccessibility = ImportantForAccessibility.No };
            line.SetBackgroundColor(AndroidUiTheme.FromToken(DesignTokens.ColorMenuAndroidSeparator));
            AddView(line, new LayoutParams(MatchParent, 1)
            {
                LeftMargin = inset, RightMargin = inset, TopMargin = gap, BottomMargin = gap,
            });
        }

        int columnGap = Px(DesignTokens.MenuAndroidSortGap);
        int cellWidth = (_menuWidth - 2 * _padding - (rows.Count - 1) * columnGap) / rows.Count;
        bool compact = preferCompact;
        if (compact)
            foreach (var row in rows)
                if (!row.FitsCompact(cellWidth)) { compact = false; break; }

        if (!compact)
        {
            foreach (var row in rows)
            {
                row.Build(compact: false);
                AddView(row, new LayoutParams(MatchParent, WrapContent));
            }
            return;
        }

        var options = new LinearLayout(Context!) { Orientation = Orientation.Horizontal };
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            row.Build(compact: true);
            options.AddView(row, new LayoutParams(0, WrapContent, 1)
            {
                MarginStart = index > 0 ? columnGap : 0,
            });
        }
        AddView(options, new LayoutParams(MatchParent, WrapContent));
    }

    internal AndroidGlassPopupSession? Show(AndroidGlassPopupLayer layer, Rect anchor, string title,
        View? entry, Action onClosed, bool anchoredToButton)
    {
        if (_menuWidth <= 0 || _available.Height() <= 0 || ChildCount == 0) return null;
        Measure(MeasureSpec.MakeMeasureSpec(_menuWidth, MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        int height = Math.Min(MeasuredHeight, _available.Height());
        if (height <= 0) return null;
        int gap = anchoredToButton ? Px(DesignTokens.MenuAndroidAnchorGap) : 0;
        int x = anchoredToButton && anchor.CenterX() > _available.CenterX()
            ? anchor.Right - _menuWidth : anchor.Left;
        int y = anchor.Bottom + gap;
        int above = anchor.Top - gap - height;
        if (anchoredToButton && y + height > _available.Bottom && above >= _available.Top) y = above;
        x = Math.Clamp(x, _available.Left, _available.Right - _menuWidth);
        y = Math.Clamp(y, _available.Top, _available.Bottom - height);
        using var frame = new Rect(x, y, x + _menuWidth, y + height);
        View content = this;
        if (height < MeasuredHeight)
        {
            var scroll = new ScrollView(Context!) { DescendantFocusability = DescendantFocusability.AfterDescendants };
            scroll.AddView(this, new FrameLayout.LayoutParams(MatchParent, WrapContent));
            content = scroll;
        }
        return layer.Show(new AndroidGlassPopupRequest(content, frame, DesignTokens.MenuAndroidCornerRadius,
            AndroidUiTheme.FromToken(DesignTokens.ColorPopupBackground),
            AndroidUiTheme.FromToken(DesignTokens.ColorPopupBorder), 1, title, entry, onClosed, GlassPopupAppearance.Menu));
    }

    internal static Rect WindowAnchor(AndroidGlassPopupLayer layer, View view)
    {
        var location = new int[2];
        view.GetLocationOnScreen(location);
        using var screen = new Rect(location[0], location[1], location[0] + view.Width, location[1] + view.Height);
        return layer.ToWindow(screen);
    }

    private int Px(double dp) => AndroidUiTheme.DpPx(this, dp);

    internal sealed class Row : LinearLayout
    {
        private readonly TextView _label;
        private readonly string _glyph;
        private readonly Color _foreground;
        private View? _mark;
        private bool _compact;
        private bool? _checked;
        private float _cornerRadius = -1f;

        internal Row(Context context, string label, string glyph, bool? check, Action activate,
            bool enabled = true) : base(context)
        {
            _glyph = glyph;
            _checked = check;
            _foreground = enabled ? AndroidUiTheme.TextPrimary : AndroidUiTheme.FromToken(DesignTokens.ColorMenuForegroundDisabled);
            _label = new TextView(context) { Text = label, ImportantForAccessibility = ImportantForAccessibility.No };
            _label.SetTextColor(_foreground);
            SetGravity(GravityFlags.CenterVertical);
            Clickable = enabled;
            Focusable = enabled;
            Enabled = enabled;
            ContentDescription = label;
            ImportantForAccessibility = ImportantForAccessibility.Yes;
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) DefaultFocusHighlightEnabled = false;
            ViewCompat.SetAccessibilityDelegate(this, new RowAccessibility(this));
            if (enabled) Click += (_, _) => activate();
        }

        internal bool Marked => _compact ? Selected : _mark?.Visibility == ViewStates.Visible;

        internal bool FitsCompact(int width)
        {
            _label.TextSize = (float)DesignTokens.MenuAndroidSortFontSize;
            return _label.Paint!.MeasureText(_label.Text) + 2 * Px(DesignTokens.MenuAndroidRowPaddingX) <= width;
        }

        internal void Build(bool compact)
        {
            _compact = compact;
            int padX = Px(DesignTokens.MenuAndroidRowPaddingX);
            int padY = Px(DesignTokens.MenuAndroidRowPaddingY);
            SetPadding(padX, padY, padX, padY);
            SetMinimumHeight(Px(compact ? DesignTokens.MenuAndroidSortHeight : DesignTokens.MenuAndroidRowHeight));
            int iconSize = Px(DesignTokens.MenuAndroidIconSize);
            var icon = new AndroidGlyphView(Context!, _glyph, DesignTokens.MenuAndroidIconFontSize, _foreground,
                DesignTokens.MenuAndroidIconSize);
            if (compact)
            {
                Orientation = Orientation.Vertical;
                SetGravity(GravityFlags.Center);
                _label.TextSize = (float)DesignTokens.MenuAndroidSortFontSize;
                _label.Gravity = GravityFlags.Center;
                AddView(icon, new LayoutParams(iconSize, iconSize)
                {
                    BottomMargin = Px(DesignTokens.MenuAndroidSortIconGap),
                });
                AddView(_label, new LayoutParams(MatchParent, WrapContent));
            }
            else
            {
                Orientation = Orientation.Horizontal;
                _label.TextSize = (float)DesignTokens.MenuAndroidRowFontSize;
                _mark = new AndroidGlyphView(Context!, AndroidIcons.Check, DesignTokens.MenuCheckFontSize, _foreground,
                    DesignTokens.MenuCheckSize);
                int markSize = Px(DesignTokens.MenuCheckSize);
                AddView(_mark, new LayoutParams(markSize, markSize) { MarginEnd = Px(DesignTokens.MenuAndroidCheckGap) });
                AddView(icon, new LayoutParams(iconSize, iconSize) { MarginEnd = Px(DesignTokens.MenuIconGap) });
                AddView(_label, new LayoutParams(0, WrapContent, 1));
            }
            ApplyCheck();
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            float maxRadius = Math.Min(w, h) * 0.5f;
            float radius = _compact
                ? Math.Min(maxRadius, AndroidUiTheme.Dp(Context!, DesignTokens.MenuAndroidSortCornerRadius))
                : maxRadius;
            if (_cornerRadius == radius) return;
            _cornerRadius = radius;
            Background = MakeBackground(_compact);
        }

        internal void SetChecked(bool value)
        {
            if (_checked == value) return;
            _checked = value;
            ApplyCheck();
            SendAccessibilityEvent(global::Android.Views.Accessibility.EventTypes.WindowContentChanged);
        }

        private void ApplyCheck()
        {
            if (_compact) Selected = _checked == true;
            else if (_mark != null) _mark.Visibility = _checked == true ? ViewStates.Visible : ViewStates.Invisible;
        }

        private StateListDrawable MakeBackground(bool compact)
        {
            var states = new StateListDrawable();
            void Add(int[] state, uint color)
            {
                var fill = new GradientDrawable();
                fill.SetColor(AndroidUiTheme.FromToken(color));
                fill.SetCornerRadius(_cornerRadius);
                states.AddState(state, fill);
            }
            const int enabled = global::Android.Resource.Attribute.StateEnabled;
            if (compact)
            {
                const int selected = global::Android.Resource.Attribute.StateSelected;
                Add([enabled, selected, global::Android.Resource.Attribute.StateFocused], DesignTokens.ColorMenuItemPressed);
                Add([enabled, selected, global::Android.Resource.Attribute.StateHovered], DesignTokens.ColorMenuItemPressed);
            }
            Add([enabled, global::Android.Resource.Attribute.StatePressed], DesignTokens.ColorMenuItemPressed);
            Add([enabled, global::Android.Resource.Attribute.StateFocused], DesignTokens.ColorMenuItemHover);
            Add([enabled, global::Android.Resource.Attribute.StateHovered], DesignTokens.ColorMenuItemHover);
            if (compact) Add([global::Android.Resource.Attribute.StateSelected], DesignTokens.ColorMenuAndroidSortSelected);
            Add([], 0);
            return states;
        }

        private int Px(double dp) => AndroidUiTheme.DpPx(this, dp);

        private sealed class RowAccessibility(Row row) : AccessibilityDelegateCompat
        {
            public override void OnInitializeAccessibilityNodeInfo(View? host, AccessibilityNodeInfoCompat? info)
            {
                base.OnInitializeAccessibilityNodeInfo(host, info);
                if (info == null || row._checked is not { } check) return;
                info.Checkable = true;
                info.SetChecked(check);
            }
        }
    }
}
