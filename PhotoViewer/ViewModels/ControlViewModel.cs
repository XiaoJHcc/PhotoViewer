using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class ControlViewModel : ReactiveObject
{
    private readonly MainViewModel Main;
    
    // 布局方向（从主视图模型获取实际布局状态）
    public bool IsVerticalLayout => Main.IsHorizontalLayout;

    // 当前文件的 EXIF 数据
    public ExifData? CurrentExifData => Main.CurrentFile?.ExifData;

    // 启用的 EXIF 显示项列表
    public IEnumerable<SettingsViewModel.ExifDisplayItem> EnabledExifItems => 
        Main.Settings.EnabledExifItems;

    // 评分属性
    private int _rating = 0;
    public int Rating
    {
        get => _rating;
        set => this.RaiseAndSetIfChanged(ref _rating, value);
    }

    public ControlViewModel(MainViewModel mainViewModel)
    {
        Main = mainViewModel;
        
        // 初始化命令
        OnOpen = ReactiveCommand.Create(ExecuteOpen);
        OnPrevious = ReactiveCommand.Create(ExecutePrevious);
        OnNext = ReactiveCommand.Create(ExecuteNext);
        OnFit = ReactiveCommand.Create(ExecuteFit);
        OnZoomIn = ReactiveCommand.Create(ExecuteZoomIn);
        OnZoomOut = ReactiveCommand.Create(ExecuteZoomOut);

        // 监听当前文件变化，通知 EXIF 数据更新
        Main.WhenAnyValue(vm => vm.CurrentFile)
            .Subscribe(currentFile =>
            {
                this.RaisePropertyChanged(nameof(CurrentExifData));
                
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
        
        // 监听设置变化
        Main.Settings.Hotkeys.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(EnabledControls));
        foreach (var hotkey in Main.Settings.Hotkeys)
        {
            hotkey.PropertyChanged += (s, e) => 
            {
                if (e.PropertyName == nameof(SettingsViewModel.HotkeyItem.IsEnabled))
                {
                    this.RaisePropertyChanged(nameof(EnabledControls));
                }
            };
        }

        // 监听 EXIF 显示设置变化
        Main.Settings.ExifDisplayItems.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(EnabledExifItems));
        foreach (var exifItem in Main.Settings.ExifDisplayItems)
        {
            exifItem.PropertyChanged += (s, e) => 
            {
                if (e.PropertyName == nameof(SettingsViewModel.ExifDisplayItem.IsEnabled))
                {
                    this.RaisePropertyChanged(nameof(EnabledExifItems));
                }
            };
        }
    }

    // 命令
    public ReactiveCommand<Unit, Unit> OnOpen { get; }
    public ReactiveCommand<Unit, Unit> OnPrevious { get; }
    public ReactiveCommand<Unit, Unit> OnNext { get; }
    public ReactiveCommand<Unit, Unit> OnFit { get; }
    public ReactiveCommand<Unit, Unit> OnZoomIn { get; }
    public ReactiveCommand<Unit, Unit> OnZoomOut { get; }

    // 启用的控件列表
    public IEnumerable<SettingsViewModel.HotkeyItem> EnabledControls => 
        Main.Settings.Hotkeys.Where(h => h.IsEnabled);

    // 所有快捷键（用于全局监听）
    public IEnumerable<SettingsViewModel.HotkeyItem> AllHotkeys => Main.Settings.Hotkeys;

    // 根据命令名称获取命令
    public ICommand? GetCommandByName(string commandName)
    {
        return commandName switch
        {
            "Open" => OnOpen,
            "Previous" => OnPrevious,
            "Next" => OnNext,
            "Fit" => OnFit,
            "ZoomIn" => OnZoomIn,
            "ZoomOut" => OnZoomOut,
            _ => null
        };
    }

    private void ExecuteOpen()
    {
        // 打开文件
        Main.FolderVM.OpenFilePickerAsync();
    }
    
    private void ExecutePrevious()
    {
        // 上一张
        if (Main.FolderVM.HasPreviousFile())
        {
            var currentIndex = Main.FolderVM.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FolderVM.FilteredFiles[currentIndex - 1];
            Main.FolderVM.ScrollToCurrent();
        }
    }

    private void ExecuteNext()
    {
        // 下一张
        if (Main.FolderVM.HasNextFile())
        {
            var currentIndex = Main.FolderVM.FilteredFiles.IndexOf(Main.CurrentFile);
            Main.CurrentFile = Main.FolderVM.FilteredFiles[currentIndex + 1];
            Main.FolderVM.ScrollToCurrent();
        }
    }

    private void ExecuteFit()
    {
        // 缩放适应
        Main.ImageVM.ToggleFit();
    }

    private void ExecuteZoomIn()
    {
        // 放大
        Main.ImageVM.ZoomPreset(+1);
    }

    private void ExecuteZoomOut()
    {
        // 缩小
        Main.ImageVM.ZoomPreset(-1);
    }

    // 设置评分
    public void SetRating(int rating)
    {
        if (rating >= 0 && rating <= 5)
        {
            Rating = rating;
            // TODO: 在这里可以添加保存评分到文件元数据的逻辑
        }
    }
}