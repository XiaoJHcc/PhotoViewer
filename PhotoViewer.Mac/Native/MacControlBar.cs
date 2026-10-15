using AppKit;
using CoreGraphics;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using System.ComponentModel;

namespace PhotoViewer.Mac.Native;

/// <summary>原生控制栏保留 EXIF、评分、命令三个整体分组，按可用空间换行或换列。</summary>
internal sealed class MacControlBar : NSView
{
    private const double Padding = 10;
    private const double GroupSpacing = 10;
    private readonly Action _changed;
    private readonly List<NSView> _groups = [];
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
    internal void Rebuild(MainViewModel main, Action<string> execute, Action<int> rate)
    {
        ClearGroups();
        var entries = main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            var exif = new GroupView();
            foreach (var entry in entries) exif.AddSubview(new ExifTile(entry));
            AddGroup(exif);
        }
        if (main.Settings.ShowRating)
        {
            var ratingGroup = new GroupView { WantsLayer = true };
            ratingGroup.Layer!.BackgroundColor = NSColor.FromWhite(2f / 15, 1).CGColor;
            ratingGroup.Layer.CornerRadius = 6;
            for (int rating = 0; rating < 6; rating++)
            {
                int value = rating;
                bool active = rating > 0 && rating <= (main.CurrentFile?.Rating ?? 0);
                var button = MacSymbols.Button(rating == 0 ? "xmark" : active ? "star.fill" : "star",
                    rating == 0 ? "无星级" : rating + " 星", () => rate(value), 30, false);
                button.Tag = rating;
                button.ContentTintColor = active ? NSColor.FromRgb(255, 210, 20) : NSColor.SecondaryLabel;
                button.Enabled = main.CurrentFile != null;
                ratingGroup.AddSubview(button);
                _ratings.Add(button);
            }
            AddGroup(ratingGroup);
        }
        var commandGroup = new GroupView();
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            string? symbol = MacSymbols.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            string tooltip = CommandTooltip(hotkey, main.Settings);
            var button = MacSymbols.Button(symbol, tooltip, () => execute(hotkey.Command));
            button.BezelStyle = NSBezelStyle.SmallSquare;
            button.WantsLayer = true;
            button.Layer!.CornerRadius = 6;
            button.Layer.MasksToBounds = true;
            button.ContentTintColor = NSColor.Label;
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            if (hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal))
            {
                button.Title = hotkey.Command[9..];
                button.ImagePosition = NSCellImagePosition.ImageLeft;
                button.Font = NSFont.SystemFontOfSize(11) ?? throw new InvalidOperationException("系统字体不可用");
            }
            commandGroup.AddSubview(button);
            _commands.Add(hotkey.Command, button);
        }
        if (commandGroup.Subviews.Length > 0) AddGroup(commandGroup);
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

    /// <summary>登记非空业务分组，不把组内按钮摊平成可任意断开的列表。</summary>
    private void AddGroup(NSView group)
    {
        _groups.Add(group);
        AddSubview(group);
    }

    /// <summary>按方向安排组内控件，EXIF 小块和评分块保留各自的内边距与顺序。</summary>
    private void LayoutGroups(bool horizontal)
    {
        _horizontal = horizontal;
        foreach (var group in _groups) LayoutGroup(group, horizontal);
    }

    private void LayoutGroup(NSView group, bool horizontal, double crossSize = 0)
    {
        var children = group.Subviews;
        bool ratingGroup = children.Length > 0 && children[0] is NSButton button && _ratings.Contains(button);
        bool exifGroup = children.Length > 0 && children[0] is ExifTile;
        double inset = ratingGroup ? 8 : exifGroup ? 4 : 0;
        double spacing = ratingGroup ? 0 : exifGroup ? 4 : 8;
        double width = exifGroup ? 50 : ratingGroup ? 30 : 40;
        double height = ratingGroup ? 30 : 40;
        int count = children.Length;
        double childMain = horizontal ? width : height;
        double childCross = horizontal ? height : width;
        double main = count * childMain + Math.Max(0, count - 1) * spacing + inset * 2 * (exifGroup ? count : 1);
        double cross = Math.Max(childCross, crossSize);
        group.Frame = new CGRect(0, 0, horizontal ? main : cross, horizontal ? cross : main);
        for (int index = 0; index < count; index++)
        {
            int position = ratingGroup && !horizontal ? count - index - 1 : index;
            double childStart = inset + position * (childMain + spacing + (exifGroup ? inset * 2 : 0));
            double crossStart = (cross - childCross) / 2;
            children[index].Frame = new CGRect(horizontal ? childStart : crossStart,
                horizontal ? crossStart : childStart, width, height);
        }
    }

    /// <summary>整组沿主轴排布，空间不足时换行/换列，返回包含外边距的文档尺寸。</summary>
    internal CGSize Measure(bool horizontal, double availableMain)
    {
        LayoutGroups(horizontal);
        var lines = GetLines(Math.Max(1, availableMain - Padding * 2));
        double cross = lines.Sum(line => line.Max(group => CrossSize(group))) + Math.Max(0, lines.Count - 1) * GroupSpacing;
        double main = lines.Count == 0 ? 0 : lines.Max(line => LineLength(line));
        return horizontal ? new CGSize(main + Padding * 2, cross + Padding * 2)
            : new CGSize(cross + Padding * 2, main + Padding * 2);
    }

    /// <summary>在实际视口内居中每一行/列，超长分组保留完整尺寸并交系统滚动容器处理。</summary>
    internal void Arrange(CGSize viewport)
    {
        double availableMain = _horizontal ? (double)viewport.Width : (double)viewport.Height;
        var measured = Measure(_horizontal, availableMain);
        var document = new CGSize(Math.Max((double)viewport.Width, (double)measured.Width), Math.Max((double)viewport.Height, (double)measured.Height));
        Frame = new CGRect(CGPoint.Empty, document);
        var lines = GetLines(Math.Max(1, availableMain - Padding * 2));
        double cross = Padding;
        foreach (var line in lines)
        {
            double lineCross = line.Max(group => CrossSize(group));
            double main = Math.Max(Padding, (availableMain - LineLength(line)) / 2);
            foreach (var group in line)
            {
                LayoutGroup(group, _horizontal, lineCross);
                group.SetFrameOrigin(_horizontal ? new CGPoint(main, cross) : new CGPoint(cross, main));
                main += MainSize(group) + GroupSpacing;
            }
            cross += lineCross + GroupSpacing;
        }
    }

    /// <summary>按完整分组计算行列，单个超长分组保持整体而不是拆开评分或命令顺序。</summary>
    private List<List<NSView>> GetLines(double available)
    {
        var lines = new List<List<NSView>>();
        foreach (var group in _groups)
        {
            if (lines.Count == 0 || lines[^1].Count > 0 && LineLength(lines[^1]) + GroupSpacing + MainSize(group) > available) lines.Add([]);
            lines[^1].Add(group);
        }
        return lines;
    }

    /// <summary>返回分组在当前排列主轴上的长度。</summary>
    private double MainSize(NSView group) => _horizontal ? (double)group.Frame.Width : (double)group.Frame.Height;
    /// <summary>返回分组在当前排列交叉轴上的长度。</summary>
    private double CrossSize(NSView group) => _horizontal ? (double)group.Frame.Height : (double)group.Frame.Width;
    /// <summary>返回一行或一列的总长度，组间空隙不计入最后一项。</summary>
    private double LineLength(List<NSView> line) => line.Sum(MainSize) + Math.Max(0, line.Count - 1) * GroupSpacing;

    /// <summary>重建或关闭时释放控件并解除模型订阅。</summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in _groups) DisposeView(group);
        _groups.Clear(); _commands.Clear(); _ratings.Clear();
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

    /// <summary>三个业务分组均以左上角作为布局原点。</summary>
    private sealed class GroupView : NSView { public override bool IsFlipped => true; }

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
