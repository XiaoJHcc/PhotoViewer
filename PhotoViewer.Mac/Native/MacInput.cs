using AppKit;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 事件与共享手势模型之间的单向映射，保留设置 JSON 的键名与修饰键口径。</summary>
public static class MacInput
{
    /// <summary>把原生事件转为现有键盘/鼠标手势；不支持的事件返回 null。</summary>
    public static AppGesture? FromEvent(NSEvent nativeEvent, SettingsViewModel? captureSettings = null)
    {
        GestureModifiers modifiers = 0;
        if (nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask)) modifiers |= GestureModifiers.Shift;
        if (nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.ControlKeyMask)) modifiers |= GestureModifiers.Control;
        if (nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask)) modifiers |= GestureModifiers.Alt;
        if (nativeEvent.ModifierFlags.HasFlag(NSEventModifierMask.CommandKeyMask)) modifiers |= GestureModifiers.Meta;
        modifiers = captureSettings == null ? AppleKeyboardMapping.ApplyForRuntime(modifiers)
            : AppleKeyboardMapping.ApplyForRuntime(modifiers, captureSettings.UseAppleKeyboard, captureSettings.MapCommandTarget, captureSettings.MapOptionTarget, captureSettings.MapControlTarget);
        if (nativeEvent.Type == NSEventType.ScrollWheel)
        {
            if ((nativeEvent.HasPreciseScrollingDeltas && modifiers == 0) || nativeEvent.ScrollingDeltaY == 0) return null;
            return AppGesture.FromMouse(new MouseGestureEx(nativeEvent.ScrollingDeltaY > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, modifiers));
        }
        if (nativeEvent.Type is NSEventType.LeftMouseDown or NSEventType.RightMouseDown or NSEventType.OtherMouseDown)
        {
            MouseAction action = nativeEvent.ButtonNumber switch
            {
                0 => MouseAction.LeftClick, 1 => MouseAction.RightClick, 2 => MouseAction.MiddleClick,
                3 => MouseAction.XButton1Click, _ => MouseAction.XButton2Click,
            };
            return AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
        }
        if (nativeEvent.Type != NSEventType.KeyDown) return null;
        var key = KeyFromEvent(nativeEvent);
        return key.HasValue ? AppGesture.FromKey(new AppKeyGesture(key.Value, modifiers)) : null;
    }

    /// <summary>优先读取特殊键/数字键盘，普通字符按持久化键名转换。</summary>
    private static GestureKey? KeyFromEvent(NSEvent nativeEvent)
    {
        GestureKey? special = nativeEvent.KeyCode switch
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
        string text = nativeEvent.CharactersIgnoringModifiers?.ToUpperInvariant() ?? string.Empty;
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
}
