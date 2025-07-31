using System;
using System.Collections.ObjectModel;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    
    public class SettingsViewModel : ReactiveObject
    {
        private int _maxCacheSizeMB = 500;
        private int _preloadCount = 3;
        
        public ObservableCollection<string> AvailableFormats { get; } = new ObservableCollection<string>
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".svg"
        };
        
        public ObservableCollection<string> SelectedFormats { get; } = new ObservableCollection<string>
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
        };
        
        
        public int MaxCacheSizeMB
        {
            get => _maxCacheSizeMB;
            set => this.RaiseAndSetIfChanged(ref _maxCacheSizeMB, value);
        }
        
        public int PreloadCount
        {
            get => _preloadCount;
            set => this.RaiseAndSetIfChanged(ref _preloadCount, value);
        }
    }
}