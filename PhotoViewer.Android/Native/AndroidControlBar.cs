using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using AndroidX.Core.View.Accessibility;
using AndroidX.CustomView.Widget;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 控制栏（A3，[TOOLBAR-01]）：EXIF 信息块 / 评分 / 命令三个整体分组，尺寸、按空间整组换行或换列、
/// 行内居中与交叉轴拉伸全部由共享 <see cref="ControlBarLayout"/> 计算；本类只自绘并按命中分发。
/// 显隐与顺序来自共享设置；评分方向随布局方向（横排 0→5、竖排 5→0）。
/// </summary>
internal sealed class AndroidControlBar : View
{
    private readonly MainViewModel _main;
    private readonly Paint _textPaint;
    private readonly Paint _valuePaint;
    private readonly Paint _iconPaint;
    private readonly Paint _numberPaint;
    private readonly Paint _starActive;
    private readonly Paint _starInactive;
    private readonly Paint _starClear;
    private readonly ControlBarExploreByTouchHelper _accessibility;
    private readonly List<Group> _groups = [];
    private readonly List<ControlBarLayout.GroupSpec> _specs = [];
    private List<List<int>> _lines = [];
    private Action<string>? _execute;
    private Action<int>? _rate;
    private Action? _openTools;
    private bool _horizontal = true;
    private double _availableMain;
    private bool _dirty = true;

    /// <summary>分组数量（验收读取）。</summary>
    internal int GroupCount => _groups.Count;
    /// <summary>命令标识集合（验收读取）。</summary>
    internal IReadOnlyList<string> Commands
    {
        get
        {
            var commands = new List<string>();
            foreach (var group in _groups)
                foreach (var child in group.Children)
                    if (!string.IsNullOrEmpty(child.Command)) commands.Add(child.Command);
            return commands;
        }
    }
    /// <summary>EXIF 分组是否存在。</summary>
    internal bool HasExifGroup => _groups.Exists(group => group.Kind == ControlBarGroupKind.Exif);
    /// <summary>评分分组是否存在。</summary>
    internal bool HasRatingGroup => _groups.Exists(group => group.Kind == ControlBarGroupKind.Rating);

