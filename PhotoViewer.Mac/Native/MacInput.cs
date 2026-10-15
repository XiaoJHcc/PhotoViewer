using AppKit;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 事件与共享手势模型之间的单向映射，保留设置 JSON 的键名与修饰键口径。</summary>
public static class MacInput
{
    /// <summary>把原生事件转为现有键盘/鼠标手势；不支持的事件返回 null。
    /// NSEvent 只允许读取与事件类型相关的字段，读取其余字段会抛 NSInternalInconsistencyException，
    /// 故必须先按类型取值再复用 <see cref="FromRaw"/> 的同一映射口径，不能一次性传入全部字段。</summary>
    public static AppGesture? FromEvent(NSEvent nativeEvent, SettingsViewModel? captureSettings = null)
    {
        switch (nativeEvent.Type)
        {
            case NSEventType.ScrollWheel:
                return FromRaw((int)nativeEvent.Type, 0, (ulong)nativeEvent.ModifierFlags, 0,
                    nativeEvent.ScrollingDeltaY, nativeEvent.HasPreciseScrollingDeltas, null, captureSettings);
            case NSEventType.LeftMouseDown:
            case NSEventType.RightMouseDown:
            case NSEventType.OtherMouseDown:
                return FromRaw((int)nativeEvent.Type, 0, (ulong)nativeEvent.ModifierFlags, nativeEvent.ButtonNumber,
                    0, false, null, captureSettings);
            case NSEventType.KeyDown:
                return FromRaw((int)nativeEvent.Type, nativeEvent.KeyCode, (ulong)nativeEvent.ModifierFlags, 0,
                    0, false, nativeEvent.CharactersIgnoringModifiers, captureSettings);
            default:
                return null;
        }
    }

    /// <summary>把 SwiftUI 侧拆包后的原生事件字段还原为共享手势，供跨语言录制复用同一映射。</summary>
    /// <param name="eventType">NSEventType 原始值。</param>
    /// <param name="keyCode">原生键码。</param>
    /// <param name="modifierFlags">原生修饰键掩码。</param>
    /// <param name="buttonNumber">鼠标按键编号。</param>
    /// <param name="scrollingDeltaY">滚轮纵向增量。</param>
    /// <param name="hasPreciseScrollingDeltas">是否为精确滚轮事件。</param>
    /// <param name="charactersIgnoringModifiers">忽略修饰键后的字符。</param>
    /// <param name="captureSettings">录制所用的工作副本，决定苹果键盘映射口径。</param>
    public static AppGesture? FromRaw(int eventType, ushort keyCode, ulong modifierFlags, nint buttonNumber, double scrollingDeltaY,
        bool hasPreciseScrollingDeltas, string? charactersIgnoringModifiers, SettingsViewModel? captureSettings = null)
    {
        GestureModifiers modifiers = 0;
        if ((modifierFlags & (ulong)NSEventModifierMask.ShiftKeyMask) != 0) modifiers |= GestureModifiers.Shift;
        if ((modifierFlags & (ulong)NSEventModifierMask.ControlKeyMask) != 0) modifiers |= GestureModifiers.Control;
        if ((modifierFlags & (ulong)NSEventModifierMask.AlternateKeyMask) != 0) modifiers |= GestureModifiers.Alt;
        if ((modifierFlags & (ulong)NSEventModifierMask.CommandKeyMask) != 0) modifiers |= GestureModifiers.Meta;
        modifiers = captureSettings == null ? AppleKeyboardMapping.ApplyForRuntime(modifiers)
            : AppleKeyboardMapping.ApplyForRuntime(modifiers, captureSettings.UseAppleKeyboard, captureSettings.MapCommandTarget, captureSettings.MapOptionTarget, captureSettings.MapControlTarget);
        var type = (NSEventType)eventType;
        if (type == NSEventType.ScrollWheel)
        {
            if ((hasPreciseScrollingDeltas && modifiers == 0) || scrollingDeltaY == 0) return null;
            return AppGesture.FromMouse(new MouseGestureEx(scrollingDeltaY > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, modifiers));
        }
        if (type is NSEventType.LeftMouseDown or NSEventType.RightMouseDown or NSEventType.OtherMouseDown)
        {
            MouseAction action = (int)buttonNumber switch
            {
                0 => MouseAction.LeftClick, 1 => MouseAction.RightClick, 2 => MouseAction.MiddleClick,
                3 => MouseAction.XButton1Click, _ => MouseAction.XButton2Click,
            };
            return AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
        }
        if (type != NSEventType.KeyDown) return null;
        var key = KeyFromRaw(keyCode, charactersIgnoringModifiers);
        return key.HasValue ? AppGesture.FromKey(new AppKeyGesture(key.Value, modifiers)) : null;
    }

