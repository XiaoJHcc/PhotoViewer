using System;
using System.Collections.ObjectModel;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels
{
    public class SettingsViewModel : ReactiveObject
    {
        private readonly AppState _state;
        
        public ObservableCollection<string> AvailableFormats { get; } = new ObservableCollection<string>
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".svg"
        };
        
        public ObservableCollection<string> SelectedFormats { get; } = new ObservableCollection<string>();
        
        public Array SortModes => Enum.GetValues(typeof(SortMode));
        public Array SortOrders => Enum.GetValues(typeof(SortOrder));
        
        public SortMode SelectedSortMode
        {
            get => _state.SortMode;
            set => _state.SortMode = value;
        }
        
        public SortOrder SelectedSortOrder
        {
            get => _state.SortOrder;
            set => _state.SortOrder = value;
        }
        
        public int CacheSize
        {
            get => _state.CacheSettings.MaxCacheSizeMB;
            set => _state.CacheSettings.MaxCacheSizeMB = value;
        }
        
        public SettingsViewModel(AppState state)
        {
            _state = state;
            
            // 初始化选择的格式
            foreach (var format in _state.FilterSettings.IncludedFormats)
            {
                SelectedFormats.Add(format);
            }
            
            // 当选择的格式变化时更新状态
            SelectedFormats.CollectionChanged += (s, e) =>
            {
                _state.FilterSettings.IncludedFormats = new ObservableCollection<string>(SelectedFormats);
            };
        }
    }
}