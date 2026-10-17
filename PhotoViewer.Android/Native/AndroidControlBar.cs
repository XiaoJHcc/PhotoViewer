using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Views;
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
    private readonly Paint _starFill;
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
        _textPaint = AndroidUiTheme.TextPaint(context, 10, AndroidUiTheme.TextSecondary);
        _valuePaint = AndroidUiTheme.TextPaint(context, 12, AndroidUiTheme.TextPrimary, bold: true);
        _iconPaint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _iconPaint.TextSize = AndroidUiTheme.Sp(context, 18);
        _iconPaint.TextAlign = Paint.Align.Center;
        _numberPaint = AndroidUiTheme.TextPaint(context, 11, AndroidUiTheme.TextPrimary);
        _starActive = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        _starInactive = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
        _starFill = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        SetWillNotDraw(false);
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
            int rating = _main.CurrentFile?.Rating ?? 0;
            for (int value = 0; value < 6; value++) children.Add(new Child { Rating = value, Active = value > 0 && value <= rating });
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
    internal static bool IsCommandAvailable(string command) => command != "ToggleAnalysisView";

    /// <summary>量文档主轴/交叉轴长度（dp）；行布局主轴为高、列布局主轴为宽。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    /// <returns>文档尺寸。</returns>
    internal (double Main, double Cross) MeasureDocument(bool horizontal, double availableMain)
    {
        if (_dirty) Rebuild();
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
        if (_dirty) Rebuild();
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
        Invalidate();
    }

    /// <summary>绘制分组与子控件。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas? canvas)
    {
        base.OnDraw(canvas);
        if (canvas == null) return;
        if (_dirty) Rebuild();
        float density = (float)AndroidUiTheme.Density(Context!);
        foreach (var group in _groups)
        {
            if (group.Rect.Width <= 0 || group.Rect.Height <= 0) continue;
            if (group.Kind == ControlBarGroupKind.Rating)
            {
                var rect = ToRectF(group.Rect, density);
                AndroidUiTheme.DrawRounded(canvas, rect, 6 * density, AndroidUiTheme.ControlFill);
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
        AndroidUiTheme.DrawRounded(canvas, rect, 6 * density, AndroidUiTheme.ControlFill);
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
        if (child.Rating == 0)
        {
            float arm = 4.5f * density;
            var paint = AndroidUiTheme.Paint(Color.ParseColor("#888888")!, Math.Max(1, density));
            canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, paint);
            canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, paint);
            return;
        }
        using var star = StarPath(cx, cy, 7 * density, 3.2f * density);
        canvas.DrawPath(star, child.Active ? _starFill : _starInactive);
    }

    /// <summary>命令按钮：40×40 圆角底 + 图标字体（评分命令显示数字）。</summary>
    private void DrawCommand(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        AndroidUiTheme.DrawRounded(canvas, rect, 6 * density, Color.ParseColor("#333333")!);
        if (child.Number != null)
        {
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
                        _rate?.Invoke(child.Rating);
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
        internal bool Enabled { get; init; } = true;
        internal LayoutRect Rect { get; set; }
    }
}