    /// <summary>构建控制栏。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidControlBar(Context context, MainViewModel main) : base(context)
    {
        _main = main;
        _textPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarExifLabelFontSize, AndroidUiTheme.TextSecondary);
        _valuePaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarExifValueFontSize, AndroidUiTheme.TextPrimary, bold: true);
        _iconPaint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _iconPaint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.ToolbarCommandGlyphFontSize);
        _iconPaint.TextAlign = Paint.Align.Center;
        _numberPaint = AndroidUiTheme.TextPaint(context, 11, AndroidUiTheme.TextPrimary);
        _starActive = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        _starInactive = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
        _starClear = AndroidUiTheme.Paint(AndroidUiTheme.StarClear);
        SetWillNotDraw(false);
        Focusable = true;
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        _accessibility = new ControlBarExploreByTouchHelper(this);
        ViewCompat.SetAccessibilityDelegate(this, _accessibility);
    }

    /// <summary>命令执行入口与评分入口；EXIF 块点击打开工具页。</summary>
    /// <param name="execute">执行共享命令。</param>
    /// <param name="rate">评分写回。</param>
    /// <param name="openTools">EXIF 块点击。</param>
    internal void Wire(Action<string> execute, Action<int> rate, Action? openTools)
    {
        _execute = execute;
        _rate = rate;
        _openTools = openTools;
    }

    /// <summary>按共享设置重建三组控件（显隐/顺序、当前星级与 EXIF 项）。</summary>
    internal void Rebuild()
    {
        _groups.Clear();
        _specs.Clear();
        _dirty = false;
        var entries = _main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            var children = new List<Child>(entries.Count);
            foreach (var entry in entries) children.Add(new Child { Exif = entry });
            _groups.Add(new Group(ControlBarGroupKind.Exif, children));
            _specs.Add(new ControlBarLayout.GroupSpec(ControlBarGroupKind.Exif, entries.Count));
        }
        if (_main.Settings.ShowRating)
        {
            var children = new List<Child>(6);
            bool hasPhoto = _main.CurrentFile != null;
            int rating = _main.CurrentFile?.Rating ?? 0;
            for (int value = 0; value < 6; value++)
            {
                // TOOLBAR-03：无照片时整组禁用，按钮保留几何可见。
                children.Add(new Child { Rating = value, Active = value > 0 && value <= rating, Enabled = hasPhoto });
            }
            _groups.Add(new Group(ControlBarGroupKind.Rating, children));
            _specs.Add(new ControlBarLayout.GroupSpec(ControlBarGroupKind.Rating, 6));
        }
        var commands = new List<Child>();
        foreach (var hotkey in _main.ControlVM.EnabledControls)
        {
            if (!IsCommandAvailable(hotkey.Command)) continue;
            commands.Add(new Child
            {
                Command = hotkey.Command,
                Icon = hotkey.DisplaySymbol,
                Number = hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal) ? hotkey.Command[9..] : null,
                Label = hotkey.Name,
                Binding = BindingText(hotkey),
                Enabled = hotkey.Command == "Open" || _main.CurrentFile != null,
            });
        }
        if (commands.Count > 0)
        {
            _groups.Add(new Group(ControlBarGroupKind.Command, commands));
            _specs.Add(new ControlBarLayout.GroupSpec(ControlBarGroupKind.Command, commands.Count));
        }
        Invalidate();
    }

    /// <summary>标记设置/文件变化后需要重建（宿主在变化时调用）。</summary>
    internal void MarkDirty() => _dirty = true;

    /// <summary>是否提供该命令入口；A5 之前分析栏没有面板，先不显示该入口。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>是否显示。</returns>
    internal static bool IsCommandAvailable(string command) => true;

    /// <summary>命令的读屏文本：名称 + 已设置的绑定（TOOLBAR-04，未设置不占位）。</summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>读屏文本。</returns>
    private static string BindingText(SettingsViewModel.HotkeyItem hotkey)
    {
        string text = hotkey.Name;
        if (hotkey.PrimaryHotkey?.ToDisplayString() is { Length: > 0 } primary) text += $" {primary}";
        if (hotkey.SecondaryHotkey?.ToDisplayString() is { Length: > 0 } secondary) text += $" / {secondary}";
        return text;
    }

    /// <summary>量文档主轴/交叉轴长度（dp）；行布局主轴为高、列布局主轴为宽。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    /// <returns>文档尺寸。</returns>
    internal (double Main, double Cross) MeasureDocument(bool horizontal, double availableMain)
    {
        if (_dirty || _groups.Count == 0) Rebuild();
        _horizontal = horizontal;
        _availableMain = Math.Max(1, availableMain);
        _lines = ControlBarLayout.PackLines(_specs, horizontal, Math.Max(1, _availableMain - ControlBarLayout.Padding * 2));
        var document = ControlBarLayout.DocumentSize(_specs, horizontal, _lines);
        return horizontal ? (document.Height, document.Width) : (document.Width, document.Height);
    }

    /// <summary>在实际视口内居中每一行/列，并保存每个子控件的 dp 矩形供绘制/命中。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    internal void Arrange(bool horizontal, double availableMain)
    {
        if (_dirty || _groups.Count == 0) Rebuild();
        _horizontal = horizontal;
        _availableMain = Math.Max(1, availableMain);
        _lines = ControlBarLayout.PackLines(_specs, horizontal, Math.Max(1, _availableMain - ControlBarLayout.Padding * 2));
        double cross = ControlBarLayout.Padding;
        foreach (var line in _lines)
        {
            double lineCross = ControlBarLayout.LineCross(_specs, horizontal, line);
            double main = Math.Max(ControlBarLayout.Padding,
                (_availableMain - ControlBarLayout.LineLength(_specs, horizontal, line)) / 2);
            foreach (int index in line)
            {
                if (index < 0 || index >= _groups.Count) continue;
                var spec = _specs[index];
                var metrics = ControlBarLayout.GetMetrics(spec.Kind);
                double groupMain = ControlBarLayout.MainLength(spec, horizontal);
                double groupCross = Math.Max(horizontal ? metrics.Height : metrics.Width, lineCross);
                var groupRect = horizontal
                    ? new LayoutRect(main, cross, groupMain, groupCross)
                    : new LayoutRect(cross, main, groupCross, groupMain);
                _groups[index].Rect = groupRect;
                var offsets = ControlBarLayout.ChildOffsets(spec, horizontal, lineCross);
                for (int child = 0; child < _groups[index].Children.Count && child < offsets.Length; child++)
                {
                    double childMain = horizontal ? metrics.Width : metrics.Height;
                    double childCross = horizontal ? metrics.Height : metrics.Width;
                    var rect = horizontal
                        ? new LayoutRect(main + offsets[child].Main, cross + offsets[child].Cross, childMain, childCross)
                        : new LayoutRect(cross + offsets[child].Cross, main + offsets[child].Main, childCross, childMain);
                    _groups[index].Children[child].Rect = rect;
                }
                main += groupMain + ControlBarLayout.GroupSpacing;
            }
            cross += lineCross + ControlBarLayout.GroupSpacing;
        }
        _accessibility.InvalidateRoot();
        Invalidate();
    }

    /// <summary>绘制分组与子控件。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // 不能只 Rebuild：新分组还没有 Rect，OnDraw 会全部跳过，表现就是“只剩容器”。
        if (_dirty || _groups.Count == 0) Arrange(_horizontal, _availableMain);
        float density = (float)AndroidUiTheme.Density(Context!);
        foreach (var group in _groups)
        {
            if (group.Rect.Width <= 0 || group.Rect.Height <= 0) continue;
            if (group.Kind == ControlBarGroupKind.Rating)
            {
                var rect = ToRectF(group.Rect, density);
                // TOOLBAR-10：评分块与 EXIF 块共用 #222222 底色。
                AndroidUiTheme.DrawRounded(canvas, rect,
                    (float)(DesignTokens.ToolbarCornerRadius * density), AndroidUiTheme.CardBackground);
            }
            foreach (var child in group.Children)
            {
                if (child.Rect.Width <= 0 || child.Rect.Height <= 0) continue;
                switch (group.Kind)
                {
                    case ControlBarGroupKind.Exif:
                        DrawExif(canvas, child, density);
                        break;
                    case ControlBarGroupKind.Rating:
                        DrawRating(canvas, child, density);
                        break;
                    default:
                        DrawCommand(canvas, child, density);
                        break;
                }
            }
        }
    }

    /// <summary>EXIF 小块：50×40，名称 10sp / 值 12sp 居中尾部截断。</summary>
    private void DrawExif(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        AndroidUiTheme.DrawRounded(canvas, rect,
            (float)(DesignTokens.ToolbarCornerRadius * density), AndroidUiTheme.CardBackground);
        var entry = child.Exif;
        if (entry == null) return;
        AndroidUiTheme.DrawCentered(canvas, entry.DisplayName, _textPaint, rect.CenterX(),
            rect.Top + 17 * density, rect.Width() - 4 * density);
        AndroidUiTheme.DrawCentered(canvas, entry.DisplayValue, _valuePaint, rect.CenterX(),
            rect.Top + 34 * density, rect.Width() - 4 * density);
    }

    /// <summary>评分子控件：0 号为清除叉，1–5 号按当前星级点亮。</summary>
    private void DrawRating(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        float cx = rect.CenterX(), cy = rect.CenterY();
        int alpha = child.Enabled ? 255 : 97;
        if (child.Rating == 0)
        {
            float arm = 4.5f * density;
            _starClear.Alpha = alpha;
            canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, _starClear);
            canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, _starClear);
            return;
        }
        _starActive.Alpha = alpha;
        _starInactive.Alpha = alpha;
        using var star = StarPath(cx, cy, 7 * density, 3.2f * density);
        canvas.DrawPath(star, child.Active ? _starActive : _starInactive);
    }

    /// <summary>命令按钮：40×40 圆角底 + 图标字体（评分命令显示数字）。</summary>
    private void DrawCommand(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        float cx = rect.CenterX(), cy = rect.CenterY();
        AndroidUiTheme.DrawRounded(canvas, rect,
            (float)(DesignTokens.ToolbarCornerRadius * density),
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground));
        if (child.Number != null)
        {
            // TOOLBAR-03：评分快捷命令画数字，0 号画叉，不用星形。
            if (child.Number == "0")
            {
                float arm = 4.5f * density;
                _starClear.Alpha = child.Enabled ? 255 : 97;
                canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, _starClear);
                canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, _starClear);
                return;
            }
            _numberPaint.Alpha = child.Enabled ? 255 : 97;
            AndroidUiTheme.DrawCentered(canvas, child.Number, _numberPaint, rect.CenterX(),
                rect.CenterY() + _numberPaint.TextSize * .36f, rect.Width());
            return;
        }
        var typeface = AndroidUiTheme.IconTypeface(Context!);
        if (typeface != null) _iconPaint.SetTypeface(typeface);
        _iconPaint.Color = child.Enabled ? AndroidUiTheme.TextPrimary : Color.ParseColor("#666666")!;
        canvas.DrawText(child.Icon ?? "?", rect.CenterX(), rect.CenterY() + _iconPaint.TextSize * .36f, _iconPaint);
    }

    /// <summary>点按命中：EXIF 块开工具、评分写回、命令走共享命令表。</summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked != MotionEventActions.Up) return true;
        double density = AndroidUiTheme.Density(Context!);
        double x = e.GetX() / density, y = e.GetY() / density;
        foreach (var group in _groups)
        {
            foreach (var child in group.Children)
            {
                if (!Contains(child.Rect, x, y)) continue;
                switch (group.Kind)
                {
                    case ControlBarGroupKind.Exif:
                        _openTools?.Invoke();
                        break;
                    case ControlBarGroupKind.Rating:
                        if (child.Enabled) _rate?.Invoke(child.Rating);
                        break;
                    default:
                        if (child.Enabled && child.Command != null) _execute?.Invoke(child.Command);
                        break;
                }
                return true;
            }
        }
        return true;
    }

    /// <summary>命中的虚拟节点 id（按分组与子控件顺序展平）；未命中返回 -1。</summary>
    /// <param name="x">本地横坐标（dp）。</param>
    /// <param name="y">本地纵坐标（dp）。</param>
    /// <returns>虚拟节点 id 或 -1。</returns>
    private int VirtualViewIdAt(double x, double y)
    {
        int id = 0;
        foreach (var group in _groups)
        {
            foreach (var child in group.Children)
            {
                if (child.Rect.Width > 0 && Contains(child.Rect, x, y)) return id;
                id++;
            }
        }
        return -1;
    }

    /// <summary>按展平 id 取子控件。</summary>
    /// <param name="id">虚拟节点 id。</param>
    /// <returns>分组与子控件；越界返回 null。</returns>
    private (Group Group, Child Child)? ChildAt(int id)
    {
        int index = 0;
        foreach (var group in _groups)
        {
            foreach (var child in group.Children)
            {
                if (index == id) return (group, child);
                index++;
            }
        }
        return null;
    }

    /// <summary>虚拟节点的读屏名称（TOOLBAR-11）。</summary>
    private static string AccessibleName(Group group, Child child) => group.Kind switch
    {
        ControlBarGroupKind.Exif => child.Exif == null
            ? ""
            : $"{child.Exif.DisplayName} {child.Exif.DisplayValue}",
        ControlBarGroupKind.Rating => child.Rating == 0 ? "无星级" : $"{child.Rating} 星",
        _ => child.Binding ?? child.Label ?? child.Command ?? "",
    };

    /// <summary>执行虚拟节点动作（与点按命中同一入口）。</summary>
    private bool PerformVirtualAction(Group group, Child child)
    {
        if (!child.Enabled) return false;
        switch (group.Kind)
        {
            case ControlBarGroupKind.Exif:
                _openTools?.Invoke();
                return true;
            case ControlBarGroupKind.Rating:
                _rate?.Invoke(child.Rating);
                return true;
            default:
                if (child.Command == null) return false;
                _execute?.Invoke(child.Command);
                return true;
        }
    }

    private static bool Contains(LayoutRect rect, double x, double y)
        => x >= rect.X && x <= rect.MaxX && y >= rect.Y && y <= rect.MaxY;

    private static RectF ToRectF(LayoutRect rect, float density) => new(
        (float)rect.X * density, (float)rect.Y * density,
        (float)rect.MaxX * density, (float)rect.MaxY * density);

    private static Path StarPath(float cx, float cy, float outer, float inner)
    {
        var path = new Path();
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float radius = i % 2 == 0 ? outer : inner;
            float x = (float)(cx + Math.Cos(angle) * radius);
            float y = (float)(cy + Math.Sin(angle) * radius);
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        path.Close();
        return path;
    }

    /// <summary>
    /// 控制栏虚拟节点（TOOLBAR-11）：按视觉顺序暴露 EXIF 块、评分 0–5 与命令，
    /// 每个节点带名称、禁用态与位置，点按/回车走与触摸相同的执行入口。
    /// </summary>
    private sealed class ControlBarExploreByTouchHelper : ExploreByTouchHelper
    {
        private readonly AndroidControlBar _owner;

        /// <summary>绑定控制栏。</summary>
        /// <param name="owner">控制栏视图。</param>
        public ControlBarExploreByTouchHelper(AndroidControlBar owner) : base(owner) => _owner = owner;

        /// <summary>按本地像素坐标找虚拟节点。</summary>
        protected override int GetVirtualViewAt(float x, float y)
        {
            double density = AndroidUiTheme.Density(_owner.Context!);
            return _owner.VirtualViewIdAt(x / density, y / density);
        }

        /// <summary>按视觉顺序列出可见虚拟节点。</summary>
        protected override void GetVisibleVirtualViews(IList<Java.Lang.Integer>? virtualViewIds)
        {
            if (virtualViewIds == null) return;
            int id = 0;
            foreach (var group in _owner._groups)
            {
                foreach (var child in group.Children)
                {
                    if (child.Rect.Width > 0 && child.Rect.Height > 0)
                        virtualViewIds.Add(Java.Lang.Integer.ValueOf(id));
                    id++;
                }
            }
        }

        /// <summary>填充节点名称、禁用态、选中态与边界。</summary>
        protected override void OnPopulateNodeForVirtualView(int virtualViewId, AccessibilityNodeInfoCompat? node)
        {
            if (node == null) return;
            var pair = _owner.ChildAt(virtualViewId);
            if (pair == null)
            {
                node.ContentDescription = "";
                node.SetBoundsInScreen(new Rect());
                return;
            }

            var (group, child) = pair.Value;
            node.ContentDescription = AccessibleName(group, child);
            node.Enabled = child.Enabled;
            node.Clickable = child.Enabled;
            node.Focusable = true;
            node.VisibleToUser = true;
            if (group.Kind == ControlBarGroupKind.Rating) node.Selected = child.Active;
            if (child.Enabled) node.AddAction((int)global::Android.Views.Accessibility.Action.Click);
            double density = AndroidUiTheme.Density(_owner.Context!);
            // 虚拟节点边界用屏幕坐标，避免已弃用的 parent bounds。
            var location = new int[2];
            _owner.GetLocationOnScreen(location);
            node.SetBoundsInScreen(new Rect(
                location[0] + (int)Math.Round(child.Rect.X * density),
                location[1] + (int)Math.Round(child.Rect.Y * density),
                location[0] + (int)Math.Round(child.Rect.MaxX * density),
                location[1] + (int)Math.Round(child.Rect.MaxY * density)));
        }

        /// <summary>读屏点按：复用控制栏的点按执行路径。</summary>
        protected override bool OnPerformActionForVirtualView(int virtualViewId, int action, Bundle? arguments)
        {
            if (action != (int)global::Android.Views.Accessibility.Action.Click) return false;
            var pair = _owner.ChildAt(virtualViewId);
            return pair != null && _owner.PerformVirtualAction(pair.Value.Group, pair.Value.Child);
        }
    }

    /// <summary>业务分组。</summary>
    private sealed class Group
    {
        /// <summary>分组类型。</summary>
        internal ControlBarGroupKind Kind { get; }
        /// <summary>子控件。</summary>
        internal List<Child> Children { get; }
        /// <summary>分组矩形（dp）。</summary>
        internal LayoutRect Rect { get; set; }

        /// <summary>建立分组。</summary>
        internal Group(ControlBarGroupKind kind, List<Child> children)
        {
            Kind = kind;
            Children = children;
        }
    }

    /// <summary>子控件：EXIF 项 / 评分 / 命令三选一。</summary>
    private sealed class Child
    {
        internal ExifDisplayEntry? Exif { get; init; }
        internal int Rating { get; init; }
        internal bool Active { get; init; }
        internal string? Command { get; init; }
        internal string? Icon { get; init; }
        internal string? Number { get; init; }
        internal string? Label { get; init; }
        internal string? Binding { get; init; }
        internal bool Enabled { get; init; } = true;
        internal LayoutRect Rect { get; set; }
    }
}
