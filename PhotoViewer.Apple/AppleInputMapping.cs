using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Apple;

/// <summary>
/// 苹果平台原生事件与共享手势模型之间的单向映射，保留设置 JSON 的键名与修饰键口径。
/// 事件类型码与修饰键位一律使用 macOS/iOS 原生事件的公开数值（数值即契约，见各常量），
/// 具体事件对象的字段解包留在各平台头部。
/// </summary>
public static class AppleInputMapping
{
    /// <summary>左键按下（原生事件类型 1）。</summary>
    public const int LeftMouseDown = 1;
    /// <summary>右键按下（原生事件类型 3）。</summary>
    public const int RightMouseDown = 3;
    /// <summary>键盘按下（原生事件类型 10）。</summary>
    public const int KeyDown = 10;
    /// <summary>滚轮事件（原生事件类型 22）。</summary>
    public const int ScrollWheel = 22;
    /// <summary>中键/侧键按下（原生事件类型 25）。</summary>
    public const int OtherMouseDown = 25;

    /// <summary>Shift 修饰键位（1 &lt;&lt; 17）。</summary>
    public const ulong ShiftMask = 1UL << 17;
    /// <summary>Control 修饰键位（1 &lt;&lt; 18）。</summary>
    public const ulong ControlMask = 1UL << 18;
    /// <summary>Option/Alt 修饰键位（1 &lt;&lt; 19）。</summary>
    public const ulong AlternateMask = 1UL << 19;
    /// <summary>Command 修饰键位（1 &lt;&lt; 20）。</summary>
    public const ulong CommandMask = 1UL << 20;

    /// <summary>把原生事件字段还原为共享手势，供事件解包与跨语言录制复用同一映射。</summary>
    /// <param name="eventType">原生事件类型码。</param>
    /// <param name="keyCode">原生键码；键盘事件按它判定物理键位。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <param name="buttonNumber">鼠标按键编号。</param>
    /// <param name="scrollingDeltaY">滚轮纵向增量。</param>
    /// <param name="hasPreciseScrollingDeltas">是否为精确滚轮事件。</param>
    /// <param name="charactersIgnoringModifiers">忽略修饰键后的字符；仅保留原生调用契约，不参与键位判定。</param>
    /// <param name="captureSettings">录制所用的工作副本，决定苹果键盘映射口径。</param>
    /// <returns>匹配到的共享手势；不支持的事件返回 null。</returns>
    public static AppGesture? FromRaw(int eventType, ushort keyCode, ulong modifierFlags, nint buttonNumber, double scrollingDeltaY,
        bool hasPreciseScrollingDeltas, string? charactersIgnoringModifiers, SettingsViewModel? captureSettings = null)
    {
        GestureModifiers modifiers = DecodeModifiers(modifierFlags, captureSettings);
        if (eventType == ScrollWheel)
        {
            if ((hasPreciseScrollingDeltas && modifiers == 0) || scrollingDeltaY == 0) return null;
            return AppGesture.FromMouse(new MouseGestureEx(scrollingDeltaY > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, modifiers));
        }
        if (eventType is LeftMouseDown or RightMouseDown or OtherMouseDown)
        {
            MouseAction action = (int)buttonNumber switch
            {
                0 => MouseAction.LeftClick, 1 => MouseAction.RightClick, 2 => MouseAction.MiddleClick,
                3 => MouseAction.XButton1Click, _ => MouseAction.XButton2Click,
            };
            return AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
        }
        if (eventType != KeyDown) return null;
        var key = KeyFromRaw(keyCode);
        return key.HasValue ? AppGesture.FromKey(new AppKeyGesture(key.Value, modifiers)) : null;
    }

    /// <summary>
    /// iOS 硬件键盘按键 → 共享手势：<c>UIKey.KeyCode</c> 直接给出 HID usage 数值，
    /// 修饰键位与 macOS 的 <c>NSEventModifierFlags</c> 同值，故苹果键盘映射口径完全复用。
    /// </summary>
    /// <param name="usage">HID usage（USB HID Usage Tables 0x07 键盘页）。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <param name="captureSettings">录制所用的工作副本，决定苹果键盘映射口径。</param>
    /// <returns>匹配到的共享手势；未登记的 usage 返回 null。</returns>
    public static AppGesture? FromHidUsage(int usage, ulong modifierFlags, SettingsViewModel? captureSettings = null)
    {
        GestureKey? key = KeyFromHidUsage(usage);
        return key.HasValue ? AppGesture.FromKey(new AppKeyGesture(key.Value, DecodeModifiers(modifierFlags, captureSettings))) : null;
    }

