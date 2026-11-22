using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 控制栏（TOOLBAR-01/02）：EXIF 摘要 → 评分 → 命令三组。分组尺寸、整组换行/换列与行内居中全部取共享
/// <see cref="ToolbarFlowLayout"/>，子控件矩形取 <see cref="ExifBlockGeometry"/>、
/// <see cref="RatingControlGeometry"/> 与 <see cref="ToolbarFlowLayout.CommandOffset"/>。
/// EXIF 块、星级控件与命令按钮都是真实子视图：前两者是自绘定制组件，命令是 Material 圆按钮，
/// 悬停/按下/焦点与禁用由 Material 状态层承担；本类只负责把共享矩形落到子视图上。
/// 移动端左右布局的 EXIF 取无衬底纵排（透明项目宽容器、整项裁剪），行布局保留衬底块。
/// </summary>
internal sealed class AndroidControlBar : FrameLayout
{
    private readonly MainViewModel _main;
    private readonly AndroidGlassControlLayer? _glassLayer;
    private readonly AndroidExifBlock _exifView;
    private readonly AndroidRatingControl _ratingView;
    private readonly Dictionary<string, AndroidGlassButton> _commandViews = new(StringComparer.Ordinal);
    private readonly Dictionary<View, LayoutRect> _viewRects = [];
    private readonly List<Group> _groups = [];
    private readonly List<ToolbarGroupSize> _specs = [];
    private Action<string>? _execute;
    private Action<int>? _rate;
    private Action? _openTools;
    private Action? _openRatingPopup;
    private bool _horizontal = true;
    private double _availableMain;
    private bool _joinedToAnalysis;
    private bool _dirty = true;
    private bool _builtHorizontal = true;
    private bool _ratingCompact;
    private double _documentWidth;
    private double _documentHeight;

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
    /// <summary>命令按钮的真实视图（验收读取：确认是 Material 按钮并核对位置/读屏名）。</summary>
    internal IReadOnlyDictionary<string, AndroidGlassButton> CommandViewsForChecks => _commandViews;
    /// <summary>EXIF 块子视图（验收读取底色与变体）。</summary>
    internal AndroidExifBlock ExifBlock => _exifView;
    /// <summary>星级控件子视图（验收读取胶囊矩形与状态）。</summary>
    internal AndroidRatingControl RatingControl => _ratingView;
    /// <summary>评分槽位当前是否接受输入（验收读取；STATE-05 不可用期间为 false）。</summary>
    internal bool RatingInputEnabledForChecks => _ratingView.EnabledForChecks;
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
    /// 验收用它核对 0 号槽位（清零叉/冲突三角）的真实落位，产品路径不读取。
    /// </summary>
    /// <param name="index">槽位索引（0 为清零叉/冲突三角）。</param>
    /// <param name="rect">屏幕矩形（px）。</param>
    /// <returns>可取到矩形返回 true。</returns>
    internal bool TryGetRatingSlotScreenRect(int index, out Rect rect)
    {
        rect = new Rect();
        if (!IsAttachedToWindow) return false;
        // 重建后分组还没有矩形：与 OnLayout 一样先 Arrange，避免一次无害刷新被当成“入口消失”。
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

    /// <summary>EXIF 整组点击入口（与子视图的点击路径共用同一回调；验收读取）。</summary>
    internal void TapExifGroup() => _openTools?.Invoke();

    /// <summary>
    /// 按视觉顺序展开的节点快照（验收用）：读屏名称、分组类型、本地 dp 矩形与禁用态，
    /// 与真实子视图同源（命令取真实按钮的读屏名称与布局矩形）。
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
    /// <param name="material">共享照片层材质（带衬底 EXIF 块取样用）；null 表示只用正式色层。</param>
    public AndroidControlBar(Context context, MainViewModel main, AndroidPhotoMaterial? material = null,
        AndroidGlassControlLayer? glassLayer = null) : base(context)
    {
        _main = main;
        _glassLayer = glassLayer;
        _exifView = new AndroidExifBlock(context, material);
        _exifView.ExifActivated += () => _openTools?.Invoke();
        _exifView.Visibility = ViewStates.Gone;
        AddView(_exifView);
        _ratingView = new AndroidRatingControl(context);
        _ratingView.RateRequested += rating => _rate?.Invoke(rating);
        _ratingView.PopupRequested += () => _openRatingPopup?.Invoke();
        _ratingView.Visibility = ViewStates.Gone;
        AddView(_ratingView);
        _glassLayer?.Register(_ratingView);
        SetClipChildren(true);
    }

    /// <summary>命令执行入口与评分入口；EXIF 块点击打开工具页，星级弹窗入口见 <paramref name="openRatingPopup"/>。</summary>
    /// <param name="execute">执行共享命令。</param>
    /// <param name="rate">评分写回。</param>
    /// <param name="openTools">EXIF 块点击。</param>
    /// <param name="openRatingPopup">打开星级弹窗（紧凑态起手与冲突三角，RATING-04 / RATING-07）。</param>
    internal void Wire(Action<string> execute, Action<int> rate, Action? openTools, Action? openRatingPopup)
    {
        _execute = execute;
        _rate = rate;
        _openTools = openTools;
        _openRatingPopup = openRatingPopup;
    }

    /// <summary>与相邻分析栏共边时省略交叉轴前导内边距（SHELL-12）。</summary>
    /// <param name="joined">是否紧邻分析栏。</param>
    internal void SetJoinedToAnalysis(bool joined) => _joinedToAnalysis = joined;

    /// <summary>按共享设置重建三组控件（显隐/顺序、当前星级与 EXIF 项）；同名命令保留原按钮实例。</summary>
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
            _exifView.SetContent(entries, _horizontal, !_horizontal, entries.Count);
            _exifView.Visibility = ViewStates.Visible;
        }
        else
        {
            _exifView.Visibility = ViewStates.Gone;
        }
        if (_main.Settings.ShowRating)
        {
            var children = new List<Child>(6);
            // STATE-05：目录不可用时评分写入已冻结，槽位保留几何但不再接受输入。
            bool hasPhoto = _main.CurrentFile != null && !_main.FolderVM.IsFolderUnavailable;
            int rating = _main.CurrentFile?.Rating ?? 0;
            // RATING-04：组级冲突时 0 号换成警示三角，几何与命中区不变。
            bool conflict = hasPhoto && _main.CurrentFile!.HasRatingConflict;
            for (int value = 0; value < 6; value++)
            {
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
            _ratingView.SetOrientation(_horizontal);
            _ratingView.SetState(rating, conflict, hasPhoto);
            _ratingView.Visibility = ViewStates.Visible;
        }
        else
        {
            _ratingView.Visibility = ViewStates.Gone;
        }

        var commands = new List<Child>();
        foreach (var hotkey in _main.ControlVM.EnabledControls)
        {
            bool ratingCommand = IsRatingCommand(hotkey.Command);
            string? number = RatingNumber(hotkey.Command);
            string glyph = CommandGlyph(hotkey);
            string description = CommandDescription(hotkey);
            bool enabled = IsCommandEnabled(_main, hotkey.Command);
            commands.Add(new Child
            {
                Command = hotkey.Command,
                Icon = glyph,
                Number = number,
                Label = hotkey.Name,
                Binding = description,
                Enabled = enabled,
            });
            if (!_commandViews.TryGetValue(hotkey.Command, out var button))
            {
                button = new AndroidGlassButton(Context!, glyph, description,
                    solid: ratingCommand && number != "0", number);
                button.Click += (_, _) =>
                {
                    if (button.Enabled) _execute?.Invoke(hotkey.Command);
                };
                _commandViews.Add(hotkey.Command, button);
                AddView(button);
                _glassLayer?.Register(button);
            }
            button.ContentDescription = description;
            button.Enabled = enabled;
            button.Visibility = ViewStates.Visible;
        }
        var stale = new List<string>();
        foreach (var command in _commandViews.Keys)
            if (!commands.Exists(child => child.Command == command)) stale.Add(command);
        foreach (var command in stale)
        {
            var button = _commandViews[command];
            _viewRects.Remove(button);
            _glassLayer?.Unregister(button);
            RemoveView(button);
            button.Dispose();
            _commandViews.Remove(command);
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
    /// 本端只负责把绑定格式化成可读键名（TOOLBAR-04）。移动端不设长按浮层。
    /// </summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>读屏文本。</returns>
    internal static string CommandDescription(SettingsViewModel.HotkeyItem hotkey)
        => CommandTooltipText.Format(hotkey.Name, hotkey.PrimaryHotkey?.ToDisplayString(),
            hotkey.SecondaryHotkey?.ToDisplayString());

    /// <summary>是否评分快捷命令（<c>SetRating0</c>–<c>SetRating5</c>）。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>评分命令返回 true。</returns>
    internal static bool IsRatingCommand(string command)
        => command.StartsWith("SetRating", StringComparison.Ordinal);

    /// <summary>评分快捷命令的星级（1–5 叠数字，"0" 表示清零叉、不叠数字；非评分命令为 null）。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>星级数字或 null。</returns>
    internal static string? RatingNumber(string command)
        => IsRatingCommand(command) ? command[9..] : null;

    /// <summary>
    /// 命令实际绘制的字形（TOOLBAR-04）：评分 1–5 为实心星、0 为零清叉，
    /// 其余取共享语义清单；控制栏与手机可选命令组共用同一映射。
    /// </summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>共享 Fluent 字形。</returns>
    internal static string CommandGlyph(SettingsViewModel.HotkeyItem hotkey)
    {
        if (!IsRatingCommand(hotkey.Command)) return AndroidIcons.ForCommand(hotkey.Command);
        return RatingNumber(hotkey.Command) == "0" ? AndroidIcons.Clear : AndroidIcons.StarFilled;
    }

    /// <summary>
    /// 命令可用性（TOOLBAR-04：无照片时除打开文件外全部禁用；STATE-05 不可用期间只冻结评分命令，
    /// 缩放、翻页与打开其他文件源照常可用）。
    /// </summary>
    /// <param name="main">主 VM。</param>
    /// <param name="command">共享命令标识。</param>
    /// <returns>命令可用返回 true。</returns>
    internal static bool IsCommandEnabled(MainViewModel main, string command)
    {
        if (command == "Open") return true;
        if (main.CurrentFile == null) return false;
        return !(IsRatingCommand(command) && main.FolderVM.IsFolderUnavailable);
    }

    /// <summary>
    /// 命令在控制栏与手机可选命令组中的呈现：字形、读屏名称、可用性与评分叠字同源。
    /// </summary>
    /// <param name="main">主 VM。</param>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>胶囊段模型。</returns>
    internal static GlassComboSegment CommandSegment(MainViewModel main, SettingsViewModel.HotkeyItem hotkey)
    {
        string? number = RatingNumber(hotkey.Command);
        return new GlassComboSegment(CommandGlyph(hotkey), CommandDescription(hotkey), hotkey.Command,
            IsCommandEnabled(main, hotkey.Command), IsRatingCommand(hotkey.Command) && number != "0", number);
    }

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
        double main = horizontal ? document.Width : document.Height;
        double cross = horizontal ? document.Height : document.Width;
        return (Math.Max(main, _availableMain), cross);
    }

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
        // 文档主轴至少填满视口：整行居中由共享 LineStart 给出，子视图必须落在自身宽高内才能被命中。
        double documentMain = horizontal ? document.Width : document.Height;
        double documentCross = horizontal ? document.Height : document.Width;
        double main = Math.Max(documentMain, _availableMain);
        _documentWidth = horizontal ? main : documentCross;
        _documentHeight = horizontal ? documentCross : main;
        _viewRects.Clear();
        for (int index = 0; index < _groups.Count && index < document.Groups.Count; index++)
        {
            var group = _groups[index];
            var frame = document.Groups[index];
            group.Rect = frame;
            LayoutChildren(group, frame);
        }
        Invalidate();
        return (main, documentCross);
    }

