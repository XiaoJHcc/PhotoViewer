using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Shared;

/// <summary>
/// 平台中立的“手势 → 用户命令”查询：各平台把原生事件解包成共享 <see cref="AppGesture"/> 后，
/// 统一从这里按用户主/次绑定查命令。苹果的修饰键解码与 HID usage 表留在
/// <c>AppleInputMapping</c>，Android 的 <c>KeyEvent</c> 解码留在 <c>AndroidInputMapping</c>。
/// </summary>
public static class InputCommandMap
{
    /// <summary>
    /// 命令是否登记在共享设置目录 <see cref="SettingsCatalog.Commands"/> 中。
    /// 输入派发只用这一判据决定能否执行；SF Symbols 之类的图标表只负责 UI，
    /// 不得反过来充当命令白名单（否则新增/改名的命令会因缺图标而静默失效）。
    /// </summary>
    /// <param name="command">命令标识（可为 null）。</param>
    /// <returns>已登记返回 true；null、空串与未登记标识返回 false。</returns>
    public static bool IsCommandKnown(string? command)
        => command is not null && SettingsCatalog.TryGetCommand(command, out _);

    /// <summary>按用户主/次绑定查找命令；各平台的事件解包之后共用这一张命令表。</summary>
    /// <param name="gesture">共享手势（可为 null）。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>匹配到的命令标识；没有绑定返回 null。</returns>
    public static string? CommandForGesture(AppGesture? gesture, SettingsViewModel settings)
        => gesture is { } value ? settings.GetCommandByGesture(value) : null;

    /// <summary>
    /// 按住按键/滚轮时是否允许重复执行该命令（INPUT-05）。
    /// 只有上一张、下一张与预设缩放可连续执行；评分、打开、切换、显隐等只执行一次。
    /// </summary>
    /// <param name="command">命令标识（可为 null）。</param>
    /// <returns>可重复返回 true；未知命令或 null 返回 false。</returns>
    public static bool IsRepeatable(string? command) => command switch
    {
        "Previous" or "Next" or "ZoomInPreset" or "ZoomOutPreset" => true,
        _ => false
    };
}