    /// <summary>
    /// HID usage → 共享键映射表（纯数据）。逐条登记而不做区间算术，
    /// 避免依赖 <see cref="GestureKey"/> 的成员顺序；未登记返回 null，由调用方把按键交还系统。
    /// 字母/数字/标点只认物理位置；数字键盘的 ± 与主行 =/− 同键（才能命中默认缩放绑定），回车保持 Return。
    /// </summary>
    /// <param name="usage">HID usage 数值。</param>
    /// <returns>对应的共享键；未定义时为 null。</returns>
    public static GestureKey? KeyFromHidUsage(int usage) => usage switch
    {
        4 => GestureKey.A, 5 => GestureKey.B, 6 => GestureKey.C, 7 => GestureKey.D, 8 => GestureKey.E,
        9 => GestureKey.F, 10 => GestureKey.G, 11 => GestureKey.H, 12 => GestureKey.I, 13 => GestureKey.J,
        14 => GestureKey.K, 15 => GestureKey.L, 16 => GestureKey.M, 17 => GestureKey.N, 18 => GestureKey.O,
        19 => GestureKey.P, 20 => GestureKey.Q, 21 => GestureKey.R, 22 => GestureKey.S, 23 => GestureKey.T,
        24 => GestureKey.U, 25 => GestureKey.V, 26 => GestureKey.W, 27 => GestureKey.X, 28 => GestureKey.Y,
        29 => GestureKey.Z,
        30 => GestureKey.D1, 31 => GestureKey.D2, 32 => GestureKey.D3, 33 => GestureKey.D4, 34 => GestureKey.D5,
        35 => GestureKey.D6, 36 => GestureKey.D7, 37 => GestureKey.D8, 38 => GestureKey.D9, 39 => GestureKey.D0,
        40 => GestureKey.Return, 41 => GestureKey.Escape, 42 => GestureKey.Back, 43 => GestureKey.Tab,
        44 => GestureKey.Space,
        45 => GestureKey.OemMinus, 46 => GestureKey.OemPlus, 47 => GestureKey.OemOpenBrackets,
        48 => GestureKey.OemCloseBrackets, 49 => GestureKey.OemPipe,
        51 => GestureKey.OemSemicolon, 52 => GestureKey.OemQuotes, 53 => GestureKey.OemTilde,
        54 => GestureKey.OemComma, 55 => GestureKey.OemPeriod, 56 => GestureKey.OemQuestion,
        57 => GestureKey.CapsLock,
        58 => GestureKey.F1, 59 => GestureKey.F2, 60 => GestureKey.F3, 61 => GestureKey.F4,
        62 => GestureKey.F5, 63 => GestureKey.F6, 64 => GestureKey.F7, 65 => GestureKey.F8,
        66 => GestureKey.F9, 67 => GestureKey.F10, 68 => GestureKey.F11, 69 => GestureKey.F12,
        70 => GestureKey.PrintScreen, 71 => GestureKey.Scroll, 72 => GestureKey.Pause, 73 => GestureKey.Insert,
        74 => GestureKey.Home, 75 => GestureKey.PageUp, 76 => GestureKey.Delete, 77 => GestureKey.End,
        78 => GestureKey.PageDown,
        79 => GestureKey.Right, 80 => GestureKey.Left, 81 => GestureKey.Down, 82 => GestureKey.Up,
        83 => GestureKey.NumLock, 84 => GestureKey.Divide, 85 => GestureKey.Multiply, 86 => GestureKey.OemMinus,
        87 => GestureKey.OemPlus, 88 => GestureKey.Return,
        89 => GestureKey.NumPad1, 90 => GestureKey.NumPad2, 91 => GestureKey.NumPad3, 92 => GestureKey.NumPad4,
        93 => GestureKey.NumPad5, 94 => GestureKey.NumPad6, 95 => GestureKey.NumPad7, 96 => GestureKey.NumPad8,
        97 => GestureKey.NumPad9, 98 => GestureKey.NumPad0, 99 => GestureKey.Decimal,
        104 => GestureKey.F13, 105 => GestureKey.F14, 106 => GestureKey.F15, 107 => GestureKey.F16,
        108 => GestureKey.F17, 109 => GestureKey.F18, 110 => GestureKey.F19, 111 => GestureKey.F20,
        112 => GestureKey.F21, 113 => GestureKey.F22, 114 => GestureKey.F23, 115 => GestureKey.F24,
        224 => GestureKey.LeftCtrl, 225 => GestureKey.LeftShift, 226 => GestureKey.LeftAlt, 227 => GestureKey.LWin,
        228 => GestureKey.RightCtrl, 229 => GestureKey.RightShift, 230 => GestureKey.RightAlt, 231 => GestureKey.RWin,
        _ => null,
    };

    /// <summary>把原生修饰键掩码还原为共享修饰键（含苹果键盘映射，各事件类型共用一份）。</summary>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <param name="captureSettings">录制所用的工作副本；null 时用全局运行时映射。</param>
    /// <returns>映射后的共享修饰键。</returns>
    private static GestureModifiers DecodeModifiers(ulong modifierFlags, SettingsViewModel? captureSettings)
    {
        GestureModifiers modifiers = 0;
        if ((modifierFlags & ShiftMask) != 0) modifiers |= GestureModifiers.Shift;
        if ((modifierFlags & ControlMask) != 0) modifiers |= GestureModifiers.Control;
        if ((modifierFlags & AlternateMask) != 0) modifiers |= GestureModifiers.Alt;
        if ((modifierFlags & CommandMask) != 0) modifiers |= GestureModifiers.Meta;
        return captureSettings == null ? AppleKeyboardMapping.ApplyForRuntime(modifiers)
            : AppleKeyboardMapping.ApplyForRuntime(modifiers, captureSettings.UseAppleKeyboard, captureSettings.MapCommandTarget,
                captureSettings.MapOptionTarget, captureSettings.MapControlTarget);
    }