    /// <summary>按当前主轴可用长度刷新评分分组尺寸：窗口跨过展开阈值时展开/紧凑必须真实切换。</summary>
    private void UpdateRatingSpec()
    {
        for (int index = 0; index < _groups.Count && index < _specs.Count; index++)
            if (_groups[index].Kind == ControlBarGroupKind.Rating)
                _specs[index] = ToolbarFlowLayout.Rating(ToolbarFlowLayout.ContentMain(_availableMain));
    }

    /// <summary>
    /// 按分组类型落子控件：EXIF 块与星级控件是分组本身，命令按钮在分组内按共享偏移落位，
    /// 交叉轴按 (lineCross - size) / 2 居中（TOOLBAR-02）。
    /// </summary>
    private void LayoutChildren(Group group, LayoutRect frame)
    {
        switch (group.Kind)
        {
            case ControlBarGroupKind.Exif:
                _viewRects[_exifView] = frame;
                double blockMain = _horizontal ? frame.Width : frame.Height;
                double cross = _horizontal ? frame.Height : frame.Width;
                for (int index = 0; index < group.Children.Count; index++)
                {
                    var item = ExifBlockGeometry.ItemRect(index, group.Children.Count, _horizontal, blockMain, !_horizontal);
                    // 无衬底纵排：容器宽取项目固有宽，文字在容器内截断。
                    double itemCross = Math.Min(_horizontal ? item.Height : item.Width, cross);
                    group.Children[index].Rect = _horizontal
                        ? new LayoutRect(frame.X + item.X, frame.Y, item.Width, itemCross)
                        : new LayoutRect(frame.X, frame.Y + item.Y, itemCross, item.Height);
                }
                break;
            case ControlBarGroupKind.Rating:
                var control = RatingControlGeometry.ControlFrame(frame, _horizontal);
                _viewRects[_ratingView] = control;
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
                // RATING-07 紧凑态：五颗星（冲突时另加三角）按 CompactOffset 排布，点按/起手打开星级弹窗；
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
                double lineCross = _horizontal ? frame.Height : frame.Width;
                double crossOffset = (lineCross - side) / 2;
                for (int index = 0; index < group.Children.Count; index++)
                {
                    var child = group.Children[index];
                    double offset = ToolbarFlowLayout.CommandOffset(index);
                    child.Rect = _horizontal
                        ? new LayoutRect(frame.X + offset, frame.Y + crossOffset, side, side)
                        : new LayoutRect(frame.X + crossOffset, frame.Y + offset, side, side);
                    if (child.Command != null && _commandViews.TryGetValue(child.Command, out var button))
                        _viewRects[button] = child.Rect;
                }
                break;
        }
    }

