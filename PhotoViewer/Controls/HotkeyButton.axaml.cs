using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PhotoViewer.Converters;
using PhotoViewer.ViewModels; // 新增：用于读取全局映射

namespace PhotoViewer.Controls;

public partial class HotkeyButton : UserControl
{
    // 依赖属性：快捷键（更新为通用手势）
    public static readonly StyledProperty<AppGesture?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyButton, AppGesture?>(nameof(Hotkey));

    public AppGesture? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    // 依赖属性：快捷键文本显示 - 修改为 public set
    public static readonly StyledProperty<string> HotkeyTextProperty =
        AvaloniaProperty.Register<HotkeyButton, string>(nameof(HotkeyText), "未设置");

    public string HotkeyText
    {
        get => GetValue(HotkeyTextProperty);
        set => SetValue(HotkeyTextProperty, value); // 改为 public
    }

    // 依赖属性：冲突状态
    public static readonly StyledProperty<bool> HasConflictProperty =
        AvaloniaProperty.Register<HotkeyButton, bool>(nameof(HasConflict), false);

    public bool HasConflict
    {
        get => GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    // 路由事件：快捷键改变
    public static readonly RoutedEvent<RoutedEventArgs> HotkeyChangedEvent =
        RoutedEvent.Register<HotkeyButton, RoutedEventArgs>(nameof(HotkeyChanged), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> HotkeyChanged
    {
        add => AddHandler(HotkeyChangedEvent, value);
        remove => RemoveHandler(HotkeyChangedEvent, value);
    }

    private bool _isCapturing = false;
    private static readonly KeyGestureToStringConverter _converter = new();
    private TopLevel? _topLevel;

    static HotkeyButton()
    {
        HotkeyProperty.Changed.AddClassHandler<HotkeyButton>((x, e) => x.OnHotkeyChanged());
        HasConflictProperty.Changed.AddClassHandler<HotkeyButton>((x, e) => x.OnConflictChanged());
    }

    public HotkeyButton()
    {
        InitializeComponent();
        UpdateHotkeyText();
        AppleKeyboardMapping.MappingChanged += OnAppleMappingChanged;
    }

    private void OnAppleMappingChanged()
    {
        // 可能在后台线程触发，切回 UI 线程刷新
        if (HotkeyBtn is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateHotkeyText);
        }
    }

    private void OnHotkeyChanged()
    {
        UpdateHotkeyText();
    }

    private void UpdateHotkeyText()
    {
        string displayText = "未设置";
        if (Hotkey?.Key != null)
        {
            displayText = _converter.Convert(Hotkey.Key, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "未设置";
        }
        else if (Hotkey?.Mouse != null)
        {
            displayText = Hotkey.Mouse.ToDisplayString();
        }
        HotkeyText = displayText;
        
        // 直接更新按钮内容
        if (HotkeyBtn != null && !_isCapturing)
        {
            HotkeyBtn.Content = displayText;
        }
    }

    private void OnConflictChanged()
    {
        if (HotkeyBtn != null)
        {
            if (HasConflict)
            {
                HotkeyBtn.Classes.Add("Conflict");
            }
            else
            {
                HotkeyBtn.Classes.Remove("Conflict");
            }
        }
    }

    private void OnHotkeyButtonClick(object? sender, RoutedEventArgs e)
    {
        StartCapturing();
    }

    private void StartCapturing()
    {
        if (_isCapturing) return;

        _isCapturing = true;
        HotkeyBtn.Classes.Add("Capturing");
        HotkeyBtn.Content = "按下快捷键";

        // 获取焦点以接收键盘事件
        this.Focus();
        
        // 监听全局事件（滚轮使用路由事件拦截以阻止 UI 滚动）
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            _topLevel = topLevel;
            _topLevel.KeyDown += OnGlobalKeyDown;
            _topLevel.PointerPressed += OnGlobalPointerPressed;
            // _topLevel.PointerWheelChanged += OnGlobalPointerWheelChanged; // 改为路由事件拦截
            _topLevel.AddHandler(InputElement.PointerWheelChangedEvent,
                                 OnCapturePointerWheelChanged,
                                 RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                                 handledEventsToo: true);
        }
    }

    private void StopCapturing()
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        HotkeyBtn.Classes.Remove("Capturing");
        
        if (_topLevel != null)
        {
            _topLevel.KeyDown -= OnGlobalKeyDown;
            _topLevel.PointerPressed -= OnGlobalPointerPressed;
            // _topLevel.PointerWheelChanged -= OnGlobalPointerWheelChanged;
            _topLevel.RemoveHandler(InputElement.PointerWheelChangedEvent, OnCapturePointerWheelChanged);
            _topLevel = null;
        }
        
        UpdateHotkeyText();
    }

