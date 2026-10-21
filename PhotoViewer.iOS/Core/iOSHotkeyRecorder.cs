using Foundation;
using GameController;
using PhotoViewer.Apple;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 快捷键录制器（非视图）：持有当前录制目标，把 <c>UIPress</c>、捕获层鼠标/滚轮与 GC 中键/侧键/滚轮
/// 经共享 <see cref="AppleInputMapping"/> 写入设置工作副本；Esc 取消、Delete/Backspace 清空。
/// 最后外设断开、应用失活或宿主页离开时取消录制；绑定或冲突状态变化后触发 <see cref="Changed"/>。
/// </summary>
internal sealed class iOSHotkeyRecorder : IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly iOSInputCapabilities _capabilities;
    private readonly List<GCControllerButtonInput> _mouseButtons = [];
    private readonly List<GCControllerDirectionPad> _mouseScrolls = [];
    private readonly NSObject _inactive;
    private (SettingsViewModel.HotkeyItem Item, bool Secondary)? _recording;
    private iOSHotkeyCaptureView? _captureSurface;
    private NSTimer? _captureReleaseTimer;
    private bool _waitingForMouseRelease;
    private long _recordingGeneration;
    private ulong _modifiers;
    private bool _disposed;

    internal iOSHotkeyRecorder(SettingsViewModel settings, iOSInputCapabilities capabilities)
    {
        _settings = settings;
        _capabilities = capabilities;
        _capabilities.Changed += OnCapabilitiesChanged;
        _inactive = NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => CancelRecording());
    }

    /// <summary>录制目标、绑定或冲突状态变化。</summary>
    internal event Action? Changed;

    internal bool IsRecording => _recording.HasValue;
    internal bool HasExternalInput => _capabilities.HasExternalInput;
    internal iOSHotkeyCaptureView? CaptureSurface => _captureSurface;
    internal ulong CurrentModifiers => _modifiers;

    /// <summary>当前是否正在录制指定命令的指定槽位。</summary>
    internal bool IsRecordingSlot(SettingsViewModel.HotkeyItem item, bool secondary)
        => _recording is { } target && ReferenceEquals(target.Item, item) && target.Secondary == secondary;

    /// <summary>开始录制：透明捕获层覆盖窗口接管命中，<paramref name="focus"/> 让宿主页成为按键响应者。</summary>
    internal void BeginRecording(SettingsViewModel.HotkeyItem item, bool secondary, UIWindow? window, Action? focus = null)
    {
        if (!HasExternalInput || window == null) return;
        CancelRecording();
        _recording = (item, secondary);
        _modifiers = 0;
        _captureSurface = new iOSHotkeyCaptureView(this, window.Bounds);
        window.AddSubview(_captureSurface);
        AttachMouse();
        focus?.Invoke();
        Changed?.Invoke();
    }

    internal void CancelRecording()
    {
        bool wasActive = _recording.HasValue || _captureSurface != null;
        _recording = null;
        _recordingGeneration++;
        _waitingForMouseRelease = false;
        DetachMouse();
        RemoveCaptureSurface();
        if (wasActive) Changed?.Invoke();
    }

    /// <summary>清空指定槽位（取消进行中的录制）并重算冲突。</summary>
    internal void Clear(SettingsViewModel.HotkeyItem item, bool secondary)
    {
        CancelRecording();
        if (secondary) item.SecondaryHotkey = null;
        else item.PrimaryHotkey = null;
        _settings.CheckHotkeyConflicts();
        Changed?.Invoke();
    }

    internal void UpdateModifiers(ulong modifiers) => _modifiers = modifiers;

    /// <summary>处理一次 HID 按键；非录制态返回 false 交还宿主。</summary>
    internal bool CaptureKey(int usage, ulong modifiers)
    {
        if (!IsRecording) return false;
        _modifiers = modifiers;
        if (usage == 41) { CancelRecording(); return true; }
        if (usage is 42 or 76) { Commit(null); return true; }
        if (usage is >= 224 and <= 231 || usage == 43) return true;
        var gesture = AppleInputMapping.FromHidUsage(usage, modifiers, _settings);
        if (gesture != null) Commit(gesture);
        return true;
    }

    /// <summary>处理一次鼠标按键（<paramref name="wheel"/> 为 0）或滚轮；主键与 Mac 同为左键手势。</summary>
    internal bool CaptureMouse(nint button, double wheel = 0)
    {
        if (!IsRecording) return false;
        int eventType = wheel != 0
            ? AppleInputMapping.ScrollWheel
            : button == 0 ? AppleInputMapping.LeftMouseDown : AppleInputMapping.OtherMouseDown;
        var gesture = AppleInputMapping.FromRaw(eventType, 0, _modifiers, button, wheel, false, null, _settings);
        if (gesture != null)
        {
            _waitingForMouseRelease = wheel == 0;
            Commit(gesture, retainSurface: true);
            if (wheel != 0) ScheduleWheelRelease();
        }
        return true;
    }

    /// <summary>鼠标抬起或滚动结束：录制已提交且无活动指针时释放捕获层。</summary>
    internal void ReleaseCapturedPointer()
    {
        _waitingForMouseRelease = false;
        long generation = _recordingGeneration;
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (generation == _recordingGeneration) ReleaseCaptureIfIdle();
        });
    }

    /// <summary>绑定的可读文本：修饰键按当前苹果键盘映射显示 ⌘⌥⌃⇧，空槽为"未设置"。</summary>
    internal string Display(AppGesture? gesture) => iOSHotkeyFormatter.Format(_settings, gesture);

    internal static string KeyLabel(GestureKey key) => iOSHotkeyFormatter.KeyLabel(key);

    private void OnCapabilitiesChanged()
    {
        if (!HasExternalInput) CancelRecording();
        else if (IsRecording) AttachMouse();
    }

    private void Commit(AppGesture? gesture, bool retainSurface = false)
    {
        if (_recording is not { } recording) return;
        if (recording.Secondary) recording.Item.SecondaryHotkey = gesture;
        else recording.Item.PrimaryHotkey = gesture;
        if (retainSurface) _recording = null;
        else CancelRecording();
        _settings.CheckHotkeyConflicts();
        Changed?.Invoke();
    }

    private void DetachMouse()
    {
        foreach (var button in _mouseButtons) button.PressedChangedHandler = null;
        foreach (var scroll in _mouseScrolls) scroll.ValueChangedHandler = null;
        _mouseButtons.Clear();
        _mouseScrolls.Clear();
    }

    private void RemoveCaptureSurface()
    {
        _captureReleaseTimer?.Invalidate();
        _captureReleaseTimer?.Dispose();
        _captureReleaseTimer = null;
        _captureSurface?.RemoveFromSuperview();
        _captureSurface?.Dispose();
        _captureSurface = null;
    }

    private void ReleaseCaptureIfIdle()
    {
        if (IsRecording || _waitingForMouseRelease || _captureSurface is { PointerActive: true } or { ScrollActive: true }) return;
        DetachMouse();
        RemoveCaptureSurface();
    }

    private void ScheduleWheelRelease()
    {
        _captureReleaseTimer?.Invalidate();
        _captureReleaseTimer?.Dispose();
        _captureReleaseTimer = NSTimer.CreateScheduledTimer(TimeSpan.FromMilliseconds(200), _ => ReleaseCaptureIfIdle());
    }

    private void AttachMouse()
    {
        long generation = _recordingGeneration;
        foreach (var mouse in GCMouse.Mice)
        {
            if (mouse.MouseInput is not { } input) continue;
            void Attach(GCControllerButtonInput? button, int index)
            {
                if (button == null || _mouseButtons.Contains(button)) return;
                _mouseButtons.Add(button);
                button.PressedChangedHandler = (_, _, pressed) => UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    if (generation != _recordingGeneration) return;
                    if (pressed) CaptureMouse(index);
                    else ReleaseCapturedPointer();
                });
            }
            Attach(input.MiddleButton, 2);
            var auxiliary = input.AuxiliaryButtons ?? [];
            for (int index = 0; index < auxiliary.Length; index++) Attach(auxiliary[index], index + 3);
            if (!_mouseScrolls.Contains(input.Scroll))
            {
                _mouseScrolls.Add(input.Scroll);
                input.Scroll.ValueChangedHandler = (_, _, y) =>
                {
                    if (y != 0) UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                    {
                        if (generation == _recordingGeneration) CaptureMouse(0, y);
                    });
                };
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelRecording();
        _capabilities.Changed -= OnCapabilitiesChanged;
        NSNotificationCenter.DefaultCenter.RemoveObserver(_inactive);
    }
}

