using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
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
}

