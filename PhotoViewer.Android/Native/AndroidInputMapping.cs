using System;
using Android.Views;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 输入映射（A4）：原生 <see cref="KeyEvent"/> / 修饰键掩码 → 共享 <see cref="GestureKey"/> /
/// <see cref="GestureModifiers"/>。键名与命令表复用共享定义；Ctrl/Alt/Shift/Meta 按 Android 习惯解码。
/// </summary>
internal static class AndroidInputMapping
{
    /// <summary>Android 修饰键状态 → 共享修饰键。</summary>
    /// <param name="metaState">原生 MetaKeyStates。</param>
    /// <returns>共享修饰键。</returns>
    internal static GestureModifiers ModifiersOf(int metaState)
    {
        var modifiers = GestureModifiers.None;
        var state = (MetaKeyStates)metaState;
        if ((state & MetaKeyStates.AltOn) != 0 || (state & MetaKeyStates.AltLeftOn) != 0 || (state & MetaKeyStates.AltRightOn) != 0)
            modifiers |= GestureModifiers.Alt;
        if ((state & MetaKeyStates.CtrlOn) != 0 || (state & MetaKeyStates.CtrlLeftOn) != 0 || (state & MetaKeyStates.CtrlRightOn) != 0)
            modifiers |= GestureModifiers.Control;
        if ((state & MetaKeyStates.ShiftOn) != 0 || (state & MetaKeyStates.ShiftLeftOn) != 0 || (state & MetaKeyStates.ShiftRightOn) != 0)
            modifiers |= GestureModifiers.Shift;
        if ((state & MetaKeyStates.MetaOn) != 0 || (state & MetaKeyStates.MetaLeftOn) != 0 || (state & MetaKeyStates.MetaRightOn) != 0)
            modifiers |= GestureModifiers.Meta;
        return modifiers;
    }

    /// <summary>把硬件按键解包成共享键手势；修饰键/未知键返回 null。</summary>
    /// <param name="keyCode">原生键码。</param>
    /// <param name="metaState">原生修饰键状态。</param>
    /// <returns>共享手势或 null。</returns>
    internal static AppGesture? FromKeyEvent(Keycode keyCode, int metaState)
    {
        var key = MapKey(keyCode);
        if (key == null) return null;
        return AppGesture.FromKey(new AppKeyGesture(key.Value, ModifiersOf(metaState)));
    }

    /// <summary>原生键码 → 共享键；名称一致的字母/数字/功能键直接走枚举，其余逐项映射。</summary>
    /// <param name="keyCode">原生键码。</param>
    /// <returns>共享键或 null（修饰键、媒体键等不参与命令表）。</returns>
    private static GestureKey? MapKey(Keycode keyCode)
    {
        switch (keyCode)
        {
            case Keycode.DpadLeft: return GestureKey.Left;
            case Keycode.DpadRight: return GestureKey.Right;
            case Keycode.DpadUp: return GestureKey.Up;
            case Keycode.DpadDown: return GestureKey.Down;
            case Keycode.Space: return GestureKey.Space;
            case Keycode.Enter: return GestureKey.Enter;
            case Keycode.Tab: return GestureKey.Tab;
            case Keycode.Escape: return GestureKey.Escape;
            case Keycode.Del: return GestureKey.Delete;
            case Keycode.Insert: return GestureKey.Insert;
            case Keycode.MoveHome: return GestureKey.Home;
            case Keycode.MoveEnd: return GestureKey.End;
            case Keycode.PageUp: return GestureKey.PageUp;
            case Keycode.PageDown: return GestureKey.PageDown;
            case Keycode.Grave: return GestureKey.OemTilde;
            case Keycode.Equals:
            case Keycode.Plus:
            case Keycode.NumpadAdd: return GestureKey.OemPlus;
            case Keycode.Minus:
            case Keycode.NumpadSubtract: return GestureKey.OemMinus;
            case Keycode.Slash: return GestureKey.OemQuestion;
            case Keycode.Comma: return GestureKey.OemComma;
            case Keycode.Period: return GestureKey.OemPeriod;
            case Keycode.Semicolon: return GestureKey.OemSemicolon;
            case Keycode.Apostrophe: return GestureKey.OemQuotes;
            case Keycode.LeftBracket: return GestureKey.OemOpenBrackets;
            case Keycode.RightBracket: return GestureKey.OemCloseBrackets;
            case Keycode.Backslash: return GestureKey.OemPipe;
            case Keycode.Back: return GestureKey.Back;
        }
        // 字母 / 数字 / F1–F24 / 小键盘数字与原生键码枚举同名，直接解析；修饰键与媒体键解析失败返回 null。
        string name = keyCode.ToString();
        if (name.StartsWith("Numpad", StringComparison.Ordinal))
            name = "NumPad" + name["Numpad".Length..];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        return Enum.TryParse<GestureKey>(name, ignoreCase: false, out var key) ? key : null;
    }
}
