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
/// <para>EXIF 信息块点按直达 EXIF 详情（无可用 EXIF 时由宿主回退工具列表，[TOOLS-01]）；
/// 星级组是常驻的 <see cref="iOSRatingControl"/>（评分变化只刷新显示值，不重建控制栏）；
/// 命令换 44 点圆形玻璃按钮，评分快捷命令保留星心叠数字，读屏名称取共享
/// <see cref="CommandTooltipText"/> 的「名称（主 / 次）」。</para>
/// </summary>
internal sealed class iOSToolbarView : UIView
{
    private readonly Action _changed;
    private readonly List<UIView> _groupViews = [];
    private readonly List<(ControlBarGroupKind Kind, int Count)> _groupSpecs = [];
    private readonly List<SettingsViewModel.HotkeyItem> _hotkeys = [];
    private readonly Dictionary<string, UIButton> _commands = new(StringComparer.Ordinal);
    private readonly iOSRatingControl _ratingControl = new();
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
    }

    /// <summary>业务分组视图（EXIF / 星级 / 命令，按顺序）。</summary>
    internal IReadOnlyList<UIView> Groups => _groupViews;

    /// <summary>命令标识与原生按钮一一对应。</summary>
    internal IReadOnlyDictionary<string, UIButton> Commands => _commands;

    /// <summary>EXIF 信息块分组（点击直接进入 EXIF 详情；验收核对入口）。</summary>
    internal iOSExifBlock? ExifGroup => _exifGroup;

    /// <summary>常驻星级控件（三个槽位共用同一实现；宽形态控制栏始终是同一个实例）。</summary>
    internal iOSRatingControl RatingControl => _ratingControl;

    /// <summary>本次重建是否包含星级分组（「显示评分」关闭时整组移除）。</summary>
    internal bool HasRatingGroup => _hasRatingGroup;

    /// <summary>EXIF 信息块点按入口（识别器与验收共用）：块自身的点按路径，直接进入 EXIF 详情。</summary>
    internal void TapExifGroup() => _exifGroup?.Tap();

    /// <summary>是否提供该命令的入口：本壳全部共享命令都有原生实现。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>是否有可用实现。</returns>
    internal static bool IsCommandAvailable(string command) => true;

    /// <summary>按共享设置重建分组：EXIF 信息块、星级组（受「显示评分」控制）与命令圆按钮。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行入口（与 Mac 同一份命令语义）。</param>
    /// <param name="openExif">EXIF 信息块点击（直接进入 EXIF 详情）；null 表示本壳无工具页。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action? openExif = null)
    {
        ClearGroups();
        _openExif = openExif;
        var entries = main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            var exif = new iOSExifBlock { Tapped = () => _openExif?.Invoke() };
            exif.SetEntries(entries);
            _exifGroup = exif;
            AddGroup(exif, ControlBarGroupKind.Exif, entries.Count);
        }
        _hasRatingGroup = main.Settings.ShowRating;
        _ratingControl.Hidden = !_hasRatingGroup;
        if (_hasRatingGroup) AddGroup(_ratingControl, ControlBarGroupKind.Rating, 0);
        else _ratingControl.RemoveFromSuperview();
        var commandGroup = new UIView(CGRect.Empty) { BackgroundColor = UIColor.Clear };
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            if (!IsCommandAvailable(hotkey.Command)) continue;
            string? symbol = AppleSymbolNames.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            bool ratingCommand = hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal);
            bool clearRating = hotkey.Command == "SetRating0";
            // TOOLBAR-04：评分快捷命令画实心星（清零为叉），星心再叠数字，不再与标题并排。
            var button = iOSGlassButtons.Round(ratingCommand && !clearRating ? "star.fill" : symbol,
                CommandAccessibilityLabel(hotkey), () => execute(hotkey.Command));
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            double size = ToolbarFlowLayout.CommandSize;
            button.Frame = new CGRect(0, 0, size, size);
            if (ratingCommand && !clearRating)
                button.AddSubview(new RatingDigitLabel(hotkey.Command[9..], ToolbarFlowLayout.CommandSize));
            commandGroup.AddSubview(button);
            _commands.Add(hotkey.Command, button);
        }
        if (commandGroup.Subviews.Length > 0) AddGroup(commandGroup, ControlBarGroupKind.Command, commandGroup.Subviews.Length);
        foreach (var hotkey in main.Settings.Hotkeys)
        {
            _hotkeys.Add(hotkey);
            hotkey.PropertyChanged += OnHotkeyChanged;
        }
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
        Frame = new CGRect(0, 0,
            Math.Max((double)viewport.Width, (double)measured.Width),
            Math.Max((double)viewport.Height, (double)measured.Height));
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

    /// <summary>登记分组及其类型/子项数，不把组内按钮摊平成可任意断开的列表。</summary>
    /// <param name="group">已填好子控件的分组视图。</param>
    /// <param name="kind">分组类型，决定共享布局口径。</param>
    /// <param name="count">分组子项数（星级组按 0 计，尺寸取控件自身口径）。</param>
    private void AddGroup(UIView group, ControlBarGroupKind kind, int count)
    {
        _groupViews.Add(group);
        _groupSpecs.Add((kind, count));
        AddSubview(group);
    }

    /// <summary>按方向求分组尺寸、整组换行并把分组与子控件落成 frame，返回文档尺寸。</summary>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="availableMain">主轴可用长度。</param>
    /// <returns>文档尺寸。</returns>
    private CGSize Layout(bool horizontal, double availableMain)
    {
        _horizontal = horizontal;
        double main = Math.Max(1, availableMain - ToolbarFlowLayout.Padding * 2);
        var sizes = Sizes(main);
        var lines = ToolbarFlowLayout.PackLines(sizes, main);
        double cross = ToolbarFlowLayout.Padding;
        foreach (var line in lines)
        {
            double lineCross = ToolbarFlowLayout.LineCross(sizes, line);
            double start = ToolbarFlowLayout.LineStart(availableMain, ToolbarFlowLayout.LineLength(sizes, line));
            foreach (int index in line)
            {
                var size = sizes[index];
                _groupViews[index].Frame = horizontal
                    ? new CGRect(start, cross, size.Main, lineCross)
                    : new CGRect(cross, start, lineCross, size.Main);
                LayoutChildren(index, horizontal, lineCross);
                start += size.Main + ToolbarFlowLayout.GroupSpacing;
            }
            cross += lineCross + ToolbarFlowLayout.GroupSpacing;
        }
        var document = ToolbarFlowLayout.DocumentSize(sizes, horizontal, lines);
        return new CGSize(document.Width, document.Height);
    }

    /// <summary>按分组类型求共享尺寸：EXIF 按块尺寸、星级按可用主轴、命令按圆按钮数与间距。</summary>
    /// <param name="availableMain">扣掉外边距后的主轴可用长度。</param>
    /// <returns>与分组顺序一致的尺寸。</returns>
    private List<ToolbarGroupSize> Sizes(double availableMain)
    {
        var sizes = new List<ToolbarGroupSize>(_groupSpecs.Count);
        foreach (var (kind, count) in _groupSpecs)
        {
            sizes.Add(kind switch
            {
                ControlBarGroupKind.Exif => ToolbarFlowLayout.Exif(count, _horizontal),
                ControlBarGroupKind.Rating => ToolbarFlowLayout.Rating(availableMain),
                _ => ToolbarFlowLayout.Commands(count),
            });
        }
        return sizes;
    }

    /// <summary>分组内子控件落位：EXIF 块与星级控件就是分组视图，命令圆按钮按共享偏移排在主轴、交叉轴居中。</summary>
    /// <param name="index">分组顺序索引。</param>
    /// <param name="horizontal">是否横向排列。</param>
    /// <param name="lineCross">所在行的交叉轴长度。</param>
    private void LayoutChildren(int index, bool horizontal, double lineCross)
    {
        var kind = _groupSpecs[index].Kind;
        var group = _groupViews[index];
        if (kind is ControlBarGroupKind.Rating or ControlBarGroupKind.Exif)
        {
            // EXIF 块按整组矩形排项、星级控件自绘胶囊与星排：分组视图自己消费方向，不再有子控件偏移。
            if (kind == ControlBarGroupKind.Rating) _ratingControl.Horizontal = horizontal;
            else ((iOSExifBlock)group).Horizontal = horizontal;
            return;
        }
        var children = group.Subviews;
        double size = ToolbarFlowLayout.CommandSize;
        for (int child = 0; child < children.Length; child++)
        {
            double offset = ToolbarFlowLayout.CommandOffset(child);
            children[child].Frame = horizontal
                ? new CGRect(offset, (lineCross - size) / 2, size, size)
                : new CGRect((lineCross - size) / 2, offset, size, size);
        }
    }

    /// <summary>
    /// 重建或关闭时移除并释放分组、解除模型订阅；常驻星级控件只从视图树摘下，随控制栏一起复用。
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
            if (!ReferenceEquals(group, _ratingControl)) group.Dispose();
        }
        _groupViews.Clear();
        _groupSpecs.Clear();
        _exifGroup = null;
        _commands.Clear();
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
