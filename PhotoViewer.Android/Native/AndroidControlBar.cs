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
/// 控制栏（TOOLBAR-01/02）：EXIF 摘要 → 评分 → 命令三组的尺寸、整组换行/换列与行内居中全部取共享
/// <see cref="ToolbarFlowLayout"/>，子控件矩形取 <see cref="ExifBlockGeometry"/>、
/// <see cref="RatingControlGeometry"/> 与 <see cref="ToolbarFlowLayout.CommandOffset"/>；本类只自绘并按命中分发。
/// 移动端左右布局的 EXIF 取无衬底纵排（透明 44 宽容器、整项裁剪），行布局保留衬底块。
/// </summary>
internal sealed class AndroidControlBar : View
{
    private readonly MainViewModel _main;
    private readonly Paint _textPaint;
    private readonly Paint _valuePaint;
    private readonly Paint _iconPaint;
    private readonly Paint _solidIconPaint;
    private readonly Paint _numberPaint;
    private readonly Paint _starActive;
    private readonly Paint _starInactive;
    private readonly Paint _starClear;
    private readonly Paint _starConflict;
    private readonly Paint _compactConflict;
    private bool _ratingCompact;
    private readonly ControlBarExploreByTouchHelper _accessibility;
    private readonly List<Group> _groups = [];
    private readonly List<ToolbarGroupSize> _specs = [];
    private Action<string>? _execute;
    private Action<int>? _rate;
    private Action? _openTools;
    private Action? _openRatingSources;
    private bool _horizontal = true;
    private double _availableMain;
    private bool _joinedToAnalysis;
    private bool _dirty = true;
    private bool _builtHorizontal = true;

    /// <summary>分组数量（验收读取）。</summary>
    internal int GroupCount => _groups.Count;
    /// <summary>控制栏是否为横排（底部横排向上、右侧竖排向左，RATING-04 方向判定用）。</summary>
    internal bool IsHorizontal => _horizontal;
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
    /// <summary>指定命令当前实际绘制的图标字形（验收读取）。</summary>
    internal string? CommandIconForChecks(string command)
    {
        foreach (var group in _groups)
            foreach (var child in group.Children)
                if (child.Command == command) return child.Icon;
        return null;
    }
    /// <summary>分组类型顺序（验收读取，与 <see cref="GroupFramesForChecks"/> 同序）。</summary>
    internal IReadOnlyList<ControlBarGroupKind> GroupKindsForChecks
        => _groups.ConvertAll(group => group.Kind);

    /// <summary>EXIF 分组是否存在。</summary>
    internal bool HasExifGroup => _groups.Exists(group => group.Kind == ControlBarGroupKind.Exif);
    /// <summary>评分分组是否存在。</summary>
    internal bool HasRatingGroup => _groups.Exists(group => group.Kind == ControlBarGroupKind.Rating);
    /// <summary>当前照片是否处于组级星级冲突（0 号显示警示三角，验收用）。</summary>
    internal bool RatingConflictForChecks => _main.CurrentFile?.HasRatingConflict == true;
    /// <summary>分组矩形（dp，按组顺序；验收读取整组换行与居中）。</summary>
    internal IReadOnlyList<LayoutRect> GroupFramesForChecks()
    {
        var frames = new List<LayoutRect>(_groups.Count);
        foreach (var group in _groups) frames.Add(group.Rect);
        return frames;
    }
    /// <summary>EXIF 分组是否取无衬底变体（左右布局）。</summary>
    internal bool ExifBare => !_horizontal;
    /// <summary>当前评分字形字号（紧凑 18 / 展开 16；验收读取）。</summary>
    internal double RatingGlyphSizeForChecks
        => _ratingCompact ? RatingControlGeometry.CompactGlyph : DesignTokens.ToolbarRatingGlyphFontSize;

    /// <summary>控制栏文档尺寸（dp，主轴、交叉轴；验收读取）。</summary>
    internal (double Main, double Cross) DocumentSizeForChecks
        => _horizontal ? (_documentWidth, _documentHeight) : (_documentHeight, _documentWidth);

