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
    private MainViewModel Main { get; }
    
    // 布局方向（从主视图模型获取实际布局状态）
    public bool IsVerticalLayout => Main.IsHorizontalLayout;

    // 当前文件的 EXIF 数据
    public ExifData? CurrentExifData => Main.CurrentFile?.ExifData;

    // 启用的 EXIF 显示项列表
    public IEnumerable<SettingsViewModel.ExifDisplayItem> EnabledExifItems => 
        Main.Settings.EnabledExifItems;

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
            "Fit" => OnFit,
            "ZoomInPreset" => OnZoomInPreset,
            "ZoomOutPreset" => OnZoomOutPreset,
            "ZoomInScale" => OnZoomInScale,
            "ZoomOutScale" => OnZoomOutScale,
            _ => null
        };
    }
    
    private void ExecuteExchange()
    {
        Main.CurrentFile = Main.LastFile;
    }

    // 命令
    public ReactiveCommand<Unit, Unit> OnOpen { get; }
    public ReactiveCommand<Unit, Unit> OnPrevious { get; }
    public ReactiveCommand<Unit, Unit> OnNext { get; }
    public ReactiveCommand<Unit, Unit> OnExchange { get; }
    public ReactiveCommand<Unit, Unit> OnFit { get; }
    public ReactiveCommand<Unit, Unit> OnZoomInPreset { get; }
    public ReactiveCommand<Unit, Unit> OnZoomOutPreset { get; }
    public ReactiveCommand<Unit, Unit> OnZoomInScale { get; }
    public ReactiveCommand<Unit, Unit> OnZoomOutScale { get; }
    
    public ControlViewModel(MainViewModel mainViewModel)
    {
        Main = mainViewModel;
        
        // 初始化命令
        OnOpen = ReactiveCommand.Create(() => { Main.FolderVM.OpenFilePickerAsync(); });
        OnPrevious = ReactiveCommand.Create(ExecutePrevious);
        OnNext = ReactiveCommand.Create(ExecuteNext);
        OnExchange = ReactiveCommand.Create(() => { Main.CurrentFile = Main.LastFile; });
        OnFit = ReactiveCommand.Create(() => Main.ImageVM.ToggleFit() );
        OnZoomInPreset = ReactiveCommand.Create(() => Main.ImageVM.ZoomPreset(+1) );
        OnZoomOutPreset = ReactiveCommand.Create(() => Main.ImageVM.ZoomPreset(-1) );
        OnZoomInScale = ReactiveCommand.Create(() => Main.ImageVM.ZoomScale(1.25) );
        OnZoomOutScale = ReactiveCommand.Create(() => Main.ImageVM.ZoomScale(0.8) );

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
        
        // 监听设置变化
        Main.Settings.WhenAnyValue(s => s.ShowRating)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowRating)));
            
        Main.Settings.WhenAnyValue(s => s.SafeSetRating)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(SafeSetRating)));

        Main.Settings.Hotkeys.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(EnabledControls));
        foreach (var hotkey in Main.Settings.Hotkeys)
        {
            hotkey.PropertyChanged += (s, e) => 
            {
                if (e.PropertyName == nameof(SettingsViewModel.HotkeyItem.IsDisplay))
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

    // 启用的控件列表
    public IEnumerable<SettingsViewModel.HotkeyItem> EnabledControls => 
        Main.Settings.Hotkeys.Where(h => h.IsDisplay);

    // 所有快捷键（用于全局监听）
    public IEnumerable<SettingsViewModel.HotkeyItem> AllHotkeys => Main.Settings.Hotkeys;
    
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

    /// <summary>
    /// 设置评分并写入 XMP
    /// </summary>
    public async void SetRating(int rating)
    {
        if (Main.CurrentFile == null) return;

        var file = Main.CurrentFile;

        try
        {
            var success = await XmpWriter.WriteRatingAsync(file.File, rating, SafeSetRating);
            if (success)
            {
                // 同步写入隐藏同名文件星级
                if (file.HiddenFiles.Count > 0)
                {
                    foreach (var hidden in file.HiddenFiles)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await XmpWriter.WriteRatingAsync(hidden, rating, SafeSetRating);
                            }
                            catch (Exception exHidden)
                            {
                                Console.WriteLine($"Hidden file rating sync failed: {exHidden.Message}");
                            }
                        });
                    }
                }

                // 异步刷新 EXIF 数据（仅代表文件）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        file.ClearExifData();
                        await file.LoadExifDataAsync();

                        // 在 UI 线程上通知更新
                        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            this.RaisePropertyChanged(nameof(CurrentExifData));
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to refresh EXIF after rating update: {ex.Message}");
                    }
                });
            }
            else
            {
                Console.WriteLine($"Failed to update rating for {file.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting rating for {file.Name}: {ex.Message}");
        }
    }
}

