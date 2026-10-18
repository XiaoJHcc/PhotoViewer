using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using PhotoViewer.Core.Input;
using Windows.System;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// Windows 输入 → 共享手势模型的单向映射。
/// 共享模型 <see cref="AppGesture"/> 是持久化契约(设置 JSON 里存手势名/值),
/// 因此这里只做"平台事件 → 共享手势"的翻译,绝不改写字码或手势表。
/// </summary>
internal static class WindowsInput
{
    /// <summary>虚拟键里没有名字的 OEM 键码 → 共享键。</summary>
    private static readonly Dictionary<int, GestureKey> OemKeys = new()
    {
        [186] = GestureKey.OemSemicolon,
        [187] = GestureKey.OemPlus,
        [188] = GestureKey.OemComma,
        [189] = GestureKey.OemMinus,
        [190] = GestureKey.OemPeriod,
        [191] = GestureKey.OemQuestion,
        [192] = GestureKey.OemTilde,
        [219] = GestureKey.OemOpenBrackets,
        [220] = GestureKey.OemPipe,
        [221] = GestureKey.OemCloseBrackets,
        [222] = GestureKey.OemQuotes,
    };

    /// <summary>
    /// 把 Windows 虚拟键映射为共享键。
    /// </summary>
    /// <param name="key">虚拟键</param>
    /// <returns>共享键;无法映射时返回 null(该键不参与照片快捷键)</returns>
    public static GestureKey? FromVirtualKey(VirtualKey key)
    {
        var code = (int)key;
        if (OemKeys.TryGetValue(code, out var oem))
        {
            return oem;
        }

        // 主键盘数字键在共享模型里叫 D0-D9
        if (code >= (int)VirtualKey.Number0 && code <= (int)VirtualKey.Number9)
        {
            return GestureKey.D0 + (code - (int)VirtualKey.Number0);
        }

        // WinRT 与共享模型的命名差异键(小键盘数字等)显式映射,不能交给名称解析兜底
        var mismatched = MapMismatchedName(key);
        if (mismatched != null)
        {
            return mismatched;
        }

        // 其余键在共享模型里与 Windows 虚拟键同名(GestureKey 成员名与平台键名对齐)
        return Enum.TryParse<GestureKey>(key.ToString(), out var mapped) ? mapped : null;
    }

    /// <summary>
    /// 映射 WinRT 与共享键模型命名不一致的键。
    /// 共享模型成员名镜像平台键名:小键盘数字叫 NumPad0-9、回车叫 Return、
    /// 大写锁定叫 CapsLock、数字锁定叫 NumLock;WinRT 对应的名字分别是
    /// NumberPad0-9 / Enter / CapitalLock / NumberKeyLock,名称对不上,
    /// 靠 <c>Enum.TryParse</c> 兜底会失败(默认次级评分快捷键 NumPad0-5 会因此失效)。
    /// </summary>
    /// <param name="key">虚拟键</param>
    /// <returns>共享键;名称与共享模型一致时返回 null,交给名称解析兜底</returns>
    private static GestureKey? MapMismatchedName(VirtualKey key) => key switch
    {
        VirtualKey.NumberPad0 => GestureKey.NumPad0,
        VirtualKey.NumberPad1 => GestureKey.NumPad1,
        VirtualKey.NumberPad2 => GestureKey.NumPad2,
        VirtualKey.NumberPad3 => GestureKey.NumPad3,
        VirtualKey.NumberPad4 => GestureKey.NumPad4,
        VirtualKey.NumberPad5 => GestureKey.NumPad5,
        VirtualKey.NumberPad6 => GestureKey.NumPad6,
        VirtualKey.NumberPad7 => GestureKey.NumPad7,
        VirtualKey.NumberPad8 => GestureKey.NumPad8,
        VirtualKey.NumberPad9 => GestureKey.NumPad9,
        VirtualKey.Enter => GestureKey.Return,
        VirtualKey.CapitalLock => GestureKey.CapsLock,
        VirtualKey.NumberKeyLock => GestureKey.NumLock,
        _ => null,
    };

    /// <summary>
    /// 读取当前修饰键状态。
    /// </summary>
    /// <returns>修饰键</returns>
    public static GestureModifiers CurrentModifiers()
    {
        var modifiers = GestureModifiers.None;
        if (IsDown(VirtualKey.Shift)) modifiers |= GestureModifiers.Shift;
        if (IsDown(VirtualKey.Control)) modifiers |= GestureModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= GestureModifiers.Alt;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= GestureModifiers.Meta;
        return modifiers;
    }