    /// <summary>给子视图按共享矩形精确测量（不与内容协商尺寸）。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec), height = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(width, height);
        double density = AndroidUiTheme.Density(Context!);
        foreach (var (view, rect) in _viewRects)
        {
            int childWidth = Math.Max(0, (int)Math.Round(rect.Width * density));
            int childHeight = Math.Max(0, (int)Math.Round(rect.Height * density));
            view.Measure(MeasureSpec.MakeMeasureSpec(childWidth, MeasureSpecMode.Exactly),
                MeasureSpec.MakeMeasureSpec(childHeight, MeasureSpecMode.Exactly));
        }
    }

    /// <summary>
    /// 只重绘的刷新路径（星级写回、透明度等）也要把新状态落到真实子视图上：
    /// 几何没变时 Rebuild 只同步文本/状态与显隐，不移动任何控件。
    /// </summary>
    /// <param name="canvas">画布。</param>
    protected override void DispatchDraw(Canvas canvas)
    {
        if (_dirty && _availableMain > 0) Arrange(_horizontal, _availableMain);
        base.DispatchDraw(canvas);
    }

    /// <summary>把每个子视图摆到共享矩形上。</summary>
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

    /// <summary>虚拟节点/验收快照的读屏名称（与真实子视图文案同源，TOOLBAR-11）。</summary>
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

    /// <summary>释放命令按钮子视图。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var button in _commandViews.Values)
            {
                _glassLayer?.Unregister(button);
                RemoveView(button);
                button.Dispose();
            }
            _commandViews.Clear();
            _glassLayer?.Unregister(_ratingView);
            _viewRects.Clear();
            RemoveView(_exifView);
            _exifView.Dispose();
            RemoveView(_ratingView);
            _ratingView.Dispose();
        }
        base.Dispose(disposing);
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
