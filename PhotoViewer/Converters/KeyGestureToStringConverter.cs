using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Converters;

/// <summary>
/// 把 <see cref="AppKeyGesture"/> 转换为显示文本(修饰键符号 + 键名,如 "⌘ N" / "Ctrl N")。
/// </summary>
public class KeyGestureToStringConverter : IValueConverter
{
    private static readonly Dictionary<GestureKey, string> KeyDisplayNames = new()
    {
        { GestureKey.Left, "←" },
        { GestureKey.Right, "→" },
        { GestureKey.Up, "↑" },
        { GestureKey.Down, "↓" },
        { GestureKey.Enter, "Enter" },
        { GestureKey.Space, "Space" },
        { GestureKey.Tab, "Tab" },
        { GestureKey.Back, "Backspace" },
        { GestureKey.Delete, "Delete" },
        { GestureKey.Home, "Home" },
        { GestureKey.End, "End" },
        { GestureKey.PageUp, "Page Up" },
        { GestureKey.PageDown, "Page Down" },
        { GestureKey.Escape, "Esc" },
        { GestureKey.F1, "F1" }, { GestureKey.F2, "F2" }, { GestureKey.F3, "F3" }, { GestureKey.F4, "F4" },
        { GestureKey.F5, "F5" }, { GestureKey.F6, "F6" }, { GestureKey.F7, "F7" }, { GestureKey.F8, "F8" },
        { GestureKey.F9, "F9" }, { GestureKey.F10, "F10" }, { GestureKey.F11, "F11" }, { GestureKey.F12, "F12" },
        { GestureKey.NumPad0, "Num 0" }, { GestureKey.NumPad1, "Num 1" }, { GestureKey.NumPad2, "Num 2" },
        { GestureKey.NumPad3, "Num 3" }, { GestureKey.NumPad4, "Num 4" }, { GestureKey.NumPad5, "Num 5" },
        { GestureKey.NumPad6, "Num 6" }, { GestureKey.NumPad7, "Num 7" }, { GestureKey.NumPad8, "Num 8" }, { GestureKey.NumPad9, "Num 9" },
        { GestureKey.OemMinus, "-" },
        { GestureKey.OemPlus, "+" },
        { GestureKey.OemComma, "," },
        { GestureKey.OemPeriod, "." },
        { GestureKey.OemQuestion, "/" },
        { GestureKey.OemSemicolon, ";" },
        { GestureKey.OemQuotes, "'" },
        { GestureKey.OemOpenBrackets, "[" },
        { GestureKey.OemCloseBrackets, "]" },
        { GestureKey.OemPipe, "\\" },
        { GestureKey.OemTilde, "`" },
        { GestureKey.Multiply, "Num *" },
        { GestureKey.Add, "Num +" },
        { GestureKey.Subtract, "Num -" },
        { GestureKey.Decimal, "Num ." },
        { GestureKey.Divide, "Num /" }
    };

    /// <summary>
    /// 转换键盘手势为显示文本;非键盘手势返回"未设置"。
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AppKeyGesture keyGesture)
            return "未设置";

        var parts = new List<string>();

        bool hasCtrl = keyGesture.Modifiers.HasFlag(GestureModifiers.Control);
        bool hasAlt  = keyGesture.Modifiers.HasFlag(GestureModifiers.Alt);
        bool hasShift= keyGesture.Modifiers.HasFlag(GestureModifiers.Shift);
        bool hasMeta = keyGesture.Modifiers.HasFlag(GestureModifiers.Meta);

        if (hasCtrl)
            parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Control));
        if (hasAlt)
            parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Alt));
        if (hasShift)
            parts.Add(AppleKeyboardMapping.GetShiftDisplay()); // 修改：Shift 显示 ⇧（苹果模式）
        if (hasMeta)
            parts.Add(AppleKeyboardMapping.GetDisplayForModifier(GestureModifiers.Meta));

        var keyName = GetKeyDisplayName(keyGesture.Key);
        parts.Add(keyName);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 不支持反向转换。
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    /// <summary>键名转显示名,字母/数字直接用键名。</summary>
    private static string GetKeyDisplayName(GestureKey key)
    {
        if (KeyDisplayNames.TryGetValue(key, out var displayName))
            return displayName;

        // 对于字母和数字键，直接使用键名
        var keyString = key.ToString();
        
        // 处理数字键
        if (keyString.StartsWith("D") && keyString.Length == 2 && char.IsDigit(keyString[1]))
            return keyString[1].ToString();
            
        return keyString;
    }
}
