using System;
using System.Collections.Generic;
using System.Text;

namespace PhotoViewer.Core.Input;

/// <summary>
/// 平台中立的修饰键标志。<b>数值与 Avalonia.Input.KeyModifiers 完全一致</b>,
/// 设置 JSON 中以数值持久化,改动数值会破坏用户既有配置。
/// </summary>
[Flags]
public enum GestureModifiers
{
    /// <summary>无修饰键。</summary>
    None = 0,
    /// <summary>Alt。</summary>
    Alt = 1,
    /// <summary>Ctrl。</summary>
    Control = 2,
    /// <summary>Shift。</summary>
    Shift = 4,
    /// <summary>Meta(macOS Cmd / Windows Win)。</summary>
    Meta = 8,
}

/// <summary>
/// 平台中立的键枚举。<b>成员名与 Avalonia.Input.Key 逐一对应</b>(UI 层按名称互转,
/// 设置 JSON 以名称字符串持久化);数值不保证与 Avalonia 相同,禁止按数值转换。
/// </summary>
public enum GestureKey
{
    None, Cancel, Back, Tab, LineFeed, Clear, Return, Enter, Pause, CapsLock, Capital,
    HangulMode, KanaMode, JunjaMode, FinalMode, KanjiMode, HanjaMode, Escape, ImeConvert,
    ImeNonConvert, ImeAccept, ImeModeChange, Space, PageUp, Prior, PageDown, Next, End, Home,
    Left, Up, Right, Down, Select, Print, Execute, Snapshot, PrintScreen, Insert, Delete, Help,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    LWin, RWin, Apps, Sleep,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply, Add, Separator, Subtract, Decimal, Divide,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    NumLock, Scroll, LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
    BrowserBack, BrowserForward, BrowserRefresh, BrowserStop, BrowserSearch, BrowserFavorites, BrowserHome,
    VolumeMute, VolumeDown, VolumeUp, MediaNextTrack, MediaPreviousTrack, MediaStop, MediaPlayPause,
    LaunchMail, SelectMedia, LaunchApplication1, LaunchApplication2,
    OemSemicolon, Oem1, OemPlus, OemComma, OemMinus, OemPeriod, OemQuestion, Oem2, OemTilde, Oem3,
    AbntC1, AbntC2, OemOpenBrackets, Oem4, OemPipe, Oem5, OemCloseBrackets, Oem6, OemQuotes, Oem7, Oem8,
    OemBackslash, Oem102, ImeProcessed, System, OemAttn, DbeAlphanumeric, OemFinish, DbeKatakana,
    DbeHiragana, OemCopy, DbeSbcsChar, OemAuto, DbeDbcsChar, OemEnlw, OemBackTab, DbeRoman, DbeNoRoman,
    Attn, CrSel, DbeEnterWordRegisterMode, ExSel, DbeEnterImeConfigureMode, EraseEof, DbeFlushString,
    Play, DbeCodeInput, DbeNoCodeInput, Zoom, NoName, DbeDetermineString, DbeEnterDialogConversionMode,
    Pa1, OemClear, DeadCharProcessed, FnLeftArrow, FnRightArrow, FnUpArrow, FnDownArrow,
    MediaHome, MediaChannelList, MediaChannelRaise, MediaChannelLower, MediaRecord,
    MediaRed, MediaGreen, MediaYellow, MediaBlue, MediaMenu, MediaMore, MediaOption,
    MediaInfo, MediaSearch, MediaSubtitle, MediaTvGuide, MediaPreviousChannel,
}

/// <summary>
/// 鼠标动作(点击/滚轮)。
/// </summary>
public enum MouseAction
{
    /// <summary>左键点击。</summary>
    LeftClick,
    /// <summary>右键点击。</summary>
    RightClick,
    /// <summary>滚轮按压(中键)。</summary>
    MiddleClick,
    /// <summary>侧键 1。</summary>
    XButton1Click,
    /// <summary>侧键 2。</summary>
    XButton2Click,
    /// <summary>滚轮上。</summary>
    WheelUp,
    /// <summary>滚轮下。</summary>
    WheelDown,
}

/// <summary>
/// 键盘快捷键手势(平台中立)。
/// </summary>
public sealed class AppKeyGesture
{
    /// <summary>键。</summary>
    public GestureKey Key { get; }

    /// <summary>修饰键。</summary>
    public GestureModifiers Modifiers { get; }

