using System.Collections.ObjectModel;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
    //////////////
    /// 布局
    //////////////
    
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
}

