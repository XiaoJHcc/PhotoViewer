using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidInputManager = global::Android.Hardware.Input.InputManager;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 外设能力（INPUT-13）：通过系统 <see cref="AndroidInputManager"/> 的设备增删
/// 判断是否连接了硬件键盘、鼠标或触控板。手机内置的导航/音量虚拟设备与仅非字母键盘不计入，
/// 设置页按此决定是否提供键鼠录制控件。
/// </summary>
internal static class AndroidInputDevices
{
    private static AndroidInputManager? _manager;
    private static InputDeviceListener? _listener;
    private static bool _hasKeyboard;
    private static bool _hasPointer;
    private static bool? _keyboardOverride;
    private static bool? _pointerOverride;

    /// <summary>是否连接了可输入文字的硬件键盘。</summary>
    internal static bool HasExternalKeyboard => _keyboardOverride ?? _hasKeyboard;

    /// <summary>是否连接了鼠标或触控板。</summary>
    internal static bool HasPointer => _pointerOverride ?? _hasPointer;

    /// <summary>是否存在任一可录制键鼠手势的外设。</summary>
    internal static bool HasBindingDevice => HasExternalKeyboard || HasPointer;

    /// <summary>外设连接状态变化（主线程）。</summary>
    internal static event Action? Changed;

    /// <summary>开始监听系统输入设备增删；重复调用无副作用。</summary>
    /// <param name="activity">宿主 Activity。</param>
    internal static void Initialize(Activity activity)
    {
        if (_manager != null) return;
        _manager = activity.GetSystemService(Context.InputService) as AndroidInputManager;
        if (_manager == null) return;
        _listener = new InputDeviceListener();
        _manager.RegisterInputDeviceListener(_listener, new Handler(Looper.MainLooper!));
        Refresh();
    }

    /// <summary>重新读取系统设备列表并广播变化。</summary>
    internal static void Refresh()
    {
        bool keyboard = false;
        bool pointer = false;
        if (_manager != null)
        {
            foreach (int id in _manager.GetInputDeviceIds() ?? [])
            {
                var device = _manager.GetInputDevice(id);
                if (device == null) continue;
                keyboard |= IsAlphabeticKeyboard(device);
                pointer |= IsPointerDevice(device);
            }
        }
        if (keyboard == _hasKeyboard && pointer == _hasPointer) return;
        _hasKeyboard = keyboard;
        _hasPointer = pointer;
        Console.WriteLine($"[Native] 输入外设：键盘={keyboard} 鼠标/触控板={pointer}");
        Changed?.Invoke();
    }

    /// <summary>验收注入外设能力；null 表示回到系统检测。</summary>
    /// <param name="keyboard">键盘能力覆盖值。</param>
    /// <param name="pointer">指针能力覆盖值。</param>
    internal static void SetChecksOverride(bool? keyboard, bool? pointer)
    {
        _keyboardOverride = keyboard;
        _pointerOverride = pointer;
        Changed?.Invoke();
    }

    /// <summary>验收：按同一口径判断设备是否计入硬件键盘。</summary>
    internal static bool CountsAsKeyboard(InputDevice device) => IsAlphabeticKeyboard(device);

    /// <summary>验收：按同一口径判断设备是否计入鼠标/触控板。</summary>
    internal static bool CountsAsPointer(InputDevice device) => IsPointerDevice(device);

    /// <summary>硬件键盘判定：真实设备、具备键盘源、字母键盘，且确实有字母键。</summary>
    private static bool IsAlphabeticKeyboard(InputDevice device)
    {
        if (device.IsVirtual || device.Id < 0) return false;
        if ((device.Sources & InputSourceType.Keyboard) != InputSourceType.Keyboard) return false;
        if (device.KeyboardType != InputKeyboardType.Alphabetic) return false;
        // 部分电视/遥控设备的“键盘”只有方向与音量键；要求至少具备字母键才视为可录制键盘。
        var keys = device.HasKeys((int)Keycode.A);
        return keys != null && keys.Length > 0 && keys[0];
    }

    /// <summary>鼠标/触控板判定：真实设备的指针类来源，触屏与手写笔不算。</summary>
    private static bool IsPointerDevice(InputDevice device)
    {
        if (device.IsVirtual || device.Id < 0) return false;
        var sources = device.Sources;
        return (sources & InputSourceType.Mouse) == InputSourceType.Mouse
            || (sources & InputSourceType.Touchpad) == InputSourceType.Touchpad
            || (sources & InputSourceType.Trackball) == InputSourceType.Trackball;
    }

    /// <summary>系统输入设备监听器，只负责触发重新枚举。</summary>
    private sealed class InputDeviceListener : Java.Lang.Object, AndroidInputManager.IInputDeviceListener
    {
        /// <summary>设备接入。</summary>
        /// <param name="deviceId">设备 id。</param>
        public void OnInputDeviceAdded(int deviceId) => Refresh();

        /// <summary>设备移除。</summary>
        /// <param name="deviceId">设备 id。</param>
        public void OnInputDeviceRemoved(int deviceId) => Refresh();

        /// <summary>设备能力变化。</summary>
        /// <param name="deviceId">设备 id。</param>
        public void OnInputDeviceChanged(int deviceId) => Refresh();
    }
}
