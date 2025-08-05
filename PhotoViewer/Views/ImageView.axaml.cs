using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ImageView : UserControl
{
    private ImageViewModel? ViewModel => DataContext as ImageViewModel;
    
    public ImageView()
    {
        InitializeComponent();
            
        // 启用拖拽支持
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        
        // 点击事件（仅当没有图片时）
        // MainGrid.PointerPressed += OnPointerPressed;
        
        SetupEventHandlers();
    }

    #region Control
    
    // 操作状态
    private Vector _lastPanPosition;
    private Vector _lastCenter;
    private double _lastDistance;
    private bool _isDragging;
    
    
    // 活动指针跟踪
    private readonly Dictionary<IPointer, Point> _activePointers = new();
    
    private void SetupEventHandlers()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
            
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
        // 当没有图片时 打开图片
        if (ViewModel.SourceBitmap == null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ = OpenImageAsync();
        }
        else
        {
            var point = e.GetPosition(this);
            var pointer = e.Pointer;

            // if (pointer.Capture(this))
            {
                _activePointers[pointer] = point;
                
                if (_activePointers.Count == 1)
                {
                    _lastPanPosition = point;
                    _isDragging = true;
                }
            }
        }
        
        e.Handled = true;
    }
    
    /// <summary>
    /// 监听移动拖动
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ViewModel == null || !_activePointers.TryGetValue(e.Pointer, out var lastPoint)) 
            return;

        var currentPoint = e.GetPosition(this);
            
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
            
        if (_activePointers.ContainsKey(pointer))
        {
            _activePointers.Remove(pointer);
            pointer.Capture(null);
        }
            
        if (_activePointers.Count < 2)
        {
            _lastDistance = 0;
        }
            
        if (_activePointers.Count == 0)
        {
            _isDragging = false;
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
                case Key.Add:
                    ViewModel.Zoom(1.25 * ViewModel.Scale);
                    break;
                case Key.Subtract:
                    ViewModel.Zoom(0.8 * ViewModel.Scale);
                    break;
                case Key.D0:
                    ViewModel.FitToScreen();
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Left:
                    ViewModel.Main.ControlViewModel.OnPrevious();
                    break;
                case Key.Right:
                    ViewModel.Main.ControlViewModel.OnNext();
                    break;
            }
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

    
    // 边界约束
    // private void ConstrainPanOffset()
    // {
    //     if (ViewModel == null || _imageSize == default || _controlSize == default) return;
    //         
    //     var scaledWidth = _imageSize.Width * ViewModel.Scale;
    //     var scaledHeight = _imageSize.Height * ViewModel.Scale;
    //         
    //     var maxX = Math.Max(0, (scaledWidth - _controlSize.Width) / 2);
    //     var maxY = Math.Max(0, (scaledHeight - _controlSize.Height) / 2);
    //         
    //     ViewModel.Translate = new Point(
    //         Math.Clamp(ViewModel.Translate.X, -maxX, maxX),
    //         Math.Clamp(ViewModel.Translate.Y, -maxY, maxY)
    //     );
    // }

    #endregion


    /*
     *  打开图片
     */
    #region OpenFile
    
    /*
     *  选择打开图片
     */
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
                // 注册加载完成事件
                // ViewModel.ImageLoaded += OnImageLoaded;
                
                // 通过 Main 加载文件夹
                await ViewModel.Main.OpenAndroidFolder(folders[0]);
            }
        }
        else
        {
            // 选择图片窗口
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                FileTypeFilter = new[] { ImageFileTypes.All },
                AllowMultiple = false
            });

            if (files.Count > 0 && files[0] is IStorageFile file)
            {
                await LoadNewImageAsync(file);
            }
        }
    }
    /*
     *  选择新文件 或 拖入新文件 需要刷新文件夹
     */
    private async Task LoadNewImageAsync(IStorageFile file)
    {
        // 新打开文件时始终适配显示
        ViewModel.Fit = true;
        
        // 加载图片
        await LoadImageAsync(file);
        
        // 同步信息至 Main
        ViewModel.Main.LoadNewImageFolder(file);

    }
    
    /*
     *  加载文件
     */
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

    /*
     *  拖入文件
     */
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasValidFile = e.Data.GetFiles()?
            .Any(f => IsImageFile(f.Name)) ?? false;
            
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
        
    // 检查是否是图片文件
    public static bool IsImageFile(string fileName)
    {
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
        return extension switch
        {
            ".png" => true,
            ".jpg" => true,
            ".jpeg" => true,
            ".bmp" => true,
            ".gif" => true,
            ".webp" => true,
            _ => false
        };
    }
    #endregion
}

public static class ImageFileTypes
{
    public static FilePickerFileType All { get; } = new("图片文件")
    {
        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" },
        AppleUniformTypeIdentifiers = new[] { "public.image" },
        MimeTypes = new[] { "image/*" }
    };
}