using AppKit;
using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>原生控制栏保留 EXIF、评分、命令三个整体分组，按可用空间换行或换列。</summary>
internal sealed class MacControlBar : NSView
{
    private readonly Action _changed;
    private readonly List<NSView> _groups = [];
    private readonly List<ControlBarLayout.GroupSpec> _specs = [];
    private readonly List<SettingsViewModel.HotkeyItem> _hotkeys = [];
    private readonly Dictionary<string, NSButton> _commands = [];
    private readonly List<NSButton> _ratings = [];
    private bool _horizontal;

    public override bool IsFlipped => true;
    /// <summary>实际分组供布局与系统截图验收读取。</summary>
    internal IReadOnlyList<NSView> Groups => _groups;
    /// <summary>命令身份与原生按钮保持一一对应。</summary>
    internal IReadOnlyDictionary<string, NSButton> Commands => _commands;
    /// <summary>评分按钮按评分值索引，视觉顺序由布局方向决定。</summary>
    internal IReadOnlyList<NSButton> Ratings => _ratings;

    /// <summary>建立深色控制文档，项目变更通过调用方合并刷新。</summary>
    internal MacControlBar(Action changed)
    {
        _changed = changed;
        WantsLayer = true;
        Layer!.BackgroundColor = NSColor.FromWhite(.07f, 1).CGColor;
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
        if (entries.Count > 0)
        {
            var exif = new GroupView { WantsLayer = true, Clicked = openTools };
            foreach (var entry in entries) exif.AddSubview(new ExifTile(entry));
            AddGroup(exif, ControlBarGroupKind.Exif);
        }
        if (main.Settings.ShowRating)
        {
            var ratingGroup = new GroupView { WantsLayer = true };
            ratingGroup.Layer!.BackgroundColor = NSColor.FromWhite(2f / 15, 1).CGColor;
            ratingGroup.Layer.CornerRadius = 6;
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
                }, 30, false);
                button.Tag = rating;
                // TOOLBAR-03：点亮星 color.star.on、空心星 color.star.off、清零叉中性 color.star.clear；
                // RATING-04 冲突时的警示三角取 color.star.conflict，几何与命中区不变。
                button.ContentTintColor = MacDesignColors.Argb(rating == 0 ? (conflict ? DesignTokens.ColorStarConflict : DesignTokens.ColorStarClear)
                    : active ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
                button.Enabled = main.CurrentFile != null;
                ratingGroup.AddSubview(button);
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
            var button = ToolbarButton.Create(symbol, tooltip, () => execute(hotkey.Command));
            // TOOLBAR-04：读屏名称与悬停提示一致，包含当前主/次绑定。
            ((NSView)button).AccessibilityLabel = tooltip;
            button.Bordered = false;
            button.ContentTintColor = NSColor.Label;
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            if (hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal))
            {
                // TOOLBAR-04：评分快捷命令 = 星形内叠数字（0 为清零叉），不再用星形图标与数字并排。
                bool clear = hotkey.Command == "SetRating0";
                button.Title = "";
                button.Image = MacSymbols.Get(clear ? "xmark" : "star.fill");
                button.ImagePosition = NSCellImagePosition.ImageOnly;
                button.SymbolConfiguration = NSImageSymbolConfiguration.Create(
                    (nfloat)DesignTokens.ToolbarCommandGlyphFontSize, (double)NSFontWeight.Regular);
                if (!clear) button.AddSubview(RatingDigitLabel.Create(hotkey.Command[9..]));
            }
            commandGroup.AddSubview(button);
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

