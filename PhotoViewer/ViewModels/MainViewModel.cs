using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewer.Core;
using PhotoViewer.Views;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

public partial class MainViewModel : ReactiveObject
{
    private readonly AppState _appState = new();
        
    public ThumbnailViewModel ThumbnailViewModel { get; }
    public ControlViewModel ControlViewModel { get; }
    public ImageViewModel ImageViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
        
    public MainViewModel()
    {
        ThumbnailViewModel = new ThumbnailViewModel(_appState);
        ControlViewModel = new ControlViewModel(_appState);
        ImageViewModel = new ImageViewModel(_appState);
        SettingsViewModel = new SettingsViewModel(_appState);
            
        // 当当前文件变化时加载图片
        _appState.WhenAnyValue(state => state.CurrentFile)
            .Subscribe(file =>
            {
                if (file != null)
                {
                    ImageViewModel.LoadImage(file.File);
                }
                else
                {
                    ImageViewModel.CurrentImage = null;
                }
            });
    }
        
    public async Task OpenFolder(IStorageFolder folder)
    {
        await _appState.LoadFolder(folder);
        await ThumbnailViewModel.PreloadThumbnailsAsync();
    }
}