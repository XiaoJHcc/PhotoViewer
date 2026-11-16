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
    private readonly Dictionary<string, NSButton> _commands = [];
    private readonly List<NSButton> _ratings = [];
    private bool _horizontal;

    public override bool IsFlipped => true;
    /// <summary>实际分组供布局与系统截图验收读取。</summary>
    internal IReadOnlyList<NSView> Groups => _groups;
    /// <summary>分组类型与 <see cref="Groups"/> 一一对应。</summary>
    internal IReadOnlyList<ControlBarGroupKind> GroupKinds => _kinds;
    /// <summary>命令身份与原生按钮保持一一对应。</summary>
    internal IReadOnlyDictionary<string, NSButton> Commands => _commands;
    /// <summary>评分按钮按评分值索引，视觉顺序由布局方向决定。</summary>
    internal IReadOnlyList<NSButton> Ratings => _ratings;
    /// <summary>评分控件的实际外框视图。</summary>
    internal NSView? RatingControl => _groups.FirstOrDefault(group => group.Subviews.FirstOrDefault() is RatingControlView)?.Subviews[0];
    /// <summary>实际显示的 EXIF 项数（工具区背景出现条件之一）。</summary>
    internal int ExifItemCount { get; private set; }

    /// <summary>建立透明控制文档：栏位材质由主窗口的工具区宿主承担，本层不画第二份底色。</summary>
    internal MacControlBar(Action changed)
    {
        _changed = changed;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.Clear.CGColor;
    }

    /// <summary>根据共享设置重建三组控件，保留设置排序、主次手势与缺少照片时的状态。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行回调。</param>
    /// <param name="rate">评分写回回调。</param>
    /// <param name="openTools">EXIF 信息块打开工具窗口。</param>
    /// <param name="showSources">RATING-04：冲突时 0 号按钮改为来源浮窗入口，参数即该按钮。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action<int> rate, Action? openTools = null,
        Action<NSView>? showSources = null)
    {
        ClearGroups();
        var entries = main.ControlVM.EnabledExifItems;
        ExifItemCount = entries.Count;
        if (entries.Count > 0)
        {
            // EXIF 块使用系统分组外观，块内两行文字按共享几何落位。
            var exif = new ExifBlockView { Clicked = openTools };
            foreach (var entry in entries) exif.AddTile(new ExifTile(entry));
            AddGroup(exif, ControlBarGroupKind.Exif);
        }
        if (main.Settings.ShowRating)
        {
            var ratingGroup = new GroupView { WantsLayer = false };
            var control = new RatingControlView();
            ratingGroup.AddSubview(control);
            for (int rating = 0; rating < 6; rating++)
            {
                int value = rating;
                bool conflict = value == 0 && main.CurrentFile?.HasRatingConflict == true;
                bool active = rating > 0 && rating <= (main.CurrentFile?.Rating ?? 0);
                string symbol = rating == 0 ? (conflict ? MacSymbols.RatingConflict : "xmark") : active ? "star.fill" : "star";
                string tooltip = rating == 0 ? (conflict ? "评分来源冲突" : "无星级") : rating + " 星";
                NSButton? button = null;
                button = ToolbarButton.Create(symbol, tooltip, () =>
                {
                    if (value == 0 && conflict && showSources != null) showSources(button!);
                    else rate(value);
                }, 44, false, RatingControlGeometry.Slot, RatingControlGeometry.Thickness);
                button.Tag = rating;
                // TOOLBAR-03：点亮星 color.star.on、空心星 color.star.off、清零叉中性 color.star.clear；
                // RATING-04 冲突时的警示三角取 color.star.conflict，几何与命中区不变。
                var color = MacDesignColors.Argb(rating == 0 ? (conflict ? DesignTokens.ColorStarConflict : DesignTokens.ColorStarClear)
                    : active ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
                button.ContentTintColor = color;
                button.Image = MacSymbols.Get(symbol, color);
                button.Enabled = main.CurrentFile != null;
                control.ContentView!.AddSubview(button);
                _ratings.Add(button);
            }
            AddGroup(ratingGroup, ControlBarGroupKind.Rating);
        }
        var commandGroup = new GroupView();
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            string? symbol = AppleSymbolNames.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            string tooltip = CommandTooltip(hotkey, main.Settings);
            var button = ToolbarButton.Create(symbol, tooltip, () => execute(hotkey.Command), ToolbarFlowLayout.CommandSize);
            // TOOLBAR-04：读屏名称与悬停提示一致，包含当前主/次绑定。
            ((NSView)button).AccessibilityLabel = tooltip;
            button.ContentTintColor = NSColor.Label;
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            if (hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal))
            {
                // TOOLBAR-04：评分快捷命令 = 星形内叠数字（0 为清零叉）。
                bool clear = hotkey.Command == "SetRating0";
                button.Title = "";
                button.Image = MacSymbols.Get(clear ? "xmark" : "star.fill");
                button.ImagePosition = NSCellImagePosition.ImageOnly;
                button.SymbolConfiguration = NSImageSymbolConfiguration.Create(
                    (nfloat)DesignTokens.ToolbarCommandGlyphFontSize, (double)NSFontWeight.Regular);
                if (!clear) button.AddSubview(RatingDigitLabel.Create(hotkey.Command[9..]));
            }
            commandGroup.AddSubview(GlassHost(button));
            _commands.Add(hotkey.Command, button);
        }
        if (commandGroup.Subviews.Length > 0) AddGroup(commandGroup, ControlBarGroupKind.Command);
        else commandGroup.Dispose();
        foreach (var hotkey in main.Settings.Hotkeys)
        {
            _hotkeys.Add(hotkey);
            hotkey.PropertyChanged += OnHotkeyChanged;
        }
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

    /// <summary>登记非空业务分组及其类型，不把组内按钮摊平成可任意断开的列表。</summary>
    /// <param name="group">已填好子控件的分组视图。</param>
    /// <param name="kind">分组类型，决定共享布局口径。</param>
    private void AddGroup(NSView group, ControlBarGroupKind kind)
    {
        _groups.Add(group);
        _kinds.Add(kind);
        AddSubview(group);
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

    /// <summary>使用 macOS 26 原生玻璃视图承载按钮，按钮自身只保留内容与事件。</summary>
    private static MacGlassEffectView GlassHost(NSButton button)
    {
        var host = new MacGlassEffectView
        {
            ContentView = button,
            CornerRadius = 22,
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
                var control = (RatingControlView)group.Subviews[0];
                control.Frame = new CGRect(controlFrame.X, controlFrame.Y, controlFrame.Width, controlFrame.Height);
                control.Arrange(horizontal, _ratings);
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

    /// <summary>重建或关闭时释放控件并解除模型订阅。</summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in _groups) DisposeView(group);
        _groups.Clear(); _kinds.Clear(); _commands.Clear(); _ratings.Clear(); ExifItemCount = 0;
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
            AddSubview(tile);
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
            ToolTip = entry.DisplayName + " · " + entry.DisplayValue;
            _label = Text(entry.DisplayName, 10, false);
            _value = Text(entry.DisplayValue, 12, true);
            AddSubview(_label);
            AddSubview(_value);
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
