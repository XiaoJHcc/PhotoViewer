using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class MainView : UserControl
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    
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
    }
    
    private void OnImageLoaded(object? sender, IStorageFile file)
    {
        if (ViewModel != null)
        {
            ViewModel.CurrentFile = file;
            LoadFolderImages(file);
        }
    }
        
    private async void LoadFolderImages(IStorageFile currentFile)
    {
        try
        {
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
            
            if (ViewModel != null)
            {
                ViewModel.SetFolderFiles(imageFiles);
                await ThumbnailView.SetFiles(imageFiles, currentFile);
                UpdateNavigationState();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载文件夹图片失败: {ex.Message}");
            if (ViewModel != null)
            {
                ViewModel.ClearFolderFiles();
                UpdateNavigationState();
            }
        }
    }
    
    private void OnThumbnailSelected(object? sender, IStorageFile file)
    {
        _ = LoadImageAsync(file);
    }
        
    private async Task LoadImageAsync(IStorageFile? file = null)
    {
        if (file == null) return;
            
        try
        {
            await ImageViewer.LoadImageAsync(file);
                
            if (ViewModel != null)
            {
                ViewModel.CurrentFile = file;
                ThumbnailView.SetCurrentFile(file);
                UpdateNavigationState();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载图片失败: {ex.Message}");
        }
    }
    
    private async void OnPreviousRequested(object? sender, EventArgs e)
    {
        if (ViewModel != null && ViewModel.HasPreviousFile())
        {
            var prevFile = ViewModel.GetPreviousFile();
            if (prevFile != null)
            {
                await LoadImageAsync(prevFile);
            }
        }
    }
        
    private async void OnNextRequested(object? sender, EventArgs e)
    {
        if (ViewModel != null && ViewModel.HasNextFile())
        {
            var nextFile = ViewModel.GetNextFile();
            if (nextFile != null)
            {
                await LoadImageAsync(nextFile);
            }
        }
    }
        
    private void OnClearRequested(object? sender, EventArgs e)
    {
        ImageViewer.ClearImage();
            
        if (ViewModel != null)
        {
            ViewModel.ClearFolderFiles();
            ThumbnailView.SetFiles(Enumerable.Empty<IStorageFile>());
            UpdateNavigationState();
        }
    }
        
    private void UpdateNavigationState()
    {
        if (ViewModel != null)
        {
            ControlBar.SetNavigationState(
                ViewModel.HasPreviousFile(),
                ViewModel.HasNextFile()
            );
        }
        else
        {
            ControlBar.SetNavigationState(false, false);
        }
    }
}