using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 控制栏（[TOOLBAR-01]）：EXIF 信息块 / 评分 / 命令三个整体分组，分组尺寸、按空间整组换行或换列、
/// 行内居中与交叉轴拉伸全部由共享 <see cref="ControlBarLayout"/> 计算，本类只落 UIKit frame 与系统符号。
/// 显隐与顺序来自共享设置；评分方向随布局方向（横排 0→5、竖排 5→0）。
/// 分析栏 / 增强预览 / 工具页全部可用：命令按钮由共享设置生成，EXIF 信息块点击打开工具页。
/// </summary>
internal sealed class iOSControlBar : UIView
{
    private readonly Action _changed;
    private readonly List<UIView> _groups = [];
    private readonly List<ControlBarLayout.GroupSpec> _specs = [];
    private readonly List<SettingsViewModel.HotkeyItem> _hotkeys = [];
    private readonly Dictionary<string, UIButton> _commands = new(StringComparer.Ordinal);
    private readonly List<UIButton> _ratings = [];
    private UIView? _exifGroup;
    private Action? _openTools;
    private bool _horizontal;
    /// <summary>评分组 0 号按钮当前字形（RATING-04：冲突警示三角 / 非冲突清零叉）。</summary>
    private string _ratingClearSymbol = "xmark";

    /// <summary>业务分组视图（EXIF / 评分 / 命令，按顺序）。</summary>
    internal IReadOnlyList<UIView> Groups => _groups;
    /// <summary>命令标识与原生按钮一一对应。</summary>
    internal IReadOnlyDictionary<string, UIButton> Commands => _commands;
    /// <summary>评分按钮按星级索引（视觉顺序由布局方向决定）。</summary>
    internal IReadOnlyList<UIButton> Ratings => _ratings;
    /// <summary>EXIF 信息块分组（点击打开工具页；验收核对入口）。</summary>
    internal UIView? ExifGroup => _exifGroup;
    /// <summary>评分组 0 号按钮（验收读取冲突字形与状态）。</summary>
    internal UIButton? RatingClearButton => _ratings.Count > 0 ? _ratings[0] : null;
    /// <summary>评分组 0 号按钮当前字形名称（冲突 <c>exclamationmark.triangle.fill</c>，否则 <c>xmark</c>）。</summary>
    internal string RatingClearSymbol => _ratingClearSymbol;

    /// <summary>EXIF 信息块点按入口（识别器与验收共用）：打开工具页。</summary>
    internal void TapExifGroup() => _openTools?.Invoke();

    /// <summary>建立深色控制文档。</summary>
    /// <param name="changed">设置变化时请求宿主合并刷新。</param>
    public iOSControlBar(Action changed) : base(CGRect.Empty)
    {
        _changed = changed;
        // SHELL-01：控制栏底色与窗口/栏位同一令牌（#111111）。
        BackgroundColor = iOSColors.FromToken(DesignTokens.ColorBarBackground);
    }

    /// <summary>是否提供该命令的入口：分析栏 / 增强预览 / 其余共享命令都有原生实现。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>是否有可用实现。</returns>
    internal static bool IsCommandAvailable(string command) => true;

