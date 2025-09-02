using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class SettingsViewModel : ReactiveObject
{
    private int _maxCacheSizeMB = 500;
    private int _preloadCount = 3;
    
    public int MaxCacheSizeMB
    {
        get => _maxCacheSizeMB;
        set => this.RaiseAndSetIfChanged(ref _maxCacheSizeMB, value);
    }
        
    public int PreloadCount
    {
        get => _preloadCount;
        set => this.RaiseAndSetIfChanged(ref _preloadCount, value);
    }
    
    public SettingsViewModel()
    {
        InitFileSettings();
        
        SortScalePreset();
    }


    //////////////
    /// 文件格式支持
    //////////////
    
    #region FileFormatSetting
    
    public class FormatItem : ReactiveObject
    {
        private bool _isChecked;
        private string _format;
    
        public string Format
        {
            get => _format;
            set => this.RaiseAndSetIfChanged(ref _format, value);
        }
    
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                this.RaiseAndSetIfChanged(ref _isChecked, value);
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        
        public event EventHandler CheckedChanged;
    }
    
    private ObservableCollection<FormatItem> _formatItems;
        
    public ObservableCollection<FormatItem> FormatItems
    {
        get => _formatItems;
        set => this.RaiseAndSetIfChanged(ref _formatItems, value);
    }
    
    private ObservableCollection<string> _selectedFormats;
    public ObservableCollection<string> SelectedFormats
    {
        get => _selectedFormats;
        set => this.RaiseAndSetIfChanged(ref _selectedFormats, value);
    }
    
    public void InitFileSettings()
    {
        FormatItems = new ObservableCollection<FormatItem>
        {
            new() { Format = ".jpg", IsChecked = true },
            new() { Format = ".jpeg", IsChecked = true },
            new() { Format = ".png", IsChecked = true },
            new() { Format = ".webp", IsChecked = true },
            new() { Format = ".tif", IsChecked = true },
            new() { Format = ".tiff", IsChecked = true },
            new() { Format = ".gif", IsChecked = false }
        };
        foreach (var item in FormatItems)
        {
            item.CheckedChanged += (_, _) => UpdateSelectedFormats();
        }
        // 监听 FormatItems 的变化
        FormatItems.CollectionChanged += (_, _) => UpdateSelectedFormats();
        
        UpdateSelectedFormats();
    }
    
    private void UpdateSelectedFormats()
    {
        SelectedFormats = new ObservableCollection<string>(
            FormatItems.Where(x => x.IsChecked)
                .Select(x => x.Format)
        );
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
}