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
    /// <summary>评分按钮按评分值索引（仅展开态），视觉顺序由布局方向决定。</summary>
    internal IReadOnlyList<NSButton> Ratings => _ratings;
    /// <summary>评分控件的实际外框视图（展开/紧凑共用一个胶囊）。</summary>
    internal NSView? RatingControl => _groups.FirstOrDefault(group => group.Subviews.FirstOrDefault() is RatingControlView)?.Subviews[0];
    /// <summary>当前评分形态（展开态才有六个独立槽位按钮）。</summary>
    internal RatingControlForm RatingForm { get; private set; } = RatingControlForm.Expanded;
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
            // TOOLBAR-10：有平台深色模糊材质时 EXIF 块取该材质，块内两行文字直接叠在材质上。
            var exif = new ExifBlockView { Clicked = openTools };
            foreach (var entry in entries) exif.AddSubview(new ExifTile(entry));
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
                button.ContentTintColor = MacDesignColors.Argb(rating == 0 ? (conflict ? DesignTokens.ColorStarConflict : DesignTokens.ColorStarClear)
                    : active ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
                button.Enabled = main.CurrentFile != null;
                control.AddSubview(button);
                _ratings.Add(button);
            }
            control.Configure(main.CurrentFile?.Rating ?? 0, main.CurrentFile?.HasRatingConflict == true);
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
            button.Bordered = false;
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

    /// <summary>按当前内容求出共享流式分组尺寸（EXIF 无衬底只在移动端，Mac 两向都保留衬底块）。</summary>
    private List<ToolbarGroupSize> GroupSizes(bool horizontal, double availableMain)
    {
        var sizes = new List<ToolbarGroupSize>(_groups.Count);
        double contentMain = ToolbarFlowLayout.ContentMain(availableMain);
        foreach (var kind in _kinds)
        {
            sizes.Add(kind switch
            {
                ControlBarGroupKind.Exif => ToolbarFlowLayout.Exif(ExifItemCount, horizontal),
                ControlBarGroupKind.Rating => ToolbarFlowLayout.Rating(contentMain),
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
        var sizes = GroupSizes(horizontal, availableMain);
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
        var sizes = GroupSizes(horizontal, availableMain);
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
                var exifSize = ExifBlockGeometry.Size(group.Subviews.Length, horizontal);
                double crossOffset = Math.Max(0, ((horizontal ? (double)bounds.Height : (double)bounds.Width)
                    - (horizontal ? exifSize.Height : exifSize.Width)) / 2);
                for (int item = 0; item < group.Subviews.Length; item++)
                {
                    var rect = ExifBlockGeometry.ItemRect(item, group.Subviews.Length, horizontal, blockMain);
                    group.Subviews[item].Frame = new CGRect(rect.X + (horizontal ? 0 : crossOffset),
                        rect.Y + (horizontal ? crossOffset : 0), rect.Width, rect.Height);
                    if (group.Subviews[item] is ExifTile tile) tile.Arrange();
                }
                break;
            case ControlBarGroupKind.Rating:
                double slotMain = horizontal ? (double)bounds.Width : (double)bounds.Height;
                var form = RatingControlGeometry.ResolveForm(slotMain);
                RatingForm = form;
                var controlFrame = RatingControlGeometry.ControlFrame(
                    new LayoutRect(0, 0, (double)bounds.Width, (double)bounds.Height), horizontal);
                var control = (RatingControlView)group.Subviews[0];
                control.Frame = new CGRect(controlFrame.X, controlFrame.Y, controlFrame.Width, controlFrame.Height);
                control.Arrange(horizontal, form, _ratings);
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
        private bool _hover;
        private bool _command;
        private NSTrackingArea? _tracking;

        internal static ToolbarButton Create(string symbol, string tooltip, Action action, double size = 40,
            bool command = true, double? width = null, double? height = null)
        {
            double frameWidth = width ?? size, frameHeight = height ?? size;
            var button = new ToolbarButton
            {
                Frame = new CGRect(0, 0, frameWidth, frameHeight),
                Image = MacSymbols.Get(symbol), ImagePosition = NSCellImagePosition.ImageOnly,
                Title = "", Bordered = false, ToolTip = tooltip, _command = command,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(
                    (nfloat)(command ? DesignTokens.ToolbarCommandGlyphFontSize
                        : symbol == "xmark" ? DesignTokens.ToolbarRatingClearGlyphFontSize : DesignTokens.ToolbarRatingGlyphFontSize),
                    (double)NSFontWeight.Regular),
            };
            if (command)
            {
                // 命令按钮是 44×44 正圆：图层只做一次圆形裁切，状态面仍由自身的单层绘制完成。
                button.WantsLayer = true;
                button.Layer!.MasksToBounds = true;
                button.Layer.CornerRadius = (nfloat)(Math.Min(frameWidth, frameHeight) / 2);
            }
            button.SetButtonType(NSButtonType.MomentaryChange);
            ((NSView)button).AccessibilityLabel = tooltip;
            button.Activated += (_, _) => action();
            return button;
        }

        /// <summary>圆形命令按钮的图层半径随实际边长重算；评分槽位保持方形状态面，不参与圆裁切。</summary>
        public override void Layout()
        {
            base.Layout();
            if (_command)
                Layer!.CornerRadius = (nfloat)(Math.Min((double)Bounds.Width, (double)Bounds.Height) / 2);
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
            if (_command)
            {
                var fill = pressed ? DesignTokens.ColorToolbarCommandPressedBackground
                    : hover ? DesignTokens.ColorToolbarCommandHoverBackground : DesignTokens.ColorToolbarCommandBackground;
                using var circle = NSBezierPath.FromRoundedRect(new CGRect(.5, .5, Bounds.Width - 1, Bounds.Height - 1),
                    (nfloat)(Bounds.Width / 2), (nfloat)(Bounds.Height / 2));
                MacDesignColors.Argb(fill).SetFill(); circle.Fill();
                MacDesignColors.Argb(hover ? DesignTokens.ColorToolbarCommandHoverBorder : DesignTokens.ColorToolbarCommandBorder).SetStroke();
                circle.LineWidth = 1; circle.Stroke();
                base.DrawRect(dirtyRect);
                if (Window?.FirstResponder == this)
                {
                    using var focus = NSBezierPath.FromRoundedRect(new CGRect(1.5, 1.5, Bounds.Width - 3, Bounds.Height - 3),
                        (nfloat)(Bounds.Width / 2), (nfloat)(Bounds.Height / 2));
                    NSColor.KeyboardFocusIndicator.SetStroke(); focus.LineWidth = 2; focus.Stroke();
                }
                return;
            }
            var ratingFill = pressed ? DesignTokens.ColorToolbarRatingPressedBackground
                : hover ? DesignTokens.ColorToolbarRatingHoverBackground : 0u;
            using var path = NSBezierPath.FromRoundedRect(new CGRect(.5, .5, Bounds.Width - 1, Bounds.Height - 1),
                (nfloat)DesignTokens.ToolbarCornerRadius, (nfloat)DesignTokens.ToolbarCornerRadius);
            MacDesignColors.Argb(ratingFill).SetFill(); path.Fill();
            base.DrawRect(dirtyRect);
            if (Window?.FirstResponder == this)
            {
                NSColor.KeyboardFocusIndicator.SetStroke(); path.LineWidth = 2; path.Stroke();
            }
        }
    }

    /// <summary>
    /// 星级控件外框（RATING-07）：展开态承载六个方形槽位按钮，紧凑态只画静态星簇。
    /// 两个形态共用同一胶囊底色与圆角，不新增弹窗或拖动评分。
    /// </summary>
    internal sealed class RatingControlView : NSView
    {
        private readonly List<NSImageView> _cluster = [];
        private int _rating;
        private bool _conflict;
        private bool _horizontal = true;
        private RatingControlForm _form = RatingControlForm.Expanded;

        public override bool IsFlipped => true;

        /// <summary>评分胶囊使用与控件厚度相同的半圆端，两个形态共用一层底色裁切。</summary>
        internal RatingControlView()
        {
            WantsLayer = true;
            Layer!.CornerRadius = (nfloat)(RatingControlGeometry.Thickness / 2);
            Layer.MasksToBounds = true;
            Layer.BackgroundColor = MacDesignColors.Argb(DesignTokens.ColorToolbarBlockBackground).CGColor;
        }

        /// <summary>记录当前评分与冲突态，供紧凑星簇取色与图标。</summary>
        internal void Configure(int rating, bool conflict)
        {
            _rating = rating;
            _conflict = conflict;
            NeedsDisplay = true;
        }

        /// <summary>落位槽位按钮或紧凑星簇；槽位矩形与偏移全部取共享 <see cref="RatingControlGeometry"/>。</summary>
        internal void Arrange(bool horizontal, RatingControlForm form, IReadOnlyList<NSButton> buttons)
        {
            _horizontal = horizontal;
            _form = form;
            double main = horizontal ? (double)Bounds.Width : (double)Bounds.Height;
            double cross = horizontal ? (double)Bounds.Height : (double)Bounds.Width;
            bool expanded = form == RatingControlForm.Expanded;
            foreach (var button in buttons)
            {
                button.Hidden = !expanded;
                if (!expanded) continue;
                var slot = RatingControlGeometry.SlotRect((int)button.Tag, main, cross, horizontal, RatingControlGeometry.Slot);
                button.Frame = new CGRect(slot.X, slot.Y, slot.Width, slot.Height);
            }
            if (expanded)
            {
                foreach (var glyph in _cluster) glyph.Hidden = true;
            }
            else
            {
                LayoutCluster(main, cross);
            }
            NeedsDisplay = true;
        }

        /// <summary>紧凑态静态星簇：冲突时最前一格是警示三角，1–5 星按共享中心距与偏移叠压。</summary>
        private void LayoutCluster(double main, double cross)
        {
            int count = RatingControlGeometry.CompactItemCount(_conflict);
            while (_cluster.Count < count)
            {
                var view = new NSImageView { ImageScaling = NSImageScale.ProportionallyDown };
                AddSubview(view);
                _cluster.Add(view);
            }
            double glyph = RatingControlGeometry.CompactGlyph;
            for (int index = 0; index < _cluster.Count; index++)
            {
                var view = _cluster[index];
                view.Hidden = index >= count;
                if (view.Hidden) continue;
                int item = _conflict ? index : index + 1;
                string symbol = item == 0 ? MacSymbols.RatingConflict : item <= _rating ? "star.fill" : "star";
                view.Image = MacSymbols.Get(symbol);
                view.ContentTintColor = MacDesignColors.Argb(item == 0 ? DesignTokens.ColorStarConflict
                    : item <= _rating ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff);
                double offset = RatingControlGeometry.CompactOffset(item, _conflict, _horizontal, main);
                double centerMain = main / 2 + offset, centerCross = cross / 2;
                view.Frame = _horizontal
                    ? new CGRect(centerMain - glyph / 2, centerCross - glyph / 2, glyph, glyph)
                    : new CGRect(centerCross - glyph / 2, centerMain - glyph / 2, glyph, glyph);
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

    /// <summary>
    /// EXIF 整块的深色衬底（TOOLBAR-05 / TOOLBAR-10）：系统窗口内取样的原生材质，
    /// 十二点圆角只由图层裁切一次；横排与竖排都保留衬底，点击整块进入工具窗口。
    /// </summary>
    internal sealed class ExifBlockView : NSVisualEffectView
    {
        /// <summary>整组点击回调；null 时不接收点击。</summary>
        public Action? Clicked { get; set; }
        public override bool IsFlipped => true;

        internal ExifBlockView()
        {
            Material = NSVisualEffectMaterial.Sidebar;
            BlendingMode = NSVisualEffectBlendingMode.WithinWindow;
            State = NSVisualEffectState.Active;
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);
            WantsLayer = true;
            Layer!.CornerRadius = (nfloat)ExifBlockGeometry.CornerRadius;
            Layer.MasksToBounds = true;
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
            if (Superview is ExifBlockView block) block.MouseDown(theEvent);
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