    /// <summary>
    /// 评分分组某一槽位的屏幕矩形（px）；分组不存在或尚未排布返回 false。
    /// RATING-04 以这里返回的真实 0 号格子矩形作为来源浮窗锚点。
    /// </summary>
    /// <param name="index">槽位索引（0 为清零叉/冲突三角）。</param>
    /// <param name="rect">屏幕矩形（px）。</param>
    /// <returns>可取到矩形返回 true。</returns>
    internal bool TryGetRatingSlotScreenRect(int index, out Rect rect)
    {
        rect = new Rect();
        if (!IsAttachedToWindow) return false;
        // 重建后分组还没有矩形：与 OnDraw 一样先 Arrange，避免一次无害刷新被当成“入口消失”。
        if (_dirty || _groups.Count == 0) Arrange(_horizontal, _availableMain);
        var group = _groups.Find(item => item.Kind == ControlBarGroupKind.Rating);
        if (group == null || index < 0 || index >= group.Children.Count) return false;
        var child = group.Children[index];
        if (child.Rect.Width <= 0 || child.Rect.Height <= 0) return false;
        double density = AndroidUiTheme.Density(Context!);
        var location = new int[2];
        GetLocationOnScreen(location);
        rect = new Rect(
            location[0] + (int)Math.Round(child.Rect.X * density),
            location[1] + (int)Math.Round(child.Rect.Y * density),
            location[0] + (int)Math.Round(child.Rect.MaxX * density),
            location[1] + (int)Math.Round(child.Rect.MaxY * density));
        return true;
    }

    /// <summary>EXIF 整组点击入口（触摸与读屏虚拟节点共用同一回调；验收读取）。</summary>
    internal void TapExifGroup() => _openTools?.Invoke();