    /// <summary>
    /// 创建键盘手势。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="modifiers">修饰键。</param>
    public AppKeyGesture(GestureKey key, GestureModifiers modifiers = GestureModifiers.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    /// <summary>
    /// 生成显示文本(与 Avalonia KeyGesture.ToString 同格式:修饰键 + 键名,如 "Ctrl+N")。
    /// </summary>
    /// <returns>显示文本。</returns>
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        void Plus() { if (sb.Length > 0) sb.Append('+'); }
        if (Modifiers.HasFlag(GestureModifiers.Control)) { Plus(); sb.Append("Ctrl"); }
        if (Modifiers.HasFlag(GestureModifiers.Alt)) { Plus(); sb.Append("Alt"); }
        if (Modifiers.HasFlag(GestureModifiers.Shift)) { Plus(); sb.Append("Shift"); }
        if (Modifiers.HasFlag(GestureModifiers.Meta)) { Plus(); sb.Append("Meta"); }
        Plus(); sb.Append(Key);
        return sb.ToString();
    }
}

/// <summary>
/// 鼠标手势(动作 + 修饰键)。
/// </summary>
public sealed class MouseGestureEx
{
    /// <summary>鼠标动作。</summary>
    public MouseAction Action { get; }

    /// <summary>修饰键。</summary>
    public GestureModifiers Modifiers { get; }

    /// <summary>
    /// 创建带修饰键的鼠标手势。
    /// </summary>
    /// <param name="action">鼠标动作。</param>
    /// <param name="modifiers">修饰键。</param>
    public MouseGestureEx(MouseAction action, GestureModifiers modifiers)
    {
        Action = action;
        Modifiers = modifiers;
    }

    /// <summary>
    /// 创建无修饰键的鼠标手势。
    /// </summary>
    /// <param name="action">鼠标动作。</param>
    public MouseGestureEx(MouseAction action)
    {
        Action = action;
        Modifiers = GestureModifiers.None;
    }

    /// <summary>
    /// 生成显示文本(如 "⌘ 滚轮上" / "左键")。
    /// </summary>
    /// <returns>显示文本。</returns>
    public string ToDisplayString()
    {
        var mods = Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(GestureModifiers.Shift)) parts.Add(AppleKeyboardMapping.GetShiftDisplay());
        if (mods.HasFlag(GestureModifiers.Control)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Control));
        if (mods.HasFlag(GestureModifiers.Alt)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Alt));
        if (mods.HasFlag(GestureModifiers.Meta)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Meta));

        string actionName = Action switch
        {
            MouseAction.LeftClick => "左键",
            MouseAction.RightClick => "右键",
            MouseAction.MiddleClick => "中键",
            MouseAction.XButton1Click => "侧键1",
            MouseAction.XButton2Click => "侧键2",
            MouseAction.WheelUp => "滚轮上",
            MouseAction.WheelDown => "滚轮下",
            _ => "鼠标"
        };

        parts.Add(actionName);
        return string.Join(" ", parts);
    }
}

/// <summary>
/// 通用手势:键盘或鼠标,二者居一。
/// </summary>
public sealed class AppGesture
{
    /// <summary>键盘手势(非 null 时 Mouse 为 null)。</summary>
    public AppKeyGesture? Key { get; }

    /// <summary>鼠标手势(非 null 时 Key 为 null)。</summary>
    public MouseGestureEx? Mouse { get; }

    private AppGesture(AppKeyGesture? key, MouseGestureEx? mouse)
    {
        Key = key;
        Mouse = mouse;
    }

    /// <summary>
    /// 由键盘手势创建。
    /// </summary>
    /// <param name="key">键盘手势。</param>
    public static AppGesture FromKey(AppKeyGesture key) => new AppGesture(key, null);

    /// <summary>
    /// 由鼠标手势创建。
    /// </summary>
    /// <param name="mouse">鼠标手势。</param>
    public static AppGesture FromMouse(MouseGestureEx mouse) => new AppGesture(null, mouse);

    /// <summary>
    /// 生成显示文本;未设置时返回"未设置"。
    /// </summary>
    /// <returns>显示文本。</returns>
    public string ToDisplayString()
    {
        if (Key != null)
        {
            return Key.ToDisplayString();
        }
        if (Mouse != null) return Mouse.ToDisplayString();
        return "未设置";
    }
}

/// <summary>
/// 苹果键盘映射目标。
/// </summary>
public enum AppleMappingTarget
{
    /// <summary>不映射。</summary>
    None,
    /// <summary>映射为 Ctrl。</summary>
    Ctrl,
    /// <summary>映射为 Alt。</summary>
    Alt,
    /// <summary>映射为 Win(Meta)。</summary>
    Win,
}

