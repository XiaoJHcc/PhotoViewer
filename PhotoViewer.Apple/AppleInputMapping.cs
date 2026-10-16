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
    /// <param name="keyCode">原生键码。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <param name="buttonNumber">鼠标按键编号。</param>
    /// <param name="scrollingDeltaY">滚轮纵向增量。</param>
    /// <param name="hasPreciseScrollingDeltas">是否为精确滚轮事件。</param>
    /// <param name="charactersIgnoringModifiers">忽略修饰键后的字符。</param>
    /// <param name="captureSettings">录制所用的工作副本，决定苹果键盘映射口径。</param>
    /// <returns>匹配到的共享手势；不支持的事件返回 null。</returns>
    public static AppGesture? FromRaw(int eventType, ushort keyCode, ulong modifierFlags, nint buttonNumber, double scrollingDeltaY,
        bool hasPreciseScrollingDeltas, string? charactersIgnoringModifiers, SettingsViewModel? captureSettings = null)
    {
        GestureModifiers modifiers = 0;
        if ((modifierFlags & ShiftMask) != 0) modifiers |= GestureModifiers.Shift;
        if ((modifierFlags & ControlMask) != 0) modifiers |= GestureModifiers.Control;
        if ((modifierFlags & AlternateMask) != 0) modifiers |= GestureModifiers.Alt;
        if ((modifierFlags & CommandMask) != 0) modifiers |= GestureModifiers.Meta;
        modifiers = captureSettings == null ? AppleKeyboardMapping.ApplyForRuntime(modifiers)
            : AppleKeyboardMapping.ApplyForRuntime(modifiers, captureSettings.UseAppleKeyboard, captureSettings.MapCommandTarget, captureSettings.MapOptionTarget, captureSettings.MapControlTarget);
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
        var key = KeyFromRaw(keyCode, charactersIgnoringModifiers);
        return key.HasValue ? AppGesture.FromKey(new AppKeyGesture(key.Value, modifiers)) : null;
    }

    /// <summary>优先读取特殊键/数字键盘，普通字符按持久化键名转换。</summary>
    /// <param name="keyCode">原生键码。</param>
    /// <param name="charactersIgnoringModifiers">忽略修饰键后的字符。</param>
    /// <returns>匹配到的共享键；未定义时返回 null。</returns>
    private static GestureKey? KeyFromRaw(ushort keyCode, string? charactersIgnoringModifiers)
    {
        GestureKey? special = keyCode switch
        {
            123 => GestureKey.Left, 124 => GestureKey.Right, 125 => GestureKey.Down, 126 => GestureKey.Up,
            53 => GestureKey.Escape, 36 => GestureKey.Return, 48 => GestureKey.Tab, 49 => GestureKey.Space,
            51 => GestureKey.Back, 117 => GestureKey.Delete, 50 => GestureKey.OemTilde,
            122 => GestureKey.F1, 120 => GestureKey.F2, 99 => GestureKey.F3, 118 => GestureKey.F4,
            96 => GestureKey.F5, 97 => GestureKey.F6, 98 => GestureKey.F7, 100 => GestureKey.F8,
            101 => GestureKey.F9, 109 => GestureKey.F10, 103 => GestureKey.F11, 111 => GestureKey.F12,
            105 => GestureKey.F13, 107 => GestureKey.F14, 113 => GestureKey.F15, 106 => GestureKey.F16,
            64 => GestureKey.F17, 79 => GestureKey.F18, 80 => GestureKey.F19, 90 => GestureKey.F20,
            115 => GestureKey.Home, 119 => GestureKey.End, 116 => GestureKey.PageUp, 121 => GestureKey.PageDown,
            24 => GestureKey.OemPlus, 27 => GestureKey.OemMinus,
            82 => GestureKey.NumPad0, 83 => GestureKey.NumPad1, 84 => GestureKey.NumPad2, 85 => GestureKey.NumPad3,
            86 => GestureKey.NumPad4, 87 => GestureKey.NumPad5, 88 => GestureKey.NumPad6, 89 => GestureKey.NumPad7,
            91 => GestureKey.NumPad8, 92 => GestureKey.NumPad9, 69 => GestureKey.Add, 78 => GestureKey.Subtract,
            67 => GestureKey.Multiply, 75 => GestureKey.Divide, 65 => GestureKey.Decimal, 76 => GestureKey.Return,
            _ => null,
        };
        if (special.HasValue) return special;
        string text = charactersIgnoringModifiers?.ToUpperInvariant() ?? string.Empty;
        if (text.Length != 1) return null;
        if (text[0] is >= 'A' and <= 'Z' && Enum.TryParse<GestureKey>(text, out var letter)) return letter;
        if (text[0] is >= '0' and <= '9' && Enum.TryParse<GestureKey>("D" + text, out var digit)) return digit;
        return text[0] switch
        {
            ',' => GestureKey.OemComma, '.' => GestureKey.OemPeriod, '/' => GestureKey.OemQuestion,
            ';' => GestureKey.OemSemicolon, '\'' => GestureKey.OemQuotes, '[' => GestureKey.OemOpenBrackets,
            ']' => GestureKey.OemCloseBrackets, '\\' => GestureKey.OemPipe, _ => null,
        };
    }
}
