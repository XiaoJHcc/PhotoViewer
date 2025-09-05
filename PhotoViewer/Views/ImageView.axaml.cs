using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using System.IO;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Threading;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ImageView : UserControl
{
    private ImageViewModel? ViewModel => DataContext as ImageViewModel;
    
    // 右键菜单
    private MenuFlyout _menuFlyout;
    private void InitMenu()
    {
        _menuFlyout = new MenuFlyout();
        var menuItem = new MenuItem { Header = "设置" };
        menuItem.Click += (s, e) => OpenImageSetting();
        _menuFlyout.Items.Add(menuItem);
        // 移动端手指上方弹出
        _menuFlyout.Placement = PlacementMode.AnchorAndGravity;
        _menuFlyout.PlacementAnchor = PopupAnchor.TopLeft;
        _menuFlyout.PlacementGravity = PopupGravity.Top;
    }
    private void ShowMenuAtPointer()
    {
        _menuFlyout.ShowAt(this, true);
    }
    private void ShowMenuAtTouch(Point point)
    {
        _menuFlyout.HorizontalOffset = point.X;
        _menuFlyout.VerticalOffset = point.Y - 40;
        _menuFlyout.ShowAt(this, false);
    }
    
    /// <summary>
    /// 构造函数
    /// </summary>
    public ImageView()
    {
        InitializeComponent();
        
        InitMenu();
        
        SetupEventHandlers();
            
        // 启用拖拽支持
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }
    
    /// <summary>
    /// 打开图片预览设置窗口
    /// </summary>
    private void OpenImageSetting()
    {
        if (VisualRoot is Window parentWindow)
        {
            ViewModel.Main.OpenSettingWindow(parentWindow);
        }
        else
        {
            ViewModel.Main.OpenSettingModal();
        }
    }

    ////////////////////
    /// 预览控制
    ////////////////////
    
    #region Control
    
    // 菜单长按状态
    private Point _pressPosition;
    private DispatcherTimer _longPressTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(800) // 长按时间（毫秒）
    };
    private const double MoveTolerance = 10; // 移动容差（像素）
    private bool _isLongPressTriggered;
    
    // 长按成功
    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        _isLongPressTriggered = true;
        ShowMenuAtTouch(_pressPosition);
    }
    
    // 长按取消
    private void CancelLongPress()
    {
        _longPressTimer.Stop();
    }
    
    // 操作状态
    private Vector _lastPanPosition;
    private Vector _lastCenter;
    private double _lastDistance;
    // private bool _isDragging;
    private bool _wasTwoFingers = false; // 标记上一次操作是否为双指
    
    
    // 活动指针跟踪
    private readonly Dictionary<IPointer, Point> _activePointers = new();
    
    private void SetupEventHandlers()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        // KeyDown += OnKeyDown;
        
        _longPressTimer.Tick += OnLongPressTimerTick;
            
        // 监听尺寸变化
        this.GetObservable(BoundsProperty)
            .Subscribe(_ => 
                ViewModel?.UpdateView(new Vector(Bounds.Size.Width, Bounds.Size.Height)));
    }
    
    /// <summary>
    /// 监听滚轮
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel == null) return;
        
        var pointerPosition = e.GetPosition(this);
        var zoomFactor = e.Delta.Y > 0 ? 1.25 : 0.8;
        
        ViewModel.Zoom(ViewModel.Scale * zoomFactor, pointerPosition);
        
        e.Handled = true;
    }
    
    /// <summary>
    /// 监听点击
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 右键 打开菜单
        if (e.Properties.IsRightButtonPressed)
        {
            ShowMenuAtPointer();
            e.Handled = true;
        }
        
        // 左键
        if (e.Properties.IsLeftButtonPressed)
        {
            // 触摸等待判断：点击、拖动、长按
            if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
            {
                _isLongPressTriggered = false;
                _pressPosition = e.GetPosition(this);
                _longPressTimer.Start();
                e.Pointer.Capture(this);
            }

            // 已打开图片时 准备拖动图片
            if (ViewModel?.SourceBitmap != null)
            {
                var point = e.GetPosition(this);
                var pointer = e.Pointer;

                pointer.Capture(this);
                
                _activePointers[pointer] = point;
                _wasTwoFingers = false;

                if (_activePointers.Count == 1)
                {
                    _lastPanPosition = point;
                    // _isDragging = true;
                }
            }
            else
            {
                // 无图片时 鼠标左键打开图片
                if (e.Pointer.Type == PointerType.Mouse)
                {
                    _ = OpenImageAsync();
                }
            }
            e.Handled = true;
        }
    }
    
    /// <summary>
    /// 监听拖动
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed) return; // 非左键全无效
        
        if (ViewModel == null || !_activePointers.TryGetValue(e.Pointer, out var lastPoint)) return;

        var currentPoint = e.GetPosition(this);

        // 触屏设备识别长按
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen || _longPressTimer.IsEnabled)
        {
            // 超出容差范围，取消长按
            if (Vector.Distance(_pressPosition, currentPoint) > MoveTolerance) 
                CancelLongPress();
        }
            
        // 当从双指变为单指时，不执行任何操作
        if (_wasTwoFingers && _activePointers.Count == 1)
        {
            _activePointers[e.Pointer] = currentPoint;
            return;
        }
        
        switch (_activePointers.Count)
        {
            case 1:
                // 单指拖动
                ViewModel.Move(currentPoint - _lastPanPosition);
                
                _lastPanPosition = currentPoint;
                _activePointers[e.Pointer] = currentPoint;
                break;
            case >= 2:
            {
                // 双指缩放
                _wasTwoFingers = true;
                var points = _activePointers.Values.ToArray();
                var center = GetCenter(points);
                var distance = GetDistance(points);

                if (_lastDistance > 0)
                {
                    var scaleChange = distance / _lastDistance;
                    ViewModel.Zoom(ViewModel.Scale * scaleChange, center);

                    var centerOffset = center - _lastCenter;
                    ViewModel.Move(centerOffset);

                    ViewModel.Fit = false;
                }

                _lastCenter = center;
                _lastDistance = distance;
                
                // 更新当前点
                _activePointers[e.Pointer] = currentPoint;
                break;
            }
        }

        e.Handled = true;
    }
    
    private static Vector GetCenter(IEnumerable<Point> points)
    {
        return new Vector(
            points.Average(p => p.X),
            points.Average(p => p.Y)
        );
    }

    private static double GetDistance(IEnumerable<Point> points)
    {
        var arr = points.Take(2).ToArray();
        if (arr.Length < 2) return 0;
        return Point.Distance(arr[0], arr[1]);
    }
    
    /// <summary>
    /// 监听抬手
    /// </summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pointer = e.Pointer;

        // 触摸设备响应长按
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            // 取消长按计时
            CancelLongPress();
            
            // 未触发长按 且无图片时 打开图片
            if (!_isLongPressTriggered && ViewModel?.SourceBitmap == null)
                _ = OpenImageAsync();
        }
            
        if (_activePointers.ContainsKey(pointer))
        {
            _activePointers.Remove(pointer);
            pointer.Capture(null);
        }
            
        // 当从双指变为单指时
        if (_wasTwoFingers && _activePointers.Count == 1)
        {
            // 清除拖动状态
            // _isDragging = false;
        
            // 更新最后位置为剩余手指的位置
            _lastPanPosition = _activePointers.Values.First();
        }
    
        if (_activePointers.Count < 2)
        {
            _lastDistance = 0;
        
            // 双指状态结束
            if (_activePointers.Count == 0)
            {
                _wasTwoFingers = false;
            }
        }
        
        if (_activePointers.Count == 0)
        {
            // _isDragging = false;
        }

        e.Handled = true;
    }
    
    /// <summary>
    /// 监听双击
    /// </summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ViewModel?.ToggleFit(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// 监听键盘
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel == null) return;
            
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                    ViewModel.ZoomPreset(+1);
                    break;
                case Key.OemMinus:
                    ViewModel.ZoomPreset(-1);
                    break;
                case Key.D0:
                    ViewModel.ToggleFit();
                    break;
            }
        }
        else
        {
            // switch (e.Key)
            // {
            //     case Key.Left:
            //         ViewModel.Main.ControlViewModel.OnPrevious();
            //         break;
            //     case Key.Right:
            //         ViewModel.Main.ControlViewModel.OnNext();
            //         break;
            // }
        }
    }
    
    private double SnapToPreset(double zoom)
    {
        // 预设缩放比例
        double[] presets = { 0.125, 0.25, 0.5, 1.0, 2.0, 4.0 };
            
        var closest = presets
            .Select(p => new { Value = p, Diff = Math.Abs(p - zoom) })
            .OrderBy(x => x.Diff)
            .FirstOrDefault();

        return closest?.Value ?? zoom;
    }

    #endregion


    ////////////////////
    /// 打开图片
    ////////////////////
    
    #region OpenFile
    
    /// <summary>
    /// 选择打开图片
    /// </summary>
    private async Task OpenImageAsync()
    {
        // 获取顶级窗口的StorageProvider
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            // Android 平台：选择文件夹
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择图片文件夹",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                await LoadNewFolderAsync(folders);
            }
        }
        else
        {
            // 选择图片窗口
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                FileTypeFilter = [FilePickerFileTypes],
                AllowMultiple = false
            });

            if (files.Count > 0 && files[0] is IStorageFile file)
            {
                await LoadNewImageAsync(file);
            }
        }
    }

    private readonly FilePickerFileType _filePickerFileTypes = new("选择图片")
    {
        AppleUniformTypeIdentifiers = new[] { "public.image" },
        MimeTypes = new[] { "image/*" }
    };

    private FilePickerFileType FilePickerFileTypes
    {
        get
        {
            _filePickerFileTypes.Patterns = ViewModel?.Main.Settings.SelectedFormats
                .Select(format => $"*{format}")
                .ToArray();
            return _filePickerFileTypes;
        }
    }

    /// <summary>
    /// 刷新文件夹（选择新文件夹时）
    /// </summary>
    private async Task LoadNewFolderAsync(IReadOnlyList<IStorageFolder> folders)
    {
        // 新打开文件夹时始终适配显示
        ViewModel.Fit = true;
        
        // 通过 Main 加载文件夹
        await ViewModel.Main.OpenAndroidFolder(folders[0]);
    }
    
    /// <summary>
    /// 刷新文件夹（选择新文件 或 拖入新文件 时）
    /// </summary>
    private async Task LoadNewImageAsync(IStorageFile file)
    {
        // 新打开文件时始终适配显示
        ViewModel.Fit = true;
        
        // 加载图片
        await LoadImageAsync(file);
        
        // 同步信息至 Main
        ViewModel.Main.LoadNewImageFolder(file);
    }
    
    /// <summary>
    /// 加载文件
    /// </summary>
    public async Task LoadImageAsync(IStorageFile file)
    {
        try
        {
            // 使用 ImageViewModel 加载图片
            ViewModel.LoadImageAsync(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载图片失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 拖入文件
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasValidFile = e.Data.GetFiles()?
            .Any(f => ViewModel.Main.IsImageFile(f.Name)) ?? false;
            
        e.DragEffects = hasValidFile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.ToList();
        if (files?.Count > 0 && files[0] is IStorageFile file)
        {
            await LoadNewImageAsync(file);
        }
        e.Handled = true;
    }

    #endregion
}