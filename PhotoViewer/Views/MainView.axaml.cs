using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Views;

public partial class MainView : UserControl
{
    // 当前文件夹中的所有图片
    private List<IStorageFile>? _currentFolderImages;
    private int _currentIndex = -1;
    
    public MainView()
    {
        InitializeComponent();
        
        // 订阅图片加载事件
        ImageViewer.ImageLoaded += OnImageLoaded;
            
        // 订阅控制栏事件
        ControlBar.PreviousRequested += OnPreviousRequested;
        ControlBar.NextRequested += OnNextRequested;
        ControlBar.ClearRequested += OnClearRequested;
            
        // 订阅缩略图选择事件
        ThumbnailView.ThumbnailSelected += OnThumbnailSelected;
            
        // 监听窗口大小变化
        // SizeChanged += OnSizeChanged;
        
        // 初始更新导航状态
        UpdateNavigationState();
    }
    
    private void OnImageLoaded(object? sender, IStorageFile file)
    {
        // 当加载新图片时，获取其所在文件夹的所有图片
        LoadFolderImages(file);
    }
        
    private async void LoadFolderImages(IStorageFile currentFile)
    {
        try
        {
            // 获取文件所在文件夹
            var folder = await currentFile.GetParentAsync();
            if (folder == null) return;

            // 获取文件夹中的所有文件
            var allItems = folder.GetItemsAsync();
        
            // 过滤出图片文件并按名称排序
            var imageFiles = new List<IStorageFile>();
            await foreach (var item in allItems)
            {
                if (item is IStorageFile file && 
                    ImageView.IsImageFile(item.Name))
                {
                    imageFiles.Add(file);
                }
            }
        
            // 按文件名排序
            _currentFolderImages = imageFiles
                .OrderBy(f => f.Name)
                .ToList();
                    
            // 设置当前索引
            _currentIndex = _currentFolderImages.IndexOf(currentFile);
            
            // 更新缩略图视图
            await ThumbnailView.SetFiles(_currentFolderImages, currentFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载文件夹图片失败: {ex.Message}");
            _currentFolderImages = null;
            _currentIndex = -1;
        }
        finally
        {
            UpdateNavigationState();
        }
    }
    
    private async void OnThumbnailSelected(object? sender, IStorageFile file)
    {
        await LoadImageFromFolderAsync(file);
    }
        
    private async Task LoadImageFromFolderAsync(IStorageFile? file = null)
    {
        if (file == null && _currentFolderImages != null && _currentIndex >= 0)
        {
            file = _currentFolderImages[_currentIndex];
        }
            
        if (file == null) return;
            
        try
        {
            await ImageViewer.LoadImageAsync(file);
                
            // 更新当前索引
            if (_currentFolderImages != null)
            {
                _currentIndex = _currentFolderImages
                    .FindIndex(f => f.Path.AbsolutePath == file.Path.AbsolutePath);
            }
                
            // 更新缩略图视图
            ThumbnailView.SetCurrentFile(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载图片失败: {ex.Message}");
        }
            
        UpdateNavigationState();
    }
    
    private async void OnPreviousRequested(object? sender, EventArgs e)
    {
        if (_currentFolderImages == null || _currentIndex <= 0) return;
    
        _currentIndex--;
        await LoadImageFromFolderAsync();
    }

    private async void OnNextRequested(object? sender, EventArgs e)
    {
        if (_currentFolderImages == null || _currentIndex >= _currentFolderImages.Count - 1) return;
    
        _currentIndex++;
        await LoadImageFromFolderAsync();
    }
        
    private void OnClearRequested(object? sender, EventArgs e)
    {
        ImageViewer.ClearImage();
        _currentFolderImages = null;
        _currentIndex = -1;
        ThumbnailView.SetFiles(Enumerable.Empty<IStorageFile>());
        UpdateNavigationState();
    }
        
    private void UpdateNavigationState()
    {
        ControlBar.SetNavigationState(
            hasPrevious: _currentIndex > 0,
            hasNext: _currentIndex >= 0 && _currentIndex < (_currentFolderImages?.Count - 1 ?? 0)
        );
    }
}