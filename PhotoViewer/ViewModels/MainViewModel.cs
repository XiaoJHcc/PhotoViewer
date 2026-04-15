using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public class MainViewModel : ViewModelBase
{
    public FolderViewModel FolderVM { get; }
    public ControlViewModel ControlVM { get; }
    public ImageViewModel ImageVM { get; }
    public DetailViewModel DetailVM { get; }
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
        DetailVM = new DetailViewModel(this);
        ControlVM = new ControlViewModel(this);

        // 监听布局模式变化
        Settings.WhenAnyValue(s => s.LayoutMode)
            .Subscribe(_ => UpdateLayoutFromSettings());

        // 当前图片切换时同步更新已打开的 EXIF 详情窗口/模态
        this.WhenAnyValue(m => m.CurrentFile)
            .Subscribe(file =>
            {
                if (file == null) return;
                if (_exifDetailWindow?.DataContext is ExifDetailViewModel desktopVm)
                    desktopVm.UpdateToFile(file);
                if (IsModalVisible && IsModalExifDetail && _exifDetailVM != null)
                    _exifDetailVM.UpdateToFile(file);
            });
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

    // 桌面端单例 EXIF 详情窗口引用
    private ExifDetailWindow? _exifDetailWindow;

    /// <summary>
    /// 打开 EXIF 详情窗口（桌面端单例）。已打开时直接前置并更新内容，不重复创建。
    /// </summary>
    public void OpenExifDetailWindow(Window parentWindow)
    {
        var imageFile = CurrentFile;
        if (imageFile?.ExifData == null) return;

        if (_exifDetailWindow != null)
        {
            _exifDetailWindow.Activate();
            ((ExifDetailViewModel)_exifDetailWindow.DataContext!).UpdateToFile(imageFile);
            return;
        }

        _exifDetailWindow = new ExifDetailWindow
        {
            DataContext = new ExifDetailViewModel(imageFile)
        };
        _exifDetailWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _exifDetailWindow.Closed += (_, _) => _exifDetailWindow = null;
        _exifDetailWindow.Show(parentWindow);
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
    /// 当前模态内容类型：Settings 或 ExifDetail
    /// </summary>
    private string _modalContentType = "Settings";
    public string ModalContentType
    {
        get => _modalContentType;
        set
        {
            this.RaiseAndSetIfChanged(ref _modalContentType, value);
            this.RaisePropertyChanged(nameof(IsModalSettings));
            this.RaisePropertyChanged(nameof(IsModalExifDetail));
        }
    }
    
    /// <summary>模态内容是否为设置页</summary>
    public bool IsModalSettings => _modalContentType == "Settings";
    
    /// <summary>模态内容是否为 EXIF 详情</summary>
    public bool IsModalExifDetail => _modalContentType == "ExifDetail";
    
    /// <summary>
    /// 模态标题文本
    /// </summary>
    private string _modalTitle = "设置";
    public string ModalTitle
    {
        get => _modalTitle;
        set => this.RaiseAndSetIfChanged(ref _modalTitle, value);
    }

    /// <summary>
    /// EXIF 详情 ViewModel（模态显示时使用）
    /// </summary>
    private ExifDetailViewModel? _exifDetailVM;
    public ExifDetailViewModel? ExifDetailVM
    {
        get => _exifDetailVM;
        set => this.RaiseAndSetIfChanged(ref _exifDetailVM, value);
    }
    
    /// <summary>
    /// 打开设置弹窗
    /// </summary>
    public void OpenSettingModal()
    {
        ModalContentType = "Settings";
        ModalTitle = "设置";
        ShowModal();
    }

    /// <summary>
    /// 打开 EXIF 详情弹窗（移动端）
    /// </summary>
    public void OpenExifDetailModal()
    {
        var imageFile = CurrentFile;
        if (imageFile?.ExifData == null) return;
        
        ExifDetailVM = new ExifDetailViewModel(imageFile);
        ModalContentType = "ExifDetail";
        ModalTitle = "EXIF 详情";
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
            DetailVM?.RaisePropertyChanged(nameof(DetailVM.IsVerticalLayout));
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
    
    private bool _isDetailViewVisible = false;
    public bool IsDetailViewVisible
    {
        get => _isDetailViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isDetailViewVisible, value);
    }

    private bool _isThumbnailViewVisible = true;
    public bool IsThumbnailViewVisible
    {
        get => _isThumbnailViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isThumbnailViewVisible, value);
    }

    private bool _isControlViewVisible = true;
    public bool IsControlViewVisible
    {
        get => _isControlViewVisible;
        set => this.RaiseAndSetIfChanged(ref _isControlViewVisible, value);
    }

    public void ToggleDetailView()
    {
        IsDetailViewVisible = !IsDetailViewVisible;
    }

    public void ToggleThumbnailView()
    {
        IsThumbnailViewVisible = !IsThumbnailViewVisible;
    }

    public void ToggleControlView()
    {
        IsControlViewVisible = !IsControlViewVisible;
    }
    
    #endregion

    
    ////////////////
    /// 星级管理
    ////////////////
    
    #region Rating
    
    /// <summary>
    /// 获取当前文件及其已合并隐藏伴侣文件对应的运行期对象，便于在写入 XMP 后同步刷新内存星级。
    /// </summary>
    /// <param name="file">当前参与评分的代表文件</param>
    /// <returns>需要同步星级缓存的图片对象集合</returns>
    private List<ImageFile> GetRatingSyncTargets(ImageFile file)
    {
        var relatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            file.File.Path.LocalPath
        };

        foreach (var hiddenFile in file.HiddenFiles)
        {
            relatedPaths.Add(hiddenFile.Path.LocalPath);
        }

        return FolderVM.AllFiles
            .Where(candidate => relatedPaths.Contains(candidate.File.Path.LocalPath))
            .ToList();
    }

    /// <summary>
    /// 在 UI 线程同步更新已成功写入文件的运行期星级缓存，并刷新依赖星级的界面状态。
    /// </summary>
    /// <param name="syncedFiles">已成功写入星级的图片对象</param>
    /// <param name="rating">新的星级</param>
    private void UpdateRatingCaches(IReadOnlyCollection<ImageFile> syncedFiles, int rating)
    {
        foreach (var syncedFile in syncedFiles)
        {
            syncedFile.UpdateCachedRating(rating);
        }

        ControlVM.RaisePropertyChanged(nameof(ControlVM.CurrentExifData));

        // 仅在启用了星级筛选时才重建文件列表，否则仅更新内存缓存即可，避免重建 ObservableCollection 导致 UI 卡顿
        if (FolderVM.SelectedRatingFilter != "All")
        {
            FolderVM.RefreshFilters();
        }
    }

    /// <summary>
    /// 设置图片星级，并在写入代表文件及其同名伴侣文件后同步更新运行期缓存。
    /// 所有文件 I/O 均在后台线程执行，不阻塞 UI。
    /// </summary>
    /// <param name="file">要设置星级的图片</param>
    /// <param name="rating">新的星级（0~5）</param>
    public async Task SetRatingAsync(ImageFile? file, int rating)
    {
        if (file == null) return;

        var ratingSw = Stopwatch.StartNew();
        Console.WriteLine($"[Rating] === BEGIN SetRating({file.Name}, {rating}) ===");

        // 先在 UI 线程收集需要的引用，再整体下放到后台执行文件 I/O。
        var safeMode = Settings.SafeSetRating;
        var syncTargets = GetRatingSyncTargets(file);
        var hiddenFiles = file.HiddenFiles.ToList();

        try
        {
            // 将全部文件读写操作移到后台线程，避免阻塞 UI。
            var (success, syncedFiles) = await Task.Run(async () =>
            {
                var writeSuccess = await XmpWriter.WriteRatingAsync(file.File, rating, safeMode);
                if (!writeSuccess)
                {
                    return (false, (List<ImageFile>?)null);
                }

                var synced = new List<ImageFile> { file };

                if (hiddenFiles.Count > 0)
                {
                    var hiddenWriteTasks = hiddenFiles.Select(async hidden =>
                    {
                        try
                        {
                            var ok = await XmpWriter.WriteRatingAsync(hidden, rating, safeMode);
                            return (Path: hidden.Path.LocalPath, Success: ok);
                        }
                        catch (Exception exHidden)
                        {
                            Console.WriteLine("Hidden file rating sync failed: " + exHidden.Message);
                            return (Path: hidden.Path.LocalPath, Success: false);
                        }
                    }).ToList();

                    var hiddenResults = await Task.WhenAll(hiddenWriteTasks);
                    var hiddenSuccessPaths = hiddenResults
                        .Where(r => r.Success)
                        .Select(r => r.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    synced.AddRange(syncTargets.Where(target =>
                        !ReferenceEquals(target, file) && hiddenSuccessPaths.Contains(target.File.Path.LocalPath)));
                }

                return (true, (List<ImageFile>?)synced);
            });

            if (!success || syncedFiles == null)
            {
                Console.WriteLine($"[Rating] FAIL for {file.Name}. Total: {ratingSw.ElapsedMilliseconds}ms");
                return;
            }

            Console.WriteLine($"[Rating] Write done. Elapsed: {ratingSw.ElapsedMilliseconds}ms. Updating UI caches...");

            // 回到 UI 线程刷新内存缓存与界面。
            await Dispatcher.UIThread.InvokeAsync(() => UpdateRatingCaches(syncedFiles, rating));

            // 再异步重载当前文件完整 EXIF，确保界面中的其他字段也与磁盘状态保持一致。
            _ = Task.Run(async () =>
            {
                try
                {
                    // 在 UI 线程清除 EXIF 标记（涉及 RaisePropertyChanged），再由 LoadExifDataAsync 在后台重新加载
                    await Dispatcher.UIThread.InvokeAsync(() => file.ClearExifData());
                    await file.LoadExifDataAsync();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ControlVM.RaisePropertyChanged(nameof(ControlVM.CurrentExifData));
                        file.RaisePropertyChanged(nameof(file.Rating));
                        file.RaisePropertyChanged(nameof(file.HasRatingConflict));
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Refresh EXIF after rating failed: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error setting rating for " + file.Name + ": " + ex.Message);
        }
        finally
        {
            Console.WriteLine($"[Rating] === END SetRating({file.Name}): Total {ratingSw.ElapsedMilliseconds}ms ===");
        }
    }
    
    #endregion
}
