using System;
using System.Collections.ObjectModel;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    public enum SortMode { Name, Date, Size }
    public enum SortOrder { Ascending, Descending }
    
    public class SettingsViewModel : ReactiveObject
    {
        private SortMode _sortMode = SortMode.Name;
        private SortOrder _sortOrder = SortOrder.Ascending;
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
        
        public Array SortModes => Enum.GetValues(typeof(SortMode));
        public Array SortOrders => Enum.GetValues(typeof(SortOrder));
        
        public SortMode SortMode
        {
            get => _sortMode;
            set => this.RaiseAndSetIfChanged(ref _sortMode, value);
        }
        
        public SortOrder SortOrder
        {
            get => _sortOrder;
            set => this.RaiseAndSetIfChanged(ref _sortOrder, value);
        }
        
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