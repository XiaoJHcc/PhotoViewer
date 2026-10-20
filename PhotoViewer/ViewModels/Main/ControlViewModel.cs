using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoViewer.Core;
using PhotoViewer.Core.Exif;
using PhotoViewer.Core.Image;
using PhotoViewer.ViewModels.Settings;
using ReactiveUI;


namespace PhotoViewer.ViewModels.Main;

/// <summary>
/// 控制栏中展示的 EXIF 条目。
/// </summary>
public sealed class ExifDisplayEntry
{
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// 原始 EXIF 属性名。
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// 已格式化的显示值。
    /// </summary>
    public string DisplayValue { get; }

    /// <summary>
    /// 构造控制栏 EXIF 展示项。
    /// </summary>
    public ExifDisplayEntry(string displayName, string propertyName, string displayValue)
    {
        DisplayName = displayName;
        PropertyName = propertyName;
        DisplayValue = displayValue;
    }
}

public class ControlViewModel : ReactiveObject
{
    private MainViewModel Main { get; }
    
    // 布局方向（从主视图模型获取实际布局状态）
    public bool IsRowLayout => Main.IsRowLayout;

    // 当前文件的 EXIF 数据
    public ExifData? CurrentExifData => Main.CurrentFile?.ExifData;

    private IReadOnlyList<ExifDisplayEntry> _enabledExifItems = Array.Empty<ExifDisplayEntry>();

    // 启用的 EXIF 显示项列表
    public IReadOnlyList<ExifDisplayEntry> EnabledExifItems
    {
        get => _enabledExifItems;
        private set => this.RaiseAndSetIfChanged(ref _enabledExifItems, value);
    }

    // 直接访问设置属性
    public bool ShowRating => Main.Settings.ShowRating;
    public bool SafeSetRating => Main.Settings.SafeSetRating;

    // 星级区域的不透明度：有照片时为1.0，无照片时为0.1
    public double StarOpacity => Main.CurrentFile != null ? 1.0 : 0.1;
    
    // 根据命令名称获取命令
    public ICommand? GetCommandByName(string commandName)
    {
        return commandName switch
        {
            "Open" => OnOpen,
            "Previous" => OnPrevious,
            "Next" => OnNext,
            "Exchange" => OnExchange,
            "ToggleAnalysisView" => OnToggleAnalysisView,
            "SetRating0" => OnSetRating0,
            "SetRating1" => OnSetRating1,
            "SetRating2" => OnSetRating2,
            "SetRating3" => OnSetRating3,
            "SetRating4" => OnSetRating4,
            "SetRating5" => OnSetRating5,
            _ => null
        };
    }

    // 命令
    public ReactiveCommand<Unit, Unit> OnOpen { get; }
    public ReactiveCommand<Unit, Unit> OnPrevious { get; }
    public ReactiveCommand<Unit, Unit> OnNext { get; }
    public ReactiveCommand<Unit, Unit> OnExchange { get; }
    // 新增：评分命令
    public ReactiveCommand<Unit, Unit> OnSetRating0 { get; }
    public ReactiveCommand<Unit, Unit> OnSetRating1 { get; }
    public ReactiveCommand<Unit, Unit> OnSetRating2 { get; }
    public ReactiveCommand<Unit, Unit> OnSetRating3 { get; }
    public ReactiveCommand<Unit, Unit> OnSetRating4 { get; }
    public ReactiveCommand<Unit, Unit> OnSetRating5 { get; }
    public ReactiveCommand<Unit, Unit> OnToggleAnalysisView { get; }