/// <summary>
/// 全局苹果键盘映射器:开启"使用苹果键盘"后,把 Cmd/Option/Ctrl 按配置映射到目标修饰键。
/// </summary>
public static class AppleKeyboardMapping
{
    /// <summary>是否启用苹果键盘映射。</summary>
    public static bool UseAppleKeyboard { get; set; }

    /// <summary>Cmd(Meta)的映射目标。</summary>
    public static AppleMappingTarget MapCommandTarget { get; set; }

    /// <summary>Option(Alt)的映射目标。</summary>
    public static AppleMappingTarget MapOptionTarget { get; set; }

    /// <summary>Ctrl 的映射目标。</summary>
    public static AppleMappingTarget MapControlTarget { get; set; }

    /// <summary>映射配置变化事件。</summary>
    public static event Action? MappingChanged;

    /// <summary>触发映射配置变化通知。</summary>
    public static void RaiseChanged() => MappingChanged?.Invoke();

    /// <summary>
    /// 运行时映射:把事件携带的修饰键按配置转换。
    /// </summary>
    /// <param name="mods">原始修饰键。</param>
    /// <returns>映射后的修饰键。</returns>
    public static GestureModifiers ApplyForRuntime(GestureModifiers mods)
        => ApplyForRuntime(mods, UseAppleKeyboard, MapCommandTarget, MapOptionTarget, MapControlTarget);

    /// <summary>用指定设置映射修饰键，供工作副本录制使用，不临时改写全局键盘配置。</summary>
    public static GestureModifiers ApplyForRuntime(GestureModifiers mods, bool useAppleKeyboard, AppleMappingTarget command, AppleMappingTarget option, AppleMappingTarget control)
    {
        if (!useAppleKeyboard) return mods;

        var result = mods;
        result = ApplySource(result, GestureModifiers.Meta, command); // Cmd
        result = ApplySource(result, GestureModifiers.Alt, option);  // Option
        result = ApplySource(result, GestureModifiers.Control, control); // Ctrl
        return result;
    }

    /// <summary>
    /// 捕获时映射(与运行时同口径)。
    /// </summary>
    /// <param name="mods">原始修饰键。</param>
    /// <returns>映射后的修饰键。</returns>
    public static GestureModifiers ApplyForCapture(GestureModifiers mods) => ApplyForRuntime(mods);

    /// <summary>
    /// 显示目标修饰键的文本(若有映射到此目标则按 Cmd ⌘ → Option ⌥ → Ctrl ⌃ 优先展示符号)。
    /// </summary>
    /// <param name="targetFlag">目标修饰键。</param>
    /// <returns>显示文本。</returns>
    public static string GetDisplayForModifier(GestureModifiers targetFlag)
    {
        string defaultName = targetFlag switch
        {
            GestureModifiers.Control => "Ctrl",
            GestureModifiers.Alt => "Alt",
            GestureModifiers.Meta => "Win",
            _ => targetFlag.ToString()
        };

        if (!UseAppleKeyboard) return defaultName;

        bool cmd = ToModifiers(MapCommandTarget) == targetFlag;
        bool opt = ToModifiers(MapOptionTarget) == targetFlag;
        bool ctl = ToModifiers(MapControlTarget) == targetFlag;

        if (cmd) return "⌘";
        if (opt) return "⌥";
        if (ctl) return "⌃";
        return defaultName;
    }

    /// <summary>
    /// Shift 的显示文本(开启苹果键盘时显示 ⇧)。
    /// </summary>
    /// <returns>显示文本。</returns>
    public static string GetShiftDisplay() => UseAppleKeyboard ? "⇧" : "Shift";

    /// <summary>把来源修饰键替换为目标映射。</summary>
    private static GestureModifiers ApplySource(GestureModifiers current, GestureModifiers sourceFlag, AppleMappingTarget target)
    {
        if (target == AppleMappingTarget.None) return current;
        if (!current.HasFlag(sourceFlag)) return current;

        current &= ~sourceFlag;                 // 去掉原来源键
        current |= ToModifiers(target);         // 添加目标键
        return current;
    }

    /// <summary>映射目标转修饰键标志。</summary>
    private static GestureModifiers ToModifiers(AppleMappingTarget target) => target switch
    {
        AppleMappingTarget.Ctrl => GestureModifiers.Control,
        AppleMappingTarget.Alt => GestureModifiers.Alt,
        AppleMappingTarget.Win => GestureModifiers.Meta,
        _ => 0
    };
}
