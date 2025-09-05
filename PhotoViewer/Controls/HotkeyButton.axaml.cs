using System;
using Avalonia;
using Avalonia.Controls;
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

    private void OnHotkeyButtonClick(object? sender, RoutedEventArgs e)
    {
        StartCapturing();
    }

    private void StartCapturing()
    {
        if (_isCapturing) return;

        _isCapturing = true;
        HotkeyBtn.Classes.Add("Capturing");
        HotkeyBtn.Content = "按下快捷键...";

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

        // 创建新的快捷键
        var newHotkey = new KeyGesture(e.Key, e.KeyModifiers);
        
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
}
