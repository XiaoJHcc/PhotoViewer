using AppKit;
using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>原生控制栏保留 EXIF、评分、命令三个整体分组，分组尺寸与换行取共享 <see cref="ToolbarFlowLayout"/>。</summary>
internal sealed class MacControlBar : NSView
{
    private readonly Action _changed;
    private readonly List<NSView> _groups = [];
    private readonly List<ControlBarGroupKind> _kinds = [];
    private readonly List<SettingsViewModel.HotkeyItem> _hotkeys = [];
    private readonly Dictionary<string, NSButton> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NSGlassEffectView> _commandHosts = new(StringComparer.Ordinal);
    private readonly List<NSButton> _ratingButtons = [];
    private readonly List<ExifTile> _exifTiles = [];
    private ExifBlockView? _exifGroup;
    private GroupView? _ratingGroup;
    private GroupView? _commandGroup;
    private RatingControlView? _ratingControl;
    private MainViewModel? _main;
    private Action<string>? _execute;
    private Action<int>? _rate;
    private Action? _openTools;
    private Action<NSView>? _showSources;
    private bool _ratingConflict;
    private bool _horizontal;

    public override bool IsFlipped => true;
    /// <summary>实际分组供布局与系统截图验收读取。</summary>
    internal IReadOnlyList<NSView> Groups => _groups;
    /// <summary>分组类型与 <see cref="Groups"/> 一一对应。</summary>
    internal IReadOnlyList<ControlBarGroupKind> GroupKinds => _kinds;
    /// <summary>命令身份与原生按钮保持一一对应，顺序与设置一致。</summary>
    internal IReadOnlyDictionary<string, NSButton> Commands => _commands;
    /// <summary>评分按钮按评分值索引，视觉顺序由布局方向决定；评分组隐藏时为空。</summary>
    internal IReadOnlyList<NSButton> Ratings => _ratingGroup?.Superview == null ? [] : _ratingButtons;
    /// <summary>评分控件的实际外框视图；评分组隐藏时为 null。</summary>
    internal NSView? RatingControl => _ratingGroup?.Superview == null ? null : _ratingControl;
    /// <summary>实际显示的 EXIF 项数（工具区背景出现条件之一）。</summary>
    internal int ExifItemCount => _exifTiles.Count;
    /// <summary>STATE-05：当前文件夹不可用时星级控件进入禁用态（几何与内容不变）。</summary>
    internal bool RatingGated { get; set; }

    /// <summary>建立透明控制文档：栏位材质由主窗口的工具区宿主承担，本层不画第二份底色。</summary>
    internal MacControlBar(Action changed)
    {
        _changed = changed;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Clear.CGColor;
    }

