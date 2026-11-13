using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 宽形态控制栏（TOOLBAR-01，手机形态不使用）：EXIF 信息块 / 星级控件 / 命令三分组固定顺序，
/// 分组尺寸、整组换行或换列、行长、行内居中与文档尺寸取共享 <see cref="ToolbarFlowLayout"/>，
/// 本类只把结果落成 UIKit frame。文档本身透明，底色由宿主栏位模糊材质承担。
/// EXIF 信息块在上下布局是深色模糊衬底的圆角块，左右布局取无衬底变体（无衬底、无块内边距，SHELL-12）。
/// <para>EXIF 信息块点按直达 EXIF 详情（无可用 EXIF 时由宿主回退工具列表，[TOOLS-01]）；
/// 星级组是常驻的 <see cref="iOSRatingControl"/>（评分变化只刷新显示值，不重建控制栏）；
/// 命令换 44 点圆形玻璃按钮，与星级控件同在一个系统玻璃容器内（静止分离、按压融合），评分快捷命令保留星心叠数字，读屏名称取共享
/// <see cref="CommandTooltipText"/> 的「名称（主 / 次）」。</para>
/// </summary>
internal sealed class iOSToolbarView : UIView
{
    private readonly Action _changed;
    private readonly List<UIView> _groupViews = [];
    private readonly List<(ControlBarGroupKind Kind, int Count)> _groupSpecs = [];
    private readonly List<UIButton> _commandButtons = [];
    private readonly List<SettingsViewModel.HotkeyItem> _hotkeys = [];
    private readonly Dictionary<string, UIButton> _commands = new(StringComparer.Ordinal);
    private readonly iOSRatingControl _ratingControl = new();
    private readonly iOSGlassContainer _glass = new(DesignTokens.ShellGlassContainerSpacing);
    // 命令组的逻辑矩形：命令圆按钮本身是玻璃容器内容区里与星级控件平级的效果元素（SHELL-12），
    // 这个不接收触摸的视图只保留「命令组」的排版矩形，不夹在命令玻璃与共同容器之间。
    private readonly UIView _commandGroup = new(CGRect.Empty)
    {
        BackgroundColor = UIColor.Clear,
        UserInteractionEnabled = false,
    };
    private iOSExifBlock? _exifGroup;
    private Action? _openExif;
    private bool _horizontal;
    private bool _hasRatingGroup;

    /// <summary>建立透明控制文档。</summary>
    /// <param name="changed">设置变化时请求宿主合并刷新。</param>
    internal iOSToolbarView(Action changed) : base(CGRect.Empty)
    {
        _changed = changed;
        BackgroundColor = UIColor.Clear;
        _ratingControl.Hidden = true;
        _commandGroup.AccessibilityElementsHidden = true;
        AddSubview(_glass);
    }

    /// <summary>业务分组视图（EXIF / 星级 / 命令，按顺序）；命令组只承载排版矩形，玻璃按钮与星级控件平级挂在玻璃容器内容区。</summary>
    internal IReadOnlyList<UIView> Groups => _groupViews;

    /// <summary>命令标识与原生按钮一一对应。</summary>
    internal IReadOnlyDictionary<string, UIButton> Commands => _commands;

    /// <summary>EXIF 信息块分组（点击直接进入 EXIF 详情；验收核对入口）。</summary>
    internal iOSExifBlock? ExifGroup => _exifGroup;

    /// <summary>常驻星级控件（三个槽位共用同一实现；宽形态控制栏始终是同一个实例）。</summary>
    internal iOSRatingControl RatingControl => _ratingControl;

    /// <summary>星级控件与命令圆按钮共用的玻璃容器（铺满文档，只命中内部控件）。</summary>
    internal iOSGlassContainer GlassContainer => _glass;

    /// <summary>本次重建是否包含星级分组（「显示评分」关闭时整组移除）。</summary>
    internal bool HasRatingGroup => _hasRatingGroup;

