using CoreGraphics;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 手机形态可选命令组（SHELL-08／SHELL-09／TOOLBAR-04）：一个交互式系统玻璃胶囊承载零至三段独立动作按钮，
/// 竖屏从左到右、横屏从上到下；每段是可聚焦、可点按、带读屏名称的系统按钮，不使用表达互斥选择的分段控件。
/// 零项时整组隐藏，一项时胶囊即圆形，两至三项共用同一胶囊与同一次系统玻璃效果。
/// <para>按钮按命令标识复用：排序、勾选或绑定改写只刷新读屏名称、图标与可用态，不换实例，
/// 避免中断系统正在进行的按压/融合动画；雾天增强的强调态落在当前承载该命令的按钮上，不假设固定段。</para>
/// </summary>
internal sealed class iOSPhoneCommandGroup : UIVisualEffectView
{
    private readonly Dictionary<string, UIButton> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (UIButton Button, bool Emphasized)> _emphasis = new(StringComparer.Ordinal);
    private readonly List<UIButton> _order = [];
    private bool _vertical;

    /// <summary>建立共享交互式玻璃胶囊（与手机上下张、筛选入口同一种系统玻璃效果）。</summary>
    internal iOSPhoneCommandGroup() : base(new UIGlassEffect { Interactive = true })
    {
        CornerConfiguration = UICornerConfiguration.CreateCapsule();
        BackgroundColor = UIColor.Clear;
    }

    /// <summary>各段按钮实例，顺序即生效命令顺序。</summary>
    internal IReadOnlyList<UIButton> Buttons => _order;

    /// <summary>命令标识与按钮一一对应（验收读取）。</summary>
    internal IReadOnlyDictionary<string, UIButton> Commands => _commands;

    /// <summary>合体组共用单一系统玻璃背景。</summary>
    internal bool UsesSharedGlass => true;

    /// <summary>每段沿排布轴的边长；手机形态取 <see cref="DesignTokens.ShellPhoneCommandSize"/>。</summary>
    internal double SegmentLength { get; set; } = DesignTokens.ShellPhoneCommandSize;

    /// <summary>是否纵向排布（手机横屏控制列）；否则横向（手机竖屏底行）。</summary>
    internal bool Vertical
    {
        get => _vertical;
        set
        {
            if (_vertical == value) return;
            _vertical = value;
            SetNeedsLayout();
        }
    }

    /// <summary>
    /// 按生效命令同步按钮：同名命令保留实例（只刷新读屏名称与可用态），新命令创建并追加，
    /// 不再生效的命令摘除。命令标识由共享 <c>PhoneControlCommands</c> 给出，壳不另维护白名单。
    /// </summary>
    /// <param name="commands">生效命令标识，顺序即排布顺序。</param>
    /// <param name="labelFor">命令的读屏名称（名称 + 实际主/次绑定）。</param>
    /// <param name="enabledFor">命令当前是否可执行（无照片时除打开文件外禁用）。</param>
    /// <param name="execute">命令执行入口（与宽形态控制栏、快捷键同一入口）。</param>
    internal void Sync(IReadOnlyList<string> commands, Func<string, string> labelFor,
        Func<string, bool> enabledFor, Action<string> execute)
    {
        var desired = new List<UIButton>(commands.Count);
        foreach (string command in commands)
        {
            if (!_commands.TryGetValue(command, out var button))
            {
                string symbol = AppleSymbolNames.ForCommand(command)
                    ?? throw new InvalidOperationException($"未定义原生命令图标：{command}");
                // TOOLBAR-04：评分快捷命令画实心星（清零为叉），星心再叠数字。
                bool ratingCommand = command.StartsWith("SetRating", StringComparison.Ordinal);
                bool clearRating = command == "SetRating0";
                button = iOSGlassButtons.Segment(ratingCommand && !clearRating ? "star.fill" : symbol,
                    labelFor(command), () => execute(command));
                if (ratingCommand && !clearRating)
                    button.AddSubview(new iOSRatingDigitLabel(command[9..], SegmentLength));
                ContentView.AddSubview(button);
                _commands.Add(command, button);
            }
            button.AccessibilityLabel = labelFor(command);
            button.Enabled = enabledFor(command);
            if (button.Superview == null) ContentView.AddSubview(button);
            desired.Add(button);
        }

        foreach (var (command, button) in _commands.ToArray())
        {
            if (desired.Contains(button)) continue;
            button.RemoveFromSuperview();
            button.Dispose();
            _commands.Remove(command);
            _emphasis.Remove(command);
        }

        bool orderChanged = !_order.SequenceEqual(desired);
        _order.Clear();
        _order.AddRange(desired);
        // 子视图顺序也要跟随生效顺序：系统玻璃容器按内容区子视图顺序取样与融合；顺序未变时不触碰视图树。
        if (orderChanged)
            foreach (var button in desired) ContentView.AddSubview(button);
        SetNeedsLayout();
    }

    /// <summary>改写承载指定命令的按钮的强调态（不改变几何与命中区；命令不在组内时无操作）。</summary>
    /// <param name="command">共享命令标识。</param>
    /// <param name="emphasized">是否用系统强调色。</param>
    /// <param name="accent">系统强调色。</param>
    internal void SetEmphasized(string command, bool emphasized, UIColor accent)
    {
        if (!_commands.TryGetValue(command, out var button)) return;
        // 布局会在每次重排后补一次强调态：状态与按钮实例都未变时不再改写配置，避免打断系统按压缩放。
        if (_emphasis.TryGetValue(command, out var state)
            && ReferenceEquals(state.Button, button) && state.Emphasized == emphasized) return;
        iOSGlassButtons.SetEmphasized(button, emphasized, accent);
        _emphasis[command] = (button, emphasized);
    }

    /// <summary>各段按生效顺序贴胶囊内缘排布；组容器的 frame 由宿主按共享槽位写入。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        double length = SegmentLength;
        for (int index = 0; index < _order.Count; index++)
        {
            iOSViewGeometry.SetRectIfNeeded(_order[index], Vertical
                ? new CGRect(0, index * length, Bounds.Width, length)
                : new CGRect(index * length, 0, length, Bounds.Height));
        }
    }
}