    /// <summary>
    /// 按视觉顺序展开的虚拟节点快照（验收用）：读屏名称、分组类型、本地 dp 矩形与禁用态，
    /// 与 <c>ExploreByTouchHelper</c> 暴露给读屏的节点同源。
    /// </summary>
    /// <returns>节点快照列表。</returns>
    internal IReadOnlyList<(string Name, string Kind, double X, double Y, double Width, double Height, bool Enabled)> NodesForChecks()
    {
        var nodes = new List<(string, string, double, double, double, double, bool)>(_groups.Count * 6);
        foreach (var group in _groups)
        {
            foreach (var child in group.Children)
            {
                nodes.Add((AccessibleName(group, child), group.Kind.ToString(), child.Rect.X, child.Rect.Y,
                    child.Rect.Width, child.Rect.Height, child.Enabled));
            }
        }
        return nodes;
    }

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
        _solidIconPaint = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _solidIconPaint.TextSize = AndroidUiTheme.Sp(context, DesignTokens.ToolbarCommandGlyphFontSize);
        _solidIconPaint.TextAlign = Paint.Align.Center;
        _numberPaint = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingDigitFontSize,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground), bold: true);
        _starActive = AndroidUiTheme.Paint(AndroidUiTheme.StarActive);
        _starInactive = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
        _starClear = AndroidUiTheme.Paint(AndroidUiTheme.StarClear, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _starConflict = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingGlyphFontSize, AndroidUiTheme.StarConflict);
        _compactConflict = AndroidUiTheme.TextPaint(context, RatingControlGeometry.CompactGlyph, AndroidUiTheme.StarConflict);
        if (AndroidUiTheme.IconTypeface(context) is { } iconTypeface)
        {
            _starConflict.SetTypeface(iconTypeface);
            _compactConflict.SetTypeface(iconTypeface);
        }
        SetWillNotDraw(false);
        Focusable = true;
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        _accessibility = new ControlBarExploreByTouchHelper(this);
        ViewCompat.SetAccessibilityDelegate(this, _accessibility);
    }

    /// <summary>命令执行入口与评分入口；EXIF 块点击打开工具页，冲突三角打开来源浮窗。</summary>
    /// <param name="execute">执行共享命令。</param>
    /// <param name="rate">评分写回。</param>
    /// <param name="openTools">EXIF 块点击。</param>
    /// <param name="openRatingSources">冲突三角点击（RATING-04）。</param>
    internal void Wire(Action<string> execute, Action<int> rate, Action? openTools, Action? openRatingSources)
    {
        _execute = execute;
        _rate = rate;
        _openTools = openTools;
        _openRatingSources = openRatingSources;
    }

    /// <summary>与相邻分析栏共边时省略交叉轴前导内边距（SHELL-12）。</summary>
    /// <param name="joined">是否紧邻分析栏。</param>
    internal void SetJoinedToAnalysis(bool joined) => _joinedToAnalysis = joined;

    /// <summary>按共享设置重建三组控件（显隐/顺序、当前星级与 EXIF 项）。</summary>
    internal void Rebuild()
    {
        _groups.Clear();
        _specs.Clear();
        _dirty = false;
        _builtHorizontal = _horizontal;
        var entries = _main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            var children = new List<Child>(entries.Count);
            foreach (var entry in entries) children.Add(new Child { Exif = entry });
            _groups.Add(new Group(ControlBarGroupKind.Exif, children));
            _specs.Add(ToolbarFlowLayout.Exif(entries.Count, horizontal: _horizontal, bare: !_horizontal));
        }
        if (_main.Settings.ShowRating)
        {
            var children = new List<Child>(6);
            bool hasPhoto = _main.CurrentFile != null;
            int rating = _main.CurrentFile?.Rating ?? 0;
            // RATING-04：组级冲突时 0 号换成警示三角，几何与命中区不变。
            bool conflict = hasPhoto && _main.CurrentFile!.HasRatingConflict;
            for (int value = 0; value < 6; value++)
            {
                // TOOLBAR-03：无照片时整组禁用，按钮保留几何可见。
                children.Add(new Child
                {
                    Rating = value,
                    Active = value > 0 && value <= rating,
                    Conflict = value == 0 && conflict,
                    Enabled = hasPhoto,
                });
            }
            _groups.Add(new Group(ControlBarGroupKind.Rating, children));
            _specs.Add(ToolbarFlowLayout.Rating(ToolbarFlowLayout.ContentMain(_availableMain)));
        }
        var commands = new List<Child>();
        foreach (var hotkey in _main.ControlVM.EnabledControls)
        {
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
            _specs.Add(ToolbarFlowLayout.Commands(commands.Count));
        }
        Invalidate();
    }

    /// <summary>标记设置/文件变化后需要重建（宿主在变化时调用）。</summary>
    internal void MarkDirty() => _dirty = true;

    /// <summary>
    /// 命令的读屏文本：格式 <c>名称（主 / 次）</c> 取共享 <see cref="CommandTooltipText"/>，与桌面悬停提示同一文案；
    /// 本端只负责把绑定格式化成可读键名（TOOLBAR-04）。
    /// </summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>读屏文本。</returns>
    private static string BindingText(SettingsViewModel.HotkeyItem hotkey)
        => CommandTooltipText.Format(hotkey.Name, hotkey.PrimaryHotkey?.ToDisplayString(), hotkey.SecondaryHotkey?.ToDisplayString());

    /// <summary>量文档主轴/交叉轴长度（dp）；行布局主轴为宽、列布局主轴为高。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    /// <returns>文档尺寸（主轴、交叉轴）。</returns>
    internal (double Main, double Cross) MeasureDocument(bool horizontal, double availableMain)
    {
        _horizontal = horizontal;
        _availableMain = Math.Max(1, availableMain);
        // 分组尺寸依赖主轴方向（EXIF 横排/无衬底纵排、评分展开长度），换向必须按新方向重建。
        if (_dirty || _groups.Count == 0 || _builtHorizontal != horizontal) Rebuild();
        UpdateRatingSpec();
        var document = ToolbarFlowLayout.Arrange(_specs, horizontal, _availableMain, CrossLeadingPadding());
        return horizontal ? (document.Width, document.Height) : (document.Height, document.Width);
    }

    private double _documentWidth;
    private double _documentHeight;

    /// <summary>交叉轴前导内边距：与相邻分析栏共边时省略，独立时取工具区内边距。</summary>
    private double CrossLeadingPadding() => ToolRegionGeometry.ControlLeadingInset(_joinedToAnalysis);

    /// <summary>按共享流式排布落每个分组与子控件矩形，并保存文档尺寸供滚动宿主使用。</summary>
    /// <param name="horizontal">是否行布局。</param>
    /// <param name="availableMain">主轴可用长度（dp）。</param>
    internal (double Main, double Cross) Arrange(bool horizontal, double availableMain)
    {
        _horizontal = horizontal;
        _availableMain = Math.Max(1, availableMain);
        if (_dirty || _groups.Count == 0 || _builtHorizontal != horizontal) Rebuild();
        UpdateRatingSpec();
        var document = ToolbarFlowLayout.Arrange(_specs, horizontal, _availableMain, CrossLeadingPadding());
        _documentWidth = document.Width;
        _documentHeight = document.Height;
        for (int index = 0; index < _groups.Count && index < document.Groups.Count; index++)
        {
            var group = _groups[index];
            var frame = document.Groups[index];
            group.Rect = frame;
            LayoutChildren(group, frame);
        }
        _accessibility.InvalidateRoot();
        Invalidate();
        return horizontal ? (document.Width, document.Height) : (document.Height, document.Width);
    }

    /// <summary>按当前主轴可用长度刷新评分分组尺寸：窗口跨过展开阈值时展开/紧凑必须真实切换。</summary>
    private void UpdateRatingSpec()
    {
        for (int index = 0; index < _groups.Count && index < _specs.Count; index++)
            if (_groups[index].Kind == ControlBarGroupKind.Rating)
                _specs[index] = ToolbarFlowLayout.Rating(ToolbarFlowLayout.ContentMain(_availableMain));
    }

    /// <summary>按分组类型落子控件：EXIF 项目、评分六格或命令按钮。</summary>
    private void LayoutChildren(Group group, LayoutRect frame)
    {
        switch (group.Kind)
        {
            case ControlBarGroupKind.Exif:
                double blockMain = _horizontal ? frame.Width : frame.Height;
                double cross = _horizontal ? frame.Height : frame.Width;
                for (int index = 0; index < group.Children.Count; index++)
                {
                    var item = ExifBlockGeometry.ItemRect(index, group.Children.Count, _horizontal, blockMain, !_horizontal);
                    // 无衬底纵排：透明容器固定 44 宽，项目宽（52）被容器裁切，文字在容器内截断。
                    double itemCross = Math.Min(_horizontal ? item.Height : item.Width, cross);
                    group.Children[index].Rect = _horizontal
                        ? new LayoutRect(frame.X + item.X, frame.Y, item.Width, itemCross)
                        : new LayoutRect(frame.X, frame.Y + item.Y, itemCross, item.Height);
                }
                break;
            case ControlBarGroupKind.Rating:
                var control = RatingControlGeometry.ControlFrame(frame, _horizontal);
                double rowLength = _horizontal ? control.Width : control.Height;
                double rowCross = _horizontal ? control.Height : control.Width;
                var form = RatingControlGeometry.ResolveForm(rowLength);
                _ratingCompact = form == RatingControlForm.Compact;
                if (form == RatingControlForm.Expanded)
                {
                    for (int value = 0; value < group.Children.Count; value++)
                    {
                        var slot = RatingControlGeometry.SlotRect(value, rowLength, rowCross, _horizontal,
                            RatingControlGeometry.Slot);
                        group.Children[value].Rect = _horizontal
                            ? new LayoutRect(control.X + slot.X, control.Y, slot.Width, slot.Height)
                            : new LayoutRect(control.X, control.Y + slot.Y, slot.Width, slot.Height);
                    }
                    break;
                }
                // RATING-07 紧凑态：五颗星（冲突时另加三角）按 CompactOffset 静态排布，不新增弹窗；
                // 每个可读节点仍是胶囊内的真实矩形，非冲突 0 号不占位。
                bool conflict = group.Children.Count > 0 && group.Children[0].Conflict;
                double glyph = RatingControlGeometry.CompactGlyph;
                double mainStart = _horizontal ? control.X : control.Y;
                double mainLength = _horizontal ? control.Width : control.Height;
                double crossStart = _horizontal ? control.Y : control.X;
                double crossLength = _horizontal ? control.Height : control.Width;
                for (int value = 0; value < group.Children.Count; value++)
                {
                    if (value == 0 && !conflict)
                    {
                        group.Children[value].Rect = default;
                        continue;
                    }
                    double offset = RatingControlGeometry.CompactOffset(value, conflict, _horizontal, mainLength);
                    double centerMain = mainStart + mainLength / 2 + offset;
                    double centerCross = crossStart + crossLength / 2;
                    double x = Math.Clamp(centerMain - glyph / 2, mainStart, Math.Max(mainStart, mainStart + mainLength - glyph));
                    double y = Math.Clamp(centerCross - glyph / 2, crossStart, Math.Max(crossStart, crossStart + crossLength - glyph));
                    group.Children[value].Rect = _horizontal
                        ? new LayoutRect(x, y, glyph, glyph)
                        : new LayoutRect(y, x, glyph, glyph);
                }
                break;
            default:
                double side = ToolbarFlowLayout.CommandSize;
                for (int index = 0; index < group.Children.Count; index++)
                {
                    double offset = ToolbarFlowLayout.CommandOffset(index);
                    group.Children[index].Rect = _horizontal
                        ? new LayoutRect(frame.X + offset, frame.Y, side, side)
                        : new LayoutRect(frame.X, frame.Y + offset, side, side);
                }
                break;
        }
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
            switch (group.Kind)
            {
                case ControlBarGroupKind.Exif:
                    DrawExifGroup(canvas, group, density);
                    break;
                case ControlBarGroupKind.Rating:
                    // TOOLBAR-10：评分块与 EXIF 块共用 #222222 底色。
                    AndroidUiTheme.DrawRounded(canvas, ToRectF(group.Rect, density),
                        (float)(RatingControlGeometry.Thickness / 2 * density), AndroidUiTheme.CardBackground);
                    foreach (var child in group.Children) DrawRating(canvas, child, density);
                    break;
                default:
                    foreach (var child in group.Children) DrawCommand(canvas, child, density);
                    break;
            }
        }
    }

    /// <summary>EXIF 组：行布局画带衬底的块，左右布局只画项目（无衬底、圆角与内边距都不绘制）。</summary>
    private void DrawExifGroup(Canvas canvas, Group group, float density)
    {
        if (!ExifBare)
        {
            AndroidUiTheme.DrawRounded(canvas, ToRectF(group.Rect, density),
                (float)(ExifBlockGeometry.CornerRadius * density), AndroidUiTheme.CardBackground);
        }
        foreach (var child in group.Children)
        {
            if (child.Rect.Width <= 0 || child.Rect.Height <= 0 || child.Exif == null) continue;
            var rect = ToRectF(child.Rect, density);
            canvas.Save();
            canvas.ClipRect(rect.Left, rect.Top, Math.Min(rect.Right, Width), Math.Min(rect.Bottom, Height));
            AndroidUiTheme.DrawCentered(canvas, child.Exif.DisplayName, _textPaint, rect.CenterX(),
                rect.Top + rect.Height() * .36f, rect.Width());
            AndroidUiTheme.DrawCentered(canvas, child.Exif.DisplayValue, _valuePaint, rect.CenterX(),
                rect.Top + rect.Height() * .82f, rect.Width());
            canvas.Restore();
        }
    }

    /// <summary>评分子控件：0 号为清除叉（冲突时为警示三角），1–5 号按当前星级点亮。</summary>
    private void DrawRating(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        float cx = rect.CenterX(), cy = rect.CenterY();
        int alpha = child.Enabled ? 255 : (int)Math.Round(255 * DesignTokens.ColorToolbarDisabledContentAlpha);
        if (child.Rating == 0)
        {
            if (child.Conflict)
            {
                var conflictPaint = _ratingCompact ? _compactConflict : _starConflict;
                conflictPaint.Alpha = alpha;
                string glyph = AndroidUiTheme.IconGlyph(DesignTokens.IconWarning);
                AndroidUiTheme.DrawGlyphCentered(canvas, glyph, conflictPaint, cx, cy);
                return;
            }
            float arm = 4.5f * density;
            _starClear.Alpha = alpha;
            canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, _starClear);
            canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, _starClear);
            return;
        }
        _starActive.Alpha = alpha;
        _starInactive.Alpha = alpha;
        double glyphSize = _ratingCompact ? RatingControlGeometry.CompactGlyph : DesignTokens.ToolbarRatingGlyphFontSize;
        using var star = StarPath(cx, cy, (float)(glyphSize / 2 * density), (float)(glyphSize * .2 * density));
        canvas.DrawPath(star, child.Active ? _starActive : _starInactive);
    }

    /// <summary>
    /// 命令按钮：44×44 圆角底 + 图标字体。评分快捷命令 1–5 为实心星内叠数字（数字取按钮底色），
    /// 0 为描边字体的清零叉；两者与普通命令共用同一前景/禁用强调。
    /// </summary>
    private void DrawCommand(Canvas canvas, Child child, float density)
    {
        var rect = ToRectF(child.Rect, density);
        float cx = rect.CenterX(), cy = rect.CenterY();
        AndroidUiTheme.DrawRounded(canvas, rect, Math.Min(rect.Width(), rect.Height()) / 2,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground));
        var foreground = child.Enabled ? AndroidUiTheme.TextPrimary : Color.ParseColor("#666666")!;
        int alpha = child.Enabled ? 255 : (int)Math.Round(255 * DesignTokens.ColorToolbarDisabledContentAlpha);
        if (child.Number != null)
        {
            if (child.Number == "0")
            {
                var regular = AndroidUiTheme.IconTypeface(Context!);
                if (regular != null) _iconPaint.SetTypeface(regular);
                _iconPaint.Color = foreground;
                _iconPaint.Alpha = alpha;
                AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconClear), _iconPaint, cx, cy);
                return;
            }
            var solid = AndroidUiTheme.IconSolidTypeface(Context!);
            if (solid != null) _solidIconPaint.SetTypeface(solid);
            _solidIconPaint.Color = foreground;
            _solidIconPaint.Alpha = alpha;
            AndroidUiTheme.DrawGlyphCentered(canvas, AndroidUiTheme.IconGlyph(DesignTokens.IconStarFilled), _solidIconPaint, cx, cy);
            // 数字叠在星形中心，颜色取按钮底色；命中区域仍是整颗按钮，数字不单独拦截点击。
            _numberPaint.Alpha = alpha;
            AndroidUiTheme.DrawGlyphCentered(canvas, child.Number, _numberPaint, cx, cy);
            return;
        }
        var typeface = AndroidUiTheme.IconTypeface(Context!);
        if (typeface != null) _iconPaint.SetTypeface(typeface);
        _iconPaint.Color = foreground;
        _iconPaint.Alpha = alpha;
        AndroidUiTheme.DrawGlyphCentered(canvas, child.Icon ?? "?", _iconPaint, cx, cy);
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
                        if (!child.Enabled) break;
                        // RATING-04：冲突三角只打开来源浮窗，不写 0。
                        if (child.Rating == 0 && child.Conflict)
                        {
                            _openRatingSources?.Invoke();
                            break;
                        }
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
        ControlBarGroupKind.Rating => child.Rating == 0
            ? (child.Conflict ? "星级冲突" : "无星级")
            : $"{child.Rating} 星",
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
                // RATING-04：读屏点按冲突三角与触摸同义，只开浮窗、不写值。
                if (child.Rating == 0 && child.Conflict)
                {
                    _openRatingSources?.Invoke();
                    return true;
                }
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
        => AndroidUiTheme.StarPath(cx, cy, outer, inner);

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
        internal bool Conflict { get; init; }
        internal string? Command { get; init; }
        internal string? Icon { get; init; }
        internal string? Number { get; init; }
        internal string? Label { get; init; }
        internal string? Binding { get; init; }
        internal bool Enabled { get; init; } = true;
        internal LayoutRect Rect { get; set; }
    }
}
