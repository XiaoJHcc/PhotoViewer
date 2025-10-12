using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Input;
using ReactiveUI;
using System.Reactive;
using PhotoViewer.Controls;
using System;

namespace PhotoViewer.ViewModels;

// 新增：鼠标动作与通用手势
public enum MouseAction
{
    LeftClick,
    RightClick,
    MiddleClick,   // 滚轮按压
    XButton1Click, // 侧键1
    XButton2Click, // 侧键2
    WheelUp,
    WheelDown
}

public sealed class MouseGestureEx
{
    public MouseAction Action { get; }
    public KeyModifiers Modifiers { get; }

    public MouseGestureEx(MouseAction action, KeyModifiers modifiers)
    {
        Action = action;
        Modifiers = modifiers;
    }
    
    public MouseGestureEx(MouseAction action)
    {
        Action = action;
        Modifiers = KeyModifiers.None;
    }

    public string ToDisplayString()
    {
        var mods = Modifiers;
        var parts = new System.Collections.Generic.List<string>();
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add(AppleKeyboardMapping.GetShiftDisplay());
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(KeyModifiers.Control));
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(KeyModifiers.Alt));
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add(AppleKeyboardMapping.GetDisplayForModifier(KeyModifiers.Meta));

        string actionName = Action switch
        {
            MouseAction.LeftClick => "左键",
            MouseAction.RightClick => "右键",
            MouseAction.MiddleClick => "中键",
            MouseAction.XButton1Click => "侧键1",
            MouseAction.XButton2Click => "侧键2",
            MouseAction.WheelUp => "滚轮上",
            MouseAction.WheelDown => "滚轮下",
            _ => "鼠标"
        };

        parts.Add(actionName);
        return string.Join(" ", parts);
    }
}

public sealed class AppGesture
{
    public KeyGesture? Key { get; }
    public MouseGestureEx? Mouse { get; }

    private AppGesture(KeyGesture? key, MouseGestureEx? mouse)
    {
        Key = key;
        Mouse = mouse;
    }

    public static AppGesture FromKey(KeyGesture key) => new AppGesture(key, null);
    public static AppGesture FromMouse(MouseGestureEx mouse) => new AppGesture(null, mouse);

    public string ToDisplayString()
    {
        if (Key != null)
        {
            // 交给 Key 转换器（HotkeyButton 内也会处理）
            return Key.ToString() ?? "未设置";
        }
        if (Mouse != null) return Mouse.ToDisplayString();
        return "未设置";
    }
}

public partial class SettingsViewModel
{
    //////////////
    /// 快捷键设置
    //////////////
    
    private ObservableCollection<HotkeyItem> _hotkeys = new();
    public ObservableCollection<HotkeyItem> Hotkeys
    {
        get => _hotkeys;
        set => this.RaiseAndSetIfChanged(ref _hotkeys, value);
    }

    private void InitializeHotkeys()
    {
        MoveHotkeyCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveHotkey);
        
