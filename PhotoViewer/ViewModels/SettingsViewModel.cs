using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using System.Collections.Generic;
using PhotoViewer.Controls;
using Avalonia.Input;

namespace PhotoViewer.ViewModels;

// 布局模式枚举
public enum LayoutMode
{
    Vertical,    // 上中下
    Horizontal,  // 左中右
    Auto         // 智能（根据屏幕方向）
}

public class SettingsViewModel : ReactiveObject
{
    public SettingsViewModel()
    {
        SortScalePreset();
        InitializeFileFormats();
        InitializeHotkeys();
        InitializeLayoutModes();
        InitializeExifDisplayItems();
        
        // 初始化移动命令
        MoveFileFormatCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveFileFormat);
        MoveHotkeyCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveHotkey);
        MoveExifDisplayCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveExifDisplay);
    }
    
    //////////////
    /// 布局
    //////////////
    
    #region LayoutModeSetting

    // 布局模式
    private LayoutMode _layoutMode = LayoutMode.Auto;
    public LayoutMode LayoutMode
    {
        get => _layoutMode;
        set => this.RaiseAndSetIfChanged(ref _layoutMode, value);
    }

    // 布局模式选项
    public ObservableCollection<LayoutModeItem> LayoutModes { get; } = new();

    private void InitializeLayoutModes()
    {
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Vertical, "上下", "缩略图和控制栏位于上下侧，适合竖屏"));
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Horizontal, "左右", "缩略图和控制栏位于左右侧，适合横屏"));
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Auto, "自动", "根据屏幕方向自动选择空间较多两侧"));
    }

    public class LayoutModeItem
    {
        public LayoutMode Value { get; }
        public string DisplayName { get; }
        public string Description { get; }

        public LayoutModeItem(LayoutMode value, string displayName, string description)
        {
            Value = value;
            DisplayName = displayName;
            Description = description;
        }
    }

    #endregion

    //////////////
    /// 文件格式支持
    //////////////
    
    #region FileFormatSetting

    private ObservableCollection<FileFormatItem> _fileFormats = new();
    public ObservableCollection<FileFormatItem> FileFormats
    {
        get => _fileFormats;
        set => this.RaiseAndSetIfChanged(ref _fileFormats, value);
    }

    private List<string> _selectedFormats = new();
    public List<string> SelectedFormats
    {
        get => _selectedFormats;
        private set => this.RaiseAndSetIfChanged(ref _selectedFormats, value);
    }

    private void InitializeFileFormats()
    {
        FileFormats = new ObservableCollection<FileFormatItem>
        {
            new("JPG", new[] { ".jpg", ".jpeg" }, true),
            new("PNG", new[] { ".png" }, true),
            new("TIFF", new[] { ".tiff", ".tif" }, false),
            new("WebP", new[] { ".webp" }, true),
            // new("RAW", new[] { ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".srw" }, false),
            new("BMP", new[] { ".bmp" }, false),
            new("GIF", new[] { ".gif" }, false),
        };

        // 监听集合变化
        FileFormats.CollectionChanged += OnFileFormatsChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in FileFormats)
        {
            item.PropertyChanged += OnFileFormatItemChanged;
        }

        UpdateSelectedFormats();
    }

    private void OnFileFormatsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 为新添加的项目订阅属性变化
        if (e.NewItems != null)
        {
            foreach (FileFormatItem item in e.NewItems)
            {
                item.PropertyChanged += OnFileFormatItemChanged;
            }
        }

        // 为移除的项目取消订阅
        if (e.OldItems != null)
        {
            foreach (FileFormatItem item in e.OldItems)
            {
                item.PropertyChanged -= OnFileFormatItemChanged;
            }
        }

        UpdateSelectedFormats();
    }

    private void OnFileFormatItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileFormatItem.IsEnabled))
        {
            UpdateSelectedFormats();
        }
    }

    private void UpdateSelectedFormats()
    {
        SelectedFormats = FileFormats
            .Where(f => f.IsEnabled)
            .SelectMany(f => f.Extensions)
            .ToList();
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveFileFormatCommand { get; private set; }

    private void OnMoveFileFormat(MoveCommandParameter parameter)
    {
        MoveFileFormat(parameter.FromIndex, parameter.ToIndex);
    }

    public void MoveFileFormat(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= FileFormats.Count || 
            toIndex < 0 || toIndex >= FileFormats.Count || 
            fromIndex == toIndex)
            return;

        var item = FileFormats[fromIndex];
        FileFormats.RemoveAt(fromIndex);
        FileFormats.Insert(toIndex, item);
        
        // 移动操作会触发 CollectionChanged 事件，自动更新 SelectedFormats
    }

    public class FileFormatItem : ReactiveObject
    {
        private string _displayName;
        private string[] _extensions;
        private bool _isEnabled;

        public string DisplayName
        {
            get => _displayName;
            set => this.RaiseAndSetIfChanged(ref _displayName, value);
        }

        public string[] Extensions
        {
            get => _extensions;
            set => this.RaiseAndSetIfChanged(ref _extensions, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        // 用于显示的扩展名字符串
        public string ExtensionsText => string.Join(" / ", Extensions);

        // 保持向后兼容的 Name 属性
        public string Name => DisplayName;

        public FileFormatItem(string displayName, string[] extensions, bool isEnabled = true)
        {
            _displayName = displayName;
            _extensions = extensions;
            _isEnabled = isEnabled;
        }
    }
        
    #endregion

    //////////////
    /// 同名文件处理
    //////////////
    
    #region SameNameFileHandling

    private bool _sameNameAsOnePhoto = true;
    public bool SameNameAsOnePhoto
    {
        get => _sameNameAsOnePhoto;
        set => this.RaiseAndSetIfChanged(ref _sameNameAsOnePhoto, value);
    }

    #endregion

    //////////////
    /// 快捷键设置
    //////////////
    
    #region HotkeySetting

    private ObservableCollection<HotkeyItem> _hotkeys = new();
    public ObservableCollection<HotkeyItem> Hotkeys
    {
        get => _hotkeys;
        set => this.RaiseAndSetIfChanged(ref _hotkeys, value);
    }

    private void InitializeHotkeys()
    {
        Hotkeys = new ObservableCollection<HotkeyItem>
        {
            new("打开文件", "Open", "\uf6b5", "打开文件", true, new KeyGesture(Key.N, KeyModifiers.Control), new KeyGesture(Key.O, KeyModifiers.Control)),
            new("上一张", "Previous", "\uf151", "上一张", true, new KeyGesture(Key.Left), new KeyGesture(Key.A)),
            new("下一张", "Next", "\uf152", "下一张", true, new KeyGesture(Key.Right), new KeyGesture(Key.D)),
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
            if (!hotkeyItem.IsEnabled) continue;
            
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
            if (!hotkeyItem.IsEnabled) continue;
            
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
        private bool _isEnabled;
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

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
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

        public HotkeyItem(string name, string command, string displaySymbol, string tooltip, bool isEnabled = true, KeyGesture? primaryHotkey = null, KeyGesture? secondaryHotkey = null)
        {
            _name = name;
            _command = command;
            _displaySymbol = displaySymbol;
            _tooltip = tooltip;
            _isEnabled = isEnabled;
            _primaryHotkey = primaryHotkey;
            _secondaryHotkey = secondaryHotkey;
        }
    }
        
    #endregion

    //////////////
    /// 缩放倍率预设
    //////////////
        
    #region ScalePresetSetting
        
    private ObservableCollection<ScalePreset> _scalePresets = [
        new("12.5"), 
        new("25"), 
        new("33.333"),
        new("50"),
        new("75"),
        new("100"),
        new("200"),
        new("400")
    ];
    public ObservableCollection<ScalePreset> ScalePresets
    {
        get => _scalePresets;
        set => this.RaiseAndSetIfChanged(ref _scalePresets, value);
    }
    // 添加预设 +
    public void AddScalePreset() => ScalePresets.Add(new("100"));
    // 删除预设 -
    public void RemoveScalePreset(ScalePreset item) => ScalePresets.Remove(item);
    // 排序预设 回车或失焦后
    public void SortScalePreset()
    {
        var sorted = ScalePresets.OrderBy(x => x.Value).ToList();
        ScalePresets = new ObservableCollection<ScalePreset>(sorted);
    }
    // 切换编辑模式
    public void EditScalePreset(ScalePreset item)
    {
        item.Editing = true;
    }
    // 应用编辑
    public void ApplyScalePreset()
    {
        foreach (var preset in ScalePresets)
        {
            preset.Editing = false;
        }
        SortScalePreset();
    }

    public class ScalePreset : ReactiveObject
    {
        private double _value;
        public double Value
        {
            get => _value;
            set
            {
                Display = value.ToString("P1");
                this.RaiseAndSetIfChanged(ref _value, value);
            }
        }

        private string _text;
        public string Text
        {
            get => _text;
            set
            {
                double newValue;
                if (double.TryParse(value, out newValue))
                {
                    Value = newValue / 100;
                    this.RaiseAndSetIfChanged(ref _text, value);
                }
                else
                {
                    Display = "Error";
                    this.RaiseAndSetIfChanged(ref _text, value);
                }
                
            }
        }

        private string _display;
        public string Display
        {
            get => _display;
            private set => this.RaiseAndSetIfChanged(ref _display, value);
        }

        private bool _editing = false;

        public bool Editing
        {
            get => _editing;
            set => this.RaiseAndSetIfChanged(ref _editing, value);
        }

        public ScalePreset(string text)
        {
            Text = text;
        }
    }
    
    #endregion

    //////////////
    /// EXIF 显示设置
    //////////////
    
    #region ExifDisplaySetting

    private ObservableCollection<ExifDisplayItem> _exifDisplayItems = new();
    public ObservableCollection<ExifDisplayItem> ExifDisplayItems
    {
        get => _exifDisplayItems;
        set => this.RaiseAndSetIfChanged(ref _exifDisplayItems, value);
    }

    private List<ExifDisplayItem> _enabledExifItems = new();
    public List<ExifDisplayItem> EnabledExifItems
    {
        get => _enabledExifItems;
        private set => this.RaiseAndSetIfChanged(ref _enabledExifItems, value);
    }

    private void InitializeExifDisplayItems()
    {
        ExifDisplayItems = new ObservableCollection<ExifDisplayItem>
        {
            new("光圈", "Aperture", true),
            new("快门", "ExposureTime", true),
            new("ISO", "Iso", true),
            new("等效焦距", "EquivFocalLength", true),
            new("实际焦距", "FocalLength", false),
            new("相机型号", "CameraModel", false),
            new("镜头型号", "LensModel", false),
            new("拍摄时间", "DateTimeOriginal", false),
            new("曝光补偿", "ExposureBias", false),
            new("白平衡", "WhiteBalance", false),
            new("闪光灯", "Flash", false),
        };

        // 监听集合变化
        ExifDisplayItems.CollectionChanged += OnExifDisplayItemsChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged += OnExifDisplayItemChanged;
        }

        UpdateEnabledExifItems();
    }

    private void OnExifDisplayItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 为新添加的项目订阅属性变化
        if (e.NewItems != null)
        {
            foreach (ExifDisplayItem item in e.NewItems)
            {
                item.PropertyChanged += OnExifDisplayItemChanged;
            }
        }

        // 为移除的项目取消订阅
        if (e.OldItems != null)
        {
            foreach (ExifDisplayItem item in e.OldItems)
            {
                item.PropertyChanged -= OnExifDisplayItemChanged;
            }
        }

        UpdateEnabledExifItems();
    }

    private void OnExifDisplayItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExifDisplayItem.IsEnabled))
        {
            UpdateEnabledExifItems();
        }
    }

    private void UpdateEnabledExifItems()
    {
        EnabledExifItems = ExifDisplayItems
            .Where(item => item.IsEnabled)
            .ToList();
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveExifDisplayCommand { get; private set; }

    private void OnMoveExifDisplay(MoveCommandParameter parameter)
    {
        MoveExifDisplay(parameter.FromIndex, parameter.ToIndex);
    }

    public void MoveExifDisplay(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= ExifDisplayItems.Count || 
            toIndex < 0 || toIndex >= ExifDisplayItems.Count || 
            fromIndex == toIndex)
            return;

        var item = ExifDisplayItems[fromIndex];
        ExifDisplayItems.RemoveAt(fromIndex);
        ExifDisplayItems.Insert(toIndex, item);
        
        // 移动操作会触发 CollectionChanged 事件，自动更新 EnabledExifItems
    }

    public class ExifDisplayItem : ReactiveObject
    {
        private string _displayName;
        private string _propertyName;
        private bool _isEnabled;

        public string DisplayName
        {
            get => _displayName;
            set => this.RaiseAndSetIfChanged(ref _displayName, value);
        }

        public string PropertyName
        {
            get => _propertyName;
            set => this.RaiseAndSetIfChanged(ref _propertyName, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        public ExifDisplayItem(string displayName, string propertyName, bool isEnabled = true)
        {
            _displayName = displayName;
            _propertyName = propertyName;
            _isEnabled = isEnabled;
        }
    }

    #endregion
    
    //////////////
    /// 星级设置
    //////////////

    #region RatingSetting

    private bool _showRating = true;
    public bool ShowRating
    {
        get => _showRating;
        set => this.RaiseAndSetIfChanged(ref _showRating, value);
    }
    private bool _safeSetRating = true;
    public bool SafeSetRating
    {
        get => _safeSetRating;
        set => this.RaiseAndSetIfChanged(ref _safeSetRating, value);
    }
    
    #endregion
}