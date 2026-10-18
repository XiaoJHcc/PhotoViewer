using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Settings;

public partial class SettingsViewModel
{
    /// <summary>缩放比例预设的百分比上限（SETTINGS-07：&gt;0 且 ≤1600%）。</summary>
    public const double ScalePresetPercentMax = 1600;

    private const double ScalePresetPercentMin = 0.001;

    /// <summary>
    /// 校验缩放比例预设的百分比数值（SETTINGS-07）：必须为有限数值、&gt;0 且 ≤1600。
    /// 入参单位是百分比（100 表示 100%），不是 <see cref="ScalePreset.Value"/> 使用的倍率。
    /// </summary>
    /// <param name="percent">百分比数值，例如 33.333。</param>
    /// <returns>合法返回 true。</returns>
    public static bool IsValidScalePresetPercent(double percent)
        => double.IsFinite(percent) && percent > 0 && percent <= ScalePresetPercentMax;

        
    private bool _showZoomIndicator = true;
    public bool ShowZoomIndicator
    {
        get => _showZoomIndicator;
        set => this.RaiseAndSetIfChanged(ref _showZoomIndicator, value);
    }
    
    //////////////
    /// 缩放倍率预设
    //////////////
        
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
        set
        {
            this.RaiseAndSetIfChanged(ref _scalePresets, value);
            this.RaisePropertyChanged(nameof(CanRemoveScalePreset));
        }
    }

    /// <summary>是否允许删除缩放比例预设（SETTINGS-07：至少保留一项）。</summary>
    public bool CanRemoveScalePreset => ScalePresets.Count > 1;

    // 添加预设 +
    public void AddScalePreset() => ScalePresets.Add(new("100"));

    /// <summary>删除缩放比例预设；只剩一项时删除无效（SETTINGS-07）。</summary>
    /// <param name="item">要删除的预设。</param>
    public void RemoveScalePreset(ScalePreset item)
    {
        if (!CanRemoveScalePreset) return;
        ScalePresets.Remove(item);
        RequestSave();
    }
    // 排序预设 回车或失焦后
    public void SortScalePreset()
    {
        var sorted = ScalePresets.OrderBy(x => x.Value).ToList();
        ScalePresets.CollectionChanged -= OnScalePresetsChanged;
        foreach (var preset in ScalePresets)
        {
            preset.PropertyChanged -= OnScalePresetPropertyChanged;
        }

        ScalePresets = new ObservableCollection<ScalePreset>(sorted);
        ScalePresets.CollectionChanged += OnScalePresetsChanged;
        foreach (var preset in ScalePresets)
        {
            preset.PropertyChanged += OnScalePresetPropertyChanged;
        }
        RequestSave();
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
        RequestSave();
    }

    /// <summary>缩放比例预设；<see cref="Value"/> 为倍率，<see cref="Text"/> 为百分比文本。</summary>
    public class ScalePreset : ReactiveObject
    {
        private double _value;

        /// <summary>倍率（0.125 表示 12.5%），供缩放命令直接使用。</summary>
        public double Value
        {
            get => _value;
            set
            {
                Display = value.ToString("P1");
                this.RaiseAndSetIfChanged(ref _value, value);
            }
        }

        private string _text = null!;

        /// <summary>
        /// 百分比文本（100 表示 100%）。非法输入（无法解析、≤0 或 &gt;1600）不写入，
        /// 只回推一次属性变更，让壳把输入框恢复为原值（SETTINGS-07）。
        /// </summary>
        public string Text
        {
            get => _text;
            set
            {
                var text = value?.Trim() ?? string.Empty;
                if (!IsValidScalePresetPercentText(text, out var percent))
                {
                    this.RaisePropertyChanged();
                    return;
                }

                Value = percent / 100;
                this.RaiseAndSetIfChanged(ref _text, text);
            }
        }

        private string _display = null!;
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

        /// <summary>用百分比文本创建预设；初值非法时回落到 100%，保证列表不出现空文本或 0 倍率。</summary>
        /// <param name="text">百分比文本。</param>
        public ScalePreset(string text)
        {
            if (IsValidScalePresetPercentText(text, out var percent))
            {
                Value = percent / 100;
                _text = text.Trim();
            }
            else
            {
                Value = 1;
                _text = "100";
            }
        }

        private static bool IsValidScalePresetPercentText(string? text, out double percent)
        {
            percent = 0;
            return !string.IsNullOrWhiteSpace(text)
                && (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
                    || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
                && SettingsViewModel.IsValidScalePresetPercent(percent);
        }
    }
}