        Hotkeys = new ObservableCollection<HotkeyItem>
        {
            new("打开文件", "Open", "\uf6b5", "打开文件", true, 
                AppGesture.FromKey(new KeyGesture(Key.N, KeyModifiers.Control)), 
                AppGesture.FromKey(new KeyGesture(Key.O, KeyModifiers.Control))),
            new("上一张", "Previous", "\uf151", "上一张", true, 
                AppGesture.FromKey(new KeyGesture(Key.Left)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp))),
            new("下一张", "Next", "\uf152", "下一张", true, 
                AppGesture.FromKey(new KeyGesture(Key.Right)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelDown))),
            new("切换上一张", "Exchange", "\uf5ea", "切换上一张", false, 
                AppGesture.FromKey(new KeyGesture(Key.Z)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.MiddleClick))),
            new("缩放适应", "Fit", "\uf1b2", "缩放适应", true, 
                AppGesture.FromKey(new KeyGesture(Key.D0, KeyModifiers.Control)), 
                AppGesture.FromKey(new KeyGesture(Key.F))),
            new("放大（预设）", "ZoomInPreset", "\ufaac", "放大", true, 
                AppGesture.FromKey(new KeyGesture(Key.OemPlus, KeyModifiers.Control)), 
                null),
            new("缩小（预设）", "ZoomOutPreset", "\uf94e", "缩小", true, 
                AppGesture.FromKey(new KeyGesture(Key.OemMinus, KeyModifiers.Control)), 
                null),
            new("放大（比例）", "ZoomInScale", "\ufaac", "放大", false, 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp, KeyModifiers.Control)), 
                null),
            new("缩小（比例）", "ZoomOutScale", "\uf94e", "缩小", false, 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelDown, KeyModifiers.Control)), 
                null),
        };

        // 监听集合变化
        Hotkeys.CollectionChanged += OnHotkeysChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged += OnHotkeyItemChanged;
        }

        // 新增：默认映射设置（苹果平台默认开启“使用苹果键盘”，Cmd => Ctrl，Ctrl => Ctrl，Option => Alt）
        if (!_appleMappingInitializedOnce)
        {
            UseAppleKeyboard = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
            MapCommandTarget = AppleMappingTarget.Ctrl;
            MapOptionTarget  = AppleMappingTarget.Alt;
            MapControlTarget = AppleMappingTarget.Ctrl;
            _appleMappingInitializedOnce = true;
        }

        // 同步到全局映射器
        AppleKeyboardMapping.UseAppleKeyboard = UseAppleKeyboard;
        AppleKeyboardMapping.MapCommandTarget = MapCommandTarget;
        AppleKeyboardMapping.MapOptionTarget  = MapOptionTarget;
        AppleKeyboardMapping.MapControlTarget = MapControlTarget;
        AppleKeyboardMapping.RaiseChanged();
    }

    private void OnHotkeysChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 为新添加的项目订阅属性变化
        if (e.NewItems != null)
        {
            foreach (HotkeyItem item in e.NewItems)
            {
                item.PropertyChanged += OnHotkeyItemChanged;
            }
        }

        // 为移除的项目取消订阅
        if (e.OldItems != null)
        {
            foreach (HotkeyItem item in e.OldItems)
            {
                item.PropertyChanged -= OnHotkeyItemChanged;
            }
        }
    }

    private void OnHotkeyItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 当快捷键变化时检测冲突
        if (e.PropertyName == nameof(HotkeyItem.PrimaryHotkey) || 
            e.PropertyName == nameof(HotkeyItem.SecondaryHotkey))
        {
            CheckHotkeyConflicts();
        }
    }

    public void CheckHotkeyConflicts()
    {
        // 清除所有冲突标记
        foreach (var item in Hotkeys)
        {
            item.HasPrimaryConflict = false;
            item.HasSecondaryConflict = false;
        }

        // 检查冲突（键盘/鼠标类型各自匹配）
        for (int i = 0; i < Hotkeys.Count; i++)
        {
            var current = Hotkeys[i];
            
            for (int j = i + 1; j < Hotkeys.Count; j++)
            {
                var other = Hotkeys[j];
                
                if (AreGesturesEqual(current.PrimaryHotkey, other.PrimaryHotkey))
                {
                    current.HasPrimaryConflict = true;
                    other.HasPrimaryConflict = true;
                }
                if (AreGesturesEqual(current.PrimaryHotkey, other.SecondaryHotkey))
                {
                    current.HasPrimaryConflict = true;
                    other.HasSecondaryConflict = true;
                }
                if (AreGesturesEqual(current.SecondaryHotkey, other.PrimaryHotkey))
                {
                    current.HasSecondaryConflict = true;
                    other.HasPrimaryConflict = true;
                }
                if (AreGesturesEqual(current.SecondaryHotkey, other.SecondaryHotkey))
                {
                    current.HasSecondaryConflict = true;
                    other.HasSecondaryConflict = true;
                }
            }
        }
    }

    // 统一手势比较（同类型才有可比性）
    private static bool AreGesturesEqual(AppGesture? g1, AppGesture? g2)
    {
        if (g1 == null || g2 == null) return false;

        // 键盘
        if (g1.Key != null && g2.Key != null)
        {
            var k1 = NormalizeKeyForCompare(g1.Key.Key);
            var k2 = NormalizeKeyForCompare(g2.Key.Key);
            return k1 == k2 && g1.Key.KeyModifiers == g2.Key.KeyModifiers;
        }

        // 鼠标
        if (g1.Mouse != null && g2.Mouse != null)
        {
            return g1.Mouse.Action == g2.Mouse.Action &&
                   g1.Mouse.Modifiers == g2.Mouse.Modifiers;
        }

        return false;
    }

    // iOS 下将 Add/Subtract 视为 OemPlus/OemMinus，用于冲突检测等价比较
    private static Key NormalizeKeyForCompare(Key key)
    {
        if (OperatingSystem.IsIOS())
        {
            if (key == Key.Add) return Key.OemPlus;
            if (key == Key.Subtract) return Key.OemMinus;
        }
        return key;
    }

    // 新增：根据通用手势查找命令（用于执行）
    public string? GetCommandByGesture(AppGesture targetGesture)
    {
        foreach (var hotkeyItem in Hotkeys)
        {
            if (AreGesturesEqual(hotkeyItem.PrimaryHotkey, targetGesture) ||
                AreGesturesEqual(hotkeyItem.SecondaryHotkey, targetGesture))
                return hotkeyItem.Command;
        }
        return null;
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveHotkeyCommand { get; private set; }
    private void OnMoveHotkey(MoveCommandParameter parameter) => MoveHotkey(parameter.FromIndex, parameter.ToIndex);
    public void MoveHotkey(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Hotkeys.Count || 
            toIndex < 0 || toIndex >= Hotkeys.Count || 
            fromIndex == toIndex)
            return;

        var item = Hotkeys[fromIndex];
        Hotkeys.RemoveAt(fromIndex);
        Hotkeys.Insert(toIndex, item);
    }

    public class HotkeyItem : ReactiveObject
    {
        private string _name;
        private string _command;
        private string _displaySymbol;
        private string _tooltip;
        private bool _isDisplay;
        private AppGesture? _primaryHotkey;
        private AppGesture? _secondaryHotkey;

        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        public string Command
        {
            get => _command;
            set => this.RaiseAndSetIfChanged(ref _command, value);
        }

        public string DisplaySymbol
        {
            get => _displaySymbol;
            set => this.RaiseAndSetIfChanged(ref _displaySymbol, value);
        }

        public string Tooltip
        {
            get => _tooltip;
            set => this.RaiseAndSetIfChanged(ref _tooltip, value);
        }

        public bool IsDisplay
        {
            get => _isDisplay;
            set => this.RaiseAndSetIfChanged(ref _isDisplay, value);
        }

        public AppGesture? PrimaryHotkey
        {
            get => _primaryHotkey;
            set => this.RaiseAndSetIfChanged(ref _primaryHotkey, value);
        }

        public AppGesture? SecondaryHotkey
        {
            get => _secondaryHotkey;
            set => this.RaiseAndSetIfChanged(ref _secondaryHotkey, value);
        }

        public string PrimaryHotkeyText => PrimaryHotkey?.ToDisplayString() ?? "未设置";
        public string SecondaryHotkeyText => SecondaryHotkey?.ToDisplayString() ?? "未设置";

        private bool _hasPrimaryConflict;
        public bool HasPrimaryConflict
        {
            get => _hasPrimaryConflict;
            set => this.RaiseAndSetIfChanged(ref _hasPrimaryConflict, value);
        }

        private bool _hasSecondaryConflict;
        public bool HasSecondaryConflict
        {
            get => _hasSecondaryConflict;
            set => this.RaiseAndSetIfChanged(ref _hasSecondaryConflict, value);
        }

        public HotkeyItem(string name, string command, string displaySymbol, string tooltip, bool isDisplay = true, AppGesture? primaryHotkey = null, AppGesture? secondaryHotkey = null)
        {
            _name = name;
            _command = command;
            _displaySymbol = displaySymbol;
            _tooltip = tooltip;
            _isDisplay = isDisplay;
            _primaryHotkey = primaryHotkey;
            _secondaryHotkey = secondaryHotkey;
        }
    }

    // ========= 新增：映射设置属性 =========
    public AppleMappingTarget[] AppleMappingTargets { get; } = Enum.GetValues<AppleMappingTarget>();

    private bool _appleMappingInitializedOnce = false;

    private bool _useAppleKeyboard;
    public bool UseAppleKeyboard
    {
        get => _useAppleKeyboard;
        set
        {
            this.RaiseAndSetIfChanged(ref _useAppleKeyboard, value);
            AppleKeyboardMapping.UseAppleKeyboard = value;
            AppleKeyboardMapping.RaiseChanged();
        }
    }

    private AppleMappingTarget _mapCommandTarget;
    public AppleMappingTarget MapCommandTarget
    {
        get => _mapCommandTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _mapCommandTarget, value);
            AppleKeyboardMapping.MapCommandTarget = value;
            AppleKeyboardMapping.RaiseChanged();
        }
    }

    private AppleMappingTarget _mapOptionTarget;
    public AppleMappingTarget MapOptionTarget
    {
        get => _mapOptionTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _mapOptionTarget, value);
            AppleKeyboardMapping.MapOptionTarget = value;
            AppleKeyboardMapping.RaiseChanged();
        }
    }

    private AppleMappingTarget _mapControlTarget;
    public AppleMappingTarget MapControlTarget
    {
        get => _mapControlTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _mapControlTarget, value);
            AppleKeyboardMapping.MapControlTarget = value;
            AppleKeyboardMapping.RaiseChanged();
        }
    }
}