    // 用路由事件在捕获模式下拦截滚轮（隧道阶段先于 ScrollViewer）
    private void OnCapturePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_isCapturing) return;

        // 拦截滚动
        e.Handled = true;

        var action = e.Delta.Y > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        var mods = AppleKeyboardMapping.ApplyForCapture(e.KeyModifiers);

        Hotkey = AppGesture.FromMouse(new MouseGestureEx(action, mods));
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
        StopCapturing();
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isCapturing) return;

        // 忽略单独的修饰键
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LWin || e.Key == Key.RWin)
        {
            return;
        }

        // 处理 Escape 取消
        if (e.Key == Key.Escape)
        {
            StopCapturing();
            e.Handled = true;
            return;
        }

        // 删除类键 清除快捷键
        if (e.Key is Key.Delete or Key.Back or Key.Clear or Key.OemClear or Key.OemBackslash)
        {
            Hotkey = null;
            RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
            StopCapturing();
            e.Handled = true;
            return;
        }

        // 基于物理键标准化，正确区分主键盘 +/- 与小键盘 +/-（并修正 iOS/macOS 的映射差异）
        var normalizedKey = NormalizeKey(e);
        var mappedMods = AppleKeyboardMapping.ApplyForCapture(e.KeyModifiers);
        Hotkey = AppGesture.FromKey(new KeyGesture(normalizedKey, mappedMods));
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
        StopCapturing();
        e.Handled = true;
    }

    // 新增：捕获鼠标按压（含侧键/中键）
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isCapturing) return;
        if (e.Pointer.Type != PointerType.Mouse) return;

        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        MouseAction? action = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseAction.LeftClick,
            PointerUpdateKind.RightButtonPressed => MouseAction.RightClick,
            PointerUpdateKind.MiddleButtonPressed => MouseAction.MiddleClick,
            PointerUpdateKind.XButton1Pressed => MouseAction.XButton1Click,
            PointerUpdateKind.XButton2Pressed => MouseAction.XButton2Click,
            _ => null
        };
        if (action == null) return;

        var mods = AppleKeyboardMapping.ApplyForCapture(e.KeyModifiers);

        // 禁止单独左/右键
        if ((action is MouseAction.LeftClick or MouseAction.RightClick) && mods == KeyModifiers.None)
        {
            return;
        }

        Hotkey = AppGesture.FromMouse(new MouseGestureEx(action.Value, mods));
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
        StopCapturing();
        e.Handled = true;
    }

    // 新增：捕获滚轮上下（保留以兼容性，实际拦截由 OnCapturePointerWheelChanged 完成）
    private void OnGlobalPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_isCapturing) return;

        var action = e.Delta.Y > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        var mods = AppleKeyboardMapping.ApplyForCapture(e.KeyModifiers);

        Hotkey = AppGesture.FromMouse(new MouseGestureEx(action, mods));
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
        StopCapturing();
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopCapturing();
        AppleKeyboardMapping.MappingChanged -= OnAppleMappingChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // 使用 PhysicalKey 标准化逻辑键，修复 iOS/macOS 的键位映射问题
    private static Key NormalizeKey(KeyEventArgs e)
    {
        // 优先用物理键定位行列来源
        switch (e.PhysicalKey)
        {
            case PhysicalKey.Equal:           return Key.OemPlus;
            case PhysicalKey.Minus:           return Key.OemMinus;
            case PhysicalKey.NumPadAdd:       return OperatingSystem.IsIOS() ? Key.OemPlus : Key.Add;
            case PhysicalKey.NumPadSubtract:  return OperatingSystem.IsIOS() ? Key.OemMinus : Key.Subtract;
        }
        if (OperatingSystem.IsIOS())
        {
            if (e.Key == Key.Add) return Key.OemPlus;
            if (e.Key == Key.Subtract) return Key.OemMinus;
        }
        return e.Key;
    }
}