    /// <summary>按共享设置重建三组控件：保留显隐/顺序、缺少照片时的禁用态与编号评分命令的星数语义。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行入口（与 Mac 同一份命令语义）。</param>
    /// <param name="rate">评分写回入口。</param>
    /// <param name="openTools">EXIF 信息块点击（打开工具页）；null 表示本壳无工具页。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action<int> rate, Action? openTools = null,
        Action<UIButton>? showSources = null)
    {
        ClearGroups();
        _openTools = openTools;
        var entries = main.ControlVM.EnabledExifItems;
        if (entries.Count > 0)
        {
            var exif = new UIView();
            exif.BackgroundColor = UIColor.Clear;
            foreach (var entry in entries) exif.AddSubview(new ExifTile(entry));
            var tap = new UITapGestureRecognizer(_ => TapExifGroup());
            exif.AddGestureRecognizer(tap);
            exif.UserInteractionEnabled = true;
            exif.AccessibilityLabel = "EXIF 信息，点击打开工具";
            _exifGroup = exif;
            AddGroup(exif, ControlBarGroupKind.Exif);
        }
        if (main.Settings.ShowRating)
        {
            var ratingGroup = new UIView();
            // TOOLBAR-10：EXIF/评分块统一取 color.toolbar.block.background（#222222）。
            ratingGroup.BackgroundColor = iOSColors.FromToken(DesignTokens.ColorToolbarBlockBackground);
            ratingGroup.Layer.CornerRadius = (nfloat)DesignTokens.ToolbarCornerRadius;
            for (int rating = 0; rating < 6; rating++)
            {
                int value = rating;
                bool active = rating > 0 && rating <= (main.CurrentFile?.Rating ?? 0);
                // RATING-04：组级来源冲突时 0 号按钮换成警示三角（几何/命中区不变），点击不写值。
                bool conflict = rating == 0 && (main.CurrentFile?.HasRatingConflict ?? false);
                if (rating == 0) _ratingClearSymbol = conflict ? "exclamationmark.triangle.fill" : "xmark";
                var button = new UIButton(new CGRect(0, 0, 30, 30));
                button.SetImage(iOSSymbols.Symbol(
                    rating == 0 ? _ratingClearSymbol : active ? "star.fill" : "star",
                    rating == 0
                        ? (conflict ? DesignTokens.ToolbarRatingGlyphFontSize : DesignTokens.ToolbarRatingClearGlyphFontSize)
                        : DesignTokens.ToolbarRatingGlyphFontSize), UIControlState.Normal);
                button.TintColor = rating == 0
                    ? (conflict ? iOSColors.FromToken(DesignTokens.ColorStarConflict) : UIColor.SecondaryLabel)
                    : active ? iOSColors.FromToken(DesignTokens.ColorStarOn) : UIColor.SecondaryLabel;
                button.Enabled = main.CurrentFile != null;
                button.AccessibilityLabel = rating == 0
                    ? (conflict ? "星级冲突" : "无星级")
                    : $"{rating} 星";
                button.TouchUpInside += (_, _) =>
                {
                    if (value == 0 && conflict)
                    {
                        showSources?.Invoke(button);
                        return;
                    }
                    rate(value);
                };
                ratingGroup.AddSubview(button);
                _ratings.Add(button);
            }
            AddGroup(ratingGroup, ControlBarGroupKind.Rating);
        }
        var commandGroup = new UIView();
        commandGroup.BackgroundColor = UIColor.Clear;
        foreach (var hotkey in main.ControlVM.EnabledControls)
        {
            if (!IsCommandAvailable(hotkey.Command)) continue;
            string? symbol = AppleSymbolNames.ForCommand(hotkey.Command);
            if (symbol == null) continue;
            bool ratingCommand = hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal);
            bool clearRating = hotkey.Command == "SetRating0";
            var button = new UIButton(new CGRect(0, 0, DesignTokens.ToolbarCommandButtonSize, DesignTokens.ToolbarCommandButtonSize));
            // TOOLBAR-04：评分快捷命令画实心星（清零为叉），星心再叠数字，不再与标题并排。
            button.SetImage(iOSSymbols.Symbol(ratingCommand && !clearRating ? "star.fill" : symbol,
                DesignTokens.ToolbarCommandGlyphFontSize), UIControlState.Normal);
            button.TintColor = UIColor.Label;
            // TOOLBAR-10：命令按钮 #333333 底 + #555555 描边，圆角只由控件自身裁切一次。
            button.BackgroundColor = iOSColors.FromToken(DesignTokens.ColorToolbarCommandBackground);
            button.Layer.BorderColor = iOSColors.FromToken(DesignTokens.ColorToolbarCommandBorder).CGColor;
            button.Layer.BorderWidth = (nfloat)DesignTokens.ShellDividerThickness;
            button.Layer.CornerRadius = (nfloat)DesignTokens.ToolbarCornerRadius;
            button.Layer.MasksToBounds = true;
            // TOOLBAR-04：读屏名称含命令名与当前主/次绑定（未设置的绑定不显示占位）。
            button.AccessibilityLabel = CommandAccessibilityLabel(hotkey);
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            button.TouchUpInside += (_, _) => execute(hotkey.Command);
            if (ratingCommand && !clearRating) button.AddSubview(new RatingDigitLabel(hotkey.Command[9..], (double)button.Bounds.Width));
            commandGroup.AddSubview(button);
            _commands.Add(hotkey.Command, button);
        }
        if (commandGroup.Subviews.Length > 0) AddGroup(commandGroup, ControlBarGroupKind.Command);
        foreach (var hotkey in main.Settings.Hotkeys)
        {
            _hotkeys.Add(hotkey);
            hotkey.PropertyChanged += OnHotkeyChanged;
        }
    }

    /// <summary>
    /// 命令按钮的读屏名称：命令名 + 实际主/次绑定；未设置的绑定不出现"未设置"占位（TOOLBAR-04）。
    /// </summary>
    /// <param name="hotkey">共享快捷键项。</param>
    /// <returns>无障碍名称。</returns>
    private static string CommandAccessibilityLabel(SettingsViewModel.HotkeyItem hotkey)
    {
        var parts = new List<string> { hotkey.Name };
        if (hotkey.PrimaryHotkey != null) parts.Add($"主 {hotkey.PrimaryHotkeyText}");
        if (hotkey.SecondaryHotkey != null) parts.Add($"次 {hotkey.SecondaryHotkeyText}");
        return string.Join("，", parts);
    }

    /// <summary>显隐、名称或快捷键变化时请求宿主合并刷新。</summary>
    /// <param name="sender">事件源（快捷键项）。</param>
    /// <param name="args">属性变化参数。</param>
    private void OnHotkeyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.HotkeyItem.IsDisplay) or nameof(SettingsViewModel.HotkeyItem.Name)
            or nameof(SettingsViewModel.HotkeyItem.PrimaryHotkey) or nameof(SettingsViewModel.HotkeyItem.SecondaryHotkey)) _changed();
    }

    /// <summary>登记非空业务分组及其类型，不把组内按钮摊平成可任意断开的列表。</summary>
    /// <param name="group">已填好子控件的分组视图。</param>
    /// <param name="kind">分组类型，决定共享布局口径。</param>
    private void AddGroup(UIView group, ControlBarGroupKind kind)
    {
        _groups.Add(group);
        _specs.Add(new ControlBarLayout.GroupSpec(kind, group.Subviews.Length));
        AddSubview(group);
    }

    /// <summary>按方向安排组内控件，EXIF 小块与评分块保留各自的内边距与顺序。</summary>
    /// <param name="horizontal">是否横向。</param>
    private void LayoutGroups(bool horizontal)
    {
        _horizontal = horizontal;
        for (int index = 0; index < _groups.Count; index++) LayoutGroup(index, horizontal);
    }

    /// <summary>把共享布局算得的子控件偏移落到 UIKit frame；交叉轴按所在行拉伸并居中。</summary>
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
        Frame = new CGRect(CGPoint.Empty, new CGSize(Math.Max((double)viewport.Width, (double)measured.Width), Math.Max((double)viewport.Height, (double)measured.Height)));
        var lines = ControlBarLayout.PackLines(_specs, _horizontal, Math.Max(1, availableMain - ControlBarLayout.Padding * 2));
        double cross = ControlBarLayout.Padding;
        foreach (var line in lines)
        {
            double lineCross = ControlBarLayout.LineCross(_specs, _horizontal, line);
            double main = Math.Max(ControlBarLayout.Padding, (availableMain - ControlBarLayout.LineLength(_specs, _horizontal, line)) / 2);
            foreach (int index in line)
            {
                LayoutGroup(index, _horizontal, lineCross);
                _groups[index].Frame = new CGRect(_horizontal ? main : cross, _horizontal ? cross : main,
                    _groups[index].Frame.Width, _groups[index].Frame.Height);
                main += ControlBarLayout.MainLength(_specs[index], _horizontal) + ControlBarLayout.GroupSpacing;
            }
            cross += lineCross + ControlBarLayout.GroupSpacing;
        }
    }

    /// <summary>
    /// 重建或关闭时移除并释放控件、解除模型订阅。
    /// <c>UIView.Dispose</c> 只释放托管句柄，原生视图仍被父视图持有并留在视图树里；
    /// 必须先 <c>RemoveFromSuperview</c>，否则每次设置回写都会把新分组叠在旧分组上（重叠且不可用）。
    /// </summary>
    private void ClearGroups()
    {
        foreach (var hotkey in _hotkeys) hotkey.PropertyChanged -= OnHotkeyChanged;
        _hotkeys.Clear();
        foreach (var group in _groups)
        {
            group.RemoveFromSuperview();
            group.Dispose();
        }
        _groups.Clear();
        _exifGroup = null;
        _specs.Clear();
        _commands.Clear();
        _ratings.Clear();
        _ratingClearSymbol = "xmark";
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

    /// <summary>EXIF 小块（50×40）：次要标签 + 强调值，超宽内容尾部截断。</summary>
    private sealed class ExifTile : UIView
    {
        /// <summary>以 50×40 参考规模创建信息块。</summary>
        /// <param name="entry">共享 EXIF 展示条目。</param>
        internal ExifTile(ExifDisplayEntry entry)
            : base(new CGRect(0, 0, DesignTokens.ToolbarExifBlockWidth, DesignTokens.ToolbarExifBlockHeight))
        {
            BackgroundColor = iOSColors.FromToken(DesignTokens.ColorToolbarBlockBackground);
            Layer.CornerRadius = (nfloat)DesignTokens.ToolbarCornerRadius;
            AccessibilityLabel = $"{entry.DisplayName} {entry.DisplayValue}";
            AddSubview(Text(entry.DisplayName, new CGRect(0, 4, DesignTokens.ToolbarExifBlockWidth, 13),
                DesignTokens.ToolbarExifLabelFontSize, false));
            AddSubview(Text(entry.DisplayValue, new CGRect(0, 19, DesignTokens.ToolbarExifBlockWidth, 17),
                DesignTokens.ToolbarExifValueFontSize, true));
        }

        /// <summary>生成居中原生标签，值使用较高字重。</summary>
        /// <param name="value">文本。</param>
        /// <param name="frame">frame。</param>
        /// <param name="size">字号。</param>
        /// <param name="emphasized">是否强调（数值）。</param>
        /// <returns>配置完成的标签。</returns>
        private static UILabel Text(string value, CGRect frame, double size, bool emphasized) => new(frame)
        {
            Text = value,
            Font = emphasized ? UIFont.BoldSystemFontOfSize((nfloat)size) : UIFont.SystemFontOfSize((nfloat)size),
            TextColor = emphasized ? UIColor.Label : UIColor.SecondaryLabel,
            TextAlignment = UITextAlignment.Center,
            LineBreakMode = UILineBreakMode.TailTruncation,
            BackgroundColor = UIColor.Clear,
        };
    }
}
