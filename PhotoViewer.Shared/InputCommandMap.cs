using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Shared;

/// <summary>
/// 平台中立的“手势 → 用户命令”查询：各平台把原生事件解包成共享 <see cref="AppGesture"/> 后，
/// 统一从这里按用户主/次绑定查命令。苹果的修饰键解码与 HID usage 表留在
/// <c>AppleInputMapping</c>，Android 的 <c>KeyEvent</c> 解码留在 <c>AndroidInputMapping</c>。
/// </summary>
public static class InputCommandMap
{
    /// <summary>按用户主/次绑定查找命令；各平台的事件解包之后共用这一张命令表。</summary>
    /// <param name="gesture">共享手势（可为 null）。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>匹配到的命令标识；没有绑定返回 null。</returns>
    public static string? CommandForGesture(AppGesture? gesture, SettingsViewModel settings)
        => gesture is { } value ? settings.GetCommandByGesture(value) : null;
}