    public ControlViewModel(MainViewModel mainViewModel)
    {
        Main = mainViewModel;
        
        // 初始化命令
        OnOpen = ReactiveCommand.Create(() => { var _ = Main.FolderVM.OpenFilePickerAsync(); });
        OnPrevious = ReactiveCommand.Create(ExecutePrevious);
        OnNext = ReactiveCommand.Create(ExecuteNext);
        OnExchange = ReactiveCommand.Create(() => { Main.CurrentFile = Main.LastFile; });
        // 新增：评分命令
        OnSetRating0 = ReactiveCommand.Create(() => SetRating(0));
        OnSetRating1 = ReactiveCommand.Create(() => SetRating(1));
        OnSetRating2 = ReactiveCommand.Create(() => SetRating(2));
        OnSetRating3 = ReactiveCommand.Create(() => SetRating(3));
        OnSetRating4 = ReactiveCommand.Create(() => SetRating(4));
        OnSetRating5 = ReactiveCommand.Create(() => SetRating(5));
        OnToggleAnalysisView = ReactiveCommand.Create(Main.ToggleAnalysisView);

        // 监听当前文件变化，通知 EXIF 数据更新
        Main.WhenAnyValue(vm => vm.CurrentFile)
            .Subscribe(currentFile =>
            {
                this.RaisePropertyChanged(nameof(CurrentExifData));
                this.RaisePropertyChanged(nameof(StarOpacity));
                
                // 如果当前文件有效且 EXIF 未加载，则加载 EXIF
                if (currentFile != null && !currentFile.IsExifLoaded && !currentFile.IsExifLoading)
                {
                    _ = Task.Run(async () => 
                    {
                        await currentFile.LoadExifDataAsync();
                        // EXIF 加载完成后通知 UI 更新
                        this.RaisePropertyChanged(nameof(CurrentExifData));
                    });
                }
            });

        this.WhenAnyValue(vm => vm.CurrentExifData)
            .Subscribe(_ => UpdateEnabledExifItems());
        
        // 监听设置变化
        Main.Settings.WhenAnyValue(s => s.ShowRating)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowRating)));
            
        Main.Settings.WhenAnyValue(s => s.SafeSetRating)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(SafeSetRating)));

        // 使用具名本地函数，以便在集合变化时正确地订阅/取消订阅新增项目
        void OnHotkeyIsDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.HotkeyItem.IsDisplay))
                this.RaisePropertyChanged(nameof(EnabledControls));
        }
        foreach (var hotkey in Main.Settings.Hotkeys)
            hotkey.PropertyChanged += OnHotkeyIsDisplayChanged;

        // 集合变化时：为新增项订阅、为移除项取消订阅，并刷新控件列表
        Main.Settings.Hotkeys.CollectionChanged += (s, e) =>
        {
            if (e.OldItems != null)
                foreach (SettingsViewModel.HotkeyItem item in e.OldItems)
                    item.PropertyChanged -= OnHotkeyIsDisplayChanged;
            if (e.NewItems != null)
                foreach (SettingsViewModel.HotkeyItem item in e.NewItems)
                    item.PropertyChanged += OnHotkeyIsDisplayChanged;
            this.RaisePropertyChanged(nameof(EnabledControls));
        };

        // 监听 EXIF 显示设置变化：直接订阅 SettingsVM.EnabledExifItems 属性
        // （SettingsVM 在 UpdateEnabledExifItems() 中通过 RaiseAndSetIfChanged 更新缓存后才通知，
        //   避免 ControlVM 先于 UpdateEnabledExifItems 读到旧缓存导致"滞后一次"的问题）
        Main.Settings.WhenAnyValue(s => s.EnabledExifItems)
            .Subscribe(_ => UpdateEnabledExifItems());

        UpdateEnabledExifItems();
    }

    // 启用的控件列表
    public IEnumerable<SettingsViewModel.HotkeyItem> EnabledControls => 
        Main.Settings.Hotkeys.Where(h => h.IsDisplay);

    // 所有快捷键（用于全局监听）
    public IEnumerable<SettingsViewModel.HotkeyItem> AllHotkeys => Main.Settings.Hotkeys;

    /// <summary>
    /// 根据当前照片与设置项生成控制栏 EXIF 展示列表。
    /// </summary>
    private void UpdateEnabledExifItems()
    {
        EnabledExifItems = Main.Settings.EnabledExifItems
            .Select(item => new ExifDisplayEntry(
                item.DisplayName,
                item.PropertyName,
                FormatExifValue(CurrentExifData, item.PropertyName)))
            .ToList();
    }

    /// <summary>
    /// 将 EXIF 原始值转换为控制栏使用的展示文本。
    /// </summary>
    private static string FormatExifValue(ExifData? exifData, string propertyName)
    {
        if (exifData == null)
        {
            return "--";
        }

        try
        {
            return propertyName switch
            {
                "Aperture" => ExifDisplayFormat.Aperture(exifData.Aperture),
                "ExposureTime" => ExifDisplayFormat.ExposureTime(exifData.ExposureTime),
                "Iso" => exifData.Iso?.ToString() ?? "--",
                "EquivFocalLength" => ExifDisplayFormat.FocalLength(exifData.EquivFocalLength),
                "FocalLength" => ExifDisplayFormat.FocalLength(exifData.FocalLength),
                "CameraModel" => exifData.CameraModel ?? "--",
                "LensModel" => exifData.LensModel ?? "--",
                "DateTimeOriginal" => ExifDisplayFormat.DateTime(exifData.DateTimeOriginal),
                "ExposureBias" => ExifDisplayFormat.ExposureBias(exifData.ExposureBias),
                "WhiteBalance" => exifData.WhiteBalance ?? "--",
                "Flash" => exifData.Flash ?? "--",
                _ => "--"
            };
        }
        catch
        {
            return "--";
        }
    }
    
    private void ExecutePrevious()
    {
        // FILTER-05：翻页统一走共享实现，当前照片被筛掉时按锚点落到最近的前一项。
        Main.FileVM.ThumbnailList.MoveCurrent(-1);
    }

    private void ExecuteNext()
    {
        // FILTER-05：翻页统一走共享实现，当前照片被筛掉时按锚点落到最近的后一项。
        Main.FileVM.ThumbnailList.MoveCurrent(1);
    }

    /// <summary>
    /// 设置评分并写入 XMP
    /// </summary>
    public async void SetRating(int rating)
    {
        if (Main.CurrentFile == null) return;

        await Main.SetRatingAsync(Main.CurrentFile, rating);
    }
    
    /// <summary>
    /// 打开工具窗口（桌面端）。
    /// </summary>
    public void OpenTools()
    {
        Main.OpenToolsWindow();
    }
}