/// <summary>
/// 快捷键显示文本：修饰键按当前苹果键盘映射显示 ⌘⌥⌃⇧，键名转键帽文本，空槽为"未设置"。
/// 录制器与控制页行内绑定按钮共用这一处格式化。
/// </summary>
internal static class iOSHotkeyFormatter
{
    /// <summary>手势的可读文本。</summary>
    /// <param name="settings">当前设置（决定 Command/Option/Control 的映射符号）。</param>
    /// <param name="gesture">手势；null 显示"未设置"。</param>
    internal static string Format(SettingsViewModel settings, AppGesture? gesture)
    {
        if (gesture == null) return "未设置";
        var flags = gesture.Key?.Modifiers ?? gesture.Mouse!.Modifiers;
        string Modifier(GestureModifiers target)
        {
            if (!flags.HasFlag(target)) return "";
            foreach (var (mask, symbol) in new[] { (AppleInputMapping.CommandMask, "⌘"), (AppleInputMapping.AlternateMask, "⌥"), (AppleInputMapping.ControlMask, "⌃") })
                if (AppleInputMapping.FromHidUsage(4, mask, settings)?.Key?.Modifiers == target) return symbol;
            return target.ToString() + "+";
        }
        string modifiers = Modifier(GestureModifiers.Meta) + Modifier(GestureModifiers.Alt) + Modifier(GestureModifiers.Control)
            + (flags.HasFlag(GestureModifiers.Shift) ? "⇧" : "");
        string name = gesture.Key is { } keyboard ? KeyLabel(keyboard.Key) : gesture.Mouse!.Action switch
        {
            MouseAction.WheelUp => "滚轮上", MouseAction.WheelDown => "滚轮下", MouseAction.MiddleClick => "中键",
            MouseAction.LeftClick => "左键", MouseAction.RightClick => "右键", MouseAction.XButton1Click => "侧键 1", _ => "侧键 2",
        };
        return modifiers + name;
    }

