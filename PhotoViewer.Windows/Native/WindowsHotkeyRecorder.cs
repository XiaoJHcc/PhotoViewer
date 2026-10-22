using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PhotoViewer.Core.Input;
using PhotoViewer.ViewModels.Settings;
using Windows.System;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 快捷键录制:挂在窗口根上捕获真实键盘/滚轮/中键,写入工作副本并重算冲突。
/// 只负责"录制态 + 手势赋值",按钮外观由行自己按 <see cref="StateChanged"/> 刷新。
/// </summary>
internal sealed class WindowsHotkeyRecorder
{
    private readonly SettingsViewModel _settings;
    private readonly KeyEventHandler _keyHandler;
    private readonly PointerEventHandler _wheelHandler;
    private readonly PointerEventHandler _pressedHandler;
    private UIElement? _root;
    private SettingsViewModel.HotkeyItem? _item;
    private bool _primary;

    /// <summary>创建录制器。</summary>
    /// <param name="settings">设置工作副本</param>
    public WindowsHotkeyRecorder(SettingsViewModel settings)
    {
        _settings = settings;
        _keyHandler = OnKeyEvent;
        _wheelHandler = OnPointerWheelChanged;
        _pressedHandler = OnPointerPressed;
    }

    /// <summary>录制态或绑定变化时触发,供行刷新文本与描边。</summary>
    public event Action? StateChanged;

    /// <summary>当前是否处于录制态。</summary>
    public bool IsRecording => _item != null;

    /// <summary>判断某行某侧是否正在录制。</summary>
    /// <param name="item">快捷键项</param>
    /// <param name="primary">是否主绑定</param>
    /// <returns>是否正在录制该绑定</returns>
    public bool IsRecordingBinding(SettingsViewModel.HotkeyItem item, bool primary) =>
        ReferenceEquals(_item, item) && _primary == primary;

    /// <summary>把捕获器挂到窗口根元素。</summary>
    /// <param name="root">窗口根元素</param>
    public void Attach(UIElement root)
    {
        Detach();
        _root = root;
        root.AddHandler(UIElement.PreviewKeyDownEvent, _keyHandler, true);
        root.AddHandler(UIElement.KeyDownEvent, _keyHandler, true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        root.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
    }

    /// <summary>退订捕获器并结束录制态。</summary>
    public void Detach()
    {
        if (_root != null)
        {
            _root.RemoveHandler(UIElement.PreviewKeyDownEvent, _keyHandler);
            _root.RemoveHandler(UIElement.KeyDownEvent, _keyHandler);
            _root.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
            _root.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            _root = null;
        }

        Stop();
    }

    /// <summary>进入录制态。</summary>
    /// <param name="item">目标快捷键项</param>
    /// <param name="primary">录制主绑定还是次绑定</param>
    public void Begin(SettingsViewModel.HotkeyItem item, bool primary)
    {
        _item = item;
        _primary = primary;
        StateChanged?.Invoke();
    }

    /// <summary>取消录制态(不写绑定)。</summary>
    public void Cancel()
    {
        if (_item == null)
        {
            return;
        }

        Stop();
        StateChanged?.Invoke();
    }

    /// <summary>键盘事件:录制态优先消费,Esc 取消、Delete/Backspace 清空。</summary>
    /// <param name="sender">事件源</param>
    /// <param name="args">键盘参数</param>
    private void OnKeyEvent(object sender, KeyRoutedEventArgs args) => HandleKey(args);

    /// <summary>处理一次按键。</summary>
    /// <param name="args">键盘参数</param>
    private void HandleKey(KeyRoutedEventArgs args)
    {
        if (_item == null)
        {
            return;
        }

        // 录制态吞掉所有按键:避免 Space/方向键被列表或导航栏拿去滚屏/移焦点。
        args.Handled = true;

        if (args.Key == VirtualKey.Escape)
        {
            Cancel();
            return;
        }

        if (args.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            Assign(null);
            return;
        }

        // 纯修饰键不产出手势,继续等真正的键。
        if (args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        var gesture = WindowsInput.FromKey(args.Key);
        if (gesture != null)
        {
            Assign(gesture);
        }
    }

    /// <summary>滚轮录制:正向为上滚,负向为下滚。</summary>
    /// <param name="sender">事件源</param>
    /// <param name="args">指针参数</param>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_item == null || _root == null)
        {
            return;
        }

        var delta = args.GetCurrentPoint(_root).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        args.Handled = true;
        Assign(WindowsInput.FromWheel(delta));
    }

    /// <summary>中键录制;左/右键留给界面操作。</summary>
    /// <param name="sender">事件源</param>
    /// <param name="args">指针参数</param>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_item == null || _root == null)
        {
            return;
        }

        if (!args.GetCurrentPoint(_root).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        args.Handled = true;
        Assign(WindowsInput.FromMouse(MouseAction.MiddleClick));
    }

    /// <summary>把录制到的手势写入当前行并重算冲突。</summary>
    /// <param name="gesture">手势;null 表示清空</param>
    private void Assign(AppGesture? gesture)
    {
        if (_item == null)
        {
            return;
        }

        if (_primary)
        {
            _item.PrimaryHotkey = gesture;
        }
        else
        {
            _item.SecondaryHotkey = gesture;
        }

        Stop();
        _settings.CheckHotkeyConflicts();
        StateChanged?.Invoke();
    }

    /// <summary>结束录制态。</summary>
    private void Stop()
    {
        _item = null;
        _primary = false;
    }
}
