using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Input;
using ReactiveUI;
using System.Reactive;
using PhotoViewer.Controls;

namespace PhotoViewer.ViewModels;

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
            new("打开文件", "Open", "\uf6b5", "打开文件", true, new KeyGesture(Key.N, KeyModifiers.Control), new KeyGesture(Key.O, KeyModifiers.Control)),
            new("上一张", "Previous", "\uf151", "上一张", true, new KeyGesture(Key.Left), new KeyGesture(Key.A)),
            new("下一张", "Next", "\uf152", "下一张", true, new KeyGesture(Key.Right), new KeyGesture(Key.D)),
            new("切换上一张", "Exchange", "\uf5ea", "切换上一张", false, new KeyGesture(Key.Z), new KeyGesture(Key.Y)),
            new("缩放适应", "Fit", "\uf1b2", "缩放适应", true, new KeyGesture(Key.D0, KeyModifiers.Control), new KeyGesture(Key.F)),
            new("放大", "ZoomIn", "\ufaac", "放大", true, new KeyGesture(Key.OemPlus, KeyModifiers.Control), null),
            new("缩小", "ZoomOut", "\uf94e", "缩小", true, new KeyGesture(Key.OemMinus, KeyModifiers.Control), null),
        };

        // 监听集合变化
        Hotkeys.CollectionChanged += OnHotkeysChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in Hotkeys)
        {
            item.PropertyChanged += OnHotkeyItemChanged;
        }
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

        // 检查冲突
        for (int i = 0; i < Hotkeys.Count; i++)
        {
            var current = Hotkeys[i];
            
            for (int j = i + 1; j < Hotkeys.Count; j++)
            {
                var other = Hotkeys[j];
                
                // 检查主要快捷键冲突
                if (current.PrimaryHotkey != null && other.PrimaryHotkey != null &&
                    AreKeyGesturesEqual(current.PrimaryHotkey, other.PrimaryHotkey))
                {
                    current.HasPrimaryConflict = true;
                    other.HasPrimaryConflict = true;
                }
                
                if (current.PrimaryHotkey != null && other.SecondaryHotkey != null &&
                    AreKeyGesturesEqual(current.PrimaryHotkey, other.SecondaryHotkey))
                {
                    current.HasPrimaryConflict = true;
                    other.HasSecondaryConflict = true;
                }
                
                // 检查次要快捷键冲突
                if (current.SecondaryHotkey != null && other.PrimaryHotkey != null &&
                    AreKeyGesturesEqual(current.SecondaryHotkey, other.PrimaryHotkey))
                {
                    current.HasSecondaryConflict = true;
                    other.HasPrimaryConflict = true;
                }
                
                if (current.SecondaryHotkey != null && other.SecondaryHotkey != null &&
                    AreKeyGesturesEqual(current.SecondaryHotkey, other.SecondaryHotkey))
                {
                    current.HasSecondaryConflict = true;
                    other.HasSecondaryConflict = true;
                }
            }
        }
    }

    private bool AreKeyGesturesEqual(KeyGesture? gesture1, KeyGesture? gesture2)
    {
        if (gesture1 == null || gesture2 == null)
            return false;
            
        return gesture1.Key == gesture2.Key && 
               gesture1.KeyModifiers == gesture2.KeyModifiers;
    }

    // 获取有效的快捷键（用于执行，优先级按列表顺序）
    public KeyGesture? GetEffectiveHotkey(KeyGesture targetGesture)
    {
        foreach (var hotkeyItem in Hotkeys)
        {
            if (AreKeyGesturesEqual(hotkeyItem.PrimaryHotkey, targetGesture))
                return hotkeyItem.PrimaryHotkey;
                
            if (AreKeyGesturesEqual(hotkeyItem.SecondaryHotkey, targetGesture))
                return hotkeyItem.SecondaryHotkey;
        }
        
        return null;
    }

    // 根据快捷键获取对应的命令名称（仅返回第一个匹配的）
    public string? GetCommandByHotkey(KeyGesture targetGesture)
    {
        foreach (var hotkeyItem in Hotkeys)
        {
            if (AreKeyGesturesEqual(hotkeyItem.PrimaryHotkey, targetGesture) ||
                AreKeyGesturesEqual(hotkeyItem.SecondaryHotkey, targetGesture))
                return hotkeyItem.Command;
        }
        
        return null;
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveHotkeyCommand { get; private set; }

    private void OnMoveHotkey(MoveCommandParameter parameter)
    {
        MoveHotkey(parameter.FromIndex, parameter.ToIndex);
    }

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
        private KeyGesture? _primaryHotkey;
        private KeyGesture? _secondaryHotkey;

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

        public KeyGesture? PrimaryHotkey
        {
            get => _primaryHotkey;
            set => this.RaiseAndSetIfChanged(ref _primaryHotkey, value);
        }

        public KeyGesture? SecondaryHotkey
        {
            get => _secondaryHotkey;
            set => this.RaiseAndSetIfChanged(ref _secondaryHotkey, value);
        }

        public string PrimaryHotkeyText => PrimaryHotkey?.ToString() ?? "未设置";
        public string SecondaryHotkeyText => SecondaryHotkey?.ToString() ?? "未设置";

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

        public HotkeyItem(string name, string command, string displaySymbol, string tooltip, bool isDisplay = true, KeyGesture? primaryHotkey = null, KeyGesture? secondaryHotkey = null)
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
}

