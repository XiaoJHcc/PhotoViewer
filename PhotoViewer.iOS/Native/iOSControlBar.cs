using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 控制栏（[TOOLBAR-01]）：EXIF 信息块 / 评分 / 命令三个整体分组，分组尺寸、按空间整组换行或换列、
/// 行内居中与交叉轴拉伸全部由共享 <see cref="ControlBarLayout"/> 计算，本类只落 UIKit frame 与系统符号。
/// 显隐与顺序来自共享设置；评分方向随布局方向（横排 0→5、竖排 5→0）。
/// I5 起分析栏 / 增强预览 / 工具页全部可用：命令按钮由共享设置生成，EXIF 信息块点击打开工具页。
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

    /// <summary>业务分组视图（EXIF / 评分 / 命令，按顺序）。</summary>
    internal IReadOnlyList<UIView> Groups => _groups;
    /// <summary>命令标识与原生按钮一一对应。</summary>
    internal IReadOnlyDictionary<string, UIButton> Commands => _commands;
    /// <summary>评分按钮按星级索引（视觉顺序由布局方向决定）。</summary>
    internal IReadOnlyList<UIButton> Ratings => _ratings;
    /// <summary>EXIF 信息块分组（点击打开工具页；验收核对入口）。</summary>
    internal UIView? ExifGroup => _exifGroup;

    /// <summary>EXIF 信息块点按入口（识别器与验收共用）：打开工具页。</summary>
    internal void TapExifGroup() => _openTools?.Invoke();

    /// <summary>建立深色控制文档。</summary>
    /// <param name="changed">设置变化时请求宿主合并刷新。</param>
    public iOSControlBar(Action changed) : base(CGRect.Empty)
    {
        _changed = changed;
        BackgroundColor = UIColor.FromWhiteAlpha(.07f, 1);
    }

    /// <summary>是否提供该命令的入口：I5 起分析栏 / 增强预览 / 其余共享命令都有原生实现。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <returns>是否有可用实现。</returns>
    internal static bool IsCommandAvailable(string command) => true;

    /// <summary>按共享设置重建三组控件：保留显隐/顺序、缺少照片时的禁用态与编号评分命令的星数语义。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="execute">命令执行入口（与 Mac 同一份命令语义）。</param>
    /// <param name="rate">评分写回入口。</param>
    /// <param name="openTools">EXIF 信息块点击（打开工具页）；null 表示本壳无工具页。</param>
    internal void Rebuild(MainViewModel main, Action<string> execute, Action<int> rate, Action? openTools = null)
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
            ratingGroup.BackgroundColor = UIColor.FromWhiteAlpha(2f / 15, 1);
            ratingGroup.Layer.CornerRadius = 6;
            for (int rating = 0; rating < 6; rating++)
            {
                int value = rating;
                bool active = rating > 0 && rating <= (main.CurrentFile?.Rating ?? 0);
                var button = new UIButton(new CGRect(0, 0, 30, 30));
                button.SetImage(iOSSymbols.Symbol(rating == 0 ? "xmark" : active ? "star.fill" : "star", rating == 0 ? 12 : 16), UIControlState.Normal);
                button.TintColor = active ? UIColor.FromRGB(255, 210, 20) : UIColor.SecondaryLabel;
                button.Enabled = main.CurrentFile != null;
                button.AccessibilityLabel = rating == 0 ? "无星级" : $"{rating} 星";
                button.TouchUpInside += (_, _) => rate(value);
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
            var button = new UIButton(new CGRect(0, 0, 40, 40));
            button.SetImage(iOSSymbols.Symbol(symbol, 18), UIControlState.Normal);
            button.TintColor = UIColor.Label;
            button.BackgroundColor = UIColor.FromRGB(0x33, 0x33, 0x33);
            button.Layer.CornerRadius = 6;
            button.Layer.MasksToBounds = true;
            button.AccessibilityLabel = hotkey.Name;
            button.Enabled = hotkey.Command == "Open" || main.CurrentFile != null;
            button.TouchUpInside += (_, _) => execute(hotkey.Command);
            if (hotkey.Command.StartsWith("SetRating", StringComparison.Ordinal))
            {
                button.SetTitle(hotkey.Command[9..], UIControlState.Normal);
                button.SetTitleColor(UIColor.Label, UIControlState.Normal);
                button.TitleLabel!.Font = UIFont.SystemFontOfSize(11);
            }
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
    }

    /// <summary>销毁时解除快捷键订阅并释放所有分组。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearGroups();
        base.Dispose(disposing);
    }

    /// <summary>EXIF 小块（50×40）：次要标签 + 强调值，超宽内容尾部截断。</summary>
    private sealed class ExifTile : UIView
    {
        /// <summary>以 50×40 参考规模创建信息块。</summary>
        /// <param name="entry">共享 EXIF 展示条目。</param>
        internal ExifTile(ExifDisplayEntry entry) : base(new CGRect(0, 0, 50, 40))
        {
            BackgroundColor = UIColor.FromWhiteAlpha(2f / 15, 1);
            Layer.CornerRadius = 6;
            AccessibilityLabel = $"{entry.DisplayName} {entry.DisplayValue}";
            AddSubview(Text(entry.DisplayName, new CGRect(0, 4, 50, 13), 10, false));
            AddSubview(Text(entry.DisplayValue, new CGRect(0, 19, 50, 17), 12, true));
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
