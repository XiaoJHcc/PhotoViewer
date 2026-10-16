using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using PhotoViewer.Core.Input;
using Windows.System;

namespace PhotoViewer.Desktop.Native;

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

        // 其余键在共享模型里与 Windows 虚拟键同名(GestureKey 与 Avalonia Key 同名对齐)
        return Enum.TryParse<GestureKey>(key.ToString(), out var mapped) ? mapped : null;
    }

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
