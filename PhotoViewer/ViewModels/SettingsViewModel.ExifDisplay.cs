using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ReactiveUI;
using System.Reactive;
using PhotoViewer.Controls;

namespace PhotoViewer.ViewModels;

public partial class SettingsViewModel
{
    //////////////
    /// EXIF 显示设置
    //////////////
    
    private ObservableCollection<ExifDisplayItem> _exifDisplayItems = new();
    public ObservableCollection<ExifDisplayItem> ExifDisplayItems
    {
        get => _exifDisplayItems;
        set => this.RaiseAndSetIfChanged(ref _exifDisplayItems, value);
    }

    private List<ExifDisplayItem> _enabledExifItems = new();
    public List<ExifDisplayItem> EnabledExifItems
    {
        get => _enabledExifItems;
        private set => this.RaiseAndSetIfChanged(ref _enabledExifItems, value);
    }

    private void InitializeExifDisplay()
    {
        MoveExifDisplayCommand = ReactiveCommand.Create<MoveCommandParameter>(OnMoveExifDisplay);
        
        ExifDisplayItems = new ObservableCollection<ExifDisplayItem>
        {
            new("光圈", "Aperture", true),
            new("快门", "ExposureTime", true),
            new("ISO", "Iso", true),
            new("等效焦距", "EquivFocalLength", true),
            new("实际焦距", "FocalLength", false),
            new("相机型号", "CameraModel", false),
            new("镜头型号", "LensModel", false),
            new("拍摄时间", "DateTimeOriginal", false),
            new("曝光补偿", "ExposureBias", false),
            new("白平衡", "WhiteBalance", false),
            new("闪光灯", "Flash", false),
        };

        // 监听集合变化
        ExifDisplayItems.CollectionChanged += OnExifDisplayItemsChanged;
        
        // 为现有项目订阅属性变化
        foreach (var item in ExifDisplayItems)
        {
            item.PropertyChanged += OnExifDisplayItemChanged;
        }

        UpdateEnabledExifItems();
    }

    private void OnExifDisplayItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ExifDisplayItem item in e.NewItems)
            {
                item.PropertyChanged += OnExifDisplayItemChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ExifDisplayItem item in e.OldItems)
            {
                item.PropertyChanged -= OnExifDisplayItemChanged;
            }
        }

        UpdateEnabledExifItems();
    }

    private void OnExifDisplayItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExifDisplayItem.IsEnabled))
        {
            UpdateEnabledExifItems();
        }
    }

    private void UpdateEnabledExifItems()
    {
        EnabledExifItems = ExifDisplayItems
            .Where(item => item.IsEnabled)
            .ToList();
    }

    // 添加移动命令
    public ReactiveCommand<MoveCommandParameter, Unit> MoveExifDisplayCommand { get; private set; }

    private void OnMoveExifDisplay(MoveCommandParameter parameter)
    {
        MoveExifDisplay(parameter.FromIndex, parameter.ToIndex);
    }

    public void MoveExifDisplay(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= ExifDisplayItems.Count || 
            toIndex < 0 || toIndex >= ExifDisplayItems.Count || 
            fromIndex == toIndex)
            return;

        var item = ExifDisplayItems[fromIndex];
        ExifDisplayItems.RemoveAt(fromIndex);
        ExifDisplayItems.Insert(toIndex, item);
    }

    public class ExifDisplayItem : ReactiveObject
    {
        private string _displayName;
        private string _propertyName;
        private bool _isEnabled;

        public string DisplayName
        {
            get => _displayName;
            set => this.RaiseAndSetIfChanged(ref _displayName, value);
        }

        public string PropertyName
        {
            get => _propertyName;
            set => this.RaiseAndSetIfChanged(ref _propertyName, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        public ExifDisplayItem(string displayName, string propertyName, bool isEnabled = true)
        {
            _displayName = displayName;
            _propertyName = propertyName;
            _isEnabled = isEnabled;
        }
    }
}

