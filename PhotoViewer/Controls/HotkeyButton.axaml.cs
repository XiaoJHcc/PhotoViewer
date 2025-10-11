using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PhotoViewer.Converters;

namespace PhotoViewer.Controls;

public partial class HotkeyButton : UserControl
{
    // 依赖属性：快捷键
    public static readonly StyledProperty<KeyGesture?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyButton, KeyGesture?>(nameof(Hotkey));

    public KeyGesture? Hotkey
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

    static HotkeyButton()
    {
        HotkeyProperty.Changed.AddClassHandler<HotkeyButton>((x, e) => x.OnHotkeyChanged());
        HasConflictProperty.Changed.AddClassHandler<HotkeyButton>((x, e) => x.OnConflictChanged());
    }

    public HotkeyButton()
    {
        InitializeComponent();
        UpdateHotkeyText();
    }

    private void OnHotkeyChanged()
    {
        UpdateHotkeyText();
    }

    private void UpdateHotkeyText()
    {
        var displayText = _converter.Convert(Hotkey, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "未设置";
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
        
        // 监听全局键盘事件
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown += OnGlobalKeyDown;
        }
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

        // 处理 Escape 键取消输入
        if (e.Key == Key.Escape)
        {
            StopCapturing();
            e.Handled = true;
            return;
        }

        // 处理 Delete 键清除快捷键
        if (e.Key == Key.Delete)
        {
            Hotkey = null;
            RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
            StopCapturing();
            e.Handled = true;
            return;
        }

        // 基于物理键标准化，正确区分主键盘 +/- 与小键盘 +/-（并修正 iOS/macOS 的映射差异）
        var normalizedKey = NormalizeKey(e);

        // 创建新的快捷键
        var newHotkey = new KeyGesture(normalizedKey, e.KeyModifiers);
        
        // 更新快捷键
        Hotkey = newHotkey;
        
        // 触发事件
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
        
        StopCapturing();
        e.Handled = true;
    }

    private void StopCapturing()
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        HotkeyBtn.Classes.Remove("Capturing");
        
        // 移除全局键盘事件监听
        if (this.GetVisualRoot() is TopLevel topLevel)
        {
            topLevel.KeyDown -= OnGlobalKeyDown;
        }
        
        UpdateHotkeyText();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopCapturing();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        OnConflictChanged(); // 确保模板应用后状态正确
    }

    // 使用 PhysicalKey 标准化逻辑键，修复 iOS/macOS 的键位映射问题
    private static Key NormalizeKey(KeyEventArgs e)
    {
        // 优先用物理键定位行列来源
        switch (e.PhysicalKey)
        {
            case PhysicalKey.Equal:           // 主键盘 "="（Shift+"=" 为 "+")
                return Key.OemPlus;
            case PhysicalKey.Minus:           // 主键盘 "-"
                return Key.OemMinus;
            case PhysicalKey.NumPadAdd:       // 小键盘 "+"
                return OperatingSystem.IsIOS() ? Key.OemPlus : Key.Add;
            case PhysicalKey.NumPadSubtract:  // 小键盘 "-"
                return OperatingSystem.IsIOS() ? Key.OemMinus : Key.Subtract;
        }

        // iOS 兼容：将 Add/Subtract 归一到 OemPlus/OemMinus，避免把主键盘当作小键盘
        if (OperatingSystem.IsIOS())
        {
            if (e.Key == Key.Add) return Key.OemPlus;
            if (e.Key == Key.Subtract) return Key.OemMinus;
        }

        return e.Key;
    }
}