    /// <summary>EXIF 信息块点按入口（识别器与验收共用）：块自身的点按路径，直接进入 EXIF 详情。</summary>
    internal void TapExifGroup() => _exifGroup?.Tap();

    /// <summary>是否提供该命令的入口：本壳全部共享命令都有原生实现。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>是否有可用实现。</returns>
    internal static bool IsCommandAvailable(string command) => true;

    /// <summary>
    /// 按共享设置同步分组：EXIF 信息块、星级组（受「显示评分」控制）与命令圆按钮。
    /// 重建就地复用现有视图——EXIF 块项数不变只换文本、星级控件与同名命令按钮实例保留、只摘掉真正消失的项：
    /// 切图会因 EXIF 文本变化而重跑本方法，重挂控件会中断系统正在进行的按压缩放与松手回弹。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行入口（与 Mac 同一份命令语义）。</param>
    /// <param name="openExif">EXIF 信息块点击（直接进入 EXIF 详情）；null 表示本壳无工具页。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action? openExif = null)
    {
        _openExif = openExif;
        var groups = new List<UIView>(3);
        var specs = new List<(ControlBarGroupKind Kind, int Count)>(3);

        var entries = main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            if (_exifGroup == null)
            {
                _exifGroup = new iOSExifBlock();
                AddSubview(_exifGroup);
            }
            _exifGroup.Tapped = () => _openExif?.Invoke();
            _exifGroup.SetEntries(entries);
            groups.Add(_exifGroup);
            specs.Add((ControlBarGroupKind.Exif, entries.Count));
        }
        else DetachExif();

        _hasRatingGroup = main.Settings.ShowRating;
        _ratingControl.Hidden = !_hasRatingGroup;
        if (_hasRatingGroup)
        {
            if (!ReferenceEquals(_ratingControl.Superview, _glass.ContentView))
                _glass.ContentView.AddSubview(_ratingControl);
            groups.Add(_ratingControl);
            specs.Add((ControlBarGroupKind.Rating, 0));
        }
        else if (_ratingControl.Superview != null) _ratingControl.RemoveFromSuperview();

        SyncCommands(main, execute);
        if (_commandButtons.Count > 0)
        {
            if (_commandGroup.Superview == null) _glass.ContentView.AddSubview(_commandGroup);
            groups.Add(_commandGroup);
            specs.Add((ControlBarGroupKind.Command, _commandButtons.Count));
        }
        else _commandGroup.RemoveFromSuperview();