    /// <summary>
    /// 控制栏结构摘要：只含决定分组与按钮增删的状态——评分显隐、EXIF 项的显隐与顺序、
    /// 命令的显隐 / 顺序 / 绑定（悬停提示文案）与排列方向。评分值、EXIF 文本与命令可用态
    /// 走就地刷新，不参与比对。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <returns>可逐字比较的结构摘要。</returns>
    internal static string Signature(MainViewModel main)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(main.Settings.ShowRating ? '1' : '0');
        builder.Append(main.IsRowLayout ? 'r' : 'c');
        builder.Append('|');
        foreach (var entry in main.ControlVM.EnabledExifItems) builder.Append(entry.DisplayName).Append(';');
        builder.Append('|');
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            if (AppleSymbolNames.ForCommand(hotkey.Command) == null) continue;
            builder.Append(hotkey.Command).Append(',').Append(CommandTooltip(hotkey, main.Settings)).Append(';');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 按共享设置同步三组控件，就地复用现有实例：EXIF 项数不变只换文本、评分槽位与同名命令按钮保留、
    /// 只摘除真正消失的项，切图与评分写回不重挂仍在的控件（SHELL-12、TOOLBAR-07）。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行回调。</param>
    /// <param name="rate">评分写回回调。</param>
    /// <param name="openTools">EXIF 信息块打开工具窗口。</param>
    /// <param name="showSources">RATING-04：冲突时 0 号按钮改为来源浮窗入口，参数即该按钮。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action<int> rate, Action? openTools = null,
        Action<NSView>? showSources = null)
    {
        _main = main;
        _execute = execute;
        _rate = rate;
        _openTools = openTools;
        _showSources = showSources;
        SyncExif(main.ControlVM.EnabledExifItems);
        SyncRatingGroup(main.Settings.ShowRating);
        SyncCommands(main);
        SyncGroupSequence();
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var hotkey in main.Settings.Hotkeys)
        {
            _hotkeys.Add(hotkey);
            hotkey.PropertyChanged += OnHotkeyChanged;
        }
        UpdateRatingDisplay();
        UpdateCommandAvailability();
    }

    /// <summary>EXIF 文本（含切图后的占位值）就地更新，项数不变时不改动分组与项实例。</summary>
    internal void UpdateExifSummary()
    {
        if (_main == null) return;
        SyncExif(_main.ControlVM.EnabledExifItems);
    }

    /// <summary>按启用项同步 EXIF 块：项数不变只换两行文本与悬停提示，新增项追加、消失项摘除释放。</summary>
    /// <param name="entries">设置启用的 EXIF 项，按设置顺序。</param>
    private void SyncExif(IReadOnlyList<ExifDisplayEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (_exifGroup != null) DisposeView(_exifGroup);
            _exifGroup = null;
            _exifTiles.Clear();
            return;
        }
        if (_exifGroup == null)
        {
            _exifGroup = new ExifBlockView();
            AddSubview(_exifGroup);
        }
        _exifGroup.Clicked = _openTools;
        while (_exifTiles.Count > entries.Count)
        {
            var stale = _exifTiles[^1];
            _exifTiles.RemoveAt(_exifTiles.Count - 1);
            _exifGroup.RemoveTile(stale);
        }
        for (int index = 0; index < entries.Count; index++)
        {
            if (index < _exifTiles.Count) _exifTiles[index].SetEntry(entries[index]);
            else
            {
                var tile = new ExifTile(entries[index]);
                _exifTiles.Add(tile);
                _exifGroup.AddTile(tile);
            }
        }
    }

    /// <summary>评分组只在「显示评分」开关变化时增删；槽位按钮与玻璃胶囊保持到窗口关闭。</summary>
    /// <param name="visible">是否显示评分组。</param>
    private void SyncRatingGroup(bool visible)
    {
        if (!visible)
        {
            _ratingGroup?.RemoveFromSuperview();
            return;
        }
        if (_ratingGroup == null)
        {
            _ratingGroup = new GroupView { WantsLayer = false };
            var control = new RatingControlView();
            _ratingControl = control;
            _ratingGroup.AddSubview(control);
            for (int rating = 0; rating < 6; rating++) _ratingButtons.Add(CreateRatingButton(rating));
            AddSubview(_ratingGroup);
        }
        else if (_ratingGroup.Superview == null) AddSubview(_ratingGroup);
    }

    /// <summary>建立单个评分槽位按钮；动作读取当前冲突状态，重建与否都不改变命中语义。</summary>
    /// <param name="rating">评分值（0 为清零 / 冲突入口）。</param>
    /// <returns>已挂进评分控件内容区的原生按钮。</returns>
    private NSButton CreateRatingButton(int rating)
    {
        int value = rating;
        NSButton? button = null;
        button = ToolbarButton.Create("star", RatingTooltip(value, false), () =>
        {
            // RATING-04：冲突时 0 号槽位是来源浮窗入口，不当作清零写入。
            if (value == 0 && _ratingConflict && _showSources != null) _showSources(button!);
            else _rate?.Invoke(value);
        }, DesignTokens.ShellGlassButtonSize, false, RatingControlGeometry.Slot, RatingControlGeometry.Thickness);
        button.Tag = rating;
        _ratingControl!.ContentView!.AddSubview(button);
        return button;
    }

    /// <summary>评分槽位的悬停提示与读屏名称。</summary>
    /// <param name="rating">评分值。</param>
    /// <param name="conflict">是否为冲突入口。</param>
    private static string RatingTooltip(int rating, bool conflict)
        => rating == 0 ? (conflict ? "评分来源冲突" : "无星级") : rating + " 星";

    /// <summary>评分值、冲突与可用态就地刷新，不替换槽位按钮（TOOLBAR-03、RATING-04）。</summary>
    internal void UpdateRatingDisplay()
    {
        var file = _main?.CurrentFile;
        int value = Math.Max(0, file?.Rating ?? 0);
        bool conflict = file?.HasRatingConflict == true;
        _ratingConflict = conflict;
        bool enabled = file != null && !RatingGated;
        foreach (var button in _ratingButtons)
        {
            int rating = (int)button.Tag;
            bool active = rating > 0 && rating <= value;
            string symbol = rating == 0 ? (conflict ? MacSymbols.RatingConflict : "xmark") : active ? "star.fill" : "star";
            string tooltip = RatingTooltip(rating, conflict);
            // TOOLBAR-03：点亮星 color.star.on、空心星 color.star.off、清零叉中性 color.star.clear；
            // RATING-04 冲突时的警示三角取 color.star.conflict，几何与命中区不变。
            var color = MacDesignColors.Argb(rating == 0 ? (conflict ? DesignTokens.ColorStarConflict : DesignTokens.ColorStarClear)
                : active ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
            ((ToolbarButton)button).ApplySymbol(symbol, color);
            button.ToolTip = tooltip;
            ((NSView)button).AccessibilityLabel = tooltip;
            button.Enabled = enabled;
        }
    }

    /// <summary>命令可用态随当前照片就地切换（TOOLBAR-09），不替换按钮。</summary>
    internal void UpdateCommandAvailability()
    {
        bool hasFile = _main?.CurrentFile != null;
        foreach (var pair in _commands) pair.Value.Enabled = pair.Key == "Open" || hasFile;
    }

    /// <summary>
    /// 命令按钮就地同步：同名命令保留原按钮实例（只刷新悬停提示与读屏名称），新命令追加、
    /// 消失的命令摘除释放；字典与排版顺序都按本次生效顺序重排。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    private void SyncCommands(MainViewModel main)
    {
        _commandGroup ??= new GroupView();
        var desired = new List<(string Command, NSButton Button)>();
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            string? symbol = AppleSymbolNames.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            string tooltip = CommandTooltip(hotkey, main.Settings);
            if (!_commands.TryGetValue(hotkey.Command, out var button))
            {
                string command = hotkey.Command;
                button = ToolbarButton.Create(symbol, tooltip, () => _execute?.Invoke(command), ToolbarFlowLayout.CommandSize);
                // TOOLBAR-04：读屏名称与悬停提示一致，包含当前主/次绑定。
                ((NSView)button).AccessibilityLabel = tooltip;
                button.ContentTintColor = NSColor.Label;
                if (command.StartsWith("SetRating", StringComparison.Ordinal))
                {
                    // TOOLBAR-04：评分快捷命令 = 星形内叠数字（0 为清零叉）。
                    bool clear = command == "SetRating0";
                    button.Title = "";
                    button.Image = MacSymbols.Get(clear ? "xmark" : "star.fill");
                    button.ImagePosition = NSCellImagePosition.ImageOnly;
                    button.SymbolConfiguration = NSImageSymbolConfiguration.Create(
                        (nfloat)DesignTokens.ToolbarCommandGlyphFontSize, (double)NSFontWeight.Regular);
                    if (!clear) button.AddSubview(RatingDigitLabel.Create(command[9..]));
                }
                var glazed = GlassHost(button);
                _commandHosts.Add(command, glazed);
                _commandGroup.AddSubview(glazed);
            }
            else
            {
                button.ToolTip = tooltip;
                ((NSView)button).AccessibilityLabel = tooltip;
            }
            desired.Add((hotkey.Command, button));
        }
        foreach (var pair in _commands.ToArray())
        {
            if (desired.Exists(entry => entry.Command == pair.Key)) continue;
            if (_commandHosts.Remove(pair.Key, out var host)) DisposeView(host);
            else pair.Value.Dispose();
        }
        _commands.Clear();
        foreach (var (command, button) in desired) _commands.Add(command, button);
        if (_commands.Count == 0)
        {
            _commandGroup.RemoveFromSuperview();
            return;
        }
        if (_commandGroup.Superview == null) AddSubview(_commandGroup);
        // 命令顺序属于用户设置：依次重挂把子视图顺序对齐本次生效顺序，实例本身保持不变。
        foreach (var (command, _) in desired) _commandGroup.AddSubview(_commandHosts[command]);
    }

    /// <summary>按 EXIF、评分、命令的固定顺序投影当前可见分组，空分组不占流式位置。</summary>
    private void SyncGroupSequence()
    {
        _groups.Clear();
        _kinds.Clear();
        if (_exifGroup is { Superview: not null }) { _groups.Add(_exifGroup); _kinds.Add(ControlBarGroupKind.Exif); }
        if (_ratingGroup is { Superview: not null }) { _groups.Add(_ratingGroup); _kinds.Add(ControlBarGroupKind.Rating); }
        if (_commandGroup is { Superview: not null }) { _groups.Add(_commandGroup); _kinds.Add(ControlBarGroupKind.Command); }
    }

    /// <summary>悬停描述包含业务名称和实际主次绑定：格式 `名称（主 / 次）` 取共享 <see cref="CommandTooltipText"/>，
    /// 本端只负责把绑定格式化成苹果修饰键符号并省略未设置占位。</summary>
    private static string CommandTooltip(SettingsViewModel.HotkeyItem hotkey, SettingsViewModel settings)
        => CommandTooltipText.Format(hotkey.Name,
            hotkey.PrimaryHotkey == null ? null : MacInput.FormatGesture(hotkey.PrimaryHotkey, settings),
            hotkey.SecondaryHotkey == null ? null : MacInput.FormatGesture(hotkey.SecondaryHotkey, settings));

    /// <summary>显隐、名称或快捷键变化时请求刷新，冲突标志不改变工具栏展示。</summary>
    private void OnHotkeyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.HotkeyItem.IsDisplay) or nameof(SettingsViewModel.HotkeyItem.Name)
            or nameof(SettingsViewModel.HotkeyItem.PrimaryHotkey) or nameof(SettingsViewModel.HotkeyItem.SecondaryHotkey)) _changed();
    }

    /// <summary>按当前内容求出共享流式分组尺寸（Mac 底部横排有衬底，右侧竖排无衬底）。</summary>
    private List<ToolbarGroupSize> GroupSizes(bool horizontal)
    {
        var sizes = new List<ToolbarGroupSize>(_groups.Count);
        foreach (var kind in _kinds)
        {
            sizes.Add(kind switch
            {
                ControlBarGroupKind.Exif => ToolbarFlowLayout.Exif(ExifItemCount, horizontal, bare: !horizontal),
                ControlBarGroupKind.Rating => ToolbarFlowLayout.Rating(RatingControlGeometry.ExpandedLength),
                _ => ToolbarFlowLayout.Commands(_commands.Count),
            });
        }
        return sizes;
    }

    /// <summary>整组沿主轴排布，空间不足时换行/换列，返回包含外边距的文档尺寸。</summary>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <param name="joined">是否与相邻分析栏共处同一工具区（交叉轴前导内边距为 0）。</param>
    /// <returns>文档尺寸。</returns>
    internal CGSize Measure(bool horizontal, double availableMain, bool joined = false)
    {
        var sizes = GroupSizes(horizontal);
        var document = ToolbarFlowLayout.Arrange(sizes, horizontal, availableMain, joined ? 0 : null);
        return new CGSize(document.Width, document.Height);
    }

    /// <summary>把共享排布结果落到实际视口：文档覆盖裁剪区，每行/列居中，分组矩形与子控件直接取共享几何。</summary>
    /// <param name="viewport">系统滚动容器的客户端尺寸。</param>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="joined">是否与相邻分析栏共处同一工具区。</param>
    internal void Arrange(CGSize viewport) => Arrange(viewport, _horizontal);

    /// <summary>把共享排布结果落到实际视口：文档覆盖裁剪区，每行/列居中，分组矩形与子控件直接取共享几何。</summary>
    /// <param name="viewport">系统滚动容器的客户端尺寸。</param>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="joined">是否与相邻分析栏共处同一工具区。</param>
    internal void Arrange(CGSize viewport, bool horizontal, bool joined = false)
    {
        _horizontal = horizontal;
        double availableMain = horizontal ? (double)viewport.Width : (double)viewport.Height;
        var sizes = GroupSizes(horizontal);
        var document = ToolbarFlowLayout.Arrange(sizes, horizontal, availableMain, joined ? 0 : null);
        Frame = new CGRect(CGPoint.Empty, new CGSize(
            Math.Max((double)viewport.Width, document.Width),
            Math.Max((double)viewport.Height, document.Height)));
        for (int index = 0; index < _groups.Count; index++)
        {
            var frame = document.Groups[index];
            _groups[index].Frame = new CGRect(frame.X, frame.Y, frame.Width, frame.Height);
            LayoutGroupContents(index, horizontal);
        }
    }

    /// <summary>使用 macOS 26 原生玻璃视图承载按钮，圆角取玻璃按钮令牌的半径。</summary>
    private static MacGlassEffectView GlassHost(NSButton button)
    {
        var host = new MacGlassEffectView
        {
            ContentView = button,
            CornerRadius = (nfloat)(DesignTokens.ShellGlassButtonSize / 2),
            Style = NSGlassEffectViewStyle.Regular,
        };
        button.Frame = host.Bounds;
        return host;
    }

    /// <summary>按分组类型把子控件落进共享矩形。</summary>
    private void LayoutGroupContents(int index, bool horizontal)
    {
        var group = _groups[index];
        var kind = _kinds[index];
        var bounds = group.Bounds;
        switch (kind)
        {
            case ControlBarGroupKind.Exif:
                double blockMain = horizontal ? (double)bounds.Width : (double)bounds.Height;
                var tiles = ((ExifBlockView)group).Tiles;
                ((ExifBlockView)group).Bare = !horizontal;
                var exifSize = ExifBlockGeometry.Size(tiles.Count, horizontal, bare: !horizontal);
                double crossOffset = Math.Max(0, ((horizontal ? (double)bounds.Height : (double)bounds.Width)
                    - (horizontal ? exifSize.Height : exifSize.Width)) / 2);
                for (int item = 0; item < tiles.Count; item++)
                {
                    var rect = ExifBlockGeometry.ItemRect(item, tiles.Count, horizontal, blockMain, bare: !horizontal);
                    tiles[item].Frame = new CGRect(rect.X + (horizontal ? 0 : crossOffset),
                        rect.Y + (horizontal ? crossOffset : 0), rect.Width, rect.Height);
                    if (tiles[item] is ExifTile tile) tile.Arrange();
                }
                break;
            case ControlBarGroupKind.Rating:
                var controlFrame = RatingControlGeometry.ControlFrame(
                    new LayoutRect(0, 0, (double)bounds.Width, (double)bounds.Height), horizontal);
                var control = _ratingControl!;
                control.Frame = new CGRect(controlFrame.X, controlFrame.Y, controlFrame.Width, controlFrame.Height);
                control.Arrange(horizontal, _ratingButtons);
                break;
            case ControlBarGroupKind.Command:
                double cross = horizontal ? (double)bounds.Height : (double)bounds.Width;
                double offset = Math.Max(0, (cross - ToolbarFlowLayout.CommandSize) / 2);
                for (int button = 0; button < group.Subviews.Length; button++)
                {
                    double main = ToolbarFlowLayout.CommandOffset(button);
                    group.Subviews[button].Frame = horizontal
                        ? new CGRect(main, offset, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize)
                        : new CGRect(offset, main, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize);
                    if (group.Subviews[button] is NSGlassEffectView glass && glass.ContentView is { } content)
                        content.Frame = glass.Bounds;
                }
                break;
        }
    }

    /// <summary>关闭时释放三组控件并解除模型订阅。</summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in new NSView?[] { _exifGroup, _ratingGroup, _commandGroup })
            if (group != null) DisposeView(group);
        _exifGroup = null; _ratingGroup = null; _commandGroup = null; _ratingControl = null;
        _exifTiles.Clear(); _ratingButtons.Clear();
        _groups.Clear(); _kinds.Clear(); _commands.Clear(); _commandHosts.Clear();
    }

    /// <summary>递归释放分组的托管控件，避免原生层持有旧按钮事件。</summary>
    private static void DisposeView(NSView view)
    {
        foreach (var child in view.Subviews) DisposeView(child);
        view.RemoveFromSuperview();
        view.Dispose();
    }

    /// <summary>关闭时解除快捷键订阅并释放所有分组。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearGroups();
        base.Dispose(disposing);
    }

    internal sealed class ToolbarButton : NSButton
    {
        private string? _symbol;

        internal static ToolbarButton Create(string symbol, string tooltip, Action action, double size = 40,
            bool command = true, double? width = null, double? height = null)
        {
            double frameWidth = width ?? size, frameHeight = height ?? size;
            var button = new ToolbarButton
            {
                Frame = new CGRect(0, 0, frameWidth, frameHeight),
                Image = MacSymbols.Get(symbol), ImagePosition = NSCellImagePosition.ImageOnly,
                Title = "", Bordered = true, ShowsBorderOnlyWhileMouseInside = true,
                BezelStyle = NSBezelStyle.Rounded,
                BorderShape = NSControlBorderShape.Circle,
                ToolTip = tooltip,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(
                    (nfloat)(command ? DesignTokens.ToolbarCommandGlyphFontSize
                        : symbol == "xmark" ? DesignTokens.ToolbarRatingClearGlyphFontSize : DesignTokens.ToolbarRatingGlyphFontSize),
                    (double)NSFontWeight.Regular),
            };
            button.SetButtonType(command ? NSButtonType.MomentaryPushIn : NSButtonType.MomentaryChange);
            ((NSView)button).AccessibilityLabel = tooltip;
            button.Activated += (_, _) => action();
            return button;
        }

        /// <summary>按符号与设计色着色；同一符号只生成一次着色副本，符号不变时只更新色调。</summary>
        /// <param name="symbol">SF Symbol 名称。</param>
        /// <param name="color">内容色。</param>
        internal void ApplySymbol(string symbol, NSColor color)
        {
            if (_symbol != symbol)
            {
                _symbol = symbol;
                Image = MacSymbols.Get(symbol, color);
            }
            ContentTintColor = color;
        }

        public override bool AcceptsFirstResponder() => Enabled;
    }

    /// <summary>桌面星级控件外框（RATING-07）：系统玻璃胶囊始终承载完整的六个原生槽位按钮。</summary>
    internal sealed class RatingControlView : MacGlassEffectView
    {
        public override bool IsFlipped => true;

        internal RatingControlView()
        {
            ContentView = new FlippedContentView();
            CornerRadius = (nfloat)(RatingControlGeometry.Thickness / 2);
            Style = NSGlassEffectViewStyle.Regular;
        }

        private sealed class FlippedContentView : NSView
        {
            public override bool IsFlipped => true;
        }

        /// <summary>按共享几何落位完整的 0–5 槽位，桌面空间不足由外层换行或滚动吸收。</summary>
        internal void Arrange(bool horizontal, IReadOnlyList<NSButton> buttons)
        {
            double main = horizontal ? (double)Bounds.Width : (double)Bounds.Height;
            double cross = horizontal ? (double)Bounds.Height : (double)Bounds.Width;
            foreach (var button in buttons)
            {
                button.Hidden = false;
                var slot = RatingControlGeometry.SlotRect((int)button.Tag, main, cross, horizontal, RatingControlGeometry.Slot);
                button.Frame = new CGRect(slot.X, slot.Y, slot.Width, slot.Height);
            }
        }
    }

    /// <summary>
    /// 评分快捷命令星形内叠的数字：9 点半粗、颜色取命令按钮底色，居中叠在 44 见方按钮上（TOOLBAR-04）。
    /// 不参与命中测试，点击数字仍落到按钮本身。
    /// </summary>
    private sealed class RatingDigitLabel : NSTextField
    {
        /// <summary>按令牌字号与按钮底色创建居中的只读数字标签。</summary>
        /// <param name="digit">星数（1–5）。</param>
        /// <returns>已按命令按钮尺寸居中的标签。</returns>
        internal static RatingDigitLabel Create(string digit)
        {
            var label = new RatingDigitLabel
            {
                StringValue = digit,
                Editable = false,
                Selectable = false,
                Bordered = false,
                DrawsBackground = false,
                Alignment = NSTextAlignment.Center,
                Font = NSFont.SystemFontOfSize((nfloat)DesignTokens.ToolbarRatingDigitFontSize, NSFontWeight.Semibold)
                    ?? throw new InvalidOperationException("系统字体不可用"),
                TextColor = MacDesignColors.Argb(DesignTokens.ColorToolbarCommandBackground),
            };
            label.SizeToFit();
            double button = ToolbarFlowLayout.CommandSize;
            label.Frame = new CGRect((button - (double)label.Frame.Width) / 2, (button - (double)label.Frame.Height) / 2,
                label.Frame.Width, label.Frame.Height);
            return label;
        }

        /// <summary>不拦截鼠标：数字盖在按钮中心，点击仍交给按钮。</summary>
        public override NSView? HitTest(CGPoint point) => null;
    }

    /// <summary>EXIF 整块使用 AppKit 原生分组外观，项目内边距仍由共享几何单独计算。</summary>
    internal sealed class ExifBlockView : NSBox
    {
        private readonly List<NSView> _tiles = [];
        /// <summary>整组点击回调；null 时不接收点击。</summary>
        public Action? Clicked { get; set; }
        /// <summary>按设置顺序排列的 EXIF 项目。</summary>
        internal IReadOnlyList<NSView> Tiles => _tiles;
        /// <summary>竖排无衬底模式：系统分组视图不绘制背景。</summary>
        internal bool Bare
        {
            get => Transparent;
            set => Transparent = value;
        }
        public override bool IsFlipped => true;

        internal ExifBlockView()
        {
            BoxType = NSBoxType.NSBoxPrimary;
            Title = string.Empty;
            TitlePosition = NSTitlePosition.NoTitle;
            ContentViewMargins = CGSize.Empty;
            ContentView = new FlippedContentView();
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);
        }

        private sealed class FlippedContentView : NSView
        {
            public override bool IsFlipped => true;
        }

        internal void AddTile(NSView tile)
        {
            _tiles.Add(tile);
            // NSBox 把子视图挂进内容区，项坐标因此与主窗口同为顶部原点（TOOLBAR-05）。
            ((NSView)ContentView!).AddSubview(tile);
        }

        /// <summary>摘除并释放消失的 EXIF 项，保留整组与其余项实例。</summary>
        /// <param name="tile">要摘除的信息项。</param>
        internal void RemoveTile(NSView tile)
        {
            _tiles.Remove(tile);
            DisposeView(tile);
        }

        /// <summary>点击整组时触发回调；未接线时交给系统默认处理。</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            if (Clicked == null) { base.MouseDown(theEvent); return; }
            Clicked();
        }
    }

    /// <summary>三个业务分组均以左上角作为布局原点；EXIF 分组可点击打开工具窗口。</summary>
    private sealed class GroupView : NSView
    {
        /// <summary>整组点击回调；null 时不接收点击。</summary>
        public Action? Clicked { get; set; }
        public override bool IsFlipped => true;
        /// <summary>点击整组时触发回调；未接线时交给系统默认处理。</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            if (Clicked == null) { base.MouseDown(theEvent); return; }
            Clicked();
        }
    }

    /// <summary>EXIF 小块：标签在上、值在下的层级；块底色与圆角由整组承载。</summary>
    private sealed class ExifTile : NSView
    {
        private readonly NSTextField _label;
        private readonly NSTextField _value;
        public override bool IsFlipped => true;

        internal ExifTile(ExifDisplayEntry entry) : base(CGRect.Empty)
        {
            _label = Text("", 10, false);
            _value = Text("", 12, true);
            AddSubview(_label);
            AddSubview(_value);
            SetEntry(entry);
        }

        /// <summary>就地替换两行文本与悬停提示，项实例与几何不变。</summary>
        /// <param name="entry">设置启用的 EXIF 项。</param>
        internal void SetEntry(ExifDisplayEntry entry)
        {
            ToolTip = entry.DisplayName + " · " + entry.DisplayValue;
            _label.StringValue = entry.DisplayName;
            _value.StringValue = entry.DisplayValue;
        }

        /// <summary>块内文字只作内容，不拦截整块入口：命中落到本项，点击再交给 EXIF 衬底。</summary>
        public override NSView? HitTest(CGPoint point) => Frame.Contains(point) ? this : null;

        /// <summary>项内任意位置按下都等同点击 EXIF 整块，不因两行文字的子控件吞掉事件。</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            if (Superview?.Superview is ExifBlockView block) block.MouseDown(theEvent);
            else base.MouseDown(theEvent);
        }

        /// <summary>按当前项目尺寸居中排布两行文本。</summary>
        internal void Arrange()
        {
            double labelHeight = 13, valueHeight = 16, gap = 2;
            double top = Math.Max(0, ((double)Bounds.Height - labelHeight - gap - valueHeight) / 2);
            _label.Frame = new CGRect(0, top, Bounds.Width, labelHeight);
            _value.Frame = new CGRect(0, top + labelHeight + gap, Bounds.Width, valueHeight);
        }

        /// <summary>生成居中原生标签，值使用较高字重，超宽内容采用尾部截断。</summary>
        private static NSTextField Text(string value, double size, bool emphasized)
            => new()
            {
                StringValue = value, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
                Alignment = NSTextAlignment.Center, LineBreakMode = NSLineBreakMode.TruncatingTail,
                Font = (emphasized ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size)) ?? throw new InvalidOperationException("系统字体不可用"),
                TextColor = emphasized ? NSColor.Label : NSColor.SecondaryLabel,
            };
    }
}

