using Foundation;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 控制栏功能行的主/次快捷键按钮组（与 Mac 的 <c>HotkeyRow</c> 同一行布局）：
/// 两个 108×28 的灰底按钮显示当前手势文本，空槽「未设置」，录制态「按下按键…」，
/// 冲突槽位橙色并在读屏值追加「冲突」。长按/辅助点按弹出「清空」菜单，不增加可见控件。
/// </summary>
internal sealed class iOSHotkeyBindingButtons : UIView
{
    /// <summary>单个按钮宽度（与 Mac 的录制按钮一致）。</summary>
    private static nfloat ButtonWidth => 108;

    /// <summary>按钮高度。</summary>
    private static nfloat ButtonHeight => 28;

    /// <summary>两个按钮之间的间距。</summary>
    private static nfloat ButtonSpacing => 8;

    private readonly SettingsViewModel.HotkeyItem _item;
    private readonly iOSHotkeyRecorder _recorder;
    private readonly UIButton _primary;
    private readonly UIButton _secondary;
    private readonly bool[] _tinted = new bool[2];

    /// <summary>建立主/次两个真实按钮并把「清空」挂成菜单。</summary>
    /// <param name="item">当前命令。</param>
    /// <param name="recorder">快捷键录制器。</param>
    /// <param name="beginRecording">点击按钮开始录制（命令、是否次槽位）。</param>
    internal iOSHotkeyBindingButtons(SettingsViewModel.HotkeyItem item, iOSHotkeyRecorder recorder,
        Action<SettingsViewModel.HotkeyItem, bool> beginRecording)
    {
        _item = item;
        _recorder = recorder;
        TranslatesAutoresizingMaskIntoConstraints = false;
        IsAccessibilityElement = false;

        _primary = CreateButton(false, beginRecording);
        _secondary = CreateButton(true, beginRecording);

        var stack = new UIStackView([_primary, _secondary])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = ButtonSpacing,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            WidthAnchor.ConstraintEqualTo(ButtonWidth * 2 + ButtonSpacing),
            HeightAnchor.ConstraintEqualTo(ButtonHeight),
            stack.TopAnchor.ConstraintEqualTo(TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            stack.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _primary.WidthAnchor.ConstraintEqualTo(ButtonWidth),
            _secondary.WidthAnchor.ConstraintEqualTo(ButtonWidth),
            _primary.HeightAnchor.ConstraintEqualTo(ButtonHeight),
            _secondary.HeightAnchor.ConstraintEqualTo(ButtonHeight),
        ]);
        Update();
    }

    /// <summary>主槽位按钮（验收读取）。</summary>
    internal UIButton PrimaryButton => _primary;

    /// <summary>次槽位按钮（验收读取）。</summary>
    internal UIButton SecondaryButton => _secondary;

    /// <summary>菜单「清空」的同一处理入口（验收触发与菜单动作共用）。</summary>
    internal Action<bool> ClearAction => Clear;

    /// <summary>指定槽位当前是否使用录制配色（验收读取）。</summary>
    internal bool IsTinted(bool secondary) => _tinted[secondary ? 1 : 0];

    /// <summary>按录制态与冲突状态刷新两个按钮的标题、配色与读屏值。</summary>
    internal void Update()
    {
        Apply(_primary, false);
        Apply(_secondary, true);
    }

    /// <summary>外设是否连接：纯触控时整组隐藏并移出无障碍树。</summary>
    /// <param name="external">是否存在键鼠/触控板。</param>
    internal void SetExternal(bool external)
    {
        Hidden = !external;
        AccessibilityElementsHidden = !external;
        _primary.IsAccessibilityElement = external;
        _secondary.IsAccessibilityElement = external;
    }

    private UIButton CreateButton(bool secondary, Action<SettingsViewModel.HotkeyItem, bool> beginRecording)
    {
        var button = new UIButton(UIButtonType.System)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            AccessibilityLabel = secondary ? "次快捷键" : "主快捷键",
        };
        button.TouchUpInside += (_, _) => beginRecording(_item, secondary);

        var clear = UIAction.Create("清空", null, null, _ => Clear(secondary));
        clear.Attributes = UIMenuElementAttributes.Destructive;
        button.Menu = UIMenu.Create([(UIMenuElement)clear]);
        return button;
    }

    private void Clear(bool secondary) => _recorder.Clear(_item, secondary);

    private void Apply(UIButton button, bool secondary)
    {
        var gesture = secondary ? _item.SecondaryHotkey : _item.PrimaryHotkey;
        bool conflict = gesture != null && (secondary ? _item.HasSecondaryConflict : _item.HasPrimaryConflict);
        bool recording = _recorder.IsRecordingSlot(_item, secondary);
        string title = recording ? "按下按键…" : _recorder.Display(gesture);

        var configuration = recording
            ? UIButtonConfiguration.TintedButtonConfiguration
            : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(2, 6, 2, 6);
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var attributes = new UIStringAttributes { Font = UIFont.SystemFontOfSize((nfloat)12.5) };
        if (conflict) attributes.ForegroundColor = UIColor.SystemOrange;
        configuration.AttributedTitle = new NSAttributedString(title, attributes);
        if (conflict) configuration.BaseForegroundColor = UIColor.SystemOrange;
        button.Configuration = configuration;
        _tinted[secondary ? 1 : 0] = recording;
        button.AccessibilityValue = conflict ? title + "，冲突" : title;
    }
}