        _groupViews.Clear();
        _groupViews.AddRange(groups);
        _groupSpecs.Clear();
        _groupSpecs.AddRange(specs);

        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var hotkey in main.Settings.Hotkeys)
        {
            _hotkeys.Add(hotkey);
            hotkey.PropertyChanged += OnHotkeyChanged;
        }
    }

    /// <summary>
    /// 命令按钮就地同步：同名命令保留原按钮实例（只刷新读屏名称与可用态），新命令追加、消失的命令摘除释放。
    /// 按钮是玻璃容器内容区的直接子视图，与星级控件、合体按钮同处一层效果元素（SHELL-12）。
    /// </summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行入口。</param>
    private void SyncCommands(MainViewModel main, Action<string> execute)
    {
        var desired = new List<(string Command, UIButton Button)>();
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            if (!IsCommandAvailable(hotkey.Command)) continue;
            string? symbol = AppleSymbolNames.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            bool ratingCommand = hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal);
            bool clearRating = hotkey.Command == "SetRating0";
            if (!_commands.TryGetValue(hotkey.Command, out var button))
            {
                // TOOLBAR-04：评分快捷命令画实心星（清零为叉），星心再叠数字，不再与标题并排。
                button = iOSGlassButtons.Round(ratingCommand && !clearRating ? "star.fill" : symbol,
                    CommandAccessibilityLabel(hotkey), () => execute(hotkey.Command));
                double size = ToolbarFlowLayout.CommandSize;
                button.Frame = new CGRect(0, 0, size, size);
                if (ratingCommand && !clearRating)
                    button.AddSubview(new RatingDigitLabel(hotkey.Command[9..], ToolbarFlowLayout.CommandSize));
                _glass.ContentView.AddSubview(button);
            }
            button.AccessibilityLabel = CommandAccessibilityLabel(hotkey);
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            desired.Add((hotkey.Command, button));
        }
        foreach (var command in _commands.Keys.ToArray())
        {
            if (desired.Any(entry => entry.Command == command)) continue;
            var stale = _commands[command];
            stale.RemoveFromSuperview();
            stale.Dispose();
            _commands.Remove(command);
        }
        // 命令顺序属于用户设置：字典顺序与排版顺序都按本次生效顺序重排，实例本身保持不变。
        _commands.Clear();
        _commandButtons.Clear();
        foreach (var (command, button) in desired)
        {
            _commands.Add(command, button);
            _commandButtons.Add(button);
        }
    }

    /// <summary>摘除并释放 EXIF 信息块（没有启用项时整组移除，不留空条）。</summary>
    private void DetachExif()
    {
        if (_exifGroup == null) return;
        _exifGroup.RemoveFromSuperview();
        _exifGroup.Dispose();
        _exifGroup = null;
    }

    /// <summary>测量文档尺寸并顺带摆好分组与子控件（宿主随后调用 <see cref="Arrange"/> 落位）。</summary>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <returns>包含四周外边距的文档尺寸。</returns>
    internal CGSize Measure(bool horizontal, double availableMain) => Layout(horizontal, availableMain);

    /// <summary>在实际视口内居中每一行/列；超长分组保留完整尺寸并交系统滚动容器处理。</summary>
    /// <param name="viewport">系统滚动容器的客户端尺寸。</param>
    internal void Arrange(CGSize viewport)
    {
        double availableMain = _horizontal ? (double)viewport.Width : (double)viewport.Height;
        var measured = Layout(_horizontal, availableMain);
        var size = new CGSize(
            Math.Max((double)viewport.Width, (double)measured.Width),
            Math.Max((double)viewport.Height, (double)measured.Height));
        if (Frame.Size != size) Frame = new CGRect(0, 0, size.Width, size.Height);
        iOSViewGeometry.SetRectIfNeeded(_glass, new CGRect(0, 0, Bounds.Width, Bounds.Height));
    }

    /// <summary>玻璃容器始终铺满文档。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        iOSViewGeometry.SetRectIfNeeded(_glass, new CGRect(0, 0, Bounds.Width, Bounds.Height));
    }

    /// <summary>
    /// 命令按钮的读屏名称：格式 <c>名称（主 / 次）</c> 取共享 <see cref="CommandTooltipText"/>，
    /// 与桌面悬停提示同一文案；本端只负责把绑定格式化成苹果手势文本（TOOLBAR-04）。
    /// </summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>无障碍名称。</returns>
    private static string CommandAccessibilityLabel(SettingsViewModel.HotkeyItem hotkey)
        => CommandTooltipText.Format(hotkey.Name,
            hotkey.PrimaryHotkey == null ? null : hotkey.PrimaryHotkeyText,
            hotkey.SecondaryHotkey == null ? null : hotkey.SecondaryHotkeyText);

    /// <summary>显隐、名称或快捷键变化时请求宿主合并刷新。</summary>
    /// <param name="sender">事件源（快捷键项）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnHotkeyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.HotkeyItem.IsDisplay) or nameof(SettingsViewModel.HotkeyItem.Name)
            or nameof(SettingsViewModel.HotkeyItem.PrimaryHotkey) or nameof(SettingsViewModel.HotkeyItem.SecondaryHotkey)) _changed();
    }

    /// <summary>按方向求分组尺寸、整组换行并把分组与子控件落成 frame，返回文档尺寸。</summary>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <returns>文档尺寸。</returns>
    private CGSize Layout(bool horizontal, double availableMain)
    {
        _horizontal = horizontal;
        double main = Math.Max(1, availableMain - ToolbarFlowLayout.Padding * 2);
        var sizes = Sizes(main, horizontal);
        var lines = ToolbarFlowLayout.PackLines(sizes, main);
        double cross = ToolbarFlowLayout.Padding;
        foreach (var line in lines)
        {
            double lineCross = ToolbarFlowLayout.LineCross(sizes, line);
            double start = ToolbarFlowLayout.LineStart(availableMain, ToolbarFlowLayout.LineLength(sizes, line));
            foreach (int index in line)
            {
                var size = sizes[index];
                var rect = horizontal
                    ? new CGRect(start, cross, size.Main, lineCross)
                    : new CGRect(cross, start, lineCross, size.Main);
                iOSViewGeometry.SetRectIfNeeded(_groupViews[index], rect);
                LayoutChildren(index, horizontal, lineCross, rect);
                start += size.Main + ToolbarFlowLayout.GroupSpacing;
            }
            cross += lineCross + ToolbarFlowLayout.GroupSpacing;
        }
        var document = ToolbarFlowLayout.DocumentSize(sizes, horizontal, lines);
        return new CGSize(document.Width, document.Height);
    }

    /// <summary>按分组类型求共享尺寸：EXIF 按块尺寸（列布局取无衬底变体）、星级按可用主轴、命令按圆按钮数与间距。</summary>
    /// <param name="availableMain">扣掉外边距后的主轴可用长度。</param>
    /// <param name="horizontal">是否横向排列（决定 EXIF 块是否为衬底块）。</param>
    /// <returns>与分组顺序一致的尺寸。</returns>
    private List<ToolbarGroupSize> Sizes(double availableMain, bool horizontal)
    {
        var sizes = new List<ToolbarGroupSize>(_groupSpecs.Count);
        foreach (var (kind, count) in _groupSpecs)
        {
            sizes.Add(kind switch
            {
                ControlBarGroupKind.Exif => ExifSize(count, horizontal),
                ControlBarGroupKind.Rating => ToolbarFlowLayout.Rating(availableMain),
                _ => ToolbarFlowLayout.Commands(count),
            });
        }
        return sizes;
    }

    /// <summary>
    /// EXIF 分组尺寸：上下布局沿用共享 <see cref="ToolbarFlowLayout.Exif"/> 的衬底块；
    /// 左右布局（列布局）取无衬底变体——没有块内边距，只剩项目与项距，与手机横屏控制栏同口径（SHELL-12、TOOLBAR-05）。
    /// 无衬底纵排的交叉轴宽度固定取玻璃按钮边长（44），与同列的星级控件、命令圆按钮等宽同轴，不被项目固有宽撑大。
    /// </summary>
    /// <param name="count">EXIF 项数。</param>
    /// <param name="horizontal">EXIF 块是否横排。</param>
    /// <returns>EXIF 分组尺寸。</returns>
    private static ToolbarGroupSize ExifSize(int count, bool horizontal)
    {
        var (width, height) = ExifBlockGeometry.Size(count, horizontal, bare: !horizontal);
        return new ToolbarGroupSize(
            ControlBarGroupKind.Exif,
            horizontal ? width : height,
            horizontal ? height : DesignTokens.ShellGlassButtonSize);
    }

    /// <summary>
    /// 分组内子控件落位：EXIF 块与星级控件就是分组视图；命令圆按钮是玻璃容器内容区的平级效果元素，
    /// 位置按共享偏移换算到容器（= 文档）坐标，命令组矩形只作排版与验收读数的逻辑框。
    /// </summary>
    /// <param name="index">分组顺序索引。</param>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="lineCross">所在行的交叉轴长度。</param>
    /// <param name="groupRect">分组在文档坐标中的矩形。</param>
    private void LayoutChildren(int index, bool horizontal, double lineCross, CGRect groupRect)
    {
        var kind = _groupSpecs[index].Kind;
        if (kind == ControlBarGroupKind.Rating)
        {
            // 星级控件自绘胶囊与星排：分组视图自己消费方向，不再有子控件偏移。
            _ratingControl.Horizontal = horizontal;
            return;
        }
        if (kind == ControlBarGroupKind.Exif)
        {
            var exif = (iOSExifBlock)_groupViews[index];
            exif.Horizontal = horizontal;
            // 左右布局取无衬底变体：去掉模糊衬底与块内边距，直接放在栏背景上（SHELL-12、TOOLBAR-05）。
            exif.Bare = !horizontal;
            return;
        }
        double size = ToolbarFlowLayout.CommandSize;
        for (int child = 0; child < _commandButtons.Count; child++)
        {
            double offset = ToolbarFlowLayout.CommandOffset(child);
            iOSViewGeometry.SetRectIfNeeded(_commandButtons[child], horizontal
                ? new CGRect(groupRect.X + offset, groupRect.Y + (lineCross - size) / 2, size, size)
                : new CGRect(groupRect.X + (lineCross - size) / 2, groupRect.Y + offset, size, size));
        }
    }

    /// <summary>
    /// 重建或关闭时移除并释放分组、解除模型订阅；常驻星级控件与命令矩形只从视图树摘下，随控制栏一起复用。
    /// <c>UIView.Dispose</c> 只释放托管句柄，原生视图仍被父视图持有并留在视图树里；
    /// 必须先 <c>RemoveFromSuperview</c>，否则每次设置回写都会把新分组叠在旧分组上（重叠且不可用）。
    /// </summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in _groupViews)
        {
            group.RemoveFromSuperview();
            if (!ReferenceEquals(group, _ratingControl) && !ReferenceEquals(group, _commandGroup)
                && !ReferenceEquals(group, _exifGroup)) group.Dispose();
        }
        _groupViews.Clear();
        _groupSpecs.Clear();
        DetachExif();
        foreach (var button in _commands.Values)
        {
            button.RemoveFromSuperview();
            button.Dispose();
        }
        _commands.Clear();
        _commandButtons.Clear();
        _commandGroup.RemoveFromSuperview();
        _hasRatingGroup = false;
    }

    /// <summary>销毁时解除快捷键订阅并释放所有分组。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearGroups();
        base.Dispose(disposing);
    }

    /// <summary>
    /// 评分快捷命令星形内叠的数字：9 点半粗、颜色取命令按钮底色，居中叠在按钮上（TOOLBAR-04）。
    /// 不接收触摸，点击数字仍落到按钮本身。
    /// </summary>
    private sealed class RatingDigitLabel : UILabel
    {
        /// <summary>按令牌字号与按钮底色创建居中的只读数字标签。</summary>
        /// <param name="digit">星数（1–5）。</param>
        /// <param name="buttonSize">命令按钮边长。</param>
        internal RatingDigitLabel(string digit, double buttonSize) : base(CGRect.Empty)
        {
            Text = digit;
            Font = UIFont.SystemFontOfSize((nfloat)DesignTokens.ToolbarRatingDigitFontSize, UIFontWeight.Semibold);
            TextColor = iOSColors.FromToken(DesignTokens.ColorToolbarCommandBackground);
            TextAlignment = UITextAlignment.Center;
            BackgroundColor = UIColor.Clear;
            UserInteractionEnabled = false;
            // 数字只是图标的一部分：读屏名称仍由按钮的「评分 N 星 + 绑定」承担。
            AccessibilityElementsHidden = true;
            SizeToFit();
            Center = new CGPoint(buttonSize / 2, buttonSize / 2);
        }
    }
}