    /// <summary>优先读取特殊键/数字键盘，普通字符按持久化键名转换。</summary>
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

    /// <summary>按用户主/次绑定查找命令，不另建原生默认快捷键表。</summary>
    public static string? ResolveCommand(NSEvent nativeEvent, SettingsViewModel settings)
        => FromEvent(nativeEvent) is { } gesture ? settings.GetCommandByGesture(gesture) : null;

    /// <summary>按当前设置工作副本生成 macOS 快捷键符号，显示转换不改变持久化手势。</summary>
    public static string FormatGesture(AppGesture? gesture, SettingsViewModel settings)
    {
        if (gesture == null) return "未设置";
        GestureModifiers modifiers = gesture.Key?.Modifiers ?? gesture.Mouse?.Modifiers ?? GestureModifiers.None;
        var parts = new List<string>();
        foreach (var modifier in new[] { GestureModifiers.Control, GestureModifiers.Alt, GestureModifiers.Shift, GestureModifiers.Meta })
        {
            if (!modifiers.HasFlag(modifier)) continue;
            AppleMappingTarget target = modifier switch
            {
                GestureModifiers.Control => AppleMappingTarget.Ctrl,
                GestureModifiers.Alt => AppleMappingTarget.Alt,
                GestureModifiers.Meta => AppleMappingTarget.Win,
                _ => AppleMappingTarget.None,
            };
            string symbol = settings.UseAppleKeyboard && target != AppleMappingTarget.None
                ? settings.MapCommandTarget == target ? "⌘" : settings.MapOptionTarget == target ? "⌥"
                    : settings.MapControlTarget == target ? "⌃" : ModifierSymbol(modifier)
                : ModifierSymbol(modifier);
            if (!parts.Contains(symbol)) parts.Add(symbol);
        }
        string key = gesture.Key is { } keyboard ? KeyLabel(keyboard.Key) : gesture.Mouse!.Action switch
        {
            MouseAction.LeftClick => "左键", MouseAction.RightClick => "右键", MouseAction.MiddleClick => "中键",
            MouseAction.XButton1Click => "侧键 1", MouseAction.XButton2Click => "侧键 2",
            MouseAction.WheelUp => "滚轮上", MouseAction.WheelDown => "滚轮下", _ => throw new InvalidOperationException("未知鼠标动作"),
        };
        return string.Concat(parts) + (gesture.Mouse != null && parts.Count > 0 ? " " : "") + key;
    }

    /// <summary>将原生修饰键转换为系统常用符号。</summary>
    private static string ModifierSymbol(GestureModifiers modifier) => modifier switch
    {
        GestureModifiers.Control => "⌃", GestureModifiers.Alt => "⌥", GestureModifiers.Shift => "⇧",
        GestureModifiers.Meta => "⌘", _ => throw new InvalidOperationException("未知修饰键"),
    };

    /// <summary>显示键帽和方向符号，将数字、数字键盘和标点的存储名称转换为用户可读文本。</summary>
    private static string KeyLabel(GestureKey key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal)) return "数字 " + name[6..];
        return key switch
        {
            GestureKey.Left => "←", GestureKey.Right => "→", GestureKey.Up => "↑", GestureKey.Down => "↓",
            GestureKey.Return or GestureKey.Enter => "↩", GestureKey.Tab => "⇥", GestureKey.Back => "⌫",
            GestureKey.Delete => "⌦", GestureKey.Escape => "⎋", GestureKey.Space => "空格",
            GestureKey.OemPlus => "=", GestureKey.OemMinus => "−", GestureKey.OemTilde => "\u0060",
            GestureKey.OemComma => ",", GestureKey.OemPeriod => ".", GestureKey.OemQuestion => "/",
            GestureKey.OemSemicolon => ";", GestureKey.OemQuotes => "'", GestureKey.OemOpenBrackets => "[",
            GestureKey.OemCloseBrackets => "]", GestureKey.OemPipe => "\\",
            GestureKey.Add => "数字 +", GestureKey.Subtract => "数字 −", GestureKey.Multiply => "数字 ×",
            GestureKey.Divide => "数字 /", GestureKey.Decimal => "数字 .", _ => name,
        };
    }
}