    /// <summary>悬停描述包含业务名称和实际主次绑定，使用苹果修饰键符号并省略未设置占位。</summary>
    private static string CommandTooltip(SettingsViewModel.HotkeyItem hotkey, SettingsViewModel settings)
    {
        var gestures = new[] { hotkey.PrimaryHotkey, hotkey.SecondaryHotkey }.Where(gesture => gesture != null)
            .Select(gesture => MacInput.FormatGesture(gesture, settings)).Distinct().ToArray();
        return hotkey.Name + (gestures.Length == 0 ? "" : " · " + string.Join(" / ", gestures));
    }

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
        _specs.Add(new ControlBarLayout.GroupSpec(kind, group.Subviews.Length));
        AddSubview(group);
    }

    /// <summary>按方向安排组内控件，EXIF 小块和评分块保留各自的内边距与顺序。</summary>
    private void LayoutGroups(bool horizontal)
    {
        _horizontal = horizontal;
        for (int index = 0; index < _groups.Count; index++) LayoutGroup(index, horizontal);
    }

    /// <summary>把共享布局算得的子控件偏移落到原生 frame；交叉轴按所在行拉伸并居中。</summary>
    /// <param name="index">分组顺序索引。</param>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="crossSize">所在行的交叉轴长度，0 表示仅按分组自身长度。</param>
    private void LayoutGroup(int index, bool horizontal, double crossSize = 0)
    {
        var group = _groups[index];
        var spec = _specs[index];
        var metrics = ControlBarLayout.GetMetrics(spec.Kind);
        double main = ControlBarLayout.MainLength(spec, horizontal);
        double childCross = horizontal ? metrics.Height : metrics.Width;
        double cross = Math.Max(childCross, crossSize);
        group.Frame = new CGRect(0, 0, horizontal ? main : cross, horizontal ? cross : main);
        var offsets = ControlBarLayout.ChildOffsets(spec, horizontal, crossSize);
        var children = group.Subviews;
        for (int child = 0; child < children.Length; child++)
            children[child].Frame = new CGRect(horizontal ? offsets[child].Main : offsets[child].Cross,
                horizontal ? offsets[child].Cross : offsets[child].Main, metrics.Width, metrics.Height);
    }

    /// <summary>整组沿主轴排布，空间不足时换行/换列，返回包含外边距的文档尺寸。</summary>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <returns>文档尺寸。</returns>
    internal CGSize Measure(bool horizontal, double availableMain)
    {
        LayoutGroups(horizontal);
        var lines = ControlBarLayout.PackLines(_specs, horizontal, Math.Max(1, availableMain - ControlBarLayout.Padding * 2));
        var document = ControlBarLayout.DocumentSize(_specs, horizontal, lines);
        return new CGSize(document.Width, document.Height);
    }

    /// <summary>在实际视口内居中每一行/列，超长分组保留完整尺寸并交系统滚动容器处理。</summary>
    /// <param name="viewport">系统滚动容器的客户端尺寸。</param>
    internal void Arrange(CGSize viewport)
    {
        double availableMain = _horizontal ? (double)viewport.Width : (double)viewport.Height;
        var measured = Measure(_horizontal, availableMain);
        var document = new CGSize(Math.Max((double)viewport.Width, (double)measured.Width), Math.Max((double)viewport.Height, (double)measured.Height));
        Frame = new CGRect(CGPoint.Empty, document);
        var lines = ControlBarLayout.PackLines(_specs, _horizontal, Math.Max(1, availableMain - ControlBarLayout.Padding * 2));
        double cross = ControlBarLayout.Padding;
        foreach (var line in lines)
        {
            double lineCross = ControlBarLayout.LineCross(_specs, _horizontal, line);
            double main = Math.Max(ControlBarLayout.Padding, (availableMain - ControlBarLayout.LineLength(_specs, _horizontal, line)) / 2);
            foreach (int index in line)
            {
                LayoutGroup(index, _horizontal, lineCross);
                _groups[index].SetFrameOrigin(_horizontal ? new CGPoint(main, cross) : new CGPoint(cross, main));
                main += ControlBarLayout.MainLength(_specs[index], _horizontal) + ControlBarLayout.GroupSpacing;
            }
            cross += lineCross + ControlBarLayout.GroupSpacing;
        }
    }

    /// <summary>重建或关闭时释放控件并解除模型订阅。</summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in _groups) DisposeView(group);
        _groups.Clear(); _specs.Clear(); _commands.Clear(); _ratings.Clear();
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
        private bool _hover;
        private bool _command;
        private NSTrackingArea? _tracking;

        internal static ToolbarButton Create(string symbol, string tooltip, Action action, double size = 40, bool command = true)
        {
            var button = new ToolbarButton
            {
                Frame = new CGRect(0, 0, size, size),
                Image = MacSymbols.Get(symbol), ImagePosition = NSCellImagePosition.ImageOnly,
                Title = "", Bordered = false, ToolTip = tooltip, _command = command,
                SymbolConfiguration = NSImageSymbolConfiguration.Create((nfloat)(size <= 30 ? symbol == "xmark" ? 12 : 16 : 18), (double)NSFontWeight.Regular),
            };
            button.SetButtonType(NSButtonType.MomentaryChange);
            ((NSView)button).AccessibilityLabel = tooltip;
            button.Activated += (_, _) => action();
            return button;
        }

        public override bool AcceptsFirstResponder() => Enabled;
        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking != null) { RemoveTrackingArea(_tracking); _tracking.Dispose(); }
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
            AddTrackingArea(_tracking);
        }
        public override void MouseEntered(NSEvent e) { _hover = true; NeedsDisplay = true; }
        public override void MouseExited(NSEvent e) { _hover = false; NeedsDisplay = true; }
        public override void DrawRect(CGRect dirtyRect)
        {
            bool pressed = Enabled && (Cell?.Highlighted ?? false);
            bool hover = Enabled && _hover;
            var fill = _command
                ? pressed ? DesignTokens.ColorToolbarCommandPressedBackground : hover ? DesignTokens.ColorToolbarCommandHoverBackground : DesignTokens.ColorToolbarCommandBackground
                : pressed ? DesignTokens.ColorToolbarRatingPressedBackground : hover ? DesignTokens.ColorToolbarRatingHoverBackground : 0u;
            using var path = NSBezierPath.FromRoundedRect(new CGRect(.5, .5, Bounds.Width - 1, Bounds.Height - 1), 6, 6);
            MacDesignColors.Argb(fill).SetFill(); path.Fill();
            if (_command)
            {
                MacDesignColors.Argb(hover ? DesignTokens.ColorToolbarCommandHoverBorder : DesignTokens.ColorToolbarCommandBorder).SetStroke();
                path.LineWidth = 1; path.Stroke();
            }
            base.DrawRect(dirtyRect);
            if (Window?.FirstResponder == this)
            {
                NSColor.KeyboardFocusIndicator.SetStroke(); path.LineWidth = 2; path.Stroke();
            }
        }
    }

    /// <summary>
    /// 评分快捷命令星形内叠的数字：9 点半粗、颜色取命令按钮底色，居中叠在 40 见方按钮上（TOOLBAR-04）。
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
            double button = DesignTokens.ToolbarCommandButtonSize;
            label.Frame = new CGRect((button - (double)label.Frame.Width) / 2, (button - (double)label.Frame.Height) / 2,
                label.Frame.Width, label.Frame.Height);
            return label;
        }

        /// <summary>不拦截鼠标：数字盖在按钮中心，点击仍交给按钮。</summary>
        public override NSView? HitTest(CGPoint point) => null;
    }

    /// <summary>三个业务分组均以左上角作为布局原点；EXIF 分组可点击打开工具窗口（与旧壳 EXIF 信息块同义）。</summary>
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

    /// <summary>EXIF 小块保留次要标签、强调值与完整内容悬停提示。</summary>
    private sealed class ExifTile : NSView
    {
        public override bool IsFlipped => true;
        /// <summary>以原始 50×40 参考规模创建原生信息块，长值截断但不丢失内容。</summary>
        internal ExifTile(ExifDisplayEntry entry) : base(new CGRect(0, 0, 50, 40))
        {
            WantsLayer = true; Layer!.BackgroundColor = NSColor.FromWhite(2f / 15, 1).CGColor; Layer.CornerRadius = 6;
            ToolTip = entry.DisplayName + " · " + entry.DisplayValue;
            AddSubview(Text(entry.DisplayName, new CGRect(0, 4, 50, 13), 10, false));
            AddSubview(Text(entry.DisplayValue, new CGRect(0, 19, 50, 17), 12, true));
        }
        /// <summary>生成居中原生标签，值使用较高字重，超宽内容采用尾部截断。</summary>
        private static NSTextField Text(string value, CGRect frame, double size, bool emphasized)
            => new(frame) { StringValue = value, Editable = false, Selectable = false, Bordered = false, DrawsBackground = false,
                Alignment = NSTextAlignment.Center, LineBreakMode = NSLineBreakMode.TruncatingTail,
                Font = (emphasized ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size)) ?? throw new InvalidOperationException("系统字体不可用"),
                TextColor = emphasized ? NSColor.Label : NSColor.SecondaryLabel };
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