    /// <summary>
    /// 由键盘事件构造手势。
    /// </summary>
    /// <param name="key">虚拟键</param>
    /// <returns>手势;无法映射时返回 null</returns>
    public static AppGesture? FromKey(VirtualKey key)
    {
        var mapped = FromVirtualKey(key);
        if (mapped == null || mapped == GestureKey.None)
        {
            return null;
        }

        var modifiers = AppleKeyboardMapping.ApplyForRuntime(CurrentModifiers());
        return AppGesture.FromKey(new AppKeyGesture(mapped.Value, modifiers));
    }

    /// <summary>
    /// 由滚轮事件构造手势(应用整数增量与高精度滚动统一走这里)。
    /// </summary>
    /// <param name="delta">滚动增量,正为上</param>
    /// <returns>手势</returns>
    public static AppGesture FromWheel(int delta)
    {
        var modifiers = AppleKeyboardMapping.ApplyForRuntime(CurrentModifiers());
        return AppGesture.FromMouse(new MouseGestureEx(
            delta > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, modifiers));
    }

    /// <summary>
    /// 由鼠标按键构造手势。
    /// </summary>
    /// <param name="action">鼠标动作</param>
    /// <returns>手势</returns>
    public static AppGesture FromMouse(MouseAction action)
    {
        var modifiers = AppleKeyboardMapping.ApplyForRuntime(CurrentModifiers());
        return AppGesture.FromMouse(new MouseGestureEx(action, modifiers));
    }

    /// <summary>
    /// 手势 → 设置页显示文本(Windows 键名,不使用 ⌘/⌥ 符号)。
    /// </summary>
    /// <param name="gesture">手势</param>
    /// <returns>显示文本;未设置时为"未设置"</returns>
    public static string FormatGesture(AppGesture? gesture)
    {
        if (gesture?.Key is { } key)
        {
            var parts = ModifierParts(key.Modifiers);
            parts.Add(KeyName(key.Key));
            return string.Join("+", parts);
        }

        if (gesture?.Mouse is { } mouse)
        {
            var parts = ModifierParts(mouse.Modifiers);
            parts.Add(mouse.Action switch
            {
                MouseAction.LeftClick => "左键",
                MouseAction.RightClick => "右键",
                MouseAction.MiddleClick => "中键",
                MouseAction.XButton1Click => "侧键1",
                MouseAction.XButton2Click => "侧键2",
                MouseAction.WheelUp => "滚轮上",
                MouseAction.WheelDown => "滚轮下",
                _ => "鼠标",
            });
            return string.Join("+", parts);
        }

        return "未设置";
    }

    /// <summary>把修饰键转成显示片段。</summary>
    /// <param name="modifiers">修饰键</param>
    /// <returns>片段列表</returns>
    private static List<string> ModifierParts(GestureModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(GestureModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(GestureModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(GestureModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(GestureModifiers.Meta)) parts.Add("Win");
        return parts;
    }

    /// <summary>共享键的 Windows 显示名。</summary>
    /// <param name="key">键</param>
    /// <returns>显示名</returns>
    private static string KeyName(GestureKey key)
    {
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1].ToString();
        }

        if (name.StartsWith("NumPad", StringComparison.Ordinal))
        {
            return "小键盘" + name["NumPad".Length..];
        }

        return KeyDisplayNames.TryGetValue(name, out var display) ? display : name;
    }

    /// <summary>特殊键显示名。</summary>
    private static readonly Dictionary<string, string> KeyDisplayNames = new(StringComparer.Ordinal)
    {
        ["Space"] = "空格",
        ["Return"] = "回车",
        ["Back"] = "退格",
        ["Escape"] = "Esc",
        ["Tab"] = "Tab",
        ["Delete"] = "Delete",
        ["Insert"] = "Insert",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PgUp",
        ["PageDown"] = "PgDn",
        ["CapsLock"] = "Caps",
        ["NumLock"] = "Num",
        ["Left"] = "←",
        ["Right"] = "→",
        ["Up"] = "↑",
        ["Down"] = "↓",
        ["OemPlus"] = "+",
        ["OemMinus"] = "-",
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemQuestion"] = "/",
        ["OemSemicolon"] = ";",
        ["OemTilde"] = "`",
        ["OemOpenBrackets"] = "[",
        ["OemCloseBrackets"] = "]",
        ["OemPipe"] = "\\",
        ["OemQuotes"] = "'",
        ["Add"] = "+",
        ["Subtract"] = "-",
        ["Multiply"] = "*",
        ["Divide"] = "/",
        ["Decimal"] = ".",
    };

    /// <summary>
    /// 判断虚拟键是否按下。
    /// </summary>
    /// <param name="key">虚拟键</param>
    /// <returns>是否按下</returns>
    private static bool IsDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;

    /// <summary>读取虚拟键状态;高位为 1 表示按下。</summary>
    /// <param name="virtualKey">虚拟键码</param>
    /// <returns>按键状态</returns>
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