    /// <summary>
    /// macOS 虚拟键码 → 共享键的物理键位表。<paramref name="keyCode"/> 是唯一判据：
    /// 非 US 布局下即使 <c>CharactersIgnoringModifiers</c> 为空或是别的字符，字母、主行数字与常用标点
    /// 仍按硬件位置识别，与 iOS 硬件键盘的 HID usage 表同口径。
    /// </summary>
    /// <param name="keyCode">macOS 虚拟键码（<c>NSEvent.KeyCode</c>）。</param>
    /// <returns>匹配到的共享键；未登记返回 null，由调用方把按键交还系统。</returns>
    private static GestureKey? KeyFromRaw(ushort keyCode) => keyCode switch
    {
        // 字母行（物理位置，非字符）
        0 => GestureKey.A, 1 => GestureKey.S, 2 => GestureKey.D, 3 => GestureKey.F, 4 => GestureKey.H,
        5 => GestureKey.G, 6 => GestureKey.Z, 7 => GestureKey.X, 8 => GestureKey.C, 9 => GestureKey.V,
        11 => GestureKey.B, 12 => GestureKey.Q, 13 => GestureKey.W, 14 => GestureKey.E, 15 => GestureKey.R,
        16 => GestureKey.Y, 17 => GestureKey.T, 31 => GestureKey.O, 32 => GestureKey.U, 34 => GestureKey.I,
        35 => GestureKey.P, 37 => GestureKey.L, 38 => GestureKey.J, 40 => GestureKey.K, 45 => GestureKey.N,
        46 => GestureKey.M,
        // 主行数字
        18 => GestureKey.D1, 19 => GestureKey.D2, 20 => GestureKey.D3, 21 => GestureKey.D4, 22 => GestureKey.D6,
        23 => GestureKey.D5, 25 => GestureKey.D9, 26 => GestureKey.D7, 28 => GestureKey.D8, 29 => GestureKey.D0,
        // 常用标点（与 HID 的 =/−/[/]/;/'/\`/,/./ 一一对应）
        24 => GestureKey.OemPlus, 27 => GestureKey.OemMinus, 30 => GestureKey.OemCloseBrackets,
        33 => GestureKey.OemOpenBrackets, 39 => GestureKey.OemQuotes, 41 => GestureKey.OemSemicolon,
        42 => GestureKey.OemPipe, 43 => GestureKey.OemComma, 44 => GestureKey.OemQuestion,
        47 => GestureKey.OemPeriod, 50 => GestureKey.OemTilde,
        // 特殊键
        123 => GestureKey.Left, 124 => GestureKey.Right, 125 => GestureKey.Down, 126 => GestureKey.Up,
        53 => GestureKey.Escape, 36 => GestureKey.Return, 48 => GestureKey.Tab, 49 => GestureKey.Space,
        51 => GestureKey.Back, 117 => GestureKey.Delete,
        122 => GestureKey.F1, 120 => GestureKey.F2, 99 => GestureKey.F3, 118 => GestureKey.F4,
        96 => GestureKey.F5, 97 => GestureKey.F6, 98 => GestureKey.F7, 100 => GestureKey.F8,
        101 => GestureKey.F9, 109 => GestureKey.F10, 103 => GestureKey.F11, 111 => GestureKey.F12,
        105 => GestureKey.F13, 107 => GestureKey.F14, 113 => GestureKey.F15, 106 => GestureKey.F16,
        64 => GestureKey.F17, 79 => GestureKey.F18, 80 => GestureKey.F19, 90 => GestureKey.F20,
        115 => GestureKey.Home, 119 => GestureKey.End, 116 => GestureKey.PageUp, 121 => GestureKey.PageDown,
        // 数字键盘：± 与主行 =/− 同键，才能命中默认缩放绑定；回车保持 Return
        69 => GestureKey.OemPlus, 78 => GestureKey.OemMinus, 76 => GestureKey.Return,
        82 => GestureKey.NumPad0, 83 => GestureKey.NumPad1, 84 => GestureKey.NumPad2, 85 => GestureKey.NumPad3,
        86 => GestureKey.NumPad4, 87 => GestureKey.NumPad5, 88 => GestureKey.NumPad6, 89 => GestureKey.NumPad7,
        91 => GestureKey.NumPad8, 92 => GestureKey.NumPad9,
        67 => GestureKey.Multiply, 75 => GestureKey.Divide, 65 => GestureKey.Decimal,
        _ => null,
    };
}
