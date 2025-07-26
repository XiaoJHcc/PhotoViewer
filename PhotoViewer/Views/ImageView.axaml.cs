using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PhotoViewer.Core;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ImageView : UserControl
{
    private ImageViewModel? ViewModel => DataContext as ImageViewModel;
    
    // 缩放状态
    private enum ZoomState { Normal, Zoomed }
    private ZoomState _currentZoomState = ZoomState.Normal;
        
    // 原始图片尺寸
    private Size _originalImageSize;
    public event EventHandler<IStorageFile>? ImageLoaded;
    
    public ImageView()
    {
        InitializeComponent();
            
        // 启用拖拽支持
        DragDrop.SetAllowDrop(this, true);
            
        // 点击事件（仅当没有图片时）
        MainGrid.PointerPressed += OnPointerPressed;

        // 拖拽支持
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
            
        // 桌面端双击事件
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            MainGrid.DoubleTapped += OnDoubleTapped;
        }
        // 移动端双指缩放
        else
        {
            SetupPinchZoom();
        }
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 仅当没有图片时才响应点击
        if (PreviewImage.Source == null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ = OpenImageAsync();
        }
    }

    private void OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source == null) return;
            
        switch (_currentZoomState)
        {
            case ZoomState.Normal:
                // 放大到100%
                ZoomToOriginalSize();
                _currentZoomState = ZoomState.Zoomed;
                break;
                    
            case ZoomState.Zoomed:
                // 恢复自适应大小
                ResetZoom();
                _currentZoomState = ZoomState.Normal;
                break;
        }
    }
    
    private void ZoomToOriginalSize()
    {
        if (PreviewImage.Source is Bitmap bitmap)
        {
            // 保存当前尺寸
            _originalImageSize = new Size(
                PreviewImage.Bounds.Width, 
                PreviewImage.Bounds.Height
            );
                
            // 设置到原始尺寸
            PreviewImage.Width = bitmap.PixelSize.Width;
            PreviewImage.Height = bitmap.PixelSize.Height;
            PreviewImage.Stretch = Stretch.None;
                
            // 居中显示
            Canvas.SetLeft(PreviewImage, (MainGrid.Bounds.Width - bitmap.PixelSize.Width) / 2);
            Canvas.SetTop(PreviewImage, (MainGrid.Bounds.Height - bitmap.PixelSize.Height) / 2);
        }
    }

    private void ResetZoom()
    {
        // 恢复自适应大小
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        PreviewImage.Stretch = Stretch.Uniform;
            
        // 恢复原始位置
        Canvas.SetLeft(PreviewImage, 0);
        Canvas.SetTop(PreviewImage, 0);
        // PreviewImage.HorizontalAlignment = HorizontalAlignment.Center;
        // PreviewImage.VerticalAlignment = VerticalAlignment.Center;
    }
    
    private void SetupPinchZoom()
    {
        // // 使用手势识别器实现双指缩放
        // var pinchGesture = new PinchGestureRecognizer();
        // double initialScale = 1;
        // double currentScale = 1;
        //
        // pinchGesture.PinchStarted += (s, e) => {
        //     initialScale = currentScale;
        // };
        //
        // pinchGesture.PinchUpdated += (s, e) => {
        //     currentScale = initialScale * e.Scale;
        //     currentScale = Math.Max(0.5, Math.Min(currentScale, 5)); // 限制缩放范围
        //
        //     PreviewImage.RenderTransform = new ScaleTransform(currentScale, currentScale);
        // };
        //
        // this.GestureRecognizers.Add(pinchGesture);
    }
    
    private async Task OpenImageAsync()
    {
        // 获取顶级窗口的StorageProvider
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;
            
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            FileTypeFilter = new[] { ImageFileTypes.All },
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0] is IStorageFile file)
        {
            await LoadImageAsync(file);
        }
    }
    
    public async Task LoadImageAsync(IStorageFile file)
    {
        try
        {
            // 使用缓存服务加载图片
            var bitmap = await BitmapCacheService.GetBitmapAsync(file);
            if (bitmap == null) return;
        
            PreviewImage.Source = bitmap;
            HintText.IsVisible = false;
            // ViewModel.HintText = file.Path.ToString(); //DEBUG
        
            // 重置缩放状态
            _currentZoomState = ZoomState.Normal;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            PreviewImage.Stretch = Stretch.Uniform;
        
            ImageLoaded?.Invoke(this, file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载图片失败: {ex.Message}");
        }
    }
    
    public void ClearImage()
    {
        PreviewImage.Source = null;
        HintText.IsVisible = true;
        _currentZoomState = ZoomState.Normal;
    }

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
            await LoadImageAsync(file);
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