/// <summary>系统玻璃视图只负责材质；命中按公开 Frame/Bounds/IsFlipped 换算后交给 ContentView 内的原生控件。</summary>
internal class MacGlassEffectView : NSGlassEffectView
{
    /// <summary>TOOLBAR-10：主屏玻璃常态底材统一染 <c>color.toolbar.button.tint</c> 的白色色层。</summary>
    internal MacGlassEffectView()
    {
        TintColor = MacDesignColors.Argb(DesignTokens.ColorToolbarButtonTint, DesignTokens.ShellGlassButtonTintOpacityMac);
    }

    public override NSView? HitTest(CGPoint point)
    {
        if (Hidden || !Frame.Contains(point) || ContentView is not { } content || content.Hidden || Superview is not { } parent) return null;
        var local = new CGPoint(point.X - Frame.X, point.Y - Frame.Y);
        if (IsFlipped != parent.IsFlipped) local.Y = Bounds.Height - local.Y;
        if (content.IsFlipped != IsFlipped) local.Y = content.Bounds.Height - local.Y;
        if (!content.Bounds.Contains(local)) return null;
        foreach (var child in content.Subviews.Reverse())
            if (child.HitTest(local) is { } hit) return hit;
        return content is NSControl ? content : null;
    }
}

/// <summary>
/// 控制栏的滚动宿主（TOOLBAR-08）：接入鼠标后系统会把滚动条切回常驻的 legacy 样式，
/// 即使内容不溢出也常驻 17 点竖向槽并挤窄裁剪区。与胶片栏同一做法，每次重新排布前锁回悬浮样式，
/// 裁剪区始终铺满控制栏；是否显示滚动条由主窗口按"列布局且内容溢出"决定。
/// </summary>
internal sealed class MacControlScrollView : NSScrollView
{
    /// <summary>排布前锁回悬浮滚动条样式，避免系统偏好把裁剪区挤窄。</summary>
    public override void Tile()
    {
        if (ScrollerStyle != NSScrollerStyle.Overlay) ScrollerStyle = NSScrollerStyle.Overlay;
        base.Tile();
    }
}
