using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;

namespace PhotoViewer.Converters;

public class KeyGestureToStringConverter : IValueConverter
{
    private static readonly Dictionary<Key, string> KeyDisplayNames = new()
    {
        { Key.Left, "←" },
        { Key.Right, "→" },
        { Key.Up, "↑" },
        { Key.Down, "↓" },
        { Key.Enter, "Enter" },
        { Key.Space, "Space" },
        { Key.Tab, "Tab" },
        { Key.Back, "Backspace" },
        { Key.Delete, "Delete" },
        { Key.Home, "Home" },
        { Key.End, "End" },
        { Key.PageUp, "Page Up" },
        { Key.PageDown, "Page Down" },
        { Key.Escape, "Esc" },
        { Key.F1, "F1" }, { Key.F2, "F2" }, { Key.F3, "F3" }, { Key.F4, "F4" },
        { Key.F5, "F5" }, { Key.F6, "F6" }, { Key.F7, "F7" }, { Key.F8, "F8" },
        { Key.F9, "F9" }, { Key.F10, "F10" }, { Key.F11, "F11" }, { Key.F12, "F12" },
        { Key.NumPad0, "Num 0" }, { Key.NumPad1, "Num 1" }, { Key.NumPad2, "Num 2" },
        { Key.NumPad3, "Num 3" }, { Key.NumPad4, "Num 4" }, { Key.NumPad5, "Num 5" },
        { Key.NumPad6, "Num 6" }, { Key.NumPad7, "Num 7" }, { Key.NumPad8, "Num 8" }, { Key.NumPad9, "Num 9" },
        { Key.OemMinus, "-" },
        { Key.OemPlus, "+" },
        { Key.OemComma, "," },
        { Key.OemPeriod, "." },
        { Key.OemQuestion, "/" },
        { Key.OemSemicolon, ";" },
        { Key.OemQuotes, "'" },
        { Key.OemOpenBrackets, "[" },
        { Key.OemCloseBrackets, "]" },
        { Key.OemPipe, "\\" },
        { Key.OemTilde, "`" },
        { Key.Multiply, "Num *" },
        { Key.Add, "Num +" },
        { Key.Subtract, "Num -" },
        { Key.Decimal, "Num ." },
        { Key.Divide, "Num /" }
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not KeyGesture keyGesture)
            return "未设置";

        var parts = new List<string>();

        // 添加修饰键
        if (keyGesture.KeyModifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (keyGesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (keyGesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (keyGesture.KeyModifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");

        // 添加主键
        var keyName = GetKeyDisplayName(keyGesture.Key);
        parts.Add(keyName);

        return string.Join("+", parts);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static string GetKeyDisplayName(Key key)
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