    /// <summary>键枚举转键帽文本；未登记的特殊键回退枚举名。</summary>
    /// <param name="key">键枚举。</param>
    internal static string KeyLabel(GestureKey key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal)) return "数字 " + name[6..];
        return key switch
        {
            GestureKey.Left => "←", GestureKey.Right => "→", GestureKey.Up => "↑", GestureKey.Down => "↓",
            GestureKey.Return or GestureKey.Enter => "↩", GestureKey.Tab => "⇥", GestureKey.Back => "⌫",
            GestureKey.Delete => "⌦", GestureKey.Escape => "⎋", GestureKey.Space => "空格",
            GestureKey.Home => "↖", GestureKey.End => "↘", GestureKey.PageUp or GestureKey.Prior => "⇞",
            GestureKey.PageDown or GestureKey.Next => "⇟", GestureKey.Insert => "插入", GestureKey.CapsLock or GestureKey.Capital => "⇪",
            GestureKey.NumLock => "数字锁定", GestureKey.PrintScreen or GestureKey.Snapshot => "截屏", GestureKey.Scroll => "滚动锁定", GestureKey.Pause => "暂停",
            GestureKey.OemPlus => "=", GestureKey.OemMinus => "−", GestureKey.OemTilde or GestureKey.Oem3 => "`",
            GestureKey.OemComma => ",", GestureKey.OemPeriod => ".", GestureKey.OemQuestion or GestureKey.Oem2 => "/",
            GestureKey.OemSemicolon or GestureKey.Oem1 => ";", GestureKey.OemQuotes or GestureKey.Oem7 => "'",
            GestureKey.OemOpenBrackets or GestureKey.Oem4 => "[", GestureKey.OemCloseBrackets or GestureKey.Oem6 => "]",
            GestureKey.OemPipe or GestureKey.Oem5 or GestureKey.OemBackslash or GestureKey.Oem102 => "\\",
            GestureKey.Add => "数字 +", GestureKey.Subtract => "数字 −", GestureKey.Multiply => "数字 ×",
            GestureKey.Divide => "数字 /", GestureKey.Decimal => "数字 .", _ => name,
        };
    }
}