// ========= 新增：映射目标枚举 =========
public enum AppleMappingTarget
{
    None,
    Ctrl,
    Alt,
    Win
}

// ========= 更新：全局苹果键盘映射器 =========
public static class AppleKeyboardMapping
{
    public static bool UseAppleKeyboard { get; set; }

    public static AppleMappingTarget MapCommandTarget { get; set; }
    public static AppleMappingTarget MapOptionTarget  { get; set; }
    public static AppleMappingTarget MapControlTarget { get; set; }

    public static event Action? MappingChanged;
    public static void RaiseChanged() => MappingChanged?.Invoke();

    public static KeyModifiers ApplyForRuntime(KeyModifiers mods)
    {
        if (!UseAppleKeyboard) return mods;

        var result = mods;
        result = ApplySource(result, KeyModifiers.Meta,    MapCommandTarget); // Cmd
        result = ApplySource(result, KeyModifiers.Alt,     MapOptionTarget);  // Option
        result = ApplySource(result, KeyModifiers.Control, MapControlTarget); // Ctrl
        return result;
    }

    public static KeyModifiers ApplyForCapture(KeyModifiers mods) => ApplyForRuntime(mods);

    // 显示目标修饰键的文本（若有映射到此目标则按 Cmd ⌘ → Option ⌥ → Ctrl ⌃ 优先展示符号）
    public static string GetDisplayForModifier(KeyModifiers targetFlag)
    {
        string defaultName = targetFlag switch
        {
            KeyModifiers.Control => "Ctrl",
            KeyModifiers.Alt     => "Alt",
            KeyModifiers.Meta    => "Win",
            _ => targetFlag.ToString()
        };

        if (!UseAppleKeyboard) return defaultName;

        bool cmd = ToModifiers(MapCommandTarget) == targetFlag;
        bool opt = ToModifiers(MapOptionTarget)  == targetFlag;
        bool ctl = ToModifiers(MapControlTarget) == targetFlag;

        if (cmd) return "⌘";
        if (opt) return "⌥";
        if (ctl) return "⌃";
        return defaultName;
    }

    // 新增：Shift 的显示（开启苹果键盘时显示 ⇧）
    public static string GetShiftDisplay() => UseAppleKeyboard ? "⇧" : "Shift";

    private static KeyModifiers ApplySource(KeyModifiers current, KeyModifiers sourceFlag, AppleMappingTarget target)
    {
        if (target == AppleMappingTarget.None) return current;
        if (!current.HasFlag(sourceFlag)) return current;

        current &= ~sourceFlag;                 // 去掉原来源键
        current |= ToModifiers(target);         // 添加目标键
        return current;
    }

    private static KeyModifiers ToModifiers(AppleMappingTarget target) => target switch
    {
        AppleMappingTarget.Ctrl => KeyModifiers.Control,
        AppleMappingTarget.Alt  => KeyModifiers.Alt,
        AppleMappingTarget.Win  => KeyModifiers.Meta,
        _ => 0
    };
}
