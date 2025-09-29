using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class MainViewModel : ViewModelBase
{
    public FolderViewModel FolderVM { get; }
    public ControlViewModel ControlVM { get; }
    public ImageViewModel ImageVM { get; }
    public SettingsViewModel Settings { get; }

    // 当前状态
    public ImageFile? LastFile;
    private ImageFile? _currentFile;
    public ImageFile? CurrentFile
    {
        get => _currentFile;
        set
        {
            if (_currentFile != null) _currentFile.IsCurrent = false;
            if (_currentFile != value) LastFile = _currentFile;
            this.RaiseAndSetIfChanged(ref _currentFile, value);
            if (value != null) 
            {
                value.IsCurrent = true;
                FolderVM.PreloadNearbyFiles();
            }
            else
            {
                Console.WriteLine("CurrentFile => null");
            }
        }
    }
        
    public MainViewModel()
    {
        // 先创建设置 ViewModel
        Settings = new SettingsViewModel();
        // 创建子 ViewModel
        FolderVM = new FolderViewModel(this);
        ImageVM = new ImageViewModel(this);
        ControlVM = new ControlViewModel(this);

        // 监听布局模式变化
        Settings.WhenAnyValue(s => s.LayoutMode)
            .Subscribe(_ => UpdateLayoutFromSettings());
    }
    
    ////////////////
    /// 打开设置模态
    ////////////////
    
    #region OpenSettingsModal
    
    /// <summary>
    /// 打开设置窗口
    /// </summary>
    public void OpenSettingWindow(Window parentWindow)
    {
        var settingsWindow = new SettingsWindow
        {
            DataContext = Settings
        };
        settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        settingsWindow.ShowDialog(parentWindow);
    }

    // 模态显示
    private bool _isModalVisible = false;
    public bool IsModalVisible
    {
        get => _isModalVisible;
        set => this.RaiseAndSetIfChanged(ref _isModalVisible, value);
    }
    // 模态显示时 遮罩不透明度
    private double _modalMaskOpacity = 0.5;
    public double ModalMaskOpacity
    {
        get => _modalMaskOpacity;
        set => this.RaiseAndSetIfChanged(ref _modalMaskOpacity, value);
    }
    // 模态显示时 模态弹出
    private double _modalMarginTop = 2000;
    public double ModalMarginTop
    {
        get => _modalMarginTop;
        set => this.RaiseAndSetIfChanged(ref _modalMarginTop, value);
    }
    
        
    /// <summary>
    /// 打开设置弹窗
    /// </summary>
    public void OpenSettingModal()
    {
        ShowModal();
    }

    public void ShowModal()
    {
        IsModalVisible = true;
        
        ModalMaskOpacity = 0.5;
        ModalMarginTop = 80;
    }
    public async void HideModal()
    {
        ModalMaskOpacity = 0;
        ModalMarginTop = 2000;
        
        await Task.Delay(400);
        IsModalVisible = false;
    }
    
    #endregion
    
    
    ////////////////
    /// 整体布局
    ////////////////

    #region Layout
    
    // 实际使用的布局方向（考虑智能模式）
    private bool _isHorizontalLayout = false;
    public bool IsHorizontalLayout
    {
        get => _isHorizontalLayout;
        private set => this.RaiseAndSetIfChanged(ref _isHorizontalLayout, value);
    }

    // 屏幕方向状态
    private bool _isScreenLandscape = true;
    public bool IsScreenLandscape
    {
        get => _isScreenLandscape;
        set
        {
            var oldValue = _isScreenLandscape;
            this.RaiseAndSetIfChanged(ref _isScreenLandscape, value);
            
            // 只有当值真正发生变化时才更新布局
            if (oldValue != value)
            {
                UpdateLayoutFromSettings();
            }
        }
    }
    
    /// <summary>
    /// 根据设置和屏幕方向更新布局
    /// </summary>
    private void UpdateLayoutFromSettings()
    {
        bool newLayout = Settings.LayoutMode switch
        {
            LayoutMode.Horizontal => true,
            LayoutMode.Vertical => false,
            LayoutMode.Auto => IsScreenLandscape,
            _ => false
        };

        if (IsHorizontalLayout != newLayout)
        {
            IsHorizontalLayout = newLayout;
            
            // 通知相关视图模型布局已变化
            FolderVM?.RaisePropertyChanged(nameof(FolderVM.IsVerticalLayout));
            ControlVM?.RaisePropertyChanged(nameof(ControlVM.IsVerticalLayout));
        }
    }

    /// <summary>
    /// 更新屏幕方向（由视图调用）
    /// </summary>
    public void UpdateScreenOrientation(double width, double height)
    {
        bool newIsLandscape = width > height;
        
        // 直接比较值是否变化，而不是依赖 RaiseAndSetIfChanged 的返回值
        if (IsScreenLandscape != newIsLandscape)
        {
            IsScreenLandscape = newIsLandscape;
            
            // 如果是智能模式，屏幕方向变化会触发布局更新
            if (Settings.LayoutMode == LayoutMode.Auto)
            {
                UpdateLayoutFromSettings();
            }
        }
    }

    #endregion

    public async Task SetRatingAsync(ImageFile? file, int rating)
    {
        if (file == null) return;
        try
        {
            var success = await XmpWriter.WriteRatingAsync(file.File, rating, Settings.SafeSetRating);
            if (success)
            {
                // 同步写入隐藏文件
                if (file.HiddenFiles.Count > 0)
                {
                    foreach (var hidden in file.HiddenFiles)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await XmpWriter.WriteRatingAsync(hidden, rating, Settings.SafeSetRating); }
                            catch (Exception exHidden) { Console.WriteLine("Hidden file rating sync failed: " + exHidden.Message); }
                        });
                    }
                }

                // 重新加载 EXIF 刷新星级显示
                _ = Task.Run(async () =>
                {
                    try
                    {
                        file.ClearExifData();
                        await file.LoadExifDataAsync();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            ControlVM.RaisePropertyChanged(nameof(ControlVM.CurrentExifData));
                            file.RaisePropertyChanged(nameof(file.Rating));
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Refresh EXIF after rating failed: " + ex.Message);
                    }
                });

                // 重新应用筛选（若当前使用星级筛选）
                FolderVM.RefreshFilters();
            }
            else
            {
                Console.WriteLine("Failed to update rating for " + file.Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error setting rating for " + file.Name + ": " + ex.Message);
        }
    }
}
