using System.Collections.ObjectModel;
using PhotoViewer.Core.Settings;
using ReactiveUI;

namespace PhotoViewer.ViewModels.Settings;

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

    private bool _showNavigationButtons = true;
    /// <summary>手机形态底部行是否显示上一张/下一张合体按钮。</summary>
    public bool ShowNavigationButtons
    {
        get => _showNavigationButtons;
        set => this.RaiseAndSetIfChanged(ref _showNavigationButtons, value);
    }

    // 布局模式选项
    public ObservableCollection<LayoutModeItem> LayoutModes { get; } = new();

    private void InitializeLayoutModes()
    {
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Row, "上下", "缩略图和控制栏位于上下侧，适合竖屏"));
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Col, "左右", "缩略图和控制栏位于左右侧，适合横屏"));
        LayoutModes.Add(new LayoutModeItem(LayoutMode.Auto, "自动", "根据屏幕方向自动选择空间较多两侧"));
        if (LayoutCapabilities.PhoneLayoutSupported)
            LayoutModes.Add(new LayoutModeItem(LayoutMode.Phone, "手机", "按手机屏幕排布，操作区靠近手指"));
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

