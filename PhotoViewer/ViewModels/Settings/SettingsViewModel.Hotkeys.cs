using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using PhotoViewer.Controls;
using System;
using PhotoViewer.Core.Input;

namespace PhotoViewer.ViewModels.Settings;

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
        
        Hotkeys = new ObservableCollection<HotkeyItem>(BuildDefaultHotkeys());

        SubscribeAndInitHotkeyMapping();
    }

    /// <summary>
    /// 构建出厂默认热键列表（也是"合并缺失默认项"的来源，见 Persistence.ResetHotkeys）。
    /// </summary>
    public static System.Collections.Generic.List<HotkeyItem> BuildDefaultHotkeys() => new()
    {
            new("打开文件", "Open", "\uf6b5", "打开文件", false, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.N, GestureModifiers.Control)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.O, GestureModifiers.Control))),
            new("上一张", "Previous", "\uf151", "上一张", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.Left)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp))),
            new("下一张", "Next", "\uf152", "下一张", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.Right)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelDown))),
            new("切换上一张", "Exchange", "\uf5ea", "切换上一张", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.Z)), 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.MiddleClick))),
            new("缩放适应", "Fit", "\uf1b2", "缩放适应", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D0, GestureModifiers.Control)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.F))),
            new("放大（预设）", "ZoomInPreset", "\ufaac", "放大", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.OemPlus, GestureModifiers.Control)), 
                null),
            new("缩小（预设）", "ZoomOutPreset", "\uf94e", "缩小", true, 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.OemMinus, GestureModifiers.Control)), 
                null),
            new("放大（比例）", "ZoomInScale", "\ufaac", "放大", false, 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelUp, GestureModifiers.Control)), 
                null),
            new("缩小（比例）", "ZoomOutScale", "\uf94e", "缩小", false, 
                AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelDown, GestureModifiers.Control)), 
                null),
            new("分析栏展开/收纳", "ToggleAnalysisView", "\ufad2", "分析栏", false,
                null,
                null),

            new("增强预览", "ToggleEnhance", "", "增强预览", true,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.E)),
                null),

            // ===== 新增：评分热键（默认不在控制栏显示） =====
            new("评分 0 星", "SetRating0", "\uf4f4", "0 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.OemTilde)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad0))),
            new("评分 1 星", "SetRating1", "\uf3ea", "1 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D1)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad1))),
            new("评分 2 星", "SetRating2", "\uf3eb", "2 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D2)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad2))),
            new("评分 3 星", "SetRating3", "\uf3ec", "3 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D3)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad3))),
            new("评分 4 星", "SetRating4", "\uf3ed", "4 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D4)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad4))),
            new("评分 5 星", "SetRating5", "\uf3ee", "5 星", false,
                AppGesture.FromKey(new AppKeyGesture(GestureKey.D5)), 
                AppGesture.FromKey(new AppKeyGesture(GestureKey.NumPad5))),
    };

    /// <summary>
    /// 订阅热键集合 / 各项变化，并初始化苹果键盘映射（从原 InitializeHotkeys 尾部抽出）。
    /// </summary>
    private void SubscribeAndInitHotkeyMapping()
    {
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

        RequestSave();
    }

    private void OnHotkeyItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 当快捷键变化时检测冲突
        if (e.PropertyName == nameof(HotkeyItem.PrimaryHotkey) || 
            e.PropertyName == nameof(HotkeyItem.SecondaryHotkey))
        {
            CheckHotkeyConflicts();
        }

        RequestSave();
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
            return k1 == k2 && g1.Key.Modifiers == g2.Key.Modifiers;
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
    private static GestureKey NormalizeKeyForCompare(GestureKey key)
    {
        if (OperatingSystem.IsIOS())
        {
            if (key == GestureKey.Add) return GestureKey.OemPlus;
            if (key == GestureKey.Subtract) return GestureKey.OemMinus;
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
    public ReactiveCommand<MoveCommandParameter, Unit> MoveHotkeyCommand { get; private set; } = null!;
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
        RequestSave();
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

