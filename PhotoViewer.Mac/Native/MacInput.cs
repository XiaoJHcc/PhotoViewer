using AppKit;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>AppKit 事件与共享手势模型之间的单向映射，保留设置 JSON 的键名与修饰键口径。
/// 字段解包与 <see cref="AppleInputMapping"/> 的映射口径分家：NSEvent 只允许读取与事件类型相关的字段，
/// 读取其余字段会抛 NSInternalInconsistencyException，故必须先按类型取值再交给共享映射。</summary>
public static class MacInput
{
    /// <summary>把原生事件转为现有键盘/鼠标手势；不支持的事件返回 null。</summary>
    /// <param name="nativeEvent">AppKit 事件。</param>
    /// <param name="captureSettings">录制所用的工作副本，决定苹果键盘映射口径。</param>
    /// <returns>匹配到的共享手势；不支持的事件返回 null。</returns>
    public static AppGesture? FromEvent(NSEvent nativeEvent, SettingsViewModel? captureSettings = null)
    {
        switch (nativeEvent.Type)
        {
            case NSEventType.ScrollWheel:
                return AppleInputMapping.FromRaw(AppleInputMapping.ScrollWheel, 0, (ulong)nativeEvent.ModifierFlags, 0,
                    nativeEvent.ScrollingDeltaY, nativeEvent.HasPreciseScrollingDeltas, null, captureSettings);
            case NSEventType.LeftMouseDown:
            case NSEventType.RightMouseDown:
            case NSEventType.OtherMouseDown:
                return AppleInputMapping.FromRaw(AppKitMouseType(nativeEvent.Type), 0, (ulong)nativeEvent.ModifierFlags, nativeEvent.ButtonNumber,
                    0, false, null, captureSettings);
            case NSEventType.KeyDown:
                return AppleInputMapping.FromRaw(AppleInputMapping.KeyDown, nativeEvent.KeyCode, (ulong)nativeEvent.ModifierFlags, 0,
                    0, false, nativeEvent.CharactersIgnoringModifiers, captureSettings);
            default:
                return null;
        }
    }

    /// <summary>把按下的原生鼠标事件类型码对齐到共享映射的三种按键事件。</summary>
    /// <param name="type">AppKit 事件类型。</param>
    /// <returns>共享映射使用的事件类型码。</returns>
    private static int AppKitMouseType(NSEventType type) => type switch
    {
        NSEventType.LeftMouseDown => AppleInputMapping.LeftMouseDown,
        NSEventType.RightMouseDown => AppleInputMapping.RightMouseDown,
        _ => AppleInputMapping.OtherMouseDown,
    };

    /// <summary>按用户主/次绑定查找命令，不另建原生默认快捷键表。</summary>
    /// <param name="nativeEvent">AppKit 事件。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>匹配到的命令标识；没有绑定返回 null。</returns>
    public static string? ResolveCommand(NSEvent nativeEvent, SettingsViewModel settings)
        => AppleInputMapping.CommandForGesture(FromEvent(nativeEvent), settings);

    /// <summary>按当前设置工作副本生成 macOS 快捷键符号，显示转换不改变持久化手势。</summary>
    /// <param name="gesture">共享手势。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>用户可读的快捷键文本。</returns>
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
